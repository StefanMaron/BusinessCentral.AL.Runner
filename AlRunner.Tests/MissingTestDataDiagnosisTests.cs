// MissingTestDataDiagnosisTests — the proving tests for issue #2240's diagnostic half.
//
// WHAT IS BEING PROVED, AND WHY IT IS ASSERTED ON THE CONSOLE OUTPUT
//   The claim is "a developer whose suite failed on missing setup data can tell that from the
//   output". So the assertions read the actual per-test lines the runner printed, not an
//   internal classification value. That distinction has cost this repo real defects before:
//   #2261 shipped a diagnosis whose actionable half never reached anyone because the bundle
//   reporter keeps only line 1 of the message. An assertion on an internal result cannot see
//   that; an assertion on the printed block can.
//
// WHY IT LIVES HERE AND NOT IN tests/runner-extras/
//   The claim is about the RUNNER's output, not about what Business Central does with AL, so
//   .claude/rules/bc-behavior-tests-go-upstream.md does not send it to the corpus. It also
//   cannot be an AL bundle: an AL test cannot assert on the reporter's own stdout, which is
//   the only place the diagnosis exists.
//
// THE NEGATIVE IS THE IMPORTANT ONE
//   PopulatedTable_* below fails with the SAME exception type and the SAME message shape as
//   the positive case — `NavCSideRecordNotFoundException: The <table> does not exist.` — and
//   must get NO explanation, because the table has a row in it. An implementation that
//   pattern-matched BC's wording instead of checking the store would pass every other test
//   here and fail that one. It is the test that makes the rest mean something.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// #2364 -- the "application" floor in this file's fixtures is an OUTSTANDING VIOLATION of
// .claude/rules/no-base-app-in-csharp-tests.md, not an exception to it. Every other fixture
// dropped it (#2358). These tests keep it only because they resolve "Source Code Setup"
// (table 242) against REAL metadata -- the assertion is on BC's own table id precisely so
// the diagnosis cannot pass by echoing a name back out of the message. A fixture table
// carrying its own metadata would serve the same claim without the closure; until someone
// does that, this stays and is tracked in #2364. Do not copy this floor into a new test.
public sealed class MissingTestDataDiagnosisTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>BC's own id for "Source Code Setup". Asserted literally so the diagnosis has to
    /// have RESOLVED the table against real metadata; echoing a name back out of the message
    /// would not produce it.</summary>
    private const int SourceCodeSetupTableId = 242;

    private readonly string _root;

    public MissingTestDataDiagnosisTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-missing-test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ────────────────────────────────────────────────────────── the fixture ──

    /// <summary>
    /// Four failures, chosen so that the two that must be explained and the two that must not
    /// are indistinguishable by exception type or message shape:
    ///
    ///   EmptySetupTable_Get       — record-not-found on a table with NO rows        → explained
    ///   PopulatedTable_Get        — record-not-found on a table WITH a row          → not explained
    ///   EmptySetupTable_TestField — TestField on a table with NO rows               → explained
    ///   PopulatedTable_TestField  — TestField on a table WITH a row                 → not explained
    ///   PlainError                — an ordinary AL Error naming no table            → not explained
    ///
    /// "Source Code Setup" is the table #2240 measured 12 of its 16 failures on. "Payment
    /// Method" is a Base App table the runner starts empty and this fixture inserts into, so
    /// "populated" is a fact the test creates rather than one it inherits.
    ///
    /// "Source Code Setup" itself is NOT guaranteed empty by construction any more (#2348):
    /// fixing IncludeSender sender-position dispatch also fixed a latent install-time bug that
    /// used to leave Codeunit2's OnBeforeSourceCodeSetupInsert NRE-ing silently, which — before
    /// that fix — happened to keep this table permanently empty as a side effect. A correct
    /// runner may now legitimately seed it during install, so the two "EmptySetupTable_*"
    /// procedures below call DeleteAll() first to make "no rows" a fact THIS TEST guarantees.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b7c1d2e3-4f50-4a61-9b72-8c3d4e5f6a7b",
          "name": "Missing Test Data Diagnosis Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "27.0.0.0",
          "application": "27.0.0.0",
          "idRanges": [ { "from": 62440, "to": 62449 } ],
          "runtime": "17.0",
          "target": "Cloud"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "MissingSetupTests.Codeunit.al"), """
        codeunit 62440 "Missing Setup Diag Tests"
        {
            Subtype = Test;

            [Test]
            procedure EmptySetupTable_Get()
            var
                SourceCodeSetup: Record "Source Code Setup";
            begin
                // #2348: fixing IncludeSender sender-position dispatch also fixed a latent
                // install-time bug that used to leave this table's own default-row insert
                // silently broken, so a fresh company may now legitimately arrive with a row
                // already in it (Codeunit2's OnBeforeSourceCodeSetupInsert firing correctly).
                // DeleteAll() first so "no rows" is still a fact this test GUARANTEES, not one
                // it merely used to inherit from a broken install.
                SourceCodeSetup.DeleteAll();
                SourceCodeSetup.Get();
            end;

            [Test]
            procedure PopulatedTable_Get()
            var
                PaymentMethod: Record "Payment Method";
            begin
                PaymentMethod.Init();
                PaymentMethod.Code := 'DIAG-A';
                PaymentMethod.Description := 'populated on purpose';
                PaymentMethod.Insert(true);
                PaymentMethod.Get('NOPE');
            end;

            [Test]
            procedure EmptySetupTable_TestField()
            var
                SourceCodeSetup: Record "Source Code Setup";
            begin
                // Same #2348 guarantee as EmptySetupTable_Get above — DeleteAll() first.
                SourceCodeSetup.DeleteAll();
                SourceCodeSetup.Init();
                SourceCodeSetup.TestField("Sales Journal");
            end;

            [Test]
            procedure PopulatedTable_TestField()
            var
                PaymentMethod: Record "Payment Method";
            begin
                PaymentMethod.Init();
                PaymentMethod.Code := 'DIAG-B';
                PaymentMethod.Insert(true);
                PaymentMethod.TestField(Description);
            end;

            [Test]
            procedure PlainError()
            begin
                Error('two plus two is %1, not 5', 2 + 2);
            end;
        }
        """);
    }

    // ─────────────────────────────────────────────────── running the runner ──

    private (string output, int exit) RunRunner(IDictionary<string, string>? env = null,
                                                params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The reported block for one test: its FAIL/ERROR header line plus every indented
    /// continuation line under it. This is exactly what a developer reads, which is why the
    /// assertions below are written against it rather than against the whole log — "the output
    /// contains no diagnosis" would otherwise be satisfiable by the diagnosis simply landing
    /// under the wrong test.
    /// </summary>
    private static string BlockFor(string output, string testMethod)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var block = new StringBuilder();
        var inBlock = false;
        foreach (var line in lines)
        {
            var isHeader = line.StartsWith("FAIL ", StringComparison.Ordinal)
                        || line.StartsWith("PASS ", StringComparison.Ordinal)
                        || line.StartsWith("ERROR", StringComparison.Ordinal)
                        || line.StartsWith("SKIP ", StringComparison.Ordinal);
            if (isHeader)
            {
                if (inBlock) break;
                inBlock = line.Contains($".{testMethod} (", StringComparison.Ordinal);
                if (inBlock) block.AppendLine(line);
                continue;
            }
            if (inBlock) block.AppendLine(line);
        }
        Assert.True(block.Length > 0,
            $"no reported block for '{testMethod}' — the fixture did not run as expected. Full output:\n{output}");
        return block.ToString();
    }

    // ───────────────────────────────────────────── the flag-off behaviour ──

    /// <summary>
    /// The whole point of #2240, and its negative in the same run so the two cannot drift
    /// apart: two failures that look identical to a reader get opposite treatment, decided by
    /// whether the named table actually holds rows.
    /// </summary>
    [SkippableFact]
    public void EmptySetupTable_IsExplained_AndAPopulatedOneIsNot()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();
        Assert.NotEqual(0, exit);   // every test in the fixture fails, by construction

        // ── explained: the table BC named has no rows ──────────────────────────────
        var emptyGet = BlockFor(output, "EmptySetupTable_Get");
        // BC's own failure, untouched. If this line ever changes shape the diagnosis is
        // replacing the failure instead of sitting next to it, which #2240 forbids.
        Assert.Contains("NavCSideRecordNotFoundException: The Source Code Setup does not exist.",
            emptyGet, StringComparison.Ordinal);
        Assert.Contains($"[test-data] 'Source Code Setup' (table {SourceCodeSetupTableId}) has no rows in this run",
            emptyGet, StringComparison.Ordinal);
        // It must say what to do, not merely what happened.
        Assert.Contains("--test-data", emptyGet, StringComparison.Ordinal);

        // ── NOT explained: same exception type, same message shape, table has a row ──
        var populatedGet = BlockFor(output, "PopulatedTable_Get");
        Assert.Contains("NavCSideRecordNotFoundException: The Payment Method does not exist.",
            populatedGet, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", populatedGet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling failure shape #2240 measured alongside the record-not-found one
    /// (`Invoice Nos. must have a value in Purchases &amp; Payables Setup`). It reaches the
    /// diagnosis through a different route — NavTestFieldException's own TableName property
    /// rather than a table id the runner recorded — so it needs its own positive AND its own
    /// negative.
    /// </summary>
    [SkippableFact]
    public void EmptySetupTable_TestFieldFailure_IsExplained_AndAPopulatedOneIsNot()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner();

        var emptyTestField = BlockFor(output, "EmptySetupTable_TestField");
        Assert.Contains("NavTestFieldException: Sales Journal must have a value in Source Code Setup",
            emptyTestField, StringComparison.Ordinal);
        Assert.Contains($"[test-data] 'Source Code Setup' (table {SourceCodeSetupTableId}) has no rows in this run",
            emptyTestField, StringComparison.Ordinal);

        var populatedTestField = BlockFor(output, "PopulatedTable_TestField");
        Assert.Contains("NavTestFieldException: Description must have a value in Payment Method",
            populatedTestField, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", populatedTestField, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure that names no table at all — an ordinary AL Error, which is what a failed
    /// Assert compiles to — must be left completely alone. This is the shape of the ONE
    /// genuine failure #2240 measured hiding among fifteen fake ones.
    /// </summary>
    [SkippableFact]
    public void AnErrorThatNamesNoTable_IsNotExplained()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner();

        var plain = BlockFor(output, "PlainError");
        Assert.Contains("two plus two is 4, not 5", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", plain, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────── the flag-on behaviour ──

    /// <summary>
    /// With --test-data ON and the table STILL empty, the message has to say something
    /// different, because the user cannot work out which of "refused", "not in this backup" or
    /// "empty in the backup" happened and the runner can.
    ///
    /// Driven through the AL_RUNNER_BCBAK seam with a fake reader rather than a ~1 GB backup no
    /// CI leg has — the same seam TestDataLazyHydrationTests uses. The fake offers
    /// "Source Code Setup" with rows, then answers the read with a column no AL field of that
    /// table has, which is exactly the refusal shape #2273 records as the only one left on a
    /// real CRONUS backup. So the table is in scope, --test-data really ran, and it is still
    /// empty — which is the case this branch exists for.
    /// </summary>
    [SkippableFact]
    public void WithTestDataOn_AnEmptyTableSaysWhyItIsStillEmpty()
    {
        TestArtifacts.SkipIfMissing();

        var backup = Path.Combine(_root, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[256]);
        var reader = Path.Combine(_root, "bcbak");
        File.WriteAllText(reader, FakeReaderScript());
        File.SetUnixFileMode(reader,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (output, _) = RunRunner(
            new Dictionary<string, string> { ["AL_RUNNER_BCBAK"] = reader },
            $"--test-data={backup}", "--test-data-company", "CRONUS");

        var emptyGet = BlockFor(output, "EmptySetupTable_Get");
        // Still BC's own failure, still untouched.
        Assert.Contains("NavCSideRecordNotFoundException: The Source Code Setup does not exist.",
            emptyGet, StringComparison.Ordinal);
        // And a DIFFERENT sentence from the flag-off one — this is the whole claim.
        Assert.Contains($"[test-data] 'Source Code Setup' (table {SourceCodeSetupTableId}) still has no rows although --test-data is on",
            emptyGet, StringComparison.Ordinal);
        Assert.Contains("refused", emptyGet, StringComparison.Ordinal);
        // The flag-off wording must NOT appear: telling a user who already passed --test-data
        // to pass --test-data is the failure this branch exists to avoid.
        Assert.DoesNotContain("pass --test-data to load a company", emptyGet, StringComparison.Ordinal);

        // The negative still holds with the flag on.
        Assert.DoesNotContain("[test-data]", BlockFor(output, "PopulatedTable_Get"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A `bcbak` stand-in: one company, one table in scope ("Source Code Setup", AL id 242,
    /// non-zero row count so BuildPlan keeps it), and a read that hands back a column the AL
    /// table has no field for so the hydration refuses it and the table stays empty.
    /// No `$ext` companion, so the once-per-run merge probe does not run.
    /// </summary>
    private static string FakeReaderScript() =>
        "#!/bin/sh\n"
        + "cmd=\"$1\"\n"
        + "case \"$cmd\" in\n"
        + "  companies) echo 'CRONUS' ;;\n"
        + $"  tables) printf '%s\\n' '   4 Table\tCRONUS\tSource Code Setup\t{SourceCodeSetupTableId} \"Source Code Setup\" (Base Application)' ;;\n"
        + "  read) echo '[{\"Not An AL Field Of This Table\": 1}]' ;;\n"
        + "esac\n";
}
