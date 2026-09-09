using System.Reflection;
using System.Text.RegularExpressions;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3574 — the enum-typed-field fix-up matches the field's raw AL source spelling, and AL
/// keywords are case-insensitive, so <c>enum "X"</c> and <c>ENUM "X"</c> must resolve exactly
/// as <c>Enum "X"</c> does.
///
/// <para>Why the failure was easy to miss, and why this test is worth its line: the field's
/// runtime TYPE was never wrong. <c>MapNavType</c> uppercases before comparing, so a
/// lowercase-declared enum field still became <c>NavType.Option</c> and ordinary reads of it
/// worked. Only the ordinal-keyed option metadata went missing, which surfaces much later and
/// somewhere else — measured on a probe bundle, as
/// <c>GetEnumValueNameFromOrdinalValue</c> raising "An object with that ID does not exist",
/// and on the corpus fixture as the whole bundle aborting with
/// <c>NavMetadataNotFoundException</c> out of the Field virtual table.</para>
///
/// <para>The AL-observable claim — that all three spellings produce identical enum metadata —
/// is adjudicated by a real service tier in corpus PR #304
/// (<c>.claude/rules/bc-behavior-tests-go-upstream.md</c>). What is pinned here is the runner's
/// own pattern, because a regex is where the defect was and where a regression would
/// reappear.</para>
/// </summary>
public class EnumTypeKeywordCaseTests
{
    /// <summary>
    /// The live pattern, read off the field rather than restated here. A copy of the regex
    /// would keep passing after someone edited the real one, which is the one thing this test
    /// must not do.
    /// </summary>
    private static Regex EnumTypeNameRegex()
    {
        var field = typeof(RecordPatches).GetField(
            "_rxEnumTypeName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<Regex>(field!.GetValue(null));
    }

    private static string? MatchedEnumName(string typeName)
    {
        var m = EnumTypeNameRegex().Match(typeName);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    [Theory]
    // The canonical spelling — the control. It passed before the fix, and a change that broke
    // it while fixing the others would be a regression this row catches.
    [InlineData("Enum \"ALT Status\"")]
    [InlineData("enum \"ALT Status\"")]
    [InlineData("ENUM \"ALT Status\"")]
    // Mixed case is legal AL too; nothing about the keyword is position-sensitive.
    [InlineData("EnUm \"ALT Status\"")]
    public void EveryCapitalizationOfTheEnumKeyword_ResolvesTheSameQuotedEnumName(string typeName)
        => Assert.Equal("ALT Status", MatchedEnumName(typeName));

    [Theory]
    [InlineData("Enum MyStatus")]
    [InlineData("enum MyStatus")]
    [InlineData("ENUM MyStatus")]
    public void EveryCapitalizationOfTheEnumKeyword_ResolvesTheSameUnquotedEnumName(string typeName)
        => Assert.Equal("MyStatus", MatchedEnumName(typeName));

    [Theory]
    // A quoted name may hold spaces and punctuation; the fix must not have narrowed that.
    [InlineData("enum \"Name With Spaces\"", "Name With Spaces")]
    [InlineData("ENUM \"Name-With-Dashes\"", "Name-With-Dashes")]
    // Leading and trailing whitespace is what ParseFieldSyntax's raw `f.Type.ToString()` can
    // carry, so the pattern anchors around it rather than assuming a trimmed value.
    [InlineData("  enum \"Padded\"  ", "Padded")]
    public void QuotedAndPaddedEnumNames_StillParse_AfterTheCaseRelaxation(string typeName, string expected)
        => Assert.Equal(expected, MatchedEnumName(typeName));

    [Theory]
    // The negative direction. IgnoreCase relaxes the KEYWORD's case, and nothing else: a type
    // whose name merely starts with those four letters is not an enum declaration, and neither
    // is a bare `Enum` with no name. Without these rows the fix could have been "match
    // anything containing enum" and every row above would still pass.
    [InlineData("Enumerable")]
    [InlineData("enumeration \"X\"")]
    [InlineData("Enum")]
    [InlineData("Option")]
    [InlineData("Text[50]")]
    [InlineData("Integer")]
    public void ATypeThatIsNotAnEnumDeclaration_DoesNotMatch(string typeName)
        => Assert.Null(MatchedEnumName(typeName));
}
