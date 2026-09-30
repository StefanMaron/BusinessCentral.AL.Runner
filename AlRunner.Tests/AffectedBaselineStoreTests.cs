// AffectedBaselineStoreTests — #4979: the persisted affectedOnly baseline's file and its file-hash
// comparison, without a server. docs/server-mode.md#affectedonly-across-server-processes.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AffectedBaselineStoreTests
{
    private static readonly AffectedObjectId Helper = new("Codeunit", 60601, "Helper");
    private static readonly AffectedObjectId Tests = new("Codeunit", 60610, "Tests");
    private static readonly AffectedObjectId Added = new("Codeunit", 60603, "Added");

    private static AffectedModuleSnapshot Snapshot(
        Dictionary<string, string> files, Dictionary<string, AffectedObjectId> objects,
        string manifest = "m", string refs = "r")
        => new(manifest, refs, files, objects);

    private static AffectedModuleSnapshot Recorded() => Snapshot(
        new() { ["/b/Helper.al"] = "H1", ["/b/Tests.al"] = "T1" },
        new() { ["/b/Helper.al"] = Helper, ["/b/Tests.al"] = Tests });

    private static AlRunner.Infrastructure.AffectedBaselineStore.ChangeResult Diff(AffectedModuleSnapshot? now)
        => AffectedBaselineStore.ChangedSince(
            new Dictionary<string, AffectedModuleSnapshot> { ["V2_b"] = Recorded() }, new[] { "V2_b" }, _ => now);

    [Fact]
    public void UnchangedHashes_ChangeNothing()
    {
        var r = Diff(Recorded());
        Assert.Null(r.ForceFullReason);
        Assert.Empty(r.Changed);
    }

    [Fact]
    public void ChangedAddedAndRemovedFiles_ChangeTheirObjects()
    {
        var modified = Diff(Snapshot(
            new() { ["/b/Helper.al"] = "H2", ["/b/Tests.al"] = "T1" },
            new() { ["/b/Helper.al"] = Helper, ["/b/Tests.al"] = Tests }));
        Assert.Null(modified.ForceFullReason);
        Assert.Equal(new[] { Helper }, modified.Changed);

        var removed = Diff(Snapshot(new() { ["/b/Tests.al"] = "T1" }, new() { ["/b/Tests.al"] = Tests }));
        Assert.Null(removed.ForceFullReason);
        Assert.Equal(new[] { Helper }, removed.Changed);

        var added = Diff(Snapshot(
            new() { ["/b/Helper.al"] = "H1", ["/b/Tests.al"] = "T1", ["/b/Added.al"] = "A1" },
            new() { ["/b/Helper.al"] = Helper, ["/b/Tests.al"] = Tests, ["/b/Added.al"] = Added }));
        Assert.Null(added.ForceFullReason);
        Assert.Equal(new[] { Added }, added.Changed);
    }

    [Fact]
    public void WhatTheSnapshotCannotVouchFor_ForcesAFullRun()
    {
        Assert.Contains("no change-model baseline", Diff(null).ForceFullReason);
        Assert.Contains("app.json", Diff(Snapshot(Recorded().FileHashByPath, Recorded().ObjectByPath, manifest: "m2")).ForceFullReason);
        Assert.Contains("dependency set", Diff(Snapshot(Recorded().FileHashByPath, Recorded().ObjectByPath, refs: "r2")).ForceFullReason);
        // A changed file no single object is tracked for.
        Assert.Contains("exactly one object", Diff(Snapshot(
            new() { ["/b/Helper.al"] = "H1", ["/b/Tests.al"] = "T1", ["/b/Two.al"] = "X" },
            Recorded().ObjectByPath)).ForceFullReason);
        var missingModule = AffectedBaselineStore.ChangedSince(
            new Dictionary<string, AffectedModuleSnapshot>(), new[] { "V2_b" }, _ => Recorded());
        Assert.Contains("no snapshot of module V2_b", missingModule.ForceFullReason);
    }

    private static AffectedBaseline Sample() => new(
        new Dictionary<string, AffectedModuleSnapshot> { ["V2_b"] = Recorded() },
        new Dictionary<string, AffectedBundleBaseline>
        {
            ["/b"] = new(
                "env",
                new() { ["Codeunit60610.A"] = new() { "Codeunit|id:60601", "Codeunit|id:60610" }, ["Codeunit60610.B"] = new() { "Codeunit|id:60610" } },
                new() { "Codeunit60610.C" },
                new() { "Codeunit60610.B" },
                new() { ["Codeunit60610.A"] = new() { "ev|Codeunit|60620|OnDoWork" }, ["Codeunit60610.B"] = new() },
                new List<SubscriberBinding> { new("Codeunit", 60621, "Handle", "ev|Codeunit|60620|OnDoWork", "id1") },
                new EventObservability(new() { "ev|Codeunit|60620|OnDoWork" }, new() { ["Codeunit|60620"] = true })),
        });

    [Fact]
    public void WriteThenLoad_RoundTripsEveryField()
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-affected-store"), "s.json");
        AffectedBaselineStore.Write(path, Sample());
        var loaded = AffectedBaselineStore.Load(path);
        Assert.Null(loaded.Unusable);
        var b = loaded.Baseline!;
        var bundle = b.Bundles["/b"];
        Assert.Equal("env", bundle.EnvironmentKey);
        Assert.Equal(new[] { "Codeunit|id:60601", "Codeunit|id:60610" }, bundle.Coverage["Codeunit60610.A"].OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "Codeunit|id:60610" }, bundle.Coverage["Codeunit60610.B"]);
        Assert.Equal(new[] { "Codeunit60610.C" }, bundle.Unknown);
        Assert.Equal(new[] { "Codeunit60610.B" }, bundle.Failing);
        Assert.Equal(new[] { "ev|Codeunit|60620|OnDoWork" }, bundle.Events["Codeunit60610.A"]);
        Assert.Empty(bundle.Events["Codeunit60610.B"]);
        Assert.Equal(Sample().Bundles["/b"].Bindings, bundle.Bindings);
        Assert.True(bundle.Observability!.PublisherObjects["Codeunit|60620"]);
        Assert.Contains("ev|Codeunit|60620|OnDoWork", bundle.Observability.ObservableEventKeys);
        var m = b.Modules["V2_b"];
        Assert.Equal(Recorded().FileHashByPath, m.FileHashByPath);
        Assert.Equal(Recorded().ObjectByPath, m.ObjectByPath);
        Assert.Null(AffectedBaselineStore.ChangedSince(b.Modules, new[] { "V2_b" }, _ => Recorded()).ForceFullReason);
    }

    [Fact]
    public void AbsentTruncatedAndOtherSchemaFiles_AreDistinguished()
    {
        var dir = TestScratch.Dir("al-runner-affected-store-bad");
        var path = Path.Combine(dir, "s.json");
        var absent = AffectedBaselineStore.Load(path);
        Assert.Null(absent.Baseline);
        Assert.Null(absent.Unusable);

        AffectedBaselineStore.Write(path, Sample());
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 5)]);
        var truncated = AffectedBaselineStore.Load(path);
        Assert.Null(truncated.Baseline);
        Assert.Contains("could not be read", truncated.Unusable);

        AffectedBaselineStore.Write(path, Sample());
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Schema\":1,", "\"Schema\":2,", StringComparison.Ordinal));
        var otherSchema = AffectedBaselineStore.Load(path);
        Assert.Null(otherSchema.Baseline);
        Assert.Contains("schema version 2", otherSchema.Unusable);

        File.WriteAllText(path, "{\"Schema\":1,\"Keys\":[],\"Modules\":{},\"Bundles\":{\"/b\":{\"Env\":\"e\",\"Coverage\":{\"T\":[3]},"
            + "\"Unknown\":[],\"Failing\":[],\"Events\":{}}}}");
        Assert.Contains("out of range", AffectedBaselineStore.Load(path).Unusable);
    }

    [Fact]
    public void PathFor_IgnoresOrderAndDuplicates_AndSeparatesBundleSets()
    {
        var a = AffectedBaselineStore.PathFor("/s", new[] { "/x", "/y" });
        Assert.Equal(a, AffectedBaselineStore.PathFor("/s", new[] { "/y", "/x", "/x" }));
        Assert.NotEqual(a, AffectedBaselineStore.PathFor("/s", new[] { "/x" }));
    }
}
