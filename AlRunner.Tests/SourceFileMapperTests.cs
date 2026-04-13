using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
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

    [Fact]
    public void GetFileForScope_PrefixMatch_BareMethodName()
    {
        SourceFileMapper.Register("CI Pipeline Tests", "test/Tests.al");
        var scopeToObject = new Dictionary<string, string>
        {
            ["ThreeAssignments_Scope_12345"] = "CI Pipeline Tests"
        };
        // Bare method name "ThreeAssignments" should match key starting with "ThreeAssignments_"
        Assert.Equal("test/Tests.al", SourceFileMapper.GetFileForScope("ThreeAssignments", scopeToObject));
    }

    [Fact]
    public void GetFileForScope_PrefixMatch_DoesNotMatchPartialName()
    {
        SourceFileMapper.Register("Helper", "src/Helper.al");
        var scopeToObject = new Dictionary<string, string>
        {
            ["HelperFunc_Scope_999"] = "Helper"
        };
        // "Helper" should NOT prefix-match "HelperFunc_Scope_999" because
        // the key starts with "HelperFunc_" not "Helper_"
        Assert.Null(SourceFileMapper.GetFileForScope("Helper", scopeToObject));
    }

    [Fact]
    public void GetFileForScope_ExactMatchPreferredOverPrefix()
    {
        SourceFileMapper.Register("Object A", "a.al");
        SourceFileMapper.Register("Object B", "b.al");
        var scopeToObject = new Dictionary<string, string>
        {
            ["MyScope"] = "Object A",
            ["MyScope_Scope_999"] = "Object B"
        };
        // Exact match "MyScope" → Object A should win over prefix match
        Assert.Equal("a.al", SourceFileMapper.GetFileForScope("MyScope", scopeToObject));
    }
}

[Collection("Pipeline")]
public class ClassToObjectMappingTests
{
    public ClassToObjectMappingTests()
    {
        SourceFileMapper.Clear();
    }

    [Fact]
    public void RegisterClass_GetObjectForClass_RoundTrip()
    {
        SourceFileMapper.RegisterClass("Codeunit50471", "CI Pipeline Tests");
        Assert.Equal("CI Pipeline Tests", SourceFileMapper.GetObjectForClass("Codeunit50471"));
    }

    [Fact]
    public void GetObjectForClass_UnknownClass_ReturnsFallback()
    {
        Assert.Equal("UnknownClass", SourceFileMapper.GetObjectForClass("UnknownClass"));
    }

    [Fact]
    public void Clear_AlsoResetsClassMappings()
    {
        SourceFileMapper.RegisterClass("Codeunit50471", "CI Pipeline Tests");
        SourceFileMapper.Clear();
        // Falls back to className after clear
        Assert.Equal("Codeunit50471", SourceFileMapper.GetObjectForClass("Codeunit50471"));
    }
}

public class AlDeclarationParsingTests
{
    [Fact]
    public void QuotedCodeunitName()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("codeunit 50 \"Loop Helper\"\n{\n}");
        Assert.Single(names);
        Assert.Equal("Loop Helper", names[0]);
    }

    [Fact]
    public void UnquotedCodeunitName()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("codeunit 50 LoopHelper\n{\n}");
        Assert.Single(names);
        Assert.Equal("LoopHelper", names[0]);
    }

    [Fact]
    public void TableDeclaration()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("table 100 \"My Table\"\n{\n}");
        Assert.Single(names);
        Assert.Equal("My Table", names[0]);
    }

    [Fact]
    public void EnumExtensionDeclaration()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("enumextension 50100 \"Status Ext\" extends Status\n{\n}");
        Assert.Single(names);
        Assert.Equal("Status Ext", names[0]);
    }

    [Fact]
    public void ProfileWithoutNumericId()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("profile \"My Profile\"\n{\n}");
        Assert.Single(names);
        Assert.Equal("My Profile", names[0]);
    }

    [Fact]
    public void ControlAddInWithoutNumericId()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("controladdin MyControl\n{\n}");
        Assert.Single(names);
        Assert.Equal("MyControl", names[0]);
    }

    [Fact]
    public void CaseInsensitiveKeyword()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("CODEUNIT 50 \"Foo\"\n{\n}");
        Assert.Single(names);
        Assert.Equal("Foo", names[0]);
    }

    [Fact]
    public void MultipleObjectsInOneFile()
    {
        var source = "codeunit 50 \"Helper\"\n{\n}\ntable 100 \"Data\"\n{\n}";
        var names = SourceFileMapper.ParseObjectDeclarations(source);
        Assert.Equal(2, names.Count);
        Assert.Contains("Helper", names);
        Assert.Contains("Data", names);
    }

    [Fact]
    public void NameInComment_NotMatched()
    {
        var source = "// codeunit 50 \"Fake\"\ncodeunit 51 \"Real\"\n{\n}";
        var names = SourceFileMapper.ParseObjectDeclarations(source);
        Assert.Single(names);
        Assert.Equal("Real", names[0]);
    }

    [Fact]
    public void NameInMessageCall_NotMatched()
    {
        var source = "codeunit 50 \"Real\"\n{\n  trigger OnRun() begin Message('codeunit 99 \"Fake\"'); end;\n}";
        var names = SourceFileMapper.ParseObjectDeclarations(source);
        Assert.Single(names);
        Assert.Equal("Real", names[0]);
    }

    [Fact]
    public void PageExtensionDeclaration()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("pageextension 50100 \"My Page Ext\" extends \"Customer Card\"\n{\n}");
        Assert.Single(names);
        Assert.Equal("My Page Ext", names[0]);
    }

    [Fact]
    public void EmptySource_ReturnsEmpty()
    {
        var names = SourceFileMapper.ParseObjectDeclarations("");
        Assert.Empty(names);
    }
}
