// TableExtensionTargetNamespaceTests: #5289. A source tableextension extends the ONE table its
// `extends` clause resolves to, by namespace; the registries it is stored in stay keyed by name.
//
// The same-name shapes need two tables of one name, which one app cannot declare (AL0197), so the
// tables are a source table in namespace TxA and a dependency's table (no namespace, Usings null) put
// in the parsed-table registry directly. The end-to-end claim against a real service tier is the
// corpus's (tableextensiontrigger/TestTableExtTargetNamespace_Tests.al).
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;
using static AlRunner.Tests.TableExtensionTargetNamespaceTests;

namespace AlRunner.Tests;

// BcEngineCollection is serial for both statics this class writes, the parse registries
// (ResetForReload) and AlObjectMetadataRegistry, and a class carries one [Collection].
[Collection(BcEngineCollection.Name)]
public sealed class TableExtensionTargetNamespaceTests : IDisposable
{
    private static readonly Type RP = typeof(RecordPatches);

    // Process-wide unique among AlRunner.Tests statics: this file owns 881290xx and 881291xx.
    internal const int TableA = 88129001;        // "TX Shared" in namespace TxA, parsed from source
    internal const int TableDep = 88129002;      // "TX Shared" read from a dependency's symbols
    internal const int TableUnique = 88129003;   // "TX Unique" in namespace TxA
    internal const int ExtQualA = 88129011;      // extends TxA."TX Shared"
    internal const int ExtQualDep = 88129012;    // extends TxDep."TX Shared", which no source table declares
    private const int ExtOwn = 88129013;        // namespace TxA, extends "TX Shared"
    private const int ExtElsewhere = 88129014;  // namespace TxElsewhere, extends "TX Shared"
    private const int ExtImporter = 88129015;   // namespace TxImp with `using TxA`, extends "TX Shared"
    private const int ExtUnique = 88129016;     // extends TxA."TX Unique"
    private const int ExtUnscoped = 88129019;   // merged with no target, as a precompiled extension is
    private const int SharedFieldId = 88129101; // declared by ExtQualA and by ExtQualDep, on different tables

    private readonly string _root = TestScratch.Dir("al-runner-5289-tests");

