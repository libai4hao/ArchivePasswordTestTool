using ArchivePasswordTestTool.Properties;
using System.Diagnostics;

namespace ArchivePasswordTestTool
{
    /// <summary>可直接交给 hashcat 的哈希任务</summary>
    internal sealed record GpuHashJob(int Mode, string HashLine);

    /// <summary>
    /// 把压缩包转换成 hashcat 哈希：
    /// - header 加密 7z：调用同目录的 7z2hashcat64-2.0.exe，只接受 $7z$ 的 type 0；
    /// - ZIP：自研解析 WinZip AES 条目，生成 $zip2$。
    /// 其余格式（普通 7z、ZipCrypto、RAR）返回"不支持"，由 CPU 处理。
    /// </summary>
    internal static class ArchiveHashExtractor
    {
        private const string SevenZipExtractorName = "7z2hashcat64-2.0.exe";

        public static bool TryPrepare(string archivePath, ArchiveInfo info, out GpuHashJob job, out string reason)
        {
            job = default!;
            reason = string.Empty;

            if (info.Type == SevenZipGuids.CLSID_CFormat7z)
            {
                return TryPrepareSevenZip(archivePath, info, out job, out reason);
            }

            if (info.Type == SevenZipGuids.CLSID_CFormatZip)
            {
                if (ZipAesHashReader.TryRead(archivePath, out string hashLine, out reason))
                {
                    job = new GpuHashJob(13600, hashLine);
                    return true;
                }

                return false;
            }

            reason = Resources.GpuFormatUnsupported;
            return false;
        }

        private static bool TryPrepareSevenZip(
            string archivePath,
            ArchiveInfo info,
            out GpuHashJob job,
            out string reason)
        {
            job = default!;

            // 普通 7z 的密码正确性必须解压后才能确认；hashcat 的 $7z$ type 2
            // 也不在当前支持范围（仅 header 加密的 type 0）
            if (!info.IsHeaderEncryption)
            {
                reason = Resources.GpuFormatUnsupported;
                return false;
            }

            if (ArchiveContext.TryGetVolumePaths(archivePath, out _))
            {
                // 7z2hashcat 2.0 对当前带分隔的分卷样本无法完成 seek；
                // 复制整个分卷集合代价过高，首期直接回退 CPU
                reason = Resources.GpuSplitUnsupported;
                return false;
            }

            string extractor = Path.Combine(AppContext.BaseDirectory, SevenZipExtractorName);
            if (!File.Exists(extractor))
            {
                reason = Resources.GpuToolsMissing;
                return false;
            }

            if (!RunExtractor(extractor, archivePath, out string stdout, out string stderr))
            {
                reason = string.Format(Resources.GpuExtractorFailed, Tail(stderr));
                return false;
            }

            foreach (string line in stdout.Split('\n'))
            {
                string candidate = line.Trim();

                if (!candidate.StartsWith("$7z$", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] fields = candidate.Split('$');

                // $7z$<type>$...：type 0 = header 加密，2 = 普通 7z
                if (fields.Length < 3)
                {
                    break;
                }

                if (fields[2] != "0")
                {
                    reason = Resources.GpuFormatUnsupported;
                    return false;
                }

                job = new GpuHashJob(11600, candidate);
                reason = string.Empty;
                return true;
            }

            reason = string.Format(Resources.GpuExtractorFailed, Tail(stderr));
            return false;
        }

        private static bool RunExtractor(
            string exePath,
            string archivePath,
            out string stdout,
            out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;

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

                startInfo.ArgumentList.Add("--skip-sensitive-data-warning");
                startInfo.ArgumentList.Add(archivePath);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    stderr = "failed to start extractor";
                    return false;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(120_000))
                {
                    process.Kill(entireProcessTree: true);
                    stderr = "extractor timed out";
                    return false;
                }

                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return false;
            }
        }

        private static string Tail(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return "unknown error";
            }

            return trimmed.Length <= 300 ? trimmed : trimmed[^300..].Trim();
        }
    }
}
