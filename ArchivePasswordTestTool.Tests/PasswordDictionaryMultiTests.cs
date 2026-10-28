namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 多字典语义（迭代 01）：按传入顺序合并、跨文件去重、
/// Reset 后重新服务全部密码，在线密码每轮重新插到队首。
/// </summary>
public sealed class PasswordDictionaryMultiTests : IDisposable
{
    private readonly string _dir;
    private readonly string _first;
    private readonly string _second;

    public PasswordDictionaryMultiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ApttMultiDict{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        _first = Path.Combine(_dir, "first.txt");
        _second = Path.Combine(_dir, "second.txt");

        File.WriteAllLines(_first, ["one", "two", "shared"]);
        File.WriteAllLines(_second, ["shared", "three"]);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private static List<string> Drain(PasswordDictionary dictionary)
    {
        var consumed = new List<string>();
        while (dictionary.Pop() is { } password)
        {
            consumed.Add(password);
        }

        return consumed;
    }

    [Fact]
    public void MultipleFilesKeepOrderAndDeduplicateGlobally()
    {
        using var dictionary = new PasswordDictionary([_first, _second]);

        Assert.Equal(4, dictionary.Count);
        Assert.Equal(new[] {"one", "two", "shared", "three"}, Drain(dictionary));
        Assert.Equal(2, dictionary.Sources.Count);
        Assert.Equal(_first + " + " + _second, dictionary.DisplayPath);
    }

    [Fact]
    public void ResetReplaysEveryPasswordForTheNextArchive()
    {
        using var dictionary = new PasswordDictionary([_first, _second]);

        Assert.Equal(new[] {"one", "two", "shared", "three"}, Drain(dictionary));

        dictionary.Reset();

        Assert.Equal(4, dictionary.Count);
        Assert.Equal(new[] {"one", "two", "shared", "three"}, Drain(dictionary));
    }

    [Fact]
    public void OnlinePasswordIsReappliedAtTheFrontOnEachReset()
    {
        using var dictionary = new PasswordDictionary([_first, _second]);

        dictionary.Reset("online");
        Assert.Equal(5, dictionary.Count);
        Assert.Equal("online", dictionary.Pop());

        dictionary.Reset("two"); // 与文件内密码重复 → 只测一次，但排在最前
        Assert.Equal(4, dictionary.Count);
        Assert.Equal("two", dictionary.Pop());
        Assert.Equal("one", dictionary.Pop());
    }

    [Fact]
    public void EachSourceReportsItsOwnEncoding()
    {
        using var dictionary = new PasswordDictionary([_first, _second]);

        Assert.All(dictionary.Sources, source => Assert.Equal(65001, source.Encoding.CodePage));
        Assert.Equal(3, dictionary.Sources[0].AddedPasswordCount);
        Assert.Equal(1, dictionary.Sources[1].AddedPasswordCount);
    }
}
