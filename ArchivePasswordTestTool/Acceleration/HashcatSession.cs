using System.Diagnostics;
using System.Text;

namespace ArchivePasswordTestTool
{
    internal sealed record HashcatRunResult(
        string? Password,
        long Submitted,
        bool Failed,
        string Error);

    /// <summary>
    /// 一次 hashcat 会话：把字典按顺序经 stdin 灌入，轮询 outfile 获取候选，
    /// 由调用方用 7z.dll 最终确认。临时 hash/outfile 放在独立目录并清理。
    /// </summary>
    internal static class HashcatSession
    {
        public static HashcatRunResult Run(
            HashcatInstallation installation,
            int mode,
            string hashLine,
            PasswordDictionary dictionary,
            Func<string, bool> confirm,
            Action<long>? onProgress)
        {
            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "ApttHashcat-" + Guid.NewGuid().ToString("N"));

            Process? process = null;
            Task? writerTask = null;
            long submitted = 0;
            string? foundPassword = null;
            string? sessionName = null;

            try
            {
                Directory.CreateDirectory(tempDirectory);

                string hashPath = Path.Combine(tempDirectory, "hash.txt");
                string outPath = Path.Combine(tempDirectory, "cracked.txt");

                File.WriteAllText(hashPath, hashLine + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var startInfo = new ProcessStartInfo(installation.ExePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = installation.WorkingDirectory,
                };

                sessionName = "Aptt" + Guid.NewGuid().ToString("N")[..12];

                void Add(string value) => startInfo.ArgumentList.Add(value);

                Add("-m");
                Add(mode.ToString());
                Add(hashPath);

                // hashcat 8.0.0 移除了 --stdin：直接 -a 0 且不给 wordlist 时从 stdin 读；
                // 6.x / 7.x 仍需要 --stdin
                bool useStdinFlag = !Version.TryParse(installation.Version, out Version? hashcatVersion)
                    || hashcatVersion.Major < 8;

                if (useStdinFlag)
                {
                    Add("--stdin");
                }
                else
                {
                    Add("-a");
                    Add("0");

                    // hashcat 8.0.0 的 --quiet 不再抑制状态屏；状态打印会调用
                    // module_hash_encode（11600 零长度 salt 的崩溃点），把更新间隔拉到最大
                    Add("--status-timer");
                    Add("3600");
                }

                Add("--keep-guessing");
                Add("--outfile");
                Add(outPath);
                Add("--outfile-format");
                Add("2");
                // 不使用 potfile：候选由 7z.dll 确认，potfile 只会多一次 hash 编码；
                // hashcat 11600 在某些 7z 哈希上 module_hash_encode 会崩溃（零长度 salt），
                // 关闭 potfile 可绕开这条路径
                Add("--potfile-disable");
                Add("--restore-disable");
                Add("--logfile-disable");
                Add("--session");
                Add(sessionName);
                Add("--quiet");

                // 只使用 -I 探测到的 GPU，避免 hashcat 同时把 CPU OpenCL 设备算进去
                if (int.TryParse(installation.DeviceId, out int deviceId) && deviceId > 0)
                {
                    Add("-d");
                    Add(deviceId.ToString());
                }

                process = Process.Start(startInfo);
                if (process is null)
                {
                    return new HashcatRunResult(null, 0, true, "failed to start hashcat.exe");
                }

                // 必须持续消费 stdout/stderr，否则管道写满会让 hashcat 阻塞
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                writerTask = Task.Run(() =>
                {
                    try
                    {
                        using var writer = new StreamWriter(
                            process.StandardInput.BaseStream,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            bufferSize: 64 * 1024)
                        {
                            AutoFlush = false,
                        };

                        var batch = new List<string>(512);

                        while (true)
                        {
                            batch.Clear();
                            int count = dictionary.PopBatch(512, batch);
                            if (count == 0)
                            {
                                break;
                            }

                            foreach (string password in batch)
                            {
                                writer.Write(password);
                                writer.Write('\n');
                            }

                            writer.Flush();
                            Interlocked.Add(ref submitted, count);
                        }
                    }
                    catch (IOException)
                    {
                        // hashcat 提前退出（已找到/报错）导致管道关闭，属正常路径
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        try
                        {
                            process.StandardInput.Close();
                        }
                        catch (Exception)
                        {
                            // 进程可能已退出
                        }
                    }
                });

                int seenLines = 0;
                long lastReported = -1;

                // hashcat 11600 在部分 7z 哈希上会卡死（模块编码崩溃后 WER 挂起进程），
                // 进程不退出、writer 阻塞、界面永远停在同一个百分比。
                // 用「stdin 提交量长时间不增长」作为无进展判据，超时后走正常失败回退 CPU。
                const int NoProgressTimeoutMs = 120_000;
                long lastObservedSubmitted = 0;
                long lastProgressTick = Environment.TickCount64;

                while (true)
                {
                    foreach (string candidate in ReadNewCandidates(outPath, ref seenLines))
                    {
                        if (confirm(candidate))
                        {
                            foundPassword = candidate;
                            break;
                        }
                    }

                    if (foundPassword is not null)
                    {
                        break;
                    }

                    long current = Interlocked.Read(ref submitted);
                    if (current != lastObservedSubmitted)
                    {
                        lastObservedSubmitted = current;
                        lastProgressTick = Environment.TickCount64;
                    }

                    if (current != lastReported)
                    {
                        onProgress?.Invoke(current);
                        lastReported = current;
                    }

                    if (process.HasExited)
                    {
                        // 退出后做最后一次读取，避免漏掉最后写入的候选
                        foreach (string candidate in ReadNewCandidates(outPath, ref seenLines, includeIncomplete: true))
                        {
                            if (confirm(candidate))
                            {
                                foundPassword = candidate;
                                break;
                            }
                        }

                        break;
                    }

                    if (Environment.TickCount64 - lastProgressTick > NoProgressTimeoutMs)
                    {
                        return new HashcatRunResult(
                            null,
                            current,
                            true,
                            $"hashcat made no progress for {NoProgressTimeoutMs / 1000} s");
                    }

                    Thread.Sleep(100);
                }

                if (foundPassword is not null)
                {
                    Kill(process);
                }

                // 让 writer 感知管道关闭；hashcat 正常结束时会自行 EOF
                try
                {
                    writerTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                }

                if (!process.WaitForExit(10_000))
                {
                    Kill(process);
                }

                string stderr = stderrTask.GetAwaiter().GetResult();
                _ = stdoutTask.GetAwaiter().GetResult();
                long totalSubmitted = Interlocked.Read(ref submitted);

                if (foundPassword is not null)
                {
                    return new HashcatRunResult(foundPassword, totalSubmitted, false, string.Empty);
                }

                // hashcat: 0=字典耗尽，1=已破解（候选应已写入 outfile）
                if (process.ExitCode is 0 or 1)
                {
                    return new HashcatRunResult(null, totalSubmitted, false, string.Empty);
                }

                return new HashcatRunResult(
                    null,
                    totalSubmitted,
                    true,
                    Tail(stderr) is { Length: > 0 } tail ? tail : $"hashcat exit {process.ExitCode}");
            }
            catch (Exception ex)
            {
                return new HashcatRunResult(null, Volatile.Read(ref submitted), true, ex.Message);
            }
            finally
            {
                if (process is not null)
                {
                    Kill(process);
                    process.Dispose();
                }

                try
                {
                    writerTask?.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                }

                // hashcat 会在工作目录（程序目录或工具子目录）写 <session>.pid/.induct/.outfiles，
                // 这些不是我们创建的临时目录内容；不清理会一直堆在程序目录里
                if (sessionName is not null)
                {
                    TryDeleteSessionFiles(installation.WorkingDirectory, sessionName);
                }

                TryDeleteDirectory(tempDirectory);
            }
        }

