using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArchivePasswordTestTool
{
    /// <summary>hashcat 安装信息与选中的 GPU 设备</summary>
    internal sealed record HashcatInstallation(
        string ExePath,
        string WorkingDirectory,
        string Version,
        string DeviceId,
        string DeviceName,
        string Vendor)
    {
        public string DisplayName => string.IsNullOrEmpty(DeviceName) ? Version : DeviceName;
    }

    /// <summary>探测结果：不可用时 Reason 给出用户可读原因（已本地化）</summary>
    internal sealed record HashcatProbeResult(bool Available, HashcatInstallation? Installation, string Reason);

    /// <summary>
    /// 只从程序 exe 所在目录查找 hashcat.exe（迭代 02 决策 D5）。
    /// 版本与设备探测各启动一次 hashcat，结果缓存到进程结束。
    /// </summary>
    internal static class HashcatLocator
    {
        private const string ExeName = "hashcat.exe";

        private static readonly Lazy<HashcatProbeResult> CachedProbe = new(Probe);

        public static HashcatProbeResult ProbeOnce() => CachedProbe.Value;

        private static HashcatProbeResult Probe()
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, ExeName);

            if (!File.Exists(exePath))
            {
                return new HashcatProbeResult(false, null, Resources.GpuToolsMissing);
            }

            if (!TryRun(exePath, ["--version"], 20_000, out string versionOutput, out string versionError))
            {
                return new HashcatProbeResult(
                    false,
                    null,
                    string.Format(Resources.GpuProbeFailed, Tail(versionError)));
            }

            string? version = ParseVersion(versionOutput);
            if (version is null || !VersionAtLeast6(version))
            {
                return new HashcatProbeResult(
                    false,
                    null,
                    string.Format(Resources.GpuHashcatTooOld, version ?? "?"));
            }

            // 首次 -I 会加载 OpenCL 内核，可能需要十几秒
            if (!TryRun(exePath, ["-I"], 120_000, out string infoOutput, out string infoError))
            {
                return new HashcatProbeResult(
                    false,
                    null,
                    string.Format(Resources.GpuProbeFailed, Tail(infoError)));
            }

            var device = ParseGpuDevice(infoOutput);
            if (device is null)
            {
                return new HashcatProbeResult(false, null, Resources.GpuDeviceNotFound);
            }

            return new HashcatProbeResult(
                true,
                new HashcatInstallation(
                    exePath,
                    AppContext.BaseDirectory,
                    version,
                    device.Value.Id,
                    device.Value.Name,
                    device.Value.Vendor),
                string.Empty);
        }

        /// <summary>运行 hashcat 并合并 stdout/stderr（去掉 ANSI 颜色）</summary>
        private static bool TryRun(string exePath, string[] arguments, int timeoutMs, out string output, out string error)
        {
            error = string.Empty;
            output = string.Empty;

            try
            {
                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };

                foreach (string argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    error = "failed to start hashcat.exe";
                    return false;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    process.Kill(entireProcessTree: true);
                    error = $"hashcat timed out after {timeoutMs} ms";
                    return false;
                }

                output = StripAnsi(stdoutTask.GetAwaiter().GetResult());
                error = StripAnsi(stderrTask.GetAwaiter().GetResult());

                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                {
                    output = error;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string? ParseVersion(string output)
        {
            Match match = Regex.Match(output, @"v(\d+\.\d+\.\d+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool VersionAtLeast6(string version)
        {
            return Version.TryParse(version, out Version? parsed) && parsed.Major >= 6;
        }

        /// <summary>
        /// 从 hashcat -I 输出里挑第一个 GPU 设备；AMD 设备优先。
        /// 输出示例见 docs/DEVELOPMENT.md「hashcat 探测」。
        /// </summary>
        private static (string Id, string Name, string Vendor)? ParseGpuDevice(string output)
        {
            var devices = new List<(string Id, string Type, string Name, string Vendor)>();

            string? currentId = null;
            string type = string.Empty;
            string name = string.Empty;
            string vendor = string.Empty;

            void Commit()
            {
                if (currentId is not null)
                {
                    devices.Add((currentId, type, name, vendor));
                }
            }

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();

                if (line.StartsWith("Backend Device ID #", StringComparison.Ordinal))
                {
                    Commit();
                    currentId = line["Backend Device ID #".Length..].Trim();
                    type = name = vendor = string.Empty;
                    continue;
                }

                if (currentId is null)
                {
                    continue;
                }

                if (line.StartsWith("Type", StringComparison.Ordinal))
                {
                    type = AfterColon(line);
                }
                else if (line.StartsWith("Name", StringComparison.Ordinal))
                {
                    name = AfterColon(line);
                }
                else if (line.StartsWith("Vendor", StringComparison.Ordinal))
                {
                    vendor = AfterColon(line);
                }
            }

            Commit();

            var gpu = devices
                .Where(device => device.Type.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(device =>
                    device.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || device.Vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
                    || device.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (gpu.Id is null)
            {
                return null;
            }

            return (gpu.Id, gpu.Name, gpu.Vendor);
        }

        private static string AfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index >= 0 ? line[(index + 1)..].Trim() : string.Empty;
        }

        private static string StripAnsi(string value)
        {
            return Regex.Replace(value, "\u001b\\[[0-9;]*[A-Za-z]", string.Empty);
        }

        private static string Tail(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length <= 300)
            {
                return trimmed;
            }

            return trimmed[^300..].Trim();
        }
    }
}
