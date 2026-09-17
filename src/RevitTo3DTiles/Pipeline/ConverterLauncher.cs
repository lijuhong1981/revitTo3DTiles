using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RevitTo3DTiles.Pipeline;

namespace RevitTo3DTiles.Pipeline
{
    /// <summary>
    /// 转换器调用：以独立进程运行 modelTo3DTiles（避免大模型转换占用Revit进程内存，
    /// 也使其崩溃不连累Revit）。可执行文件查找顺序：
    /// 1. 插件程序集同目录（随安装包分发）
    /// 2. 环境变量 MODELTO3DTILES_PATH 指定
    /// 3. 系统PATH
    /// </summary>
    public static class ConverterLauncher
    {
        public const string ConverterFileName = "modelTo3DTiles.exe";

        public class Result
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>解析转换器可执行文件路径，未找到返回null</summary>
        public static string ResolveConverterPath()
        {
            string assemblyDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(assemblyDirectory))
            {
                string local = Path.Combine(assemblyDirectory, ConverterFileName);
                if (File.Exists(local)) return local;
            }

            string fromEnvironment = Environment.GetEnvironmentVariable("MODELTO3DTILES_PATH");
            if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
                return fromEnvironment;

            // 交给系统PATH解析
            return ConverterFileName;
        }

        /// <summary>
        /// 执行转换。inputPath为glTF；转换器输出的3DTiles落在 outputDirectory。
        /// </summary>
        public static Result Run(string inputPath, string metadataPath, string outputDirectory,
            TileExportContext context)
        {
            string converterPath = ResolveConverterPath();
            var arguments = new StringBuilder();
            arguments.AppendFormat("-i \"{0}\"", inputPath);
            arguments.AppendFormat(" -o \"{0}\"", outputDirectory);
            if (!string.IsNullOrEmpty(metadataPath))
                arguments.AppendFormat(" --inputMetadata \"{0}\"", metadataPath);

            context.Log("调用转换器: " + converterPath);
            context.Log("参数: " + arguments);

            var result = new Result();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = converterPath,
                    Arguments = arguments.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    var output = new StringBuilder();
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            output.AppendLine(e.Data);
                            context.Log("[3dtiles] " + e.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            output.AppendLine(e.Data);
                            context.Log("[3dtiles:err] " + e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    result.ExitCode = process.ExitCode;
                    result.Output = output.ToString();
                    result.Success = process.ExitCode == 0;
                    if (!result.Success)
                        result.ErrorMessage = string.Format("转换器退出码 {0}，详见日志", process.ExitCode);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "启动转换器失败: " + ex.Message
                    + "\n请确认 modelTo3DTiles.exe 已随插件部署，或设置环境变量 MODELTO3DTILES_PATH。";
                context.Log(result.ErrorMessage);
            }
            return result;
        }
    }
}
