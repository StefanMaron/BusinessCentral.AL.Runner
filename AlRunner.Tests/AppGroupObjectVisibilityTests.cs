// #2279: AllObj, AllObjWithCaption and Table Metadata list only the executing app group's
// objects and those of its declared dependency closure. The CLI half is proven by
// tests/runner-extras/app-group-visibility-{a,b,c}; this file pins the decision functions and
// the --server multi-bundle request, which runner-extras does not reach.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AppGroupObjectVisibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
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
    [Fact]
    public void AppGroupOwningFile_TakesTheLongestRegisteredDir_NotTheNearestAppJson()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agv-owner"));
        var outer = Path.Combine(root, "outer");
        var owners = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            [outer] = AppA,
            [Path.Combine(root, "outer-sibling")] = AppB,
            [Path.Combine(root, "shared")] = Guid.Empty,
        };

        // A nested inner/app.json is still compiled by the outer group, and only the dir map says so.
        Assert.Equal(AppA, RecordPatches.AppGroupOwningFile(Path.Combine(outer, "inner", "Inner.al"), owners));
        // A prefix match on the NAME is not containment.
        Assert.Equal(AppB, RecordPatches.AppGroupOwningFile(Path.Combine(root, "outer-sibling", "X.al"), owners));
        Assert.Equal(Guid.Empty, RecordPatches.AppGroupOwningFile(Path.Combine(root, "shared", "S.al"), owners));
        Assert.Equal(Guid.Empty, RecordPatches.AppGroupOwningFile(Path.Combine(root, "elsewhere", "E.al"), owners));
    }

    [Fact]
    public void RecordObjectOwner_AnIdClaimedByTwoAppsHasNoOwner_AndStaysThatWay()
    {
        var owners = new Dictionary<(string Kind, int Id), Guid>();
        var ambiguous = new HashSet<(string Kind, int Id)>();

        RecordPatches.RecordObjectOwner(owners, ambiguous, ("table", 62680), AppA);
        RecordPatches.RecordObjectOwner(owners, ambiguous, ("table", 62680), AppA);
        Assert.Equal(AppA, owners[("table", 62680)]);

        RecordPatches.RecordObjectOwner(owners, ambiguous, ("table", 62680), AppB);
        RecordPatches.RecordObjectOwner(owners, ambiguous, ("table", 62680), AppA);
        Assert.False(owners.ContainsKey(("table", 62680)));
        Assert.Contains(("table", 62680), ambiguous);
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Table", 62680, new HashSet<Guid> { AppA }, owners));
    }

    private static string WriteApp(string dir, Guid appId, string name, int from, int to)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{appId}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0", "idRanges": [ { "from": {{from}}, "to": {{to}} } ], "runtime": "14.0" }
        """);
        return dir;
    }

    /// <summary>
    /// Two shapes where "who owns this object" is not "the nearest app.json above the file":
    /// a suite compiling a sub-folder that carries its own app.json, and two unrelated groups
    /// declaring the same table id. Both groups must still list the objects they compile.
    /// </summary>
    private static string[] WriteOwnershipEdgeFixtures(string root)
    {
        var outer = WriteApp(Path.Combine(root, "outer"), AppA, "Nest Outer", 62660, 62679);
        WriteApp(Path.Combine(outer, "inner"), AppB, "Nest Inner", 62665, 62669);
        File.WriteAllText(Path.Combine(outer, "inner", "Inner.al"), """
        table 62665 "Nest Inner Table" { fields { field(1; "Code"; Code[20]) { } } keys { key(PK; "Code") { Clustered = true; } } }
        """);
        File.WriteAllText(Path.Combine(outer, "Outer.al"), """
        codeunit 62661 "Nest Outer Tests"
        {
            Subtype = Test;
            [Test]
            procedure InnerTableCompiledHereIsListed()
            var
                AllObj: Record AllObj;
                TableMetadata: Record "Table Metadata";
                Inner: Record "Nest Inner Table";
            begin
                Inner.Code := 'X';
                Inner.Insert();
                if not Inner.Get('X') then Error('record access to 62665 broken');
                if not AllObj.Get(AllObj."Object Type"::Table, 62665) then Error('MISSING: AllObj does not list compiled table 62665');
                if not TableMetadata.Get(62665) then Error('MISSING: Table Metadata does not list compiled table 62665');
            end;
        }
        """);

        var dirs = new List<string> { outer };
        foreach (var (letter, appId, cu) in new[] { ("X", AppC, 62681), ("Y", AppD, 62682) })
        {
            var dir = WriteApp(Path.Combine(root, "dup" + letter), appId, "Dup " + letter, 62680, 62689);
            File.WriteAllText(Path.Combine(dir, "Dup.al"), $$"""
            table 62680 "Dup {{letter}} Table" { fields { field(1; "Code"; Code[20]) { } } keys { key(PK; "Code") { Clustered = true; } } }
            codeunit {{cu}} "Dup {{letter}} Tests"
            {
                Subtype = Test;
                [Test]
                procedure OwnTableWithASharedIdIsListed()
                var
                    AllObj: Record AllObj;
                    TableMetadata: Record "Table Metadata";
                begin
                    if not AllObj.Get(AllObj."Object Type"::Table, 62680) then Error('MISSING: AllObj does not list own table 62680 in {{letter}}');
                    if not TableMetadata.Get(62680) then Error('MISSING: Table Metadata does not list own table 62680 in {{letter}}');
                end;
            }
            """);
            dirs.Add(dir);
        }
        return dirs.ToArray();
    }

    [SkippableFact]
    public void Cli_NestedAppJsonAndSharedTableId_EachGroupStillListsWhatItCompiles()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-visibility-edges-cli");
        Directory.CreateDirectory(root);
        WriteOwnershipEdgeFixtures(root);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" --no-cache \"{root}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        Assert.Contains("3P/0F/0E across 3 tests", output);
        Assert.DoesNotContain("MISSING:", output);
        Assert.Equal(0, p.ExitCode);
    }

    [SkippableFact]
    public async Task Server_NestedAppJsonAndSharedTableId_EachGroupStillListsWhatItCompiles()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-visibility-edges-server");
        Directory.CreateDirectory(root);
        var dirs = WriteOwnershipEdgeFixtures(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        var lines = await server.SendRequestStreamingAsync(RunTests(dirs));
        var (events, _) = ProtocolV2Streaming.Split(lines);
        Assert.Equal(3, events.Count);
        foreach (var e in events)
            Assert.True(e.GetProperty("status").GetString() == "pass", string.Join(" | ", lines));
    }
}
