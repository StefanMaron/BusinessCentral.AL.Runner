// #2279: AllObj, AllObjWithCaption and Table Metadata list only the executing app group's
// objects and those of its declared dependency closure. The CLI half is proven by
// tests/runner-extras/app-group-visibility-{a,b,c}; this file pins the decision functions and
// the --server multi-bundle request, which runner-extras does not reach.
using System.Text.Json;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AppGroupObjectVisibilityTests
{
    private static readonly Guid AppA = new("3b0e6f52-0000-4c38-9e25-00000000000a");
    private static readonly Guid AppB = new("3b0e6f52-0000-4c38-9e25-00000000000b");
    private static readonly Guid AppC = new("3b0e6f52-0000-4c38-9e25-00000000000c");
    private static readonly Guid AppD = new("3b0e6f52-0000-4c38-9e25-00000000000d");

    [Fact]
    public void VisibleAppClosure_FollowsDeclaredDependenciesTransitively_AndNothingElse()
    {
        var deps = new Dictionary<Guid, Guid[]>
        {
            [AppC] = new[] { AppA },
            [AppA] = new[] { AppD },
            [AppB] = Array.Empty<Guid>(),
        };

        Assert.Equal(new[] { AppA, AppC, AppD }.OrderBy(g => g), RecordPatches.VisibleAppClosure(AppC, deps).OrderBy(g => g));
        Assert.Equal(new[] { AppB }, RecordPatches.VisibleAppClosure(AppB, deps));
    }

    [Fact]
    public void VisibleAppClosure_TerminatesOnADependencyCycle()
    {
        var deps = new Dictionary<Guid, Guid[]> { [AppA] = new[] { AppB }, [AppB] = new[] { AppA } };
        Assert.Equal(2, RecordPatches.VisibleAppClosure(AppA, deps).Count);
    }

    [Fact]
    public void IsHiddenFromAppGroup_HidesOnlyAKnownOwnerOutsideTheVisibleSet()
    {
        var owners = new Dictionary<(string Kind, int Id), Guid>
        {
            [("table", 62600)] = AppA,
            [("table", 62610)] = AppB,
        };
        var visible = new HashSet<Guid> { AppA };

        Assert.True(RecordPatches.IsHiddenFromAppGroup("Table", 62610, visible, owners));
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Table", 62600, visible, owners));
        // No recorded owner (a precompiled dependency or platform object): never hidden.
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Table", 18, visible, owners));
        // Same id, different kind: the owner map is keyed by kind too.
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Codeunit", 62610, visible, owners));
        // No executing app group: nothing is filtered.
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Table", 62610, null, owners));
    }

    [Fact]
    public void CheckInventoryScope_RefusesAStoreReadByADifferentAppGroup()
    {
        RecordPatches.CheckInventoryScope(AppA, AppA, "AllObj (virtual table 2000000038)");
        RecordPatches.CheckInventoryScope(null, null, "AllObj (virtual table 2000000038)");

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.CheckInventoryScope(AppA, AppB, "AllObj (virtual table 2000000038)"));
        Assert.Contains(AppA.ToString(), ex.Message);
        Assert.Contains(AppB.ToString(), ex.Message);
        Assert.Contains("app-group-visibility", ex.Message);

        Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.CheckInventoryScope(null, AppB, "Table Metadata (virtual table 2000000136)"));
    }

    private static string WriteGroup(string root, string letter, int baseId, Guid appId, Guid? dependsOn, int[] foreignIds, int[] visibleIds)
    {
        var dir = Path.Combine(root, "group-" + letter);
        Directory.CreateDirectory(dir);
        var deps = dependsOn is { } d
            ? $$"""[ { "id": "{{d}}", "name": "Visibility Probe A", "publisher": "AL Runner", "version": "1.0.0.0" } ]"""
            : "[]";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Visibility Probe {{letter}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": {{deps}},
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        var checks = string.Concat(
            visibleIds.Select(id => $$"""
                    if not AllObj.Get(AllObj."Object Type"::Table, {{id}}) then Error('AllObj misses visible table {{id}}');
                    if not TableMetadata.Get({{id}}) then Error('Table Metadata misses visible table {{id}}');

            """).Concat(foreignIds.Select(id => $$"""
                    if AllObj.Get(AllObj."Object Type"::Table, {{id}}) then Error('LEAK: AllObj lists foreign table {{id}}');
                    if TableMetadata.Get({{id}}) then Error('LEAK: Table Metadata lists foreign table {{id}}');

            """)));
        File.WriteAllText(Path.Combine(dir, "Probe.al"), $$"""
        table {{baseId}} "Visibility Probe {{letter}} Table"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit {{baseId + 1}} "Visibility Probe {{letter}} Tests"
        {
            Subtype = Test;

            [Test]
            procedure InventoryIsScopedToThisAppGroup()
            var
                AllObj: Record AllObj;
                TableMetadata: Record "Table Metadata";
            begin
        {{checks}}    end;
        }
        """);
        return dir;
    }

    private static string RunTests(params string[] dirs)
        => JsonSerializer.Serialize(new { command = "runTests", sourcePaths = dirs, packagePaths = Array.Empty<string>() });

    [SkippableFact]
    public async Task Server_MultiBundleRequest_AndASecondRequest_EachAppGroupSeesOnlyItsClosure()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-app-group-visibility-server");
        Directory.CreateDirectory(root);
        var a = WriteGroup(root, "A", 62630, AppA, null, new[] { 62640, 62650 }, new[] { 62630 });
        var b = WriteGroup(root, "B", 62640, AppB, null, new[] { 62630, 62650 }, new[] { 62640 });
        var c = WriteGroup(root, "C", 62650, AppC, AppA, new[] { 62640 }, new[] { 62650, 62630 });

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // One request, three bundles: before #2279 B saw A's table and C saw B's, following run order.
        var lines1 = await server.SendRequestStreamingAsync(RunTests(a, b, c));
        var (events1, _) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(3, events1.Count);
        foreach (var e in events1)
            Assert.True(e.GetProperty("status").GetString() == "pass", string.Join(" | ", lines1));

        // A second request on the warm process, B alone after A and C were loaded.
        var lines2 = await server.SendRequestStreamingAsync(RunTests(b));
        var (events2, _) = ProtocolV2Streaming.Split(lines2);
        Assert.Single(events2);
        Assert.True(events2[0].GetProperty("status").GetString() == "pass", string.Join(" | ", lines2));
    }
}
