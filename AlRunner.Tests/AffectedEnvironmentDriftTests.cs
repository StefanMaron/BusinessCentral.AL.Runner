// AffectedEnvironmentDriftTests — #5028: the per-object diff of two environments' apps, the keys it
// selects on, and the stored form, without a server. The server-side proof is
// ServerAffectedSelectionEnvironmentDriftTests. docs/server-mode.md#affectedonly-across-environments.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AffectedEnvironmentDriftTests
{
    private static readonly Guid Base = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");
    private static readonly Guid Sys = Guid.Parse("63ca2fa4-4f03-4f2b-a480-172fef340d3f");

    private static EnvironmentSnapshot Env(params (Guid Id, string Hash, Dictionary<string, string>? Objects)[] apps)
        => new(apps.ToDictionary(a => a.Id, a => new EnvironmentApp(a.Hash, a.Id == Base ? "Base Application" : "System", a.Objects)));

    private static Dictionary<string, string> Objects(params (string Key, string Value)[] o)
        => o.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    [Fact]
    public void ObjectsFromSource_KeysEveryDeclaredObject_AndNothingAReferenceNames()
    {
        var objects = AffectedEnvironmentDrift.ObjectsFromSource(new[]
        {
            ("src/Two.al", "codeunit 80 \"Sales-Post\"\n{\n}\n\ntable 18 Customer\n{\n}\n"),
            ("src/Perm.al", "permissionset 50100 \"Perm\"\n{\n    Permissions = tabledata Customer = X,\n        table \"Sales Header\" = X;\n}\n"),
            ("src/Iface.al", "interface \"IThing\"\n{\n}\n"),
            ("src/Notes.al", "// no object here\n"),
        });
        Assert.Equal(new[]
        {
            "Codeunit|id:80", "Interface|name:IThing", "PermissionSet|id:50100", "Table|id:18", "file|src/Notes.al",
        }, objects.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // Both objects of one file carry that file's hash, and their own names.
        Assert.Equal(objects["Codeunit|id:80"].Split(':')[0], objects["Table|id:18"].Split(':')[0]);
        Assert.EndsWith(":Sales-Post", objects["Codeunit|id:80"]);
        Assert.EndsWith(":Customer", objects["Table|id:18"]);
    }

    [Fact]
    public void Diff_ChangesExactlyTheObjectsWhoseHashDiffersOrIsMissingOnOneSide()
    {
        var recorded = Env((Base, "B1", Objects(("Codeunit|id:80", "h1:Sales-Post"), ("Codeunit|id:90", "h2:Purch-Post"),
            ("Table|id:18", "h3:Customer"))), (Sys, "S1", Objects(("Table|id:2000000006", "s:Company"))));
        var current = Env((Base, "B2", Objects(("Codeunit|id:80", "h9:Sales-Post"), ("Codeunit|id:90", "h2:Purch-Post"),
            ("Page|id:21", "h4:Customer Card"))), (Sys, "S1", Objects(("Table|id:2000000006", "s:Company"))));

        var d = AffectedEnvironmentDrift.Diff(recorded, current);
        Assert.Empty(d.Approximate);
        Assert.Equal(new[]
        {
            new AffectedObjectId("Codeunit", 80, "Sales-Post"),
            new AffectedObjectId("Page", 21, "Customer Card"),
            new AffectedObjectId("Table", 18, "Customer"),
        }, d.Changed.OrderBy(o => o.Kind).ThenBy(o => o.Id));

        // Equal package bytes are never read further, whatever the tables say.
        Assert.Empty(AffectedEnvironmentDrift.Diff(recorded, recorded).Changed);
    }

    [Fact]
    public void Diff_ThatCannotBeComputed_IsApproximate_AndStillNamesWhatItCould()
    {
        var noRecord = AffectedEnvironmentDrift.Diff(null, Env((Base, "B2", Objects())));
        Assert.Contains("no per-object record", Assert.Single(noRecord.Approximate));
        Assert.Contains("closure could not be read", Assert.Single(AffectedEnvironmentDrift.Diff(Env(), null).Approximate));

        var unreadable = AffectedEnvironmentDrift.Diff(
            Env((Base, "B1", Objects(("Codeunit|id:80", "h1:Sales-Post")))),
            Env((Base, "B2", null)));
        Assert.Contains("Base Application changed and its objects cannot be read", Assert.Single(unreadable.Approximate));
        Assert.Equal(new[] { new AffectedObjectId("Codeunit", 80, "Sales-Post") }, unreadable.Changed);

        var fileWithoutObject = AffectedEnvironmentDrift.Diff(
            Env((Base, "B1", Objects(("file|src/x.al", "a:src/x.al")))),
            Env((Base, "B2", Objects(("file|src/x.al", "b:src/x.al")))));
        Assert.Contains("src/x.al changed and declares no object", Assert.Single(fileWithoutObject.Approximate));
    }

    [Fact]
    public void SelectionKeys_KeyBuiltObjectsAndTables_AndNameWhatNoRecordingHolds()
    {
        var keys = AffectedEnvironmentDrift.SelectionKeys(new[]
            {
                new AffectedObjectId("Codeunit", 80, "Sales-Post"),
                new AffectedObjectId("Table", 18, "Customer"),
                new AffectedObjectId("Interface", null, "IThing"),
                new AffectedObjectId("Enum", 36, "Document Type"),
            },
            new Dictionary<int, List<int>>(),
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<int, List<int>>());
        Assert.Equal(new[] { "dep|Codeunit|id:80" }, keys.CoverageKeys);
        Assert.Contains("tbl|Table|18", keys.EventKeys);
        Assert.Equal("Enum 36 Document Type changed, and no test recording holds the use of this kind of object (Enum)",
            Assert.Single(keys.Unattributed));

        var longLived = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("Codeunit", 80, "Sales-Post") },
            new Dictionary<int, List<int>>(),
            new HashSet<string>(StringComparer.Ordinal) { AffectedEventSelection.LongLivedObjectKey("dep|Codeunit|id:80") },
            new Dictionary<int, List<int>>());
        Assert.Contains("built outside any one test", Assert.Single(longLived.Unattributed));
    }

    // #5025: a dependency's pageextension selects through its base page, now or as recorded.
    [Fact]
    public void SelectionKeys_PageExtension_KeysItsBasePage_CurrentOrRecorded()
    {
        var current = new Dictionary<int, List<int>> { [9001] = new() { 21 } };
        var recorded = new HashSet<string>(StringComparer.Ordinal) { AffectedEventSelection.PageExtensionBaseKey(9002, 22) };
        var keys = AffectedEnvironmentDrift.SelectionKeys(new[]
            {
                new AffectedObjectId("PageExtension", 9001, "Edited"),
                new AffectedObjectId("PageExtension", 9002, "Removed"),
            },
            new Dictionary<int, List<int>>(), recorded, current);
        Assert.Empty(keys.Unattributed);
        Assert.Equal(new[] { "Page|id:21", "Page|id:22", "dep|Page|id:21", "dep|Page|id:22" },
            keys.CoverageKeys.OrderBy(k => k, StringComparer.Ordinal));

        var unknown = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("PageExtension", 9003, "New") },
            new Dictionary<int, List<int>>(), recorded, current);
        Assert.Contains("base page of pageextension 9003 could not be resolved", Assert.Single(unknown.Unattributed));
    }

    [Fact]
    public void DependencyKeyOf_MatchesTheKeyADiffSelectsOn()
    {
        Assert.Equal("dep|Codeunit|id:80", AffectedEnvironmentDrift.DependencyKeyPrefix
            + AffectedEnvironmentDrift.ObjectKey(AffectedEnvironmentDrift.CanonicalKind("CodeUnit"), 80, ""));
        Assert.Equal("XmlPort", AffectedEnvironmentDrift.CanonicalKind("xmlport"));
    }

    [Fact]
    public void Warning_NamesBothBuildsTheCountAndTheObjects()
    {
        var w = AffectedEnvironmentDrift.Warning(new EnvironmentDriftInfo("27.5.1.0", "28.4.2.0", 1,
            EnvironmentDriftInfo.Diffed, new[] { "Codeunit 80 Sales-Post" }, null));
        Assert.Contains("BC 27.5.1.0, now BC 28.4.2.0", w);
        Assert.Contains("1 object(s) differ: Codeunit 80 Sales-Post", w);
        Assert.Contains("narrowed by that diff", w);
        var a = AffectedEnvironmentDrift.Warning(new EnvironmentDriftInfo("A", "B", 0,
            EnvironmentDriftInfo.Approximate, Array.Empty<string>(), "no record"));
        Assert.Contains("APPROXIMATE: no record", a);
    }

    // Review of #5073: the platform is not diffed, so a change of build is never an exact diff.
    [Fact]
    public void PlatformReason_NamesBothBuilds_OnlyWhenTheBuildDiffers()
    {
        Assert.Null(AffectedEnvironmentDrift.PlatformReason("28.1.1.0|/bc|/p", "28.1.1.0|/bc|/q|isolation=Test"));
        Assert.Equal("the BC platform changed (28.1.1.0 to 28.2.2.0) and is not diffed",
            AffectedEnvironmentDrift.PlatformReason("28.1.1.0|/bc|/p", "28.2.2.0|/bc|/p"));
    }

    [Fact]
    public void Resolve_PlatformOnlyChange_IsApproximate_NotAnExactEmptyDiff()
    {
        var env = Env((Base, "B1", Objects(("Codeunit|id:80", "h1:Sales-Post"))));
        var envs = new BundleEnvironments();
        envs.Set("T.A", env);
        var r = AffectedEnvironmentDrift.Resolve("28.1.1.0|/bc", "28.2.2.0|/bc", new[] { AffectedEnvironmentDrift.IdOf(env) },
            envs, env, new Dictionary<int, List<int>>(), new HashSet<string>(StringComparer.Ordinal), new Dictionary<int, List<int>>());
        Assert.Equal(EnvironmentDriftInfo.Approximate, r.Info.Mode);
        Assert.Equal(0, r.Info.ChangedObjects);
        Assert.Contains("the BC platform changed (28.1.1.0 to 28.2.2.0) and is not diffed", r.Info.Reason);
        Assert.Empty(r.ExactRecords);
    }

    [Fact]
    public void Resolve_DiffsEachRecordAgainstTheEnvironmentItWasTakenIn()
    {
        var e1 = Env((Base, "B1", Objects(("Codeunit|id:80", "h1:Sales-Post"), ("Codeunit|id:90", "h2:Purch-Post"))));
        var e2 = Env((Base, "B2", Objects(("Codeunit|id:80", "h9:Sales-Post"), ("Codeunit|id:90", "h2:Purch-Post"))));
        var e3 = Env((Base, "B3", Objects(("Codeunit|id:80", "h9:Sales-Post"), ("Codeunit|id:90", "h8:Purch-Post"))));
        var envs = new BundleEnvironments();
        envs.Set("T.Old", e1);
        envs.Set("T.New", e2);
        var (id1, id2) = (AffectedEnvironmentDrift.IdOf(e1), AffectedEnvironmentDrift.IdOf(e2));
        var r = AffectedEnvironmentDrift.Resolve("28.1.1.0|/bc|/p", "28.1.1.0|/bc|/q", new[] { id1, id2, id1, "" },
            envs, e3, new Dictionary<int, List<int>>(), new HashSet<string>(StringComparer.Ordinal), new Dictionary<int, List<int>>());

        Assert.Equal(new[] { "dep|Codeunit|id:80", "dep|Codeunit|id:90" }, r.KeysByRecord[id1].CoverageKeys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "dep|Codeunit|id:90" }, r.KeysByRecord[id2].CoverageKeys);
        Assert.Empty(r.KeysByRecord[""].CoverageKeys);
        Assert.Equal(new[] { id1, id2 }.OrderBy(i => i, StringComparer.Ordinal), r.ExactRecords.OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(EnvironmentDriftInfo.Approximate, r.Info.Mode);   // the record with no environment
        Assert.Equal(2, r.Info.ChangedObjects);
    }

    [Fact]
    public void IdOf_DependsOnlyOnThePackagesContent()
    {
        var a = Env((Base, "B1", Objects(("Codeunit|id:80", "h1:x"))), (Sys, "S1", null));
        var sameBytes = Env((Sys, "S1", Objects()), (Base, "B1", null));
        var other = Env((Base, "B2", Objects(("Codeunit|id:80", "h1:x"))), (Sys, "S1", null));
        Assert.Equal(AffectedEnvironmentDrift.IdOf(a), AffectedEnvironmentDrift.IdOf(sameBytes));
        Assert.NotEqual(AffectedEnvironmentDrift.IdOf(a), AffectedEnvironmentDrift.IdOf(other));
    }

    [Fact]
    public void KeyParts_SplitTheBuildAndTheIsolation()
    {
        const string key = "28.4.51311.0|/bc|/pkgs|pkg:x=y|isolation=Codeunit";
        Assert.Equal("28.4.51311.0", AffectedEnvironmentDrift.BuildOf(key));
        Assert.Equal("Codeunit", AffectedEnvironmentDrift.IsolationOf(key));
        Assert.Equal("28.4.51311.0|/bc|/pkgs|pkg:x=y", AffectedEnvironmentDrift.WithoutIsolation(key));
    }

    private static AffectedBaseline Stored(BundleEnvironments? environments) => new(
        new Dictionary<string, AffectedModuleSnapshot>(),
        new Dictionary<string, AffectedBundleBaseline>
        {
            ["/b"] = new("env", new() { ["T.A"] = new() { "dep|Codeunit|id:80" }, ["T.B"] = new() { "dep|Codeunit|id:90" } },
                new(), new(), new() { ["T.A"] = new(), ["T.B"] = new() },
                new List<SubscriberBinding>(), null, environments),
            ["/c"] = new("env", new(), new(), new(), new(), null, null, environments),
        });

    [Fact]
    public void Store_RoundTripsEachRecordsEnvironment_OncePerPackage_AndReadsAnOlderSchemaWithoutOne()
    {
        var env1 = Env((Base, "B1", Objects(("Codeunit|id:80", "h1:Sales-Post"))), (Sys, "S1", null));
        var env2 = Env((Base, "B2", Objects(("Codeunit|id:80", "h2:Sales-Post"))), (Sys, "S1", null));
        var envs = new BundleEnvironments();
        envs.Set("T.A", env2);
        envs.Set("T.B", env1);
        var path = Path.Combine(TestScratch.Dir("al-runner-affected-env-store"), "s.json");
        AffectedBaselineStore.Write(path, Stored(envs));
        var text = File.ReadAllText(path);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "\"h1:Sales-Post\"").Count);

        var loaded = AffectedBaselineStore.Load(path).Baseline!;
        foreach (var bundle in new[] { "/b", "/c" })
        {
            var e = loaded.Bundles[bundle].Environments!;
            Assert.Equal(2, e.Snapshots.Count);
            var b = e.Snapshots[e.ByTest["T.B"]];
            Assert.Equal("h1:Sales-Post", b.Apps[Base].Objects!["Codeunit|id:80"]);
            Assert.Null(b.Apps[Sys].Objects);
            Assert.Equal("S1", b.Apps[Sys].ContentHash);
            Assert.Equal("h2:Sales-Post", e.Snapshots[e.ByTest["T.A"]].Apps[Base].Objects!["Codeunit|id:80"]);
        }
        var stored = loaded.Bundles["/b"].Environments!;
        Assert.Empty(AffectedEnvironmentDrift.Diff(stored.Snapshots[stored.ByTest["T.B"]], env1).Changed);

        // A snapshot no record names any more is dropped.
        envs.Set("T.B", env2);
        envs.Prune();
        Assert.Single(envs.Snapshots);

        // A version-4 file (before #5028) loads, with no environment to diff against.
        File.WriteAllText(path, text.Replace($"\"Schema\":{AffectedBaselineStore.SchemaVersion},", "\"Schema\":4,", StringComparison.Ordinal));
        var old = AffectedBaselineStore.Load(path);
        Assert.Null(old.Unusable);
        Assert.Null(old.Baseline!.Bundles["/b"].Environments);

        File.WriteAllText(path, text.Replace($"\"Schema\":{AffectedBaselineStore.SchemaVersion},", "\"Schema\":3,", StringComparison.Ordinal));
        Assert.Contains("schema version 3", AffectedBaselineStore.Load(path).Unusable);
    }

    [Fact]
    public void ChangedSince_IgnoresTheDependencySet_OnlyWhenTheEnvironmentIsDiffed()
    {
        var recorded = new Dictionary<string, AffectedModuleSnapshot>
        {
            ["V2_b"] = new("m", "r1", new() { ["/b/T.al"] = "T1" }, new() { ["/b/T.al"] = new("Codeunit", 1, "T") }),
        };
        var now = new AffectedModuleSnapshot("m", "r2", new() { ["/b/T.al"] = "T1" }, new() { ["/b/T.al"] = new("Codeunit", 1, "T") });
        Assert.Contains("dependency set", AffectedBaselineStore.ChangedSince(recorded, new[] { "V2_b" }, _ => now).ForceFullReason);
        var diffed = AffectedBaselineStore.ChangedSince(recorded, new[] { "V2_b" }, _ => now, environmentDiffed: true);
        Assert.Null(diffed.ForceFullReason);
        Assert.Empty(diffed.Changed);
    }
}
