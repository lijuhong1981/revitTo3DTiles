using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitTo3DTiles.Commands
{
    /// <summary>Revit 应用入口：注册「导出 glTF」与「导出 3D Tiles」两个按钮</summary>
    public class App : IExternalApplication
    {
        private const string TabName = "模型转换";
        private const string PanelName = "模型导出";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // 选项卡已存在（多个插件共用）时忽略
            }

            RibbonPanel panel;
            try
            {
                panel = application.CreateRibbonPanel(TabName, PanelName);
            }
            catch
            {
                panel = application.GetRibbonPanels(TabName)[0];
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            var gltfButton = new PushButtonData(
                "ExportGltf",
                "导出\nglTF",
                assemblyPath,
                "RevitTo3DTiles.Commands.ExportGltfCommand")
            {
                ToolTip = "将当前模型导出为 glTF/glb（保留贴图与实例化去重）",
                LongDescription = "设置弹窗选择范围/精度/格式后，提取几何/材质/贴图写出 glTF 2.0" +
                                  "（可选同名 .metadata 记录构件 BIM 属性与导出设置）。\n" +
                                  "不调用 3D Tiles 转换器。"
            };
            panel.AddItem(gltfButton);

            var tilesButton = new PushButtonData(
                "ExportTo3DTiles",
                "导出\n3D Tiles",
                assemblyPath,
                "RevitTo3DTiles.Commands.ExportTo3DTilesCommand")
            {
                ToolTip = "将当前模型导出为 3D Tiles（保留贴图、BIM属性与楼层层级）",
                LongDescription = "提取几何/材质/贴图/元数据（与导出 glTF 共用管线，含实例化去重），" +
                                  "写出临时 glb + .metadata，调用 modelTo3DTiles 转换为 3D Tiles" +
                                  "（b3dm/Draco/纹理图集/构件属性表，按项目地理位置定位）。\n" +
                                  "需要 modelTo3DTiles.exe 随插件部署或设置环境变量 MODELTO3DTILES_PATH。"
            };
            panel.AddItem(tilesButton);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
