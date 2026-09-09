// CountOutTests — RED->GREEN for `--count-out` (#3675, round 3 of #3737's review).
//
// THE DEFECT THIS EXISTS FOR
// --------------------------
// The per-run corpus count comparison shipped pointed at `--out`, which is a FAILURE
// REPORT (`generated`, `total_failures`, `classifications`, `all_failures`) and never a
// list of tests. `compare_corpus_count.py` refused it — "al-language-results.json has no
// 'tests' array" — on every BC leg, so the whole mechanism never ran once. Measured on run
// 34412771210, job 102672338879.
//
// The count the runner already computes is the one `--count-baseline` compares against:
// per suite, `Tests` and `AppGroups`, keyed by BC version. `--count-out` writes exactly
// that, from the same tally, so the file the comparison reads and the number the baseline
// check uses can never disagree.
//
// WHY A SEPARATE FLAG RATHER THAN A SHAPE CHANGE TO --out
// -------------------------------------------------------
// `--out` is a public CLI contract with its own consumers (classification, failure
// triage). Changing its schema to carry counts would break them for a reason that has
// nothing to do with what they read it for.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CountOutTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // ── the document ────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape `compare_corpus_count.py` reads, asserted as concrete values rather than
    /// "it parses": a wrong key name is exactly the defect this replaces, and it would
    /// survive any test that only checked the document was valid JSON.
    /// </summary>
    [Fact]
    public void Serialize_WritesBcVersionAndOneEntryPerSuite()
    {
        var json = CountOut.Serialize("27.0", new Dictionary<string, SuiteCountActual>
        {
            ["al-language"] = new(3123, 1),
            ["al-language-onprem"] = new(29, 1),
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("27.0", doc.RootElement.GetProperty("bcVersion").GetString());

        var suites = doc.RootElement.GetProperty("suites");
        Assert.Equal(3123, suites.GetProperty("al-language").GetProperty("tests").GetInt32());
        Assert.Equal(1, suites.GetProperty("al-language").GetProperty("appGroups").GetInt32());
        Assert.Equal(29, suites.GetProperty("al-language-onprem").GetProperty("tests").GetInt32());
        Assert.Equal(1, suites.GetProperty("al-language-onprem").GetProperty("appGroups").GetInt32());
    }

    /// <summary>
    /// Sorted by suite key. The file is diffed and cached between runs, so a dictionary's
    /// insertion order — which follows whatever order the bundles happened to finish in —
    /// would make two identical runs produce different bytes.
    /// </summary>
    [Fact]
    public void Serialize_OrdersSuitesByName()
    {
        var json = CountOut.Serialize("28.4", new Dictionary<string, SuiteCountActual>
        {
            ["zeta"] = new(1, 1),
            ["alpha"] = new(2, 1),
        });

        Assert.True(json.IndexOf("alpha", StringComparison.Ordinal)
                    < json.IndexOf("zeta", StringComparison.Ordinal),
            "suites must be written in sorted order so two identical runs produce identical "
            + $"bytes; got:\n{json}");
    }

    /// <summary>
    /// A run that executed nothing writes an EMPTY suites object, not an absent one. The
    /// comparison script refuses a document with no `suites` key (exit 3, "could not
    /// measure") and reads an empty one as a real measurement of zero — two different
    /// facts, and only one of them is about the corpus.
    /// </summary>
    [Fact]
    public void Serialize_WithNoSuites_StillWritesTheSuitesObject()
    {
        var json = CountOut.Serialize("27.5", new Dictionary<string, SuiteCountActual>());

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("suites", out var suites));
        Assert.Equal(JsonValueKind.Object, suites.ValueKind);
        Assert.Empty(suites.EnumerateObject());
    }

    /// <summary>
    /// The tally is keyed by the bundle directory's basename — the same key
    /// <c>--count-baseline</c> matches suites on — and two roots that resolve to one suite
    /// name add up rather than overwrite.
    /// </summary>
    [Fact]
    public void Tally_KeysBySuiteDirectoryName_AndSumsRepeats()
    {
        var tally = CountOut.Tally(new[]
        {
            ("/x/tests/al-language", 10, 1),
            ("/y/tests/al-language-onprem", 3, 1),
            ("/z/tests/al-language", 5, 2),
        });

        Assert.Equal(15, tally["al-language"].Tests);
        Assert.Equal(3, tally["al-language"].AppGroups);
        Assert.Equal(3, tally["al-language-onprem"].Tests);
    }

    /// <summary>A trailing separator must not produce an empty suite key.</summary>
    [Fact]
    public void Tally_IgnoresATrailingSeparatorOnTheBundlePath()
    {
        var tally = CountOut.Tally(new[] { ("/x/tests/al-language/", 4, 1) });

        Assert.Equal(4, Assert.Contains("al-language", tally).Tests);
    }

    [Fact]
    public void Write_CreatesTheDirectoryAndTheFile()
    {
        var dir = Path.Combine(TestScratch.Dir("al-runner-count-out"), "nested", "deeper");
        var path = Path.Combine(dir, "corpus-count.json");
        try
        {
            CountOut.Write(path, "28.4", new Dictionary<string, SuiteCountActual>
            {
                ["runner-extras"] = new(7, 3),
            });

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(7, doc.RootElement.GetProperty("suites")
                .GetProperty("runner-extras").GetProperty("tests").GetInt32());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!, true); } catch { }
        }
    }

    // ── the flag, end to end ────────────────────────────────────────────────────

    /// <summary>
    /// The wiring, against the real binary: `--count-out` is accepted and the file it
    /// names carries this run's suite. On `main` the flag does not exist, and an unknown
    /// flag exits 2 with no file written — which is precisely how the broken version
    /// reached CI, so the end-to-end half is not optional.
    /// </summary>
    [SkippableFact]
    public void CountOutFlag_WritesTheSuiteThisRunExecuted()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-count-out-e2e");
        var countPath = Path.Combine(scratch, "corpus-count.json");
        var bundle = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        args.Append($" --count-out \"{countPath}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        Assert.True(p.ExitCode == 0,
            $"the runner exited {p.ExitCode}; exit 2 here means --count-out was rejected as an "
            + $"unknown flag.\n{output}");
        Assert.True(File.Exists(countPath),
            $"--count-out named {countPath} and nothing was written there.\n{output}");

        using var doc = JsonDocument.Parse(File.ReadAllText(countPath));
        // A BC version, not an empty string: the comparison records it beside the count so a
        // per-version difference is attributable.
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("bcVersion").GetString()));
        var suite = doc.RootElement.GetProperty("suites").GetProperty("RecordTriggerXRec");
        Assert.True(suite.GetProperty("tests").GetInt32() > 0,
            $"the fixture ran tests but the count file recorded none.\n{output}");
        Assert.True(suite.GetProperty("appGroups").GetInt32() > 0);

        try { Directory.Delete(scratch, true); } catch { }
    }
}
