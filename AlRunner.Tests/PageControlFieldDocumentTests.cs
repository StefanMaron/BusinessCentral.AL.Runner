// PageControlFieldDocumentTests — issue #3604.
//
// RUNNER-MECHANISM tests, not claims about what real BC does. The behavioural claim
// ("Page Control Field 2000000192 reports these values") is adjudicated upstream against a
// live BC service tier by corpus PR #300 (codeunit 60426 "Test Page Control Field Tree"),
// per .claude/rules/bc-behavior-tests-go-upstream.md.
//
// What these pin instead is the premise the fix rests on, which is the half a corpus test
// cannot see: the corpus asserts what the virtual table ANSWERS, never WHERE the answer came
// from. Three properties of BC's emitted page document are load-bearing here, and if a future
// BC version changed any of them the corpus test would keep passing against the AL-derived
// fallback while the conversion had silently stopped applying:
//
//   * the document states the binding decision as DataColumnName — a field NUMBER for a
//     table-bound control, a synthetic name for an unbound one — which is what makes
//     `int.TryParse` a faithful reproduction of ControlDefinition.IsBoundToTableField;
//   * an unbound control's source expression lives in <Expressions>, keyed by that same
//     DataColumnName, and nowhere on the control element itself;
//   * Enabled/Visible are omitted when defaulted while Editable is omitted when defaulted
//     AND has no default, which is why the three are not filled in the same way.
//
// docs/page-control-field-from-bc-document.md has the measurements behind all three.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class PageControlFieldDocumentTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public PageControlFieldDocumentTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-page-control-field-document-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        // The AL parser's dictionaries and the delta memo are static, so a fixture left
        // behind here would answer for the NEXT class's page of the same id. Both are
        // dropped for the same reason the runtime drops them on a --watch reload.
        RemoveParsedPage(90331);
        RemoveParsedPageExtension(90333);
        ClearBcPageExtensionControls();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void ClearBcPageExtensionControls()
        => typeof(AlRunner.Patches.RecordPatches)
            .GetMethod("ClearBcPageExtensionControls",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null);

    private static void RemoveParsedPage(int id) => RemoveFromDict("_parsedPages", id);
    private static void RemoveParsedPageExtension(int id) => RemoveFromDict("_parsedPageExtensions", id);

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(AlRunner.Patches.RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    /// <summary>
    /// Mirrors corpus fixture page 60427's shapes: a Rec-bound control, a control bound to a
    /// page VARIABLE, an Option-bound control, and two controls three group levels deep — one
    /// declaring Visible = false and one declaring Editable = false. Every assertion below
    /// fails against what the AL derivation produced before #3604.
    /// </summary>
    private const string FixtureAl = """
        table 90320 "PcfDoc Sample"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(14; "Option Field"; Option) { OptionMembers = " ",Draft,Active,Closed; DataClassification = CustomerContent; }
                field(17; "Description Field"; Text[250]) { DataClassification = CustomerContent; }
                field(18; "Name Field"; Text[50]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        page 90321 "PcfDoc Fixture"
        {
            PageType = Card;
            SourceTable = "PcfDoc Sample";

            layout
            {
                area(Content)
                {
                    group(Level1)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                        field(PcfDocLocalVar; LocalVar) { ApplicationArea = All; }
                        group(Level2)
                        {
                            field(PcfDocOption; Rec."Option Field") { ApplicationArea = All; }
                            group(Level3)
                            {
                                field(PcfDocHidden; Rec."Name Field") { ApplicationArea = All; Visible = false; }
                                field(PcfDocEditable; Rec."Description Field") { ApplicationArea = All; Editable = false; }
                            }
                        }
                    }
                }
            }

            var
                LocalVar: Integer;
        }
        """;

    private System.Xml.XmlElement EmitAndReadDocument()
    {
        File.WriteAllText(Path.Combine(_root, "PcfDoc.al"), FixtureAl);

        var output = new BcCompiler().Emit(new[] { _root }, "PcfDocModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the page to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        // "Page" is the AL compiler's own SymbolKind name, and it is the key the conversion
        // reads. A kind spelled anything else here means HasBcPageMetadataDocument answers
        // false for every page and the whole conversion silently reverts to the AL parser.
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", 90321, out var xml),
            "Expected page 90321's metadata document to be captured at emit time under kind "
            + "\"Page\" — without it the virtual table falls back to AL source text.");
        Assert.False(string.IsNullOrEmpty(xml), "the captured document is empty");

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        Assert.NotNull(doc.DocumentElement);
        return doc.DocumentElement!;
    }

    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Every element BC's <c>ed is ControlDefinition</c> predicate selects, in the
    /// document order its <c>FindAll</c> walk visits them.</summary>
    private static List<System.Xml.XmlElement> FieldControls(System.Xml.XmlElement parent)
    {
        var found = new List<System.Xml.XmlElement>();
        foreach (System.Xml.XmlNode n in parent.ChildNodes)
        {
            if (n is not System.Xml.XmlElement e) continue;
            var type = e.GetAttribute("type", Xsi);
            if (e.Name == "Controls" && (type == "ControlDefinition" || type == "MetaControlDefinition"))
                found.Add(e);
            found.AddRange(FieldControls(e));
        }
        return found;
    }

    private static System.Xml.XmlElement Named(List<System.Xml.XmlElement> controls, string name)
        => controls.Single(c => c.GetAttribute("Name") == name);

    [SkippableFact]
    public void EmittedDocument_ListsEveryFieldControl_BoundOrNot_AtEveryDepth()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var controls = FieldControls(EmitAndReadDocument());

        // Positive: all five, including the variable-bound one the AL derivation dropped
        // entirely and the two nested three group levels deep.
        Assert.Equal(
            new[] { "Entry No.", "PcfDocLocalVar", "PcfDocOption", "PcfDocHidden", "PcfDocEditable" },
            controls.Select(c => c.GetAttribute("Name")).ToArray());

        // Negative: the predicate is not "anything under <Content>". The three groups are
        // ControlGroupDefinition and the auto-added fact box part is an
        // InfopartSystemDefinition; none of them is a row, so a walk that matched on element
        // name alone — or on an xsi:type PREFIX — would be caught here.
        Assert.DoesNotContain("Level1", controls.Select(c => c.GetAttribute("Name")));
        Assert.DoesNotContain("Level3", controls.Select(c => c.GetAttribute("Name")));
        Assert.DoesNotContain("DefaultSummaryPart", controls.Select(c => c.GetAttribute("Name")));
    }

    [SkippableFact]
    public void EmittedDocument_StatesTheBindingAsDataColumnName_ANumberOnlyWhenTableBound()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var controls = FieldControls(EmitAndReadDocument());

        // Positive: a table-bound control's DataColumnName IS the field number, which is why
        // ControlDefinition.IsBoundToTableField is int.TryParse and nothing more. Asserting
        // the exact ids proves the document resolves the binding, rather than merely that it
        // parses as an integer.
        Assert.Equal("1", Named(controls, "Entry No.").GetAttribute("DataColumnName"));
        Assert.Equal("14", Named(controls, "PcfDocOption").GetAttribute("DataColumnName"));
        Assert.Equal("17", Named(controls, "PcfDocEditable").GetAttribute("DataColumnName"));
        Assert.Equal("18", Named(controls, "PcfDocHidden").GetAttribute("DataColumnName"));

        // Negative: the variable-bound control's is NOT parseable as a field number, so the
        // same test routes it to the unbound branch. If BC ever emitted a number here the
        // control would be silently mis-bound to a real field.
        var unbound = Named(controls, "PcfDocLocalVar").GetAttribute("DataColumnName");
        Assert.False(int.TryParse(unbound, out _),
            $"expected a non-numeric DataColumnName for a variable-bound control, got '{unbound}'");

        // And the control element itself carries NO SourceExpression — the whole reason the
        // <Expressions> lookup in the next test has to exist.
        Assert.False(Named(controls, "PcfDocLocalVar").HasAttribute("SourceExpression"));
    }

    [SkippableFact]
    public void EmittedDocument_StatesAnUnboundControlsSourceExpressionInExpressions()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = EmitAndReadDocument();
        var unboundKey = Named(FieldControls(root), "PcfDocLocalVar").GetAttribute("DataColumnName");

        var expressions = root.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>().SingleOrDefault(e => e.Name == "Expressions");
        Assert.NotNull(expressions);

        var entries = expressions!.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>().Where(e => e.Name == "Expression").ToList();

        // Positive: keyed by the control's own DataColumnName — BC's lookup is
        // page.Expressions.FirstOrDefault(x => x.Name == control.DataColumnName) — and it
        // states the AL VARIABLE's name.
        var entry = entries.Single(e => e.GetAttribute("Name") == unboundKey);
        Assert.Equal("LocalVar", entry.GetAttribute("SourceExpression"));

        // Negative: only the unbound control gets an entry. A table-bound control resolves
        // through the metatable instead, so an <Expressions> section that also described
        // those would mean the two branches could disagree.
        Assert.Single(entries);
    }

    [SkippableFact]
    public void EmittedDocument_OmitsDefaultedProperties_AndEditableHasNoDefaultToOmitTo()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var controls = FieldControls(EmitAndReadDocument());

        // Positive: a DECLARED property is stated verbatim, in both directions.
        Assert.Equal("false", Named(controls, "PcfDocHidden").GetAttribute("Visible"));
        Assert.Equal("false", Named(controls, "PcfDocEditable").GetAttribute("Editable"));

        // Negative, and the asymmetry the fix turns on: an UNDECLARED property is absent from
        // the document, so what the column reports is whatever BC's deserializer supplies.
        // ControlDefinition carries [DefaultValue("true")] on Enabled and Visible and NONE on
        // Editable, so Enabled/Visible read "true" and Editable reads "". Substituting "true"
        // for all three — which the AL derivation did — is therefore wrong for exactly one of
        // them. See docs/page-control-field-from-bc-document.md#the-three-property-defaults;
        // #3625 tracks getting the Editable half in front of a real tier.
        var plain = Named(controls, "Entry No.");
        Assert.False(plain.HasAttribute("Enabled"));
        Assert.False(plain.HasAttribute("Editable"));

        // Visible IS stated on this control even though it is defaulted, which is why the
        // reader must not infer "absent means declared-default" from Visible's presence.
        Assert.Equal("true", plain.GetAttribute("Visible"));
    }

    [SkippableFact]
    public void EmittedDocument_StatesThePagesSourceTableOnce_NotPerControl()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = EmitAndReadDocument();

        // BC assigns array[4] = tableNo OUTSIDE the bound/unbound branch, from
        // page.PageProperties.SourceObject.SourceTable — so TableNo is a property of the
        // PAGE, reported on every row including one whose control has no source field.
        var properties = root.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>().Single(e => e.Name == "Properties");
        var sourceObject = properties.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>().Single(e => e.Name == "SourceObject");
        Assert.Equal("90320", sourceObject.GetAttribute("SourceTable"));

        // Negative: no control element states a table of its own, so a per-control reading
        // would have nothing to read and the page-level one is the only source.
        foreach (var c in FieldControls(root))
            Assert.False(c.HasAttribute("SourceTable"),
                $"control '{c.GetAttribute("Name")}' unexpectedly states its own SourceTable");
    }

    // ---------------------------------------------------------------------------------
    // #3605 — a pageextension's added controls live ONLY in the extension's own
    // MetadataRuntimeDeltas document, never in the base page's <PageDefinition>.
    //
    // This is the exact inverse of tables (#3600), where a same-app tableextension's added
    // fields ARE folded into the base <MetaTable>. Carrying a table intuition across is
    // what makes the page case look already-solved when it is not.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A base page plus a pageextension that adds one control, over a table extended by a
    /// tableextension — the smallest shape in which "the control the base document does not
    /// mention" is a real, resolvable field rather than a dangling name.
    /// </summary>
    private const string ExtensionFixtureAl = """
        table 90330 "PcfExt Sample"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        tableextension 90332 "PcfExt Sample Ext" extends "PcfExt Sample"
        {
            fields
            {
                field(50; "Extra Note"; Text[30]) { DataClassification = CustomerContent; }
            }
        }

        page 90331 "PcfExt Fixture"
        {
            PageType = List;
            SourceTable = "PcfExt Sample";

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }
        }

        pageextension 90333 "PcfExt Fixture Ext" extends "PcfExt Fixture"
        {
            layout
            {
                addlast(Content)
                {
                    field("Extra Note"; Rec."Extra Note") { ApplicationArea = All; }
                }
            }
        }
        """;

    private void EmitExtensionFixture()
    {
        File.WriteAllText(Path.Combine(_root, "PcfExt.al"), ExtensionFixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "PcfExtModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        // A bare Emit captures the metadata documents but does NOT run the AL source parser,
        // which a real run does on the same sources. GetPageExtensionIdsForPage resolves a
        // page id to its NAME through that parser's dictionaries, so without this the lookup
        // finds no extensions and the conversion silently contributes nothing — the same
        // shape as the bug under test, which would make the assertion untrustworthy in both
        // directions. Driving the real parser here keeps the test on the production path.
        ParseAlSource("TryParsePageFile", ExtensionFixtureAl);
    }

    private static void ParseAlSource(string method, string source)
        => typeof(AlRunner.Patches.RecordPatches)
            .GetMethod(method,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, new object[] { source });

    [SkippableFact]
    public void BasePageDocument_DoesNotMentionAPageExtensionsAddedControl()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EmitExtensionFixture();

        Assert.True(AlObjectMetadataRegistry.TryGet("Page", 90331, out var pageXml),
            "expected the base page's document to be captured under kind \"Page\"");

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(pageXml);
        var controls = FieldControls(doc.DocumentElement!);

        // Positive: the base page's own control is there, so the document did parse and the
        // walk does find controls — without this the negative below would pass vacuously.
        Assert.Equal(new[] { "Entry No." }, controls.Select(c => c.GetAttribute("Name")).ToArray());

        // Negative, and the whole premise of #3605: the extension's control is absent from
        // the base document ENTIRELY — not merely from the walk. A substring check over the
        // raw XML cannot be fooled by a walk that skips an element shape it does not know.
        Assert.DoesNotContain("Extra Note", pageXml, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void PageExtensionDocument_CarriesTheAddedControlUnderControlAdd()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EmitExtensionFixture();

        // The kind string is load-bearing: the conversion looks the delta up under
        // "PageExtension", and any other spelling silently contributes zero extra controls.
        Assert.True(AlObjectMetadataRegistry.TryGet("PageExtension", 90333, out var extXml),
            "expected pageextension 90333's delta document to be captured under kind "
            + "\"PageExtension\" — without it the added control cannot be answered at all");

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(extXml);
        var root = doc.DocumentElement!;

        // The root element is MetadataRuntimeDeltas, NOT PageDefinition — this is a different
        // document shape from the base page's, which is why it needs its own read.
        Assert.Equal("MetadataRuntimeDeltas", root.Name);

        var adds = root.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>().Where(e => e.Name == "ControlAdd").ToList();
        Assert.Single(adds);

        // Positive: the added control sits under <ControlAdd> in exactly the <Controls
        // xsi:type="ControlDefinition"> shape the base document uses, which is why the same
        // collector reads both rather than needing a second parser.
        var added = FieldControls(adds[0]);
        Assert.Equal(new[] { "Extra Note" }, added.Select(c => c.GetAttribute("Name")).ToArray());

        // ...and it states the binding as a field NUMBER, so the bound branch resolves it
        // against the metatable the same way a base-page control resolves.
        Assert.Equal("50", added[0].GetAttribute("DataColumnName"));
    }

    [SkippableFact]
    public void PageControlFieldRows_IncludeAPageExtensionsAddedControl_WithItsResolvedField()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EmitExtensionFixture();

        var rows = PageControlFieldRowsFor(90331);

        // Positive: BOTH controls, the base page's and the extension's. Before #3605 this
        // answered only "Entry No." — the document path replaced an AL derivation that did
        // merge extensions, so the added control was dropped silently.
        Assert.Equal(new[] { "Entry No.", "Extra Note" },
            rows.Select(r => r.ControlName).ToArray());

        // The extension row carries BC's own resolved values, not defaults: field 50 comes
        // from the TABLEEXTENSION (so the delta's DataColumnName was really resolved against
        // the extended table, not echoed back), and TableNo is the page's source table, which
        // BC assigns on every row.
        //
        // SourceExpression is deliberately NOT asserted here. Resolving it needs a built
        // NCLMetaTable, which a bare BcCompiler.Emit does not construct — in this harness it
        // is empty for the base page's row too, so an assertion either way would measure the
        // harness rather than the conversion. It IS pinned end-to-end: a live runner probe on
        // this exact shape answers `Extra Note` (BC's field-NAME form, not the binding text
        // `Rec."Extra Note"`), and corpus codeunit 60426 pins the column against a real tier.
        var extra = rows.Single(r => r.ControlName == "Extra Note");
        Assert.Equal(50, extra.FieldNo);
        Assert.Equal(90330, extra.TableNo);

        // Sequence continues the base page's numbering rather than restarting at 0 — BC
        // numbers its FindAll walk over the MERGED page, so two rows may not share an index.
        Assert.Equal(0, rows.Single(r => r.ControlName == "Entry No.").Sequence);
        Assert.Equal(1, extra.Sequence);

        // Negative: the control id is hashed in the EXTENSION's id space, not the base
        // page's. Asserting inequality as well as equality is what catches a future change
        // that "fixes" the id by hashing against the page — which would make every TestPage
        // lookup of an extension control miss.
        Assert.Equal(MemberIdOf(90333, "Extra Note"), extra.ControlId);
        Assert.NotEqual(MemberIdOf(90331, "Extra Note"), extra.ControlId);
    }

    /// <summary>BC's IdSpace.GetMemberId, reached on the runner's own implementation by
    /// reflection so this cannot drift from the hash the runtime resolves controls with.</summary>
    private static int MemberIdOf(int ancestorObjectId, string name)
    {
        var t = typeof(AlRunner.Patches.RecordPatches).Assembly
            .GetType("AlRunner.Patches.RunnerPageInstance")
            ?? throw new InvalidOperationException("AlRunner.Patches.RunnerPageInstance not found.");
        var m = t.GetMethod("MemberId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerPageInstance.MemberId not found.");
        return (int)m.Invoke(null, new object[] { ancestorObjectId, name })!;
    }

    /// <summary>
    /// The rows the virtual table would report for one page, taken from the document path
    /// itself (<c>GetPageControlFieldRowsFromBcDocument</c>) rather than from the populated
    /// table, so the assertion is about the conversion under test and not about whether a
    /// virtual-table populate ran in this process.
    /// </summary>
    private static List<PageControlFieldRowView> PageControlFieldRowsFor(int pageId)
    {
        var rp = typeof(AlRunner.Patches.RecordPatches);
        var m = rp.GetMethod("GetPageControlFieldRowsFromBcDocument",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RecordPatches.GetPageControlFieldRowsFromBcDocument not found — the "
                + "#3604/#3605 conversion this class pins has been renamed or removed.");

        var raw = m.Invoke(null, new object[] { pageId });
        Assert.True(raw != null,
            $"page {pageId}: the document path returned null, meaning BC's document was not "
            + "captured or did not parse — the conversion is not being exercised at all.");

        var view = new List<PageControlFieldRowView>();
        foreach (var row in (System.Collections.IEnumerable)raw!)
        {
            var t = row.GetType();
            string S(string n) => (string)t.GetProperty(n)!.GetValue(row)!;
            int I(string n) => (int)t.GetProperty(n)!.GetValue(row)!;
            view.Add(new PageControlFieldRowView(
                I("ControlId"), S("ControlName"), I("TableNo"), I("FieldNo"),
                S("SourceExpression"), I("Sequence")));
        }
        return view;
    }

    private sealed record PageControlFieldRowView(
        int ControlId, string ControlName, int TableNo, int FieldNo,
        string SourceExpression, int Sequence);
}
