using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5084 — the metadata virtual tables a changed object keys. The server-level proof is
/// ServerAffectedSelectionMetadataTableTests; this pins each (kind, table) pair on its own and the
/// POPULATION of tables the runner serves, so a table added to the dispatch chain cannot escape.
/// </summary>
public class AffectedMetadataTablesTests
{
    private const int AllObj = 2000000038, AllObjWithCaption = 2000000058, EventSubscription = 2000000140;

    // Written out here, not read from AffectedMetadataTables.Population: removing a table from that
    // list must fail a row of this one.
    public static IEnumerable<object[]> ListedBy() => new[]
    {
        Row("Codeunit", 2000000137),
        Row("Table", 2000000041, 2000000141, 2000000063, 2000000136),
        Row("TableExtension", 2000000041, 2000000141, 2000000063),
        // #5452: All Profile resolves each profile's RoleCenter page name to a page id, so a page is listed by it too.
        Row("Page", 2000000143, 2000000138, 2000000192, 2000000178),
        Row("PageExtension", 2000000143, 2000000192),
        Row("Query", 2000000142),
        Row("XmlPort", 2000000280),
        Row("Report", 2000000234, 2000000139, 2000000203),
        Row("Enum"),
        Row("Interface"),
        // #5076: a permission set is read through the permission tables, and through nothing else.
        Row("PermissionSet", 2000000004, 2000000005, 2000000167, 2000000250, 2000000251, 2000000254),
        Row("PermissionSetExtension", 2000000004, 2000000005, 2000000167, 2000000250, 2000000251, 2000000254),
        // #5452: a declared profile is read through All Profile and nothing else. A profileextension is not
        // read by it at all (the runner does not apply one), so it keys no table of its own.
        Row("Profile", 2000000178),
        Row("ProfileExtension"),
    };

    // Every kind is listed by AllObj, AllObjWithCaption and Event Subscription.
    private static object[] Row(string kind, params int[] own)
        => new object[] { kind, own.Concat(new[] { AllObj, AllObjWithCaption, EventSubscription }).OrderBy(i => i).ToArray() };

    private static AffectedEventSelection.Result Keys(
        AffectedObjectId[] changed, HashSet<string>? bundleWide, Func<AffectedObjectId, bool>? whole = null)
        => AffectedMetadataTables.ChangedKeys(changed, whole ?? (_ => true), bundleWide);

