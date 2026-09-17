using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Extraction
{
    /// <summary>
    /// 层级提取器：构建"项目 → 标高(楼层) → 构件"的空间结构树。
    /// 该层级写入 meta.json，随3D Tiles一并输出，供前端建树与按楼层过滤/显隐。
    /// </summary>
    public static class HierarchyExtractor
    {
        /// <summary>层级树节点</summary>
        public class TreeNode
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public List<TreeNode> Children { get; set; } = new List<TreeNode>();
            /// <summary>该节点直接关联的构件键列表（叶子层使用）</summary>
            public List<string> ElementKeys { get; set; } = new List<string>();
        }

        /// <summary>
        /// 按楼层分组构建层级树：Root → 各标高 → 构件键。
        /// 无楼层归属的构件挂到"未指定标高"节点。
        /// </summary>
        public static TreeNode Build(Document doc, IEnumerable<string> elementKeys,
            Func<string, string> storeyOf, TileExportContext context)
        {
            var root = new TreeNode { Id = "root", Name = doc.Title, Type = "Project" };

            // 楼层顺序按标高(米)升序，与建筑认知一致
            var sortedLevels = new List<Level>();
            try
            {
                sortedLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => level.Elevation * GeometryExtractor.FeetToMeter)
                    .ToList();
            }
            catch (Exception ex)
            {
                context.Log("读取标高失败: " + ex.Message);
            }

            var levelNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (Level level in sortedLevels)
            {
                var node = new TreeNode
                {
                    Id = level.UniqueId,
                    Name = level.Name,
                    Type = "Storey"
                };
                levelNodes[level.Name] = node;
                root.Children.Add(node);
            }

            TreeNode unassigned = null;
            foreach (string key in elementKeys)
            {
                string storeyName = storeyOf(key);
                TreeNode target;
                if (string.IsNullOrWhiteSpace(storeyName))
                {
                    if (unassigned == null)
                    {
                        unassigned = new TreeNode { Id = "unassigned", Name = "未指定标高", Type = "Storey" };
                        root.Children.Add(unassigned);
                    }
                    target = unassigned;
                }
                else if (!levelNodes.TryGetValue(storeyName, out target))
                {
                    // 参数里出现但标高集合中没有的名称，动态建节点
                    target = new TreeNode { Id = "level_" + storeyName, Name = storeyName, Type = "Storey" };
                    levelNodes[storeyName] = target;
                    root.Children.Add(target);
                }
                target.ElementKeys.Add(key);
            }

            context.Log(string.Format("层级树构建完成: 标高节点 {0} 个", root.Children.Count));
            return root;
        }
    }
}
