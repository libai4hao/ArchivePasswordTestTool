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
        /// Windows 控制台一次拖入多个文件时，产生的字符流形如 <c>"C:\a 1.7z" "C:\b.7z"</c>；
        /// 手动输入通常没有引号，此时整串视为一个路径（含空格也保持完整）。
        /// 引号内的空格不拆分，引号外的空白仅作分隔。
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

                // 未加引号：从当前位置到下一个引号（或行尾）整体视为一个路径
                int quoted = raw.IndexOf('"', i);
                string segment = quoted < 0 ? raw[i..] : raw[i..quoted];
                string path = NormalizePath(segment);
                if (path.Length > 0)
                {
                    paths.Add(path);
                }

                if (quoted < 0)
                {
                    break;
                }

                i = quoted;
            }

            return paths.Where(path => path.Length > 0).ToList();
        }
    }
}
