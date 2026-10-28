using ArchivePasswordTestTool.Properties;
using System.Buffers.Binary;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 从 ZIP 中提取 WinZip AES 条目，生成 hashcat mode 13600 的 <c>$zip2$</c> 行。
    /// <para>
    /// 格式：<c>$zip2$*0*&lt;mode&gt;*0*&lt;salt&gt;*&lt;verify&gt;*&lt;cipherLen&gt;*&lt;cipher&gt;*&lt;auth&gt;*$/zip2$</c>。
    /// 只处理 AES（中央目录 method=99 且 0x9901 extra field 有效）；
    /// ZipCrypto、ZIP64 与超大密文返回"不支持"，由调用方回退 CPU。
    /// </para>
    /// </summary>
    internal static class ZipAesHashReader
    {
        private const uint EocdSignature = 0x06054B50;       // PK\x05\x06
        private const uint CentralSignature = 0x02014B50;    // PK\x01\x02
        private const uint LocalSignature = 0x04034B50;      // PK\x03\x04
        private const ushort AesExtraId = 0x9901;

        /// <summary>hashcat 单个 hash 行上限约 8 MB 数据；留出余量并避免整份大文件进内存</summary>
        private const int MaxCipherBytes = 4 * 1024 * 1024;

        public static bool TryRead(string archivePath, out string hashLine, out string reason)
        {
            hashLine = string.Empty;
            reason = Resources.GpuFormatUnsupported;

            try
            {
                using var reader = VolumeReader.Open(archivePath);

                if (!TryFindEocd(reader, out long centralOffset, out long centralSize, out int entries))
                {
                    return false;
                }

                long position = centralOffset;
                long centralEnd = centralOffset + centralSize;

                for (int i = 0; i < entries && position + 46 <= centralEnd; i++)
                {
                    byte[] header = reader.ReadExactly(position, 46);
                    if (ReadUInt32(header, 0) != CentralSignature)
                    {
                        return false;
                    }

                    ushort flags = ReadUInt16(header, 8);
                    ushort method = ReadUInt16(header, 10);
                    uint compressedSize = ReadUInt32(header, 20);
                    ushort nameLength = ReadUInt16(header, 28);
                    ushort extraLength = ReadUInt16(header, 30);
                    ushort commentLength = ReadUInt16(header, 32);
                    uint localOffset = ReadUInt32(header, 42);

                    byte[] nameAndExtra = reader.ReadExactly(position + 46, nameLength + extraLength);
                    position += 46 + nameLength + extraLength + commentLength;

                    if ((flags & 1) == 0 || method != 99)
                    {
                        continue;
                    }

                    if (compressedSize == uint.MaxValue || localOffset == uint.MaxValue)
                    {
                        // ZIP64：首期不支持，交给 CPU
                        return false;
                    }

                    AesExtra? aes = ParseAesExtra(nameAndExtra.AsSpan(nameLength, extraLength));

                    if (!TryReadLocalEntry(
                            reader,
                            localOffset,
                            aes,
                            compressedSize,
                            out AesExtra? resolvedAes,
                            out long dataOffset) || resolvedAes is null)
                    {
                        continue;
                    }

                    int saltLength = resolvedAes.Strength switch
                    {
                        1 => 8,
                        2 => 12,
                        3 => 16,
                        _ => 0,
                    };

                    if (saltLength == 0)
                    {
                        continue;
                    }

                    long cipherLength = (long)compressedSize - saltLength - 2 - 10;
                    if (cipherLength < 0 || cipherLength > MaxCipherBytes)
                    {
                        return false;
                    }

                    int total = saltLength + 2 + (int)cipherLength + 10;
                    byte[] payload = reader.ReadExactly(dataOffset, total);

                    string salt = Convert.ToHexString(payload, 0, saltLength).ToLowerInvariant();
                    string verify = Convert.ToHexString(payload, saltLength, 2).ToLowerInvariant();
                    string cipher = Convert.ToHexString(payload, saltLength + 2, (int)cipherLength).ToLowerInvariant();
                    string auth = Convert.ToHexString(payload, saltLength + 2 + (int)cipherLength, 10).ToLowerInvariant();

                    hashLine = $"$zip2$*0*{resolvedAes.Strength}*0*{salt}*{verify}*{cipherLength:x}*{cipher}*{auth}*$/zip2$";
                    reason = string.Empty;
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                // 截断/损坏的 ZIP 一律回退 CPU，不在 GPU 路径上抛给用户
                reason = Resources.GpuFormatUnsupported;
                return false;
            }
        }

        private static bool TryFindEocd(VolumeReader reader, out long centralOffset, out long centralSize, out int entries)
        {
            centralOffset = 0;
            centralSize = 0;
            entries = 0;

            const int MaxEocdWindow = 65_557; // 22 + 65535 comment
            int window = (int)Math.Min(reader.Length, MaxEocdWindow);
            long windowStart = reader.Length - window;
            byte[] tail = reader.ReadExactly(windowStart, window);

            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (ReadUInt32(tail, i) != EocdSignature)
                {
                    continue;
                }

                ushort commentLength = ReadUInt16(tail, i + 20);
                if (i + 22 + commentLength > tail.Length)
                {
                    continue;
                }

                uint size = ReadUInt32(tail, i + 12);
                uint offset = ReadUInt32(tail, i + 16);
                ushort count = ReadUInt16(tail, i + 10);

                if (size == uint.MaxValue || offset == uint.MaxValue || count == ushort.MaxValue)
                {
                    return false; // ZIP64
                }

                if ((long)offset + size > reader.Length)
                {
                    continue;
                }

                centralOffset = offset;
                centralSize = size;
                entries = count;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 读取本地文件头以定位加密数据起点。AES extra field 正常情况下在中央目录里，
        /// 个别实现只写在本地头中，因此两者都尝试。
        /// </summary>
        private static bool TryReadLocalEntry(
            VolumeReader reader,
            long localOffset,
            AesExtra? centralAes,
            uint compressedSize,
            out AesExtra? aes,
            out long dataOffset)
        {
            aes = centralAes;
            dataOffset = 0;

            byte[] localHeader = reader.ReadExactly(localOffset, 30);
            if (ReadUInt32(localHeader, 0) != LocalSignature)
            {
                return false;
            }

            ushort nameLength = ReadUInt16(localHeader, 26);
            ushort extraLength = ReadUInt16(localHeader, 28);

            if (extraLength > 0)
            {
                byte[] localExtra = reader.ReadExactly(localOffset + 30 + nameLength, extraLength);
                aes ??= ParseAesExtra(localExtra) ?? default!;
            }

            if (aes is null)
            {
                return false;
            }

            dataOffset = localOffset + 30 + nameLength + extraLength;

            if (compressedSize == 0 || dataOffset + compressedSize > reader.Length)
            {
                return false;
            }

            return true;
        }

        private static AesExtra? ParseAesExtra(ReadOnlySpan<byte> extra)
        {
            int offset = 0;

            while (offset + 4 <= extra.Length)
            {
                ushort id = ReadUInt16(extra, offset);
                ushort size = ReadUInt16(extra, offset + 2);
                offset += 4;

                if (offset + size > extra.Length)
                {
                    return null;
                }

                if (id == AesExtraId && size >= 7)
                {
                    ushort version = ReadUInt16(extra, offset);
                    if (extra[offset + 2] != (byte)'A' || extra[offset + 3] != (byte)'E')
                    {
                        return null;
                    }

                    byte strength = extra[offset + 4];
                    if (strength is < 1 or > 3)
                    {
                        return null;
                    }

                    ushort actualMethod = ReadUInt16(extra, offset + 5);
                    return new AesExtra(version, strength, actualMethod);
                }

                offset += size;
            }

            return null;
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));

        private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));

        private sealed record AesExtra(ushort Version, byte Strength, ushort ActualMethod);
    }
}
