using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 测试结果落盘（批量开始前统一确认一次，逐包写出 .txt；单包时可再询问是否定位文件）
    /// </summary>
    internal static class ResultsWriter
    {
        /// <summary>结果文件路径：与压缩包同目录，追加固定后缀</summary>
        public static string ResultPath(string archivePath) => $"{archivePath}[results].txt";

        /// <summary>
        /// 写出单个压缩包的结果。调用方负责在批量开始前询问一次是否保存，
        /// 并保证 <see cref="TestOutcome.ArchiveHash"/> 已计算（Runner 在 saveResults=true 时完成）。
        /// </summary>
        public static void Write(TestOutcome outcome, string dictionaryDisplayPath)
        {
            string resultPath = ResultPath(outcome.ArchivePath);

            using StreamWriter testOut = new(resultPath, false);
            testOut.WriteLine(string.Format(Resources.TestResultArchive, outcome.ArchivePath));
            testOut.WriteLine(string.Format(Resources.TestResultDictionary, dictionaryDisplayPath));
            testOut.WriteLine(string.Format(Resources.TestResultElapsedTime, outcome.Elapsed));
            testOut.WriteLine(string.Format(Resources.TestResultArchiveHash, outcome.ArchiveHash));
            testOut.WriteLine(BuildPasswordLine(outcome.Password, outcome.Encrypted));
        }

        /// <summary>单包批量结束后的"是否打开报告"确认；非交互终端静默跳过</summary>
        public static void OfferOpen(string resultPath)
        {
            if (!AnsiConsole.Profile.Out.IsTerminal || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                return;
            }

            if (!InteractivePrompts.ConfirmSingleKey(Resources.ViewTestReportPrompt, true))
            {
                return;
            }

            Process.Start("explorer.exe", $"/select, \"{resultPath}\"");
        }

        /// <summary>
        /// 结果文件的密码行，三态：找到密码 / 加密但未找到 / 归档未加密。
        /// <para>
        /// 未加密必须单列一态：它的 password 是空串，若按「空即未找到」处理会写成
        /// 「未找到正确的解压密码」——不是找不到，是压根没有密码，会误导用户。
        /// 旧实现用 <c>password != null</c> 判定，未加密包甚至会留下一行空密码。
        /// </para>
        /// </summary>
        internal static string BuildPasswordLine(string? password, bool archiveEncrypted)
        {
            if (!archiveEncrypted)
            {
                return Resources.TestResultArchiveNotEncrypted;
            }

            return string.IsNullOrEmpty(password)
                ? Resources.TestResultPasswordNotFound
                : string.Format(Resources.TestResultDecompressionPassword, password);
        }
    }
}
