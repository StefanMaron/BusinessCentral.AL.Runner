// AssertErrorSymbolReadCatchabilityTests — what AL's error-trapping seams do with a
// BcAppSymbolReadException (issue #3241).
//
// THE DEFECT
//   MethodScopePatches.NavMethodScope_AssertError — the Cecil-bound replacement for
//   NavMethodScope::AssertError/1 — rethrew only BcShapeGapException and caught everything
//   else. #3239 converted ten dependency-symbol reads from silent swallows to
//   BcAppSymbolReadException, several of them on AL-entered paths (GetInsertAllowedForPage,
//   IsPageShapeKnown, TryGetAnyPageType, the virtual-table walks). So `asserterror` around a
//   TestPage operation over a corrupt dependency package CAUGHT the refusal and the test
//   PASSED: on real BC the read succeeds and the asserterror fails ("expected an error"), so
//   swallowing here inverts the result rather than merely hiding a gap.
//
//   The RED for every "TearsThrough" row below is that the call returned normally.
//
// WHY THE SUBJECT IS PROVEN HERE AND NOT AS AN AL BUNDLE
//   The reachable shape is poison-AFTER-registration, and it is in-process by construction:
//   RecordPatches.AddBcAppPath parses table symbols eagerly, so a package that is already
//   corrupt on disk refuses at registration and Program.cs exits 1 before any AL runs
//   (RecordPatchesBcAppSymbolReadFailureTests). Only a package that was readable when it was
//   registered and unreadable when a lazy read drains it can reach an `asserterror` at all.
//   These rows drive the REAL NavMethodScope_AssertError over the REAL
//   RecordPatches.GetInsertAllowedForPage / IsPageShapeKnown against exactly that state.
//
//   Nothing here is a claim about Business Central — no AL statement can corrupt a dependency
//   package, so a service tier has nothing to adjudicate. Same reasoning as
//   BcShapeGapConventionTests and AssertErrorOutOfScopeCatchabilityTests
//   (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// THE CONTROLS
//   Every tear-through row is paired with something the seam must STILL catch — a plain BC
//   error and a permanent out-of-scope refusal — so no row can pass by a seam that stopped
//   catching anything, and the healthy arm proves the poisoned arm is measuring the poison
//   rather than a pipeline that never reads the .app.
//
// No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).