        private static List<string> ReadNewCandidates(
            string path,
            ref int seenLines,
            bool includeIncomplete = false)
        {
            var result = new List<string>();

            try
            {
                if (!File.Exists(path))
                {
                    return result;
                }

                string text;
                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                string[] lines = text.Split('\n');

                // 没有换行结尾时最后一段可能仍在校验值写入中，默认不消费；
                // hashcat 退出后的最后一次读取才允许取走它
                int completeLines = text.EndsWith('\n')
                    ? lines.Length - 1
                    : includeIncomplete ? lines.Length : lines.Length - 1;

                for (int i = seenLines; i < completeLines; i++)
                {
                    string candidate = lines[i].TrimEnd('\r');
                    if (candidate.Length > 0)
                    {
                        result.Add(candidate);
                    }
                }

                seenLines = Math.Max(seenLines, completeLines);
            }
            catch (IOException)
            {
                // hashcat 仍持有文件句柄，下一轮重试
            }
            catch (UnauthorizedAccessException)
            {
            }

            return result;
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // 进程已退出/无权操作，忽略
            }
        }

        /// <summary>
        /// 删除 hashcat 写在工具目录里的本次会话文件（进程已终止后调用）。
        /// <c>.induct</c> / <c>.outfiles</c> 是目录，其余是文件；句柄可能延迟释放，重试几次。
        /// </summary>
        private static void TryDeleteSessionFiles(string directory, string sessionName)
        {
            foreach (string suffix in new[] { ".pid", ".induct", ".outfiles", ".log" })
            {
                string path = Path.Combine(directory, sessionName + suffix);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else
                        {
                            File.Delete(path);
                        }

                        break;
                    }
                    catch (Exception)
                    {
                        // 句柄未释放/无权限：稍后重试
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }

                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        private static string Tail(string value)
        {
            string trimmed = value.Trim();
            return trimmed.Length <= 300 ? trimmed : trimmed[^300..].Trim();
        }
    }
}
