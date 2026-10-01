// WindowsLanguageVirtualTableTests — issue #2581.
//
// A RUNNER-MECHANISM test: it proves the route to "Windows Language" (2000000045) works and
// that the columns WITH a real source carry BC's own values. Before the fix the table had no
// provider, so every read answered zero rows and Get(1033) silently returned false.
//
// The row set and three of the columns come from BC's own
// Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper — a runtime-engine type, so driving it is
// allowed — rather than being reimplemented from CultureInfo, which would answer a different
// list than a service tier.
//
// THE STUBBED COLUMNS ARE NOT ASSERTED HERE. Six license-derived and four installed-resource
// columns have no source on the runner and carry chosen values; "the runner answers permitted"
// is a runner claim, not BC behaviour, and it is pinned in
// tests/runner-extras/windows-language-license-stub instead. See docs/limitations.md.
//
// MEASURED on BC 28.1.49838.53910 through BC's own helper: 212 rows; 1033 = English (United
// States) / en-US / ENU / OEM code page 437; 1031 = German (Germany) / de-DE / DEU; 2057 =
// English (United Kingdom) / en-GB / ENG. Note "Primary Language ID" is 1033 for BOTH English
// rows — it is the id of the primary language's default culture, not the bare LANGID 9 — which
// is why the assertion below is structural rather than a hardcoded number.
using Xunit;

namespace AlRunner.Tests;

public sealed class WindowsLanguageVirtualTableTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 120_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "WindowsLanguageVirtualTable");

    [Fact]
    public async Task WindowsLanguage_TruthfulColumns_AllFixtureTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (every fixture test must pass). exit={r.ExitCode}\n{r}");

        // The route works and the columns carry BC's own values — the direct RED this
        // fixes. Includes the OEM-vs-ANSI code page, which is the one column a plausible
        // reimplementation gets wrong.
        r.AssertPassed("Codeunit60801.WindowsLanguage_Get1033_ReturnsTruthfulColumns");
        // A second, different row: a provider answering one fixed row passes the above.
        r.AssertPassed("Codeunit60801.WindowsLanguage_Get1031_IsADifferentRow");
        // Structural rather than a magic number: two English sublanguages share a Primary
        // Language ID and German does not.
        r.AssertPassed("Codeunit60801.WindowsLanguage_PrimaryLanguageId_GroupsSublanguagesTogether");
        // Negative: an unused id still answers false. Passes against an EMPTY table too,
        // which is why it is not sufficient on its own.
        r.AssertPassed("Codeunit60801.WindowsLanguage_GetOnAnUnusedId_ReturnsFalse");
        // Negative: filtering discriminates.
        r.AssertPassed("Codeunit60801.WindowsLanguage_FilterOnLanguageId_DiscriminatesBetweenRows");
        r.AssertNoFailures();
    }
}
