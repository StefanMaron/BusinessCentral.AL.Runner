// BuiltMetadataCacheBundleBoundaryTests — the two siblings found by scanning the reset path
// while fixing #3210 and #3172. Neither had an issue of its own; both are the same shape as
// #2478, #2755, #3207, #3210 and #3172, and both are fixed in the same PR because the fix is the
// same two lines in the same method.
//
//   RecordPatches._metaPermissionSetCache   permission set id -> built NCLMetaPermissionSet
//   RecordPatches._tableExtensionTypeCache  tableextension id -> emitted CLR type
//
// Neither was cleared by anything, on any path, and both are derived from state ResetForReload
// itself discards:
//
//   * _metaPermissionSetCache's entries come from EnumerateKnownPermissionSets(), i.e. from
//     _parsedPermissionSets (cleared by ResetForReload) plus the registered .app set (cleared by
//     ClearPerBundleBcAppPaths, #2755). The NULL answer is cached too, so an id that was unknown
//     to bundle 1 kept producing BC's NavMetadataNotFoundException for the rest of a --server /
//     --watch process even after a later bundle declared it.
//   * _tableExtensionTypeCache's comment says outright that it mirrors _recordTypeCache — which
//     ResetForReload has always cleared, on the first line of the method. Both map an AL object
//     id to a CLR type resolved out of the emitted test assembly, so both name a generation of
//     types the reload is replacing. Left stale, FindTableExtensionType(extId) hands back the
//     PREVIOUS bundle's TableExtension{id} while every record type around it comes from the new
//     one: #1683's two-live-modules-for-one-AL-identity shape, arriving through a cache instead
//     of through a loader, and silent — the stale extension binds to the new record and its
//     fields read the wrong storage.
//
// Not a claim about Business Central in either case: the subject is the lifetime of a runner
// cache across the runner's own bundle-reload boundary.
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: every case calls RecordPatches.ResetForReload() and drives the AL source
// parsers into their process-global dictionaries.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BuiltMetadataCacheBundleBoundaryTests : IDisposable
{
    // Ids nothing in this repository declares, so bundle 1 below is genuinely a bundle that does
    // not know about them.
    private const int PermissionSetId = 79931;
    private const int TableExtensionId = 79941;

    private readonly string _root;

    public BuiltMetadataCacheBundleBoundaryTests()
    {
        _root = TestScratch.Dir("al-runner-built-metadata-boundary");
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
    }

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void APermissionSetUnknownToBundleOne_IsFoundOnceBundleTwoDeclaresIt()
    {
        // Bundle 1 declares no permission sets at all, so the production entry point answers
        // "no such permission set" — and memoizes that null.
        Assert.Null(RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId));
        Assert.Equal(1, RecordPatches.PermissionSetMetadataCacheCountForTests());

        // The bundle boundary. Deliberately NOT followed by an emptiness check here: that is the
        // sibling case's job, and asserting it here would make this test fail on the unfixed tree
        // BEFORE reaching the behavioural claim below, which is the one worth reading.
        RecordPatches.ResetForReload();

        // Bundle 2 declares it. This is the behavioural half, and it is available here (unlike on
        // the query and xmlport caches) because BuildNclMetaPermissionSet reaches its declaration
        // lookup BEFORE any BC reflection: with a declaration in hand it either builds the
        // NCLMetaPermissionSet (BC engine loaded in this test host) or raises the loud
        // BcShapeGapException (engine absent). What it can never do is return null — so
        // "answered null and did not throw" means one thing only: the memoized negative from
        // bundle 1 was served, and the declaration was never even looked at.
        var dir = Path.Combine(_root, "bundle-2");
        Directory.CreateDirectory(dir);
        // The permission-set parser DROPS a declaration whose owning app.json it cannot find
        // (ResolveOwningApp) rather than attributing it to an invented app id, so the manifest is
        // load-bearing here, not decoration. No "application" property: that is the Base
        // Application floor, and no-base-app-in-csharp-tests.md forbids it in a C# fixture.
        File.WriteAllText(Path.Combine(dir, "app.json"),
            """
            {
              "id": "b3f1c0de-7931-4a11-9c7e-000000079931",
              "name": "BMCB Bundle Two",
              "publisher": "AL Runner",
              "version": "1.0.0.0"
            }
            """);
        var alPath = Path.Combine(dir, "BmcbFixture.PermissionSet.al");
        File.WriteAllText(alPath,
            $"permissionset {PermissionSetId} \"BMCB Bundle Two Set\"\n"
            + "{\n    Caption = 'BMCB Bundle Two Set';\n    Assignable = true;\n}\n");
        ParseOneSourceFile(alPath);

        // Asserted, not assumed: if the source sweep had not picked the declaration up, the claim
        // below would be about the parser, not about the cache.
        Assert.Contains(RecordPatches.ParsedPermissionSets, p => p.Id == PermissionSetId);

        object? built = null;
        Exception? raised = null;
        try { built = RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId); }
        catch (Exception ex) { raised = ex; }

        // Deliberately not pinned to one exception type. Past the declaration lookup the build
        // is BC's, and what comes back depends on how much of the engine this host happens to
        // have: a real NCLMetaPermissionSet when it is bootstrapped, BcShapeGapException when
        // Ncl's shape is missing, a NavEnvironment type-initializer failure when Ncl is loaded
        // but no session was ever stood up (what a plain `dotnet test` host produces). All three
        // mean the same thing and it is the only thing being claimed: the declaration was
        // REACHED. Answering null without raising anything is reachable on exactly one path —
        // the memoized negative from bundle 1 — which is the path this PR removes.
        Assert.False(built == null && raised == null,
            "bundle 2 declares permission set " + PermissionSetId + " and the lookup still "
            + "answered null without attempting a build — bundle 1's memoized 'no such "
            + "permission set' outlived the _parsedPermissionSets clear it was derived from");
    }

    [Fact]
    public void APermissionSetMetadataEntryBuiltInBundleOne_DoesNotSurviveTheReload()
    {
        // The plain reset-contract direction, and the control that the memo really memoizes
        // within one bundle: two ids asked twice each is two entries, not four builds.
        RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId);
        RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId + 1);
        RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId);
        RecordPatches.EnsurePermissionSetInMetadataCache(PermissionSetId + 1);
        Assert.Equal(2, RecordPatches.PermissionSetMetadataCacheCountForTests());

        RecordPatches.ResetForReload();

        Assert.Equal(0, RecordPatches.PermissionSetMetadataCacheCountForTests());
    }

    [Fact]
    public void ATableExtensionTypeResolvedInBundleOne_DoesNotSurviveTheReload()
    {
        // Seeded rather than driven: FindTableExtensionType caches HITS only, and a hit needs a
        // real emitted NavRecordExtension subclass named TableExtension{id} in a loaded assembly
        // — i.e. a compiled bundle, which a unit test host has none of. The claim is therefore
        // the reset contract, not the downstream mis-binding described in the file header.
        var cache = TableExtensionTypeCache();
        cache[TableExtensionId] = typeof(BuiltMetadataCacheBundleBoundaryTests);
        Assert.True(cache.Contains(TableExtensionId));

        RecordPatches.ResetForReload();

        // On the unfixed tree the entry survives, and bundle 2's TableExtension79941 lookup is
        // answered with bundle 1's CLR type. _recordTypeCache — which this cache's own comment
        // says it mirrors — is cleared on the first line of the same method.
        Assert.Empty(TableExtensionTypeCache());
    }

    /// <summary>
    /// Run the production per-file source sweep over one .al file. Called instead of
    /// <c>AddSourceDir</c> because that entry point only parses when <c>Register()</c> has already
    /// run — i.e. when the BC engine is standing up in-process, which a unit-test host does not
    /// guarantee and which would make this case skip on some boxes and run on others. This is the
    /// same private method <c>AddSourceDirs</c> calls per file, so the declaration still lands in
    /// the production dictionary by the production route.
    /// </summary>
    private static void ParseOneSourceFile(string alFilePath)
    {
        var m = typeof(RecordPatches).GetMethod("ParseSourceFileIntoAllExtractors",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.ParseSourceFileIntoAllExtractors not found — this test drives that sweep.");
        m.Invoke(null, new object?[] { File.ReadAllText(alFilePath), alFilePath });
    }

    /// <summary>Read by reflection: no public surface reports it, and inferring "it was cleared"
    /// from a later lookup cannot tell a cleared cache from one that agrees with this bundle.
    /// </summary>
    private static IDictionary TableExtensionTypeCache()
    {
        var field = typeof(RecordPatches).GetField("_tableExtensionTypeCache",
                        BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RecordPatches._tableExtensionTypeCache not found — this test tracks that field.");
        return (IDictionary)field.GetValue(null)!;
    }
}
