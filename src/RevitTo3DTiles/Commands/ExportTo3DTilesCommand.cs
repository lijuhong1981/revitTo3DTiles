using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTo3DTiles.Extraction;
using RevitTo3DTiles.Models;
using RevitTo3DTiles.Output;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Commands
{
    /// <summary>
    /// Revit → 3D Tiles 一键导出命令。
    /// 流程：设置弹窗（范围/精度/地理位置/转换选项）
    /// → 提取几何/材质/贴图/元数据（与导出 glTF 共用同一条提取管线，含实例化去重）
    /// → 写出临时 glb（内嵌贴图，单文件交接最稳）+ .metadata
    /// → 调用 modelTo3DTiles 转换（--md 属性表 / --lla 地理位置 / -r 正北旋转 / --ts 瓦片容量 / --cc 锚点修正）
    /// → 输出 tileset.json + b3dm。临时目录成功后删除，失败/取消时保留便于排查。
    /// 进度三段加权：提取 0~80% / 写出 80~90% / 转换 90~100%（跑马灯 + 转换器日志回显）。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportTo3DTilesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                message = "请先打开一个 Revit 模型文档。";
                return Result.Failed;
            }
            Document doc = uiDocument.Document;

            // 1. 设置弹窗（共享组件，Tiles3D 模式）
            ExportSettings settings = ExportSettingsForm.Show(uiDocument, doc, ExportMode.Tiles3D);
            if (settings == null)
                return Result.Cancelled;

            string outputDirectory = settings.OutputDirectory;
            Directory.CreateDirectory(outputDirectory);
            string logPath = Path.Combine(outputDirectory, "3dtiles-export.log");

            // 临时目录先建好并作为提取上下文的输出目录：
            // 内嵌模式下偶发的"非PNG/JPEG回退外置"贴图会写到 context.OutputDirectory/textures，
            // 必须与 glb 同目录（转换器按 glb 相对路径解析），不能落进用户输出目录
            string tempDirectory = Path.Combine(Path.GetTempPath(),
                "revitTo3DTiles_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDirectory);

            var detailSettings = new GeometryDetailSettings("3dtiles", settings.DetailLevel, settings.TriangulateLod)
            {
                LogProgressEvery = 2000,
                ExportMetadata = settings.TilesExportMetadata   // .metadata 驱动转换器的构件属性表
            };

            string summaryLine = null;
            string failure = null;
            bool cancelled = false;
            bool tempKeep = false;      // 失败/取消时保留临时目录供排查
            double extractSeconds = 0;
            double writeSeconds = 0;
            int phase = 0;              // 0=提取(0~80%) 1=写出(80~90%) 2=转换(跑马灯)

            var progress = new ExportProgressForm("导出 3D Tiles");
            try
            {
                progress.Show();
                Application.DoEvents();

                using (var log = new StreamWriter(logPath, true, Encoding.UTF8))
                {
                    var context = new GltfExportContext(tempDirectory, log)
                    {
                        // 3D Tiles 管线走临时 glb：贴图内嵌，转换器自行外置去重；
                        // 2 的幂归一化恒开（Cesium 加载优化，2048 上限与转换器图集对齐）
                        SeparateTextures = false,
                        NormalizeTextures = true,
                        OnProgress = (percent, text) =>
                        {
                            if (phase == 0) progress.Report(percent * 80 / 100, text);
                            else if (phase == 1) progress.Report(80 + percent * 10 / 100, text);
                        },
                        ShouldCancel = () => progress.Cancelled
                    };
                    context.Log(string.Empty);
                    context.Log(string.Format("===== 导出 3D Tiles: {0} ({1}) =====", doc.Title, doc.PathName));
                    context.Log(string.Format("范围: {0}", settings.ScopeName));
                    context.Log(string.Format("精度: Detail={0}, Tri={1}", settings.DetailLevel,
                        ExportSettingsForm.TriText(settings.TriangulateLod)));
                    context.Log(string.Format("输出目录: {0}", outputDirectory));
                    context.Log(string.Format("BIM属性: {0}", settings.TilesExportMetadata ? "写入瓦片属性表" : "不写入"));
                    if (settings.GeoLocation != null)
                        context.Log(string.Format("地理位置: {0}", settings.GeoLocation.ToArgument()));
                    else
                        context.Log("地理位置: 未启用（转换器使用默认坐标定位）");
                    context.Log(string.Format("正北旋转: {0}", settings.TilesNorthRotation.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}°（项目北 → 正北）", settings.TilesNorthRotation.Value)
                        : "否（项目无偏转或未勾选）"));
                    context.Log(string.Format("单瓦片容量: {0:0.##}MB | 贴地: {1} | 拆分: material（按材质装填）",
                        settings.TilesTileSizeMb, settings.TilesClampToGround ? "是" : "否"));
                    context.Log(string.Format("Draco 压缩: {0} | 纹理图集: {1}",
                        settings.TilesDracoCompression ? "开" : "关（排查瓦片异常）",
                        settings.TilesTextureAtlas ? "开" : "关（排查贴图错位）"));
                    context.Log("贴图: 内嵌 glb, 2的幂归一化: 开");

                    // 2. 提取（与 glTF 导出共用管线：实例化去重/真实世界缩放UV/外观贴图）
                    ExtractResult result = null;
                    var extractWatch = Stopwatch.StartNew();
                    try
                    {
                        result = GeometryExtractor.Extract(doc, context, detailSettings, settings.ScopeIds);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        context.Log("用户取消了导出。");
                    }
                    catch (Exception ex)
                    {
                        context.Log(string.Format("提取失败: {0}", ex.Message));
                        failure = "提取失败：" + ex.Message;
                    }
                    extractWatch.Stop();
                    extractSeconds = extractWatch.Elapsed.TotalSeconds;

                    if (!cancelled && failure == null && result != null && !result.HasGeometry)
                    {
                        context.Log("无几何输出（该模型/范围无可用几何）。");
                        failure = "没有提取到任何几何，请调整 DetailLevel 或范围后重试。";
                    }

                    if (!cancelled && failure == null && result != null && result.HasGeometry)
                    {
                        // 3. 写出临时产物：glb（内嵌贴图）+ .metadata
                        phase = 1;
                        string glbPath = Path.Combine(tempDirectory, "model.glb");
                        string metadataPath = Path.Combine(tempDirectory, "model.metadata");

                        var writeWatch = Stopwatch.StartNew();
                        try
                        {
                            GltfWriter.Export(glbPath, result, context, true);
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            context.Log("用户取消了导出。");
                        }
                        catch (Exception ex)
                        {
                            context.Log(string.Format("写出失败: {0}", ex.Message));
                            failure = "写出失败：" + ex.Message;
                        }
                        writeWatch.Stop();
                        writeSeconds = writeWatch.Elapsed.TotalSeconds;

                        if (!cancelled && failure == null)
                        {
                            string effectiveMetadataPath = null;
                            if (settings.TilesExportMetadata)
                            {
                                try
                                {
                                    MetadataWriter.Export(metadataPath, result, context, doc,
                                        settings.ScopeName, settings.DetailLevel, settings.TriangulateLod, true);
                                    effectiveMetadataPath = metadataPath;
                                }
                                catch (Exception ex)
                                {
                                    // 元数据是旁路产物：写出失败不阻断转换，仅少一张属性表
                                    context.Log(string.Format("元数据写出失败(降级为无属性表): {0}", ex.Message));
                                }
                            }

                            // 4. 调用转换器（跑马灯进度，取消会终止转换进程）
                            phase = 2;
                            progress.SetIndeterminate("正在转换 3D Tiles（Draco/纹理图集/属性表），可取消…");
                            ConverterLauncher.Result convert = ConverterLauncher.Run(
                                glbPath, effectiveMetadataPath, outputDirectory,
                                settings.GeoLocation, settings.TilesClampToGround,
                                settings.TilesTileSizeMb, settings.TilesNorthRotation,
                                settings.TilesDracoCompression, settings.TilesTextureAtlas, context);

                            if (convert.Cancelled)
                            {
                                cancelled = true;
                                tempKeep = true;    // 半成品瓦片目录 + 中间产物都留给用户检查
                            }
                            else if (!convert.Success)
                            {
                                failure = (convert.ErrorMessage ?? "未知错误") + "\n详见: " + logPath
                                    + "\n中间产物保留在: " + tempDirectory;
                                tempKeep = true;
                            }
                            else
                            {
                                context.Log("导出完成。");
                                long outputBytes = DirectoryBytes(outputDirectory);
                                summaryLine = string.Format(
                                    "构件 {0} | 三角形 {1:N0} | 顶点 {2:N0} | 材质 {3} | 贴图 {4} | 输出 {5:0.0}MB | 提取 {6:0.0}s | 写出 {7:0.0}s",
                                    context.MeshElementCount, context.TriangleCount, context.VertexCount,
                                    context.MaterialCount, context.BitmapTextureCount,
                                    outputBytes / 1048576.0, extractSeconds, writeSeconds);
                                if (context.SharedMeshCount > 0)
                                    summaryLine += string.Format(
                                        "\n实例化: 共享网格 {0} 个 / 实例 {1} 个, 展开三角形 {2:N0} → 去重 {3:N0} (省 {4:0.0}%)",
                                        context.SharedMeshCount, context.InstanceCount,
                                        context.ExpandedTriangleCount, context.TriangleCount,
                                        (1 - (double)context.TriangleCount / Math.Max(1, context.ExpandedTriangleCount)) * 100);
                                if (effectiveMetadataPath == null && settings.TilesExportMetadata)
                                    summaryLine += "\n元数据: 写出失败，瓦片无 BIM 属性表（详见日志）";
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = "导出异常：" + ex.Message + "\n详见: " + logPath;
                tempKeep = true;
            }
            finally
            {
                if (!progress.IsDisposed)
                {
                    progress.Close();
                    progress.Dispose();
                }
                if (tempDirectory != null && !tempKeep)
                    CleanupTempDirectory(tempDirectory);
            }

            if (cancelled)
            {
                TaskDialog.Show("导出已取消",
                    string.Format("已在提取 {0:0.0}s / 写出 {1:0.0}s 后中止。\n半成品输出与日志: {2}",
                        extractSeconds, writeSeconds, outputDirectory)
                    + (tempKeep ? string.Format("\n中间产物保留在: {0}", tempDirectory) : ""));
                return Result.Cancelled;
            }
            if (failure != null)
            {
                TaskDialog.Show("导出失败", failure);
                return Result.Failed;
            }

            var dialog = new TaskDialog("导出 3D Tiles 完成");
            dialog.MainInstruction = "输出结构：tileset.json + Tile-*.b3dm + textures/（详见 3dtiles-export.log）";
            dialog.MainContent = summaryLine ?? "（无内容）";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "打开输出目录");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Process.Start("explorer.exe", outputDirectory);
            return Result.Succeeded;
        }

        /// <summary>目录总字节数（统计输出体积用，子目录递归）</summary>
        private static long DirectoryBytes(string directory)
        {
            try
            {
                long bytes = 0;
                foreach (string file in Directory.GetFiles(directory))
                    bytes += new FileInfo(file).Length;
                foreach (string sub in Directory.GetDirectories(directory))
                    bytes += DirectoryBytes(sub);
                return bytes;
            }
            catch
            {
                return 0;
            }
        }

        private static void CleanupTempDirectory(string tempDirectory)
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
            catch
            {
                // 清理失败不影响导出结果
            }
        }
    }
}
