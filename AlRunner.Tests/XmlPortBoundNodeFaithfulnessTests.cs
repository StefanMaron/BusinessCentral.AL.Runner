// A bound xmlport node's DataType and SourceField, written the way BC's emitter writes them
// (#4651). Base Application xmlport 99000751 synthesized, then BC's engine threw
// "Metadata mismatch for node type" on export: 78 of its 700 nodes carried no DataType,
// because an Enum field and a tableextension field did not resolve to a type. Every expected
// value below is BC's own output for that port (tools/metadata-ground-truth, BC 28.5.54151.55132):
// `Option` for Item."Costing Method" (an Enum), `Code` for Item."Routing No." (tableextension
// 99000750), and `item::No.` for `Item."No."` under `tableelement(item; Item)`.
//
// The report sibling (a column's FieldNo) resolves a field name against the same table and had
// the same blind spot for extension fields, so it is pinned here too.
using System.Collections;
using System.Reflection;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class XmlPortBoundNodeFaithfulnessTests
{
    // Ids no other test uses, so a leak is obvious.
    private const int ItemId = 64481;
    private const string ItemName = "XPB Item";

    private static ParsedTable Item() => new(ItemId, ItemName, new List<ParsedField>
    {
        new(1, "No.", "Code", 20),
        new(2, "Costing Method", "Enum", 0, EnumTypeId: 64482, EnumTypeName: "XPB Costing Method"),
        new(3, "Kind", "Option", 0, OptionMembers: "A,B"),
    }, new List<int> { 1 });

    private static readonly ParsedField ExtRouting = new(99000750, "Routing No.", "Code", 20);

    private const string PortAl = """
        xmlport 64483 "XPB Probe"
        {
            schema
            {
                textelement(Root)
                {
                    tableelement(item; "XPB Item")
                    {
                        fieldelement(No; Item."No.") { }
                        fieldelement(Costing; Item."Costing Method") { }
                        fieldelement(Kind; Item.Kind) { }
                        fieldelement(Routing; Item."Routing No.") { }
                    }
                }
            }
        }
        """;

    private static XmlNode NodeNamed(XmlDocument doc, string name)
    {
        foreach (XmlNode n in doc.SelectNodes("/XmlPort/Node")!)
            if (n.SelectSingleNode("NodeName")?.InnerText == name) return n;
        throw new InvalidOperationException($"no node named {name}");
    }

    private static XmlDocument Emit()
    {
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(64483, "XPB Probe", new Dictionary<string, string>(), PortAl);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc;
    }

    [Fact]
    public void EnumField_IsOption_AndAnOptionFieldStaysOption()
    {
        WithItem(() =>
        {
            var doc = Emit();
            Assert.Equal("Option", NodeNamed(doc, "Costing").SelectSingleNode("DataType")?.InnerText);
            Assert.Equal("Option", NodeNamed(doc, "Kind").SelectSingleNode("DataType")?.InnerText);
            Assert.Equal("Code", NodeNamed(doc, "No").SelectSingleNode("DataType")?.InnerText);
        });
    }

    [Fact]
    public void TableExtensionField_IsTyped()
    {
        WithItem(() =>
        {
            var doc = Emit();
            Assert.Equal("Code", NodeNamed(doc, "Routing").SelectSingleNode("DataType")?.InnerText);
        });
    }

    [Fact]
    public void SourceField_SpellsTheRecordAsItsTableElementDeclaresIt()
    {
        WithItem(() =>
        {
            var doc = Emit();
            Assert.Equal("item::No.", NodeNamed(doc, "No").SelectSingleNode("SourceField")?.InnerText);
            Assert.Equal("item::Costing Method", NodeNamed(doc, "Costing").SelectSingleNode("SourceField")?.InnerText);
        });
    }

    [Fact]
    public void ReportColumnOnATableExtensionField_StatesItsFieldNo()
    {
        WithItem(() =>
        {
            var report = new BcAppSymbolCache.ReportSymbol(64484, "XPB Report", null, false, true, null,
                new List<BcAppSymbolCache.ReportDataItemSymbol>
                {
                    new(1, "ItemLoop", ItemName, 0, null, null, new List<BcAppSymbolCache.ReportColumnSymbol>
                    {
                        new(10, "RoutingCol", "Code"),
                        new(11, "NoCol", "Code"),
                    }),
                });
            var xml = RecordPatches.EmitReportXml(report, new Dictionary<string, string>
            {
                ["RoutingCol"] = "ItemLoop.\"Routing No.\"",
                ["NoCol"] = "ItemLoop.\"No.\"",
            });
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            string? FieldNo(string col)
            {
                foreach (XmlNode f in doc.SelectNodes("//DataItemField")!)
                    if (f.SelectSingleNode("FriendlyFieldName")?.InnerText == col)
                        return f.SelectSingleNode("FieldNo")?.InnerText;
                throw new InvalidOperationException($"no column {col}");
            }
            Assert.Equal("99000750", FieldNo("RoutingCol"));
            Assert.Equal("1", FieldNo("NoCol"));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IDictionary Static(string name) => (IDictionary)typeof(RecordPatches)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void WithItem(Action act)
    {
        var parsed = Static("_parsedTables");
        var ext = Static("_parsedExtensionFields");
        var key = ItemName.ToLowerInvariant();
        Assert.False(parsed.Contains(ItemId), $"table id {ItemId} is already in _parsedTables; pick another");
        Assert.False(ext.Contains(key), $"'{key}' already has extension fields; pick another name");
        try
        {
            parsed[ItemId] = Item();
            ext[key] = new List<ParsedField> { ExtRouting };
            act();
        }
        finally
        {
            parsed.Remove(ItemId);
            ext.Remove(key);
        }
    }
}
