using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitTo3DTiles.Models;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Extraction
{
    /// <summary>
    /// 几何提取器：遍历文档中所有非类型元素，提取三角网格并按材质分组。
    /// 要点：
    /// - 支持项目文档(rvt)与族文档(rfa)，嵌套族几何通过GeometryInstance展开为世界坐标；
    /// - 单位由Revit内部英尺换算为米（×0.3048），坐标系保持Revit的Z-up（在meta.json中声明）；
    /// - 逐面三角化，平面法线由三角形叉积计算，UV按面的投影参数域归一化到[0,1]。
    /// </summary>
    public static class GeometryExtractor
    {
        /// <summary>英尺 → 米</summary>
        public const double FeetToMeter = 0.3048;

        public static List<RevitElementNode> Extract(Document doc, TileExportContext context)
        {
            var nodes = new List<RevitElementNode>();
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            foreach (Element element in collector)
            {
                context.ElementCount++;
                try
                {
                    GeometryElement geometry = element.get_Geometry(options);
                    if (geometry == null)
                    {
                        context.SkippedElementCount++;
                        continue;
                    }

                    var primitives = new Dictionary<string, RevitPrimitive>();
                    ExtractGeometry(geometry, Transform.Identity, element, primitives, context);

                    if (primitives.Count > 0)
                    {
                        var node = new RevitElementNode
                        {
                            Key = element.UniqueId,
                            Name = element.Name,
                            ElementId = element.Id.IntegerValue,
                            ParentKey = null
                        };
                        node.Primitives.AddRange(primitives.Values);
                        nodes.Add(node);
                        context.MeshElementCount++;
                        foreach (RevitPrimitive prim in node.Primitives)
                            context.TriangleCount += prim.Indices.Count / 3;
                    }
                    else
                    {
                        context.SkippedElementCount++;
                    }
                }
                catch (Exception ex)
                {
                    context.SkippedElementCount++;
                    context.Log(string.Format("跳过元素 {0}({1}): {2}", element.Id.IntegerValue, element.Name, ex.Message));
                }
            }
            context.Log(string.Format("几何提取完成: 元素 {0} 个, 含几何 {1} 个, 三角形 {2} 个, 跳过 {3} 个",
                context.ElementCount, context.MeshElementCount, context.TriangleCount, context.SkippedElementCount));
            return nodes;
        }

        /// <summary>
        /// 递归提取几何对象。
        /// GeometryInstance 使用 GetInstanceGeometry()（已应用实例变换、含嵌套），故不再累乘变换。
        /// </summary>
        private static void ExtractGeometry(GeometryElement geometry, Transform transform, Element element,
            Dictionary<string, RevitPrimitive> primitives, TileExportContext context)
        {
            foreach (GeometryObject obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 1e-9 && solid.Faces.Size > 0)
                        ExtractSolid(solid, transform, element.Document, primitives, context);
                    continue;
                }

                var instance = obj as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement instanceGeometry = instance.GetInstanceGeometry();
                    if (instanceGeometry != null)
                        ExtractGeometry(instanceGeometry, Transform.Identity, element, primitives, context);
                    continue;
                }

                var mesh = obj as Mesh;
                if (mesh != null)
                    ExtractMesh(mesh, transform, element.Document, primitives, context);
            }
        }

        private static void ExtractSolid(Solid solid, Transform transform, Document doc,
            Dictionary<string, RevitPrimitive> primitives, TileExportContext context)
        {
            foreach (Face face in solid.Faces)
            {
                Material material = doc.GetElement(face.MaterialElementId) as Material;
                RevitPrimitive primitive = GetOrCreatePrimitive(primitives, material, doc, context);

                Mesh mesh = face.Triangulate();
                if (mesh == null) continue;

                // 逐三角形投影到参数域并收集UV，统一求包围盒后归一化到[0,1]
                var triangles = new List<XYZ[]>();
                var triangleUvs = new List<UV[]>();
                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;

                for (int t = 0; t < mesh.NumTriangles; t++)
                {
                    var triangle = mesh.get_Triangle(t);
                    var points = new XYZ[3];
                    var uvs = new UV[3];
                    for (int j = 0; j < 3; j++)
                    {
                        XYZ point = transform.OfPoint(triangle.get_Vertex(j));
                        points[j] = point;
                        UV uv = ProjectUV(face, point);
                        uvs[j] = uv;
                        if (uv.U < minU) minU = uv.U;
                        if (uv.U > maxU) maxU = uv.U;
                        if (uv.V < minV) minV = uv.V;
                        if (uv.V > maxV) maxV = uv.V;
                    }
                    triangles.Add(points);
                    triangleUvs.Add(uvs);
                }

                double rangeU = maxU - minU;
                double rangeV = maxV - minV;

                for (int t = 0; t < triangles.Count; t++)
                {
                    XYZ p0 = triangles[t][0], p1 = triangles[t][1], p2 = triangles[t][2];
                    XYZ normal = (p1 - p0).CrossProduct(p2 - p0);
                    normal = normal.GetLength() > 1e-12 ? normal.Normalize() : XYZ.BasisZ;

                    uint baseIndex = (uint)(primitive.Positions.Count / 3);
                    for (int j = 0; j < 3; j++)
                    {
                        UV uv = triangleUvs[t][j];
                        var normalized = new UV(
                            Math.Abs(rangeU) > 1e-12 ? (uv.U - minU) / rangeU : 0,
                            Math.Abs(rangeV) > 1e-12 ? (uv.V - minV) / rangeV : 0);
                        AddVertex(primitive, triangles[t][j], normal, normalized);
                    }
                    primitive.Indices.Add(baseIndex);
                    primitive.Indices.Add(baseIndex + 1);
                    primitive.Indices.Add(baseIndex + 2);
                }
            }
        }

        /// <summary>提取自由网格（非Solid的Mesh几何），UV按三角形三点铺满[0,1]</summary>
        private static void ExtractMesh(Mesh mesh, Transform transform, Document doc,
            Dictionary<string, RevitPrimitive> primitives, TileExportContext context)
        {
            RevitPrimitive primitive = GetOrCreatePrimitive(primitives, null, doc, context);
            for (int t = 0; t < mesh.NumTriangles; t++)
            {
                var triangle = mesh.get_Triangle(t);
                XYZ p0 = transform.OfPoint(triangle.get_Vertex(0));
                XYZ p1 = transform.OfPoint(triangle.get_Vertex(1));
                XYZ p2 = transform.OfPoint(triangle.get_Vertex(2));
                XYZ normal = (p1 - p0).CrossProduct(p2 - p0);
                normal = normal.GetLength() > 1e-12 ? normal.Normalize() : XYZ.BasisZ;

                uint baseIndex = (uint)(primitive.Positions.Count / 3);
                AddVertex(primitive, p0, normal, new UV(0, 0));
                AddVertex(primitive, p1, normal, new UV(1, 0));
                AddVertex(primitive, p2, normal, new UV(0, 1));
                primitive.Indices.Add(baseIndex);
                primitive.Indices.Add(baseIndex + 1);
                primitive.Indices.Add(baseIndex + 2);
            }
        }

        /// <summary>将点投影到面的参数域得到UV（Revit的Face.Project返回IntersectionResult）</summary>
        private static UV ProjectUV(Face face, XYZ point)
        {
            try
            {
                var result = face.Project(point);
                if (result != null && result.UVPoint != null)
                    return result.UVPoint;
            }
            catch
            {
                // 投影失败（点不在面上等）时退化为(0,0)
            }
            return new UV(0, 0);
        }

        /// <summary>顶点写入并按英尺→米换算</summary>
        private static void AddVertex(RevitPrimitive primitive, XYZ point, XYZ normal, UV uv)
        {
            primitive.Positions.Add((float)(point.X * FeetToMeter));
            primitive.Positions.Add((float)(point.Y * FeetToMeter));
            primitive.Positions.Add((float)(point.Z * FeetToMeter));
            primitive.Normals.Add((float)normal.X);
            primitive.Normals.Add((float)normal.Y);
            primitive.Normals.Add((float)normal.Z);
            primitive.Uvs.Add((float)uv.U);
            primitive.Uvs.Add((float)uv.V);
        }

        /// <summary>按材质ID取得(或创建)图元分组，材质信息由MaterialExtractor填充</summary>
        private static RevitPrimitive GetOrCreatePrimitive(Dictionary<string, RevitPrimitive> primitives,
            Material material, Document doc, TileExportContext context)
        {
            int materialId = material != null ? material.Id.IntegerValue : -1;
            string key = materialId.ToString();
            RevitPrimitive primitive;
            if (!primitives.TryGetValue(key, out primitive))
            {
                primitive = new RevitPrimitive { MaterialId = materialId };
                MaterialExtractor.Apply(primitive, material, doc, context);
                primitives[key] = primitive;
            }
            return primitive;
        }
    }
}
