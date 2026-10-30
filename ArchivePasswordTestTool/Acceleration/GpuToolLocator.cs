namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 在程序目录内定位 GPU 工具：先查 exe 同目录，再查一级子目录。
    /// <para>
    /// hashcat 发行包通常解压成 <c>hashcat-6.2.6\</c> 再整个放进程序目录，因此允许一级子目录；
    /// 但不递归更深层级、不搜索 PATH/注册表——工具来源仍限定在用户明确放置的程序目录内
    /// （迭代 02 决策 D5，迭代 03 放宽为「程序目录及一级子目录」）。
    /// </para>
    /// </summary>
    internal static class GpuToolLocator
    {
        private const string HashcatExeName = "hashcat.exe";
        private const string SevenZipExtractorExactName = "7z2hashcat64-2.0.exe";
        private const string SevenZipExtractorPattern = "7z2hashcat*.exe";

        /// <summary>
        /// 候选目录：程序目录本身 + 按名称排序的一级子目录。
        /// <paramref name="baseDirectory"/> 仅测试用，默认取 <see cref="AppContext.BaseDirectory"/>。
        /// </summary>
        public static IEnumerable<string> CandidateDirectories(string? baseDirectory = null)
        {
            string root = baseDirectory ?? AppContext.BaseDirectory;
            yield return root;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                // 目录不可枚举时仍可使用程序目录本身
                yield break;
            }

            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

            foreach (string directory in subdirectories)
            {
                yield return directory;
            }
        }

        /// <summary>按同目录优先、一级子目录兜底的顺序查找文件；找不到返回 null</summary>
        public static string? FindFile(string fileName, string? baseDirectory = null)
        {
            foreach (string directory in CandidateDirectories(baseDirectory))
            {
                string path = Path.Combine(directory, fileName);

                try
                {
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (Exception)
                {
                    // 无权限/路径过长等：继续找下一个目录
                }
            }

            return null;
        }

        public static string? FindHashcat(string? baseDirectory = null) =>
            FindFile(HashcatExeName, baseDirectory);

        /// <summary>
        /// 查找 7z2hashcat：优先精确的 2.0 文件名，其次同目录内任意版本的 7z2hashcat*.exe。
        /// 仍然同目录优先、一级子目录兜底。
        /// </summary>
        public static string? FindSevenZipExtractor(string? baseDirectory = null)
        {
            string? exact = FindFile(SevenZipExtractorExactName, baseDirectory);
            if (exact is not null)
            {
                return exact;
            }

            foreach (string directory in CandidateDirectories(baseDirectory))
            {
                try
                {
                    string[] matches = Directory.GetFiles(directory, SevenZipExtractorPattern);
                    if (matches.Length > 0)
                    {
                        Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
                        return matches[0];
                    }
                }
                catch (Exception)
                {
                    // 同上：跳过不可读目录
                }
            }

            return null;
        }
    }
}
