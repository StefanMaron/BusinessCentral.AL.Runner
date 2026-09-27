// TableFieldOptionCaption* — issue #4857. Runner-mechanism tests; the BC claim (a precompiled
// table's Option field answers its OptionCaption through FieldRef.OptionCaption, Format,
// Evaluate and a Rec-bound TestPage control declaring none) is corpus codeunit 67600.
//
// These pin the three runner links a corpus run cannot tell apart when one breaks: the two
// symbol-file readers and the AL-source parser carry the declaration into ParsedField,
// BuildMetaField hands it to MetaField as OptionCaptionML (which BC's
// NCLMetaField.CreateFromMetaField reads, and nothing else), and a control with no
// ControlDefinition falls back to its bound value's captions. The same symbol readers dropped
// a field's declared Caption, so FieldCaption on a precompiled table answered the field name;
// the symbol tests pin that too.
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Captions deliberately differ from members; a reader echoing OptionMembers fails every assertion.
internal static class TableFieldOptionCaptionFixture
{
    internal const string Members = "Draft,In Process,Validaton in Process";
    internal const string Captions = "Draft,In Process,Validation in Process";
}

// BcAppSymbolCache resolves its on-disk path through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class TableFieldOptionCaptionSymbolTests
{
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 64857,
              "Name": "Opt Caption Table",
              "Fields": [
                { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } },
                {
                  "Id": 2, "Name": "Status",
                  "TypeDefinition": { "Name": "Option" },
                  "Properties": [
                    { "Name": "OptionMembers", "Value": "Draft,In Process,Validaton in Process" },
                    { "Name": "Caption", "Value": "Correction Status" },
                    { "Name": "OptionCaption", "Value": "Draft,In Process,Validation in Process" }
                  ]
                },
                {
                  "Id": 3, "Name": "Bare Option",
                  "TypeDefinition": { "Name": "Option" },
                  "Properties": [ { "Name": "OptionMembers", "Value": "A,B" } ]
                }
              ],
              "Keys": [ { "Name": "PK", "FieldNames": [ "Entry No." ] } ]
            }
          ],
          "Namespaces": [
            {
              "Name": "Probe",
              "TableExtensions": [
                {
                  "TargetObject": "#437dbf0e84ff417a965ded2bb9650972#Customer",
                  "Id": 64858,
                  "Name": "Opt Caption Ext",
                  "Fields": [
                    {
                      "Id": 64800, "Name": "Ext Status",
                      "TypeDefinition": { "Name": "Option" },
                      "Properties": [
                        { "Name": "OptionMembers", "Value": "Draft,In Process,Validaton in Process" },
                        { "Name": "Caption", "Value": "Ext Correction Status" },
                        { "Name": "OptionCaption", "Value": "Draft,In Process,Validation in Process" }
                      ]
                    },
                    {
                      "Id": 64801, "Name": "Ext Bare",
                      "TypeDefinition": { "Name": "Option" },
                      "Properties": [ { "Name": "OptionMembers", "Value": "A,B" } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string WriteApp()
    {
        var dir = TestScratch.Dir("al-runner-option-caption-4857");
        Directory.CreateDirectory(dir);
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    [Fact]
    public void TableLoop_ReadsTheFieldsOptionCaptionAndCaption_AndNullWhenNoneDeclared()
    {
        var table = Assert.Single(BcAppSymbolCache.Get(WriteApp()).Tables, t => t.TableId == 64857);

        var status = table.Fields.Single(f => f.FieldId == 2);
        Assert.Equal(TableFieldOptionCaptionFixture.Members, status.OptionMembers);
        Assert.Equal(TableFieldOptionCaptionFixture.Captions, status.OptionCaption);
        Assert.Null(table.Fields.Single(f => f.FieldId == 3).OptionCaption);
        // The field Caption, dropped by the same reader: FieldCaption answered the name.
        Assert.Equal("Correction Status", status.Caption);
        Assert.Null(table.Fields.Single(f => f.FieldId == 3).Caption);
    }

    [Fact]
    public void TableExtensionLoop_ReadsTheFieldsOptionCaptionAndCaption_AndNullWhenNoneDeclared()
    {
        var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(WriteApp()));

        Assert.Equal(TableFieldOptionCaptionFixture.Captions, ext.Fields.Single(f => f.FieldId == 64800).OptionCaption);
        Assert.Null(ext.Fields.Single(f => f.FieldId == 64801).OptionCaption);
        Assert.Equal("Ext Correction Status", ext.Fields.Single(f => f.FieldId == 64800).Caption);
        Assert.Null(ext.Fields.Single(f => f.FieldId == 64801).Caption);
    }
}

// Writes the metadata reflection statics and the parser statics.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class TableFieldOptionCaptionBuilderTests : IDisposable
{
    private readonly Dictionary<string, object?> _savedStatics = new();

    public TableFieldOptionCaptionBuilderTests()
    {
        var types = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaTable).Assembly;
        foreach (var (field, typeName) in new[]
                 {
                     ("_tMetaField",            "Microsoft.Dynamics.Nav.Types.Metadata.MetaField"),
                     ("_tALDataClassification", "Microsoft.Dynamics.Nav.Types.Metadata.ALDataClassification"),
                     ("_tNavType",              "Microsoft.Dynamics.Nav.Types.NavType"),
                     ("_tFieldClass",           "Microsoft.Dynamics.Nav.Types.Metadata.FieldClass"),
                     ("_tObsoleteState",        "Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState"),
                 })
        {
            var fi = Static(field);
            _savedStatics[field] = fi.GetValue(null);
            fi.SetValue(null, types.GetType(typeName)
                              ?? throw new InvalidOperationException($"{typeName} not found."));
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedStatics)
            try { Static(name).SetValue(null, value); } catch { }
    }

    private static FieldInfo Static(string name) =>
        typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"RecordPatches.{name} not found — this test tracks that field.");

    private static Microsoft.Dynamics.Nav.Types.Metadata.MetaField Build(string? optionCaption)
    {
        var f = new ParsedField(2, "Status", "Option", 0,
            OptionMembers: TableFieldOptionCaptionFixture.Members, OptionCaption: optionCaption);
        var m = typeof(RecordPatches).GetMethod("BuildMetaField", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("RecordPatches.BuildMetaField not found — this test drives it.");
        return (Microsoft.Dynamics.Nav.Types.Metadata.MetaField)m.Invoke(null, new object?[] { f, 0, false, null })!;
    }

    [Fact]
    public void DeclaredOptionCaption_ReachesMetaFieldAsItsOptionCaptionML()
    {
        var built = Build(TableFieldOptionCaptionFixture.Captions);

        // OptionCaptionML is the member NCLMetaField.CreateFromMetaField reads; OptionString
        // stays the member names, so the two are provably different sources.
        Assert.Equal(TableFieldOptionCaptionFixture.Captions, Enu(built.OptionCaptionML));
        Assert.Equal(TableFieldOptionCaptionFixture.Captions, PlainOptionCaption(built));
        Assert.Equal(TableFieldOptionCaptionFixture.Members, built.OptionString);
    }

    // Read off LanguageIds/Texts: GetText goes through WindowsLanguageHelper, which needs the
    // engine's Linux rewrite and so fails or passes by test order.
    private static string? Enu(Microsoft.Dynamics.Nav.Types.Metadata.MultiLanguage? ml)
    {
        if (ml == null) return null;
        for (int i = 0; i < ml.LanguageIds.Count && i < ml.Texts.Count; i++)
            if (ml.LanguageIds[i] == 1033) return ml.Texts[i];
        return null;
    }

    // MetaField's plain OptionCaption getter is not public; BC's emitter states it beside the ML.
    private static object? PlainOptionCaption(Microsoft.Dynamics.Nav.Types.Metadata.MetaField f) =>
        (typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaField).GetProperty("OptionCaption",
             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
         ?? throw new InvalidOperationException("MetaField.OptionCaption not found — this test tracks it."))
        .GetValue(f);

    [Fact]
    public void NoDeclaredOptionCaption_LeavesOptionCaptionMLUnset()
    {
        // Unset is what makes BC answer OptionString, the correct answer for a field declaring
        // no OptionCaption; an ML built from the member names would be an invented declaration.
        var built = Build(null);

        Assert.True(string.IsNullOrEmpty(Enu(built.OptionCaptionML)));
        Assert.Equal(TableFieldOptionCaptionFixture.Members, built.OptionString);
    }

    [Fact]
    public void AlSourceParser_ReadsTheOptionCaptionLabel_AndNullWhenNoneDeclared()
    {
        const int tableId = 64859;
        var text = $$"""
            table {{tableId}} "Opt Caption Source"
            {
                fields
                {
                    field(1; "Entry No."; Integer) { }
                    field(2; Status; Option)
                    {
                        OptionMembers = Draft,"In Process","Validaton in Process";
                        OptionCaption = 'Draft,In Process,Validation in Process', Comment = 'not part of it';
                    }
                    field(3; "Bare Option"; Option) { OptionMembers = A,B; }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """;
        typeof(RecordPatches)
            .GetMethod("TryParseTableFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .InvokeStatic(text);
        var table = ((Dictionary<int, ParsedTable>)typeof(RecordPatches)
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)[tableId];

        Assert.Equal(TableFieldOptionCaptionFixture.Captions, table.Fields.Single(f => f.FieldId == 2).OptionCaption);
        Assert.Null(table.Fields.Single(f => f.FieldId == 3).OptionCaption);
    }
}

// ParserStaticsIsolationGuardTests scans this file as a whole, and the builder class above
// reaches the parse statics.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class TableFieldOptionCaptionBoundOptionTests
{
    // NCLOptionMetadataWithCaptions needs server settings to construct; a subclass answering a
    // fixed OptionCaption is the part TestPageOptionValue reads.
    private sealed class CaptionedOptionMetadata(string members, string captions) : NCLOptionMetadata(members)
    {
        public override string OptionCaption => captions;
    }

    private static RunnerPageInstance BuildPage()
    {
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            types: new[] { typeof(object), typeof(object), typeof(NavRecord), typeof(int), typeof(System.Collections.IDictionary) },
            modifiers: null)
            ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");
        return (RunnerPageInstance)ctor.Invoke(new object?[]
        {
            new object(), new object(), null, 4857, new Dictionary<string, object?>()
        });
    }

    [Fact]
    public void ControlWithoutDefinition_AnswersTheBoundFieldsCaptions()
    {
        var option = NavOption.Create(
            new CaptionedOptionMetadata(TableFieldOptionCaptionFixture.Members, TableFieldOptionCaptionFixture.Captions), 2);

        Assert.Equal(TableFieldOptionCaptionFixture.Captions.Split(','), BuildPage().TryGetOptionCaptions(1, option));
    }

    [Fact]
    public void FieldWithoutCaptions_StillAnswersNull()
    {
        // BC's OptionCaption answers OptionString when nothing is declared; that must keep the
        // caller on its member-name path rather than read as a declared caption list.
        var option = NavOption.Create(NCLOptionMetadata.Create(TableFieldOptionCaptionFixture.Members), 0);

        Assert.Null(BuildPage().TryGetOptionCaptions(1, option));
        Assert.Null(TestPageOptionValue.BoundOptionCaptions(null));
    }
}
