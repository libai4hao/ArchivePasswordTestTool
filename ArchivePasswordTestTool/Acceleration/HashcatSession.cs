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
    /// 由调用方用 7z.dll 最终确认。临时 hash/potfile/outfile 放在独立目录并清理。
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

            try
            {
                Directory.CreateDirectory(tempDirectory);

                string hashPath = Path.Combine(tempDirectory, "hash.txt");
                string outPath = Path.Combine(tempDirectory, "cracked.txt");
                string potPath = Path.Combine(tempDirectory, "hashcat.potfile");

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

                string sessionName = "Aptt" + Guid.NewGuid().ToString("N")[..12];

                void Add(string value) => startInfo.ArgumentList.Add(value);

                Add("-m");
                Add(mode.ToString());
                Add(hashPath);
                Add("--stdin");
                Add("--keep-guessing");
                Add("--outfile");
                Add(outPath);
                Add("--outfile-format");
                Add("2");
                Add("--potfile-path");
                Add(potPath);
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
