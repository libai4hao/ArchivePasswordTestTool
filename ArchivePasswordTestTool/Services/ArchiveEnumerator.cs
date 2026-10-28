using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool
{
    /// <summary>文件夹扫描时被跳过的条目及原因</summary>
    internal enum ArchiveSkipReason
    {
        /// <summary>扩展名在白名单内，但文件签名不是受支持的压缩包</summary>
        Unrecognized,

        /// <summary>分卷的后续卷（.002 及以后），只能从 .001 开始处理</summary>
        NonFirstVolume,

        /// <summary>隐藏或系统文件</summary>
        HiddenOrSystem,

        /// <summary>目录/文件无法访问</summary>
        Inaccessible,
    }

    internal sealed record SkippedEntry(string Path, ArchiveSkipReason Reason);

    internal sealed record ArchiveEnumeration(
        IReadOnlyList<string> Archives,
        IReadOnlyList<SkippedEntry> Skipped);

    /// <summary>
    /// 把「文件 + 文件夹」输入展开为确定的压缩包列表。
    /// <para>
    /// 文件夹规则（与迭代 01 的决策一致）：递归子目录 → 扩展名白名单初筛 →
    /// 签名确认 → 分卷只取 .001 → 跳过隐藏/系统文件 → 按完整路径去重。
    /// </para>
    /// <para>
    /// 显式指定的文件不做白名单过滤：用户明确点名时应当得到真实的格式错误，
    /// 而不是被静默跳过；但分卷的后续卷仍会被拒绝（无法拼出完整流）。
    /// </para>
    /// </summary>
    internal static class ArchiveEnumerator
    {
        private static readonly HashSet<string> CandidateExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".7z", ".zip", ".rar", ".exe", ".001" };

        /// <summary>展开输入；缺失的输入按启动参数错误抛出（正常情况下 Options 已提前校验）</summary>
        public static ArchiveEnumeration Expand(IEnumerable<string> inputs)
        {
            var archives = new List<string>();
            var skipped = new List<SkippedEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string input in inputs)
            {
                if (Directory.Exists(input))
                {
                    CollectDirectory(input, archives, skipped, seen);
                    continue;
                }

                if (!File.Exists(input))
                {
                    throw new FileNotFoundException(Resources.ArchiveFileNotFound);
                }

                string fullPath = Path.GetFullPath(input);

                if ((IsVolumeFile(fullPath, out int volumeNumber) && volumeNumber != 1)
                    || IsNonFirstRarPart(fullPath))
                {
                    skipped.Add(new SkippedEntry(fullPath, ArchiveSkipReason.NonFirstVolume));
                    continue;
                }

                if (seen.Add(fullPath))
                {
                    archives.Add(fullPath);
                }
            }

            return new ArchiveEnumeration(archives, skipped);
        }

        private static void CollectDirectory(
            string directory,
            List<string> archives,
            List<SkippedEntry> skipped,
            HashSet<string> seen)
        {
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory)
                    .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                skipped.Add(new SkippedEntry(directory, ArchiveSkipReason.Inaccessible));
                return;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception)
                {
                    skipped.Add(new SkippedEntry(entry, ArchiveSkipReason.Inaccessible));
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
                {
                    skipped.Add(new SkippedEntry(entry, ArchiveSkipReason.HiddenOrSystem));
                    continue;
                }

                // 目录联接/符号链接可能形成环，递归扫描时一律不跟随
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(new SkippedEntry(entry, ArchiveSkipReason.Inaccessible));
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    CollectDirectory(entry, archives, skipped, seen);
                    continue;
                }

                // 分卷后缀（.002 / .0003）不在扩展名白名单里，必须先按分卷规则识别，
                // 否则它们会被当作「无关文件」静默忽略，用户看不到跳过原因
                bool isVolume = IsVolumeFile(entry, out int volumeNumber);

                if (!isVolume && !CandidateExtensions.Contains(Path.GetExtension(entry)))
                {
                    // 白名单之外的文件不读取、不计入跳过统计，避免大目录里成千上万的无关文件刷屏
                    continue;
                }

                if ((isVolume && volumeNumber != 1) || IsNonFirstRarPart(entry))
                {
                    skipped.Add(new SkippedEntry(entry, ArchiveSkipReason.NonFirstVolume));
                    continue;
                }

                if (ArchiveFormatDetector.DetectArchiveFormat(entry).Format == Guid.Empty)
                {
                    skipped.Add(new SkippedEntry(entry, ArchiveSkipReason.Unrecognized));
                    continue;
                }

                string fullPath = Path.GetFullPath(entry);
                if (seen.Add(fullPath))
                {
                    archives.Add(fullPath);
                }
            }
        }

        /// <summary>
        /// 判断文件名是否带分卷序号（.001 / .0001 之类的 2–3 位数字后缀）。
        /// 与 <see cref="ArchiveContext.TryGetVolumePaths"/> 的识别规则保持一致。
        /// </summary>
        private static bool IsVolumeFile(string path, out int volumeNumber)
        {
            volumeNumber = 0;
            string name = Path.GetFileName(path);
            int dot = name.LastIndexOf('.');

            if (dot <= 0)
            {
                return false;
            }

            string digits = name[(dot + 1)..];
            if (digits.Length is < 2 or > 3 || !digits.All(char.IsAsciiDigit))
            {
                return false;
            }

            volumeNumber = int.Parse(digits);
            return true;
        }

        /// <summary>
        /// WinRAR 分卷命名 <c>xxx.part1.rar</c>：只有 part1 是入口，
        /// 后续分卷在文件夹扫描时必须跳过，否则会被当成独立压缩包逐个报错。
        /// </summary>
        private static bool IsNonFirstRarPart(string path)
        {
            string name = Path.GetFileName(path);
            int partIndex = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);

            if (partIndex < 0)
            {
                return false;
            }

            string tail = name[(partIndex + ".part".Length)..];
            int dot = tail.IndexOf('.');

            if (dot <= 0 || !tail[(dot + 1)..].Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string digits = tail[..dot];
            return digits.Length <= 9
                && digits.All(char.IsAsciiDigit)
                && int.TryParse(digits, out int number)
                && number != 1;
        }
    }
}
