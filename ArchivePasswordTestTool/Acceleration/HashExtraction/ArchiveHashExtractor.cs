using ArchivePasswordTestTool.Properties;
using System.Diagnostics;

namespace ArchivePasswordTestTool
{
    /// <summary>可直接交给 hashcat 的哈希任务</summary>
    internal sealed record GpuHashJob(int Mode, string HashLine);

    /// <summary>
    /// 把压缩包转换成 hashcat 哈希：
    /// - header 加密 7z：调用程序目录（含一级子目录）里的 7z2hashcat，
    ///   data_type 为加密头解密后的压缩方法（0/1/2/7 均由 hashcat 11600 支持）；
    /// - ZIP：自研解析 WinZip AES 条目，生成 $zip2$。
    /// 其余格式（普通 7z、ZipCrypto、RAR）返回"不支持"，由 CPU 处理。
    /// </summary>
    internal static class ArchiveHashExtractor
    {
        /// <summary>
        /// hashcat 11600 支持的 $7z$ data_type：0=原始、1=LZMA1、2=LZMA2、7=DEFLATE。
        /// 该字段是「加密头解密后的压缩方法」，不是「是否 header 加密」——
        /// header 判定由 <see cref="ArchiveInfo.IsHeaderEncryption"/> 负责。
        /// 0x80 表示数据被截断，hashcat 不接受。
        /// </summary>
        private static readonly HashSet<string> SupportedSevenZipDataTypes = ["0", "1", "2", "7"];

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

            // 本方法只处理 header 加密 7z：普通 7z 的 $7z$ 哈希对应文件数据，
            // 是否值得 GPU 化不在首期范围内，保持 CPU
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

            string? extractor = GpuToolLocator.FindSevenZipExtractor();
            if (extractor is null)
            {
                reason = Resources.GpuToolsMissing;
                return false;
            }

            if (!RunExtractor(extractor, archivePath, out string stdout, out string stderr))
            {
                reason = string.Format(Resources.GpuExtractorFailed, Tail(stderr));
                return false;
            }

            if (!TryParseSevenZipHash(stdout, out job, out reason))
            {
                // 有 $7z$ 行但类型不受支持时 reason 已给出；
                // 完全没解析到哈希时用提取器 stderr 说明原因
                if (string.IsNullOrEmpty(reason))
                {
                    reason = string.Format(Resources.GpuExtractorFailed, Tail(stderr));
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// 从 7z2hashcat 的 stdout 中解析出 hashcat 可用的 $7z$ 行。
        /// 只校验 data_type；header 是否加密由调用方事先判定。
        /// </summary>
        internal static bool TryParseSevenZipHash(string stdout, out GpuHashJob job, out string reason)
        {
            job = default!;
            reason = string.Empty;

            foreach (string rawLine in stdout.Split('\n'))
            {
                string candidate = rawLine.Trim();

                if (!candidate.StartsWith("$7z$", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] fields = candidate.Split('$');

                // $7z$<data_type>$...；字段不足或类型不被 hashcat 支持时拒绝
                if (fields.Length < 3 || !SupportedSevenZipDataTypes.Contains(fields[2]))
                {
                    reason = Resources.GpuFormatUnsupported;
                    return false;
                }

                job = new GpuHashJob(11600, candidate);
                return true;
            }

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
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
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
