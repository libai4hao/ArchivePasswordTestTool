using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 批量编排：参数解析（一次）→ 展开压缩包 → 逐包执行 Runner → 汇总 → 交互式下一轮。
    /// <para>
    /// 与 Runner 的职责边界：Runner 只处理一个压缩包并返回 TestOutcome；
    /// 本类负责跨包状态（字典复用、保存答复、退出码、循环），因此包与包之间不会
    /// 串用哈希、密码或 Planner 上下文。
    /// </para>
    /// </summary>
    internal sealed class BatchRunner(string[] args)
    {
        /// <summary>会话入口：标题、备用屏幕、异常兜底；返回进程退出码</summary>
        public int Run()
        {
            int exitCode = 1;

            try
            {
                ConsoleTitle.Initialize();

                if (AnsiConsole.Profile.Out.IsTerminal)
                {
                    AnsiConsole.Clear();
                }

                Ui.RunInAlternateScreenWhenSupported(() => exitCode = RunSession());
            }
            catch (Exception ex)
            {
                // 启动期错误（参数缺失、7z.dll 不可用等）：与单包时代一致，打印后 exit 1
                Ui.WriteError(ex.Message);
                exitCode = 1;
            }
            finally
            {
                ConsoleTitle.Restore();
            }

            return exitCode;
        }

        private int RunSession()
        {
            Ui.WriteHeader();

            // 上一次启动完成了自我替换：明确告知，避免用户误以为「版本号变了但没更新」
            if (PendingUpdate.JustUpdated)
            {
                Ui.WriteWarning(string.Format(Resources.UpgradeApplied, Program.VersionText));
            }

            // 更新检查在后台发起：网络耗时与后续引导、测密重叠，不阻塞首屏
            Upgrade.StartBackgroundCheck();

            try
            {
                var options = Options.Parse(args);
                SevenZip.Initialize(options.SevenZipDllPath);

                var enumeration = ArchiveEnumerator.Expand(options.ArchiveInputs);
                if (enumeration.Archives.Count == 0)
                {
                    Ui.WriteError(Resources.BatchNoArchives);
                    return 1;
                }

                Ui.WriteBatchSkipped(enumeration.Skipped);

                bool interactive = IsInteractive();
                bool saveResults = interactive
                    && InteractivePrompts.ConfirmSingleKey(Resources.SaveTestResultsPrompt, true);

                // 字典只构建一次（合并/去重/编码探测），每个压缩包开始前 Reset 回起点
                using var dictionary = new PasswordDictionary(options.DictionaryPaths);

                int exitCode = 0;

                while (true)
                {
                    var outcomes = RunBatch(enumeration.Archives, options, dictionary, saveResults);

                    if (outcomes.Any(outcome => outcome.Error is not null))
                    {
                        exitCode = 1;
                    }

                    if (outcomes.Count > 1)
                    {
                        Ui.WriteBatchSummary(outcomes);
                    }

                    // 更新提示先于本轮结束时的阻塞式确认（打开报告、下一轮）消费；
                    // 后者若让用户停留或直接关窗口，排在后面的更新提示就再也没机会出现
                    Upgrade.CompleteBackgroundCheck();

                    if (saveResults && outcomes.Count == 1
                        && outcomes[0].Error is null && outcomes[0].Inspected)
                    {
                        ResultsWriter.OfferOpen(ResultsWriter.ResultPath(outcomes[0].ArchivePath));
                    }

                    // 仅交互终端进入下一轮；带参批处理/管道调用跑完即退出
                    if (!interactive || !InteractivePrompts.ConfirmSingleKey(Resources.BatchNextRoundPrompt, true))
                    {
                        break;
                    }

                    // 下一轮沿用上一轮的字典与其他开关，只重新选择压缩包/文件夹。
                    // 选到空文件夹/全是无法识别的文件时重新询问，而不是直接结束会话
                    while (true)
                    {
                        var inputs = InteractivePrompts.AskArchiveInputs();
                        if (inputs.Count == 0)
                        {
                            return exitCode;
                        }

                        var candidate = ArchiveEnumerator.Expand(inputs);
                        if (candidate.Archives.Count == 0)
                        {
                            Ui.WriteError(Resources.BatchNoArchives);
                            continue;
                        }

                        enumeration = candidate;
                        break;
                    }

                    Ui.WriteBatchSkipped(enumeration.Skipped);
                }

                return exitCode;
            }
            finally
            {
                // 更新提示在本轮所有阻塞式确认之后消费，避免提示被保存/打开报告的询问淹没
                Upgrade.CompleteBackgroundCheck();
            }
        }

        private static List<TestOutcome> RunBatch(
            IReadOnlyList<string> archives,
            Options options,
            PasswordDictionary dictionary,
            bool saveResults)
        {
            var outcomes = new List<TestOutcome>(archives.Count);

            for (int i = 0; i < archives.Count; i++)
            {
                string archivePath = archives[i];
                Ui.WriteBatchPosition(i + 1, archives.Count, archivePath);

                var outcome = new Runner(new TestJob(archivePath, dictionary, options)).Run(saveResults);
                outcomes.Add(outcome);

                if (outcome.Error is not null)
                {
                    Ui.WriteError($"{archivePath}: {outcome.Error}");
                    continue;
                }

                if (saveResults && outcome.Inspected)
                {
                    try
                    {
                        ResultsWriter.Write(outcome, dictionary.DisplayPath);
                    }
                    catch (Exception ex)
                    {
                        // 单个结果文件写失败（只读目录、磁盘满）不应中断整批
                        Ui.WriteWarning($"{archivePath}: {ex.Message}");
                    }
                }
            }

            return outcomes;
        }

        /// <summary>循环与保存询问需要真实终端；管道/重定向下全部退化（与单包时代一致）</summary>
        private static bool IsInteractive()
        {
            return !Console.IsInputRedirected
                && !Console.IsOutputRedirected
                && AnsiConsole.Profile.Capabilities.Interactive;
        }
    }
}
