using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitTo3DTiles.Models;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Output
{
    /// <summary>
    /// glTF 2.0 写出器（外部 .bin + 外部贴图）。
    /// 顶点为世界坐标（已含嵌套族变换），故节点不写变换；
    /// 使用uint32索引规避旧实现的65535顶点上限；含NORMAL/TEXCOORD_0与贴图引用。
    /// 输出可被 modelTo3DTiles 的 glTF 管线直接消费。
    /// </summary>
    public static class GltfWriter
    {
        private const int ComponentTypeFloat = 5126;
        private const int ComponentTypeUnsignedShort = 5123;
        private const int ComponentTypeUnsignedInt = 5125;
        private const int TargetArrayBuffer = 34962;
        private const int TargetElementArrayBuffer = 34963;

        public static void Export(string gltfPath, List<RevitElementNode> nodes, TileExportContext context)
        {
            var binary = new MemoryStream();
            var bufferViews = new JArray();
            var accessors = new JArray();
            var meshes = new JArray();
            var materials = new JArray();
            var gltfNodes = new JArray();
            var images = new JArray();
            var textures = new JArray();
            var samplers = new JArray();

            var materialIndexMap = new Dictionary<int, int>();
            var textureIndexMap = new Dictionary<string, int>();

            // 采样器：UV按面归一化，使用CLAMP_TO_EDGE避免边缘渗色
            samplers.Add(new JObject
            {
                ["magFilter"] = 9729,   // LINEAR
                ["minFilter"] = 9987,   // LINEAR_MIPMAP_LINEAR
                ["wrapS"] = 33071,      // CLAMP_TO_EDGE
                ["wrapT"] = 33071
            });

            foreach (RevitElementNode node in nodes)
            {
                if (!node.HasGeometry) continue;

                var primitives = new JArray();
                foreach (RevitPrimitive primitive in node.Primitives)
                {
                    if (primitive.VertexCount == 0) continue;

                    int materialIndex = GetMaterialIndex(primitive, materials, materialIndexMap,
                        textures, images, textureIndexMap);

                    // 顶点数据：位置 / 法线 / UV / 索引，各bufferView按4字节对齐
                    int positionAccessor = WriteVec3Accessor(binary, bufferViews, accessors, primitive.Positions, true);
                    int normalAccessor = WriteVec3Accessor(binary, bufferViews, accessors, primitive.Normals, false);
                    int uvAccessor = -1;
                    if (primitive.Uvs.Count == primitive.VertexCount * 2)
                        uvAccessor = WriteVec2Accessor(binary, bufferViews, accessors, primitive.Uvs);
                    int indexAccessor = WriteIndexAccessor(binary, bufferViews, accessors, primitive.Indices);

                    var attributes = new JObject { ["POSITION"] = positionAccessor };
                    if (normalAccessor >= 0) attributes["NORMAL"] = normalAccessor;
                    if (uvAccessor >= 0) attributes["TEXCOORD_0"] = uvAccessor;

                    primitives.Add(new JObject
                    {
                        ["attributes"] = attributes,
                        ["indices"] = indexAccessor,
                        ["material"] = materialIndex,
                        ["mode"] = 4  // TRIANGLES
                    });
                }

                if (primitives.Count == 0) continue;

                meshes.Add(new JObject { ["primitives"] = primitives });
                gltfNodes.Add(new JObject
                {
                    // 节点名 = Revit UniqueId，作为与 meta.json 的匹配键
                    ["name"] = node.Key,
                    ["mesh"] = meshes.Count - 1
                });
            }

            // 全部节点挂在场景根下（层级信息由 meta.json 的tree承载，保持glTF扁平以便转换器按构件切分）
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "revitTo3DTiles"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray { new JObject { ["nodes"] = new JArray(Enumerable.Range(0, gltfNodes.Count)) } },
                ["nodes"] = gltfNodes,
                ["meshes"] = meshes,
                ["materials"] = materials,
                ["buffers"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = Path.GetFileNameWithoutExtension(gltfPath) + ".bin",
                        ["byteLength"] = binary.Length
                    }
                },
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors
            };

            if (images.Count > 0)
            {
                gltf["images"] = images;
                gltf["textures"] = textures;
                gltf["samplers"] = samplers;
            }

            string binPath = Path.ChangeExtension(gltfPath, ".bin");
            File.WriteAllBytes(binPath, binary.ToArray());
            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));

            context.Log(string.Format("glTF写出完成: 节点 {0} 个, 网格 {1} 个, 材质 {2} 个, 贴图 {3} 个, 二进制 {4}MB",
                gltfNodes.Count, meshes.Count, materials.Count, images.Count, binary.Length / 1048576));
            context.Log(string.Format("材质贴图诊断: 处理材质 {0} 个, 含渲染外观资产 {1} 个, 含位图贴图 {2} 个",
                context.MaterialCount, context.AppearanceAssetCount, context.BitmapTextureCount));
        }

        /// <summary>材质去重并生成glTF材质定义（含baseColorTexture引用）</summary>
        private static int GetMaterialIndex(RevitPrimitive primitive, JArray materials,
            Dictionary<int, int> materialIndexMap, JArray textures, JArray images,
            Dictionary<string, int> textureIndexMap)
        {
            int index;
            if (materialIndexMap.TryGetValue(primitive.MaterialId, out index))
                return index;

            var pbr = new JObject
            {
                ["baseColorFactor"] = new JArray(primitive.BaseColor),
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 1.0
            };

            if (!string.IsNullOrEmpty(primitive.TextureUri))
            {
                int textureIndex;
                if (!textureIndexMap.TryGetValue(primitive.TextureUri, out textureIndex))
                {
                    images.Add(new JObject { ["uri"] = primitive.TextureUri });
                    textures.Add(new JObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                    textureIndex = textures.Count - 1;
                    textureIndexMap[primitive.TextureUri] = textureIndex;
                }
                pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
            }

            var material = new JObject
            {
                ["name"] = primitive.MaterialName ?? ("Material_" + primitive.MaterialId),
                ["pbrMetallicRoughness"] = pbr,
                ["doubleSided"] = primitive.DoubleSided
            };
            if (primitive.BaseColor.Length > 3 && primitive.BaseColor[3] < 0.999f)
                material["alphaMode"] = "BLEND";

            materials.Add(material);
            index = materials.Count - 1;
            materialIndexMap[primitive.MaterialId] = index;
            return index;
        }

        /// <summary>写入VEC3数据(位置或法线)，返回accessor索引；数据为空返回-1</summary>
        private static int WriteVec3Accessor(MemoryStream binary, JArray bufferViews, JArray accessors,
            List<float> values, bool computeBounds)
        {
            if (values.Count == 0) return -1;
            int count = values.Count / 3;
            int byteOffset = AlignTo4(binary);
            foreach (float value in values)
                WriteFloat(binary, value);

            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = count * 12,
                ["target"] = TargetArrayBuffer
            });

            var accessor = new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = ComponentTypeFloat,
                ["count"] = count,
                ["type"] = "VEC3"
            };
            if (computeBounds)
            {
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < values.Count; i += 3)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        min[axis] = Math.Min(min[axis], values[i + axis]);
                        max[axis] = Math.Max(max[axis], values[i + axis]);
                    }
                }
                accessor["min"] = new JArray(min.Select(v => (object)v));
                accessor["max"] = new JArray(max.Select(v => (object)v));
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        /// <summary>写入VEC2数据(UV)</summary>
        private static int WriteVec2Accessor(MemoryStream binary, JArray bufferViews, JArray accessors,
            List<float> values)
        {
            if (values.Count == 0) return -1;
            int count = values.Count / 2;
            int byteOffset = AlignTo4(binary);
            foreach (float value in values)
                WriteFloat(binary, value);

            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = count * 8,
                ["target"] = TargetArrayBuffer
            });
            accessors.Add(new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = ComponentTypeFloat,
                ["count"] = count,
                ["type"] = "VEC2"
            });
            return accessors.Count - 1;
        }

        /// <summary>写入索引：顶点数小于65536用uint16，否则uint32（旧实现的65535溢出问题在此规避）</summary>
        private static int WriteIndexAccessor(MemoryStream binary, JArray bufferViews, JArray accessors,
            List<uint> indices)
        {
            if (indices.Count == 0) return -1;
            bool useShort = indices.Max() < 65536;
            int byteOffset = AlignTo4(binary);
            foreach (uint index in indices)
            {
                if (useShort)
                {
                    binary.WriteByte((byte)(index & 0xFF));
                    binary.WriteByte((byte)((index >> 8) & 0xFF));
                }
                else
                {
                    WriteUInt32(binary, index);
                }
            }

            int byteLength = (int)binary.Length - byteOffset;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = byteLength,
                ["target"] = TargetElementArrayBuffer
            });
            accessors.Add(new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = useShort ? ComponentTypeUnsignedShort : ComponentTypeUnsignedInt,
                ["count"] = indices.Count,
                ["type"] = "SCALAR"
            });
            return accessors.Count - 1;
        }

        private static int AlignTo4(MemoryStream stream)
        {
            while (stream.Length % 4 != 0)
                stream.WriteByte(0);
            return (int)stream.Length;
        }

        private static void WriteFloat(MemoryStream stream, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUInt32(MemoryStream stream, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
