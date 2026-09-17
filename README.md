# revitTo3DTiles

Revit 模型一键导出 3D Tiles 的 Revit 插件。保留**贴图、BIM 属性集与楼层层级**，输出可在 Cesium / three.js 中加载并支持构件级拾取与按楼层过滤。

## 背景

Revit 直接导出 IFC 时贴图常丢失，且 obj/fbx 导出会丢构件属性与层级。本插件采用**几何与语义分通道**的方案绕开该问题：

```
Revit 文档
  ├─ 几何 + 材质 + 贴图 + 节点树 → glTF 2.0（.gltf + .bin + textures/）
  └─ 属性集 + 楼层层级 + 单位   → meta.json（BIM语义 sidecar）
                ↓ 调用 modelTo3DTiles 转换
        3D Tiles（b3dm / Draco压缩 / 纹理图集 / 构件拾取扩展）
```

贴图直接取自 Revit 渲染外观（`AppearanceAssetElement`）中记录的原始图片文件，不经中间格式转换，因此不受 IFC 贴图丢失问题影响。

## 架构

插件只负责 Revit 侧的提取，转换由 [modelTo3DTiles](https://github.com/lijuhong1981/modelTo3DTiles) 以**独立进程**执行（大模型转换不占用 Revit 内存，崩溃不连累 Revit）。

| 模块 | 职责 |
|---|---|
| `Extraction/GeometryExtractor` | 遍历文档元素提取三角网格，支持嵌套族变换展开，英尺→米换算 |
| `Extraction/MaterialExtractor` | 材质颜色（渲染外观优先）与透明度 |
| `Extraction/TextureExtractor` | **从渲染外观提取贴图图片并按内容哈希外置去重** |
| `Extraction/PropertyExtractor` | 参数集、类别、类型名、楼层归属 |
| `Extraction/HierarchyExtractor` | 项目 → 标高 → 构件 空间结构树 |
| `Output/GltfWriter` | glTF 2.0 写出（POSITION/NORMAL/TEXCOORD_0，uint32 索引） |
| `Output/MetaWriter` | meta.json（schemaVersion / units / upAxis / tree / elements） |
| `Pipeline/ConverterLauncher` | 调用 modelTo3DTiles 并回显日志 |

## 构建

**依赖**：Revit 2020 + Visual Studio 2019（.NET 桌面开发工作负载）

1. 打开 `revitTo3DTiles.sln`
2. 确认已安装 `.NET Framework 4.7.2 目标包`（VS 安装器 → 单个组件 → ".NET Framework 4.7.2 targeting pack"）
   - 未安装时可临时降级编译：`msbuild revitTo3DTiles.sln -p:TargetFrameworkVersion=v4.7.1`
3. Revit 安装目录默认为 `D:\Program Files\Autodesk\Revit 2020`，不同请修改 `RevitTo3DTiles.csproj` 中的 `RevitInstallDir`，或用 `/p:RevitInstallDir="..."` 覆盖
4. 生成解决方案

**命令行构建**：

```bash
msbuild revitTo3DTiles.sln -p:Configuration=Release
```

## 安装与部署

1. 将 `bin\Debug\RevitTo3DTiles.dll`（或 Release）复制到：
   ```
   C:\ProgramData\Autodesk\Revit\Addins\2020\
   ```
2. 同目录放置 `RevitTo3DTiles.addin` 清单文件
3. **部署转换器**：将 `modelTo3DTiles.exe` 放在 DLL 同目录，或设置环境变量 `MODELTO3DTILES_PATH` 指向它

启动 Revit 后，功能区出现 **模型转换 → 3D Tiles → 导出3D Tiles** 按钮。

> 调试期可用 Add-In Manager 直接加载 DLL，无需重启 Revit。

## 使用

1. 打开 `.rvt` 模型
2. 点击 **导出3D Tiles**
3. 选择输出目录（默认桌面下 `<项目名>_3dtiles`）
4. 等待提取与转换完成，弹出统计信息（构件数 / 三角形数 / 贴图数）

**输出结构**：

```
<项目名>_3dtiles/
├── tileset.json      # 3D Tiles 入口
├── Tile-0.b3dm       # 瓦片（默认 Draco 压缩）
├── textures/         # 外部贴图
└── export.log        # 导出日志（提取统计与转换器输出）
```

中间产物（glTF + meta.json）写在系统临时目录，导出后自动清理。

## meta.json 格式

```json
{
  "schemaVersion": 1,
  "units": "meters",
  "upAxis": "Z",
  "tree": [ { "id": "...", "name": "F1", "type": "Storey", "elements": ["<UniqueId>"] } ],
  "elements": {
    "<UniqueId>": {
      "elementId": 12579,
      "name": "自助票务处理机",
      "category": "专用设备",
      "type": "自助票务处理机-A",
      "storey": "1F",
      "parameters": { "系统": "AFC", "防火等级": "2h" }
    }
  }
}
```

`elements` 的键与 glTF 节点名、以及 3D Tiles 瓦片内的 featureId 一一对应。

## 已知限制

- **UV 为按面归一化**（每个面的贴图铺满一次），未使用贴图的真实世界缩放；需要精确平铺时需读取渲染外观的 RealWorldScale 参数
- 贴图提取依赖 Revit 渲染外观中的图片路径，若材质使用程序化纹理（渐变、噪波等）则无图片可提取
- 提取阶段为同步执行，超大模型（百万级构件）Revit 界面会暂时无响应

## License

MIT
