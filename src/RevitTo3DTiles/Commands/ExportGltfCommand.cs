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
using RevitTo3DTiles.Native;
using RevitTo3DTiles.Output;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Commands
{
    /// <summary>
    /// 导出 glTF 命令：设置弹窗（范围/文件名/目录/DetailLevel/Triangulate/格式/元数据/贴图分离/贴图标准化）
    /// → 按所选精度提取几何/材质/贴图 → 写出单个 glTF（.gltf + .bin + textures/ 或 .glb）。
    /// 可选勾选「导出元数据」生成同名 .metadata（项目信息 + 构件 BIM 信息）。
    /// 行为自 revitToGltf v0.5.0 等价迁移（设置弹窗抽出为共享的 ExportSettingsForm）。
    /// 不调用 3D Tiles 转换器——那是「导出 3D Tiles」按钮的职责。
    /// 输出为 <根目录>\<项目名>_<detail>_<tri>.gltf（如 ljdd_fine_1.00.gltf），统计追加写入 gltf-export.log。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportGltfCommand : IExternalCommand
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

            // 1. 设置弹窗（共享组件）：范围 + 文件名 + 输出目录 + DetailLevel + Triangulate + 格式(.gltf/.glb) + 元数据
            ExportSettings settings = ExportSettingsForm.Show(uiDocument, doc, ExportMode.Gltf);
            if (settings == null)
                return Result.Cancelled;

            string outputDirectory = settings.OutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            string gltfPath = Path.Combine(outputDirectory, settings.FileName + (settings.Binary ? ".glb" : ".gltf"));
            string metadataPath = Path.Combine(outputDirectory, settings.FileName + ".metadata");
            string logPath = Path.Combine(outputDirectory, "gltf-export.log");

            var detailSettings = new GeometryDetailSettings(settings.FileName, settings.DetailLevel, settings.TriangulateLod)
            {
                LogProgressEvery = 2000,
                ExportMetadata = settings.ExportMetadata
            };

            string summaryLine = null;
            string failure = null;      // 非空表示失败，需弹窗提示
            bool cancelled = false;
            bool writing = false;       // 切换进度条权重：提取 0~85%，写出 85~100%
            double extractSeconds = 0;
            double writeSeconds = 0;

            var progress = new ExportProgressForm("导出 glTF");
            try
            {
                progress.Show();
                Application.DoEvents();

                using (var log = new StreamWriter(logPath, true, Encoding.UTF8))
                {
                    var context = new GltfExportContext(outputDirectory, log)
                    {
                        OnProgress = (percent, text) => progress.Report(
                            writing ? 85 + percent * 15 / 100 : percent * 85 / 100, text),
                        ShouldCancel = () => progress.Cancelled,
                        SeparateTextures = settings.SeparateTextures,
                        NormalizeTextures = settings.NormalizeTextures,
                        DracoEnabled = settings.DracoCompression && DracoEncoder.Available
                    };
                    context.Log(string.Empty);
                    context.Log(string.Format("===== 导出 glTF: {0} ({1}) =====", doc.Title, doc.PathName));
                    context.Log(string.Format("范围: {0}", settings.ScopeName));
                    context.Log(string.Format("精度: Detail={0}, Tri={1}", settings.DetailLevel, ExportSettingsForm.TriText(settings.TriangulateLod)));
                    context.Log(string.Format("贴图: {0}, 2的幂归一化: {1}, Draco压缩: {2}",
                        settings.SeparateTextures ? "分离到 textures/" : "内嵌进 bin/glb",
                        settings.NormalizeTextures ? "开" : "关",
                        context.DracoEnabled ? "开" : (settings.DracoCompression ? "开(原生dll缺失,已回退关闭)" : "关")));
                    context.Log(string.Format("输出文件: {0}", gltfPath));
                    if (settings.ExportMetadata)
                        context.Log(string.Format("输出元数据: {0}", metadataPath));

                    var extractWatch = Stopwatch.StartNew();
                    ExtractResult result = null;
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
                        writing = true;
                        var writeWatch = Stopwatch.StartNew();
                        try
                        {
                            GltfWriter.Export(gltfPath, result, context, settings.Binary);
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
                            // 顶点数在写出过程中由写出器累计（图元数组写完即释放，无法再从 result 反查）
                            long vertexCount = context.VertexCount;
                            long sizeBytes = FileSize(gltfPath)
                                + (settings.Binary ? 0 : FileSize(Path.ChangeExtension(gltfPath, ".bin")));
                            double sizeMb = sizeBytes / 1048576.0;

                            string formatTag = settings.Binary ? "glb" : "gltf";
                            string sizeTag = settings.Binary ? "glb" : "gltf+bin";
                            summaryLine = string.Format(
                                "[{0}] {1} | Detail={2} | Tri={3} | 元素 {4} | 含几何 {5} | 三角形 {6:N0} | 顶点 {7:N0} | {8} {9:0.0}MB | 提取 {10:0.0}s | 写出 {11:0.0}s",
                                formatTag, settings.FileName, settings.DetailLevel, ExportSettingsForm.TriText(settings.TriangulateLod),
                                context.ElementCount, context.MeshElementCount, context.TriangleCount, vertexCount,
                                sizeTag, sizeMb, extractSeconds, writeSeconds);
                            if (context.SharedMeshCount > 0)
                                summaryLine += string.Format("\n实例化: 共享网格 {0} 个 / 实例 {1} 个, 展开三角形 {2:N0} → 去重 {3:N0} (省 {4:0.0}%)",
                                    context.SharedMeshCount, context.InstanceCount,
                                    context.ExpandedTriangleCount, context.TriangleCount,
                                    (1 - (double)context.TriangleCount / Math.Max(1, context.ExpandedTriangleCount)) * 100);

                            if (context.DracoPrimitiveCount > 0)
                                summaryLine += string.Format("\nDraco: {0} 图元 {1:0.0}MB → {2:0.0}MB (省 {3:0.0}%)",
                                    context.DracoPrimitiveCount,
                                    context.DracoRawBytes / 1048576.0,
                                    context.DracoCompressedBytes / 1048576.0,
                                    (1 - (double)context.DracoCompressedBytes / Math.Max(1, context.DracoRawBytes)) * 100);
                            if (context.DracoSkippedCount > 0)
                                summaryLine += string.Format("\nDraco: {0} 图元超限未压缩", context.DracoSkippedCount);
                            if (context.DracoFailedCount > 0)
                                summaryLine += string.Format("\nDraco: {0} 图元编码失败已回退未压缩", context.DracoFailedCount);

                            if (settings.ExportMetadata)
                            {
                                try
                                {
                                    int metaCount = MetadataWriter.Export(metadataPath, result, context, doc,
                                        settings.ScopeName, settings.DetailLevel, settings.TriangulateLod, settings.Binary);
                                    summaryLine += string.Format("\n元数据: {0} 条 → {1} ({2:0.0}KB)",
                                        metaCount, Path.GetFileName(metadataPath), FileSize(metadataPath) / 1024.0);
                                }
                                catch (Exception ex)
                                {
                                    context.Log(string.Format("元数据写出失败({0}): {1}", Path.GetFileName(metadataPath), ex.Message));
                                    summaryLine += "\n元数据: 写出失败（详见日志）";
                                }
                            }
                            context.Log(summaryLine);
                        }
                    }
                }
            }
            finally
            {
                if (!progress.IsDisposed)
                {
                    progress.Close();
                    progress.Dispose();
                }
            }

            if (cancelled)
            {
                TaskDialog.Show("导出已取消",
                    string.Format("已在提取 {0:0.0}s / 写出 {1:0.0}s 后中止。\n已写出的文件可能不完整，建议删除后重新导出。",
                        extractSeconds, writeSeconds));
                return Result.Cancelled;
            }

            if (failure != null)
            {
                TaskDialog.Show("导出失败", failure);
                return Result.Failed;
            }

            var dialog = new TaskDialog("导出 glTF 完成");
            dialog.MainInstruction = "导出统计（详见 gltf-export.log）";
            dialog.MainContent = summaryLine ?? "（无内容）";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "打开输出目录");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Process.Start("explorer.exe", outputDirectory);

            return Result.Succeeded;
        }

        private static long FileSize(string path)
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
    }
}
