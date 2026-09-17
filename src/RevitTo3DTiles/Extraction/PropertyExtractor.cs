using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Extraction
{
    /// <summary>
    /// 属性提取器：提取元素参数集、类别、类型名、楼层归属。
    /// 这些字段将写入 meta.json，最终由转换器转入 EXT_structural_metadata 属性表，
    /// 供 Cesium/three.js 端按构件查询（feature.getProperty('防火等级') 等）。
    /// </summary>
    public static class PropertyExtractor
    {
        /// <summary>参数数量上限，防止个别元素参数爆炸</summary>
        private const int MaxParameterCount = 60;

        public static Dictionary<string, string> ExtractParameters(Element element, TileExportContext context)
        {
            var parameters = new Dictionary<string, string>();
            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameters.Count >= MaxParameterCount) break;
                    if (parameter == null || !parameter.HasValue) continue;

                    string key = parameter.Definition != null ? parameter.Definition.Name : null;
                    if (string.IsNullOrWhiteSpace(key) || parameters.ContainsKey(key)) continue;

                    string value = ReadParameterValue(parameter);
                    if (!string.IsNullOrWhiteSpace(value))
                        parameters[key] = value;
                }
            }
            catch (Exception ex)
            {
                context.Log(string.Format("读取元素 {0} 参数失败: {1}", element.Id.IntegerValue, ex.Message));
            }
            return parameters;
        }

        /// <summary>读取参数值（Double用工程单位字符串，避免英尺等内部单位泄露给用户）</summary>
        private static string ReadParameterValue(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Double:
                        return parameter.AsValueString();
                    case StorageType.Integer:
                        // 是/否类参数转为可读文本
                        if (parameter.Definition != null && parameter.Definition.ParameterType == ParameterType.YesNo)
                            return parameter.AsInteger() == 1 ? "是" : "否";
                        return parameter.AsInteger().ToString();
                    case StorageType.ElementId:
                        ElementId id = parameter.AsElementId();
                        return id != null ? id.IntegerValue.ToString() : null;
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>构件类别名（如"墙""风管""家具"）</summary>
        public static string GetCategoryName(Element element)
        {
            try
            {
                return element.Category != null ? element.Category.Name : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>类型名（如"基本墙:常规 - 200mm"）</summary>
        public static string GetTypeName(Element element, Document doc)
        {
            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                Element type = doc.GetElement(typeId);
                return type != null ? type.Name : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 楼层名：优先元素自身的标高参数，其次宿主(如墙上的门窗)的标高。
        /// 项目文档的楼层归属是"按楼层显隐/过滤"的基础。
        /// </summary>
        public static string GetStoreyName(Element element, Document doc)
        {
            try
            {
                ElementId levelId = element.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    Element level = doc.GetElement(levelId);
                    if (level != null) return level.Name;
                }

                // 常规模型等类别无LevelId时，尝试标高相关参数
                string[] levelParameterNames = { "标高", "参照标高", "基底标高", "Schedule Level", "Reference Level" };
                foreach (string name in levelParameterNames)
                {
                    Parameter parameter = element.LookupParameter(name);
                    if (parameter != null && parameter.HasValue)
                    {
                        string value = parameter.AsValueString() ?? parameter.AsString();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }

                // 退回宿主元素的标高（门窗等依附于墙）
                Element host = element as FamilyInstance != null ? ((FamilyInstance)element).Host : null;
                if (host != null && host.LevelId != null && host.LevelId != ElementId.InvalidElementId)
                {
                    Element hostLevel = doc.GetElement(host.LevelId);
                    if (hostLevel != null) return hostLevel.Name;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
