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
}
