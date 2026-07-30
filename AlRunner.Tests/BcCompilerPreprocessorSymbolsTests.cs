// BcCompilerPreprocessorSymbolsTests — validates SetExtraPreprocessorSymbols
// and the Program.cs IsValidAlPreprocessorSymbol helper.
//
// RED before the feature: BcCompiler had no SetExtraPreprocessorSymbols method;
// Program.cs had no --define / --preprocessor-symbols flags; no way for callers
// to inject symbols.
// GREEN after: symbols are collected, validated, and merged at both ParseOptions
// sites (Emit and EmitDepSymbols) without dropping CLEANSCHEMA1..25.

using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class BcCompilerPreprocessorSymbolsTests
{
    // ── IsValidAlPreprocessorSymbol (white-box via reflection) ────────────────

    private static bool IsValid(string sym) => BcCompiler.IsValidPreprocessorSymbol(sym);

    [Theory]
    [InlineData("MY_SYM")]
    [InlineData("MY_TEST_SYMBOL")]
    [InlineData("ABC")]
    [InlineData("_underscore")]
    [InlineData("X1")]
    [InlineData("CLEANSCHEMA1")]
    [InlineData("a")]
    public void ValidSymbols_ReturnTrue(string sym) => Assert.True(IsValid(sym));

    [Theory]
    [InlineData("")]
    [InlineData("1STARTS_DIGIT")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("has@at")]
    public void InvalidSymbols_ReturnFalse(string sym) => Assert.False(IsValid(sym));

    // ── SetExtraPreprocessorSymbols / GetExtraPreprocessorSymbols (state) ────

    [Fact]
    public void SetExtraPreprocessorSymbols_StoredAndReadBack()
    {
        var symbols = new List<string> { "MY_SYM", "ANOTHER_SYM" };
        BcCompiler.SetExtraPreprocessorSymbols(symbols);

        // Verify via reflection that the private static field holds our list.
        var field = typeof(BcCompiler).GetField(
            "_extraPreprocessorSymbols",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!;
        var stored = (IReadOnlyList<string>)field.GetValue(null)!;

        Assert.Equal(symbols, stored);
    }

    [Fact]
    public void CleanSchema_NotPresentInExtraSymbols_MergedAtParseTime()
    {
        // After SetExtraPreprocessorSymbols, the static field should contain
        // ONLY caller symbols — CLEANSCHEMA merging happens inside Emit/EmitDepSymbols
        // at ParseOptions construction time, not in the field itself.
        BcCompiler.SetExtraPreprocessorSymbols(["MY_SYM"]);
        var field = typeof(BcCompiler).GetField(
            "_extraPreprocessorSymbols",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!;
        var stored = (IReadOnlyList<string>)field.GetValue(null)!;

        Assert.DoesNotContain("CLEANSCHEMA1", stored);
        Assert.Contains("MY_SYM", stored);
    }
}
