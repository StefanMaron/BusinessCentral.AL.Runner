// BcAppSymbolCacheKeyPropertiesReadTests — issue #3568, the key cluster's read half.
//
// What this pins, and what it deliberately does not
// -------------------------------------------------
// "A key's Clustered / Unique / SumIndexFields are what AL declared" is a claim about
// Business Central, and the metadata-equivalence harness already adjudicates it against BC's
// OWN emitter output for all 150 tables of Business Foundation + System Application
// (MetadataEquivalenceHarnessTests.The_reader_states_every_key_property_the_symbol_file_carries).
// Nothing here restates that.
//
// What IS runner-specific, and what the harness cannot reach, is the JSON PARSE: the exact
// spelling BcAppSymbolCache must read out of a precompiled .app's SymbolReference.json. The
// harness only ever sees Microsoft's own two apps at one BC build, so the spellings those
// happen not to contain are unmeasured there and pinned here:
//
//   * booleans arrive as "1"/"0" — measured across Business Foundation + System Application
//     28.1.49838.53910, where 134 Clustered and 3 Unique properties are all stated that way;
//   * SumIndexFields arrives as a "Field11,Field12" list of field IDS, not of names, which is
//     the opposite of the AL SOURCE form of the same property (field names) that
//     RecordPatches.AlSourceParser reads;
//   * a key that states no property at all must answer ABSENT rather than false, because the
//     builder distinguishes the two: a declared key stating nothing is NOT clustered, while a
//     table declaring no key at all gets a synthesized clustered one.
//
// The last of those is the one with no natural witness in Microsoft's apps and the one a
// future reader is most likely to get wrong.

