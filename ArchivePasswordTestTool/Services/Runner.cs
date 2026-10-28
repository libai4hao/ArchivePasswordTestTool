using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>单个压缩包的测试任务：归档路径 + 可复用的字典 + 本轮选项</summary>
    internal sealed record TestJob(string ArchivePath, PasswordDictionary Dictionary, Options Options);

    /// <summary>
    /// 单个压缩包的测试结果。批量编排（BatchRunner）据此决定汇总、结果落盘与退出码。
    /// Error 非空表示该包未能完成（格式不识别、文件损坏、7z.dll 故障等），其余字段无意义。
    /// </summary>
    internal sealed record TestOutcome(
        string ArchivePath,
        bool Inspected,
        bool Encrypted,
        string? Password,
        long Tested,
        TimeSpan Elapsed,
        string ArchiveHash,
        string? Error)
    {
        public static TestOutcome Failed(string archivePath, string error) =>
            new(archivePath, false, true, null, 0, TimeSpan.Zero, string.Empty, error);
    }

    /// <summary>
    /// 单个压缩包的测试主流程：打开归档 → 在线查询 → 字典重置 → 执行（多进程/进程内并行）
    /// → 输出结果 → 回传。批量循环、参数解析、结果保存与下一轮交互由 BatchRunner 负责。
    /// 异常与退出码语义由调用方决定：单包失败返回带 Error 的 TestOutcome，不再直接退出进程。
    /// </summary>
    internal sealed class Runner(TestJob job)
    {
        private static readonly SHA256 HashAlgorithm = SHA256.Create();

        private readonly Options options = job.Options;
        private readonly PasswordDictionary dictionary = job.Dictionary;
        private readonly string archivePath = job.ArchivePath;
        private readonly Stopwatch stopwatch = new();
        private readonly Planner planner = new();

        // 归档哈希：Base64 版用于结果文件，Hex 版作为在线服务的归档标识。
        // 均为惰性计算（见 ComputeArchiveHash）——离线且不保存结果时一次都不读全文件
        private string archiveHash = string.Empty;
        private string archiveHashHex = string.Empty;
        private bool hashComputed;

        /// <summary>归档是否加密（决定报告里的密码行写密码 / 未找到 / 未加密）</summary>
        private bool archiveEncrypted = true;

        private long dictionaryCount;
        private long testedCount;
        private string? foundPassword;

        /// <summary>
        /// 执行单个压缩包。<paramref name="saveResults"/> 为 true 时顺带计算归档哈希，
        /// 供调用方写出结果文件；为 false 且无需在线交互时不会整文件读一遍。
        /// </summary>
        public TestOutcome Run(bool saveResults)
        {
            try
            {
                return RunCore(saveResults);
            }
            catch (Exception ex)
            {
                // 单包失败不终止进程：由 BatchRunner 记录、继续处理其余压缩包并决定最终退出码
                return TestOutcome.Failed(archivePath, ex.Message);
            }
            finally
            {
                planner.DisposeAll();
            }
        }

        private TestOutcome RunCore(bool saveResults)
        {
            // 在线服务只在真正配置了地址时才可能用到哈希；
            // 占位地址（未部署服务）下绝不为此读一遍整个压缩包
            bool onlineEnabled = Sync.IsConfigured(options.OnlineServiceUrl);

            using var archiveContext = new ArchiveContext(options.SevenZipDllPath, archivePath, password: null);
            var archiveInfo = archiveContext.Info;

            // 归档已打开、信息已知：后续无论加密与否都要给用户留一份报告
            if (!archiveInfo.IsEncrypted)
            {
                archiveEncrypted = false;
                AnsiConsole.WriteLine(string.Format(Resources.ArchiveNotEncrypted, archivePath));

                if (saveResults)
                {
                    ComputeArchiveHash();
                }

                return BuildOutcome(inspected: true);
            }

            // 在线共享（默认开启）：按哈希向服务器查询共享密码（一个哈希对应一个密码），
            // 存在则置于队列最前优先测试；失败只提示不阻断。
            // 工作进程不做网络操作，只有主进程在此请求
            string? onlinePassword = null;
            if (options.QueryOnline)
            {
                if (onlineEnabled)
                {
                    ComputeArchiveHash();
                }

                onlinePassword = Sync.RequestOnlinePassword(options.OnlineServiceUrl, archiveHashHex);
            }

            // 每个压缩包重新起跑同一批密码（含本包的在线密码）
            dictionary.Reset(onlinePassword);
            dictionaryCount = dictionary.Count;

            // 非 UTF-8 字典（典型：简中 Windows 记事本另存的 ANSI/GBK）照旧能正常破解，
            // 但要让用户知道密码文本是按哪种编码解读的，否则看到"怪字符"会以为读错了
            foreach (var source in dictionary.Sources)
            {
                if (source.Encoding.CodePage != Encoding.UTF8.CodePage)
                {
                    string message = string.Format(Resources.DictionaryEncodingDetected, source.Encoding.WebName);
                    if (dictionary.Sources.Count > 1)
                    {
                        message += $" [{source.Path}]";
                    }

                    Ui.WriteWarning(message);
                }
            }

            var (Mode, WorkerCount, ThreadParallelism, DisplayParallelism) = planner.Plan(options.SevenZipDllPath, archiveInfo, archivePath, dictionaryCount);
            Ui.WriteArchiveInfo(
                archivePath,
                hashComputed ? archiveHash : null,
                archiveInfo.Format,
                archiveInfo.FileCount,
                archiveInfo.IsHeaderEncryption,
                archiveInfo.SupportsQuickTest,
                dictionary.DisplayPath,
                dictionaryCount,
                DisplayParallelism);

            if (Mode == ExecutionMode.MultiProcess)
            {
                RunMultiProcess(options.SevenZipDllPath, dictionary, WorkerCount);
            }
            else
            {
                RunThreaded(options.SevenZipDllPath, archiveInfo, dictionary, ThreadParallelism);
            }

            Ui.WriteOutcome(
                !string.IsNullOrEmpty(foundPassword),
                archivePath,
                dictionary.DisplayPath,
                foundPassword,
                stopwatch.Elapsed,
                Interlocked.Read(ref testedCount));

            if (string.IsNullOrEmpty(foundPassword))
            {
                ConsoleTitle.ShowNotFound();
            }
            else
            {
                ConsoleTitle.ShowFound();
            }

            // 密码回传（默认开启）：找到即把 哈希+密码 POST 给服务端供他人共享；失败只提示
            if (!string.IsNullOrEmpty(foundPassword) && options.ReportOnline && onlineEnabled)
            {
                ComputeArchiveHash();
                Sync.ReportFoundPassword(options.OnlineServiceUrl, archiveHashHex, foundPassword);
            }

            // 结果文件需要 Base64 哈希；放在最后，只有确实要保存时才多读一遍文件
            if (saveResults)
            {
                ComputeArchiveHash();
            }

            return BuildOutcome(inspected: true);
        }

        private TestOutcome BuildOutcome(bool inspected)
        {
            return new TestOutcome(
                archivePath,
                inspected,
                archiveEncrypted,
                foundPassword,
                Interlocked.Read(ref testedCount),
                stopwatch.Elapsed,
                archiveHash,
                Error: null);
        }

        /// <summary>
        /// 计算压缩包哈希（惰性、幂等）：Base64 版给结果文件，Hex 版给在线服务。
        /// 分块 TransformBlock 计算并驱动进度条按字节推进（大文件不再黑屏干等）
        /// </summary>
        private void ComputeArchiveHash()
        {
            if (hashComputed)
            {
                return;
            }

            using var file = File.OpenRead(archivePath);
            HashAlgorithm.Initialize();
            long fileLength = Math.Max(1, file.Length);

            CreateProgress()
                .Start(ctx =>
                {
                    var hashTask = ctx.AddTask(Resources.AnalyzingArchiveInformation, maxValue: fileLength);

                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        HashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
                        hashTask.Increment(read);
                        ConsoleTitle.ShowProgress(Resources.TitleAnalyzing, hashTask.Value, hashTask.MaxValue);
                    }

                    HashAlgorithm.TransformFinalBlock([], 0, 0);
                    var hashBytes = HashAlgorithm.Hash!;
                    archiveHash = Convert.ToBase64String(hashBytes);
                    archiveHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    // 空文件无字节可读，强制补满避免进度条停在起点
                    hashTask.Value = hashTask.MaxValue;
                });

            hashComputed = true;
        }

        /// <summary>
        /// 多进程模式：主进程负责字典分发（去重/线程安全），密码逐批经管道送入
        /// N 个自身副本；每个副本加载独立的 7z.dll（独立全局锁），绕开 KDF 串行化。
        /// 进度条由主进程按 worker 回传的批测试计数推进
        /// </summary>
        private void RunMultiProcess(string dllPath, PasswordDictionary dictionary, int workerCount)
        {
            stopwatch.Restart();

            CreateProgress()
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    MultiProcess.RunWorkers(
                        Environment.ProcessPath!,
                        dllPath,
                        archivePath,
                        dictionary,
                        workerCount,
                        onTested: acked =>
                        {
                            Interlocked.Add(ref testedCount, acked);
                            testProgressBar.Increment(acked);
                            ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                        },
                        onFound: password =>
                        {
                            foundPassword = password;
                            stopwatch.Stop();
                        });

                    // 只在真正跑完（字典耗尽）时才拉满：找到密码后剩余密码并未测试，
                    // 拉满会谎称 100%，而重定向输出下用户就是靠这个百分比判断进度
                    if (string.IsNullOrEmpty(foundPassword))
                    {
                        testProgressBar.Value = testProgressBar.MaxValue;
                    }
                });
        }

        /// <summary>
        /// 进程内并行模式：上下文池复用（非 header 加密）或按密码重开（header 加密），
        /// Parallel.For 消费字典，进度条随测试计数推进
        /// </summary>
        private void RunThreaded(string dllPath, ArchiveInfo archiveInfo, PasswordDictionary dictionary, int parallelism)
        {
            var isFullyEncrypted = archiveInfo.IsHeaderEncryption;

            stopwatch.Restart();

            CreateProgress()
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    Parallel.For(0, dictionaryCount, new ParallelOptions() { MaxDegreeOfParallelism = parallelism }, (i, state) =>
                    {
                        ArchiveContext? context = null;
                        try
                        {
                            string? password = dictionary.Pop();
                            if (password is null)
                            {
                                // 字典已耗尽（不应发生：迭代数等于 Count），安全跳过
                                return;
                            }

                            if (!isFullyEncrypted)
                            {
                                context = planner.RentOrCreate(dllPath, archivePath, archiveInfo);
                            }
                            else
                            {
                                context = new ArchiveContext(dllPath, archivePath, password, archiveInfo);
                            }

                            // 快速测试目标（最小加密文件索引）由 ArchiveContext 打开归档时算好并缓存，
                            // 这里不再逐密码新建索引数组
                            if (context.TestPassword(password))
                            {
                                foundPassword = password;
                                stopwatch.Stop();
                                state.Stop();
                            }
                        }
                        catch (ArchivePasswordException)
                        {
                            // 只吞"预期内的密码不匹配"（header 加密 / 全加密包在 Open 阶段校验密码）。
                            // 其余异常（7z.dll 加载失败、OOM、未知格式等）不再静默——
                            // 否则真实故障会被伪装成"未找到密码"
                        }
                        finally
                        {
                            if (!isFullyEncrypted && context != null)
                            {
                                planner.Return(context);
                            }
                            else
                            {
                                context?.Dispose();
                            }

                            Interlocked.Increment(ref testedCount);
                            testProgressBar.Increment(1);
                            ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                        }
                    });

                    // 只在真正跑完（字典耗尽）时才拉满：找到密码后 state.Stop() 提前退出，
                    // 剩余密码并未测试，拉满会谎称 100%
                    if (string.IsNullOrEmpty(foundPassword))
                    {
                        testProgressBar.Value = testProgressBar.MaxValue;
                    }
                });

            planner.DisposeAll();
        }

        /// <summary>
        /// 进度条统一样式（哈希计算与两处测密的列配置此前各写一份，且缩进都跑偏了）。
        /// 重定向/非终端环境下 Spectre 自动退化，测试安全。
        /// </summary>
        private static Progress CreateProgress() =>
            AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns([
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn()
                ]);
    }
}
