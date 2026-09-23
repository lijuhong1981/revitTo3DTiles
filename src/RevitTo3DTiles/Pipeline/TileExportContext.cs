using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitTo3DTiles.Pipeline
{
    /// <summary>导出上下文：承载输出目录、日志与统计</summary>
    public class TileExportContext
    {
        public string OutputDirectory { get; set; }
        public string TempDirectory { get; set; }
        public int ElementCount { get; set; }
        public int MeshElementCount { get; set; }
        public long TriangleCount { get; set; }
        public long TextureFileCount { get; set; }
        public int SkippedElementCount { get; set; }
        public int MaterialCount { get; set; }
        public int AppearanceAssetCount { get; set; }
        public int BitmapTextureCount { get; set; }

        private readonly StreamWriter _log;

        public TileExportContext(string outputDirectory, StreamWriter log)
        {
            OutputDirectory = outputDirectory;
            TempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "revitTo3DTiles_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _log = log;
        }

        public void Log(string message)
        {
            if (_log != null)
            {
                _log.WriteLine(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message));
                _log.Flush();
            }
        }
    }
}
