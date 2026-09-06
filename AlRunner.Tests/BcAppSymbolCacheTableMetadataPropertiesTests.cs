// BcAppSymbolCacheTableMetadataPropertiesTests — proves BcAppSymbolCache reads a table's
// DataClassification, ExternalName, TableType and DataPerCompany out of a dependency's
// SymbolReference.json, for the "Table Metadata" (2000000136) virtual table (issue #2938).
//
// Why this is a RUNNER-side test and not a corpus one
// ---------------------------------------------------
// The BC-behaviour half of #2938 — "Table Metadata's columns are computed from the table's own
// AL declaration" — is already pinned upstream and validated against a real service tier by
// StefanMaron/BusinessCentral.AL.Language.Tests#173 (codeunit 60801,
// record/TestTableMetadataVirtualTable.al), which is in the corpus at the pin this repo
// currently carries. Nothing here re-states that claim.
//
// What it pins instead is the runner's own PRECOMPILED-DEPENDENCY route, which no corpus test
// can reach: the corpus's fixture tables are all source-compiled, so they exercise
// RecordPatches.AlSourceParser.cs and never this parser. For a table living in an R2R .app
// there is no other route — the package ships no metadata XML — so if this parser drops a
// property, every Base Application table silently reports the column's default and the corpus
// stays green while being wrong.
//
// The shapes below are captured from Base Application 28.1.49838.53910's own
// SymbolReference.json, not invented. Of its 1523 tables: 1510 state DataClassification (1447
// CustomerContent, 61 SystemMetadata, 2 OrganizationIdentifiableInformation), 149 state
// TableType (88 Temporary, 56 CRM, 4 Exchange, 1 MicrosoftGraph), 61 state ExternalName, and
// 41 state DataPerCompany — always as the string "0", which is the spelling the AL false takes
// in a symbol file. "CDS BC Table Relation" is a real table there and really does carry
// TableType = CRM with ExternalName = dyn365bc_syntheticrelation.
//
// The .app shape (a plain zip holding SymbolReference.json) mirrors
// BcAppSymbolCachePageMetadataTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheTableMetadataPropertiesTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // Three tables, chosen so every property below is asserted at TWO different values across
    // the set. A parser answering any of them with a constant fails at least one assertion:
    //
    //   TableType           CRM (60901) / Temporary (60902) / undeclared (60903)
    //   DataClassification  SystemMetadata (60901) / CustomerContent (60902) / none (60903)
    //   ExternalName        declared (60901) / undeclared (60902, 60903)
    //   DataPerCompany      declared "0" (60902) / undeclared (60901, 60903)
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 60901,
              "Name": "Sym CRM Entity",
              "Properties": [
                { "Name": "TableType", "Value": "CRM" },
                { "Name": "ExternalName", "Value": "dyn365bc_syntheticrelation" },
                { "Name": "DataClassification", "Value": "SystemMetadata" }
              ],
              "Fields": [
                { "Id": 1, "Name": "EntityId", "TypeDefinition": { "Name": "Guid" } }
              ]
            },
            {
              "Id": 60902,
              "Name": "Sym Global Temp",
              "Properties": [
                { "Name": "TableType", "Value": "Temporary" },
                { "Name": "DataPerCompany", "Value": "0" },
                { "Name": "DataClassification", "Value": "CustomerContent" }
              ],
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 20 } }
              ]
            },
            {
              "Id": 60903,
              "Name": "Sym Bare Table",
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 20 } }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Tables_DeclaredProperties_AreReadFromTheSymbolFile()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tablemeta-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var tables = BcAppSymbolCache.Get(WriteApp(dir, SymbolReference)).Tables;

            var crm = Assert.Single(tables, t => t.TableId == 60901);
            // TableTypeName carries the member name VERBATIM. It is resolved to the Table
            // Metadata column's ordinal later, against that column's own OptionString — so a
            // parser that normalised or numbered it here would break that resolution.
            Assert.Equal("CRM", crm.TableTypeName);
            Assert.False(crm.IsTableTypeTemporary);
            Assert.Equal("dyn365bc_syntheticrelation", crm.ExternalName);
            Assert.Equal("SystemMetadata", crm.DataClassificationName);

            var temp = Assert.Single(tables, t => t.TableId == 60902);
            Assert.Equal("Temporary", temp.TableTypeName);
            Assert.True(temp.IsTableTypeTemporary);
            // The second, DIFFERENT value of DataClassification in this set: a parser echoing
            // one constant cannot satisfy this and the SystemMetadata assertion above.
            Assert.Equal("CustomerContent", temp.DataClassificationName);
            // The regression this test exists for. TryParseTableSymbol hardcoded
            // DataPerCompany: true while the source-parsed path read the declared property,
            // so all 41 Base Application 28.1 tables declaring DataPerCompany = false were
            // handed to Table Metadata (and to every other ParsedTable consumer) as
            // per-company. The symbol file spells AL's false "0".
            Assert.False(temp.DataPerCompany);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tables_UndeclaredProperties_StayNullSoAlDefaultsApplyLater()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tablemeta-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var tables = BcAppSymbolCache.Get(WriteApp(dir, SymbolReference)).Tables;

            var bare = Assert.Single(tables, t => t.TableId == 60903);
            // The negative half. "Declares none" must stay distinguishable from "declares the
            // default" all the way through: the parser records null, and the row builder
            // applies the defaults (TableType = Normal, DataClassification = CustomerContent,
            // ExternalName = blank). A parser that substituted those strings here would make
            // the two cases indistinguishable downstream.
            //
            // CustomerContent there was reasoning when this comment was written, and is now a
            // measurement: Microsoft documents the DataClassification default as
            // ToBeClassified, and corpus fixture ALT Unclassified (60837) put the question to a
            // real service tier in StefanMaron/BusinessCentral.AL.Language.Tests#191, which
            // answered CustomerContent on the eight Cloud legs of run 34026600861 (BC 27.0
            // through 28.4) — the legs that actually execute codeunit 60801. See
            // RecordPatches.TableMetadataVirtualTable.cs's AlDefaultDataClassification, which
            // records why eight is the number and why it is enough (#3019).
            Assert.Null(bare.TableTypeName);
            Assert.Null(bare.DataClassificationName);
            Assert.Null(bare.ExternalName);
            // AL's DataPerCompany default is TRUE, so only the explicit opt-out flips it —
            // the mirror of the "0" case above, and what keeps that assertion from being
            // satisfiable by a parser that simply always answers false.
            Assert.True(bare.DataPerCompany);

            // ExternalName is declared on exactly one of the three tables, so the column is
            // proven to vary rather than to be echoed onto every row.
            Assert.Null(Assert.Single(tables, t => t.TableId == 60902).ExternalName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
