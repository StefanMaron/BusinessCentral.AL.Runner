// DefineFlagIntegrationTests — real RED→GREEN guard for --define / --preprocessor-symbols.
//
// Ghost test problem: the previous runner-extras suite gated BOTH the helper and
// the assertion on #if MY_TEST_SYMBOL, so it passed 0=0 without the flag and 1=1
// with it — both green regardless of whether the symbol ever reached ParseOptions.
// Deleting the `.Concat(_extraPreprocessorSymbols ?? [])` line would leave every
// test green.
//
// Fix: the [Test] codeunit below asserts UNCONDITIONALLY that GetCompiledBranch()=1.
// Only the helper is gated by #if. Without --define the #else branch compiles and
// the assertion fails (FAIL, non-zero exit). With --define MY_TEST_SYMBOL the #if
// branch compiles and the assertion passes (PASS, exit 0 with --strict).
//
// Test A proves the flag is necessary (RED without it).
// Test B proves the flag works (GREEN with --define).
// Test C proves the alias --preprocessor-symbols works (GREEN).
// Reverting the .Concat line makes B and C fail → real guard.
//
// #1900 — coverage for a #if-gated TABLE FIELD, not just a #if-gated procedure.
// BcCompiler.Emit's two ParseOptions sites merged --define with the compiler, but
// RecordPatches.AlSourceParser's ParseOptions (used to build table metadata for the
// in-memory record provider) did not — the compiler and the metadata parser disagreed
// about which #if branch was live. Two shapes:
//   - loud: FieldExist(20)/FieldExist(21)/RecordRef.Get round-trip on a field whose
//     NUMBER differs per branch — this throws NavNCLFieldNotFoundException outright.
//   - quiet: a field whose declared TYPE differs per branch (Enum vs. Option) but whose
//     field NUMBER is the same in both branches — this round-trips a value fine even
//     against the wrong branch's metadata (the ordinals happen to coincide), so only
//     FieldRef.OptionMembers (which differs: the Enum's member names vs. the dead
//     branch's option literals) catches the divergence.
// Both shapes are gated on the SAME --define symbol as the existing procedure test, so
// WithDefine_TestPasses / WithPreprocessorSymbols_TestPasses already prove they pass
// together with the pre-existing procedure test; FieldGatedByDefine_* below additionally
// names the shapes explicitly, per issue #1900's acceptance criteria.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Used to be [Collection("server-serial")] with every other runner-subprocess
// integration test — see #1809. That was a real, documented reaction to real SIGBUS
// (exit 135) crashes, not habit: each of these classes spawns a real `dotnet run
// --project AlRunner` process (native BC engine, R2R/EventPipe), and running several
// concurrently under xUnit's default parallelization used to hit them. Root cause
// (found later, same v2-cutover work): NclCecilRewrite.RewriteInPlace published the
// rewritten Ncl.dll with a plain truncate-in-place write; every loaded assembly is
// memory-mapped, so a second process's page fault against the half-written file
// raised SIGBUS. That was fixed with an atomic temp-file+rename publish (see
// NclCecilRewrite.AtomicReplace) well before this comment, and the AL-output cache
// got the same atomic-publish treatment in #1810. #1808 additionally stopped these
// tests from going through `dotnet run` at all — TestBuildConfig.RunArgs invokes the
// built al-runner.dll directly. #1809 removed the serialization now that its actual
// cause is fixed; see that issue for the investigation.
public sealed class DefineFlagIntegrationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public DefineFlagIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-define-flag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package to <paramref name="dir"/>:
    ///   - app.json (no dependencies, id range 62100..62119)
    ///   - Assert codeunit (integer AreEqual)
    ///   - Test codeunit: asserts UNCONDITIONALLY that GetCompiledBranch() == 1.
    ///     Only the helper (GetCompiledBranch) is #if-gated.
    ///     Without MY_TEST_SYMBOL: #else → exit(0) → 1≠0 → FAIL.
    ///     With    MY_TEST_SYMBOL: #if  → exit(1) → 1=1 → PASS.
    ///   - Table 62102 "PPD Define Gated": field 20 in the #if branch, field 21 in the
    ///     #else branch — the "loud" #1900 shape (a missing/extra field NUMBER).
    ///   - Enum 62104 "PPD Status" + Table 62103 "PPD Type Gated": field 10's declared
    ///     TYPE differs per branch (Enum vs. Option) but its NUMBER does not — the
    ///     "quiet" #1900 shape.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "f1a2b3c4-d5e6-7890-abcd-ef1234567890",
          "name": "Define Flag Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62100, "to": 62119 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), """
        codeunit 62101 "DFT Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        // "Loud" #1900 shape: field 20 exists only in the #if branch, field 21 only in
        // the #else branch. If the metadata parser and the compiler disagree about which
        // branch is live, either FieldExist(20) is false when it must be true, or
        // FieldExist(21) is true when it must be false, or the round trip throws
        // NavNCLFieldNotFoundException outright.
        File.WriteAllText(Path.Combine(dir, "DefineGated.Table.al"), """
        table 62102 "PPD Define Gated"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
        #if MY_TEST_SYMBOL
                field(20; "Active Branch"; Text[30]) { }
        #else
                field(21; "Dead Branch"; Text[30]) { }
        #endif
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        // "Quiet" #1900 shape: field 10 exists in BOTH branches with the SAME number, so
        // a value written through the #if branch's Enum type round-trips fine even
        // against the #else branch's wrong Option metadata (matching ordinals). Only
        // FieldRef.OptionMembers — which differs (the enum's member names vs. the dead
        // branch's literal option strings) — catches the wrong branch having been parsed.
        File.WriteAllText(Path.Combine(dir, "TypeGated.Enum.al"), """
        enum 62104 "PPD Status"
        {
            Extensible = true;

            value(0; Open) { }
            value(1; Closed) { }
        }
        """);

        File.WriteAllText(Path.Combine(dir, "TypeGated.Table.al"), """
        table 62103 "PPD Type Gated"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
        #if MY_TEST_SYMBOL
                field(10; "Status"; Enum "PPD Status") { }
        #else
                field(10; "Status"; Option) { OptionMembers = Dead0,Dead1; }
        #endif
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(dir, "DefineFlagTest.Codeunit.al"), """
        codeunit 62100 "Define Flag Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "DFT Assert";

            // The assertion is UNCONDITIONAL: always expects 1.
            // Only GetCompiledBranch is #if-gated.
            // Without MY_TEST_SYMBOL: #else compiles → exit(0) → 1≠0 → FAIL.
            // With    MY_TEST_SYMBOL: #if  compiles → exit(1) → 1=1 → PASS.
            [Test]
            procedure SymbolDefinedBranchMustBe1()
            begin
                Assert.AreEqual(1, GetCompiledBranch(), 'MY_TEST_SYMBOL must be defined');
            end;

            local procedure GetCompiledBranch(): Integer
            begin
        #if MY_TEST_SYMBOL
                exit(1);
        #else
                exit(0);
        #endif
            end;

            /// Positive: field 20 is in the branch the COMPILER took, so it must exist
            /// at runtime too.
            [Test]
            procedure ActiveBranchFieldIsPresentInTableMetadata()
            var
                Rec: Record "PPD Define Gated";
                RecRef: RecordRef;
            begin
                RecRef.GetTable(Rec);
                if not RecRef.FieldExist(20) then
                    Error('Field 20 belongs to the ACTIVE #if MY_TEST_SYMBOL branch but is absent from table 62102 metadata.');
                RecRef.Close();
            end;

            /// Negative: field 21 is in the branch the compiler DISCARDED, so it must
            /// not exist.
            [Test]
            procedure DeadBranchFieldIsAbsentFromTableMetadata()
            var
                Rec: Record "PPD Define Gated";
                RecRef: RecordRef;
            begin
                RecRef.GetTable(Rec);
                if RecRef.FieldExist(21) then
                    Error('Field 21 belongs to the INACTIVE #else branch but is PRESENT in table 62102 metadata.');
                RecRef.Close();
            end;

            /// Positive: the active-branch field is usable end to end. Deliberately
            /// written through RecordRef/FieldRef rather than the strongly-typed
            /// `Rec."Active Branch"` accessor — the strongly-typed name only exists in
            /// the #if MY_TEST_SYMBOL branch, and this codeunit must still COMPILE
            /// without --define too (so the pre-existing SymbolDefinedBranchMustBe1
            /// RED/GREEN pair keeps working). RecRef.Field(20) resolves at runtime, so
            /// without --define it throws NavNCLFieldNotFoundException — a real FAIL,
            /// not a compile error — exactly the exception the #1900 issue reports.
            [Test]
            procedure ActiveBranchFieldRoundTrips()
            var
                Rec: Record "PPD Define Gated";
                RecRef: RecordRef;
                ActiveValue: Text;
            begin
                RecRef.GetTable(Rec);
                RecRef.Field(1).Value := 'D1';
                RecRef.Field(20).Value := 'active-value';
                RecRef.Insert();
                RecRef.Close();

                Clear(RecRef);
                RecRef.GetTable(Rec);
                RecRef.Field(1).SetRange('D1');
                if not RecRef.FindFirst() then
                    Error('Row ''D1'' not found after insert.');
                ActiveValue := Format(RecRef.Field(20).Value);
                if ActiveValue <> 'active-value' then
                    Error('Expected ''active-value'' but got ''%1''', ActiveValue);
                RecRef.Close();
            end;

            /// The "quiet" #1900 shape: field 10 exists in both branches with the same
            /// number, so a naive round-trip test would pass even against the wrong
            /// branch's metadata. FieldRef.OptionMembers carries the DECLARED TYPE's
            /// member names, which differ per branch (the Enum's Open/Closed vs. the
            /// dead branch's Dead0/Dead1 literals) — this is what actually distinguishes
            /// "metadata parsed the active branch" from "metadata parsed the dead one".
            [Test]
            procedure ActiveBranchFieldOptionMembersMatchEnum()
            var
                Rec: Record "PPD Type Gated";
                RecRef: RecordRef;
                FldRef: FieldRef;
            begin
                RecRef.GetTable(Rec);
                FldRef := RecRef.Field(10);
                if FldRef.OptionMembers <> 'Open,Closed' then
                    Error('Field 10 metadata must carry the ACTIVE #if MY_TEST_SYMBOL branch''s Enum "PPD Status" member names (Open,Closed), got ''%1''', FldRef.OptionMembers);
                RecRef.Close();
            end;
        }
        """);
    }

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --strict");
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Without --define the #else branch compiles, the unconditional assert(1==0)
    /// fails, and the runner exits non-zero (--strict).  This is the RED proof.
    /// </summary>
    [SkippableFact]
    public void WithoutDefine_TestFails()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();

        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL", output);
    }

    /// <summary>
    /// With --define MY_TEST_SYMBOL the #if branch compiles, the assert(1==1)
    /// passes, and the runner exits 0.  Reverting the .Concat line breaks this test.
    /// </summary>
    [SkippableFact]
    public void WithDefine_TestPasses()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--define MY_TEST_SYMBOL");

        Assert.Equal(0, exit);
        Assert.Contains("PASS", output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>
    /// Same as WithDefine_TestPasses but uses --preprocessor-symbols (the batch alias).
    /// </summary>
    [SkippableFact]
    public void WithPreprocessorSymbols_TestPasses()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--preprocessor-symbols MY_TEST_SYMBOL");

        Assert.Equal(0, exit);
        Assert.Contains("PASS", output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>
    /// Without --define, compiler and metadata parser agree (both take the #else
    /// branch for every #if in the fixture) — the #1900 defect only shows up once the
    /// two DISAGREE, which needs --define to be passed. What's still true without the
    /// flag: the fixture's UNCONDITIONAL procedure-branch assertion
    /// (SymbolDefinedBranchMustBe1) fails, so the runner exits non-zero. This is the
    /// RED half of the pair, mirroring WithoutDefine_TestFails but naming the exact
    /// failing test for this fixture's expanded #1900 coverage.
    /// </summary>
    [SkippableFact]
    public void FieldGatedByDefine_WithoutFlag_ProcedureBranchFails()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();

        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL  Codeunit62100.SymbolDefinedBranchMustBe1", output);
    }

    /// <summary>
    /// #1900 GREEN: with --define MY_TEST_SYMBOL, BOTH the "loud" shape (field 20/21 —
    /// a field NUMBER that only exists in the #if branch) and the "quiet" shape (field
    /// 10's declared TYPE, Enum vs. Option, with the SAME number in both branches) pass.
    /// Reverting RecordPatches.AlSourceParser's AlParseOptions to the pre-fix
    /// `static readonly` field (or a `.Concat` bolted onto it, per the issue's warning)
    /// makes all four of these named assertions fail while the pre-existing procedure
    /// test (SymbolDefinedBranchMustBe1) keeps passing — proving the fix is not
    /// redundant with the procedure-level guard the class already had.
    /// </summary>
    [SkippableFact]
    public void FieldGatedByDefine_WithFlag_LoudAndQuietShapesBothPass()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--define MY_TEST_SYMBOL");

        Assert.Equal(0, exit);
        Assert.Contains("PASS  Codeunit62100.ActiveBranchFieldIsPresentInTableMetadata", output);
        Assert.Contains("PASS  Codeunit62100.DeadBranchFieldIsAbsentFromTableMetadata", output);
        Assert.Contains("PASS  Codeunit62100.ActiveBranchFieldRoundTrips", output);
        Assert.Contains("PASS  Codeunit62100.ActiveBranchFieldOptionMembersMatchEnum", output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }
}