using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: calls RecordPatches.ResetForReload() directly (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class AssertErrorSymbolReadCatchabilityTests : IDisposable
{
    private readonly string _root;

    public AssertErrorSymbolReadCatchabilityTests()
    {
        _root = TestScratch.Dir("al-runner-3241-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string AppGuid = "c3241a00-3241-4a32-9a32-000000003241";

    // Process-wide unique among AlRunner.Tests statics — RecordPatches' dependency state is
    // process-global, so an id another fixture also declares could answer from its payload.
    private const int TableId = 88324101;
    private const int PageId = 88324103;

    // ── the fixture ──────────────────────────────────────────────────────────────────────

    // Poison technique, unchanged from DependencySymbolReadFailureTests (#3143): the root
    // "AppId" as a JSON NUMBER. BcAppSymbolCache.Parse calls ReadAppIdentity LAST, after every
    // container has been collected, so this is a parse that dies part-way AFTER the data the
    // sites want — not a whole-file failure any path would notice. The different LENGTH is
    // what gives the content-hash memo and the symbol cache a new key.
    private static string SymbolReference(bool poison)
    {
        var appId = poison ? "\"AppId\": 3241" : $"\"AppId\": \"{AppGuid}\"";
        return $$"""
            {
              "RuntimeVersion": "15.1",
              {{appId}},
              "Name": "Bug3241 Symbol App",
              "Tables": [
                {
                  "Id": {{TableId}},
                  "Name": "Bug3241 Table",
                  "Properties": [ { "Name": "Caption", "Value": "Bug3241 Table" } ],
                  "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code" } } ]
                }
              ],
              "Pages": [
                {
                  "Id": {{PageId}},
                  "Name": "Bug3241 Page",
                  "Properties": [
                    { "Name": "PageType", "Value": "List" },
                    { "Name": "SourceTable", "Value": "{{TableId}}" },
                    { "Name": "InsertAllowed", "Value": "false" }
                  ]
                }
              ]
            }
            """;
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    private string RegisterHealthy(string fileName)
    {
        var appPath = Path.Combine(_root, fileName);
        WriteApp(appPath, SymbolReference(poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        return appPath;
    }

    /// <summary>
    /// Register a healthy .app, then rewrite it on disk into the poisoned shape — the only
    /// state that can reach an AL `asserterror` at all (see this file's header).
    /// </summary>
    private string RegisterThenPoison(string fileName = "dep3241.app")
    {
        var appPath = RegisterHealthy(fileName);
        WriteApp(appPath, SymbolReference(poison: true));
        File.SetLastWriteTimeUtc(appPath, File.GetLastWriteTimeUtc(appPath).AddSeconds(5));
        return appPath;
    }

    private static BcAppSymbolReadException Refusal() =>
        new("/artifacts/Some.Publisher_Some App_1.0.0.0.app", "table symbols",
            new InvalidOperationException("The requested operation requires an element of type 'String'."));

    private const string AssertErrorTypeName =
        "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException";

    // ── the defect, over the real AL-entered read ────────────────────────────────────────

    [Fact]
    public void AssertError_TearsThrough_WhenAnAlEnteredPageReadHitsACorruptDependency()
    {
        var appPath = RegisterThenPoison();

        // RED: this returned normally — NavMethodScope_AssertError caught the refusal, so the
        // AL `asserterror` PASSED on a read real BC performs fine.
        var ex = Assert.Throws<BcAppSymbolReadException>(
            () => BcRuntime.NavMethodScope_AssertError(
                null!, () => RecordPatches.GetInsertAllowedForPage(PageId)));

        Assert.Contains("symbol-read-fail", ex.Message);
        Assert.Contains(Path.GetFileName(appPath), ex.Message);
        Assert.Equal(appPath, ex.AppPath);
    }

    [Fact]
    public void AssertError_TearsThrough_WhenAnAlEnteredPageShapeProbeHitsACorruptDependency()
    {
        RegisterThenPoison("dep3241-shape.app");

        // The sibling read on the same AL-entered path: IsPageShapeKnown answered `false`
        // before #3239 and is caught by `asserterror` before this fix.
        var ex = Assert.Throws<BcAppSymbolReadException>(
            () => BcRuntime.NavMethodScope_AssertError(
                null!, () => RecordPatches.IsPageShapeKnown(PageId)));

        Assert.Contains("pages and pageextensions", ex.Message);
    }

    [Fact]
    public void AssertError_Fails_WhenTheSameReadSucceeds()
    {
        // The positive control for both rows above: with the .app healthy the body completes,
        // and the asserterror replacement's own failure signal is what comes back — so the
        // poisoned rows are measuring the poison, not a pipeline that never reads the .app.
        RegisterHealthy("healthy3241.app");

        Assert.False(RecordPatches.GetInsertAllowedForPage(PageId),
            "the fixture page declares InsertAllowed = false");

        var ex = Record.Exception(
            () => BcRuntime.NavMethodScope_AssertError(
                null!, () => RecordPatches.GetInsertAllowedForPage(PageId)));

        Assert.NotNull(ex);
        Assert.Equal(AssertErrorTypeName, ex!.GetType().FullName);
    }

    // ── the same claim, raised directly and wrapped ──────────────────────────────────────

    [Fact]
    public void AssertError_TearsThrough_WhenTheRefusalIsRaisedDirectly()
    {
        var raised = Refusal();
        var ex = Assert.Throws<BcAppSymbolReadException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => throw raised));
        Assert.Same(raised, ex);
    }

    [Fact]
    public void AssertError_TearsThrough_WhenTheRefusalArrivesWrapped()
    {
        // A refusal raised behind MethodBase.Invoke comes back inside a
        // TargetInvocationException, and BC's own RemapToALExceptionAndThrow can rewrap it
        // again — an `is` test would miss both, which is why the seam asks Find().
        var raised = Refusal();
        var ex = Assert.Throws<TargetInvocationException>(
            () => BcRuntime.NavMethodScope_AssertError(
                null!, () => throw new TargetInvocationException(raised)));
        Assert.Same(raised, ex.InnerException);
    }

    // ── the controls: the seam still catches what it is supposed to ──────────────────────

    [Fact]
    public void AssertError_StillPasses_WhenTheBodyRaisesAnOrdinaryError()
    {
        // Returning normally IS the pass signal. Without this row, "the seam stopped catching
        // anything at all" would satisfy every tear-through row above.
        BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new InvalidOperationException("Bug3241 ordinary AL error"));
    }

    [Fact]
    public void AssertError_StillPasses_WhenTheBodyRaisesAPermanentOutOfScopeRefusal()
    {
        // #2871's contract, deliberately untouched — the discrimination is by TYPE, so a fix
        // that widened the filter to "anything the runner raises" would fail here.
        BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new RunnerOutOfScopeException("NavFile.Upload", "browser-roundtrip", "file-storage"));
    }

    // ── the other AL trapping seam ───────────────────────────────────────────────────────

    [Fact]
    public void TryInvoke_TearsThrough_WhenTheRefusalArrivesWrapped()
    {
        // NavApplicationObjectBase_TryInvoke's ordering matters for the same reason as the
        // asserterror seam's: bare, the refusal already reached the final `throw`; remapped
        // into a trappable NavBaseException it would have been swallowed into `false`.
        var raised = Refusal();
        var ex = Assert.Throws<TargetInvocationException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(
                null, () => throw new TargetInvocationException(raised)));
        Assert.Same(raised, ex.InnerException);
    }

    [Fact]
    public void TryInvoke_StillTraps_APermanentRefusal()
    {
        // The control: without it, "tears through" would be satisfied by a TryInvoke that
        // trapped nothing at all.
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw new RunnerOutOfScopeException("NavFile.Upload", "browser-roundtrip", "file-storage")));
    }

    [Fact]
    public void TryInvoke_StillAnswersTrue_WhenTheBodySucceeds()
    {
        Assert.True(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => { }));
    }
}
