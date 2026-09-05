// RecordRefCompilationTargetScopeTests — issue #2783.
//
// RUNNER-MECHANISM test. The BEHAVIOURAL claim ("what does real BC do when a Cloud-target
// app calls RecordRef.Open on an internal system table") is adjudicated upstream in the
// al-language corpus, which runs on real BC service tiers — corpus run 33968379281 printed,
// on all 8 legs (27.0 … 28.4):
//
//     You cannot open record 2000000071 from a RecordRef data type when you are using
//     target Cloud.
//
// What this file pins is the RUNNER wiring that claim needs: app.json's `target` has to
// reach the RUNTIME, not only the compile path. #2725 made the manifest target reach
// NavCA.CompilationOptions, so `Record "Object Metadata"` in a Cloud bundle correctly fails
// with AL0296 at compile time — but RecordRef.Open takes an *id*, so it skips the
// compile-time half entirely, and the runtime half did not exist: NclCecilRewrite replaced
// NavRecordRef.CheckIsOpenAllowed with NoOp3 and IsOpenAllowed with ReturnTrue_ThreeArgs,
// and the three NavRecordRef_ALOpen_Target* helpers dropped the CompilationTarget argument
// on the floor. A Cloud bundle opened table 2000000071 and read rows out of it.
//
// The two fixtures below are byte-identical AL except for `target` in app.json and the
// object ids, and BOTH must exit 0:
//
//   * Cloud   — Open(2000000071) must RAISE, with BC's own sentence naming both the table
//               id and the target; Open(2000000026), Open(2000000187), Open(2000000188)
//               and Open(<own table>) must still succeed.
//   * OnPrem  — every one of those five, INCLUDING 2000000071, must succeed.
//
// That pins the gate from three sides at once. Reverting the fix breaks the Cloud
// asserterror test; a gate that refuses every system table breaks the Cloud
// 2000000026/187/188 tests; a gate that forgets to exempt OnPrem breaks the whole OnPrem
// fixture.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordRefCompilationTargetScopeTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>BC's own message, from Lang.NotAllowedRecordRefCompilationTarget.</summary>
    private const string BcRefusal =
        "You cannot open record 2000000071 from a RecordRef data type when you are using target Cloud.";

    private readonly string _root;
    private readonly string _cloudDir;
    private readonly string _onPremDir;

    public RecordRefCompilationTargetScopeTests()
    {
        _root = TestScratch.Dir("al-runner-recordref-target-scope");
        _cloudDir = Path.Combine(_root, "cloud");
        _onPremDir = Path.Combine(_root, "onprem");
        Directory.CreateDirectory(_cloudDir);
        Directory.CreateDirectory(_onPremDir);
        WriteFixture(_cloudDir, "Cloud", idBase: 62660,
            appId: "3f7a1c92-5b48-4d61-9e02-7c4d8a1b6e30");
        WriteFixture(_onPremDir, "OnPrem", idBase: 62670,
            appId: "3f7a1c92-5b48-4d61-9e02-7c4d8a1b6e31");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// One self-contained AL package. No <c>"application"</c> property — see
    /// .claude/rules/no-base-app-in-csharp-tests.md; nothing here needs the Base App floor,
    /// the tables are the platform's own system tables plus one the fixture declares.
    /// </summary>
    private static void WriteFixture(string dir, string target, int idBase, string appId)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "RecordRef Target Scope Fixture ({{target}})",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase}}, "to": {{idBase + 9}} } ],
          "runtime": "14.0",
          "target": "{{target}}"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Ordinary.Table.al"), $$"""
        table {{idBase}} "RRTS Ordinary {{target}}"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), $$"""
        codeunit {{idBase + 1}} "RRTS Assert {{target}}"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;

            procedure ExpectedError(Expected: Text)
            var
                Actual: Text;
            begin
                Actual := GetLastErrorText();
                if Actual = '' then
                    Error('Expected an error containing <%1> but no error was raised.', Expected);
                if StrPos(Actual, Expected) = 0 then
                    Error('Expected an error containing <%1> but got <%2>.', Expected, Actual);
            end;
        }
        """);

        // The one test whose EXPECTED OUTCOME differs between the two fixtures. Written as a
        // real assertion in each direction rather than "run it and see what the runner says",
        // so both fixtures are exit-0-on-correct-behaviour.
        var internalTableTest = target == "Cloud"
            ? $$"""
                /// Cloud target: table 2000000071 is in SystemTables.InternalTables, so real BC
                /// refuses the open outright. Assert on BC's OWN sentence — it names both the
                /// table id and the target, so a runner paraphrase, or a refusal that reports
                /// the wrong target, fails here.
                [Test]
                procedure InternalSystemTable_IsRefused()
                var
                    RecRef: RecordRef;
                begin
                    asserterror RecRef.Open(2000000071);
                    Assert.ExpectedError('{{BcRefusal}}');
                end;
                """
            : """
                /// OnPrem target: BC applies no compilation-target gate at all, so the very
                /// same open must succeed. This is the arm that fails if the gate is written
                /// to refuse regardless of target.
                [Test]
                procedure InternalSystemTable_IsAllowed()
                var
                    RecRef: RecordRef;
                begin
                    RecRef.Open(2000000071);
                    Assert.AreEqual(2000000071, RecRef.Number, 'RecordRef.Open(2000000071) must succeed for an OnPrem-target app');
                    RecRef.Close();
                end;
                """;

        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{idBase + 2}} "RRTS Tests {{target}}"
        {
            Subtype = Test;

            var
                Assert: Codeunit "RRTS Assert {{target}}";

        {{internalTableTest}}

            /// A system table that is NOT internal and NOT OnPrem-scoped (2000000026
            /// "Integer"). BC allows it from every target; a gate that blanket-refuses
            /// 2000000000+ ids from Cloud fails here.
            ///
            /// The filtered Count() is the second half of this arm and covers the SIBLING
            /// gate: BC's NavRecordRef.CheckOperationIsAllowed runs on every RecordRef
            /// operation (not just Open), consults the same
            /// IsSystemTableAllowedForRecordRefUsage, and only engages for ids above
            /// 2000000000 when the calling object is not compiled for on-premise — i.e.
            /// exactly this Cloud fixture. It is BC's own unreplaced body, so this asserts
            /// it neither refuses a permitted table nor breaks on the headless skeleton.
            /// 3 is a specific non-default answer: a stubbed Count() returning 0 fails.
            [Test]
            procedure NonScopedSystemTable_IsAllowed()
            var
                RecRef: RecordRef;
                FldRef: FieldRef;
            begin
                RecRef.Open(2000000026);
                Assert.AreEqual(2000000026, RecRef.Number, 'RecordRef.Open(2000000026) must succeed for every target');
                FldRef := RecRef.Field(1);
                FldRef.SetRange(1, 3);
                Assert.AreEqual(3, RecRef.Count(), 'A filtered Count() on an open RecordRef must not be refused either');
                RecRef.Close();
            end;

            /// The two ids in SystemTables.OnPremSystemTableRecordRefAllowed. BC lets these
            /// through from a non-OnPrem target where it refuses their OnPrem-scoped
            /// neighbours, so a gate that ignores the allow-list fails here.
            [Test]
            procedure AllowListedOnPremSystemTable187_IsAllowed()
            var
                RecRef: RecordRef;
            begin
                RecRef.Open(2000000187);
                Assert.AreEqual(2000000187, RecRef.Number, 'RecordRef.Open(2000000187) must succeed for every target');
                RecRef.Close();
            end;

            [Test]
            procedure AllowListedOnPremSystemTable188_IsAllowed()
            var
                RecRef: RecordRef;
            begin
                RecRef.Open(2000000188);
                Assert.AreEqual(2000000188, RecRef.Number, 'RecordRef.Open(2000000188) must succeed for every target');
                RecRef.Close();
            end;

            /// An ordinary application table this very bundle declares. Never gated.
            [Test]
            procedure OrdinaryApplicationTable_IsAllowed()
            var
                RecRef: RecordRef;
            begin
                RecRef.Open({{idBase}});
                Assert.AreEqual({{idBase}}, RecRef.Number, 'RecordRef.Open on an ordinary table must succeed for every target');
                RecRef.Close();
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(params string[] bundleDirs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --strict");
        foreach (var d in bundleDirs) args.Append($" \"{d}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
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
    /// Both fixtures in one runner invocation. Each named PASS below is a separate arm of the
    /// gate; the run also has to exit 0, so any FAIL anywhere in either bundle is caught even
    /// if a future edit renames a test.
    /// </summary>
    [SkippableFact]
    public void ManifestTargetGatesRecordRefOpenAtRuntime()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(_cloudDir, _onPremDir);

        // Cloud: refused for the internal system table, allowed for everything else.
        Assert.Contains("PASS  Codeunit62662.InternalSystemTable_IsRefused", output);
        Assert.Contains("PASS  Codeunit62662.NonScopedSystemTable_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62662.AllowListedOnPremSystemTable187_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62662.AllowListedOnPremSystemTable188_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62662.OrdinaryApplicationTable_IsAllowed", output);

        // OnPrem: the identical opens all succeed, 2000000071 included.
        Assert.Contains("PASS  Codeunit62672.InternalSystemTable_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62672.NonScopedSystemTable_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62672.AllowListedOnPremSystemTable187_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62672.AllowListedOnPremSystemTable188_IsAllowed", output);
        Assert.Contains("PASS  Codeunit62672.OrdinaryApplicationTable_IsAllowed", output);

        Assert.DoesNotContain("FAIL  Codeunit", output);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The refusal must carry BC's own exception type and BC's own text — not a runner
    /// paraphrase and not a bare InvalidOperationException. Proven by letting the error
    /// escape to the reporter (no asserterror), which prints
    /// <c>&lt;ExceptionTypeName&gt;: &lt;message&gt;</c> on the FAIL line.
    /// </summary>
    [SkippableFact]
    public void RefusalCarriesBcExceptionTypeAndText()
    {
        TestArtifacts.SkipIfMissing();

        var dir = Path.Combine(_root, "uncaught");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "3f7a1c92-5b48-4d61-9e02-7c4d8a1b6e32",
          "name": "RecordRef Target Scope Fixture (uncaught)",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62680, "to": 62689 } ],
          "runtime": "14.0",
          "target": "Cloud"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), """
        codeunit 62680 "RRTS Uncaught"
        {
            Subtype = Test;

            [Test]
            procedure OpenInternalSystemTableFromCloud()
            var
                RecRef: RecordRef;
            begin
                RecRef.Open(2000000071);
            end;
        }
        """);

        var (output, exit) = RunRunner(dir);

        Assert.Contains("NavNCLNotAllowedForCompilationTargetException: " + BcRefusal, output);
        Assert.Contains("FAIL  Codeunit62680.OpenInternalSystemTableFromCloud", output);
        Assert.NotEqual(0, exit);
    }
}
