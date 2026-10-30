using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// ZIP AES 哈希提取（hashcat $zip2$）的结构测试。不依赖 hashcat：
/// GPU 集成测试在 EndToEndTests 中按工具是否存在跳过。
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class HashExtractionTests(TestDataFixture fx)
{
    [Fact]
    public void AesZipProducesZip2Hash()
    {
        bool ok = ZipAesHashReader.TryRead(fx.Archive("SecretZipAes.zip"), out string hash, out string reason);

        Assert.True(ok, reason);
        Assert.StartsWith("$zip2$*0*3*0*", hash, StringComparison.Ordinal);
        Assert.EndsWith("*$/zip2$", hash, StringComparison.Ordinal);

        string[] fields = hash.Split('*');

        // $zip2$ * 0 * mode * 0 * salt * verify * cipherLen * cipher * auth * $/zip2$
        Assert.Equal(10, fields.Length);
        Assert.Equal(32, fields[4].Length);   // AES-256 salt = 16 bytes
        Assert.Equal(4, fields[5].Length);    // password verifier = 2 bytes
        Assert.Equal(20, fields[8].Length);   // authentication code = 10 bytes

        int cipherLength = Convert.ToInt32(fields[6], 16);
        Assert.True(cipherLength > 0);
        Assert.Equal(cipherLength * 2, fields[7].Length);
    }

    [Fact]
    public void ZipCryptoIsNotAcceptedAsAes()
    {
        Assert.False(ZipAesHashReader.TryRead(fx.Archive("SecretZip.zip"), out _, out _));
    }

    /// <summary>
    /// $7z$ 的 data_type 是「加密头解密后的压缩方法」，不是「是否 header 加密」。
    /// 4.7z 这类归档输出 type 1（LZMA1），hashcat 11600 完全支持；
    /// 只接受 0 会让这类包被误判为「格式不支持 GPU」。
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("7")]
    public void SevenZipDataTypeSupportedByHashcatIsAccepted(string dataType)
    {
        string hash = SevenZipHash(dataType);

        bool ok = ArchiveHashExtractor.TryParseSevenZipHash(hash, out GpuHashJob job, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(11600, job.Mode);
        Assert.Equal(hash, job.HashLine);
    }

    /// <summary>0x80 = 截断，hashcat 不接受；其余未登记类型同样拒绝</summary>
    [Theory]
    [InlineData("128")]
    [InlineData("3")]
    [InlineData("")]
    public void UnsupportedSevenZipDataTypeIsRejected(string dataType)
    {
        bool ok = ArchiveHashExtractor.TryParseSevenZipHash(SevenZipHash(dataType), out _, out string reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    /// <summary>没有 $7z$ 行时返回空原因，由调用方补「提取失败 + stderr」</summary>
    [Fact]
    public void MissingSevenZipHashLeavesReasonForCaller()
    {
        Assert.False(ArchiveHashExtractor.TryParseSevenZipHash(
            "WARNING: nothing here\n", out _, out string reason));
        Assert.Empty(reason);
    }

    private static string SevenZipHash(string dataType) =>
        $"$7z${dataType}$19$0$$16$00112233445566778899aabbccddeeff$123456789$32$16$00112233445566778899aabbccddeeff$";
}
