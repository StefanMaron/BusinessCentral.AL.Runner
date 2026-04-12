using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class SourceFileMapperTests
{
    public SourceFileMapperTests()
    {
        SourceFileMapper.Clear();
    }

    [Fact]
    public void Register_GetFile_RoundTrip()
    {
        SourceFileMapper.Register("Loop Helper", "src/LoopHelper.al");
        Assert.Equal("src/LoopHelper.al", SourceFileMapper.GetFile("Loop Helper"));
    }

    [Fact]
    public void GetFile_UnknownObject_ReturnsNull()
    {
        Assert.Null(SourceFileMapper.GetFile("Nonexistent"));
    }

    [Fact]
    public void Clear_RemovesAllRegistrations()
    {
        SourceFileMapper.Register("Foo", "Foo.al");
        SourceFileMapper.Clear();
        Assert.Null(SourceFileMapper.GetFile("Foo"));
    }

    [Fact]
    public void MultipleObjects_SameFile()
    {
        SourceFileMapper.Register("Helper", "src/Multi.al");
        SourceFileMapper.Register("Utils", "src/Multi.al");
        Assert.Equal("src/Multi.al", SourceFileMapper.GetFile("Helper"));
        Assert.Equal("src/Multi.al", SourceFileMapper.GetFile("Utils"));
    }

    [Fact]
    public void GetFileForScope_ResolvesChain()
    {
        SourceFileMapper.Register("Loop Helper", "src/LoopHelper.al");
        var scopeToObject = new Dictionary<string, string>
        {
            ["Codeunit50020_Scope"] = "Loop Helper"
        };
        Assert.Equal("src/LoopHelper.al", SourceFileMapper.GetFileForScope("Codeunit50020_Scope", scopeToObject));
    }

    [Fact]
    public void GetFileForScope_UnknownScope_ReturnsNull()
    {
        var scopeToObject = new Dictionary<string, string>();
        Assert.Null(SourceFileMapper.GetFileForScope("Unknown_Scope", scopeToObject));
    }

    [Fact]
    public void GetFileForScope_ScopeKnownButObjectNotRegistered_ReturnsNull()
    {
        var scopeToObject = new Dictionary<string, string>
        {
            ["Codeunit50020_Scope"] = "Loop Helper"
        };
        Assert.Null(SourceFileMapper.GetFileForScope("Codeunit50020_Scope", scopeToObject));
    }
}
