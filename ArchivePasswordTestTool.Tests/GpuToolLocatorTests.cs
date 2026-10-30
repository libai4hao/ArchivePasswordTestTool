namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// GPU 工具查找规则：程序目录优先，其次一级子目录；不递归更深层级。
/// 不启动真实 hashcat，只用空文件验证路径选择。
/// </summary>
public sealed class GpuToolLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ApttGpuTools-" + Guid.NewGuid().ToString("N"));

    public GpuToolLocatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    [Fact]
    public void FindsHashcatNextToProgram()
    {
        string expected = CreateFile("hashcat.exe");

        Assert.Equal(expected, GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void FindsHashcatInFirstLevelSubdirectory()
    {
        string expected = CreateFile(Path.Combine("hashcat-6.2.6", "hashcat.exe"));

        Assert.Equal(expected, GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void DirectDirectoryWinsOverSubdirectory()
    {
        string expected = CreateFile("hashcat.exe");
        CreateFile(Path.Combine("hashcat-6.2.6", "hashcat.exe"));

        Assert.Equal(expected, GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void SubdirectoriesAreSearchedInNameOrder()
    {
        CreateFile(Path.Combine("hashcat-b", "hashcat.exe"));
        string expected = CreateFile(Path.Combine("hashcat-a", "hashcat.exe"));

        Assert.Equal(expected, GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void DoesNotSearchDeeperThanOneLevel()
    {
        CreateFile(Path.Combine("tools", "hashcat-6.2.6", "hashcat.exe"));

        Assert.Null(GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void MissingHashcatReturnsNull()
    {
        CreateFile("readme.txt");

        Assert.Null(GpuToolLocator.FindHashcat(_root));
    }

    [Fact]
    public void ExactExtractorNameWinsOverWildcard()
    {
        CreateFile(Path.Combine("sub", "7z2hashcat64-1.9.exe"));
        string expected = CreateFile(Path.Combine("hashcat-6.2.6", "7z2hashcat64-2.0.exe"));

        Assert.Equal(expected, GpuToolLocator.FindSevenZipExtractor(_root));
    }

    [Fact]
    public void ExtractorWildcardCoversOtherVersionNames()
    {
        string expected = CreateFile(Path.Combine("sub", "7z2hashcat64.exe"));

        Assert.Equal(expected, GpuToolLocator.FindSevenZipExtractor(_root));
    }

    [Fact]
    public void MissingExtractorReturnsNull()
    {
        CreateFile("hashcat.exe");

        Assert.Null(GpuToolLocator.FindSevenZipExtractor(_root));
    }
}
