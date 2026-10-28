namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 命令行参数读取辅助。-Flag 形式的参数与其后的值成对出现。
    /// 消费方：Options（主流程参数解析）、MultiProcess（工作进程 -F/-lib）。
    /// </summary>
    internal static class Arguments
    {
        /// <summary>
        /// 检查 Flag 是否存在且具有非空值；取值过程的异常转为统一的启动参数错误
        /// </summary>
        public static bool StartupParametersCheck(List<string> Parameters, string Flag)
        {
            if (Parameters.Contains($"-{Flag}"))
            {
                try
                {
                    return !string.IsNullOrEmpty(GetParameter(Parameters, Flag, ""));
                }
                catch (Exception)
                {
                    throw new Exception($"Startup parameters error. Please check: {Flag}");
                }
            }

            return false;
        }

        public static bool StartupParametersCheck(string[] Parameters, string Flag)
        {
            return StartupParametersCheck(new List<string>(Parameters), Flag);
        }

        /// <summary>读取 Flag 的值；缺 Flag/缺值/转换失败时返回默认值</summary>
        public static T GetParameter<T>(List<string> Parameters, string Flag, T DefaultParameter)
        {
            try
            {
                if (!Parameters.Contains($"-{Flag}"))
                {
                    throw new ArgumentException(Flag);
                }

                if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                {
                    throw new ArgumentException(Flag);
                }

                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
            }
            catch (Exception)
            {
                return DefaultParameter;
            }
        }

        public static T GetParameter<T>(string[] Parameters, string Flag, T DefaultParameter)
        {
            return GetParameter(new List<string>(Parameters), Flag, DefaultParameter);
        }

        /// <summary>读取 Flag 的值；缺 Flag/缺值/转换失败时返回类型默认值</summary>
        public static T? GetParameter<T>(List<string> Parameters, string Flag)
        {
            try
            {
                if (!Parameters.Contains($"-{Flag}"))
                {
                    throw new ArgumentException(Flag);
                }

                if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                {
                    throw new ArgumentException(Flag);
                }

                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
            }
            catch (Exception)
            {
                return default;
            }
        }

        public static T? GetParameter<T>(string[] Parameters, string Flag)
        {
            return GetParameter<T>(new List<string>(Parameters), Flag);
        }

        /// <summary>
        /// 读取 Flag 的**全部**出现值，按出现顺序返回（消费方：Options 的多字典/多压缩包参数）。
        /// 与 <see cref="GetParameter{T}(string[], string, T)"/> 的区别是后者只取第一次出现。
        /// 末尾缺少值的 Flag 直接忽略，由调用方按"参数缺失"处理。
        /// </summary>
        public static List<string> GetParameters(string[] Parameters, string Flag)
        {
            string token = "-" + Flag;
            var values = new List<string>();

            for (int i = 0; i < Parameters.Length; i++)
            {
                if (!Parameters[i].Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= Parameters.Length)
                {
                    break;
                }

                values.Add(Parameters[i + 1]);

                // 值本身即使以 '-' 开头也不再被当成 Flag（路径很少以 '-' 开头，
                // 但 `-D -F x` 这类漏值输入会在值校验阶段明确报错，而不是静默丢掉 -F）
                i++;
            }

            return values;
        }

        /// <summary>
        /// 去掉路径两端引号与空白。命令行与交互式拖入都会带上引号。
        /// </summary>
        public static string NormalizePath(string raw)
        {
            return raw.Replace("\"", "").Trim();
        }

        /// <summary>
        /// 把一次输入拆成多个路径：
        /// Windows 控制台一次拖入多个文件时，产生的字符流形如
        /// <c>"C:\a 1.7z" "C:\b.7z"</c>（含空格的路径带引号）或
        /// <c>C:\a.7z C:\b.7z</c>（无空格路径不带引号，仅以空白分隔）。
        /// 引号内的空白不拆分，引号外的空白一律视为分隔符。
        /// </summary>
        public static List<string> SplitPaths(string raw)
        {
            var paths = new List<string>();
            int i = 0;

            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i]))
                {
                    i++;
                }

                if (i >= raw.Length)
                {
                    break;
                }

                if (raw[i] == '"')
                {
                    int end = raw.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        paths.Add(NormalizePath(raw[(i + 1)..]));
                        break;
                    }

                    paths.Add(NormalizePath(raw[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }

                // 未加引号：读到下一个空白或引号为止
                int start = i;
                while (i < raw.Length && !char.IsWhiteSpace(raw[i]) && raw[i] != '"')
                {
                    i++;
                }

                paths.Add(NormalizePath(raw[start..i]));
            }

            return paths.Where(path => path.Length > 0).ToList();
        }

        /// <summary>
        /// 解析一次输入中的路径。优先把整串当作一个路径：手动输入含空格的路径
        /// （或拖入单个含空格的文件）时，整串是有效路径，不能按空白拆散；
        /// 整串不存在时才按引号/空白拆分，覆盖「一次拖入多个文件」。
        /// </summary>
        public static List<string> ResolvePaths(string raw, Func<string, bool> pathExists)
        {
            string whole = NormalizePath(raw);
            if (whole.Length == 0)
            {
                return [];
            }

            return pathExists(whole) ? [whole] : SplitPaths(raw);
        }
    }
}
