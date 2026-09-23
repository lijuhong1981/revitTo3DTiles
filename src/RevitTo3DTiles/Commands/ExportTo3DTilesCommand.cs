using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
    /// 流程：提取几何/材质/贴图/元数据（与导出 glTF 共用同一条提取管线，含实例化去重）
    /// → 写出临时 glb（内嵌贴图，单文件交接最稳）+ .metadata
    /// → 调用 modelTo3DTiles 转换（--md 属性表 / -lla 项目地理位置 / --cc 锚点修正）
    /// → 输出 tileset.json + b3dm。临时目录成功后删除，失败时保留便于排查。
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

            // 1. 选择输出目录（默认桌面下 项目名_3dtiles）
            string outputDirectory = SelectOutputDirectory(doc);
            if (string.IsNullOrEmpty(outputDirectory))
                return Result.Cancelled;

            Directory.CreateDirectory(outputDirectory);
            string logPath = Path.Combine(outputDirectory, "3dtiles-export.log");

            string summaryLine = null;
            string failure = null;
            bool cancelled = false;
            string tempDirectory = null;
            bool tempKeep = false;      // 失败/取消时保留临时目录供排查

            try
            {
                using (var log = new StreamWriter(logPath, true, Encoding.UTF8))
                {
                    var context = new GltfExportContext(outputDirectory, log)
                    {
                        // 3D Tiles 管线走临时 glb：贴图内嵌，转换器自行外置去重
                        SeparateTextures = false
                    };
                    context.Log(string.Empty);
                    context.Log(string.Format("===== 导出 3D Tiles: {0} ({1}) =====", doc.Title, doc.PathName));
                    context.Log(string.Format("输出目录: {0}", outputDirectory));

                    var settings = new GeometryDetailSettings("3dtiles", ViewDetailLevel.Fine, null)
                    {
                        LogProgressEvery = 2000,
                        ExportMetadata = true     // .metadata 驱动转换器的构件属性表
                    };

                    // 2. 提取（与 glTF 导出共用管线：实例化去重/真实世界缩放UV/外观贴图）
                    ExtractResult result = null;
                    var extractWatch = Stopwatch.StartNew();
                    try
                    {
                        result = GeometryExtractor.Extract(doc, context, settings, null);
                    }
                    catch (Exception ex)
                    {
                        context.Log(string.Format("提取失败: {0}", ex.Message));
                        failure = "提取失败：" + ex.Message;
                    }
                    extractWatch.Stop();

                    if (failure == null && result != null && !result.HasGeometry)
                    {
                        context.Log("无几何输出（该模型无可用几何）。");
                        failure = "没有提取到任何几何，请调整 DetailLevel 或范围后重试。";
                    }

                    if (failure == null && result != null && result.HasGeometry)
                    {
                        // 3. 写出临时产物：glb（内嵌贴图）+ .metadata
                        tempDirectory = Path.Combine(Path.GetTempPath(),
                            "revitTo3DTiles_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        Directory.CreateDirectory(tempDirectory);
                        string glbPath = Path.Combine(tempDirectory, "model.glb");
                        string metadataPath = Path.Combine(tempDirectory, "model.metadata");

                        var writeWatch = Stopwatch.StartNew();
                        GltfWriter.Export(glbPath, result, context, true);
                        writeWatch.Stop();

                        string effectiveMetadataPath = metadataPath;
                        try
                        {
                            MetadataWriter.Export(metadataPath, result, context, doc,
                                "全模型", settings.DetailLevel, null, true);
                        }
                        catch (Exception ex)
                        {
                            // 元数据是旁路产物：写出失败不阻断转换，仅少一张属性表
                            context.Log(string.Format("元数据写出失败(降级为无属性表): {0}", ex.Message));
                            effectiveMetadataPath = null;
                        }

                        // 4. 读取项目地理位置并调用转换器
                        GeoLocation geo = GeoLocation.FromDocument(doc);
                        if (geo.HasValue)
                            context.Log(string.Format("项目地理位置: {0}", geo.ToArgument()));
                        else
                            context.Log("项目未设置地理位置，转换器使用默认坐标定位。");

                        ConverterLauncher.Result convert = ConverterLauncher.Run(
                            glbPath, effectiveMetadataPath, outputDirectory, geo, false, false, context);

                        if (convert.Cancelled)
                        {
                            cancelled = true;
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
                            summaryLine = string.Format(
                                "构件 {0} | 三角形 {1:N0} | 顶点 {2:N0} | 材质 {3} | 贴图 {4} | 提取 {5:0.0}s | 写出 {6:0.0}s",
                                context.MeshElementCount, context.TriangleCount, context.VertexCount,
                                context.MaterialCount, context.BitmapTextureCount,
                                extractWatch.Elapsed.TotalSeconds, writeWatch.Elapsed.TotalSeconds);
                            if (context.SharedMeshCount > 0)
                                summaryLine += string.Format(
                                    "\n实例化: 共享网格 {0} 个 / 实例 {1} 个, 展开三角形 {2:N0} → 去重 {3:N0} (省 {4:0.0}%)",
                                    context.SharedMeshCount, context.InstanceCount,
                                    context.ExpandedTriangleCount, context.TriangleCount,
                                    (1 - (double)context.TriangleCount / Math.Max(1, context.ExpandedTriangleCount)) * 100);
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
                if (tempDirectory != null && !tempKeep)
                    CleanupTempDirectory(tempDirectory);
            }

            if (cancelled)
            {
                TaskDialog.Show("导出已取消", "已中止导出，未生成完整的 3D Tiles 输出。");
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

        /// <summary>选择输出目录（默认桌面下的 项目名_3dtiles）</summary>
        private static string SelectOutputDirectory(Document doc)
        {
            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(projectName)) projectName = doc.Title;

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择 3D Tiles 输出目录";
                dialog.SelectedPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), projectName + "_3dtiles");
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
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
