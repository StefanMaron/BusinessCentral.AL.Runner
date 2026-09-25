// TestDataCompanyPrefixTests — the proving tests for #4553: --test-data-company takes a
// case-insensitive name prefix, and an unquoted multi-word value that spilled into the
// positional arguments is diagnosed as such.
//
// Runner CLI behaviour only, nothing about Business Central, so these stay here rather than
// in the corpus. The matcher is driven with a company list, never a restored backup, so no
// test here needs the Base Application floor (no-base-app-in-csharp-tests.md).
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(TestDataStaticsSerialCollection.Name)]
public sealed class TestDataCompanyPrefixTests
{
    private static readonly string[] W1 = { "CRONUS International Ltd_", "My Company" };

    private static string Resolve(IReadOnlyList<string> companies, string? name,
        CompanyPrompt? prompt, out string notices)
    {
        var w = new StringWriter();
        var company = TestDataProvisioner.ResolveCompany(companies, name, "/x/BusinessCentral-W1.bak", prompt, w);
        notices = w.ToString();
        return company;
    }

    // ── unique prefix ──

    [Fact]
    public void UniquePrefix_SelectsThatCompany_AndSaysWhich()
    {
        Assert.Equal("CRONUS International Ltd_", Resolve(W1, "cronus", null, out var notices));
        Assert.Contains("'cronus'", notices, StringComparison.Ordinal);
        Assert.Contains("'CRONUS International Ltd_'", notices, StringComparison.Ordinal);
        Assert.Single(notices.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Prefix_IsCaseInsensitive_InBothDirections()
    {
        Assert.Equal("My Company", Resolve(W1, "MY c", null, out _));
        Assert.Equal("My Company", Resolve(W1, "my COMPANY", null, out _));
    }

    [Fact]
    public void ExactName_SelectsSilently()
    {
        Assert.Equal("My Company", Resolve(W1, "My Company", null, out var notices));
        Assert.Equal("", notices);
    }

    // ── exact name beats a prefix ──

    [Fact]
    public void ExactName_WinsOverBeingAPrefixOfAnother()
    {
        var companies = new[] { "Foo Bar", "Foo" };
        Assert.Equal("Foo", Resolve(companies, "Foo", null, out _));
        // A case-insensitive exact name is still a name, not a prefix of "Foo Bar".
        Assert.Equal("Foo", Resolve(companies, "foo", null, out _));
    }

    // ── ambiguous, non-interactive ──

    [Fact]
    public void AmbiguousPrefix_NonInteractive_FailsListingOnlyTheCandidates()
    {
        var companies = new[] { "CRONUS International Ltd_", "CRONUS USA, Inc.", "My Company" };
        var ex = Assert.Throws<TestDataUnavailableException>(
            () => Resolve(companies, "cronus", null, out _));

        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains("'CRONUS International Ltd_'", firstLine, StringComparison.Ordinal);
        Assert.Contains("'CRONUS USA, Inc.'", firstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("My Company", firstLine, StringComparison.Ordinal);
        Assert.Contains("longer", firstLine, StringComparison.Ordinal);
    }

    // ── ambiguous, interactive (injected input, no TTY) ──

    [Fact]
    public void AmbiguousPrefix_Interactive_AsksWithANumberedList_AndUsesTheChoice()
    {
        var companies = new[] { "CRONUS International Ltd_", "CRONUS USA, Inc.", "My Company" };
        var shown = new StringWriter();
        var prompt = new CompanyPrompt(new StringReader("2\n"), shown);

        Assert.Equal("CRONUS USA, Inc.", Resolve(companies, "cr", prompt, out _));
        var text = shown.ToString();
        Assert.Contains("1) CRONUS International Ltd_", text, StringComparison.Ordinal);
        Assert.Contains("2) CRONUS USA, Inc.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("My Company", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousPrefix_Interactive_RepromptsOnBadInput_AndFailsAtEndOfInput()
    {
        var companies = new[] { "Foo One", "Foo Two" };

        var shown = new StringWriter();
        Assert.Equal("Foo One",
            Resolve(companies, "foo", new CompanyPrompt(new StringReader("9\nabc\n1\n"), shown), out _));

        var ex = Assert.Throws<TestDataUnavailableException>(
            () => Resolve(companies, "foo", new CompanyPrompt(new StringReader("7\n"), new StringWriter()), out _));
        Assert.Contains("'Foo One'", ex.Message, StringComparison.Ordinal);
    }

    // ── no match ──

    [Fact]
    public void NoMatch_FailsListingEveryCompany()
    {
        var ex = Assert.Throws<TestDataUnavailableException>(() => Resolve(W1, "Typo", null, out _));
        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains("'Typo'", firstLine, StringComparison.Ordinal);
        Assert.Contains("'CRONUS International Ltd_'", firstLine, StringComparison.Ordinal);
        Assert.Contains("'My Company'", firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMatch_IsNotRescuedByAMidNameSubstring()
    {
        // Prefix, not substring: "International" is inside a name but starts none.
        Assert.Throws<TestDataUnavailableException>(() => Resolve(W1, "International", null, out _));
    }

    // ── the spilled unquoted value (#4553 "Related") ──

    [Fact]
    public void SpilledValue_IsNamedWithTheQuotedFix()
    {
        var args = new[] { "Pageworks.Test/", "--test-data", "--test-data-company", "CRONUS", "International", "Ltd_" };
        var hint = BundleRootValidation.SpilledOptionValueHint(args, 4, new HashSet<int> { 0, 4, 5 });

        Assert.NotNull(hint);
        Assert.Contains("--test-data-company \"CRONUS International Ltd_\"", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void SpilledValue_NoHintForAnOrdinaryPositional()
    {
        // Preceded by a flag, by another bundle (even one after a value-less flag), or first.
        Assert.Null(BundleRootValidation.SpilledOptionValueHint(new[] { "--test-data", "missing" }, 1, new HashSet<int> { 1 }));
        Assert.Null(BundleRootValidation.SpilledOptionValueHint(new[] { "--test-data", "a", "missing" }, 2, new HashSet<int> { 1, 2 }));
        Assert.Null(BundleRootValidation.SpilledOptionValueHint(new[] { "missing" }, 0, new HashSet<int> { 0 }));
    }

    [Fact]
    public void SpilledValue_HintReachesTheNoSuchDirectoryMessage()
    {
        var missing = Path.Combine(TestScratch.Dir("al-runner-4553"), "International");
        var msg = BundleRootValidation.Validate(new[] { missing }, new[] { "  hint: HINT-TEXT" });
        Assert.NotNull(msg);
        Assert.Contains("no such directory", msg, StringComparison.Ordinal);
        Assert.Contains("HINT-TEXT", msg, StringComparison.Ordinal);
    }

    // ── the docs say it ──

    [Fact]
    public void Help_SaysTheNameMayBeAPrefix()
    {
        var w = new StringWriter();
        ProgramSupport.PrintHelp(w);
        Assert.Contains("case-insensitive prefix", w.ToString(), StringComparison.Ordinal);
    }
}
