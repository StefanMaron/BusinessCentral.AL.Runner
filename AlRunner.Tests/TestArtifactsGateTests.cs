using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves the ONE artifacts-presence gate the whole suite shares.
///
/// The gate it replaces was copied per class and drifted: six classes checked only
/// <c>~/.bcartifacts.cache/sandbox</c> — a path nothing in this repo ever creates —
/// so on CI they took the "environment unavailable" branch, and because that branch
/// was a bare <c>return</c>, xUnit recorded them as <b>Passed</b> having asserted
/// nothing. Fifteen other classes checked <c>~/.local/share/al-runner/artifacts</c>
/// (what the workflow actually provisions) and two checked both.
///
/// So this file asserts two separate things, and both are load-bearing:
///   1. Detection matches what CI provisions — a directory layout the workflow
///      creates must read as PRESENT, and one it does not must read as ABSENT.
///   2. The unavailable branch is observably a SKIP, not a pass — the helper raises
///      xUnit's skip signal naming what was missing, and every test that can take
///      that branch is declared [SkippableFact]/[SkippableTheory] (a SkipException
///      out of a plain [Fact] is reported Failed, which would be a different lie).
/// </summary>
public class TestArtifactsGateTests
{
    // …/AlRunner.Tests/bin/<Config>/<tfm>/ -> …/AlRunner.Tests/
    private static readonly string TestsSourceDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static string NewTempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-artifacts-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- 1. detection semantics -------------------------------------------------

