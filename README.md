# revitTo3DTiles

Revit 模型一键导出 **glTF 与 3D Tiles** 的 Revit 插件。两个按钮共用同一条提取管线（贴图、实例化去重、真实世界缩放 UV），glTF 直接落盘，3D Tiles 经 [modelTo3DTiles](https://github.com/lijuhong1981/modelTo3DTiles) 转换后输出，保留**贴图、BIM 属性集与楼层层级**，可在 Cesium / three.js 中加载并支持构件级拾取与按楼层过滤。

> 本工程由 [revitToGltf](https://github.com/lijuhong1981/revitToGltf)（v0.6.0 提取管线）与 [modelTo3DTiles](https://github.com/lijuhong1981/modelTo3DTiles)（v2.1.1 转换器）整合而来：**导出 glTF** 保留原 revitToGltf 全部功能，**导出 3D Tiles** 在其下游接一键转换。

## 架构

```
                ┌─ [导出 glTF]  ExportGltfCommand ──→ glTF/glb + 可选 .metadata（用户可见输出）
Revit 文档 ─→ 提取核心（共用）
                └─ [导出 3D Tiles] ExportTo3DTilesCommand ──→ 临时 glb（内嵌贴图）+ .metadata
                     ──→ ConverterLauncher（--md / --lla / --cc）──→ modelTo3DTiles.exe
                     ──→ tileset.json + Tile-*.b3dm + textures/
```

插件只负责 Revit 侧的提取；3D Tiles 转换由 modelTo3DTiles 以**独立进程**执行（大模型转换不占用 Revit 内存，崩溃不连累 Revit）。

| 模块 | 职责 |
|---|---|
| `Extraction/GeometryExtractor` | 三角化、嵌套族变换展开、**按尺寸签名实例化去重**（共享网格+实例矩阵）、面内顶点焊接、英尺→米、**UV 投影三层回退**（平面/旋转面/直纹面+全零兜底平面重建） |
| `Extraction/MaterialExtractor` | 材质颜色（渲染外观优先）与透明度 |
| `Extraction/TextureExtractor` | **从渲染外观提取贴图并按内容哈希外置去重**、真实世界缩放 UV、PNG/JPEG 可内嵌、**非 2 的幂贴图重采样到最近 2 的幂（上限 2048，Cesium 加载优化）** |
| `Extraction/MetadataCollector` | 类别、族、类型、标高、实例参数（写入 .metadata） |
| `Output/GltfWriter` | glTF 2.0 / glb 写出（流式 .bin 支持 2GB+，Y-up 根节点，节点名=构件名_元素ID，extras.uniqueId；可选 Draco 压缩） |
| `Native/DracoEncoder` | Draco 编码 P/Invoke 包装（KHR_draco_mesh_compression；量化参数取自 modelTo3DTiles 实践） |
| `Output/MetadataWriter` | .metadata（BIM 语义 sidecar，与 glTF 节点 extras.uniqueId 对齐） |
| `Commands/ExportSettingsForm` | 共享设置弹窗（范围/精度公共区；glTF 格式区 / 3D Tiles 地理定位区） |
| `Pipeline/ConverterLauncher` | 调用 modelTo3DTiles 并回显日志（参数契约见下） |
| `Pipeline/GeoLocation` | 项目地理位置读取（SiteLocation 弧度→度，海拔英尺→米） |

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

### 下载安装（推荐）

无需编译，从 [GitHub Releases](https://github.com/lijuhong1981/revitTo3DTiles/releases) 下载 `revitTo3DTiles-v1.2.0.zip`，解压得到：

- `RevitTo3DTiles.dll`
- `RevitTo3DTiles.addin`
- `DracoWrapper.dll`（glTF 的 Draco 几何压缩所需）
- `modelTo3DTiles.exe`（3D Tiles 导出所需，已含 v2.1.1 版）

将四个文件一起复制到：

```
C:\ProgramData\Autodesk\Revit\Addins\2020\
```

### 从源码部署

1. 生成解决方案（见[构建](#构建)），将 `bin\Release\RevitTo3DTiles.dll` 复制到：
   ```
   C:\ProgramData\Autodesk\Revit\Addins\2020\
   ```
2. 同目录放置 `RevitTo3DTiles.addin` 清单文件
3. **放置 Draco 原生库**（仅勾选「导出 glTF 的 Draco 几何压缩」时必需）：将 `native\DracoWrapper.dll` 与 DLL 同目录
4. **部署转换器**（仅 3D Tiles 导出需要）：将 `modelTo3DTiles.exe` 放在 DLL 同目录，或设置环境变量 `MODELTO3DTILES_PATH` 指向它
   - exe 在 modelTo3DTiles 仓库执行 `npm run build` 产出（约 210MB，node22-win-x64），或从其 GitHub Releases 下载

启动 Revit 后，功能区出现 **模型转换 → 模型导出** 面板，含两个按钮：**导出 glTF** 与 **导出 3D Tiles**。

> 调试期可用 Add-In Manager 直接加载 DLL，无需重启 Revit。

## 使用

### 导出 glTF

1. 点击 **导出 glTF**，弹窗内完成全部设置：
   - **导出范围**：全模型 / 当前视图可见（推荐）/ 仅选中构件
   - **DetailLevel**：Coarse / Medium / Fine，可叠加自定义三角化精度滑条（不勾选 = Revit 默认，三角形更少）
   - **格式**：.gltf（JSON + 外部 .bin）或 .glb（单文件二进制）
   - **贴图分离**：默认分离到 textures/；取消后 PNG/JPEG 内嵌进 .bin/.glb
   - **贴图标准化(尺寸2的幂归一化)**：非 2 的幂 PNG/JPEG 重采样到最近 2 的幂（上限 2048，默认开）——Cesium 对 REPEAT+mipmap 的非 2 的幂贴图会强制放大（画质差、显存最多 4 倍），转换期预处理可省显存提画质；失败自动回退原始贴图
   - **Draco 几何压缩**：默认不勾；勾选 = KHR_draco_mesh_compression，几何体积约降 80%，输出需查看器支持解码（Cesium 内置，three.js 需配 DRACOLoader）；需 `DracoWrapper.dll` 与插件同目录
   - **导出元数据**：生成同名 .metadata（项目信息 + 构件 BIM 属性）
2. 全部选择持久化，下次打开自动回填（`%APPDATA%\revitTo3DTiles\settings.json`）
3. 进度窗显示提取/写出进度，可随时取消

### 导出 3D Tiles

1. 点击 **导出 3D Tiles**，弹窗设置：
   - 范围/精度与 glTF 弹窗一致（设置互通）
   - **贴图标准化**：恒开（非 2 的幂贴图重采样到最近 2 的幂，2048 上限与转换器纹理图集对齐，Cesium 加载优化）
   - **写入 BIM 属性到瓦片**：构件拾取 / 按楼层与类别过滤（默认开）
   - **地理位置**：自动读取项目场地位置（管理 → 地理位置），可手改经纬度与海拔；未设置的模型可关闭，转换器落默认坐标
   - **旋转到正北**：自动读取项目正北角（项目北 → 正北偏角，默认开），把轴网带偏角的项目正确转向地球坐标；无偏转/读取失败时勾选框禁用
   - **单瓦片容量(MB)**：默认 10；调大瓦片更少更整，调小加载粒度更细（1~2048）
   - **转换优化**：Draco 几何压缩（默认开，体积更小）、纹理图集（默认开，合并贴图减 draw call）——排查瓦片异常/贴图错位时可分别关闭二分定位
   - **自动贴地**：模型底面贴地表（忽略海拔）
   - **输出路径 + 输出目录名**：最终输出 = `路径\目录名`；目录名默认沿用 glTF 输出文件名 + `_3dtiles`（如 `项目_fine_1.00_3dtiles`），随精度联动，可手改；输出路径独立记忆，不与 glTF 输出目录互串
2. 等待 提取 → 写出 → 转换 三阶段完成（转换阶段可取消，会终止转换进程）
3. 统计弹窗显示构件数 / 三角形数 / 实例化节省 / 输出体积

**输出结构**：

```
<输出目录>/
├── tileset.json      # 3D Tiles 入口（含地理定位 transform）
├── Tile-*.b3dm       # 瓦片（Draco 压缩 + 构件属性表 + 实例化）
├── textures/         # 外置贴图（内容哈希去重）
└── 3dtiles-export.log
```

中间产物（临时 glb + .metadata）写在系统临时目录，**导出成功后自动清理；失败/取消时保留**并在弹窗中给出路径，便于排查。

## 转换器对接契约（modelTo3DTiles v2.1.1）

- `--md <path>`：BIM 语义 sidecar（`.metadata`，Elements 数组，Key 与 glTF 节点 `extras.uniqueId` 对应）→ 瓦片内 `EXT_structural_metadata` 七列属性表（name/elementId/category/family/type/storey/parameters）
- `--lla "lng,lat,alt"`：度/米制地理定位 → 根节点 ENU→ECEF 变换；**必须双横线**（单横线 `-lla` 会被 yargs 当短旗标簇静默忽略）
- `-r "0,deg,0"`：正北旋转（度）。旋转施加在 glb 的 Y-up 场景空间，绕上轴即第二分量 Y；Revit 正北角符号右手系可直接沿用
- `--ts <mb>`：单瓦片容量（默认 10MB），控制瓦片数与加载粒度
- `-d/--no-d`：Draco 几何压缩（默认开）；`--ta/--no-ta`：纹理图集合并（默认开）
- `--cc`：锚点修正到包围盒中心；`--ctg/--no-ctg`：贴地；`-s material`：瓦片按材质装填（固定值，体积最小加载最快）
- glTF 输入按 Y-up 处理（GltfWriter 的 Y-up 根节点方案正好匹配）；共享网格+实例节点会被转换器自动坍缩为 `EXT_mesh_gpu_instancing`（≥4 个刚体实例）

## .metadata 格式

```json
{
  "Schema": "revitToGltf-metadata",
  "Version": 1,
  "CoordinateSystem": { "UpAxis": "Z", "Unit": "meters" },
  "Levels": [ { "Name": "1F", "Elevation": 0.0 } ],
  "Elements": [
    {
      "Key": "<Revit UniqueId，与 glTF 节点 extras.uniqueId 对应>",
      "Name": "自助票务处理机",
      "ElementId": 12579,
      "Category": "专用设备",
      "Family": "...",
      "type": "自助票务处理机-A",
      "Level": "1F",
      "Parameters": { "系统": "AFC", "防火等级": "2h" }
    }
  ]
}
```

## 已知限制

- 贴图提取依赖 Revit 渲染外观中的图片路径，程序化纹理（渐变、噪波等）无图片可提取
- 非 PNG/JPEG 贴图（tga/dds）无法内嵌 glb，自动回退为外置
- 提取阶段为同步执行（进度窗 + DoEvents 保持响应），超大模型（百万级构件）提取期间 Revit 界面仅部分响应
- 与 revitToGltf 旧插件共存时共用"模型转换"选项卡（各自按钮独立，设置文件互相独立）

## License

MIT
