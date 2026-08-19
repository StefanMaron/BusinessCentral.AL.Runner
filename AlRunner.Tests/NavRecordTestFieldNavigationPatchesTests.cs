// NavRecordTestFieldNavigationPatchesTests — issue #1938.
//
// Pins the C# CONTRACT the fix depends on — not "what BC does" (that's the job of the
// companion corpus PR, StefanMaron/BusinessCentral.AL.Language.Tests#53, which proves the
// AL-observable TestField message is unaffected by LookupPageId against real BC). What's
// provable here without a full AL test run is the actual mechanism: NavRecord_GetPageToOpen
// (the Cecil-replaced NavRecord.GetPageToOpen) must swallow a NavMetadataNotFoundException
// raised while resolving the OPTIONAL CardFormID follow-through, and fall back to the
// table's plain LookupFormId, rather than letting the exception propagate — exactly the
// hijack #1938 reported (a table's LookupPageId pointing at a page the runner never built).
//
// The table under test declares LookupPageId as a raw, unresolvable page id (999999) — no
// page with that id exists anywhere the runner could build one, so NCLMetadata's own
// GetMetaApplicationObject(Page, 999999, ...) is guaranteed to throw
// NavMetadataNotFoundException. That guarantee is asserted directly (first test) so the
// "GetPageToOpen doesn't throw" assertion (second test) is provably not vacuous: if the
// precondition it relies on ever stopped throwing, the first test would fail and flag it.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class NavRecordTestFieldNavigationPatchesTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public NavRecordTestFieldNavigationPatchesTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-testfield-nav-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteTableDir(int tableId, string name, int lookupPageId)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.al"), $$"""
            table {{tableId}} "TestFieldNav {{name}}"
            {
                LookupPageId = {{lookupPageId}};
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
        return dir;
    }

    private static NCLMetaTable? TryResolveTable(int tableId)
    {
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null) return null;
        try
        {
            return RecordPatches.NCLMetadata_GetMetaTableById(skeleton, tableId, false, 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Precondition guarantee: an id nothing built (999999) really does make the shared
    // NCLMetadata lookup throw. If a future change made unresolvable pages return some
    // sentinel object instead of throwing, this test — not the guard test below — is the
    // one that would catch it, keeping the guard test's own "doesn't throw" claim honest.
    [SkippableFact]
    public void UnresolvablePageId_MakesNCLMetadataLookup_ThrowNavMetadataNotFoundException()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        Assert.Throws<NavMetadataNotFoundException>(() =>
            RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
                skeleton!, ObjectType.Page, 999999, requireCompiled: true, emitVersion: 0));
    }

    [SkippableFact]
    public void GetPageToOpen_UnresolvablePageId_DegradesToPlainLookupFormId_InsteadOfThrowing()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var dir = WriteTableDir(93720, "GetPageToOpenGuard", 999999);
        RecordPatches.AddSourceDirs(new[] { dir });

        var meta = TryResolveTable(93720);
        Assert.NotNull(meta);
        // Sanity: the table's LookupPageId really did resolve to the raw literal — the
        // exact NCLMetaTable.LookupFormId shape #1926 started populating.
        Assert.Equal(999999, meta!.LookupFormId);

        // The mechanism under test (#1938's fix): resolving the OPTIONAL CardFormID
        // follow-through for an unbuildable page must not throw — it degrades to the
        // table's own plain LookupFormId, exactly like the "no CardFormID upgrade
        // available" branch NavRecord.GetPageToOpen already had for a page it CAN load.
        // Before the fix this call throws NavMetadataNotFoundException.
        var pageToOpen = RecordPatches.NavRecord_GetPageToOpen(meta);
        Assert.Equal(999999, pageToOpen);
    }

    // Negative-shaped control: a table declaring NO LookupPageId/DrillDownPageId at all
    // (both default to 0) must still report 0 — the guard must not turn "genuinely no
    // lookup page" into some other value by mistake.
    [SkippableFact]
    public void GetPageToOpen_NoLookupPageIdDeclared_ReturnsZero()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var dir = Path.Combine(_root, "NoLookup");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "NoLookup.al"), """
            table 93721 "TestFieldNav NoLookup"
            {
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
        RecordPatches.AddSourceDirs(new[] { dir });

        var meta = TryResolveTable(93721);
        Assert.NotNull(meta);
        Assert.Equal(0, meta!.LookupFormId);
        Assert.Equal(0, RecordPatches.NavRecord_GetPageToOpen(meta));
    }
}
