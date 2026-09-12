// UnobtainableDotNetTypeAttributionTests — #3890.
//
// CLAIM: some AL0185 emit-exclusions name a .NET type that ships in NO Business Central
// artifact, so the drop is permanent and identical on every run. The report has to say which,
// or every reader re-investigates a settled question; and it must say so ONLY for types a scan
// actually cleared, so an unknown drop is never dressed up as a known one.
//
// CITATION: docs/limitations.md#unobtainable-dotnet-types carries the scan — 0 of 501 DLLs on
// two independent binary sets, positive control FileExtensionContentTypeProvider = 3 — and the
// reason it cannot be staged: nothing declares an assembly() block for it.
//
// TRAP, for whoever edits this next: the attribution must key on the TYPE NAME the diagnostic
// quotes, never on the codeunit or file. One type is referenced by four Microsoft test apps, so
// keying on the object would explain one drop and leave three unexplained.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class UnobtainableDotNetTypeAttributionTests
{
    private const string RealDiagnostic =
        "SourceFile(/tmp/deps/Microsoft_Tests-TestLibraries/LibraryAzureKVMockMgmt.Codeunit.al@10:42): "
        + "error AL0185: DotNet 'MockAzureKeyVaultSecretProvider' is missing";

    /// <summary>The positive case, verbatim from a real run (BC 28.1, manual-binding-dep).</summary>
    [Fact]
    public void TheRealDiagnostic_IsAttributedToTheDocumentedRecord()
    {
        var s = DependencyLoader.AttributeToUnobtainableType(new[] { RealDiagnostic });

        Assert.NotNull(s);
        Assert.Contains("MockAzureKeyVaultSecretProvider", s);
        Assert.Contains("ships in none of Business Central's artifacts", s);
        Assert.Contains("docs/limitations.md#unobtainable-dotnet-types", s);
        // The reader's actual question is "is this mine to fix?" — the answer must be explicit.
        Assert.Contains("rather than a provisioning gap", s);
    }

    /// <summary>
    /// The negative that matters most: an AL0185 for ANY other type stays unattributed, because
    /// an unrecognised missing type may well be fixable (#3876 staged one). Attributing broadly
    /// would tell a reader to stop looking at a real, solvable gap.
    /// </summary>
    [Fact]
    public void ADifferentMissingType_IsNotAttributed()
    {
        var other = RealDiagnostic.Replace(
            "MockAzureKeyVaultSecretProvider", "FileExtensionContentTypeProvider");

        Assert.Null(DependencyLoader.AttributeToUnobtainableType(new[] { other }));
    }

    /// <summary>
    /// A diagnostic naming the type for a DIFFERENT reason than AL0185 is not this record.
    /// Without the code check, any message quoting the name would claim the documented verdict.
    /// </summary>
    [Fact]
    public void TheSameTypeUnderADifferentDiagnosticCode_IsNotAttributed()
    {
        var notAl0185 = RealDiagnostic.Replace("AL0185", "AL0432");

        Assert.Null(DependencyLoader.AttributeToUnobtainableType(new[] { notAl0185 }));
    }

    /// <summary>A substring of the name must not match: the quotes are part of the key.</summary>
    [Fact]
    public void APrefixOfTheTypeName_IsNotAttributed()
    {
        var prefixed = RealDiagnostic.Replace(
            "'MockAzureKeyVaultSecretProvider'", "'MockAzureKeyVaultSecretProviderFactory'");

        Assert.Null(DependencyLoader.AttributeToUnobtainableType(new[] { prefixed }));
    }

    [Fact]
    public void NoDiagnosticsAtAll_IsNotAttributed()
    {
        Assert.Null(DependencyLoader.AttributeToUnobtainableType(Array.Empty<string>()));
        Assert.Null(DependencyLoader.AttributeToUnobtainableType(new string?[] { null }!));
    }

    /// <summary>
    /// The attribution has to reach the message the run actually prints — the unit above proves
    /// the helper, this proves it is WIRED. Also pins that the generic text survives: the
    /// denominator (#3875's "of 70") is what makes the drop a finding at all.
    /// </summary>
    [Fact]
    public void TheFullDependencyMessage_CarriesBothTheDenominatorAndTheAttribution()
    {
        var msg = DependencyLoader.BuildDependencyEmitExcludedDetail(
            new[] { "Codeunit .\"Library - Azure KV Mock Mgmt.\"" }, 202, new[] { RealDiagnostic });

        Assert.Contains("1 of this dependency's 203 object(s)", msg);
        Assert.Contains("provides only 202", msg);
        Assert.Contains("docs/limitations.md#unobtainable-dotnet-types", msg);
    }

    /// <summary>An ordinary partial emit must NOT gain the attribution sentence.</summary>
    [Fact]
    public void AnUnrelatedPartialEmit_GainsNoAttributionSentence()
    {
        var msg = DependencyLoader.BuildDependencyEmitExcludedDetail(
            new[] { "Codeunit .\"Something Else\"" }, 9,
            new[] { "error AL0185: DotNet 'SomeOtherType' is missing" });

        Assert.Contains("1 of this dependency's 10 object(s)", msg);
        Assert.DoesNotContain("unobtainable-dotnet-types", msg);
    }

    /// <summary>
    /// Every listed type must be recorded in docs/limitations.md, or the message cites an anchor
    /// whose table does not mention it. A list entry asserts a scan was run; this is what keeps
    /// the assertion honest as the list grows.
    /// </summary>
    [Fact]
    public void EveryListedType_AppearsUnderTheDocumentedAnchor()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "limitations.md"));
        var section = doc[doc.IndexOf("unobtainable-dotnet-types", StringComparison.Ordinal)..];

        Assert.NotEmpty(DependencyLoader.UnobtainableDotNetTypes);
        foreach (var t in DependencyLoader.UnobtainableDotNetTypes)
            Assert.Contains(t, section);
    }

    // The four-up idiom every other source-reading test here uses. NOT a .git walk: in a git
    // WORKTREE .git is a file, so Directory.Exists never matches and the walk silently runs off
    // to a parent checkout -- reading a stale docs/limitations.md that has none of this branch's
    // edits. Measured while writing this test (#3890).
    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
