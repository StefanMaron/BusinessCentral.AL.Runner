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
    private static readonly Guid AppE = new("3b0e6f52-0000-4c38-9e25-00000000000e");

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
    /// <summary>
    /// #4455: a dir whose app group was never registered attributes its objects to NOBODY, and
    /// <see cref="RecordPatches.IsHiddenFromAppGroup"/> then never hides them — which is how a
    /// dependency app's objects leaked into every sibling group's inventory. The CLI half is
    /// proven in tests/runner-extras/app-group-visibility-{a,b,c} run in reverse bundle order;
    /// this pins the decision the leak rests on, in both directions.
    /// </summary>
    [Fact]
    public void AppGroupOwningFile_DirRegisteredWithoutItsAppGroup_AttributesToNobody()
    {
        var root = Path.GetFullPath(TestScratch.Dir("al-runner-app-group-visibility-order"));
        var dep = Path.Combine(root, "dep-app");
        var owner = Path.Combine(root, "owned-app");

        // The map a run holds when a dependency's source dir was handed to the parser but its
        // app group was never declared: the owned dir resolves, the dependency dir does not.
        var owners = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { [owner] = AppA };

        Assert.Equal(AppA, RecordPatches.AppGroupOwningFile(Path.Combine(owner, "Owned.al"), owners));
        Assert.Equal(Guid.Empty, RecordPatches.AppGroupOwningFile(Path.Combine(dep, "Dep.al"), owners));

        // An object from the unregistered dir has no owner, so nothing hides it from a group
        // that does not declare it — the leak. The registered one is hidden from that group.
        var objectOwners = new Dictionary<(string Kind, int Id), Guid> { [("table", 62600)] = AppA };
        var visibleToB = new HashSet<Guid> { AppB };

        Assert.True(RecordPatches.IsHiddenFromAppGroup("Table", 62600, visibleToB, objectOwners));
        Assert.False(RecordPatches.IsHiddenFromAppGroup("Table", 62601, visibleToB, objectOwners));
    }

    [Fact]
    public void AppGroupOwningFile_TakesTheLongestRegisteredDir_NotTheNearestAppJson()
    {
        var root = Path.GetFullPath(TestScratch.Dir("al-runner-app-group-visibility-owner"));
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

    [Fact]
    public void AppGroupScopeFor_OnlyAnExecutingGroupAmongSeveralDeclarersGetsItsOwnScope()
    {
        var declarers = new Dictionary<(string Kind, int Id), HashSet<Guid>>
        {
            [("xmlport", 62683)] = new() { AppC, AppD },
            [("xmlport", 62690)] = new() { AppC },
        };

        var none = new Dictionary<Guid, Guid[]>();

        // #4751: each declarer of a shared id is its own scope.
        Assert.Equal(AppC, RecordPatches.AppGroupScopeFor("XmlPort", 62683, declarers, AppC, none));
        Assert.Equal(AppD, RecordPatches.AppGroupScopeFor("xmlport", 62683, declarers, AppD, none));
        // A group that neither declares the shared id nor depends on a declarer reads the process-wide object.
        Assert.Null(RecordPatches.AppGroupScopeFor("xmlport", 62683, declarers, AppA, none));
        // One declarer, an unknown id, or no executing group: nothing to separate.
        Assert.Null(RecordPatches.AppGroupScopeFor("xmlport", 62690, declarers, AppC, none));
        Assert.Null(RecordPatches.AppGroupScopeFor("xmlport", 62691, declarers, AppC, none));
        Assert.Null(RecordPatches.AppGroupScopeFor("xmlport", 62683, declarers, null, none));
        // Keyed by kind as well as id.
        Assert.Null(RecordPatches.AppGroupScopeFor("report", 62683, declarers, AppC, none));
    }

    [Fact]
    public void AppGroupScopeFor_AGroupDependingOnOneDeclarerSeesThatDeclarer_AndOnTwoRefuses()
    {
        var declarers = new Dictionary<(string Kind, int Id), HashSet<Guid>> { [("table", 62680)] = new() { AppC, AppD } };
        var deps = new Dictionary<Guid, Guid[]>
        {
            [AppA] = new[] { AppB },
            [AppB] = new[] { AppC },
            [AppD] = new[] { AppC },
            [AppE] = new[] { AppC, AppD },
        };

        // #4844: the declarer found through the closure, transitively, is the one it compiled against.
        Assert.Equal(AppC, RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppA, deps));
        Assert.Equal(AppC, RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppB, deps));
        // A declarer depending on the other declarer still sees its own object.
        Assert.Equal(AppD, RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppD, deps));
        // Two declarers in one closure: loud, naming the id and both declarers.
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppE, deps));
        Assert.Contains("62680", ex.Message);
        Assert.Contains(AppC.ToString(), ex.Message);
        Assert.Contains(AppD.ToString(), ex.Message);
    }

    [Fact]
    public void SubscribesToAnotherAppGroupsObject_OnlyASubscriberDeclaringTheSharedIdItselfIsExcluded()
    {
        var declarers = new Dictionary<(string Kind, int Id), HashSet<Guid>>
        {
            [("table", 62680)] = new() { AppC, AppD },
        };

        // AppA depends on the declarer AppC; AppB depends on nothing; AppE on both declarers.
        var deps = new Dictionary<Guid, Guid[]> { [AppA] = new[] { AppC }, [AppE] = new[] { AppC, AppD } };

        // #4834: a declarer of the shared id subscribes to its own object, never the other one's.
        Assert.True(RecordPatches.SubscribesToAnotherAppGroupsObject("Table", 62680, AppC, AppD, declarers, deps));
        Assert.True(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppD, AppC, declarers, deps));
        Assert.False(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppC, AppC, declarers, deps));
        // #4844: a subscriber whose group depends on one declarer belongs to that declarer's object only.
        Assert.False(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppC, AppA, declarers, deps));
        Assert.True(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppD, AppA, declarers, deps));
        // A group that sees no declarer cannot name the object; nothing to decide, so it is not excluded.
        Assert.False(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppD, AppB, declarers, deps));
        // #4853: a subscriber depending on both declarers can be installed beside neither, so both exclude it.
        Assert.True(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppC, AppE, declarers, deps));
        Assert.True(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62680, AppD, AppE, declarers, deps));
        // An id only one group declares, or the same id of another kind: nothing to separate.
        Assert.False(RecordPatches.SubscribesToAnotherAppGroupsObject("table", 62681, AppC, AppD, declarers, deps));
        Assert.False(RecordPatches.SubscribesToAnotherAppGroupsObject("codeunit", 62680, AppC, AppD, declarers, deps));
    }

    [Fact]
    public void ListingObjectInventory_AWalkThatThrows_LeavesTheRefusalInForceOnThisThread()
    {
        var declarers = new Dictionary<(string Kind, int Id), HashSet<Guid>> { [("table", 62680)] = new() { AppC, AppD } };
        var deps = new Dictionary<Guid, Guid[]> { [AppE] = new[] { AppC, AppD } };
        Guid? Scope() => RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppE, deps,
            refuseTwoDeclarers: RecordPatches.RefusesTwoDeclarers);

        // #4901: inside the walk the id resolves as for a group seeing no declarer.
        Assert.Null(RecordPatches.ListingObjectInventory(Scope));
        // #4853: a walk ending in an exception must not leave the listing mode on for this thread,
        // or every later lookup on it would answer null silently instead of refusing.
        Assert.Throws<InvalidOperationException>(() =>
            RecordPatches.ListingObjectInventory<int>(() => throw new InvalidOperationException("walk failed")));
        Assert.True(RecordPatches.RefusesTwoDeclarers);
        Assert.Throws<RunnerOutOfScopeException>(() => Scope());
    }

    [Fact]
    public void IsTwoDeclarersRefusal_MatchesTheTwoDeclarersRefusalOnly()
    {
        var declarers = new Dictionary<(string Kind, int Id), HashSet<Guid>> { [("table", 62680)] = new() { AppC, AppD } };
        var deps = new Dictionary<Guid, Guid[]> { [AppE] = new[] { AppC, AppD } };
        var twoDeclarers = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.AppGroupScopeFor("table", 62680, declarers, AppE, deps));

        // #4853: the load-time field-trigger walk swallows this refusal and nothing else.
        Assert.True(RecordPatches.IsTwoDeclarersRefusal(twoDeclarers));
        Assert.False(RecordPatches.IsTwoDeclarersRefusal(new RunnerOutOfScopeException("table 62680", "not-yet-implemented")));
        Assert.False(RecordPatches.IsTwoDeclarersRefusal(
            new RunnerOutOfScopeException("NavEmail.Send", "email-smtp", "docs/scope.md#email")));
    }

    private static string WriteApp(string dir, Guid appId, string name, int from, int to, params (Guid Id, string Name)[] dependsOn)
    {
        Directory.CreateDirectory(dir);
        var deps = "[" + string.Join(", ", dependsOn.Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "AL Runner", "version": "1.0.0.0" }""")) + "]";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{appId}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": {{deps}}, "platform": "1.0.0.0", "idRanges": [ { "from": {{from}}, "to": {{to}} } ], "runtime": "14.0" }
        """);
        return dir;
    }

    /// <summary>
    /// Two shapes where "who owns this object" is not "the nearest app.json above the file":
    /// a suite compiling a sub-folder that carries its own app.json, and two unrelated groups
    /// declaring the same table id. Both groups must still list the objects they compile.
    /// </summary>
    private static string[] WriteOwnershipEdgeFixtures(string root, bool zInRequest = true)
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
        // Only X states AutoIncrement, UseRequestPage = false, DelayedInsert and RefreshOnActivate,
        // so each group's answer differs from the other's (#4767).
        foreach (var (letter, appId, cu, bufferId, subsId, autoIncrement, secondSeq, xOnly) in new[]
                 { ("X", AppC, 62681, 62684, 62690, "true", 2, "true"), ("Y", AppD, 62682, 62685, 62691, "false", 0, "false") })
        {
            // #4844: group Z depends on X, so X's subscribers see Z's row ZZ in X's table and every
            // X event also reaches Z's subscriber; Y sees neither. A run that does not include Z
            // (zInRequest: false) has no Z subscriber at all.
            var alsoOwn = letter == "X" ? " and (Rec.Code <> 'ZZ')" : "";
            string Reached(string got, string own, string z)
                => letter != "X" || !zInRequest ? $"{got} <> '{own}'"
                    : $"(StrLen({got}) <> {own.Length + z.Length}) or (StrPos({got}, '{own[..2]}') = 0) or (StrPos({got}, '{z}') = 0)" + (own.Length > 2 ? $" or (StrPos({got}, '{own[2..]}') = 0)" : "");
            var dir = WriteApp(Path.Combine(root, "dup" + letter), appId, "Dup " + letter, 62680, 62699);
            File.WriteAllText(Path.Combine(dir, "Dup.al"), $$"""
            table 62680 "Dup {{letter}} Table"
            {
                fields
                {
                    field(1; "Code"; Code[20]) { }
                    field(2; "Only{{letter}}"; Integer) { Editable = {{(xOnly == "true" ? "false" : "true")}}; trigger OnValidate() begin Mark := 'V{{letter}}'; end; }
                    field(3; Mark; Code[10]) { }
                    field(4; Seq; Integer) { AutoIncrement = {{autoIncrement}}; }
                }
                keys { key(PK; "Code") { Clustered = true; } }
                // #4834: one event name both groups declare on their table 62680, and one only this group does.
                procedure RaiseShared(): Text
                var
                    Tag: Text;
                begin
                    OnShared(Tag);
                    OnOwn{{letter}}(Tag);
                    exit(Tag);
                end;
                [IntegrationEvent(false, false)]
                local procedure OnShared(var Tag: Text) begin end;
                [IntegrationEvent(false, false)]
                local procedure OnOwn{{letter}}(var Tag: Text) begin end;
            }
            codeunit {{subsId}} "Dup {{letter}} Subs"
            {
                SingleInstance = true;
                var Fired: Text;
                [EventSubscriber(ObjectType::Table, Database::"Dup {{letter}} Table", 'OnAfterInsertEvent', '', false, false)]
                local procedure OnAfterInsert(var Rec: Record "Dup {{letter}} Table")
                begin
                    Fired += 'S{{letter}}';
                    // #4834: a Z-coded row inserted by the OTHER group must never reach this subscriber.
                    if (CopyStr(Rec.Code, 1, 1) = 'Z') and (Rec.Code <> 'Z{{letter}}'){{alsoOwn}} then
                        Error('LEAK: the OnAfterInsert subscriber of {{letter}} fired for row %1', Rec.Code);
                end;
                [EventSubscriber(ObjectType::Table, Database::"Dup {{letter}} Table", 'OnAfterValidateEvent', 'Code', false, false)]
                local procedure OnAfterValidateCode(var Rec: Record "Dup {{letter}} Table")
                begin
                    if (CopyStr(Rec.Code, 1, 1) = 'Z') and (Rec.Code <> 'Z{{letter}}'){{alsoOwn}} then
                        Error('LEAK: the OnAfterValidate subscriber of {{letter}} fired for row %1', Rec.Code);
                    Fired += 'W{{letter}}';
                end;
                [EventSubscriber(ObjectType::Table, Database::"Dup {{letter}} Table", 'OnShared', '', false, false)]
                local procedure OnSharedEvent(var Tag: Text) begin Tag += 'E{{letter}}'; end;
                [EventSubscriber(ObjectType::Table, Database::"Dup {{letter}} Table", 'OnOwn{{letter}}', '', false, false)]
                local procedure OnOwnEvent(var Tag: Text) begin Tag += 'O{{letter}}'; end;
                [EventSubscriber(ObjectType::Report, Report::"Dup {{letter}} Report", 'OnReportShared', '', false, false)]
                local procedure OnReportSharedEvent(var Tag: Text) begin Tag += 'R{{letter}}'; end;
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Dup {{letter}} Pub", 'OnCodeunitShared', '', false, false)]
                local procedure OnCodeunitSharedEvent(var Tag: Text) begin Tag += 'C{{letter}}'; end;
                procedure Take(): Text
                var
                    T: Text;
                begin
                    T := Fired;
                    Fired := '';
                    exit(T);
                end;
            }
            xmlport 62683 "Dup {{letter}} XmlPort" { Caption = 'Dup {{letter}} XmlPort Cap'; schema { textelement(Root{{letter}}) { } } }
            table {{bufferId}} "Dup {{letter}} Buffer" { fields { field(1; PK; Integer) { } field(2; Data; Blob) { } } keys { key(PK; PK) { Clustered = true; } } }
            report 62686 "Dup {{letter}} Report"
            {
                Caption = 'Dup {{letter}} Report Cap';
                ProcessingOnly = true;
                UseRequestPage = {{(xOnly == "true" ? "false" : "true")}};
                dataset { dataitem(T{{letter}}; "Dup {{letter}} Table") { } }
                trigger OnPreReport() begin Error('RAN REPORT {{letter}}'); end;
                procedure RaiseShared(): Text
                var
                    Tag: Text;
                begin
                    OnReportShared(Tag);
                    exit(Tag);
                end;
                [IntegrationEvent(false, false)]
                local procedure OnReportShared(var Tag: Text) begin end;
            }
            // #4853: no test of this group runs 62692, so Z is the first to resolve its type.
            report 62692 "Dup {{letter}} Unrun Report"
            {
                ProcessingOnly = true;
                UseRequestPage = false;
                trigger OnPreReport() begin Error('RAN UNRUN REPORT {{letter}}'); end;
            }
            codeunit 62689 "Dup {{letter}} Pub"
            {
                procedure RaiseShared(): Text
                var
                    Tag: Text;
                begin
                    OnCodeunitShared(Tag);
                    exit(Tag);
                end;
                [IntegrationEvent(false, false)]
                local procedure OnCodeunitShared(var Tag: Text) begin end;
            }
            page 62687 "Dup {{letter}} Page"
            {
                Caption = 'Dup {{letter}} Page Cap';
                SourceTable = "Dup {{letter}} Table";
                DelayedInsert = {{xOnly}};
                RefreshOnActivate = {{xOnly}};
                layout { area(Content) { field(C{{letter}}; Rec.Code) { } field(O{{letter}}; Rec."Only{{letter}}") { } } }
            }
            pageextension 62689 "Dup {{letter}} PageExt" extends "Dup {{letter}} Page"
            {
                layout { addlast(Content) { field(E{{letter}}; Rec.Seq) { } } }
            }
            query 62688 "Dup {{letter}} Query" { elements { dataitem(T; "Dup {{letter}} Table") { column(C; Code) { } } } }
            codeunit {{cu}} "Dup {{letter}} Tests"
            {
                Subtype = Test;
                [Test]
                procedure OwnTableWithASharedIdIsListed()
                var
                    AllObj: Record AllObj;
                    TableMetadata: Record "Table Metadata";
                    XmlPortMetadata: Record "XmlPort Metadata";
                begin
                    if not AllObj.Get(AllObj."Object Type"::Table, 62680) then Error('MISSING: AllObj does not list own table 62680 in {{letter}}');
                    if not TableMetadata.Get(62680) then Error('MISSING: Table Metadata does not list own table 62680 in {{letter}}');
                    // #4461: an id two groups declare has no single owner, so the group running
                    // second must not take the first group's compiled xmlport as its owner and hide it.
                    if not XmlPortMetadata.Get(62683) then Error('MISSING: XMLport Metadata does not list own xmlport 62683 in {{letter}}');
                end;

                // #4751: each group reads its OWN xmlport 62683, not whichever group resolved the id first.
                [Test]
                procedure OwnXmlPortWithASharedIdIsThisGroupsOwn()
                var
                    XmlPortMetadata: Record "XmlPort Metadata";
                    Buffer: Record "Dup {{letter}} Buffer";
                    ExpectedApp: Guid;
                    OutS: OutStream;
                    InS: InStream;
                    Exported: Text;
                begin
                    Evaluate(ExpectedApp, '{{appId}}');
                    if not XmlPortMetadata.Get(62683) then Error('MISSING: XMLport Metadata does not list own xmlport 62683 in {{letter}}');
                    if XmlPortMetadata.Name <> 'Dup {{letter}} XmlPort' then Error('WRONG: XMLport Metadata name for 62683 in {{letter}} is %1', XmlPortMetadata.Name);
                    if XmlPortMetadata."App ID" <> ExpectedApp then Error('WRONG: XMLport Metadata App ID for 62683 in {{letter}} is %1', XmlPortMetadata."App ID");
                    Buffer.Data.CreateOutStream(OutS);
                    Xmlport.Export(62683, OutS);
                    Buffer.Data.CreateInStream(InS, TextEncoding::UTF16);
                    InS.Read(Exported);
                    if StrPos(Exported, '<Root{{letter}}') = 0 then Error('WRONG: xmlport 62683 exported another group''s schema in {{letter}}: %1', Exported);
                end;

                // #4767: the inventory rows for every kind of object an id two groups both declare are this
                // group's own.
                [Test]
                procedure SharedTableMetadataIsThisGroupsOwn()
                var
                    TableMetadata: Record "Table Metadata";
                    AllObj: Record AllObjWithCaption;
                begin
                    TableMetadata.Get(62680);
                    if TableMetadata.Name <> 'Dup {{letter}} Table' then Error('WRONG: Table Metadata name for 62680 in {{letter}} is %1', TableMetadata.Name);
                    AllObj.Get(AllObj."Object Type"::Table, 62680);
                    if AllObj."Object Name" <> 'Dup {{letter}} Table' then Error('WRONG: AllObjWithCaption table name for 62680 in {{letter}} is %1', AllObj."Object Name");
                end;

                [Test]
                procedure SharedXmlPortAllObjWithCaptionIsThisGroupsOwn()
                var
                    AllObj: Record AllObjWithCaption;
                begin
                    AllObj.Get(AllObj."Object Type"::XMLport, 62683);
                    if AllObj."Object Name" <> 'Dup {{letter}} XmlPort' then Error('WRONG: AllObjWithCaption xmlport name for 62683 in {{letter}} is %1', AllObj."Object Name");
                    if AllObj."Object Caption" <> 'Dup {{letter}} XmlPort Cap' then Error('WRONG: AllObjWithCaption xmlport caption for 62683 in {{letter}} is %1', AllObj."Object Caption");
                end;

                [Test]
                procedure SharedReportMetadataIsThisGroupsOwn()
                var
                    ReportMetadata: Record "Report Metadata";
                    AllObj: Record AllObjWithCaption;
                begin
                    if not ReportMetadata.Get(62686) then Error('MISSING: Report Metadata 62686 in {{letter}}');
                    if ReportMetadata.Name <> 'Dup {{letter}} Report' then Error('WRONG: Report Metadata name for 62686 in {{letter}} is %1', ReportMetadata.Name);
                    // The dataitem names this group's own table, resolved by name in this group.
                    if ReportMetadata.FirstDataItemTableID <> 62680 then Error('WRONG: Report Metadata first data item table for 62686 in {{letter}} is %1', ReportMetadata.FirstDataItemTableID);
                    AllObj.Get(AllObj."Object Type"::Report, 62686);
                    if AllObj."Object Name" <> 'Dup {{letter}} Report' then Error('WRONG: AllObjWithCaption report name for 62686 in {{letter}} is %1', AllObj."Object Name");
                    if AllObj."Object Caption" <> 'Dup {{letter}} Report Cap' then Error('WRONG: AllObjWithCaption report caption for 62686 in {{letter}} is %1', AllObj."Object Caption");
                end;

                [Test]
                procedure SharedPageMetadataIsThisGroupsOwn()
                var
                    PageMetadata: Record "Page Metadata";
                    AllObj: Record AllObjWithCaption;
                begin
                    if not PageMetadata.Get(62687) then Error('MISSING: Page Metadata 62687 in {{letter}}');
                    if PageMetadata.Name <> 'Dup {{letter}} Page' then Error('WRONG: Page Metadata name for 62687 in {{letter}} is %1', PageMetadata.Name);
                    if PageMetadata.Caption <> 'Dup {{letter}} Page Cap' then Error('WRONG: Page Metadata caption for 62687 in {{letter}} is %1', PageMetadata.Caption);
                    if PageMetadata.SourceTable <> 62680 then Error('WRONG: Page Metadata source table for 62687 in {{letter}} is %1', PageMetadata.SourceTable);
                    AllObj.Get(AllObj."Object Type"::Page, 62687);
                    if AllObj."Object Name" <> 'Dup {{letter}} Page' then Error('WRONG: AllObjWithCaption page name for 62687 in {{letter}} is %1', AllObj."Object Name");
                end;

                // #4767, runtime half: the Field table, RecordRef, the record's own field trigger and
                // its table subscriber all read this group's NCLMetaTable of the shared id.
                [Test]
                procedure SharedTableRuntimeMetadataIsThisGroupsOwn()
                var
                    Fld: Record Field;
                    RecRef: RecordRef;
                    Rec: Record "Dup {{letter}} Table";
                    Subs: Codeunit "Dup {{letter}} Subs";
                begin
                    if not Fld.Get(62680, 2) then Error('MISSING: Field 62680/2 in {{letter}}');
                    if Fld.FieldName <> 'Only{{letter}}' then Error('WRONG: Field name for 62680/2 in {{letter}} is %1', Fld.FieldName);
                    RecRef.Open(62680);
                    if RecRef.Name <> 'Dup {{letter}} Table' then Error('WRONG: RecordRef name for 62680 in {{letter}} is %1', RecRef.Name);
                    if RecRef.Field(2).Name <> 'Only{{letter}}' then Error('WRONG: RecordRef field 2 name for 62680 in {{letter}} is %1', RecRef.Field(2).Name);
                    RecRef.Close();
                    Subs.Take();
                    Rec.Code := 'A';
                    Rec.Validate("Only{{letter}}", 7);
                    Rec.Insert();
                    if StrPos(Subs.Take(), 'S{{letter}}') = 0 then Error('WRONG: own OnAfterInsert subscriber on 62680 did not fire in {{letter}}');
                    Rec.Get('A');
                    if Rec."Only{{letter}}" <> 7 then Error('WRONG: stored value of 62680 field 2 in {{letter}} is %1', Rec."Only{{letter}}");
                    if Rec.Mark <> 'V{{letter}}' then Error('WRONG: field OnValidate of 62680 in {{letter}} set Mark to %1', Rec.Mark);
                    // Only X declares field 4 AutoIncrement.
                    Rec.Init();
                    Rec.Code := 'B';
                    Rec.Insert();
                    Rec.Get('B');
                    if Rec.Seq <> {{secondSeq}} then Error('WRONG: AutoIncrement Seq of the second row of 62680 in {{letter}} is %1', Rec.Seq);
                end;

                [Test]
                procedure SharedQueryMetadataIsThisGroupsOwn()
                var
                    QueryMetadata: Record "Query Metadata";
                begin
                    if not QueryMetadata.Get(62688) then Error('MISSING: Query Metadata 62688 in {{letter}}');
                    if QueryMetadata.Name <> 'Dup {{letter}} Query' then Error('WRONG: Query Metadata name for 62688 in {{letter}} is %1', QueryMetadata.Name);
                end;

                [Test]
                procedure SharedReportRunsThisGroupsOwn()
                var
                    ReportMetadata: Record "Report Metadata";
                begin
                    // Read from the report's emitted document, not from the AL parse.
                    ReportMetadata.Get(62686);
                    if ReportMetadata.UseRequestPage = {{xOnly}} then Error('WRONG: Report Metadata UseRequestPage for 62686 in {{letter}} is %1', ReportMetadata.UseRequestPage);
                    asserterror Report.Run(62686, false, false);
                    if GetLastErrorText() <> 'RAN REPORT {{letter}}' then Error('WRONG: Report.Run(62686) in {{letter}} ended with: %1', GetLastErrorText());
                end;

                [Test]
                procedure SharedPageTestPageIsThisGroupsOwn()
                var
                    PageMetadata: Record "Page Metadata";
                    TP: TestPage "Dup {{letter}} Page";
                    Rec: Record "Dup {{letter}} Table";
                begin
                    Rec.Code := 'P';
                    Rec."Only{{letter}}" := 5;
                    Rec.Insert();
                    TP.OpenView();
                    TP.GoToRecord(Rec);
                    if TP.C{{letter}}.Value <> 'P' then Error('WRONG: TestPage control C of 62687 in {{letter}} reads %1', TP.C{{letter}}.Value);
                    if TP.O{{letter}}.AsInteger() <> 5 then Error('WRONG: TestPage control O of 62687 in {{letter}} reads %1', TP.O{{letter}}.Value);
                    if TP.Caption <> 'Dup {{letter}} Page Cap' then Error('WRONG: TestPage caption of 62687 in {{letter}} is %1', TP.Caption);
                    TP.Close();
                    // Page Metadata's <SourceObject> and <Properties> columns come from the loaded page metadata.
                    PageMetadata.Get(62687);
                    if PageMetadata.DelayedInsert <> {{xOnly}} then Error('WRONG: Page Metadata DelayedInsert for 62687 in {{letter}} is %1', PageMetadata.DelayedInsert);
                    if PageMetadata.RefreshOnActivate <> {{xOnly}} then Error('WRONG: Page Metadata RefreshOnActivate for 62687 in {{letter}} is %1', PageMetadata.RefreshOnActivate);
                end;

                // #4834: an event declared on an object of a shared id reaches this group's own
                // subscribers, and only those.
                [Test]
                procedure SharedTableDeclaredEventReachesOnlyThisGroupsSubscribers()
                var
                    Rec: Record "Dup {{letter}} Table";
                    Got: Text;
                begin
                    Got := Rec.RaiseShared();
                    if {{Reached("Got", "E" + letter + "O" + letter, "EZ")}} then Error('WRONG: table-declared events on 62680 in {{letter}} reached subscribers %1', Got);
                end;

                [Test]
                procedure SharedReportEventReachesOnlyThisGroupsSubscribers()
                var
                    Rep: Report "Dup {{letter}} Report";
                    Got: Text;
                begin
                    Got := Rep.RaiseShared();
                    if {{Reached("Got", "R" + letter, "RZ")}} then Error('WRONG: report-declared event on 62686 in {{letter}} reached subscribers %1', Got);
                end;

                [Test]
                procedure SharedCodeunitEventReachesOnlyThisGroupsSubscribers()
                var
                    Pub: Codeunit "Dup {{letter}} Pub";
                    Got: Text;
                begin
                    Got := Pub.RaiseShared();
                    if {{Reached("Got", "C" + letter, "CZ")}} then Error('WRONG: codeunit-declared event on 62689 in {{letter}} reached subscribers %1', Got);
                end;

                [Test]
                procedure SharedTableTriggerEventReachesOnlyThisGroupsSubscribers()
                var
                    Rec: Record "Dup {{letter}} Table";
                    Subs: Codeunit "Dup {{letter}} Subs";
                begin
                    Subs.Take();
                    Rec.Validate(Code, 'Z{{letter}}');
                    Rec.Insert();
                    if Subs.Take() <> 'W{{letter}}S{{letter}}' then Error('WRONG: own OnAfterValidate and OnAfterInsert subscribers on 62680 did not fire once each in {{letter}}');
                end;

                // #4833: the Page Control Field rows for the shared page id are this group's controls.
                [Test]
                procedure SharedPageControlFieldIsThisGroupsOwn()
                var
                    PCF: Record "Page Control Field";
                    Names: Text;
                begin
                    PCF.SetRange(PageNo, 62687);
                    if PCF.FindSet() then
                        repeat
                            Names += PCF.ControlName + '=' + PCF.Editable + ',';
                        until PCF.Next() = 0;
                    // Only X declares field 2 Editable = false; each group's pageextension 62689 adds E.
                    // Rows come in control-id order, and those ids differ between the groups.
                    if (PCF.Count() <> 3) or (StrPos(Names, 'C{{letter}}=True,') = 0)
                       or (StrPos(Names, 'O{{letter}}={{(xOnly == "true" ? "False" : "True")}},') = 0) or (StrPos(Names, 'E{{letter}}=True,') = 0)
                    then
                        Error('WRONG: Page Control Field names for 62687 in {{letter}} are %1', Names);
                end;

                [Test]
                procedure SharedQueryAllObjWithCaptionIsThisGroupsOwn()
                var
                    AllObj: Record AllObjWithCaption;
                begin
                    AllObj.Get(AllObj."Object Type"::Query, 62688);
                    if AllObj."Object Name" <> 'Dup {{letter}} Query' then Error('WRONG: AllObjWithCaption query name for 62688 in {{letter}} is %1', AllObj."Object Name");
                end;

                // #4845: the Event Subscription rows for a publisher id two groups declare are the
                // subscriptions to this group's object: its own, and Z's in X, never the other group's.
                [Test]
                procedure SharedIdEventSubscriptionsAreThisGroupsOwn()
                var
                    ES: Record "Event Subscription";
                begin
                    ES.SetRange("Publisher Object Type", ES."Publisher Object Type"::Table);
                    CheckSubscribers(ES, 62680, 'table');
                    ES.SetRange("Publisher Object Type", ES."Publisher Object Type"::Codeunit);
                    CheckSubscribers(ES, 62689, 'codeunit');
                    ES.SetRange("Publisher Object Type", ES."Publisher Object Type"::Report);
                    CheckSubscribers(ES, 62686, 'report');
                end;

                local procedure CheckSubscribers(var ES: Record "Event Subscription"; PublisherId: Integer; Kind: Text)
                var
                    Got: Text;
                begin
                    ES.SetRange("Publisher Object ID", PublisherId);
                    if ES.FindSet() then
                        repeat
                            if StrPos(Got, Format(ES."Subscriber Codeunit ID", 0, 9) + ',') = 0 then
                                Got += Format(ES."Subscriber Codeunit ID", 0, 9) + ',';
                        until ES.Next() = 0;
                    if {{(letter != "X" ? "Got <> '62691,'" : zInRequest ? "Got <> '62690,62700,'" : "Got <> '62690,'")}} then
                        Error('WRONG: Event Subscription subscribers of %1 %2 in {{letter}} are %3', Kind, PublisherId, Got);
                end;
            }
            """);
            dirs.Add(dir);
        }
        dirs.Add(WriteDependentGroupFixture(root));
        return dirs.ToArray();
    }

    /// <summary>
    /// #4844: group Z declares none of the shared ids and depends on X, so every shared id it names
    /// is X's object: its subscribers belong to X's publishers only, and it reads X's metadata.
    /// </summary>
    private static string WriteDependentGroupFixture(string root)
    {
        var dir = WriteApp(Path.Combine(root, "depZ"), AppE, "Dep Z", 62700, 62719, (AppC, "Dup X"));
        File.WriteAllText(Path.Combine(dir, "Z.al"), """
        codeunit 62700 "Dep Z Subs"
        {
            SingleInstance = true;
            var Fired: Text;
            [EventSubscriber(ObjectType::Table, Database::"Dup X Table", 'OnShared', '', false, false)]
            local procedure OnSharedEvent(var Tag: Text) begin Tag += 'EZ'; end;
            [EventSubscriber(ObjectType::Report, Report::"Dup X Report", 'OnReportShared', '', false, false)]
            local procedure OnReportSharedEvent(var Tag: Text) begin Tag += 'RZ'; end;
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Dup X Pub", 'OnCodeunitShared', '', false, false)]
            local procedure OnCodeunitSharedEvent(var Tag: Text) begin Tag += 'CZ'; end;
            [EventSubscriber(ObjectType::Table, Database::"Dup X Table", 'OnAfterInsertEvent', '', false, false)]
            local procedure OnAfterInsert(var Rec: Record "Dup X Table")
            begin
                if (CopyStr(Rec.Code, 1, 1) = 'Z') and (Rec.Code <> 'ZX') and (Rec.Code <> 'ZZ') then
                    Error('LEAK: the OnAfterInsert subscriber of Z fired for row %1', Rec.Code);
                Fired += 'SZ';
            end;
            [EventSubscriber(ObjectType::Table, Database::"Dup X Table", 'OnAfterValidateEvent', 'Code', false, false)]
            local procedure OnAfterValidateCode(var Rec: Record "Dup X Table")
            begin
                if (CopyStr(Rec.Code, 1, 1) = 'Z') and (Rec.Code <> 'ZX') and (Rec.Code <> 'ZZ') then
                    Error('LEAK: the OnAfterValidate subscriber of Z fired for row %1', Rec.Code);
                Fired += 'WZ';
            end;
            procedure Take(): Text
            var
                T: Text;
            begin
                T := Fired;
                Fired := '';
                exit(T);
            end;
        }
        codeunit 62701 "Dep Z Tests"
        {
            Subtype = Test;

            [Test]
            procedure DependentGroupRaisingTheDeclarersEventReachesOnlyItsSubscribers()
            var
                Rec: Record "Dup X Table";
                Rep: Report "Dup X Report";
                Pub: Codeunit "Dup X Pub";
                Got: Text;
            begin
                Got := Rec.RaiseShared();
                if (StrLen(Got) <> 6) or (StrPos(Got, 'EX') = 0) or (StrPos(Got, 'EZ') = 0) or (StrPos(Got, 'OX') = 0) then
                    Error('WRONG: table-declared events on 62680 raised in Z reached subscribers %1', Got);
                Got := Rep.RaiseShared();
                if (StrLen(Got) <> 4) or (StrPos(Got, 'RX') = 0) or (StrPos(Got, 'RZ') = 0) then
                    Error('WRONG: report-declared event on 62686 raised in Z reached subscribers %1', Got);
                Got := Pub.RaiseShared();
                if (StrLen(Got) <> 4) or (StrPos(Got, 'CX') = 0) or (StrPos(Got, 'CZ') = 0) then
                    Error('WRONG: codeunit-declared event on 62689 raised in Z reached subscribers %1', Got);
            end;

            [Test]
            procedure DependentGroupInsertingIntoTheDeclarersTableReachesOnlyItsSubscribers()
            var
                Rec: Record "Dup X Table";
                XSubs: Codeunit "Dup X Subs";
                ZSubs: Codeunit "Dep Z Subs";
                Got: Text;
            begin
                XSubs.Take();
                ZSubs.Take();
                Rec.Validate(Code, 'ZZ');
                Rec.Insert();
                Got := XSubs.Take();
                if Got <> 'WXSX' then Error('WRONG: X''s OnAfterValidate and OnAfterInsert subscribers on 62680 fired %1 for Z''s row', Got);
                Got := ZSubs.Take();
                if Got <> 'WZSZ' then Error('WRONG: Z''s OnAfterValidate and OnAfterInsert subscribers on 62680 fired %1 for Z''s row', Got);
            end;

            [Test]
            procedure DependentGroupReadsTheDeclarersMetadata()
            var
                TableMetadata: Record "Table Metadata";
                AllObj: Record AllObjWithCaption;
                Fld: Record Field;
                ReportMetadata: Record "Report Metadata";
                PageMetadata: Record "Page Metadata";
                XmlPortMetadata: Record "XmlPort Metadata";
                QueryMetadata: Record "Query Metadata";
                RecRef: RecordRef;
            begin
                TableMetadata.Get(62680);
                if TableMetadata.Name <> 'Dup X Table' then Error('WRONG: Table Metadata name for 62680 in Z is %1', TableMetadata.Name);
                AllObj.Get(AllObj."Object Type"::Table, 62680);
                if AllObj."Object Name" <> 'Dup X Table' then Error('WRONG: AllObjWithCaption table name for 62680 in Z is %1', AllObj."Object Name");
                RecRef.Open(62680);
                if RecRef.Name <> 'Dup X Table' then Error('WRONG: RecordRef name for 62680 in Z is %1', RecRef.Name);
                RecRef.Close();
                Fld.Get(62680, 2);
                if Fld.FieldName <> 'OnlyX' then Error('WRONG: Field name for 62680/2 in Z is %1', Fld.FieldName);
                ReportMetadata.Get(62686);
                if ReportMetadata.Name <> 'Dup X Report' then Error('WRONG: Report Metadata name for 62686 in Z is %1', ReportMetadata.Name);
                PageMetadata.Get(62687);
                if PageMetadata.Name <> 'Dup X Page' then Error('WRONG: Page Metadata name for 62687 in Z is %1', PageMetadata.Name);
                XmlPortMetadata.Get(62683);
                if XmlPortMetadata.Name <> 'Dup X XmlPort' then Error('WRONG: XMLport Metadata name for 62683 in Z is %1', XmlPortMetadata.Name);
                QueryMetadata.Get(62688);
                if QueryMetadata.Name <> 'Dup X Query' then Error('WRONG: Query Metadata name for 62688 in Z is %1', QueryMetadata.Name);
            end;

            // #4853: a report run by id finds the declarer Z depends on, not the other group's same-id
            // report. 62692, not 62686: X's own run of 62686 caches its type under X, which Z reuses.
            [Test]
            procedure DependentGroupRunningTheSharedReportIdRunsTheDeclarers()
            begin
                asserterror Report.Run(62692, false, false);
                if GetLastErrorText() <> 'RAN UNRUN REPORT X' then Error('WRONG: Report.Run(62692) in Z ended with: %1', GetLastErrorText());
            end;

            // #4845: Z binds 62680 to X's table, so it lists the subscriptions to X's table, Y's none.
            [Test]
            procedure DependentGroupListsTheDeclarersEventSubscriptions()
            var
                ES: Record "Event Subscription";
                Got: Text;
            begin
                ES.SetRange("Publisher Object Type", ES."Publisher Object Type"::Table);
                ES.SetRange("Publisher Object ID", 62680);
                if ES.FindSet() then
                    repeat
                        if StrPos(Got, Format(ES."Subscriber Codeunit ID", 0, 9) + ',') = 0 then
                            Got += Format(ES."Subscriber Codeunit ID", 0, 9) + ',';
                    until ES.Next() = 0;
                if Got <> '62690,62700,' then
                    Error('WRONG: Event Subscription subscribers of table 62680 in Z are %1', Got);
            end;
        }
        """);
        return dir;
    }

    [SkippableFact]
    public void Cli_NestedAppJsonAndSharedTableId_EachGroupStillListsWhatItCompiles()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-visibility-edges-cli");
        Directory.CreateDirectory(root);
        WriteOwnershipEdgeFixtures(root);

        AssertEdgeFixturesPass(RunCli($" --no-cache \"{root}\""));
    }

    /// <summary>
    /// #4767: a cache HIT replays each group's emit-captured page and report documents from that
    /// group's own sidecar. The sidecar is written after the previous group's tests ran, so it must
    /// read the compiling group's document, not the one the still-current test assembly sees.
    /// </summary>
    [SkippableFact]
    public void Cli_SharedIds_ColdThenWarmOnOneCacheRoot_EachGroupStillReadsItsOwn()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-visibility-edges-warm");
        Directory.CreateDirectory(root);
        WriteOwnershipEdgeFixtures(root);
        var cache = TestScratch.Dir("al-runner-app-group-visibility-edges-warm-cache");

        AssertEdgeFixturesPass(RunCli($" --cache \"{cache}\" \"{root}\""));
        AssertEdgeFixturesPass(RunCli($" --cache \"{cache}\" \"{root}\""));
    }

    /// <summary>
    /// #4844: a group depending on BOTH declarers of an id cannot say which object its code names.
    /// Every test reaching that binding fails naming the id and both declarers, and the run itself
    /// completes. The declarers' own tests pass, with that group's subscriber excluded (#4853).
    /// </summary>
    [SkippableFact]
    public void Cli_GroupDependingOnTwoDeclarersOfOneId_FailsLoudlyPerTest_AndTheRunCompletes()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-visibility-two-declarers");
        Directory.CreateDirectory(root);
        var appF = new Guid("3b0e6f52-0000-4c38-9e25-0000000000f1");
        var appG = new Guid("3b0e6f52-0000-4c38-9e25-0000000000f2");
        foreach (var (letter, appId) in new[] { ("F", appF), ("G", appG) })
        {
            var dir = WriteApp(Path.Combine(root, "two" + letter), appId, "Two " + letter, 62740, 62749);
            File.WriteAllText(Path.Combine(dir, "Two.al"), $$"""
            table 62740 "Two {{letter}} Table"
            {
                fields { field(1; "Code"; Code[20]) { } }
                keys { key(PK; "Code") { Clustered = true; } }
            }
            // #4845: a subscription to the id, so group H's Event Subscription read reaches the ambiguity.
            codeunit 62742 "Two {{letter}} Subs"
            {
                [EventSubscriber(ObjectType::Table, Database::"Two {{letter}} Table", 'OnAfterInsertEvent', '', false, false)]
                local procedure OnAfterInsert(var Rec: Record "Two {{letter}} Table") begin end;
            }
            codeunit 62741 "Two {{letter}} Tests"
            {
                Subtype = Test;
                [Test]
                procedure OwnInsertStillRuns()
                var
                    Rec: Record "Two {{letter}} Table";
                begin
                    Rec.Code := 'A';
                    Rec.Insert();
                    if not Rec.Get('A') then Error('WRONG: own insert of 62740 lost in {{letter}}');
                end;

                // #4853: H's subscription is to neither declarer's table.
                [Test]
                procedure OwnEventSubscriptionsOmitTheAmbiguousGroup()
                var
                    ES: Record "Event Subscription";
                    Got: Text;
                begin
                    ES.SetRange("Publisher Object Type", ES."Publisher Object Type"::Table);
                    ES.SetRange("Publisher Object ID", 62740);
                    if ES.FindSet() then
                        repeat
                            if StrPos(Got, Format(ES."Subscriber Codeunit ID", 0, 9) + ',') = 0 then
                                Got += Format(ES."Subscriber Codeunit ID", 0, 9) + ',';
                        until ES.Next() = 0;
                    if Got <> '62742,' then Error('WRONG: Event Subscription subscribers of table 62740 in {{letter}} are %1', Got);
                end;
            }
            """);
        }
        var both = WriteApp(Path.Combine(root, "bothH"), AppE, "Both H", 62750, 62759, (appF, "Two F"), (appG, "Two G"));
        File.WriteAllText(Path.Combine(both, "Both.al"), """
        // #4853: H cannot be installed beside F or G, so this never fires in their runs.
        codeunit 62751 "Both H Subs"
        {
            [EventSubscriber(ObjectType::Table, Database::"Two F Table", 'OnAfterInsertEvent', '', false, false)]
            local procedure OnAfterInsert(var Rec: Record "Two F Table")
            begin
                Error('LEAK: the OnAfterInsert subscriber of H fired for row %1', Rec.Code);
            end;
        }
        codeunit 62750 "Both H Tests"
        {
            Subtype = Test;
            [Test]
            procedure ReadsTheAmbiguousTable()
            var
                TableMetadata: Record "Table Metadata";
            begin
                TableMetadata.Get(62740);
            end;

            [Test]
            procedure ListsTheAmbiguousTablesEventSubscriptions()
            var
                ES: Record "Event Subscription";
            begin
                ES.SetRange("Publisher Object ID", 62740);
                if ES.FindFirst() then;
            end;

            [Test]
            procedure TouchesNothingShared()
            begin
            end;

            // #4901: an inventory read names no object, so it lists the shared id from one declaration.
            [Test]
            procedure ListsTheAmbiguousIdInAllObj()
            var
                AllObj: Record AllObjWithCaption;
            begin
                if not AllObj.Get(AllObj."Object Type"::Table, 62740) then Error('MISSING: AllObjWithCaption does not list table 62740 in H');
                if (AllObj."Object Name" <> 'Two F Table') and (AllObj."Object Name" <> 'Two G Table') then
                    Error('WRONG: AllObjWithCaption name of table 62740 in H is %1', AllObj."Object Name");
            end;
        }
        """);

        // Microsoft's Test Runner app, whose reset subscriber reads AllObj before every test, loaded
        // from a cache the run names, so every box runs it (#4901, #4905).
        var testTool = TestRunnerMgtEventsTests.RequireProvisioned();
        var (output, exitCode) = RunCli(
            $" --no-cache --package-cache \"{testTool.TestApps}\" --package-cache \"{testTool.PlatformApps}\" \"{root}\"",
            loadTestToolFrom: testTool.TestApps);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.True(output.Contains("6P/2F/0E across 8 tests"), output);
        // #4853: the declarers' own tests run as if H's subscriber were absent.
        Assert.Equal(2, CountOf(output, "PASS  Codeunit62741.OwnInsertStillRuns"));
        Assert.Equal(2, CountOf(output, "PASS  Codeunit62741.OwnEventSubscriptionsOmitTheAmbiguousGroup"));
        Assert.Contains("PASS  Codeunit62750.TouchesNothingShared", output);
        Assert.Contains("PASS  Codeunit62750.ListsTheAmbiguousIdInAllObj", output);
        // #4845: the Event Subscription read fails on the ambiguity itself, not on anything else.
        var esFailure = output[output.IndexOf("FAIL  \"Both H Tests\".ListsTheAmbiguousTablesEventSubscriptions", StringComparison.Ordinal)..];
        Assert.Contains($"depends on {appF} and {appG}, which each declare Table 62740", esFailure.Split('\n')[1]);
        Assert.Contains($"depends on {appF} and {appG}, which each declare table 62740", output);
        Assert.DoesNotContain("WRONG:", output);
        Assert.DoesNotContain("MISSING:", output);
        Assert.DoesNotContain("LEAK:", output);
        Assert.Equal(1, exitCode);
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static (string Output, int ExitCode) RunCli(string args, string? loadTestToolFrom = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + args,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (loadTestToolFrom != null) DefaultTestToolPin.LoadFrom(psi, loadTestToolFrom);
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertEdgeFixturesPass((string Output, int ExitCode) run)
    {
        var (output, exitCode) = run;
        // The whole runner output as the message, so a red names the failing test and its WRONG: line.
        Assert.True(output.Contains("40P/0F/0E across 40 tests"), output);
        Assert.DoesNotContain("MISSING:", output);
        Assert.DoesNotContain("WRONG:", output);
        Assert.Equal(0, exitCode);
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
        Assert.Equal(40, events.Count);
        foreach (var e in events)
            Assert.True(e.GetProperty("status").GetString() == "pass", string.Join(" | ", lines));
    }

    /// <summary>
    /// #4835: a later --server request running only one declarer of a shared table id must get that
    /// group's own field trigger, subscribers and AutoIncrement, not what an earlier request's other
    /// declarer left behind. Only X declares field 4 AutoIncrement; each group's OnValidate marks its letter.
    /// </summary>
    [SkippableFact]
    public async Task Server_SharedTableId_LaterRequestsWithOneDeclarer_RunThatGroupsOwnTriggerAndAutoIncrement()
    {
        TestArtifacts.SkipIfMissing();
        var dirs = WriteRequestSequenceFixture("al-runner-app-group-visibility-edges-server-sequence");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        await AssertRequestSequencePasses(server, dirs[1], dirs[2]);
    }

    /// <summary>#4835 on a compile cache: the same sequence cold, then again in a second server on
    /// the same cache root, where every module is a cache HIT.</summary>
    [SkippableFact]
    public async Task Server_SharedTableId_LaterRequestsWithOneDeclarer_ColdThenWarmOnOneCacheRoot()
    {
        TestArtifacts.SkipIfMissing();
        var dirs = WriteRequestSequenceFixture("al-runner-app-group-visibility-edges-server-sequence-warm");
        var cache = TestScratch.Dir("al-runner-app-group-visibility-edges-server-sequence-cache");
        for (var pass = 0; pass < 2; pass++)
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
            await AssertRequestSequencePasses(server, dirs[1], dirs[2]);
        }
    }

    private static string[] WriteRequestSequenceFixture(string prefix)
    {
        var root = TestScratch.Dir(prefix);
        Directory.CreateDirectory(root);
        // Every request of the sequence names X and Y only, so Z is never installed there.
        return WriteOwnershipEdgeFixtures(root, zInRequest: false);
    }

    private static async Task AssertRequestSequencePasses(CliServer server, string x, string y)
    {
        int? perGroup = null;
        foreach (var (label, request) in new[] { ("[X,Y]", new[] { x, y }), ("[Y,X]", new[] { y, x }), ("[Y]", new[] { y }), ("[X]", new[] { x }) })
        {
            var lines = await server.SendRequestStreamingAsync(RunTests(request));
            var (events, _) = ProtocolV2Streaming.Split(lines);
            perGroup ??= events.Count / 2;
            Assert.True(perGroup > 0 && events.Count == perGroup * request.Length, $"{label}: {events.Count} events | " + string.Join(" | ", lines));
            var failed = events.Where(e => e.GetProperty("status").GetString() != "pass").Select(e => e.ToString()).ToList();
            Assert.True(failed.Count == 0, $"{label}: " + string.Join(" | ", failed));
        }
    }

    /// <summary>
    /// #4828: groups X and Y depend on D and both declare table 62790. Only X's field 2 relates to
    /// D's table 62780, so renaming a 62780 row carries into X's 62790 rows and never into Y's.
    /// </summary>
    private static (string D, string X, string Y) WriteSharedIdRenameFixture(string root)
    {
        var appD = new Guid("3b0e6f52-0000-4c38-9e25-0000000000d1");
        var d = WriteApp(Path.Combine(root, "renD"), appD, "Ren D", 62780, 62784);
        File.WriteAllText(Path.Combine(d, "Parent.al"), """
        table 62780 "Ren Parent" { fields { field(1; "Code"; Code[20]) { } } keys { key(PK; "Code") { Clustered = true; } } }
        """);
        string Group(string letter, Guid appId, bool related)
        {
            var dir = WriteApp(Path.Combine(root, "ren" + letter), appId, "Ren " + letter, 62790, 62799, (appD, "Ren D"));
            var relation = related ? "TableRelation = \"Ren Parent\";" : "";
            var expected = related ? "P2" : "P1";
            File.WriteAllText(Path.Combine(dir, "Child.al"), $$"""
            table 62790 "Ren Child {{letter}}"
            {
                fields
                {
                    field(1; "Code"; Code[20]) { }
                    field(2; "Parent Code"; Code[20]) { {{relation}} }
                }
                keys { key(PK; "Code") { Clustered = true; } }
            }
            codeunit 62791 "Ren {{letter}} Tests"
            {
                Subtype = Test;
                [Test]
                procedure RenamingTheParentFollowsOnlyThisGroupsRelation()
                var
                    Parent: Record "Ren Parent";
                    Child: Record "Ren Child {{letter}}";
                begin
                    Parent.Code := 'P1';
                    Parent.Insert();
                    Child.Code := 'C1';
                    Child."Parent Code" := 'P1';
                    Child.Insert();
                    Parent.Rename('P2');
                    Child.Get('C1');
                    if Child."Parent Code" <> '{{expected}}' then
                        Error('WRONG: after renaming 62780 P1 to P2, 62790 field 2 in {{letter}} is %1', Child."Parent Code");
                end;
            }
            """);
            return dir;
        }
        return (d, Group("X", AppC, related: true), Group("Y", AppD, related: false));
    }

    [SkippableFact]
    public async Task Server_SharedTableId_RenamePropagationFollowsTheExecutingGroupsOwnRelations()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-shared-id-rename");
        Directory.CreateDirectory(root);
        var (d, x, y) = WriteSharedIdRenameFixture(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        await AssertRenameSequencePasses(server, d, x, y);
    }

    /// <summary>#4828 on a compile cache: the same requests cold, then in a second server on the
    /// same cache root, where every module is a cache HIT.</summary>
    [SkippableFact]
    public async Task Server_SharedTableId_RenamePropagation_ColdThenWarmOnOneCacheRoot()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-shared-id-rename-warm");
        Directory.CreateDirectory(root);
        var (d, x, y) = WriteSharedIdRenameFixture(root);
        var cache = TestScratch.Dir("al-runner-app-group-shared-id-rename-cache");
        for (var pass = 0; pass < 2; pass++)
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
            await AssertRenameSequencePasses(server, d, x, y);
        }
    }

    private static async Task AssertRenameSequencePasses(CliServer server, string d, string x, string y)
    {
        // Both orders: whichever group renames first must not fix the answer for the other.
        foreach (var (label, request) in new[] { ("[D,X,Y]", new[] { d, x, y }), ("[D,Y,X]", new[] { d, y, x }) })
        {
            var lines = await server.SendRequestStreamingAsync(RunTests(request));
            var (events, _) = ProtocolV2Streaming.Split(lines);
            Assert.True(events.Count == 2, $"{label}: {events.Count} events | " + string.Join(" | ", lines));
            var failed = events.Where(e => e.GetProperty("status").GetString() != "pass").Select(e => e.ToString()).ToList();
            Assert.True(failed.Count == 0, $"{label}: " + string.Join(" | ", failed));
        }
    }

    [SkippableFact]
    public void Cli_SharedTableId_RenamePropagationFollowsTheExecutingGroupsOwnRelations()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-app-group-shared-id-rename-cli");
        Directory.CreateDirectory(root);
        WriteSharedIdRenameFixture(root);
        var (output, exitCode) = RunCli($" --no-cache \"{root}\"");
        Assert.True(output.Contains("2P/0F/0E across 2 tests"), output);
        Assert.DoesNotContain("WRONG:", output);
        Assert.Equal(0, exitCode);
    }
}
