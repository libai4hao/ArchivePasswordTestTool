namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 文件夹展开规则（迭代 01）：递归、扩展名白名单、签名确认、
/// 分卷只取 .001、隐藏/系统跳过、按完整路径去重与顺序保持。
/// </summary>
public sealed class ArchiveEnumeratorTests : IDisposable
{
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] NotAnArchive = [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x6E];

    private readonly string _dir;

    public ArchiveEnumeratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ApttEnum{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string Create(string relativePath, byte[] content)
    {
        string fullPath = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void ExplicitFilesKeepInputOrderAndAreDeduplicated()
    {
        string first = Create("a.7z", SevenZipSignature);
        string second = Create("b.zip", ZipSignature);

        var result = ArchiveEnumerator.Expand([first, second, first]);

        Assert.Equal(new[] {first, second}, result.Archives);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ExplicitUnrecognizedFileIsKeptSoTheUserSeesTheRealError()
    {
        string plain = Create("plain.7z", NotAnArchive);

        var result = ArchiveEnumerator.Expand([plain]);

        Assert.Equal(new[] {plain}, result.Archives);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ExplicitNonFirstVolumeIsSkipped()
    {
        Create("v.7z.001", SevenZipSignature);
        string second = Create("v.7z.002", SevenZipSignature);

        var result = ArchiveEnumerator.Expand([second]);

        Assert.Empty(result.Archives);
        Assert.Single(result.Skipped);
        Assert.Equal(ArchiveSkipReason.NonFirstVolume, result.Skipped[0].Reason);
    }

    [Fact]
    public void DirectoryScanRecursesFiltersAndSkips()
    {
        string first = Create("a.7z", SevenZipSignature);
        string volume = Create("c.7z.001", SevenZipSignature);
        string secondVolume = Create("c.7z.002", SevenZipSignature);
        string notArchive = Create("d.exe", NotAnArchive);
        string hidden = Create("hidden.7z", SevenZipSignature);
        Create("notes.txt", [0x68, 0x69]);
        string nested = Create(Path.Combine("sub", "b.zip"), ZipSignature);

        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = ArchiveEnumerator.Expand([_dir]);

        Assert.Equal(new[] {first, volume, nested}, result.Archives);

        Assert.Contains(result.Skipped, entry =>
            entry.Path == secondVolume && entry.Reason == ArchiveSkipReason.NonFirstVolume);
        Assert.Contains(result.Skipped, entry =>
            entry.Path == notArchive && entry.Reason == ArchiveSkipReason.Unrecognized);
        Assert.Contains(result.Skipped, entry =>
            entry.Path == hidden && entry.Reason == ArchiveSkipReason.HiddenOrSystem);
    }

    [Fact]
    public void WinRarPartVolumesOnlyIncludeTheFirstPart()
    {
        string firstPart = Create("r.part1.rar", Rar4Signature);
        string secondPart = Create("r.part2.rar", Rar4Signature);

        var result = ArchiveEnumerator.Expand([_dir]);

        Assert.Equal(new[] { firstPart }, result.Archives);
        Assert.Contains(result.Skipped, entry =>
            entry.Path == secondPart && entry.Reason == ArchiveSkipReason.NonFirstVolume);
    }

    [Fact]
    public void EmptyDirectoryYieldsNoArchives()
    {
        string empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);

        var result = ArchiveEnumerator.Expand([empty]);

        Assert.Empty(result.Archives);
        Assert.Empty(result.Skipped);
    }
}
