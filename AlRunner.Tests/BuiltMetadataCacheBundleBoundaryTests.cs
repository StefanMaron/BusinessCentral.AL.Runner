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
using System.Collections.Generic;
using System.Linq;
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

    // ── #3226: the two memos on the INPUT side of the profile / permission-set re-parse ──────
    //
    // ResetForReload clears _parsedProfiles and _parsedPermissionSets so an edited bundle's
    // declarations are re-read. _owningAppByDir (directory -> nearest app.json identity) and
    // _appOwnerCache (app id -> NavAppRuntimeMetadata) sit in front of that re-read and were
    // cleared by nothing, so a --watch cycle that edits app.json re-parsed every declaration
    // and attributed it to the PREVIOUS identity.

    private const string OwningAppIdOne = "e2c8a4b1-3226-4a11-9c7e-000000032261";
    private const string OwningAppIdTwo = "e2c8a4b1-3226-4a11-9c7e-000000032262";

    [Fact]
    public void AnAppJsonRenamedBetweenBundles_IsReReadRatherThanServedFromTheDirectoryMemo()
    {
        var dir = Path.Combine(_root, "renamed-app");
        Directory.CreateDirectory(dir);
        var profile = WriteProfile(dir, "BMCB Rename Profile");
        var permissionSet = WritePermissionSet(dir, PermissionSetId + 10, "BMCB Rename Set");

        WriteAppJson(dir, OwningAppIdOne, "BMCB Owner One");
        ParseOneSourceFile(profile);
        ParseOneSourceFile(permissionSet);
        Assert.Equal("BMCB Owner One", ProfileNamed("BMCB Rename Profile").AppName);
        Assert.Equal("BMCB Owner One", PermissionSetNamed("BMCB Rename Set").AppName);

        // The bundle boundary, then the edit a --watch cycle picks up: same app id, new name.
        RecordPatches.ResetForReload();
        WriteAppJson(dir, OwningAppIdOne, "BMCB Owner Two");
        ParseOneSourceFile(profile);
        ParseOneSourceFile(permissionSet);

        Assert.Equal("BMCB Owner Two", ProfileNamed("BMCB Rename Profile").AppName);
        Assert.Equal("BMCB Owner Two", PermissionSetNamed("BMCB Rename Set").AppName);
    }

    [Fact]
    public void AnAppJsonReIdentifiedBetweenBundles_IsReReadRatherThanServedFromTheDirectoryMemo()
    {
        var dir = Path.Combine(_root, "reidentified-app");
        Directory.CreateDirectory(dir);
        var profile = WriteProfile(dir, "BMCB Reid Profile");
        var permissionSet = WritePermissionSet(dir, PermissionSetId + 11, "BMCB Reid Set");

        WriteAppJson(dir, OwningAppIdOne, "BMCB Owner One");
        ParseOneSourceFile(profile);
        ParseOneSourceFile(permissionSet);
        Assert.Equal(Guid.Parse(OwningAppIdOne), ProfileNamed("BMCB Reid Profile").AppId);

        RecordPatches.ResetForReload();
        WriteAppJson(dir, OwningAppIdTwo, "BMCB Owner One");
        ParseOneSourceFile(profile);
        ParseOneSourceFile(permissionSet);

        // Both dictionaries are keyed by (AppId, Name), so a stale id is not merely a wrong
        // column — it is a wrong primary key, and All Profile's own key is (Scope, App ID,
        // Profile ID). Assert the new id is there AND the old one is gone: a surviving
        // bundle-1 entry would satisfy the first half on its own.
        Assert.Equal(Guid.Parse(OwningAppIdTwo), ProfileNamed("BMCB Reid Profile").AppId);
        Assert.Equal(Guid.Parse(OwningAppIdTwo), PermissionSetNamed("BMCB Reid Set").AppId);
        Assert.DoesNotContain(RecordPatches.ParsedProfiles,
            p => p.AppId == Guid.Parse(OwningAppIdOne));
        Assert.DoesNotContain(RecordPatches.ParsedPermissionSets,
            p => p.AppId == Guid.Parse(OwningAppIdOne));
    }

    [Fact]
    public void ADeclarationWithNoAppJsonAboveIt_IsStillDroppedAfterTheReload_Control()
    {
        // The control for the two cases above: clearing the directory memo must re-run the
        // walk-up, not weaken it. #2357's rule is that a declaration whose owning app.json
        // cannot be found is DROPPED rather than attributed to an invented app id, and a memo
        // that no longer answers must not turn that drop into an attribution. Passes before and
        // after the fix — it is here so a future "just default the identity" cannot land quietly.
        var orphan = Path.Combine(_root, "no-manifest");
        Directory.CreateDirectory(orphan);
        var orphanProfile = WriteProfile(orphan, "BMCB Orphan Profile");
        var orphanSet = WritePermissionSet(orphan, PermissionSetId + 12, "BMCB Orphan Set");

        // A sibling that DOES have a manifest, so "dropped" is distinguishable from "the sweep
        // never ran": the two are parsed by the same loop in the same state.
        var owned = Path.Combine(_root, "with-manifest");
        Directory.CreateDirectory(owned);
        WriteAppJson(owned, OwningAppIdOne, "BMCB Owner One");
        var ownedProfile = WriteProfile(owned, "BMCB Owned Profile");

        foreach (var f in new[] { orphanProfile, orphanSet, ownedProfile }) ParseOneSourceFile(f);
        RecordPatches.ResetForReload();
        foreach (var f in new[] { orphanProfile, orphanSet, ownedProfile }) ParseOneSourceFile(f);

        Assert.Contains(RecordPatches.ParsedProfiles, p => p.ProfileId == "BMCB Owned Profile");
        Assert.DoesNotContain(RecordPatches.ParsedProfiles, p => p.ProfileId == "BMCB Orphan Profile");
        Assert.DoesNotContain(RecordPatches.ParsedPermissionSets, p => p.Name == "BMCB Orphan Set");
    }

    [Fact]
    public void TheAppOwnerMetadataBuiltInBundleOne_DoesNotSurviveTheReload()
    {
        // Seeded rather than driven, for the same reason the tableextension case below is:
        // GetOrCreateAppOwner constructs a real NavAppRuntimeMetadata by reflection over Ncl,
        // which a plain unit-test host has not bootstrapped. The claim is the reset contract.
        //
        // Not redundant with the two behavioural cases: this cache is keyed by app id ALONE
        // while carrying the name, so a bundle that renames an app without changing its id
        // gets a correctly re-resolved (id, name) out of the directory walk-up and then a
        // NavAppRuntimeMetadata still carrying bundle 1's name.
        var cache = AppOwnerCache();
        cache[Guid.Parse(OwningAppIdOne)] = new object();
        Assert.True(cache.Contains(Guid.Parse(OwningAppIdOne)));

        RecordPatches.ResetForReload();

        Assert.Empty(AppOwnerCache());
    }

    [Fact]
    public void ThePopulatedPermissionSetCount_DoesNotSurviveTheReload()
    {
        // EnsurePermissionMetadataPopulated short-circuits on
        // `known.Count == _permMetaPopulatedForCount`, and nothing reset that latch. Without
        // this, the clear above is unobservable on the app-group path: bundle 2 declaring the
        // same NUMBER of permission sets under a renamed app never re-enters the populate at
        // all, so NavAppGroup.BaseGroup keeps bundle 1's summaries and their owner.
        PopulatedCountField().SetValue(null, 7);

        RecordPatches.ResetForReload();

        Assert.Equal(-1, (int)PopulatedCountField().GetValue(null)!);
    }

    // ── #3249: the count guard and the name index in the SAME file, both process-lifetime ────
    //
    // Folded in here because the fix lands in ResetPermissionSetMetadataForReload beside the
    // #3226 clears, in the same lock, in the same file. The count half is asserted by
    // ThePopulatedPermissionSetCount_DoesNotSurviveTheReload above; these two are the name
    // index, and they are behavioural rather than seeded — #3249 was filed asking whether an
    // engine-free seam existed for them, and it does: PermissionSetIdByName /
    // PermissionSetNameById read EnumerateKnownPermissionSets, which is _parsedPermissionSets
    // plus the registered .app paths and reaches no BC reflection at all.

    [Fact]
    public void ThePermissionSetNameIndex_ResolvesBundleTwosIdsRatherThanBundleOnes()
    {
        var dir = Path.Combine(_root, "name-index");
        Directory.CreateDirectory(dir);
        WriteAppJson(dir, OwningAppIdOne, "BMCB Owner One");

        // Bundle 1: one set named "BMCB Shared Set" at 79950.
        var setPath = Path.Combine(dir, "Shared.PermissionSet.al");
        File.WriteAllText(setPath,
            "permissionset 79950 \"BMCB Shared Set\"\n{\n    Assignable = true;\n}\n");
        ParseOneSourceFile(setPath);
        Assert.Equal(79950, IdByName()["BMCB Shared Set"]);
        Assert.Equal("BMCB Shared Set", NameById()[79950]);

        // Bundle 2: the same name at a DIFFERENT id, plus one the first bundle never declared.
        RecordPatches.ResetForReload();
        File.WriteAllText(setPath,
            "permissionset 79951 \"BMCB Shared Set\"\n{\n    Assignable = true;\n}\n"
            + "permissionset 79952 \"BMCB Second Set\"\n{\n    Assignable = true;\n}\n");
        ParseOneSourceFile(setPath);

        // The read this feeds is IncludedPermissionSets resolution: a name in bundle 2's
        // include list resolving to bundle 1's object id is a silent wrong answer, and a name
        // bundle 1 never saw is missing from the memo entirely.
        var byName = IdByName();
        Assert.Equal(79951, byName["BMCB Shared Set"]);
        Assert.Equal(79952, byName["BMCB Second Set"]);
        Assert.Equal("BMCB Shared Set", NameById()[79951]);
        Assert.DoesNotContain(79950, NameById().Keys);
    }

    private static Dictionary<string, int> IdByName()
        => (Dictionary<string, int>)typeof(RecordPatches).GetMethod("PermissionSetIdByName",
               BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    private static Dictionary<int, string> NameById()
        => (Dictionary<int, string>)typeof(RecordPatches).GetMethod("PermissionSetNameById",
               BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    private static void WriteAppJson(string dir, string id, string name)
        => File.WriteAllText(Path.Combine(dir, "app.json"),
            "{\n"
            + $"  \"id\": \"{id}\",\n"
            + $"  \"name\": \"{name}\",\n"
            + "  \"publisher\": \"AL Runner\",\n"
            + "  \"version\": \"1.0.0.0\"\n"
            + "}\n");

    private static string WriteProfile(string dir, string profileName)
    {
        var path = Path.Combine(dir, profileName.Replace(" ", "") + ".Profile.al");
        File.WriteAllText(path,
            $"profile \"{profileName}\"\n{{\n    Caption = '{profileName}';\n    Enabled = true;\n}}\n");
        return path;
    }

    private static string WritePermissionSet(string dir, int id, string setName)
    {
        var path = Path.Combine(dir, setName.Replace(" ", "") + ".PermissionSet.al");
        File.WriteAllText(path,
            $"permissionset {id} \"{setName}\"\n{{\n    Caption = '{setName}';\n    Assignable = true;\n}}\n");
        return path;
    }

    /// <summary>
    /// The AL-source parser defaults an UNDECLARED <c>Assignable</c> to false, matching what
    /// BC's own reader answers for the document BC emits from that same declaration (#2417,
    /// #3806) — and an explicit <c>Assignable = true</c> is still honoured, so a parser that
    /// simply answered false everywhere fails the second half.
    ///
    /// <para>TRAP: this parser's value is consumed by
    /// <c>ComposeSourcePermissionSet</c> only on the fallback path — when no BC document is
    /// registered, i.e. a COMPILE-CACHE HIT. So a parser disagreeing with the document route
    /// makes one permission set answer false on a cold run and true on a warm one, which is a
    /// cache-dependent wrong answer nothing downstream can detect.</para>
    /// </summary>
    [Fact]
    public void AlSourceParser_UndeclaredAssignableIsFalse_AndAnExplicitTrueIsHonored()
    {
        var dir = Path.Combine(_root, "assignable-default");
        Directory.CreateDirectory(dir);
        // The parser drops a declaration whose owning app.json it cannot find, so the manifest
        // is load-bearing. No "application" property — that is the Base Application floor,
        // which no-base-app-in-csharp-tests.md forbids in a C# fixture.
        File.WriteAllText(Path.Combine(dir, "app.json"),
            """
            {
              "id": "b3f1c0de-7931-4a11-9c7e-000000079942",
              "name": "BMCB Assignable App",
              "publisher": "AL Runner",
              "version": "1.0.0.0"
            }
            """);

        // Declares NO Assignable — the shape of Base Application 208/209 and System
        // Application 68, the three real sets that state none.
        var undeclaredPath = Path.Combine(dir, "BmcbUndeclared.PermissionSet.al");
        File.WriteAllText(undeclaredPath,
            "permissionset 79960 \"BMCB Undeclared Assignable\"\n"
            + "{\n    Caption = 'BMCB Undeclared Assignable';\n}\n");
        ParseOneSourceFile(undeclaredPath);

        // Declares Assignable = true explicitly.
        var declaredPath = WritePermissionSet(dir, 79961, "BMCB Declared Assignable");
        ParseOneSourceFile(declaredPath);

        // Asserted, not assumed: if the sweep had not picked these up, the claims below would
        // be about an empty dictionary rather than about the defaulting rule.
        Assert.Equal(79960, PermissionSetNamed("BMCB Undeclared Assignable").Id);
        Assert.Equal(79961, PermissionSetNamed("BMCB Declared Assignable").Id);

        Assert.False(PermissionSetNamed("BMCB Undeclared Assignable").Assignable);
        Assert.True(PermissionSetNamed("BMCB Declared Assignable").Assignable);
    }

    private static RecordPatches.ParsedAlProfile ProfileNamed(string profileId)
        => Assert.Single(RecordPatches.ParsedProfiles.Where(p => p.ProfileId == profileId));

    private static RecordPatches.ParsedAlPermissionSet PermissionSetNamed(string name)
        => Assert.Single(RecordPatches.ParsedPermissionSets.Where(p => p.Name == name));

    /// <summary>Read by reflection for the same reason <see cref="TableExtensionTypeCache"/> is:
    /// nothing reports it, and "it was cleared" cannot be inferred from a later lookup.</summary>
    private static IDictionary AppOwnerCache()
    {
        var field = typeof(RecordPatches).GetField("_appOwnerCache",
                        BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RecordPatches._appOwnerCache not found — this test tracks that field.");
        return (IDictionary)field.GetValue(null)!;
    }

    private static FieldInfo PopulatedCountField()
        => typeof(RecordPatches).GetField("_permMetaPopulatedForCount",
               BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               "RecordPatches._permMetaPopulatedForCount not found — this test tracks that field.");

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
