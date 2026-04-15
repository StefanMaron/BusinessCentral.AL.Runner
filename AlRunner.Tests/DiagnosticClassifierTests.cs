using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class DiagnosticClassifierTests
{
    // Real message format from AL compiler:
    // 'ObjName' is an ambiguous reference between 'ObjName' defined by the extension
    // 'AppName by Publisher (Version)' and 'AppName by Publisher (Version)'.

    private const string SelfDuplicateMessage =
        "'My Object' is an ambiguous reference between " +
        "'My Object' defined by the extension " +
        "'My App by My Publisher (1.0.0.0)' and " +
        "'My Object' defined by the extension " +
        "'My App by My Publisher (1.0.0.0)'.";

    private const string GenuineAmbiguityMessage =
        "'My Object' is an ambiguous reference between " +
        "'My Object' defined by the extension " +
        "'App Alpha by Publisher A (1.0.0.0)' and " +
        "'My Object' defined by the extension " +
        "'App Beta by Publisher B (1.0.0.0)'.";

    private const string SameNameDifferentVersionMessage =
        "'My Object' is an ambiguous reference between " +
        "'My Object' defined by the extension " +
        "'My App by My Publisher (1.0.0.0)' and " +
        "'My Object' defined by the extension " +
        "'My App by My Publisher (2.0.0.0)'.";

    // --- IsSelfDuplicateAmbiguity ---

    [Fact]
    public void IsSelfDuplicateAmbiguity_IdenticalExtensions_ReturnsTrue()
    {
        Assert.True(DiagnosticClassifier.IsSelfDuplicateAmbiguity(SelfDuplicateMessage));
    }

    [Fact]
    public void IsSelfDuplicateAmbiguity_DifferentExtensions_ReturnsFalse()
    {
        Assert.False(DiagnosticClassifier.IsSelfDuplicateAmbiguity(GenuineAmbiguityMessage));
    }

    [Fact]
    public void IsSelfDuplicateAmbiguity_SameNameDifferentVersion_ReturnsFalse()
    {
        // Same publisher+name but different version is NOT a self-duplicate
        Assert.False(DiagnosticClassifier.IsSelfDuplicateAmbiguity(SameNameDifferentVersionMessage));
    }

    [Fact]
    public void IsSelfDuplicateAmbiguity_MalformedMessage_ReturnsFalse()
    {
        Assert.False(DiagnosticClassifier.IsSelfDuplicateAmbiguity("some unrelated error"));
        Assert.False(DiagnosticClassifier.IsSelfDuplicateAmbiguity(""));
        Assert.False(DiagnosticClassifier.IsSelfDuplicateAmbiguity(
            "'Foo' is an ambiguous reference between 'Foo' defined by the extension 'Only One Extension (1.0)'."));
    }

    [Fact]
    public void IsSelfDuplicateAmbiguity_CaseInsensitive_ReturnsTrue()
    {
        var msg = "'Obj' is an ambiguous reference between " +
                  "'Obj' defined by the extension 'My App by ACME (1.0.0.0)' and " +
                  "'Obj' defined by the extension 'My App by Acme (1.0.0.0)'.";
        Assert.True(DiagnosticClassifier.IsSelfDuplicateAmbiguity(msg));
    }

    // --- ExtractAmbiguityExtensionIds ---

    [Fact]
    public void ExtractAmbiguityExtensionIds_ParsesBothSides()
    {
        var result = DiagnosticClassifier.ExtractAmbiguityExtensionIds(SelfDuplicateMessage);

        Assert.NotNull(result);
        Assert.Equal("My App by My Publisher (1.0.0.0)", result!.Value.Left);
        Assert.Equal("My App by My Publisher (1.0.0.0)", result.Value.Right);
    }

    [Fact]
    public void ExtractAmbiguityExtensionIds_GenuineAmbiguity_ParsesBothSides()
    {
        var result = DiagnosticClassifier.ExtractAmbiguityExtensionIds(GenuineAmbiguityMessage);

        Assert.NotNull(result);
        Assert.Equal("App Alpha by Publisher A (1.0.0.0)", result!.Value.Left);
        Assert.Equal("App Beta by Publisher B (1.0.0.0)", result.Value.Right);
    }

    [Fact]
    public void ExtractAmbiguityExtensionIds_MalformedMessage_ReturnsNull()
    {
        Assert.Null(DiagnosticClassifier.ExtractAmbiguityExtensionIds("unrelated error"));
        Assert.Null(DiagnosticClassifier.ExtractAmbiguityExtensionIds(""));
    }

    // --- IsCrossExtensionAmbiguity ---

    [Fact]
    public void IsCrossExtensionAmbiguity_DifferentExtensions_ReturnsTrue()
    {
        Assert.True(DiagnosticClassifier.IsCrossExtensionAmbiguity(GenuineAmbiguityMessage));
    }

    [Fact]
    public void IsCrossExtensionAmbiguity_SameExtension_ReturnsFalse()
    {
        Assert.False(DiagnosticClassifier.IsCrossExtensionAmbiguity(SelfDuplicateMessage));
    }

    [Fact]
    public void IsCrossExtensionAmbiguity_SameNameDifferentVersion_ReturnsTrue()
    {
        // Different version counts as different extension
        Assert.True(DiagnosticClassifier.IsCrossExtensionAmbiguity(SameNameDifferentVersionMessage));
    }

    [Fact]
    public void IsCrossExtensionAmbiguity_MalformedMessage_ReturnsFalse()
    {
        Assert.False(DiagnosticClassifier.IsCrossExtensionAmbiguity("unrelated error"));
        Assert.False(DiagnosticClassifier.IsCrossExtensionAmbiguity(""));
    }

    // --- IsCrossExtensionDuplicateDeclaration ---

    [Fact]
    public void IsCrossExtensionDuplicateDeclaration_AlreadyDeclaredBy_ReturnsTrue()
    {
        var msg = "An application object of type 'PageExtension' with name 'ItemCardExt' " +
                  "is already declared by the extension 'AppAlpha by Publisher A (1.0.0.0)'";
        Assert.True(DiagnosticClassifier.IsCrossExtensionDuplicateDeclaration(msg));
    }

    [Fact]
    public void IsCrossExtensionDuplicateDeclaration_UnrelatedMessage_ReturnsFalse()
    {
        Assert.False(DiagnosticClassifier.IsCrossExtensionDuplicateDeclaration("some other error"));
        Assert.False(DiagnosticClassifier.IsCrossExtensionDuplicateDeclaration(SelfDuplicateMessage));
    }
}
