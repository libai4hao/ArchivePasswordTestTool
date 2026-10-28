using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// GPU 加速入口：探测 hashcat、提取哈希、驱动会话，并用 7z.dll 最终确认候选。
    /// 任何一步失败都返回原因，由 Runner 回退到 CPU；本类不直接修改 UI 或退出码。
    /// </summary>
    internal static class GpuPasswordTester
    {
        /// <summary>
        /// 是否为 GPU 可能覆盖的格式（用于决定是否展示"GPU 不可用"的提示，
        /// 避免普通 7z / RAR 用户被无意义的提示打扰）。
        /// </summary>
        public static bool IsCandidateFormat(ArchiveInfo info)
        {
            return info.Type == SevenZipGuids.CLSID_CFormatZip
                || (info.Type == SevenZipGuids.CLSID_CFormat7z && info.IsHeaderEncryption);
        }

        public static bool TryPrepare(
            Options options,
            string archivePath,
            ArchiveInfo info,
            out GpuHashJob job,
            out HashcatInstallation installation,
            out string reason)
        {
            job = default!;
            installation = default!;
            reason = string.Empty;

            if (options.NoGpu)
            {
                return false;
            }

            if (!IsCandidateFormat(info))
            {
                reason = Resources.GpuFormatUnsupported;
                return false;
            }

            // 先提取哈希再探测 hashcat：格式不支持时不必付出 -I 的启动成本
            if (!ArchiveHashExtractor.TryPrepare(archivePath, info, out job, out reason))
            {
                return false;
            }

            HashcatProbeResult probe = HashcatLocator.ProbeOnce();
            if (!probe.Available || probe.Installation is null)
            {
                reason = probe.Reason;
                return false;
            }

            installation = probe.Installation;
            return true;
        }

        public static HashcatRunResult Run(
            GpuHashJob job,
            HashcatInstallation installation,
            string archivePath,
            ArchiveInfo info,
            PasswordDictionary dictionary,
            string dllPath,
            ArchiveContext archiveContext,
            Action<long> onProgress)
        {
            bool Confirm(string candidate)
            {
                if (!info.IsHeaderEncryption)
                {
                    // 普通加密：现有上下文可直接提取快速测试目标验证
                    return archiveContext.TestPassword(candidate);
                }

                // header 加密：Open 阶段才验证密码，必须按候选重开上下文
                try
                {
                    using var context = new ArchiveContext(dllPath, archivePath, candidate, info);
                    return context.TestPassword(candidate);
                }
                catch (ArchivePasswordException)
                {
                    return false;
                }
            }

            return HashcatSession.Run(
                installation,
                job.Mode,
                job.HashLine,
                dictionary,
                Confirm,
                onProgress);
        }
    }
}
