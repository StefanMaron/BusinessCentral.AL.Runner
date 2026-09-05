// ExpectationManifestSchemaTests — the loader's contract for expect-divergence (#1741).
//
// An intended, permanent divergence from BC has no open work to link, so the entry
// has to justify itself instead: Reason (what diverges) + Doc (where the decision is
// written down). If either could be omitted, expect-divergence would just be
// "expect-fail-known-gap without the paperwork" and the mode would launder exactly
// the dishonesty it was added to remove.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class ExpectationManifestSchemaTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-manifest-");

    public ExpectationManifestSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ExpectationManifest Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "divergence-x.json"), json);
        return ExpectationManifest.LoadFromDirectory(_dir);
    }

    private string LoadError(string json)
        => Assert.Throws<InvalidOperationException>(() => Load(json)).Message;

    private const string Head = @"[{""codeunitId"":60877,""CodeunitName"":""Cu"",""Method"":""M"",""Mode"":""expect-divergence""";

    [Fact]
    public void ExpectDivergence_WithReasonAndDoc_Loads()
    {
        var m = Load(Head + @",""Reason"":""task-scheduler-create-task"",""Doc"":""docs/scope.md#jobs""}]");
        var e = m.Lookup("Cu", "M");
        Assert.NotNull(e);
        Assert.Equal(ExpectationMode.ExpectDivergence, e!.Mode);
        Assert.Equal("task-scheduler-create-task", e.Reason);
        Assert.Equal("docs/scope.md#jobs", e.Doc);
        Assert.Null(e.Issue);
    }

    [Fact]
    public void ExpectDivergence_WithoutDoc_IsRejected()
    {
        Assert.Contains("requires non-empty 'Doc'",
            LoadError(Head + @",""Reason"":""task-scheduler-create-task""}]"));
    }

    [Fact]
    public void ExpectDivergence_WithoutReason_IsRejected()
    {
        Assert.Contains("requires non-empty 'Reason'",
            LoadError(Head + @",""Doc"":""docs/scope.md#jobs""}]"));
    }

    [Fact]
    public void ExpectDivergence_WithAnIssueLink_IsRejected()
    {
        // The failure mode #1741 documents is a "known gap" pointing at a CLOSED
        // issue. An intended divergence has no work to track, so linking one is
        // the same lie in a new mode — reject it at load time.
        Assert.Contains("must not carry 'Issue'",
            LoadError(Head + @",""Reason"":""r"",""Doc"":""docs/scope.md#jobs"","
                + @"""Issue"":""https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1733""}]"));
    }

    [Fact]
    public void UnknownMode_NamesEveryLegalMode()
    {
        var msg = LoadError(
            @"[{""codeunitId"":1,""CodeunitName"":""Cu"",""Method"":""M"",""Mode"":""expect-magic""}]");
        Assert.Contains("unknown Mode 'expect-magic'", msg);
        Assert.Contains("expect-divergence", msg);
    }

    /// <summary>
    /// The shipped manifest must stay loadable and stay honest: every
    /// expect-fail-known-gap entry links an issue, every expect-divergence entry
    /// documents itself instead. (#1741 was filed because three of five entries
    /// were known-gaps nobody intended to fix.)
    /// </summary>
    [Fact]
    public void ShippedManifest_LoadsAndEveryModeCarriesItsRequiredEvidence()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = ExpectationManifest.LoadFromDirectory(
            Path.Combine(repoRoot, "tests", "expectations"));

        Assert.NotEmpty(manifest.Entries);
        foreach (var e in manifest.Entries)
        {
            switch (e.Mode)
            {
                case ExpectationMode.ExpectOos:
                    Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"{e.SourceFile}: {e.CodeunitName}.{e.Method}");
                    break;
                case ExpectationMode.ExpectFailKnownGap:
                    Assert.Contains("/issues/", e.Issue!);
                    break;
                case ExpectationMode.ExpectDivergence:
                    Assert.StartsWith("docs/", e.Doc!);
                    Assert.Null(e.Issue);
                    break;
            }
        }
    }

    /// <summary>
    /// Every <c>Doc</c> an expect-divergence entry cites must resolve: the file has to exist, and
    /// when the reference carries a <c>#anchor</c>, that anchor has to be defined in it.
    ///
    /// <para>#2565 is why this exists. A divergence entry's whole justification is "the decision
    /// is recorded over there" — if the pointer rots, the entry becomes an assertion with nothing
    /// behind it, and the manifest looks as authoritative as ever. The sibling check above only
    /// asked that <c>Doc</c> starts with <c>docs/</c>, which a reference to a deleted file or a
    /// renamed heading passes.</para>
    ///
    /// <para>This does not check that the prose is TRUE — nothing automated can — but it does
    /// catch the mechanical half of the drift that has been expensive here repeatedly: three
    /// separate cases in one day of a document asserting something the code had stopped doing.
    /// Anchors are matched against both explicit <c>&lt;a id="..."&gt;</c> tags (what
    /// <c>docs/scope.md</c> uses) and GitHub's heading-slug convention.</para>
    /// </summary>
    [Fact]
    public void EveryDivergenceDocReference_ResolvesToARealFileAndAnchor()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = ExpectationManifest.LoadFromDirectory(
            Path.Combine(repoRoot, "tests", "expectations"));

        var checkedAny = false;
        foreach (var e in manifest.Entries)
        {
            if (e.Mode != ExpectationMode.ExpectDivergence || string.IsNullOrWhiteSpace(e.Doc)) continue;
            checkedAny = true;

            var hash = e.Doc!.IndexOf('#');
            var relPath = hash < 0 ? e.Doc! : e.Doc![..hash];
            var anchor = hash < 0 ? null : e.Doc![(hash + 1)..];

            var full = Path.Combine(repoRoot, relPath);
            Assert.True(File.Exists(full),
                $"{e.SourceFile}: {e.CodeunitName}.{e.Method} cites Doc '{e.Doc}', but {relPath} does not exist.");

            if (anchor == null) continue;
            var text = File.ReadAllText(full);

            // Explicit anchor tag, e.g. docs/scope.md's `<a id="jobs"></a>`.
            if (text.Contains($"id=\"{anchor}\"", StringComparison.OrdinalIgnoreCase)) continue;

            // Otherwise GitHub's heading slug: lower-cased, non-alphanumerics dropped, spaces to '-'.
            var slugs = text.Split('\n')
                .Where(l => l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                .Select(l => new string(l.TrimStart('#', ' ', '\t', '\r')
                        .ToLowerInvariant()
                        .Select(c => char.IsLetterOrDigit(c) ? c : (c == ' ' ? '-' : '\0'))
                        .Where(c => c != '\0').ToArray()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.True(slugs.Contains(anchor),
                $"{e.SourceFile}: {e.CodeunitName}.{e.Method} cites Doc '{e.Doc}', but {relPath} defines no "
                + $"anchor '{anchor}' — neither an <a id=\"...\"> tag nor a heading that slugs to it. "
                + "A divergence entry whose pointer has rotted is an assertion with nothing behind it.");
        }

        Assert.True(checkedAny,
            "no expect-divergence entry carried a Doc reference — this guard would pass vacuously.");
    }
}
