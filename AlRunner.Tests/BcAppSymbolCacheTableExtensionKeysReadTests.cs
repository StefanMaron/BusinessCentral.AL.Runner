// BcAppSymbolCacheTableExtensionKeysReadTests — issue #3216, the precompiled half.
//
// What this pins, and what it deliberately does not
// -------------------------------------------------
// "A key declared by a tableextension is part of the extended table's key list" is a claim
// about Business Central, and it belongs upstream where a real service tier adjudicates it.
// It already lives there: `TableExt_Key_ExtensionKeys_AreListedAmongTheExtendedTablesKeys` in
// the al-language corpus (codeunit 60331, tableextension/TestTableExtKeysAndInitValue.al) is
// what found this gap in the first place, and it is what proves the fix RED -> GREEN.
// Nothing here restates that claim (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// What IS runner-specific, and has no other home, is the READ: BcAppSymbolCache's parse of a
// precompiled `.app`'s SymbolReference.json. A dependency's tableextension states its keys as
//
//     "Keys": [ { "Name": "Key12", "FieldNames": [ "Service Item Group" ] } ]
//
// — measured on Base Application 28.1, where 6 of the 90 tableextensions declare any keys at
// all — and before #3216 nothing read that property at all. TableExtensionSymbol carried
// Fields and no Keys, so the merge site had nothing to pass on even after the metatable
// builder learned to consume extension keys. The JSON shape, the ORDER of the field names
// inside a key (a key's field order IS its sort order), and "no Keys declared" answering an
// empty list rather than null are all properties of this parser, not of BC.

using Xunit;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class BcAppSymbolCacheTableExtensionKeysReadTests : IDisposable
{
    private readonly string _scratch;

    public BcAppSymbolCacheTableExtensionKeysReadTests()
    {
        _scratch = TestScratch.Dir("al-runner-bcappsymbol-tableext-keys-read");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The defect, in the precompiled direction: both declared keys must come back, each with
    /// the field names it was declared with, in the order it declared them.
    /// <para>The two-field key mixes a field the EXTENDED table owns ("Name", on Customer)
    /// with one the extension adds ("Ext Rank"), and it is asserted field-by-field by index.
    /// A parser that returned the names as a set, sorted them, or resolved only the names it
    /// could see inside the extension would pass a count-only assertion and fail here — and a
    /// key whose field order is wrong is a wrong sort order, silently.</para>
    /// </summary>
    [SkippableFact]
    public void GetTableExtensions_ReadsEveryDeclaredKey_WithItsFieldNamesInDeclaredOrder()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteExtensionApp("two-keys", extensionId: 70101, keys: new[]
        {
            ("ExtRank",  new[] { "Ext Rank" }),
            ("ExtMixed", new[] { "Name", "Ext Rank" }),
        });

        var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(app));
        Assert.Equal(70101, ext.ExtensionId);
        Assert.Equal("Customer", ext.TargetTableName);

        Assert.NotNull(ext.Keys);
        Assert.Equal(2, ext.Keys!.Count);

        Assert.Equal("ExtRank", ext.Keys[0].Name);
        Assert.Equal(new[] { "Ext Rank" }, ext.Keys[0].FieldNames);

        Assert.Equal("ExtMixed", ext.Keys[1].Name);
        Assert.Equal(2, ext.Keys[1].FieldNames.Count);
        Assert.Equal("Name", ext.Keys[1].FieldNames[0]);
        Assert.Equal("Ext Rank", ext.Keys[1].FieldNames[1]);
    }

    /// <summary>
    /// The negative direction, and the reason the merge cannot just trust the array: a "Keys"
    /// entry naming no fields is dropped, not surfaced as a zero-field key. A zero-field
    /// MetaKey reaches BC's MetaKey ctor with an empty fieldRelations array and answers
    /// KeyRef.FieldCount() = 0, which no AL key can be.
    /// <para>The well-formed key alongside it must still arrive, so this cannot pass by the
    /// parser having given up on the whole array.</para>
    /// </summary>
    [SkippableFact]
    public void GetTableExtensions_DropsAKeyThatNamesNoFields_AndKeepsTheWellFormedOneBesideIt()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteExtensionApp("empty-key", extensionId: 70102, keys: new[]
        {
            ("Hollow",  Array.Empty<string>()),
            ("ExtRank", new[] { "Ext Rank" }),
        });

        var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(app));
        Assert.NotNull(ext.Keys);

        var kept = Assert.Single(ext.Keys!);
        Assert.Equal("ExtRank", kept.Name);
        Assert.Equal(new[] { "Ext Rank" }, kept.FieldNames);
        Assert.DoesNotContain(ext.Keys!, k => string.Equals(k.Name, "Hollow", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The overwhelmingly common precompiled shape — 84 of Base Application 28.1's 90
    /// tableextensions declare no keys — must answer an EMPTY list, never null. The merge site
    /// in RecordPatches.BcAppFallback passes Keys straight through to MergeExtensionFields, so
    /// "declared none" and "not read" must not be the same value there.
    /// </summary>
    [SkippableFact]
    public void GetTableExtensions_OnAnExtensionDeclaringNoKeys_AnswersAnEmptyListNotNull()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteExtensionApp("no-keys", extensionId: 70103, keys: Array.Empty<(string, string[])>());

        var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(app));
        Assert.Single(ext.Fields);          // the extension itself was read, so this is not a vacuous pass
        Assert.NotNull(ext.Keys);
        Assert.Empty(ext.Keys!);
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A registrable `.app` whose SymbolReference.json declares one tableextension on
    /// "Customer" adding "Ext Rank" (Integer) plus <paramref name="keys"/>. Each arm writes to
    /// its OWN directory: ComputeAppContentHash memoizes per full path for the process, so two
    /// arms sharing a path would share a parse.
    /// </summary>
    private string WriteExtensionApp(string subdir, int extensionId, (string Name, string[] FieldNames)[] keys)
    {
        var bundleDir = Path.Combine(_scratch, subdir, "src");
        var appPath = Path.Combine(_scratch, subdir, $"AL Runner_TableExtKeys {extensionId}_1.0.0.0.app");
        var appId = new Guid($"3216d00{extensionId % 10}-1111-4222-8333-4444555566{extensionId % 100:D2}");
        const int fieldId = 50101;

        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "TableExtKeys {{extensionId}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{extensionId}}, "to": {{extensionId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Ext.al"), $$"""
        tableextension {{extensionId}} "TableExtKeys {{extensionId}}" extends Customer
        {
            fields
            {
                field({{fieldId}}; "Ext Rank"; Integer) { DataClassification = CustomerContent; }
            }
        }
        """);

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            AppId = appId.ToString(),
            Name = $"TableExtKeys {extensionId}",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = Array.Empty<object>(),
            TableExtensions = new[]
            {
                new
                {
                    Id = extensionId,
                    Name = $"TableExtKeys {extensionId}",
                    TargetObject = "Customer",
                    Fields = new object[]
                    {
                        new { Id = fieldId, Name = "Ext Rank", TypeDefinition = new { Name = "Integer" } },
                    },
                    Keys = keys.Select(k => new { Name = k.Name, FieldNames = k.FieldNames }).ToArray(),
                },
            },
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            EnumTypes = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
        });

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundleDir, "app.json"))
            ?? throw new InvalidOperationException("could not read the identity just written");
        Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
        InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity, appPath, System.Text.Encoding.UTF8.GetBytes(json));
        return appPath;
    }
}