    private static HashSet<string> Recorded(params string[] keys) => new(keys, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(ListedBy))]
    public void ChangedObject_KeysEveryMetadataTableThatListsItsKind_AndNoOther(string kind, int[] tables)
    {
        var r = Keys(new[] { new AffectedObjectId(kind, 70000, "X") }, Recorded());
        Assert.Null(r.ForceFullReason);
        Assert.Equal(tables.Select(t => $"tbl|Table|{t}").OrderBy(k => k, StringComparer.Ordinal),
            r.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ChangeNarrowedToOneProcedure_KeysNothing_AndNeedsNoRecording()
    {
        var narrowed = new[] { new AffectedObjectId("Codeunit", 70000, "X") };
        var r = Keys(narrowed, null, _ => false);
        Assert.Empty(r.Keys);
        Assert.Null(r.ForceFullReason);

        // Whole-object and narrowed objects in one change: only the whole-object one is keyed.
        var mixed = Keys(new[] { new AffectedObjectId("Table", 70001, "T"), new AffectedObjectId("Codeunit", 70000, "X") },
            Recorded(), o => o.Kind == "Table");
        Assert.Contains("tbl|Table|2000000136", mixed.Keys);
        Assert.DoesNotContain("tbl|Table|2000000137", mixed.Keys);
    }

    [Fact]
    public void NoChangedObject_KeysNothing_EvenWithNoRecording()
    {
        var r = Keys(Array.Empty<AffectedObjectId>(), null);
        Assert.Empty(r.Keys);
        Assert.Null(r.ForceFullReason);
    }

    [Fact]
    public void ChangedObject_ForcesAFullRun_WhenThereIsNoRecordingOfWhichTestsReadTheTables()
    {
        var r = Keys(new[] { new AffectedObjectId("Codeunit", 70000, "X") }, null);
        Assert.Contains("Codeunit 70000 changed and the coverage baseline has no record of which tests read the metadata tables",
            r.ForceFullReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedObject_ForcesAFullRun_WhenATableWasHeldOutsideAnyOneTest()
    {
        // A global Record AllObj of a test codeunit: no one test built it, so none can be singled out.
        var r = Keys(new[] { new AffectedObjectId("Codeunit", 70000, "X") }, Recorded("tbl|Table|2000000058"));
        Assert.Contains("a record of metadata table 2000000058 was held outside any one test", r.ForceFullReason, StringComparison.Ordinal);

        // A table of another kind that nothing lists the codeunit in is no reason to run everything.
        var other = Keys(new[] { new AffectedObjectId("Codeunit", 70000, "X") }, Recorded("tbl|Table|2000000136"));
        Assert.Null(other.ForceFullReason);
    }

    // #5076: a dependency's changed permission set selects through the permission tables and is no
    // unattributable kind, so a minor BC bump that changes one still diffs exactly.
    [Theory]
    [InlineData("PermissionSet")]
    [InlineData("PermissionSetExtension")]
    public void DependencyChangedPermissionSet_KeysThePermissionTables_AndIsAttributed(string kind)
    {
        var keys = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId(kind, 9000, "D365 BASIC") },
            new Dictionary<int, List<int>>(), Recorded(), new Dictionary<int, List<int>>());
        Assert.Empty(keys.Unattributed);
        foreach (var t in new[] { 2000000004, 2000000005, 2000000167, 2000000250, 2000000251, 2000000254 })
            Assert.Contains($"tbl|Table|{t}", keys.EventKeys);

        // Held outside any one test: the same refusal as for every other metadata table.
        var held = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId(kind, 9000, "D365 BASIC") },
            new Dictionary<int, List<int>>(), Recorded("tbl|Table|2000000250"), new Dictionary<int, List<int>>());
        Assert.Contains("a record of metadata table 2000000250 was held outside any one test", Assert.Single(held.Unattributed),
            StringComparison.Ordinal);
    }

    // #5452: a dependency's changed profile selects through All Profile and is no unattributable kind,
    // so a minor BC bump that changes one still diffs exactly. A profileextension is still unattributable:
    // All Profile does not read it, so nothing proves that no test depends on one.
    [Fact]
    public void DependencyChangedProfile_KeysAllProfile_AndIsAttributed_ButAProfileExtensionIsNot()
    {
        var keys = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("Profile", null, "BUSINESS MANAGER") },
            new Dictionary<int, List<int>>(), Recorded(), new Dictionary<int, List<int>>());
        Assert.Empty(keys.Unattributed);
        Assert.Contains("tbl|Table|2000000178", keys.EventKeys);

        var held = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("Profile", null, "BUSINESS MANAGER") },
            new Dictionary<int, List<int>>(), Recorded("tbl|Table|2000000178"), new Dictionary<int, List<int>>());
        Assert.Contains("a record of metadata table 2000000178 was held outside any one test", Assert.Single(held.Unattributed),
            StringComparison.Ordinal);

        var extension = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("ProfileExtension", null, "BM EXT") },
            new Dictionary<int, List<int>>(), Recorded(), new Dictionary<int, List<int>>());
        Assert.Equal("ProfileExtension BM EXT changed, and no test recording holds the use of this kind of object (ProfileExtension)",
            Assert.Single(extension.Unattributed));
    }

    // The environment diff keys a dependency's changed object the same way (AffectedEnvironmentDrift.SelectionKeys).
    [Fact]
    public void DependencyChangedObject_KeysTheMetadataTablesThatListIt()
    {
        var keys = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("Codeunit", 80, "Sales-Post") },
            new Dictionary<int, List<int>>(), Recorded(), new Dictionary<int, List<int>>());
        Assert.Empty(keys.Unattributed);
        foreach (var t in new[] { AllObj, AllObjWithCaption, EventSubscription, 2000000137 })
            Assert.Contains($"tbl|Table|{t}", keys.EventKeys);

        var held = AffectedEnvironmentDrift.SelectionKeys(new[] { new AffectedObjectId("Codeunit", 80, "Sales-Post") },
            new Dictionary<int, List<int>>(), Recorded("tbl|Table|2000000038"), new Dictionary<int, List<int>>());
        Assert.Contains("a record of metadata table 2000000038 was held outside any one test", Assert.Single(held.Unattributed),
            StringComparison.Ordinal);
    }

    // ---- the population ----------------------------------------------------------------------

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string PatchesDir => Path.Combine(RepoRoot, "AlRunner", "Patches");

    private static string Dispatch => File.ReadAllText(Path.Combine(PatchesDir, "RecordPatches.DataAccessDispatch.cs"));

    private static string AllPatches => string.Join("\n",
        Directory.EnumerateFiles(PatchesDir, "RecordPatches*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));

    /// <summary>Each branch of <c>GetDataAccessForTableCore</c> that serves a table of its own: the
    /// predicate it dispatches on (without <c>Is</c>) and the ids that predicate compares against.</summary>
    private static Dictionary<string, int[]> DispatchBranches()
    {
        var source = AllPatches;
        // System table ids only: a local const of another meaning (objectTypeTable = 1) may repeat by name.
        var consts = Regex.Matches(source, @"const int (\w+)\s*=\s*(2000000\d{3})\s*;")
            .GroupBy(m => m.Groups[1].Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => int.Parse(m.Groups[2].Value)).Distinct().Single(), StringComparer.Ordinal);
        int[] Ids(string expression)
            => Regex.Matches(expression, @"(?:TableId|tableId)\s*==\s*(\w+)")
                .Select(m => consts[m.Groups[1].Value]).OrderBy(i => i).ToArray();

        var branches = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Dispatch, @"if \(Is(\w+)\(table\)\)"))
        {
            var name = m.Groups[1].Value;
            var definition = Regex.Match(source, @"bool Is" + name + @"\(NCLMetaTable\? table\)\s*=>([^;]+);");
            Assert.True(definition.Success, $"no one-line definition of Is{name}(NCLMetaTable? table) to read its table ids from");
            var body = definition.Groups[1].Value;
            var helper = Regex.Match(body, @"(Is\w+TableId)\(table\.TableId\)");
            if (helper.Success)
            {
                var helperBody = Regex.Match(source, @"bool " + helper.Groups[1].Value + @"\(int tableId\)\s*=>([^;]+);");
                Assert.True(helperBody.Success, $"no definition of {helper.Groups[1].Value}");
                body = helperBody.Groups[1].Value;
            }
            var ids = Ids(body);
            Assert.True(ids.Length > 0, $"Is{name} compares no table id this test can read: {body}");
            branches[name] = ids;
        }

        // The one branch that dispatches on the id itself: `tableId is A or B or C`.
        foreach (Match m in Regex.Matches(Dispatch, @"if \(tableId is ([\w ]+)\)"))
        {
            var names = Regex.Split(m.Groups[1].Value, @"\s+or\s+").Select(n => n.Trim()).ToArray();
            Assert.All(names, n => Assert.True(consts.ContainsKey(n), $"{n} is not a const int"));
            var key = "CodeCoverage";
            Assert.True(names.All(n => n.StartsWith("CodeCoverage", StringComparison.Ordinal)),
                $"an inline `tableId is ...` branch that is not the code coverage tables: {m.Value}; give it its own Entry and teach this test its name");
            branches[key] = names.Select(n => consts[n]).OrderBy(i => i).ToArray();
        }
        return branches;
    }

    [Fact]
    public void Population_ClassifiesEveryTableTheRunnerServesFromABranchOfItsOwn()
    {
        var branches = DispatchBranches();
        Assert.True(branches.Count >= 30, $"read only {branches.Count} dispatch branches, so the scan is not reading the chain");

        var classified = AffectedMetadataTables.Population.Select(e => e.Predicate).ToList();
        Assert.Equal(classified.Count, classified.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(branches.Keys.OrderBy(k => k, StringComparer.Ordinal), classified.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Population_NamesTheTableIdsEachBranchServes()
    {
        var branches = DispatchBranches();
        foreach (var e in AffectedMetadataTables.Population)
            Assert.True(branches[e.Predicate].SequenceEqual(e.TableIds.OrderBy(i => i)),
                $"{e.Predicate}: the source serves [{string.Join(", ", branches[e.Predicate])}], the classification names [{string.Join(", ", e.TableIds)}]");

        // No table is classified twice: a key would name two classifications.
        var all = AffectedMetadataTables.Population.SelectMany(e => e.TableIds).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Population_EveryObjectListingEntryNamesKindsTheChangeModelProducesOrAll()
    {
        // A kind spelled differently from AffectedObjectId.Kind (a RadObjectIdentity SymbolKind name) keys nothing.
        var kinds = new[] { "Table", "TableExtension", "Page", "PageExtension", "Codeunit", "Report", "ReportExtension", "Query", "XmlPort",
            "Enum", "EnumExtension", "Interface", "PermissionSet", "PermissionSetExtension", "Profile" };
        foreach (var e in AffectedMetadataTables.Population.Where(e => e.Kinds != null))
            Assert.All(e.Kinds!, k => Assert.Contains(k, kinds));
        Assert.All(AffectedMetadataTables.Population.Where(e => e.Source == AffectedMetadataTables.Source.UnkeyedKind),
            e => Assert.All(e.Kinds!, k => Assert.NotNull(AffectedEventSelection.UnkeyedKindChange(new[] { new AffectedObjectId(k, 1, "x") }))));
    }
}
