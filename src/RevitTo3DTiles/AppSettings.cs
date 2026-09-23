using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitTo3DTiles
{
    /// <summary>
    /// 插件级持久化设置：跨 Revit 会话保存导出配置（输出目录、范围、精度、格式、元数据、贴图分离、3D Tiles 转换项），
    /// 存到 %APPDATA%\revitTo3DTiles\settings.json，下次打开导出弹窗时自动回填上次的选择。
    /// </summary>
    public static class AppSettings
    {
        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "revitTo3DTiles", "settings.json");

        /// <summary>导出配置。</summary>
        public class Settings
        {
            public string LastOutputDirectory { get; set; }
            public int Scope { get; set; } = 1;                 // 0=全模型 1=视图可见 2=选中构件
            public string DetailLevel { get; set; } = "fine";   // coarse / medium / fine
            public bool UseCustomTriangulate { get; set; }      // 勾选 = 用自定义三角化精度
            public double TriangulateLod { get; set; } = 1.0;   // 0~1
            public bool Binary { get; set; }                    // true = .glb
            public bool ExportMetadata { get; set; }
            public bool SeparateTextures { get; set; } = true;

            // —— 3D Tiles 导出专属（与 glTF 弹窗互不干扰）——
            public bool TilesExportMetadata { get; set; } = true;   // BIM 属性写入瓦片属性表（--md）
            public bool TilesUseGeolocation { get; set; } = true;   // 使用项目地理位置（-lla）
            public double TilesLongitude { get; set; }              // 手改后持久化的覆盖值（度）
            public double TilesLatitude { get; set; }
            public double TilesAltitude { get; set; }               // 米
            public bool TilesClampToGround { get; set; } = false;   // 自动贴地（--ctg）
        }

        private static Settings _cached;

        /// <summary>读取上次的导出配置；无记录或读取失败时返回默认配置。</summary>
        public static Settings Load()
        {
            if (_cached != null)
                return _cached;

            var settings = new Settings();
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var data = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsFile));
                    if (data != null)
                        settings = data;
                }
            }
            catch
            {
                settings = new Settings();
            }
            _cached = settings;
            return settings;
        }

        /// <summary>保存导出配置（失败不影响导出主流程）。</summary>
        public static void Save(Settings settings)
        {
            if (settings == null)
                return;

            _cached = settings;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile,
                    JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch
            {
                // 忽略：设置文件写入失败不应阻断导出。
            }
        }
    }
}