using Xunit;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class BcAppSymbolCacheKeyPropertiesReadTests : IDisposable
{
    private readonly string _scratch;

    public BcAppSymbolCacheKeyPropertiesReadTests()
    {
        _scratch = TestScratch.Dir("al-runner-bcappsymbol-key-properties");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The positive direction, across all three properties and both key positions. The primary
    /// key carries its declared name and Clustered = "1"; the secondary key carries Unique =
    /// "1" and a SumIndexFields list of two field IDS, which must resolve to those two ids in
    /// the order declared.
    /// <para>Every assertion is a concrete value, and the SIFT ids are asserted by index: a
    /// parser that returned them as a set, sorted them, or dropped the "Field" prefix wrongly
    /// would pass a count-only check and fail here.</para>
    /// </summary>
    [SkippableFact]
    public void GetTables_ReadsClusteredUniqueAndSumIndexFields_FromTheSymbolFilesOwnSpelling()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteTableApp("declared", tableId: 70201, keys: new object[]
        {
            new
            {
                Name = "PrimaryKey",
                FieldNames = new[] { "Code" },
                Properties = new[] { new { Name = "Clustered", Value = "1" } },
            },
            new
            {
                Name = "BySize",
                FieldNames = new[] { "Description" },
                Properties = new[]
                {
                    new { Name = "Unique", Value = "1" },
                    new { Name = "SumIndexFields", Value = "Field30,Field20" },
                },
            },
        });

        var table = Assert.Single(BcAppSymbolCache.Get(app).Tables, t => t.TableId == 70201);

        // Primary key: the declared NAME (not "PK") and the declared Clustered.
        Assert.NotNull(table.PrimaryKey);
        Assert.Equal("PrimaryKey", table.PrimaryKey!.Name);
        Assert.Equal(true, table.PrimaryKey.Clustered);
        Assert.False(table.PrimaryKey.Unique);
        Assert.Null(table.PrimaryKey.SumIndexFieldIds);

        // Secondary key: Unique, and SumIndexFields resolved to ids in the DECLARED order.
        var secondary = Assert.Single(table.SecondaryKeys!);
        Assert.Equal("BySize", secondary.Name);
        Assert.True(secondary.Unique);
        Assert.NotNull(secondary.SumIndexFieldIds);
        Assert.Equal(new[] { 30, 20 }, secondary.SumIndexFieldIds!);
    }

    /// <summary>
    /// The distinction the builder depends on: a key that DECLARES no Clustered answers null,
    /// never false. Both are legitimate states and they produce different metadata — a declared
    /// key stating nothing is not clustered, while a table declaring no key at all is given a
    /// synthesized clustered one. Collapsing them to false would make the two indistinguishable
    /// at the one place that has to tell them apart.
    /// <para>Measured against BC's own emitter over 150 tables: 11 primary keys declare no
    /// Clustered and BC answers false for every one, while 6 tables declare no key at all and
    /// BC answers true for every one.</para>
    /// </summary>
    [SkippableFact]
    public void GetTables_OnAKeyDeclaringNoProperties_AnswersAbsentRatherThanFalse()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteTableApp("bare", tableId: 70202, keys: new object[]
        {
            new { Name = "Key1", FieldNames = new[] { "Code" } },
        });

        var table = Assert.Single(BcAppSymbolCache.Get(app).Tables, t => t.TableId == 70202);

        Assert.NotNull(table.PrimaryKey);
        Assert.Equal("Key1", table.PrimaryKey!.Name);
        Assert.Null(table.PrimaryKey.Clustered);   // absent, NOT false
        Assert.False(table.PrimaryKey.Unique);     // absent reads as not-unique, which BC agrees with
        Assert.Null(table.PrimaryKey.SumIndexFieldIds);
    }

    /// <summary>
    /// A table whose symbol entry carries no "Keys" at all answers a null PrimaryKey — which is
    /// how the builder knows to synthesize one over the first field and clusters it. Six of
    /// System Application 28.1's tables are written exactly this way, and BC's emitter names
    /// their synthesized key after that first field ("ID", "IgnoreCase", "ApiVersion").
    /// <para>The fields must still have been read, so this cannot pass by the parser having
    /// given up on the whole table.</para>
    /// </summary>
    [SkippableFact]
    public void GetTables_OnATableDeclaringNoKeysAtAll_AnswersANullPrimaryKey()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteTableApp("no-keys", tableId: 70203, keys: Array.Empty<object>());

        var table = Assert.Single(BcAppSymbolCache.Get(app).Tables, t => t.TableId == 70203);

        Assert.Null(table.PrimaryKey);
        Assert.Equal(3, table.Fields.Count);        // the table itself was read
        Assert.Equal(new[] { 10 }, table.PkFieldIds); // first-field fallback still applies
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A registrable `.app` whose SymbolReference.json declares one table with three fields —
    /// Code (10), Description (20), Amount (30) — and <paramref name="keys"/> verbatim, so a
    /// test states the JSON shape it is pinning rather than a helper's idea of it.
    /// <para>Each arm writes to its OWN directory: ComputeAppContentHash memoizes per full path
    /// for the process, so two arms sharing a path would share a parse.</para>
    /// </summary>
    private string WriteTableApp(string subdir, int tableId, object[] keys)
    {
        var bundleDir = Path.Combine(_scratch, subdir, "src");
        var appPath = Path.Combine(_scratch, subdir, $"AL Runner_KeyProps {tableId}_1.0.0.0.app");
        var appId = new Guid($"3568d00{tableId % 10}-1111-4222-8333-4444555566{tableId % 100:D2}");

        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "KeyProps {{tableId}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{tableId}}, "to": {{tableId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "T.al"), $$"""
        table {{tableId}} "KeyProps {{tableId}}"
        {
            fields
            {
                field(10; "Code"; Code[20]) { DataClassification = CustomerContent; }
                field(20; "Description"; Text[50]) { DataClassification = CustomerContent; }
                field(30; "Amount"; Decimal) { DataClassification = CustomerContent; }
            }
        }
        """);

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            AppId = appId.ToString(),
            Name = $"KeyProps {tableId}",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = new object[]
            {
                new
                {
                    Id = tableId,
                    Name = $"KeyProps {tableId}",
                    Fields = new object[]
                    {
                        new { Id = 10, Name = "Code", TypeDefinition = new { Name = "Code" } },
                        new { Id = 20, Name = "Description", TypeDefinition = new { Name = "Text" } },
                        new { Id = 30, Name = "Amount", TypeDefinition = new { Name = "Decimal" } },
                    },
                    Keys = keys,
                },
            },
            TableExtensions = Array.Empty<object>(),
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
