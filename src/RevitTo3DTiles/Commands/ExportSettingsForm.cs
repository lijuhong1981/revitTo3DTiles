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

        // —— 3D Tiles 模式专属 ——
        public bool TilesExportMetadata = true;
        public GeoLocation GeoLocation;
        public bool TilesClampToGround;
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
        // glTF 模式：布局与行为自 revitToGltf v0.4.1 等价迁移
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
            bool nameEdited = false;      // 用户手工改过文件名后，滑动条不再自动改写
            bool suppressNameSync = false; // 程序化赋值时抑制 TextChanged 的"已编辑"标记

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "导出 glTF 设置";
                form.ClientSize = new System.Drawing.Size(1100, 672);
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
                var nameLabel = new Label { Text = "文件名:", Left = 12, Top = 532, Width = 160 };
                var nameBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 560,
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
                var dirLabel = new Label { Text = "输出目录:", Left = 12, Top = 458, Width = 160 };
                var dirBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 486,
                    Width = 920,
                    Height = 32,
                    Text = chosenDir,
                    ReadOnly = true
                };
                var browseButton = new Button { Text = "浏览...", Left = 948, Top = 484, Width = 140, Height = 38 };
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

                // 导出元数据：独立一行（不放同一 FlowLayoutPanel，避免与格式单选同行）
                var exportMetaCheck = new CheckBox
                {
                    Text = "导出元数据（同名 .metadata）",
                    Left = 12,
                    Top = 424,
                    AutoSize = true,
                    Checked = chosenMeta,
                    BackColor = System.Drawing.Color.Transparent
                };
                exportMetaCheck.CheckedChanged += (s, e) => { chosenMeta = exportMetaCheck.Checked; };

                var okButton = new Button { Text = "导出", Left = 828, Top = 604, Width = 120, Height = 42 };
                var cancelButton = new Button
                {
                    Text = "取消",
                    Left = 960,
                    Top = 604,
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
                SeparateTextures = chosenSeparateTex
            };
            if (!ResolveScope(uiDocument, doc, scopeChoice, result))
                return null;
            return result;
        }

        // ==========================================================================================
        // 3D Tiles 模式：公共区（范围/精度）与 glTF 相同，专属区为地理定位与转换选项；
        // 输出为目录（tileset.json 所在目录），无文件名一行。
        // ==========================================================================================

        private static ExportSettings ShowTiles3D(UIDocument uiDocument, Document doc)
        {
            string projectName = ProjectNameOf(doc);

            AppSettings.Settings saved = AppSettings.Load();
            string chosenDir = (!string.IsNullOrWhiteSpace(saved.LastOutputDirectory) && Directory.Exists(saved.LastOutputDirectory))
                ? saved.LastOutputDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), projectName + "_3dtiles");

            int scopeChoice = saved.Scope;
            if (scopeChoice < 0 || scopeChoice > 2) scopeChoice = 1;
            ViewDetailLevel chosenDetail = ParseDetail(saved.DetailLevel);
            double? chosenTri = saved.UseCustomTriangulate
                ? (double?)Math.Min(1.0, Math.Max(0.0, saved.TriangulateLod))
                : null;

            // 地理位置初值：项目 SiteLocation 自动读取；无有效坐标时默认不勾选
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

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "导出 3D Tiles 设置";
                form.ClientSize = new System.Drawing.Size(1100, 746);
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
                radioCoarse.CheckedChanged += (s, e) => { if (radioCoarse.Checked) chosenDetail = ViewDetailLevel.Coarse; };
                radioMedium.CheckedChanged += (s, e) => { if (radioMedium.Checked) chosenDetail = ViewDetailLevel.Medium; };
                radioFine.CheckedChanged += (s, e) => { if (radioFine.Checked) chosenDetail = ViewDetailLevel.Fine; };

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
                };
                triTrack.ValueChanged += (s, e) =>
                {
                    chosenTri = triTrack.Value / 100.0;
                    triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                };

                // ---- 3D Tiles 专属：BIM 属性（高DPI下TrackBar实际高约80px，需留足间距） ----
                var metaCheck = new CheckBox
                {
                    Text = "写入 BIM 属性到瓦片（构件拾取 / 按楼层与类别过滤）",
                    Left = 12, Top = 376, Width = 880, AutoSize = true,
                    Checked = chosenMeta,
                    BackColor = System.Drawing.Color.Transparent
                };

                // ---- 3D Tiles 专属：地理位置 ----
                var geoGroup = new GroupBox { Text = "地理位置（模型在地球上的放置点）", Left = 12, Top = 412, Width = 1076, Height = 128 };
                var useGeoCheck = new CheckBox
                {
                    Text = string.Format("使用项目地理位置{0}", projectGeo.HasValue ? "" : "（当前模型未设置坐标）"),
                    Left = 20, Top = 24, Width = 640, AutoSize = true,
                    Checked = chosenUseGeo, Enabled = projectGeo.HasValue,
                    BackColor = System.Drawing.Color.Transparent
                };
                var lngLabel = new Label { Text = "经度(°):", Left = 20, Top = 68, Width = 80 };
                var lngBox = new System.Windows.Forms.TextBox
                {
                    Left = 104, Top = 62, Width = 170, Text = lngText, Enabled = chosenUseGeo
                };
                var latLabel = new Label { Text = "纬度(°):", Left = 300, Top = 68, Width = 80 };
                var latBox = new System.Windows.Forms.TextBox
                {
                    Left = 384, Top = 62, Width = 170, Text = latText, Enabled = chosenUseGeo
                };
                var altLabel = new Label { Text = "海拔(米):", Left = 580, Top = 68, Width = 90 };
                var altBox = new System.Windows.Forms.TextBox
                {
                    Left = 674, Top = 62, Width = 130, Text = altText, Enabled = chosenUseGeo
                };
                useGeoCheck.CheckedChanged += (s, e) =>
                {
                    lngBox.Enabled = latBox.Enabled = altBox.Enabled = useGeoCheck.Checked;
                };
                geoGroup.Controls.Add(useGeoCheck);
                geoGroup.Controls.Add(lngLabel);
                geoGroup.Controls.Add(lngBox);
                geoGroup.Controls.Add(latLabel);
                geoGroup.Controls.Add(latBox);
                geoGroup.Controls.Add(altLabel);
                geoGroup.Controls.Add(altBox);

                // ---- 3D Tiles 专属：贴地 ----
                var clampCheck = new CheckBox
                {
                    Text = "自动贴地（忽略海拔，模型底面落在地表）",
                    Left = 12, Top = 556, Width = 880, AutoSize = true,
                    Checked = chosenClamp,
                    BackColor = System.Drawing.Color.Transparent
                };

                // ---- 输出目录（tileset.json 所在目录） ----
                var dirLabel = new Label { Text = "输出目录:", Left = 12, Top = 600, Width = 160 };
                var dirBox = new System.Windows.Forms.TextBox
                {
                    Left = 12, Top = 628, Width = 920, Height = 32, Text = chosenDir, ReadOnly = true
                };
                var browseButton = new Button { Text = "浏览...", Left = 948, Top = 626, Width = 140, Height = 38 };
                browseButton.Click += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dlg.Description = "选择 3D Tiles 输出目录";
                        dlg.SelectedPath = string.IsNullOrEmpty(dirBox.Text) ? chosenDir : dirBox.Text;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            dirBox.Text = dlg.SelectedPath;
                    }
                };

                var okButton = new Button { Text = "导出", Left = 828, Top = 686, Width = 120, Height = 42 };
                var cancelButton = new Button
                {
                    Text = "取消", Left = 960, Top = 686, Width = 120, Height = 42,
                    DialogResult = DialogResult.Cancel
                };
                GeoLocation chosenGeo = null;
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

                    chosenDir = dirBox.Text;
                    // 按域保存：公共项 + 3D Tiles 专属项；glTF 域字段原样保留
                    AppSettings.Settings toSave = AppSettings.Load();
                    toSave.LastOutputDirectory = chosenDir;
                    toSave.Scope = scopeChoice;
                    toSave.DetailLevel = DetailSlug(chosenDetail);
                    toSave.UseCustomTriangulate = chosenTri.HasValue;
                    toSave.TriangulateLod = chosenTri ?? 1.0;
                    toSave.TilesExportMetadata = metaCheck.Checked;
                    toSave.TilesUseGeolocation = useGeoCheck.Checked;
                    toSave.TilesClampToGround = clampCheck.Checked;
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
                form.Controls.Add(geoGroup);
                form.Controls.Add(clampCheck);
                form.Controls.Add(dirLabel);
                form.Controls.Add(dirBox);
                form.Controls.Add(browseButton);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog() != DialogResult.OK)
                    return null;

                var result = new ExportSettings
                {
                    OutputDirectory = chosenDir,
                    DetailLevel = chosenDetail,
                    TriangulateLod = chosenTri,
                    TilesExportMetadata = metaCheck.Checked,
                    GeoLocation = chosenGeo,
                    TilesClampToGround = clampCheck.Checked
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
