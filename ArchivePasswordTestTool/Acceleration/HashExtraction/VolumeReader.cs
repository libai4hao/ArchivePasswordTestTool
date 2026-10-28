namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 把分卷压缩包（.zip.001、.zip.002……）当作一个连续只读流读取。
    /// ZIP 的中央目录通常位于最后一卷，哈希提取必须跨卷寻址。
    /// </summary>
    internal sealed class VolumeReader : IDisposable
    {
        private readonly FileStream[] _streams;
        private readonly long[] _starts;
        private bool _disposed;

        public long Length { get; }

        private VolumeReader(IReadOnlyList<string> paths)
        {
            _streams = new FileStream[paths.Count];
            _starts = new long[paths.Count];

            long position = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                _starts[i] = position;
                _streams[i] = File.OpenRead(paths[i]);
                position += _streams[i].Length;
            }

            Length = position;
        }

        /// <summary>分卷包传入第一卷；普通包传入自身</summary>
        public static VolumeReader Open(string archivePath)
        {
            return ArchiveContext.TryGetVolumePaths(archivePath, out var volumes)
                ? new VolumeReader(volumes)
                : new VolumeReader([archivePath]);
        }

        public byte[] ReadExactly(long offset, int count)
        {
            var buffer = new byte[count];
            ReadExactly(offset, buffer);
            return buffer;
        }

        public void ReadExactly(long offset, Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (offset < 0 || buffer.Length == 0)
            {
                if (buffer.Length == 0)
                {
                    return;
                }

                throw new InvalidDataException($"Invalid read offset {offset}.");
            }

            if (offset + buffer.Length > Length)
            {
                throw new InvalidDataException(
                    $"Read past end of archive: offset {offset}, length {buffer.Length}, size {Length}.");
            }

            int volume = FindVolume(offset);
            int written = 0;

            while (written < buffer.Length)
            {
                long within = offset + written - _starts[volume];
                int available = (int)Math.Min(_streams[volume].Length - within, buffer.Length - written);

                if (available <= 0)
                {
                    volume++;
                    if (volume >= _streams.Length)
                    {
                        throw new InvalidDataException("Unexpected end of multi-volume archive.");
                    }

                    continue;
                }

                _streams[volume].Seek(within, SeekOrigin.Begin);
                int read = _streams[volume].Read(buffer.Slice(written, available));
                if (read <= 0)
                {
                    throw new InvalidDataException("Unexpected end of archive volume.");
                }

                written += read;
            }
        }

        private int FindVolume(long position)
        {
            for (int i = _streams.Length - 1; i >= 0; i--)
            {
                if (position >= _starts[i])
                {
                    return i;
                }
            }

            return 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (FileStream stream in _streams)
            {
                stream.Dispose();
            }
        }
    }
}
