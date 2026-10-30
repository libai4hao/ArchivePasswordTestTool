using System.Collections.Concurrent;
using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>测试执行模式。多进程仅对 7z 且字典足够大时启用（绕开 dll 内 KDF 全局锁）</summary>
    internal enum ExecutionMode
    {
        Threaded,
        MultiProcess,
        Gpu,
    }

    /// <summary>
    /// 执行模式规划与上下文池管理。从 Runner 拆分：
    /// 决定线程/多进程模式、并发参数，并预建可复用的 ArchiveContext 池。
    /// </summary>
    internal sealed class Planner
    {
        private readonly ConcurrentBag<ArchiveContext> _contextPool = [];

        /// <summary>
        /// 执行模式与并发配置。多进程仅对 7z 且字典足够大时启用（绕开 dll 内 KDF 全局锁）
        /// </summary>
        // 阈值按实测的 CPU/GPU 速度与 hashcat 固定启动开销（约 13 秒）折算：
        // 7z CPU ≈ 1.2k/s、GPU ≈ 468k/s → 约 16k 行回本，留余量取 25k；
        // ZIP AES CPU ≈ 33k/s、GPU ≈ 1.34M/s → 约 440k 行回本，取 500k。
        public const int GpuMinPasswords7z = 25_000;
        public const int GpuMinPasswordsZip = 500_000;

        public (ExecutionMode Mode, int WorkerCount, int ThreadParallelism, int DisplayParallelism) Plan(
            string dllPath,
            ArchiveInfo archiveInfo,
            string archivePath,
            long dictionaryCount,
            bool gpuReady = false)
        {
            int threadParallelism = Math.Max(1, Environment.ProcessorCount - 1);

            if (int.TryParse(Environment.GetEnvironmentVariable("APTT_PARALLELISM"), out int envParallelism) && envParallelism > 0)
            {
                threadParallelism = Math.Min(envParallelism, threadParallelism);
            }

            // GPU 启动（OpenCL 初始化 + 内核编译）有固定成本，小字典交给 CPU 更快
            int gpuMinPasswords = GpuMinPasswordsFor(archiveInfo);
            if (gpuReady && dictionaryCount >= gpuMinPasswords)
            {
                return (ExecutionMode.Gpu, 0, threadParallelism, 1);
            }

            int workerProcessCount = MultiProcess.ResolveWorkerCount();
            bool useMultiProcess = archiveInfo.Type == SevenZipGuids.CLSID_CFormat7z
                && dictionaryCount >= MultiProcess.MinPasswordsForMultiProcess
                && workerProcessCount > 1;

            if (archiveInfo.IsHeaderEncryption)
            {
                AnsiConsole.MarkupLine($"[yellow]{Resources.FullyEncryptedArchiveWarning}[/]");
            }

            if (!useMultiProcess && !archiveInfo.IsHeaderEncryption)
            {
                PreCreateContextPool(dllPath, archivePath, archiveInfo, threadParallelism);
            }

            return useMultiProcess
                ? (ExecutionMode.MultiProcess, workerProcessCount, threadParallelism, workerProcessCount)
                : (ExecutionMode.Threaded, 0, threadParallelism, threadParallelism);
        }

        /// <summary>字典规模是否达到值得启用 GPU 的阈值（供 Runner 决定是否探测 hashcat）</summary>
        public static bool GpuWouldBeUsed(ArchiveInfo archiveInfo, long dictionaryCount)
        {
            return dictionaryCount >= GpuMinPasswordsFor(archiveInfo);
        }

        /// <summary>
        /// 当前压缩包启用 GPU 所需的字典行数。默认值按实测回本点设定，
        /// 可被 APTT_GPU_MIN 统一覆盖（0 = 强制小字典也走 GPU）。
        /// </summary>
        public static int GpuMinPasswordsFor(ArchiveInfo archiveInfo)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("APTT_GPU_MIN"), out int value) && value >= 0)
            {
                return value;
            }

            return archiveInfo.Type == SevenZipGuids.CLSID_CFormatZip
                ? GpuMinPasswordsZip
                : GpuMinPasswords7z;
        }

        /// <summary>从池中取出一个上下文；池空时按需新建</summary>
        public ArchiveContext RentOrCreate(string dllPath, string archivePath, ArchiveInfo archiveInfo)
        {
            return _contextPool.TryTake(out var context)
                ? context
                : new ArchiveContext(dllPath, archivePath, password: null, archiveInfo);
        }

        /// <summary>归还上下文到池中</summary>
        public void Return(ArchiveContext context)
        {
            _contextPool.Add(context);
        }

        /// <summary>释放池中全部上下文</summary>
        public void DisposeAll()
        {
            foreach (var context in _contextPool)
            {
                context.Dispose();
            }
            _contextPool.Clear();
        }

        private void PreCreateContextPool(string dllPath, string archivePath, ArchiveInfo archiveInfo, int parallelism)
        {
            for (int i = 0; i < parallelism; i++)
            {
                try
                {
                    _contextPool.Add(new ArchiveContext(dllPath, archivePath, password: null, archiveInfo));
                }
                catch
                {
                    // 池成员创建失败时按需补建
                }
            }
        }
    }
}
