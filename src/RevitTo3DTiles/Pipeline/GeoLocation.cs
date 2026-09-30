using System;
using Autodesk.Revit.DB;
using RevitTo3DTiles.Extraction;

namespace RevitTo3DTiles.Pipeline
{
    /// <summary>
    /// 项目地理位置：从 Revit 文档读取并换算为 modelTo3DTiles 的 -lla 参数格式（度/米）。
    /// 经纬度来自 SiteLocation（API 单位为弧度）；海拔来自 ActiveProjectPosition 的
    /// ProjectPosition.Elevation（API 单位为英尺，此处为高程零点换算，模型锚点仍由
    /// 转换器 --cc 修正到包围盒中心，故海拔只作粗略基准）。
    /// </summary>
    public class GeoLocation
    {
        /// <summary>经度（度，东经为正）</summary>
        public double Longitude { get; set; }

        /// <summary>纬度（度，北纬为正）</summary>
        public double Latitude { get; set; }

        /// <summary>海拔（米）</summary>
        public double Altitude { get; set; }

        /// <summary>文档是否带有有效地理位置（经纬度均为 0 视为未设置，常见于空模板）</summary>
        public bool HasValue { get; set; }

        /// <summary>
        /// 从 Revit 文档读取地理位置。任何一步失败或经纬度均为 0 时返回 HasValue=false，
        /// 调用方据此回退为不传 -lla（转换器落默认坐标）。
        /// </summary>
        public static GeoLocation FromDocument(Document doc)
        {
            var geo = new GeoLocation();
            if (doc == null)
                return geo;

            try
            {
                SiteLocation site = doc.SiteLocation;
                double lng = site.Longitude * 180.0 / Math.PI;
                double lat = site.Latitude * 180.0 / Math.PI;

                double alt = 0;
                try
                {
                    ProjectPosition position = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                    if (position != null)
                        alt = position.Elevation * GeometryExtractor.FeetToMeter;
                }
                catch
                {
                    // 海拔读取失败不影响经纬度使用
                }

                geo.Longitude = lng;
                geo.Latitude = lat;
                geo.Altitude = alt;
                geo.HasValue = Math.Abs(lng) > 1e-9 || Math.Abs(lat) > 1e-9;
            }
            catch
            {
                // SiteLocation 不可用时保持 HasValue=false
            }
            return geo;
        }

        /// <summary>格式化为 -lla 参数值（不变文化，6 位小数足够米级精度）</summary>
        public string ToArgument()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.######},{1:0.######},{2:0.##}",
                Longitude, Latitude, Altitude);
        }

        /// <summary>
        /// 读取项目正北角（度）：项目北 → 正北的偏角，绕上轴右手方向为正。
        /// 来自 ActiveProjectLocation 的 ProjectPosition.Angle（API 单位弧度）。
        /// 读取失败返回 null；约等于 0 视为无偏转（调用方不必旋转）。
        /// </summary>
        public static double? ReadTrueNorthAngle(Document doc)
        {
            try
            {
                ProjectPosition position = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                if (position == null)
                    return null;
                return position.Angle * 180.0 / Math.PI;
            }
            catch
            {
                return null;
            }
        }
    }
}
