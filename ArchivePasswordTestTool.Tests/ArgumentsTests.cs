namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 多值参数与拖放路径拆分（迭代 01）：重复 -D/-F 必须按出现顺序返回，
/// 一次拖入多个文件产生的带引号字符串必须拆成多个路径。
/// </summary>
public sealed class ArgumentsTests
{
    [Fact]
    public void GetParametersReturnsEveryOccurrenceInOrder()
    {
        string[] args = ["-D", "a.txt", "-D", "b.txt", "-F", "x.7z"];

        Assert.Equal(new[] {"a.txt", "b.txt"}, Arguments.GetParameters(args, "D"));
        Assert.Equal(new[] {"x.7z"}, Arguments.GetParameters(args, "F"));
    }

    [Fact]
    public void GetParametersIsCaseInsensitive()
    {
        Assert.Equal(new[] {"a.txt"}, Arguments.GetParameters(["-d", "a.txt"], "D"));
    }

    [Fact]
    public void GetParametersIgnoresTrailingFlagWithoutValue()
    {
        Assert.Equal(new[] {"a.txt"}, Arguments.GetParameters(["-D", "a.txt", "-D"], "D"));
    }

    [Fact]
    public void SplitPathsSplitsQuotedDragDrop()
    {
        Assert.Equal(
            new[] { @"C:\a 1.7z", @"C:\b.7z" },
            Arguments.SplitPaths("\"C:\\a 1.7z\" \"C:\\b.7z\""));
    }

    [Fact]
    public void SplitPathsKeepsUnquotedPathWithSpacesTogether()
    {
        Assert.Equal(new[] {@"C:\a b.7z"}, Arguments.SplitPaths(@"C:\a b.7z"));
    }

    [Fact]
    public void SplitPathsHandlesMixedQuotedAndUnquoted()
    {
        Assert.Equal(
            new[] { @"C:\a.7z", @"D:\folder" },
            Arguments.SplitPaths("\"C:\\a.7z\" D:\\folder"));
    }

    [Fact]
    public void SplitPathsReturnsEmptyForBlankInput()
    {
        Assert.Empty(Arguments.SplitPaths("   "));
    }
}
