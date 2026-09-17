using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitTo3DTiles.Extraction;
using RevitTo3DTiles.Models;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Output
{
    /// <summary>
    /// meta.json 写出器（BIM语义 sidecar）。
    /// glTF承载几何/材质/贴图，本文件承载BIM专属语义：单位与轴向、空间层级树、逐构件属性集。
    /// 转换器（modelTo3DTiles --inputMetadata）读取后转入 EXT_structural_metadata 属性表，
    /// 使 Cesium/three.js 端可按构件查询真实BIM属性并按楼层过滤。
    /// </summary>
    public static class MetaWriter
    {
        /// <summary>schema版本，字段演进时递增</summary>
        public const int SchemaVersion = 1;

        public static void Export(string metaPath, List<RevitElementNode> nodes,
            HierarchyExtractor.TreeNode tree, string projectName, TileExportContext context)
        {
            var elements = new JObject();
            foreach (RevitElementNode node in nodes)
            {
                var element = new JObject
                {
                    ["elementId"] = node.ElementId,
                    ["name"] = node.Name
                };
                if (!string.IsNullOrEmpty(node.CategoryName))
                    element["category"] = node.CategoryName;
                if (!string.IsNullOrEmpty(node.TypeName))
                    element["type"] = node.TypeName;
                if (!string.IsNullOrEmpty(node.StoreyName))
                    element["storey"] = node.StoreyName;
                if (node.Parameters != null && node.Parameters.Count > 0)
                    element["parameters"] = JObject.FromObject(node.Parameters);

                elements[node.Key] = element;
            }

            var meta = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["generator"] = "revitTo3DTiles",
                ["project"] = projectName,
                ["units"] = "meters",
                ["upAxis"] = "Z",   // Revit为Z-up；转换器据此做轴向归一化
                ["tree"] = TreeNodeToJson(tree),
                ["elements"] = elements
            };

            File.WriteAllText(metaPath, meta.ToString(Formatting.Indented));
            context.Log(string.Format("meta.json写出完成: 构件 {0} 个", elements.Count));
        }

        private static JObject TreeNodeToJson(HierarchyExtractor.TreeNode node)
        {
            var json = new JObject
            {
                ["id"] = node.Id,
                ["name"] = node.Name,
                ["type"] = node.Type
            };
            if (node.ElementKeys != null && node.ElementKeys.Count > 0)
                json["elements"] = new JArray(node.ElementKeys);
            if (node.Children != null && node.Children.Count > 0)
            {
                var children = new JArray();
                foreach (HierarchyExtractor.TreeNode child in node.Children)
                    children.Add(TreeNodeToJson(child));
                json["children"] = children;
            }
            return json;
        }
    }
}
