// Issue #4660: a control that a PRECOMPILED pageextension adds to a PRECOMPILED page was absent
// from RecordPatches.GetPageControlFieldMap's dependency fallback, which read only the base
// page's own symbol controls, so TestPage.GetField answered null and BC raised
// "The field with ID = N is not found on the page." End-to-end proof on a real service tier:
// corpus codeunit 67400 (StefanMaron/BusinessCentral.AL.Language.Tests#444).
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageExtensionControlFieldMapTests
{
    private const int TableId = 88246600;
    private const int PageId = 88246601;
    private const int BaseControlId = 646600001;
    private const int ExtControlId = 646600002;
    private const int ExtGhostControlId = 646600003;
    private const int OtherPageExtControlId = 646600004;
    private const int PrecompiledExtId = 88246603;
    private const int SourceOtherPageExtId = 88246605;

    private static readonly Type RP = typeof(RecordPatches);

    private static IDictionary ParsedPageExtensions =>
        (IDictionary)RP.GetField("_parsedPageExtensions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static void InvokeParser(string methodName, string source)
        => RP.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!.InvokeStatic(source);

    /// <summary>The control ids the source parser assigned to a pageextension's field controls.</summary>
    private static int[] ParsedControlIds(int extId)
    {
        var ext = ParsedPageExtensions[extId]
            ?? throw new InvalidOperationException($"pageextension {extId} was not parsed");
        var map = (IReadOnlyDictionary<int, string>)ext.GetType().GetProperty("ControlIdToFieldName")!.GetValue(ext)!;
        return map.Keys.ToArray();
    }

    // The page declares one field control of its own; a pageextension in the same .app adds a
    // control bound to a tableextension field, and one bound to a field nobody declares. A
    // second pageextension targets a DIFFERENT page, so its control must not leak onto this one.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88246600,
              "Name": "DPXC Dep Table",
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code[10]" } }
              ],
              "Keys": [
                { "Name": "PK", "FieldNames": [ "Code" ], "Properties": [ { "Name": "Clustered", "Value": "1" } ] }
              ]
            }
          ],
          "TableExtensions": [
            {
              "Id": 88246602,
              "Name": "DPXC Dep Table Ext",
              "TargetObject": "DPXC Dep Table",
              "Fields": [
                { "Id": 50, "Name": "Ext Flag", "TypeDefinition": { "Name": "Boolean" } }
              ]
            }
          ],
          "Pages": [
            {
              "Id": 88246601,
              "Name": "DPXC Dep Page",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "88246600" }
              ],
              "Controls": [
                {
                  "Kind": 1, "Id": 1, "Name": "content",
                  "Controls": [
                    { "Kind": 8, "Id": 646600001, "Name": "CodeCtl",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" } ] }
                  ]
                }
              ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88246603,
              "Name": "DPXC Dep Page Ext",
              "TargetObject": "DPXC Dep Page",
              "ControlChanges": [
                {
                  "Anchor": "content",
                  "ChangeKind": 2,
                  "Controls": [
                    { "Kind": 8, "Id": 646600002, "Name": "Ext Flag",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.\"Ext Flag\"" } ] },
                    { "Kind": 8, "Id": 646600003, "Name": "Ghost",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.\"Does Not Exist\"" } ] }
                  ]
                }
              ]
            },
            {
              "Id": 88246604,
              "Name": "DPXC Other Page Ext",
              "TargetObject": "DPXC Some Other Page",
              "ControlChanges": [
                {
                  "Anchor": "content",
                  "ChangeKind": 2,
                  "Controls": [
                    { "Kind": 8, "Id": 646600004, "Name": "Leaked",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" } ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    [Fact]
    public void DependencyPageExtensionControl_BoundToTableExtensionField_IsMapped()
    {
        var dir = TestScratch.Dir("al-runner-dep-pageext-fieldmap-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            Assert.True(map.TryGetValue(BaseControlId, out var baseField), "the page's own control must still map");
            Assert.Equal(1, baseField);
            Assert.True(map.TryGetValue(ExtControlId, out var extField),
                "a control the dependency pageextension adds must map to its tableextension field");
            Assert.Equal(50, extField);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DependencyPageExtensionControl_UnresolvableOrForeign_IsNotMapped()
    {
        var dir = TestScratch.Dir("al-runner-dep-pageext-fieldmap-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            Assert.False(map.ContainsKey(ExtGhostControlId),
                "an extension control whose field does not exist must not get a fabricated binding");
            Assert.False(map.ContainsKey(OtherPageExtControlId),
                "a pageextension of another page must not contribute controls to this one");
            Assert.Equal(2, map.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SourcePageExtensionOfAnotherPage_DoesNotBindOntoPrecompiledPage()
    {
        var dir = TestScratch.Dir("al-runner-dep-pageext-fieldmap-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));
            InvokeParser("TryParsePageFile", $$"""
                pageextension {{SourceOtherPageExtId}} "DPXC Src Other Ext" extends "DPXC Some Other Page"
                {
                    layout
                    {
                        addlast(content)
                        {
                            field("Src Leak"; Rec.Code) { }
                        }
                    }
                }
                """);
            var leakIds = ParsedControlIds(SourceOtherPageExtId);
            Assert.Single(leakIds);

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            Assert.False(map.ContainsKey(leakIds[0]),
                "a source-parsed pageextension of another page must not bind onto this precompiled page");
            Assert.Equal(new[] { BaseControlId, ExtControlId }.OrderBy(i => i), map.Keys.OrderBy(i => i));
        }
        finally
        {
            ParsedPageExtensions.Remove(SourceOtherPageExtId);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SameNumberedSourcePageExtension_ReplacesThePrecompiledOne()
    {
        var dir = TestScratch.Dir("al-runner-dep-pageext-fieldmap-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));
            InvokeParser("TryParsePageFile", $$"""
                pageextension {{PrecompiledExtId}} "DPXC Dep Page Ext" extends "DPXC Dep Page"
                {
                    layout
                    {
                        addlast(content)
                        {
                            field("Src Code"; Rec.Code) { }
                        }
                    }
                }
                """);
            var srcIds = ParsedControlIds(PrecompiledExtId);
            Assert.Single(srcIds);

            var map = RecordPatches.GetPageControlFieldMap(PageId);

            Assert.False(map.ContainsKey(ExtControlId),
                "the precompiled extension's Ext Flag control must not appear when a source-parsed " +
                "extension with the same id replaces it");
            Assert.True(map.TryGetValue(srcIds[0], out var srcField),
                "the source-parsed extension's own control must bind onto the precompiled page");
            Assert.Equal(1, srcField);
            Assert.Equal(2, map.Count);
        }
        finally
        {
            ParsedPageExtensions.Remove(PrecompiledExtId);
            Directory.Delete(dir, recursive: true);
        }
    }
}