    public TableExtensionTargetNamespaceTests()
    {
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Parse(string method, string source, string? filePath = null) =>
        RP.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.InvokeStatic(source, filePath);

    internal static IDictionary ParsedTables() =>
        (IDictionary)RP.GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    internal static ParsedTable Table(int id) => (ParsedTable)ParsedTables()[id]!;

    private static void ParseSourceTable(string ns, int id, string name, string? filePath = null) =>
        Parse("TryParseTableFile", $$"""
            namespace {{ns}};

            table {{id}} "{{name}}"
            {
                fields { field(1; Code; Code[10]) { } }
                keys { key(PK; Code) { Clustered = true; } }
            }
            """, filePath);

    // The dependency's table: what a symbol read produces, which is what carries no namespace.
    private static void AddDependencyTable(int id, string name) =>
        ParsedTables()[id] = new ParsedTable(id, name,
            new List<ParsedField> { new(1, "Code", "Code[10]", 10) }, new List<int> { 1 });

    private static void ParseExtension(string? ns, string? usingNs, int id, string extendsClause,
        string body, string? filePath = null) =>
        Parse("TryParseTableExtensionFile",
            (ns is null ? "" : $"namespace {ns};\n") + (usingNs is null ? "" : $"using {usingNs};\n")
            + $"\ntableextension {id} \"TX Ext {id}\" extends {extendsClause}\n{{\n{body}\n}}", filePath);

    internal static void ParseTheShape(string? filePath = null)
    {
        ParseSourceTable("TxA", TableA, "TX Shared", filePath);
        AddDependencyTable(TableDep, "TX Shared");
        ParseSourceTable("TxA", TableUnique, "TX Unique", filePath);
        ParseExtension(null, null, ExtQualA, "TxA.\"TX Shared\"", $$"""
            fields { field({{SharedFieldId}}; "TX Qual A"; Integer) { MinValue = 1; MaxValue = 5; } }
            keys { key(KQA; "TX Qual A") { } }
            """, filePath);
        ParseExtension(null, null, ExtQualDep, "TxDep.\"TX Shared\"", $$"""
            fields
            {
                field({{SharedFieldId}}; "TX Qual Dep"; Integer) { MinValue = 10; MaxValue = 50; }
                modify(Code) { Caption = 'TX Dep Code'; }
            }
            keys { key(KQD; "TX Qual Dep") { } }
            """, filePath);
        ParseExtension("TxA", null, ExtOwn, "\"TX Shared\"", """
            fields { field(88129102; "TX Own"; Integer) { } }
            """, filePath);
        ParseExtension("TxElsewhere", null, ExtElsewhere, "\"TX Shared\"", """
            fields { field(88129103; "TX Elsewhere"; Integer) { } }
            """, filePath);
        ParseExtension("TxImp", "TxA", ExtImporter, "\"TX Shared\"", """
            fields { field(88129104; "TX Imported"; Integer) { } }
            """, filePath);
        ParseExtension(null, null, ExtUnique, "TxA.\"TX Unique\"", """
            fields { field(88129105; "TX Unique Added"; Integer) { } }
            """, filePath);
    }

    private static int[] Sorted(IEnumerable<int> ids) => ids.OrderBy(i => i).ToArray();

    // Positive and negative at once: each extension is registered for the table its clause resolves
    // to and for no other. A qualified clause picks the namespace; a bare one picks the file's own
    // namespace, then what it imports, and otherwise means the dependency's table.
    [Fact]
    public void ExtensionIdsForTable_AttachesEachExtensionToTheTableItsClauseResolvesTo()
    {
        ParseTheShape();

        Assert.Equal(new[] { ExtQualA, ExtOwn, ExtImporter }, Sorted(RecordPatches.ExtensionIdsForTable(Table(TableA))));
        Assert.Equal(new[] { ExtQualDep, ExtElsewhere }, Sorted(RecordPatches.ExtensionIdsForTable(Table(TableDep))));
        // Positive control: the table whose name is unique keeps its extension.
        Assert.Equal(new[] { ExtUnique }, Sorted(RecordPatches.ExtensionIdsForTable(Table(TableUnique))));
    }

    // The fields an extension adds belong to its table, and a field id two extensions of different
    // tables both declare survives on both: the name-pooled list de-duplicates by id.
    [Fact]
    public void ExtensionFieldsFor_KeepsEachTablesOwnFieldsAndASharedFieldId()
    {
        ParseTheShape();

        var onA = RecordPatches.ExtensionFieldsFor(Table(TableA)).ToDictionary(f => f.FieldId);
        var onDep = RecordPatches.ExtensionFieldsFor(Table(TableDep)).ToDictionary(f => f.FieldId);

        Assert.Equal(new[] { SharedFieldId, 88129102, 88129104 }, Sorted(onA.Keys));
        Assert.Equal("TX Qual A", onA[SharedFieldId].FieldName);
        Assert.Equal(new[] { SharedFieldId, 88129103 }, Sorted(onDep.Keys));
        Assert.Equal("TX Qual Dep", onDep[SharedFieldId].FieldName);
        Assert.Equal(new[] { 88129105 }, RecordPatches.ExtensionFieldsFor(Table(TableUnique)).Select(f => f.FieldId).ToArray());
    }

    [Fact]
    public void ExtensionKeysFor_ReturnsOnlyTheKeysOfTheTablesOwnExtensions()
    {
        ParseTheShape();

        Assert.Equal(new[] { "KQA" }, RecordPatches.ExtensionKeysFor(Table(TableA)).Select(k => k.Name).ToArray());
        Assert.Equal(new[] { "KQD" }, RecordPatches.ExtensionKeysFor(Table(TableDep)).Select(k => k.Name).ToArray());
    }

    // GetAllFieldsIncludingExtensions feeds the page-control and virtual-table readers.
    [Fact]
    public void GetAllFieldsIncludingExtensions_AddsOnlyTheTablesOwnExtensionFields()
    {
        ParseTheShape();

        Assert.Equal(new[] { 1, SharedFieldId, 88129102, 88129104 },
            Sorted(RecordPatches.GetAllFieldsIncludingExtensions(Table(TableA)).Select(f => f.FieldId)));
        Assert.Equal(new[] { 1, SharedFieldId, 88129103 },
            Sorted(RecordPatches.GetAllFieldsIncludingExtensions(Table(TableDep)).Select(f => f.FieldId)));
    }

    // TryGetParsedFieldMinMax reads the extension's field by id: the same id on two tables answers
    // each table's own bounds.
    [Fact]
    public void TryGetParsedFieldMinMax_AnswersTheBoundsOfTheTablesOwnExtensionField()
    {
        ParseTheShape();

        Assert.Equal(("1", "5"), RecordPatches.TryGetParsedFieldMinMax(TableA, SharedFieldId));
        Assert.Equal(("10", "50"), RecordPatches.TryGetParsedFieldMinMax(TableDep, SharedFieldId));
    }

    // A modify(...) on another table's extension must not push this table off BC's own document.
    [Fact]
    public void ShouldBuildTableFromBcDocument_IsNotDecidedByAnotherTablesExtension()
    {
        var appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "app.json"),
            """{ "id": "5289aaaa-0000-4000-8000-000000000001", "name": "TX", "publisher": "x", "version": "1.0.0.0" }""");
        var file = Path.Combine(appDir, "t.al");
        ParseTheShape(file);
        // Only the extension of the dependency's table declares modify(...), and it is in this app.
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableMetadataKind, TableA, "TX Shared", "<MetaTable />");
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableMetadataKind, TableDep, "TX Shared", "<MetaTable />");

        Assert.True(RecordPatches.ShouldBuildTableFromBcDocument(TableA, Table(TableA)),
            "A's extensions add fields in its own app: BC's document carries them");
        Assert.False(RecordPatches.ShouldBuildTableFromBcDocument(TableDep, Table(TableDep)),
            "the dependency table's extension declares modify(...), which BC's document does not carry");
    }

    // Event publishers resolve an extension to its base table through ExtensionBaseObjectIds.
    [Fact]
    public void ExtensionBaseObjectIds_ResolvesAnExtensionToItsOwnTable()
    {
        ParseTheShape();

        Assert.Equal(new[] { TableA }, RecordPatches.ExtensionBaseObjectIds("TableExtension", ExtQualA));
        Assert.Equal(new[] { TableDep }, RecordPatches.ExtensionBaseObjectIds("TableExtension", ExtQualDep));
        Assert.Equal(new[] { TableDep }, RecordPatches.ExtensionBaseObjectIds("TableExtension", ExtElsewhere));
        Assert.Equal(new[] { TableA }, RecordPatches.ExtensionBaseObjectIds("TableExtension", ExtImporter));
    }

    [Fact]
    public void TableExtensionBaseTableIds_ResolvesAnExtensionToItsOwnTable()
    {
        ParseTheShape();

        var byExtension = RecordPatches.TableExtensionBaseTableIds();
        Assert.Equal(new[] { TableA }, byExtension[ExtQualA]);
        Assert.Equal(new[] { TableDep }, byExtension[ExtQualDep]);
        Assert.Equal(new[] { TableDep }, byExtension[ExtElsewhere]);
    }

    [Fact]
    public void ExtensionIdsOfBaseObject_ListsTheExtensionsOfAParsedTable()
    {
        ParseTheShape();

        Assert.Equal(new[] { ExtQualA, ExtOwn, ExtImporter }, Sorted(RecordPatches.ExtensionIdsOfBaseObject("Table", TableA)));
        Assert.Equal(new[] { ExtQualDep, ExtElsewhere }, Sorted(RecordPatches.ExtensionIdsOfBaseObject("Table", TableDep)));
    }

    // The same question for a dependency table nothing has faulted into the parsed registry yet: its
    // id is known to the symbol index only, and it carries no namespace.
    [Fact]
    public void ExtensionIdsOfBaseObject_ForAnUnparsedDependencyTable_ExcludesTheExtensionsOfASourceTable()
    {
        ParseSourceTable("TxA", TableA, "TX Shared");
        ParseExtension(null, null, ExtQualA, "TxA.\"TX Shared\"", "");
        ParseExtension("TxElsewhere", null, ExtElsewhere, "\"TX Shared\"", "");
        var json = $$"""
            { "RuntimeVersion": "15.1", "Namespaces": [ { "Name": "TxDep", "Tables": [
              { "Id": {{TableDep}}, "Name": "TX Shared",
                "Fields": [ { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 1, "Name": "Code" } ],
                "Keys": [ { "Name": "PK", "FieldNames": [ "Code" ] } ] } ] } ] }
            """;
        var appPath = Path.Combine(_root, "dep.app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
            w.Write(json);
        RecordPatches.AddBcAppPath(appPath);

        Assert.False(ParsedTables().Contains(TableDep), "the dependency table must not be parsed yet");
        Assert.Equal(new[] { ExtElsewhere }, RecordPatches.ExtensionIdsOfBaseObject("Table", TableDep));
        Assert.Equal(new[] { ExtQualA }, RecordPatches.ExtensionIdsOfBaseObject("Table", TableA));
    }

    // An extension parsed BEFORE the source table it extends reads as extending the dependency's
    // table until that source table is parsed; the answer must move when it is.
    [Fact]
    public void ExtensionParsedBeforeItsSourceTable_StopsExtendingTheDependencyTableOnceTheSourceTableIsParsed()
    {
        AddDependencyTable(TableDep, "TX Shared");
        ParseExtension("TxA", null, ExtOwn, "\"TX Shared\"", "");
        Assert.Equal(new[] { ExtOwn }, RecordPatches.ExtensionIdsForTable(Table(TableDep)));

        ParseSourceTable("TxA", TableA, "TX Shared");

        Assert.Empty(RecordPatches.ExtensionIdsForTable(Table(TableDep)));
        Assert.Equal(new[] { ExtOwn }, RecordPatches.ExtensionIdsForTable(Table(TableA)));
    }

    // Control: an extension with no recorded target, which is how a precompiled dependency's
    // extension reaches the registry, still matches every table of its name.
    [Fact]
    public void AnExtensionWithNoTarget_StillMatchesEveryTableOfItsName()
    {
        ParseSourceTable("TxA", TableA, "TX Shared");
        AddDependencyTable(TableDep, "TX Shared");
        RP.GetMethod("MergeExtensionFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .InvokeStatic("TX Shared", ExtUnscoped, new List<ParsedField>());

        Assert.Equal(new[] { ExtUnscoped }, RecordPatches.ExtensionIdsForTable(Table(TableA)));
        Assert.Equal(new[] { ExtUnscoped }, RecordPatches.ExtensionIdsForTable(Table(TableDep)));
    }

    // A target must not outlive the bundle that recorded it: the next bundle may register the same
    // extension id with no target.
    [Fact]
    public void ResetForReload_ForgetsTheTargetsTheLastBundleRecorded()
    {
        ParseTheShape();
        Assert.Equal(new[] { ExtQualA, ExtOwn, ExtImporter }, Sorted(RecordPatches.ExtensionIdsForTable(Table(TableA))));

        RecordPatches.ResetForReload();
        ParseSourceTable("TxA", TableA, "TX Shared");
        AddDependencyTable(TableDep, "TX Shared");
        RP.GetMethod("MergeExtensionFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .InvokeStatic("TX Shared", ExtQualA, new List<ParsedField>());

        Assert.Equal(new[] { ExtQualA }, RecordPatches.ExtensionIdsForTable(Table(TableDep)));
    }
}

// The in-process engine: BC's own multi-language parser unwraps a delta caption, and a built
// NCLMetaTable is what a late-parsed table has to evict.
[Collection(BcEngineCollection.Name)]
public sealed class TableExtensionTargetEngineTests : IDisposable
{
    private readonly BcEngineFixture _engine;

    public TableExtensionTargetEngineTests(BcEngineFixture engine)
    {
        _engine = engine;
        RecordPatches.ResetForReload();
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
    }

    // The modify(...) deltas are read per extension and applied by field id, so another table's
    // delta on field 1 must not caption this table's field 1.
    [SkippableFact]
    public void ApplyTableExtensionFieldDeltas_AppliesOnlyTheDeltasOfTheTablesOwnExtensions()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        ParseTheShape();
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableExtensionMetadataKind, ExtQualA, "TX Ext A",
            "<MetadataRuntimeDeltas><FieldChange TargetType=\"MetaField\" TargetID=\"1\" CaptionML=\"ENU=TX A Code\" /></MetadataRuntimeDeltas>");
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableExtensionMetadataKind, ExtQualDep, "TX Ext Dep",
            "<MetadataRuntimeDeltas><FieldChange TargetType=\"MetaField\" TargetID=\"1\" CaptionML=\"ENU=TX Dep Code\" /></MetadataRuntimeDeltas>");

        IReadOnlyList<ParsedField> Apply(int id) => RecordPatches.ApplyTableExtensionFieldDeltas(Table(id), Table(id).Fields);
        var onA = Apply(TableA);
        var onDep = Apply(TableDep);
        var onUnique = Apply(TableUnique);

        Assert.Equal("TX A Code", onA.Single(f => f.FieldId == 1).Caption);
        Assert.Equal("TX Dep Code", onDep.Single(f => f.FieldId == 1).Caption);
        Assert.Null(onUnique.Single(f => f.FieldId == 1).Caption);
    }

    // The metatable of a table whose extension was attached before the extension's own source table
    // was parsed is stale once that table appears: it must be rebuilt without the extension's field.
    [SkippableFact]
    public void ParsingTheSourceTableLater_RebuildsTheDependencyMetaTableWithoutTheExtensionField()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        const int depTableId = 88129202;
        const int extFieldId = 88129201;
        var root = TestScratch.Dir("al-runner-5289-engine");
        Directory.CreateDirectory(root);
        try
        {
            var json = $$"""
                { "RuntimeVersion": "15.1", "Namespaces": [ { "Name": "TxDep", "Tables": [
                  { "Id": {{depTableId}}, "Name": "TX Late",
                    "Fields": [ { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 1, "Name": "Code" } ],
                    "Keys": [ { "Name": "PK", "FieldNames": [ "Code" ] } ] } ] } ] }
                """;
            var appPath = Path.Combine(root, "dep.app");
            using (var fs = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(json);
            RecordPatches.AddBcAppPath(appPath);
            var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
            Assert.NotNull(skeleton);

            // The extension is parsed first and names a table no source declares yet: the
            // dependency's table is the only candidate, so it carries the extension's field.
            Parse("TryParseTableExtensionFile", $$"""
                namespace TxLate;

                tableextension 88129203 "TX Late Ext" extends "TX Late"
                {
                    fields { field({{extFieldId}}; "TX Late Field"; Integer) { } }
                }
                """);
            var before = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, depTableId, false, 0);
            Assert.Equal(extFieldId, RecordPatches.NCLMetaTable_GetFieldByNoExt(before, 88129203, extFieldId).FieldNo);

            // The extension's own namespace now declares a table of that name: it is the one the bare
            // clause names, so the dependency's table is not extended.
            Parse("TryParseTableFile", """
                namespace TxLate;

                table 88129204 "TX Late"
                {
                    fields { field(1; Code; Code[10]) { } }
                    keys { key(PK; Code) { Clustered = true; } }
                }
                """);
            var after = RecordPatches.EnsureTableInMetadataCache(depTableId)!;

            Assert.Throws<InvalidOperationException>(() =>
                RecordPatches.NCLMetaTable_GetFieldByNoExt(after, 88129203, extFieldId));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    private static void Parse(string method, string source) =>
        typeof(RecordPatches).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.InvokeStatic(source, null);
}
