using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Commands
{
    /// <summary>导出模式：glTF（用户可见的 glTF/glb 输出）或 3D Tiles（经转换器的一键输出）</summary>
    public enum ExportMode
    {
        Gltf,
        Tiles3D
    }

    /// <summary>
    /// 弹窗产出的导出设置：公共项（范围/目录/文件名/精度）两模式共享，
    /// glTF 专属与 3D Tiles 专属字段只在对应模式下被赋值。
    /// </summary>
    public class ExportSettings
    {
        /// <summary>范围元素集（null=全模型）</summary>
        public ICollection<ElementId> ScopeIds;

        /// <summary>范围名（日志与 .metadata 记录用）</summary>
        public string ScopeName;

        public string OutputDirectory;
        public string FileName;
        public ViewDetailLevel DetailLevel;
        public double? TriangulateLod;

        // —— glTF 模式专属 ——
        public bool Binary;
        public bool ExportMetadata;
        public bool SeparateTextures = true;
        public bool NormalizeTextures = true;
        public bool DracoCompression;

        // —— 3D Tiles 模式专属 ——
        public bool TilesExportMetadata = true;
        public GeoLocation GeoLocation;
        public bool TilesClampToGround;

        /// <summary>单瓦片容量 MB（--ts），弹窗默认 10</summary>
        public double TilesTileSizeMb = 10;

        /// <summary>正北旋转角（度，绕上轴右手为正）；null = 不旋转（不传 -r）</summary>
        public double? TilesNorthRotation;

        /// <summary>Draco 几何压缩（-d），默认开；排查瓦片异常时可关</summary>
        public bool TilesDracoCompression = true;

        /// <summary>纹理图集合并（--ta），默认开；排查贴图错位时可关</summary>
        public bool TilesTextureAtlas = true;
    }

    /// <summary>
    /// 导出设置弹窗（代码构建的 WinForms，无设计器文件）。
    /// 从 revitToGltf 的 ExportGltfCommand 内联弹窗抽出为共享组件：
    /// 范围三选一、DetailLevel 三档、三角化精度滑条、输出目录与文件名联动，
    /// glTF 模式含格式（.gltf/.glb）/贴图分离/元数据开关；3D Tiles 模式含地理定位与转换选项。
    /// 全部选择持久化到 AppSettings（按域分字段保存，两弹窗互不覆盖）。
    /// </summary>
    internal static class ExportSettingsForm
    {
        /// <summary>显示设置弹窗；用户取消返回 null</summary>
        public static ExportSettings Show(UIDocument uiDocument, Document doc, ExportMode mode)
        {
            if (mode == ExportMode.Gltf)
                return ShowGltf(uiDocument, doc);
            return ShowTiles3D(uiDocument, doc);
        }

        // ==========================================================================================
        // glTF 模式：布局与行为自 revitToGltf v0.5.0 等价迁移
        // ==========================================================================================

        private static ExportSettings ShowGltf(UIDocument uiDocument, Document doc)
        {
            string projectName = ProjectNameOf(doc);

            AppSettings.Settings saved = AppSettings.Load();
            string chosenDir = (!string.IsNullOrWhiteSpace(saved.LastOutputDirectory) && Directory.Exists(saved.LastOutputDirectory))
                ? saved.LastOutputDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), projectName + "_gltf");

            int scopeChoice = saved.Scope; // 0=全模型 1=视图可见 2=选中构件
            if (scopeChoice < 0 || scopeChoice > 2) scopeChoice = 1;
            ViewDetailLevel chosenDetail = ParseDetail(saved.DetailLevel);
            double? chosenTri = saved.UseCustomTriangulate
                ? (double?)Math.Min(1.0, Math.Max(0.0, saved.TriangulateLod))
                : null;    // null=使用 Revit 默认三角化（不勾选）
            string chosenFile = null;
            bool chosenBinary = saved.Binary;    // false=.gltf true=.glb
            bool chosenMeta = saved.ExportMetadata;       // 是否导出元数据 .metadata
            bool chosenSeparateTex = saved.SeparateTextures; // 贴图是否分离到 textures/（默认勾选）
            bool chosenNormalizeTex = saved.NormalizeTextures; // 贴图重采样到最近 2 的幂（默认勾选）
            bool chosenDraco = saved.DracoEnabled;   // Draco 几何压缩（默认不勾：输出需查看器支持解码）
            bool nameEdited = false;      // 用户手工改过文件名后，滑动条不再自动改写
            bool suppressNameSync = false; // 程序化赋值时抑制 TextChanged 的"已编辑"标记

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "导出 glTF 设置";
                form.ClientSize = new System.Drawing.Size(1100, 744);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;

                // ---- 导出范围（水平一行）。
                //      注意：RadioButton.AutoSize 必须显式设为 false 并同时给出 Width 与 Height ——
                //      宽度需按最长的一项（"仅选中构件（最快）"，10 字）留足，否则会折行而第二行装不下。 ----
                const int RadioHeight = 48;
                var scopeGroup = new GroupBox { Text = "导出范围", Left = 12, Top = 12, Width = 1076, Height = 118 };
                var radioFull = new RadioButton
                {
                    Text = "全模型（最慢）", Left = 25, Top = 58, Width = 280, Height = RadioHeight, AutoSize = false,
                    Checked = (scopeChoice == 0),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioView = new RadioButton
                {
                    Text = "当前视图可见（推荐）", Left = 325, Top = 58, Width = 340, Height = RadioHeight, AutoSize = false, Checked = (scopeChoice == 1),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioSel = new RadioButton
                {
                    Text = "仅选中构件（最快）", Left = 690, Top = 58, Width = 380, Height = RadioHeight, AutoSize = false, Checked = (scopeChoice == 2),
                    BackColor = System.Drawing.Color.Transparent
                };
                scopeGroup.Controls.Add(radioFull);
                scopeGroup.Controls.Add(radioView);
                scopeGroup.Controls.Add(radioSel);

                // ---- 文件名（不含扩展名）。默认 <项目名>_<detail>_<tri>，随滑动条同步；
                //      一旦用户手工改动过，就不再自动改写。 ----
                Func<string> triSlug = () => chosenTri.HasValue
                    ? chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture) : "default";
                Func<string> defaultName = () => projectName + "_" + DetailSlug(chosenDetail) + "_" + triSlug();
                var nameLabel = new Label { Text = "文件名:", Left = 12, Top = 604, Width = 160 };
                var nameBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 632,
                    Width = 920,
                    Height = 32,
                    Text = defaultName()
                };
                nameBox.TextChanged += (s, e) =>
                {
                    if (!suppressNameSync) nameEdited = true;
                };
                Action syncName = () =>
                {
                    if (nameEdited) return;
                    suppressNameSync = true;
                    nameBox.Text = defaultName();
                    suppressNameSync = false;
                };

                // ---- DetailLevel：只有 3 档，用单选比滑条更直观，且高度更矮（滑条含刻度约 84px） ----
                var detailLabel = new Label { Text = "DetailLevel（视图详细程度）:", Left = 12, Top = 142, Width = 600 };
                var radioCoarse = new RadioButton
                {
                    Text = "Coarse（最粗）", Left = 12, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Coarse),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioMedium = new RadioButton
                {
                    Text = "Medium（中等）", Left = 262, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Medium),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioFine = new RadioButton
                {
                    Text = "Fine（最细）", Left = 512, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false, Checked = (chosenDetail == ViewDetailLevel.Fine),
                    BackColor = System.Drawing.Color.Transparent
                };
                radioCoarse.CheckedChanged += (s, e) => { if (radioCoarse.Checked) { chosenDetail = ViewDetailLevel.Coarse; syncName(); } };
                radioMedium.CheckedChanged += (s, e) => { if (radioMedium.Checked) { chosenDetail = ViewDetailLevel.Medium; syncName(); } };
                radioFine.CheckedChanged += (s, e) => { if (radioFine.Checked) { chosenDetail = ViewDetailLevel.Fine; syncName(); } };

                // 注意：TrackBar 在高 DPI 下实际高度约 64px（TickStyle.None），下一行需按 ~70px 预留。
                // 勾选 = 用自定义精度(0~1)；不勾选 = 用 Revit 默认三角化（三角形更少、体积更小）。
                var useTriCheck = new CheckBox
                {
                    Text = "使用自定义三角化精度（不勾选 = Revit 默认，三角形更少、体积更小）",
                    Left = 12,
                    Top = 248,
                    Width = 880,
                    AutoSize = true,
                    Checked = chosenTri.HasValue
                };
                var triTrack = new TrackBar
                {
                    Left = 12,
                    Top = 276,
                    Width = 880,
                    Minimum = 0,
                    Maximum = 100,
                    TickFrequency = 10,
                    SmallChange = 1,
                    LargeChange = 10,
                    Value = chosenTri.HasValue ? (int)Math.Round(chosenTri.Value * 100) : 100,
                    TickStyle = TickStyle.None,
                    Enabled = chosenTri.HasValue
                };
                var triValue = new Label { Text = TriText(chosenTri), Left = 908, Top = 286, Width = 180 };
                useTriCheck.CheckedChanged += (s, e) =>
                {
                    triTrack.Enabled = useTriCheck.Checked;
                    if (useTriCheck.Checked)
                    {
                        chosenTri = triTrack.Value / 100.0;
                        triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        chosenTri = null;
                        triValue.Text = "default";
                    }
                    syncName();
                };
                triTrack.ValueChanged += (s, e) =>
                {
                    chosenTri = triTrack.Value / 100.0;
                    triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    syncName();
                };

                // ---- 输出目录 ----
                var dirLabel = new Label { Text = "输出目录:", Left = 12, Top = 530, Width = 160 };
                var dirBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 558,
                    Width = 920,
                    Height = 32,
                    Text = chosenDir,
                    ReadOnly = true
                };
                var browseButton = new Button { Text = "浏览...", Left = 948, Top = 556, Width = 140, Height = 38 };
                browseButton.Click += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dlg.Description = "选择 glTF 输出目录";
                        dlg.SelectedPath = string.IsNullOrEmpty(dirBox.Text) ? chosenDir : dirBox.Text;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            dirBox.Text = dlg.SelectedPath;
                    }
                };

                // ---- 输出格式：.gltf（JSON + 外部 .bin + 贴图）或 .glb（单文件二进制） ----
                var fmtLabel = new Label { Text = "格式:", Left = 12, Top = 346, Width = 60 };
                var radioGltf = new RadioButton
                {
                    Text = ".gltf（JSON + 外部.bin）",
                    AutoSize = true,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = !chosenBinary
                };
                var radioGlb = new RadioButton
                {
                    Text = ".glb（单文件二进制）",
                    AutoSize = true,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = chosenBinary
                };
                radioGltf.CheckedChanged += (s, e) => { if (radioGltf.Checked) chosenBinary = false; };
                radioGlb.CheckedChanged += (s, e) => { if (radioGlb.Checked) chosenBinary = true; };
                // FlowLayoutPanel 按文字实际宽度排布单选，避免高 DPI 下固定宽度截断描述文字
                var fmtPanel = new System.Windows.Forms.FlowLayoutPanel
                {
                    Left = 80,
                    Top = 340,
                    Width = 1000,
                    Height = 34,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                fmtPanel.Controls.Add(radioGltf);
                fmtPanel.Controls.Add(radioGlb);

                // 贴图分离：独立一行，放在格式选择下方、导出元数据上方
                var separateTexCheck = new CheckBox
                {
                    Text = "贴图分离（不勾选 = 内嵌进 .bin/.glb）",
                    Left = 12,
                    Top = 388,
                    AutoSize = true,
                    Checked = chosenSeparateTex,
                    BackColor = System.Drawing.Color.Transparent
                };
                separateTexCheck.CheckedChanged += (s, e) => { chosenSeparateTex = separateTexCheck.Checked; };

                // 贴图 2 的幂归一化：独立一行（贴图分离下方），非 2 的幂 PNG/JPEG 重采样到最近 2 的幂
                var normalizeTexCheck = new CheckBox
                {
                    Text = "贴图标准化(尺寸2的幂归一化)",
                    Left = 12,
                    Top = 424,
                    AutoSize = true,
                    Checked = chosenNormalizeTex,
                    BackColor = System.Drawing.Color.Transparent
                };
                normalizeTexCheck.CheckedChanged += (s, e) => { chosenNormalizeTex = normalizeTexCheck.Checked; };

                // Draco 几何压缩：独立一行，输出标记 extensionsRequired，需查看器支持解码（Cesium 内置）
                var dracoCheck = new CheckBox
                {
                    Text = "Draco 几何压缩（体积 -80% 左右，需查看器支持解码）",
                    Left = 12,
                    Top = 460,
                    AutoSize = true,
                    Checked = chosenDraco,
                    BackColor = System.Drawing.Color.Transparent
                };
                dracoCheck.CheckedChanged += (s, e) => { chosenDraco = dracoCheck.Checked; };

                // 导出元数据：独立一行（不放同一 FlowLayoutPanel，避免与格式单选同行）
                var exportMetaCheck = new CheckBox
                {
                    Text = "导出元数据（同名 .metadata）",
                    Left = 12,
                    Top = 496,
                    AutoSize = true,
                    Checked = chosenMeta,
                    BackColor = System.Drawing.Color.Transparent
                };
                exportMetaCheck.CheckedChanged += (s, e) => { chosenMeta = exportMetaCheck.Checked; };

                var okButton = new Button { Text = "导出", Left = 828, Top = 676, Width = 120, Height = 42 };
                var cancelButton = new Button
                {
                    Text = "取消",
                    Left = 960,
                    Top = 676,
                    Width = 120,
                    Height = 42,
                    DialogResult = DialogResult.Cancel
                };
                okButton.Click += (s, e) =>
                {
                    scopeChoice = radioSel.Checked ? 2 : (radioFull.Checked ? 0 : 1);

                    if (scopeChoice == 2)
                    {
                        ICollection<ElementId> selIds = uiDocument.Selection.GetElementIds();
                        if (selIds.Count == 0)
                        {
                            MessageBox.Show(form, "请先选中构件，再选择「仅选中构件」。", "范围为空",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(dirBox.Text))
                    {
                        MessageBox.Show(form, "请选择输出目录。", "目录为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string candidate = nameBox.Text.Trim();
                    if (string.IsNullOrEmpty(candidate))
                    {
                        MessageBox.Show(form, "请输入输出文件名。", "文件名为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        MessageBox.Show(form, "文件名含有非法字符（不能包含 \\ / : * ? \" < > |）。", "文件名非法",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    chosenDir = dirBox.Text;
                    chosenFile = candidate;
                    // 按域保存：公共项 + glTF 专属项；3D Tiles 域字段原样保留
                    AppSettings.Settings toSave = AppSettings.Load();
                    toSave.LastOutputDirectory = chosenDir;
                    toSave.Scope = scopeChoice;
                    toSave.DetailLevel = DetailSlug(chosenDetail);
                    toSave.UseCustomTriangulate = chosenTri.HasValue;
                    toSave.TriangulateLod = chosenTri ?? 1.0;
                    toSave.Binary = chosenBinary;
                    toSave.ExportMetadata = chosenMeta;
                    toSave.SeparateTextures = chosenSeparateTex;
                    toSave.NormalizeTextures = chosenNormalizeTex;
                    toSave.DracoEnabled = chosenDraco;
                    AppSettings.Save(toSave);
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

                form.Controls.Add(scopeGroup);
                form.Controls.Add(nameLabel);
                form.Controls.Add(nameBox);
                form.Controls.Add(dirLabel);
                form.Controls.Add(dirBox);
                form.Controls.Add(browseButton);
                form.Controls.Add(fmtLabel);
                form.Controls.Add(fmtPanel);
                form.Controls.Add(exportMetaCheck);
                form.Controls.Add(separateTexCheck);
                form.Controls.Add(normalizeTexCheck);
                form.Controls.Add(dracoCheck);
                form.Controls.Add(detailLabel);
                form.Controls.Add(radioCoarse);
                form.Controls.Add(radioMedium);
                form.Controls.Add(radioFine);
                form.Controls.Add(useTriCheck);
                form.Controls.Add(triTrack);
                form.Controls.Add(triValue);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog() != DialogResult.OK)
                    return null;
            }

            // 计算最终范围
            var result = new ExportSettings
            {
                OutputDirectory = chosenDir,
                FileName = chosenFile,
                DetailLevel = chosenDetail,
                TriangulateLod = chosenTri,
                Binary = chosenBinary,
                ExportMetadata = chosenMeta,
                SeparateTextures = chosenSeparateTex,
                NormalizeTextures = chosenNormalizeTex,
                DracoCompression = chosenDraco
            };
            if (!ResolveScope(uiDocument, doc, scopeChoice, result))
                return null;
            return result;
        }

        // ==========================================================================================
        // 3D Tiles 模式：公共区（范围/精度）与 glTF 相同，专属区为地理定位与转换选项；
        // 输出 = 输出路径\输出目录名（目录名默认沿用 glTF 文件名 + "_3dtiles"，随精度联动）。
        // ==========================================================================================

        private static ExportSettings ShowTiles3D(UIDocument uiDocument, Document doc)
        {
            string projectName = ProjectNameOf(doc);

            AppSettings.Settings saved = AppSettings.Load();
            string chosenBaseDir = (!string.IsNullOrWhiteSpace(saved.TilesLastOutputDirectory) && Directory.Exists(saved.TilesLastOutputDirectory))
                ? saved.TilesLastOutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string chosenFolderName = null;
            bool nameEdited = false;      // 用户手工改过目录名后，滑动条不再自动改写
            bool suppressNameSync = false; // 程序化赋值时抑制 TextChanged 的"已编辑"标记

            int scopeChoice = saved.Scope;
            if (scopeChoice < 0 || scopeChoice > 2) scopeChoice = 1;
            ViewDetailLevel chosenDetail = ParseDetail(saved.DetailLevel);
            double? chosenTri = saved.UseCustomTriangulate
                ? (double?)Math.Min(1.0, Math.Max(0.0, saved.TriangulateLod))
                : null;

            // 地理位置初值：项目 SiteLocation 自动读取；无有效坐标时默认不勾选（可手动勾选输入）
            GeoLocation projectGeo = GeoLocation.FromDocument(doc);
            bool chosenUseGeo = saved.TilesUseGeolocation && projectGeo.HasValue;
            string lngText = projectGeo.HasValue ? projectGeo.Longitude.ToString("0.######", CultureInfo.InvariantCulture) : "0";
            string latText = projectGeo.HasValue ? projectGeo.Latitude.ToString("0.######", CultureInfo.InvariantCulture) : "0";
            string altText = projectGeo.HasValue ? projectGeo.Altitude.ToString("0.##", CultureInfo.InvariantCulture) : "0";
            if (saved.TilesUseGeolocation && !double.IsNaN(saved.TilesLongitude) && saved.TilesLongitude != 0)
            {
                // 上次手改过的覆盖值优先回填
                lngText = saved.TilesLongitude.ToString("0.######", CultureInfo.InvariantCulture);
                latText = saved.TilesLatitude.ToString("0.######", CultureInfo.InvariantCulture);
                altText = saved.TilesAltitude.ToString("0.##", CultureInfo.InvariantCulture);
            }
            bool chosenMeta = saved.TilesExportMetadata;
            bool chosenClamp = saved.TilesClampToGround;
            bool chosenDraco = saved.TilesDracoCompression;       // Draco 压缩（默认开，排查异常可关）
            bool chosenAtlas = saved.TilesTextureAtlas;           // 纹理图集（默认开，排查贴图可关）

            // 正北角：项目北 → 正北偏角自动读取；无偏转或读取失败时勾选框禁用（勾了也不会旋转）
            double? projectNorth = GeoLocation.ReadTrueNorthAngle(doc);
            bool northAvailable = projectNorth.HasValue && Math.Abs(projectNorth.Value) > 0.01;
            bool chosenNorth = saved.TilesRotateToNorth && northAvailable;
            double chosenTileSizeMb = saved.TilesTileSizeMb > 0 ? saved.TilesTileSizeMb : 10;

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "导出 3D Tiles 设置";
                form.ClientSize = new System.Drawing.Size(1100, 906);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;

                const int RadioHeight = 48;

                // ---- 导出范围（与 glTF 弹窗一致） ----
                var scopeGroup = new GroupBox { Text = "导出范围", Left = 12, Top = 12, Width = 1076, Height = 118 };
                var radioFull = new RadioButton
                {
                    Text = "全模型（最慢）", Left = 25, Top = 58, Width = 280, Height = RadioHeight, AutoSize = false,
                    Checked = (scopeChoice == 0),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioView = new RadioButton
                {
                    Text = "当前视图可见（推荐）", Left = 325, Top = 58, Width = 340, Height = RadioHeight, AutoSize = false, Checked = (scopeChoice == 1),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioSel = new RadioButton
                {
                    Text = "仅选中构件（最快）", Left = 690, Top = 58, Width = 380, Height = RadioHeight, AutoSize = false, Checked = (scopeChoice == 2),
                    BackColor = System.Drawing.Color.Transparent
                };
                scopeGroup.Controls.Add(radioFull);
                scopeGroup.Controls.Add(radioView);
                scopeGroup.Controls.Add(radioSel);

                // ---- 输出目录名（最终目录 = 输出路径\目录名，视觉排在下方）。默认沿用 glTF
                //      输出文件名 + "_3dtiles"，随精度联动；一旦用户手工改动过，就不再自动改写。 ----
                Func<string> triSlug = () => chosenTri.HasValue
                    ? chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture) : "default";
                Func<string> defaultFolderName = () => projectName + "_" + DetailSlug(chosenDetail) + "_" + triSlug() + "_3dtiles";
                var folderNameLabel = new Label { Text = "输出目录名:", Left = 12, Top = 772, Width = 160, Height = 30 };
                var folderNameBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 800,
                    Width = 920,
                    Height = 32,
                    Text = defaultFolderName()
                };
                folderNameBox.TextChanged += (s, e) =>
                {
                    if (!suppressNameSync) nameEdited = true;
                };
                Action syncName = () =>
                {
                    if (nameEdited) return;
                    suppressNameSync = true;
                    folderNameBox.Text = defaultFolderName();
                    suppressNameSync = false;
                };

                // ---- DetailLevel（与 glTF 弹窗一致） ----
                var detailLabel = new Label { Text = "DetailLevel（视图详细程度）:", Left = 12, Top = 142, Width = 600 };
                var radioCoarse = new RadioButton
                {
                    Text = "Coarse（最粗）", Left = 12, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Coarse),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioMedium = new RadioButton
                {
                    Text = "Medium（中等）", Left = 262, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Medium),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioFine = new RadioButton
                {
                    Text = "Fine（最细）", Left = 512, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false, Checked = (chosenDetail == ViewDetailLevel.Fine),
                    BackColor = System.Drawing.Color.Transparent
                };
                radioCoarse.CheckedChanged += (s, e) => { if (radioCoarse.Checked) { chosenDetail = ViewDetailLevel.Coarse; syncName(); } };
                radioMedium.CheckedChanged += (s, e) => { if (radioMedium.Checked) { chosenDetail = ViewDetailLevel.Medium; syncName(); } };
                radioFine.CheckedChanged += (s, e) => { if (radioFine.Checked) { chosenDetail = ViewDetailLevel.Fine; syncName(); } };

                // ---- 三角化精度（与 glTF 弹窗一致） ----
                var useTriCheck = new CheckBox
                {
                    Text = "使用自定义三角化精度（不勾选 = Revit 默认，三角形更少、体积更小）",
                    Left = 12, Top = 248, Width = 880, AutoSize = true, Checked = chosenTri.HasValue
                };
                var triTrack = new TrackBar
                {
                    Left = 12, Top = 276, Width = 880, Minimum = 0, Maximum = 100,
                    TickFrequency = 10, SmallChange = 1, LargeChange = 10,
                    Value = chosenTri.HasValue ? (int)Math.Round(chosenTri.Value * 100) : 100,
                    TickStyle = TickStyle.None, Enabled = chosenTri.HasValue
                };
                var triValue = new Label { Text = TriText(chosenTri), Left = 908, Top = 286, Width = 180 };
                useTriCheck.CheckedChanged += (s, e) =>
                {
                    triTrack.Enabled = useTriCheck.Checked;
                    if (useTriCheck.Checked)
                    {
                        chosenTri = triTrack.Value / 100.0;
                        triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        chosenTri = null;
                        triValue.Text = "default";
                    }
                    syncName();
                };
                triTrack.ValueChanged += (s, e) =>
                {
                    chosenTri = triTrack.Value / 100.0;
                    triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    syncName();
                };

                // ---- 3D Tiles 专属：BIM 属性（高DPI下TrackBar实际高约80px，需留足间距） ----
                var metaCheck = new CheckBox
                {
                    Text = "写入 BIM 属性到瓦片（构件拾取 / 按楼层与类别过滤）",
                    Left = 12, Top = 376, Width = 880, AutoSize = true,
                    Checked = chosenMeta,
                    BackColor = System.Drawing.Color.Transparent
                };

                // ---- 3D Tiles 专属：地理位置（勾选框兼作组标题，勾上即启用经纬度输入） ----
                var geoGroup = new GroupBox { Text = "", Left = 12, Top = 426, Width = 1076, Height = 68 };
                // 标题勾选框放在分组框边框线上（表单子控件），实色背景遮住边框线形成"带勾选的组标题"
                var useGeoCheck = new CheckBox
                {
                    Text = projectGeo.HasValue
                        ? "地理位置（模型在地球上的放置点）"
                        : "地理位置（项目未设置坐标，勾选后手动输入）",
                    Left = 24, Top = 416, AutoSize = true,
                    Checked = chosenUseGeo,
                    BackColor = System.Drawing.SystemColors.Control
                };
                var lngLabel = new Label { Text = "经度(°):", Left = 20, Top = 28, Width = 80 };
                var lngBox = new System.Windows.Forms.TextBox
                {
                    Left = 104, Top = 22, Width = 170, Text = lngText, Enabled = chosenUseGeo
                };
                var latLabel = new Label { Text = "纬度(°):", Left = 300, Top = 28, Width = 80 };
                var latBox = new System.Windows.Forms.TextBox
                {
                    Left = 384, Top = 22, Width = 170, Text = latText, Enabled = chosenUseGeo
                };
                var altLabel = new Label { Text = "海拔(米):", Left = 580, Top = 28, Width = 90 };
                var altBox = new System.Windows.Forms.TextBox
                {
                    Left = 674, Top = 22, Width = 130, Text = altText, Enabled = chosenUseGeo
                };
                useGeoCheck.CheckedChanged += (s, e) =>
                {
                    lngBox.Enabled = latBox.Enabled = altBox.Enabled = useGeoCheck.Checked;
                };
                geoGroup.Controls.Add(lngLabel);
                geoGroup.Controls.Add(lngBox);
                geoGroup.Controls.Add(latLabel);
                geoGroup.Controls.Add(latBox);
                geoGroup.Controls.Add(altLabel);
                geoGroup.Controls.Add(altBox);

                // ---- 3D Tiles 专属：旋转到正北（项目正北角自动读取，文字反映读取状态） ----
                string northText;
                if (!projectNorth.HasValue)
                    northText = "旋转到正北（未读到项目正北角）";
                else if (!northAvailable)
                    northText = "旋转到正北（项目无偏转）";
                else
                    northText = string.Format(CultureInfo.InvariantCulture,
                        "旋转到正北（项目正北角 {0:0.##}°，自动读取）", projectNorth.Value);
                var northCheck = new CheckBox
                {
                    Text = northText,
                    Left = 12, Top = 502, AutoSize = true,
                    Checked = chosenNorth,
                    Enabled = northAvailable,
                    BackColor = System.Drawing.Color.Transparent
                };
                northCheck.CheckedChanged += (s, e) => { chosenNorth = northCheck.Checked; };

                // ---- 3D Tiles 专属：单瓦片容量（--ts），留空 = 默认 10。
                //      Box/Label 显式高，行高不随 DPI 漂移（AutoSize 控件在高 DPI 下会超出固定行距） ----
                var tileSizeLabel = new Label { Text = "单瓦片容量(MB):", Left = 12, Top = 552, Width = 132, Height = 30 };
                var tileSizeBox = new System.Windows.Forms.TextBox
                {
                    Left = 152, Top = 546, Width = 80, Height = 32,
                    Text = chosenTileSizeMb.ToString("0.##", CultureInfo.InvariantCulture)
                };
                var tileSizeHint = new Label
                {
                    Text = "默认 10；调大 = 瓦片更少更整，调小 = 加载粒度更细",
                    Left = 250, Top = 552, Width = 760, Height = 30
                };

                // ---- 3D Tiles 专属：转换优化（Draco 压缩 / 纹理图集并排一行，均为诊断用开关）。
                //      勾选框 AutoSize=false + 显式宽高（同弹窗单选按钮的做法）：
                //      高 DPI 下 AutoSize 行高会超出固定行距把文字顶进上一行，锁定高度后不再漂移 ----
                var convertLabel = new Label { Text = "转换优化:", Left = 12, Top = 612, Width = 120, Height = 30 };
                var dracoCheck = new CheckBox
                {
                    Text = "Draco 几何压缩（体积更小）",
                    AutoSize = false, Width = 360, Height = 32,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = chosenDraco
                };
                dracoCheck.CheckedChanged += (s, e) => { chosenDraco = dracoCheck.Checked; };
                var atlasCheck = new CheckBox
                {
                    Text = "纹理图集（合并贴图，减少 draw call）",
                    AutoSize = false, Width = 520, Height = 32,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = chosenAtlas
                };
                atlasCheck.CheckedChanged += (s, e) => { chosenAtlas = atlasCheck.Checked; };
                // FlowLayoutPanel 按文字实际宽度排布，避免高 DPI 下固定宽度截断（同 glTF 格式行）
                var convertPanel = new System.Windows.Forms.FlowLayoutPanel
                {
                    Left = 140,
                    Top = 606,
                    Width = 940,
                    Height = 40,
                    FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                    WrapContents = false
                };
                convertPanel.Controls.Add(dracoCheck);
                convertPanel.Controls.Add(atlasCheck);

                // ---- 3D Tiles 专属：贴地 ----
                var clampCheck = new CheckBox
                {
                    Text = "自动贴地（忽略海拔，模型底面落在地表）",
                    Left = 12, Top = 656, Width = 880, AutoSize = true,
                    Checked = chosenClamp,
                    BackColor = System.Drawing.Color.Transparent
                };

                // ---- 输出路径（基础目录，最终输出目录 = 路径\目录名） ----
                var dirLabel = new Label { Text = "输出路径:", Left = 12, Top = 700, Width = 160, Height = 30 };
                var dirBox = new System.Windows.Forms.TextBox
                {
                    Left = 12, Top = 728, Width = 920, Height = 32, Text = chosenBaseDir, ReadOnly = true
                };
                var browseButton = new Button { Text = "浏览...", Left = 948, Top = 726, Width = 140, Height = 38 };
                browseButton.Click += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dlg.Description = "选择 3D Tiles 输出路径";
                        dlg.SelectedPath = string.IsNullOrEmpty(dirBox.Text) ? chosenBaseDir : dirBox.Text;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            dirBox.Text = dlg.SelectedPath;
                    }
                };

                var okButton = new Button { Text = "导出", Left = 828, Top = 852, Width = 120, Height = 42 };
                var cancelButton = new Button
                {
                    Text = "取消", Left = 960, Top = 852, Width = 120, Height = 42,
                    DialogResult = DialogResult.Cancel
                };
                GeoLocation chosenGeo = null;
                double parsedTileSizeMb = chosenTileSizeMb;
                double? chosenNorthRotation = null;
                okButton.Click += (s, e) =>
                {
                    scopeChoice = radioSel.Checked ? 2 : (radioFull.Checked ? 0 : 1);

                    if (scopeChoice == 2)
                    {
                        ICollection<ElementId> selIds = uiDocument.Selection.GetElementIds();
                        if (selIds.Count == 0)
                        {
                            MessageBox.Show(form, "请先选中构件，再选择「仅选中构件」。", "范围为空",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(dirBox.Text))
                    {
                        MessageBox.Show(form, "请选择输出路径。", "路径为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string folderCandidate = folderNameBox.Text.Trim();
                    if (string.IsNullOrEmpty(folderCandidate))
                    {
                        MessageBox.Show(form, "请输入输出目录名。", "目录名为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (folderCandidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        MessageBox.Show(form, "输出目录名含有非法字符（不能包含 \\ / : * ? \" < > |）。", "目录名非法",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (useGeoCheck.Checked)
                    {
                        double lng, lat, alt;
                        if (!double.TryParse(lngBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lng)
                            || !double.TryParse(latBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                            || !double.TryParse(altBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out alt)
                            || lng < -180 || lng > 180 || lat < -90 || lat > 90)
                        {
                            MessageBox.Show(form, "地理位置无效：经度 ∈ [-180,180]、纬度 ∈ [-90,90]、海拔为数字（小数点用 . ）。",
                                "地理位置非法", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        chosenGeo = new GeoLocation { Longitude = lng, Latitude = lat, Altitude = alt, HasValue = true };
                    }
                    else
                    {
                        chosenGeo = null;
                    }

                    // 单瓦片容量：留空 = 默认 10；数字需在 1~2048 之间
                    string tsText = tileSizeBox.Text.Trim();
                    if (string.IsNullOrEmpty(tsText))
                        parsedTileSizeMb = 10;
                    else if (!double.TryParse(tsText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedTileSizeMb)
                        || parsedTileSizeMb < 1 || parsedTileSizeMb > 2048)
                    {
                        MessageBox.Show(form, "单瓦片容量需为 1~2048 之间的数字（MB，小数点用 . ），留空使用默认 10。",
                            "瓦片容量非法", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // 正北旋转：仅当勾选框可用且勾选、且角度确实读到时传 -r
                    if (northCheck.Enabled && northCheck.Checked && projectNorth.HasValue)
                        chosenNorthRotation = projectNorth.Value;

                    chosenFolderName = folderCandidate;
                    chosenBaseDir = dirBox.Text.Trim();
                    // 按域保存：公共项 + 3D Tiles 专属项；glTF 域字段原样保留
                    //（输出路径只持久化基础目录，目录名每次按默认名重新生成，与 glTF 文件名一致不落盘）
                    AppSettings.Settings toSave = AppSettings.Load();
                    toSave.TilesLastOutputDirectory = chosenBaseDir;
                    toSave.Scope = scopeChoice;
                    toSave.DetailLevel = DetailSlug(chosenDetail);
                    toSave.UseCustomTriangulate = chosenTri.HasValue;
                    toSave.TriangulateLod = chosenTri ?? 1.0;
                    toSave.TilesExportMetadata = metaCheck.Checked;
                    toSave.TilesUseGeolocation = useGeoCheck.Checked;
                    toSave.TilesClampToGround = clampCheck.Checked;
                    toSave.TilesTileSizeMb = parsedTileSizeMb;
                    toSave.TilesDracoCompression = dracoCheck.Checked;
                    toSave.TilesTextureAtlas = atlasCheck.Checked;
                    // 正北勾选框不可用（无偏转/读取失败）时保留原偏好，不把 false 写死
                    if (northCheck.Enabled)
                        toSave.TilesRotateToNorth = northCheck.Checked;
                    if (chosenGeo != null)
                    {
                        toSave.TilesLongitude = chosenGeo.Longitude;
                        toSave.TilesLatitude = chosenGeo.Latitude;
                        toSave.TilesAltitude = chosenGeo.Altitude;
                    }
                    AppSettings.Save(toSave);
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

                form.Controls.Add(scopeGroup);
                form.Controls.Add(detailLabel);
                form.Controls.Add(radioCoarse);
                form.Controls.Add(radioMedium);
                form.Controls.Add(radioFine);
                form.Controls.Add(useTriCheck);
                form.Controls.Add(triTrack);
                form.Controls.Add(triValue);
                form.Controls.Add(metaCheck);
                form.Controls.Add(useGeoCheck);   // 标题勾选框（表单子控件，压在分组框边框线上）
                form.Controls.Add(geoGroup);
                form.Controls.Add(northCheck);
                form.Controls.Add(tileSizeLabel);
                form.Controls.Add(tileSizeBox);
                form.Controls.Add(tileSizeHint);
                form.Controls.Add(convertLabel);
                form.Controls.Add(convertPanel);
                form.Controls.Add(clampCheck);
                form.Controls.Add(dirLabel);
                form.Controls.Add(dirBox);
                form.Controls.Add(browseButton);
                form.Controls.Add(folderNameLabel);
                form.Controls.Add(folderNameBox);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;
                useGeoCheck.BringToFront();       // 确保勾选框渲染在分组框边框之上

                if (form.ShowDialog() != DialogResult.OK)
                    return null;

                var result = new ExportSettings
                {
                    OutputDirectory = System.IO.Path.Combine(chosenBaseDir, chosenFolderName),
                    DetailLevel = chosenDetail,
                    TriangulateLod = chosenTri,
                    TilesExportMetadata = metaCheck.Checked,
                    GeoLocation = chosenGeo,
                    TilesClampToGround = clampCheck.Checked,
                    TilesTileSizeMb = parsedTileSizeMb,
                    TilesNorthRotation = chosenNorthRotation,
                    TilesDracoCompression = dracoCheck.Checked,
                    TilesTextureAtlas = atlasCheck.Checked
                };
                if (!ResolveScope(uiDocument, doc, scopeChoice, result))
                    return null;
                return result;
            }
        }

        // ==========================================================================================
        // 共享助手
        // ==========================================================================================

        /// <summary>按弹窗选择解析导出范围；范围为空时弹提示并返回 false</summary>
        private static bool ResolveScope(UIDocument uiDocument, Document doc, int scopeChoice, ExportSettings settings)
        {
            switch (scopeChoice)
            {
                case 0:
                    settings.ScopeIds = null;
                    settings.ScopeName = "全模型";
                    break;
                case 2:
                    settings.ScopeIds = uiDocument.Selection.GetElementIds();
                    settings.ScopeName = string.Format(CultureInfo.InvariantCulture, "选中 {0} 个构件", settings.ScopeIds.Count);
                    break;
                default:
                    settings.ScopeIds = new FilteredElementCollector(doc, doc.ActiveView.Id)
                        .WhereElementIsNotElementType().ToElementIds();
                    if (settings.ScopeIds.Count == 0)
                    {
                        TaskDialog.Show("范围为空", "当前视图没有可导出的构件。");
                        return false;
                    }
                    settings.ScopeName = string.Format(CultureInfo.InvariantCulture, "视图可见 {0} 个构件 ({1})",
                        settings.ScopeIds.Count, doc.ActiveView.Name);
                    break;
            }
            return true;
        }

        /// <summary>项目名（文件名去扩展；未保存时用文档标题）</summary>
        internal static string ProjectNameOf(Document doc)
        {
            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(projectName)) projectName = doc.Title;
            return projectName;
        }

        /// <summary>DetailLevel 文件名段（小写英文）</summary>
        internal static string DetailSlug(ViewDetailLevel detail)
        {
            if (detail == ViewDetailLevel.Fine) return "fine";
            if (detail == ViewDetailLevel.Medium) return "medium";
            return "coarse";
        }

        /// <summary>持久化字符串 → ViewDetailLevel（非法值回退 Fine）</summary>
        internal static ViewDetailLevel ParseDetail(string slug)
        {
            if (slug == "coarse") return ViewDetailLevel.Coarse;
            if (slug == "medium") return ViewDetailLevel.Medium;
            return ViewDetailLevel.Fine;
        }

        /// <summary>三角化精度显示文本（null=Revit 默认）</summary>
        internal static string TriText(double? triangulateLod)
        {
            return triangulateLod.HasValue
                ? triangulateLod.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "default";
        }
    }
}
