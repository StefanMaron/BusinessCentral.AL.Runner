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
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "al-runner-manifest-" + Guid.NewGuid().ToString("N"));

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
}