    /// <summary>
    /// The layout `.github/workflows/test-matrix.yml` actually provisions:
    /// `$HOME/.local/share/al-runner/artifacts/&lt;bc-version&gt;/`. This is the case
    /// the old six-class gate answered `false` for on every CI run.
    /// </summary>
    [Fact]
    public void PresentIn_TrueForTheLayoutCiProvisions()
    {
        var home = NewTempHome();
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "al-runner", "artifacts", "28.1.49838.50794"));

        Assert.True(TestArtifacts.PresentIn(home),
            $"artifacts provisioned under {home}/.local/share/al-runner/artifacts/28.1.49838.50794 must read as present");
    }

    /// <summary>The legacy dev-box layout stays supported: a full BC sandbox artifact cache.</summary>
    [Fact]
    public void PresentIn_TrueForTheLegacyBcArtifactsCacheLayout()
    {
        var home = NewTempHome();
        Directory.CreateDirectory(Path.Combine(home, ".bcartifacts.cache", "sandbox"));

        Assert.True(TestArtifacts.PresentIn(home),
            $"a sandbox cache under {home}/.bcartifacts.cache must read as present");
    }

    [Fact]
    public void PresentIn_FalseWhenNeitherLayoutExists()
    {
        var home = NewTempHome();

        Assert.False(TestArtifacts.PresentIn(home),
            "an empty home has no artifacts and must read as absent");
    }

    /// <summary>
    /// An artifacts ROOT with no version directory under it is not provisioning — the
    /// download step creates `artifacts/&lt;version&gt;/`, and a bare `mkdir -p artifacts`
    /// (or a wiped cache) must not be mistaken for it.
    /// </summary>
    [Fact]
    public void PresentIn_FalseWhenArtifactsRootHasNoVersionDirectory()
    {
        var home = NewTempHome();
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "al-runner", "artifacts"));

        Assert.False(TestArtifacts.PresentIn(home),
            "an empty artifacts root carries no service-tier DLLs and must read as absent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PresentIn_FalseWhenHomeIsUnknown(string? home)
    {
        Assert.False(TestArtifacts.PresentIn(home), "no home directory means nothing to look in");
    }

    /// <summary>
    /// The reason a developer reads must name BOTH paths that were probed — the whole
    /// defect was a gate looking in a place nobody populates, and a reason string that
    /// names its candidates makes that visible the first time it is wrong.
    /// </summary>
    [Fact]
    public void MissingReason_NamesBothCandidateLayouts()
    {
        var home = NewTempHome();

        var reason = TestArtifacts.MissingReason(home);

        Assert.Contains(Path.Combine(home, ".local", "share", "al-runner", "artifacts"), reason, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(home, ".bcartifacts.cache", "sandbox"), reason, StringComparison.Ordinal);
    }

    // ---- 2. the unavailable branch is a skip, not a pass ------------------------

    /// <summary>
    /// The core claim of this issue: when artifacts are missing the helper raises
    /// xUnit's skip signal — it does NOT hand control back so the caller can fall
    /// off the end of the method and be recorded as Passed.
    /// </summary>
    [Fact]
    public void SkipIfMissing_RaisesASkip_WhenArtifactsAreAbsent_OffCi()
    {
        var home = NewTempHome();

        var ex = Record.Exception(() => TestArtifacts.SkipIfMissingIn(home, runningOnCi: false));

        Assert.NotNull(ex);
        Assert.IsType<SkipException>(ex);
        Assert.Contains(".local/share/al-runner/artifacts", ex!.Message.Replace('\\', '/'), StringComparison.Ordinal);
    }

    /// <summary>Negative direction: a provisioned environment must NOT skip.</summary>
    [Fact]
    public void SkipIfMissing_DoesNothing_WhenArtifactsArePresent()
    {
        var home = NewTempHome();
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "al-runner", "artifacts", "28.1.49838.50794"));

        Assert.Null(Record.Exception(() => TestArtifacts.SkipIfMissingIn(home, runningOnCi: false)));
        Assert.Null(Record.Exception(() => TestArtifacts.SkipIfMissingIn(home, runningOnCi: true)));
    }

    // ---- 2b. on CI, "artifacts missing" is a defect, not a skip -----------------

    /// <summary>
    /// The hole a visible skip alone still leaves open, and the reason this rule exists:
    /// if the workflow moves where it provisions artifacts, <see cref="TestArtifacts.Present"/>
    /// answers false for EVERY test, all of them skip — visibly, correctly, and CI stays
    /// green. That is the same "nothing ran and nobody noticed" failure one level up, and
    /// the drift guards below would all still pass.
    ///
    /// On a CI leg every artifact is provisioned by construction, so "artifacts missing"
    /// there is a provisioning defect. It must FAIL the leg, naming both layouts that were
    /// searched so the fix is obvious from the log alone.
    /// </summary>
    [Fact]
    public void SkipIfMissing_FailsRatherThanSkips_WhenArtifactsAreAbsentOnCi()
    {
        var home = NewTempHome();

        var ex = Record.Exception(() => TestArtifacts.SkipIfMissingIn(home, runningOnCi: true));

        Assert.NotNull(ex);
        Assert.IsNotType<SkipException>(ex);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(ex);

        var msg = ex!.Message.Replace('\\', '/');
        // Both candidate layouts, so the log says exactly where it looked.
        Assert.Contains(".local/share/al-runner/artifacts", msg, StringComparison.Ordinal);
        Assert.Contains(".bcartifacts.cache/sandbox", msg, StringComparison.Ordinal);
        // And why a skip was refused here specifically.
        Assert.Contains("CI", msg, StringComparison.Ordinal);
        Assert.Contains("provisioning", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "true", true)]
    [InlineData("CI", "true", true)]
    [InlineData("CI", "1", true)]
    // A workflow that explicitly disables the flag means what it says.
    [InlineData("CI", "false", false)]
    [InlineData("CI", "0", false)]
    [InlineData("CI", "", false)]
    // A dev box sets neither.
    [InlineData("UNRELATED", "true", false)]
    public void IsCiEnvironment_ReadsTheStandardFlags(string name, string value, bool expected)
    {
        Assert.Equal(expected, TestArtifacts.IsCiEnvironment(n => n == name ? value : null));
    }

    /// <summary>
    /// Pins the flags themselves: GitHub Actions sets both, and dropping either from the
    /// list would silently re-open the "all skipped, still green" hole on some runner.
    /// </summary>
    [Fact]
    public void CiEnvironmentVariables_CoverGithubActionsAndTheGenericFlag()
    {
        Assert.Equal(new[] { "GITHUB_ACTIONS", "CI" }, TestArtifacts.CiEnvironmentVariables);
    }

    [Fact]
    public void SkipIf_RaisesASkipCarryingTheGivenReason()
    {
        var ex = Record.Exception(() => TestArtifacts.SkipIf(true, "platform-apps not provisioned"));

        Assert.NotNull(ex);
        Assert.IsType<SkipException>(ex);
        Assert.Contains("platform-apps not provisioned", ex!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipIf_DoesNothing_WhenTheConditionIsFalse()
    {
        Assert.Null(Record.Exception(() => TestArtifacts.SkipIf(false, "platform-apps not provisioned")));
    }

    // ---- 3. drift guards over the suite's own source ----------------------------

    private static IEnumerable<(string File, string Text)> TestSources() =>
        Directory.EnumerateFiles(TestsSourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    /// <summary>
    /// One gate, one place. Twenty-three classes each declared their own
    /// <c>ArtifactsPresent()</c> in three mutually inconsistent spellings; that
    /// duplication is exactly why six of them could go stale unnoticed.
    /// </summary>
    [Fact]
    public void NoTestClassDeclaresItsOwnArtifactsGate()
    {
        var offenders = TestSources()
            .Where(s => Regex.IsMatch(s.Text, @"bool\s+ArtifactsPresent\s*\("))
            .Select(s => s.File)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these files declare their own artifacts gate instead of using TestArtifacts: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Only the shared helper may name an artifact cache path in CODE. A class that
    /// spells a path itself is a fresh copy of the gate waiting to drift out of step
    /// with what the workflow provisions — which is exactly how six of them ended up
    /// probing a directory nothing creates. (Comments may still discuss the paths;
    /// prose cannot drift into a wrong runtime answer.)
    /// </summary>
    [Fact]
    public void OnlyTheSharedHelperNamesTheArtifactCachePathsInCode()
    {
        var offenders = new List<string>();
        foreach (var (file, text) in TestSources())
        {
            if (file is "TestArtifacts.cs" or "TestArtifactsGateTests.cs") continue;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.Contains(".bcartifacts.cache", StringComparison.Ordinal)
                    || trimmed.Contains("\"al-runner\", \"artifacts\"", StringComparison.Ordinal))
                    offenders.Add($"{file}: {trimmed.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these lines spell an artifact cache path instead of asking TestArtifacts:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// A test that returns early because its environment is unavailable is recorded
    /// Passed — a green tick meaning "asserted nothing". Nothing in this assembly may
    /// do that; the unavailable branch must raise a visible skip.
    /// </summary>
    [Fact]
    public void NoTestSilentlyReturnsWhenItsEnvironmentIsUnavailable()
    {
        // Both shapes the suite used: a "[skip] …" line followed by return, and a
        // bare `return;` whose trailing comment admits it is skipping / has nothing
        // to assert.
        var shapes = new[]
        {
            new Regex(@"\[skip\][^\n]*\breturn;", RegexOptions.None),
            new Regex(@"\breturn;\s*//[^\n]*\b(skip|not provisioned|no artifacts|nothing to assert|nothing to prove)\b",
                RegexOptions.IgnoreCase),
        };

        var offenders = new List<string>();
        foreach (var (file, text) in TestSources())
        {
            if (file == "TestArtifactsGateTests.cs") continue; // the regexes above are literals here
            foreach (var shape in shapes)
                foreach (Match m in shape.Matches(text))
                    offenders.Add($"{file}: {m.Value.Split('\n')[0].Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "these early returns report Passed without asserting anything — use TestArtifacts.SkipIf* instead:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// xUnit v2 has no dynamic-skip support of its own: a SkipException thrown from a
    /// plain <c>[Fact]</c> is reported <b>Failed</b>. Only <c>[SkippableFact]</c> /
    /// <c>[SkippableTheory]</c> translate it into a Skipped result — so a test that can
    /// reach a skip must be declared skippable, or we trade a false green for a false red.
    /// </summary>
    [Fact]
    public void EveryTestThatCanSkipIsDeclaredSkippable()
    {
        var factLine = new Regex(@"^\s*\[(?<attr>SkippableFact|SkippableTheory|Fact|Theory)[\](]");
        var methodLine = new Regex(@"^\s*(?:public|private|internal|protected)[^=]*\s(?<name>\w+)\s*\(");
        var skipCall = new Regex(@"\bTestArtifacts\.(SkipIfMissing|SkipIf|SkipIfDirectoryMissing)\b|\bSkip\.(If|IfNot|Always)\b");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(TestsSourceDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file) is "TestArtifacts.cs" or "TestArtifactsGateTests.cs") continue;
            string? attr = null, method = null;
            foreach (var line in File.ReadAllLines(file))
            {
                var f = factLine.Match(line);
                if (f.Success) { attr = f.Groups["attr"].Value; method = null; continue; }
                if (attr != null && method == null)
                {
                    var m = methodLine.Match(line);
                    if (m.Success) { method = m.Groups["name"].Value; continue; }
                }
                if (attr is "Fact" or "Theory" && method != null && skipCall.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}.{method} is [{attr}] but can skip");
                    attr = null;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a SkipException out of a plain [Fact] is reported Failed, not Skipped:\n" + string.Join("\n", offenders));
    }
}
