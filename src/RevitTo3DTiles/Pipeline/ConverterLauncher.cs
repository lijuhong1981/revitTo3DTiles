using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RevitTo3DTiles.Pipeline
{
    /// <summary>
    /// 转换器调用：以独立进程运行 modelTo3DTiles（避免大模型转换占用Revit进程内存，
    /// 也使其崩溃不连累Revit）。可执行文件查找顺序：
    /// 1. 插件程序集同目录（随安装包分发）
    /// 2. 环境变量 MODELTO3DTILES_PATH 指定
    /// 3. 系统PATH
    /// 参数契约对齐 modelTo3DTiles v2.1.0：--md 为 BIM 语义 sidecar（.metadata，
    /// Elements[].Key 与 glTF 节点 extras.uniqueId 对应），-lla 为度/米制经纬度。
    /// </summary>
    public static class ConverterLauncher
    {
        public const string ConverterFileName = "modelTo3DTiles.exe";

        public class Result
        {
            public bool Success { get; set; }
            public bool Cancelled { get; set; }
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string ErrorMessage { get; set; }
            public string ConverterPath { get; set; }
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
        /// 执行转换。inputPath为glTF/glb；geoLocation非空且有效时传-lla定位；
        /// metadataPath非空时传--md写入构件属性表；转换器输出的3DTiles落在outputDirectory。
        /// 瓦片拆分固定为 material（按材质装填，体积最小加载最快）。
        /// 等待期间轮询取消（取消时杀掉转换进程并返回Cancelled），并泵消息保持进度窗响应。
        /// </summary>
        public static Result Run(string inputPath, string metadataPath, string outputDirectory,
            GeoLocation geoLocation, bool clampToGround, GltfExportContext context)
        {
            string converterPath = ResolveConverterPath();
            var result = new Result { ConverterPath = converterPath };

            var arguments = new StringBuilder();
            // 注意：yargs 只对单字符别名接受单横线（-i/-o/-s），多字符别名必须双横线
            //（单横线 -lla/-md 会被当作短旗标簇解析而静默落回默认值，已在转换器侧实测）
            arguments.AppendFormat(CultureInfo.InvariantCulture, "-i \"{0}\"", inputPath);
            arguments.AppendFormat(CultureInfo.InvariantCulture, " -o \"{0}\"", outputDirectory);
            if (!string.IsNullOrEmpty(metadataPath))
                arguments.AppendFormat(CultureInfo.InvariantCulture, " --md \"{0}\"", metadataPath);
            if (geoLocation != null && geoLocation.HasValue)
                arguments.Append(" --lla \"").Append(geoLocation.ToArgument()).Append("\"");
            arguments.Append(" --cc");
            arguments.Append(clampToGround ? " --ctg" : " --no-ctg");
            arguments.Append(" -s material");

            context.Log("调用转换器: " + converterPath);
            context.Log("参数: " + arguments);

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

                    // 轮询等待：每200ms泵一次消息保持进度窗可响应，并检查用户取消
                    while (!process.WaitForExit(200))
                    {
                        Func<bool> check = context.ShouldCancel;
                        if (check != null && check())
                        {
                            TryKill(process);
                            result.Cancelled = true;
                            context.Log("用户取消：已终止转换器进程。");
                            return result;
                        }
                        Application.DoEvents();
                    }

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

        /// <summary>尽力终止转换进程（Kill 可能因进程已退出抛异常，忽略）</summary>
        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // 进程恰好退出时 Kill 抛异常，忽略
            }
        }
    }
}
