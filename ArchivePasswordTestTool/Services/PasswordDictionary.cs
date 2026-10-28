using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 单个字典文件的探测结果。多字典模式下每份文件独立探测编码，
    /// 避免 GBK 与 UTF-8 混用时整份字典被按错误编码解读。
    /// </summary>
    public sealed record DictionarySource(string Path, Encoding Encoding, int AddedPasswordCount);

    /// <summary>
    /// 线程安全、单趟读取、构造期去重的密码字典（支持多个文件）。
    /// 设计要点：
    /// - 多个文件按传入顺序依次读取：前一个文件的密码先进入队列，全局去重；
    /// - 消费按游标推进：Reset 可把队列恢复到起点，供批量处理多个压缩包时复用；
    ///   因此密码集合在整个批量期间常驻内存（多包复用的必要代价）；
    /// - 文件序服务：重复行仅在首次出现处被服务一次；
    /// - 多线程安全：Pop 在同一把锁内推进游标。
    /// Count 供 Parallel.For(0, Count) 迭代计划使用：迭代数与实际可服务数精确相等。
    /// 消费方：Runner（进程内并行模式）与 MultiProcess（master 分发）。
    /// </summary>
    public sealed class PasswordDictionary : IDisposable
    {
        private readonly Lock SyncRoot = new();

        // 去重后的密码（文件序），整个生命周期保留，供 Reset 复用
        private readonly List<string> Passwords;

        // 当前一轮的待测序列（在线密码可能插在最前），消费时按 Cursor 推进
        private List<string?> Pending = [];
        private int Cursor;

        /// <summary>当前一轮的唯一密码数（含在线密码），Reset 后更新</summary>
        public int Count { get; private set; }

        /// <summary>字典文件路径（按传入顺序）</summary>
        public IReadOnlyList<string> Paths { get; }

        /// <summary>展示用文本：单字典为原路径，多字典用 " + " 连接</summary>
        public string DisplayPath => string.Join(" + ", Paths);

        /// <summary>每份字典文件的编码探测结果与新增密码数</summary>
        public IReadOnlyList<DictionarySource> Sources { get; }

        /// <summary>首个字典的编码（保留给单字典调用方；多字典请查看 <see cref="Sources"/>）</summary>
        public Encoding Encoding => Sources[0].Encoding;

        /// <summary>单个字典（保留旧调用方式）</summary>
        public PasswordDictionary(string dictionaryPath)
            : this([dictionaryPath])
        {
        }

        /// <summary>单个字典 + 在线密码（保留旧调用方式）</summary>
        public PasswordDictionary(string dictionaryPath, string? onlinePassword)
            : this([dictionaryPath], onlinePassword)
        {
        }

        /// <summary>多个字典，按传入顺序合并去重</summary>
        public PasswordDictionary(IReadOnlyList<string> dictionaryPaths)
            : this(dictionaryPaths, onlinePassword: null)
        {
        }

        /// <summary>
        /// 构造字典。多个文件按传入顺序读取并全局去重；
        /// onlinePassword 为在线服务按压缩包哈希下发的共享密码（可为 null）：
        /// 置于当前一轮待测序列头部，保证被最先测试，且与文件内重复时只测一次。
        /// </summary>
        public PasswordDictionary(IReadOnlyList<string> dictionaryPaths, string? onlinePassword)
        {
            if (dictionaryPaths.Count == 0)
            {
                throw new ArgumentException("At least one dictionary path is required.", nameof(dictionaryPaths));
            }

            Paths = dictionaryPaths.ToArray();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var passwords = new List<string>();
            var sources = new List<DictionarySource>(dictionaryPaths.Count);

            foreach (string path in Paths)
            {
                // 编码自适应按文件进行：GBK 密码本（简中 Windows 记事本另存的默认结果）
                // 整份读成乱码时，密码就在字典里也必然报"未找到"
                Encoding encoding = DictionaryEncoding.Detect(path);

                int before = passwords.Count;
                foreach (string line in File.ReadLines(path, encoding))
                {
                    if (seen.Add(line))
                    {
                        passwords.Add(line);
                    }
                }

                sources.Add(new DictionarySource(path, encoding, passwords.Count - before));
            }

            Passwords = passwords;
            Sources = sources;

            Reset(onlinePassword);
        }

        /// <summary>
        /// 重置到队列起点，供下一个压缩包复用同一批密码。
        /// onlinePassword 为 null 时不插入在线密码；非空时置于最前，
        /// 并从文件密码中移除同值项（保证一轮内每个密码只测一次）。
        /// </summary>
        public void Reset(string? onlinePassword = null)
        {
            lock (SyncRoot)
            {
                var pending = new List<string?>(Passwords.Count + 1);

                if (!string.IsNullOrEmpty(onlinePassword))
                {
                    pending.Add(onlinePassword);
                }

                foreach (string password in Passwords)
                {
                    if (!string.Equals(password, onlinePassword, StringComparison.Ordinal))
                    {
                        pending.Add(password);
                    }
                }

                Pending = pending;
                Cursor = 0;
                Count = pending.Count;
            }
        }

        /// <summary>
        /// 线程安全地批量取出密码（GPU 的 stdin 喂词路径）。
        /// 返回实际取出的数量；字典耗尽返回 0。
        /// </summary>
        public int PopBatch(int maxCount, List<string> destination)
        {
            lock (SyncRoot)
            {
                int count = Math.Min(maxCount, Pending.Count - Cursor);

                for (int i = 0; i < count; i++)
                {
                    destination.Add(Pending[Cursor]!);
                    Cursor++;
                }

                return count;
            }
        }

        /// <summary>
        /// 线程安全地取出下一个密码；字典耗尽时返回 null（防御性兜底，正常迭代数等于 Count）
        /// </summary>
        public string? Pop()
        {
            lock (SyncRoot)
            {
                return Cursor < Pending.Count ? Pending[Cursor++] : null;
            }
        }

        public void Dispose()
        {
            lock (SyncRoot)
            {
                Pending.Clear();
                Cursor = 0;
                Count = 0;
            }
        }
    }
}
