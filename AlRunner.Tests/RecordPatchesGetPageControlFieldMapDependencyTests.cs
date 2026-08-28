// RecordPatchesGetPageControlFieldMapDependencyTests — pins the runner's own C# mechanism
// for issue #2088: RecordPatches.GetPageControlFieldMap answering from _parsedPages only
// (pages the runner AL-source-PARSED itself), so a page shipping precompiled in a
// dependency .app (Base Application, System Application, an ISV extension) got an EMPTY
// control map regardless of what its controls are bound to, and every field control read
// on such a page threw RunnerOutOfScopeException("testpage-control-binding").
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The actual BC-observable claim — that TestPage.SomeControl.Value() resolves the real
// field value on a page shipping precompiled in a dependency .app — is proven end-to-end
// against a real Tier-1 precompiled dependency fixture in
// tests/runner-extras/testpage-precompiled-dep-control (both directions: control name
// differs from its field, control name matches its field). This file pins the narrower,
// runner-only mechanism claim underneath it, at BOTH ends of GetPageControlFieldMap's
// dependency-fallback branch:
//   - a control whose SourceExpression is Rec.Field, where Field exists on the resolved
//     table, MUST map to that field's real field number;
//   - a control whose SourceExpression names a field that does NOT exist on the resolved
//     table (an "upgrade drift" shape: a page control that never resolves to any field,
//     the only way GetPageControlFieldMap's fallback could ever legitimately need to
//     refuse one) must NOT appear in the map at all — the fallback must never fabricate a
//     binding.
// The negative case cannot be proven end-to-end in AL: BC's own AL compiler only emits a
// LiveNavTestPage.GetField(id) dispatch for a field control whose SourceExpression it can
// already validate, at compile time, as a plain Rec.Field against the SAME symbol data the
// runner reads here — a plain Rec.Field control that fails that check could never have
// compiled into a real dependency .app in the first place (real BC rejects it at publish
// time), and a control bound to anything else compiles to different generated code that
// never reaches GetField at all. Proving the fixed METHOD directly, the same way
// BcAppSymbolCachePageMetadataTests pins the SymbolReference.json PARSER one layer down, is
// the only way to pin this half of the contract.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as BcAppSymbolCachePageMetadataTests: BcAppSymbolCache.Get() (and the
// BcAppFallback table index GetPageControlFieldMap's dependency branch now populates
// through) resolve through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class RecordPatchesGetPageControlFieldMapDependencyTests
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

    // Distinctive, unlikely-to-collide ids: RecordPatches' dependency-page/table state
    // (_bcAppPaths, _bcSymbolTableIndex, _parsedTables) is process-global, so reusing an id
    // another test/fixture might also declare would risk reading back another test's
    // cached answer instead of this one's.
    private const int TableId = 88220400;
    private const int PageId = 88220401;
    private const int DescriptionControlId = 640644901; // arbitrary, distinct from FieldNo
    private const int GhostControlId = 640644902;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88220400,
              "Name": "RGCFM Dep Table",
              "Fields": [
                { "Id": 1, "Name": "ID", "TypeDefinition": { "Name": "Integer" } },
                { "Id": 2, "Name": "Message", "TypeDefinition": { "Name": "Text[100]" } }
              ],
              "Keys": [
                { "Name": "PK", "FieldNames": [ "ID" ], "Properties": [ { "Name": "Clustered", "Value": "1" } ] }
              ],
              "Properties": [ { "Name": "DataClassification", "Value": "ToBeClassified" } ]
            }
          ],
          "Pages": [
            {
              "Id": 88220401,
              "Name": "RGCFM Dep Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88220400" }
              ],
              "Controls": [
                {
                  "Kind": 1,
                  "Id": 1,
                  "Name": "content",
                  "Controls": [
                    {
                      "Kind": 8,
                      "Id": 640644901,
                      "Name": "Description",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Message" } ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640644902,
                      "Name": "GhostField",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.\"Does Not Exist\"" } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void GetPageControlFieldMap_DependencyPage_ResolvesPlainRecFieldControl()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            // Positive: the "Description" control (bound to Rec.Message, a real field on
            // the resolved table) resolves to that field's real field number (2), the same
            // field id LiveNavTestPage.GetField hands to NavRecord to read the row.
            Assert.True(map.TryGetValue(DescriptionControlId, out var fieldNo),
                "a control bound to a plain, real Rec.Field must appear in the map");
            Assert.Equal(2, fieldNo);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetPageControlFieldMap_DependencyPage_OmitsControlWhoseFieldDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            // Negative: "GhostField" names Rec."Does Not Exist" -- absent from the resolved
            // table's Fields -- so the fallback must NOT fabricate a binding for it. A
            // no-op implementation that always returns an empty map would pass the
            // assertion above trivially but fail nothing here either; this assertion alone
            // does not prove the fix, but combined with the positive test's non-empty,
            // correct field number, an implementation that always returns empty fails that
            // one, and an implementation that maps EVERYTHING (ignoring field existence)
            // fails this one.
            Assert.False(map.ContainsKey(GhostControlId),
                "a control whose declared field does not exist on the table must not appear in the map");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
