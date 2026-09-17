using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 流程：提取几何/材质/贴图/属性/层级 → 写出临时 glTF + meta.json → 调用 modelTo3DTiles 转换 → 输出 3D Tiles。
    /// 中间产物写在临时目录，用户只看到最终的 3D Tiles 输出。
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

            // 1. 选择输出目录
            string outputDirectory = SelectOutputDirectory(doc);
            if (string.IsNullOrEmpty(outputDirectory))
                return Result.Cancelled;

            Directory.CreateDirectory(outputDirectory);
            string logPath = Path.Combine(outputDirectory, "export.log");

            using (var log = new StreamWriter(logPath, false, System.Text.Encoding.UTF8))
            {
                var context = new TileExportContext(outputDirectory, log);
                context.Log(string.Format("开始导出: {0} ({1})", doc.Title, doc.PathName));
                context.Log(string.Format("输出目录: {0}", outputDirectory));

                try
                {
                    // 2. 提取几何与材质（含贴图外置）
                    var nodes = GeometryExtractor.Extract(doc, context);
                    if (nodes.Count == 0)
                    {
                        TaskDialog.Show("导出失败", "未从模型中提取到任何几何。详见 export.log");
                        return Result.Failed;
                    }

                    // 3. 提取属性与层级
                    var storeyMap = new Dictionary<string, string>();
                    var parametersMap = new Dictionary<string, Dictionary<string, string>>();
                    var elementMap = new Dictionary<string, Element>();
                    foreach (RevitElementNode node in nodes)
                    {
                        Element element = doc.GetElement(RevitUniqueIdToElementId(doc, node.Key));
                        if (element == null) continue;
                        elementMap[node.Key] = element;
                        node.CategoryName = PropertyExtractor.GetCategoryName(element);
                        node.TypeName = PropertyExtractor.GetTypeName(element, doc);
                        node.StoreyName = PropertyExtractor.GetStoreyName(element, doc);
                        node.Parameters = PropertyExtractor.ExtractParameters(element, context);
                        storeyMap[node.Key] = node.StoreyName;
                    }
                    var tree = HierarchyExtractor.Build(doc, nodes.Select(n => n.Key), key => storeyMap.ContainsKey(key) ? storeyMap[key] : null, context);

                    // 4. 写出中间产物（glTF + meta.json）到临时目录
                    Directory.CreateDirectory(context.TempDirectory);
                    string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
                    if (string.IsNullOrEmpty(projectName)) projectName = doc.Title;
                    string gltfPath = Path.Combine(context.TempDirectory, "model.gltf");
                    string metaPath = Path.Combine(context.TempDirectory, "model.meta.json");

                    GltfWriter.Export(gltfPath, nodes, context);
                    MetaWriter.Export(metaPath, nodes, tree, projectName, context);

                    // 5. 调用转换器生成 3D Tiles
                    var result = ConverterLauncher.Run(gltfPath, metaPath, outputDirectory, context);

                    // 6. 结果反馈
                    if (result.Success)
                    {
                        context.Log("导出完成。");
                        var dialog = new TaskDialog("导出完成");
                        dialog.MainInstruction = "3D Tiles 导出成功";
                        dialog.MainContent = string.Format(
                            "构件 {0} 个, 三角形 {1:N0}, 贴图 {2} 张\n输出目录: {3}",
                            context.MeshElementCount, context.TriangleCount, context.TextureFileCount, outputDirectory);
                        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "打开输出目录");
                        dialog.CommonButtons = TaskDialogCommonButtons.Close;
                        if (dialog.Show() == TaskDialogResult.CommandLink1)
                            System.Diagnostics.Process.Start("explorer.exe", outputDirectory);
                        return Result.Succeeded;
                    }

                    TaskDialog.Show("转换失败", (result.ErrorMessage ?? "未知错误") + "\n详见: " + logPath);
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    context.Log("导出异常: " + ex);
                    TaskDialog.Show("导出异常", ex.Message + "\n详见: " + logPath);
                    return Result.Failed;
                }
                finally
                {
                    CleanupTempDirectory(context);
                }
            }
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

        /// <summary>UniqueId 转 ElementId</summary>
        private static ElementId RevitUniqueIdToElementId(Document doc, string uniqueId)
        {
            try
            {
                return doc.GetElement(uniqueId) != null ? doc.GetElement(uniqueId).Id : ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private static void CleanupTempDirectory(TileExportContext context)
        {
            try
            {
                if (Directory.Exists(context.TempDirectory))
                    Directory.Delete(context.TempDirectory, true);
            }
            catch
            {
                // 清理失败不影响导出结果
            }
        }
    }
}
