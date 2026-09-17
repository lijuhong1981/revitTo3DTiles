using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitTo3DTiles.Models
{
    /// <summary>
    /// 构件级网格数据：一个Revit元素对应一个节点，节点内按材质分组图元。
    /// 几何单位为米（已从Revit内部英尺换算），坐标系保持Revit的Z-up。
    /// </summary>
    public class RevitElementNode
    {
        /// <summary>稳定唯一键（Revit UniqueId），作为glTF节点名与meta.json的匹配键</summary>
        public string Key { get; set; }

        /// <summary>元素名称（用于展示）</summary>
        public string Name { get; set; }

        /// <summary>元素ID</summary>
        public int ElementId { get; set; }

        /// <summary>父节点Key（层级结构，可为null表示顶层）</summary>
        public string ParentKey { get; set; }

        /// <summary>构件类别名（墙/风管/家具等）</summary>
        public string CategoryName { get; set; }

        /// <summary>类型名</summary>
        public string TypeName { get; set; }

        /// <summary>所属楼层（标高名），用于层级树与按楼层过滤</summary>
        public string StoreyName { get; set; }

        /// <summary>参数集（写入meta.json）</summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        /// <summary>世界坐标变换（已含嵌套族的实例变换累积）</summary>
        public Transform Transform { get; set; } = Transform.Identity;

        /// <summary>按材质分组的几何图元</summary>
        public List<RevitPrimitive> Primitives { get; set; } = new List<RevitPrimitive>();

        /// <summary>是否含有有效几何（无几何的构件只进meta.json不进glTF）</summary>
        public bool HasGeometry { get { return Primitives.Count > 0; } }
    }

    /// <summary>
    /// 单一材质下的三角网格。顶点/UV/索引平行数组（非索引展开，索引顺序写入），
    /// 使用uint32索引以规避旧实现的65535顶点上限。
    /// </summary>
    public class RevitPrimitive
    {
        /// <summary>Revit材质元素ID（-1表示无材质）</summary>
        public int MaterialId { get; set; }

        /// <summary>材质名</summary>
        public string MaterialName { get; set; }

        /// <summary>基础色 [r,g,b,a]，取自材质外观色</summary>
        public float[] BaseColor { get; set; } = new float[] { 0.8f, 0.8f, 0.8f, 1f };

        /// <summary>是否双面材质</summary>
        public bool DoubleSided { get; set; } = true;

        /// <summary>顶点坐标，扁平 [x,y,z,...]</summary>
        public List<float> Positions { get; set; } = new List<float>();

        /// <summary>顶点法线，扁平 [x,y,z,...]（与Positions等长）</summary>
        public List<float> Normals { get; set; } = new List<float>();

        /// <summary>顶点UV，扁平 [u,v,...]（无贴图时为空）</summary>
        public List<float> Uvs { get; set; } = new List<float>();

        /// <summary>三角形索引</summary>
        public List<uint> Indices { get; set; } = new List<uint>();

        /// <summary>贴图相对路径（textures/xxx.png），null表示使用BaseColor</summary>
        public string TextureUri { get; set; }

        public int VertexCount { get { return Positions.Count / 3; } }
    }
}
