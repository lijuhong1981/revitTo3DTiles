using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitTo3DTiles.Commands
{
    /// <summary>Revit 应用入口：注册"导出3D Tiles"按钮</summary>
    public class App : IExternalApplication
    {
        private const string TabName = "模型转换";
        private const string PanelName = "3D Tiles";

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
            var buttonData = new PushButtonData(
                "ExportTo3DTiles",
                "导出\n3D Tiles",
                assemblyPath,
                "RevitTo3DTiles.Commands.ExportTo3DTilesCommand")
            {
                ToolTip = "将当前模型导出为 3D Tiles（保留贴图、BIM属性与楼层层级）",
                LongDescription = "提取几何/材质/贴图/属性/层级，写出 glTF + meta.json，" +
                                  "调用 modelTo3DTiles 转换为 3D Tiles（b3dm/Draco/纹理图集）。\n" +
                                  "需要 modelTo3DTiles.exe 随插件部署或设置环境变量 MODELTO3DTILES_PATH。"
            };
            panel.AddItem(buttonData);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
