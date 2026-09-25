// XmlPortCanonicalPropertyEncodingTests — #4471.
//
// A tableelement's SourceTableView and LinkFields, and the xmlport's Permissions, are written in
// BC's canonical form rather than as the AL source text. Every expected string below was
// produced by BC's own emitter (tools/metadata-ground-truth, BC 28.1.49838.53910) compiling the
// same AL against tables with the same field ids and types; see
// docs/xmlport-metadata-from-bc.md#table-views-link-fields-and-permissions.
//
// The tables are staged in _parsedTables directly, the same way ResolveTableIdByNameScopeTests
// stages its state, so no .app is needed.
using System.Collections;
using System.Reflection;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class XmlPortCanonicalPropertyEncodingTests
{
    // Ids no other test uses, so a leak is obvious.
    private const int HdrId = 64471;
    private const int LnId = 64472;
    private const int ValId = 64473;

    private static ParsedTable Hdr() => new(HdrId, "XPE Hdr", new List<ParsedField>
    {
        new(1, "No.", "Code", 20),
        new(2, "Kind", "Option", 0, OptionMembers: "A,B,C"),
        new(5, "Amount (LCY)", "Decimal", 0),
        new(7, "Name", "Text", 50),
        new(9, "Flag", "Boolean", 0),
    }, new List<int> { 1 });

    private static ParsedTable Ln() => new(LnId, "XPE Ln", new List<ParsedField>
    {
        new(1, "Doc No.", "Code", 20),
        new(3, "Line No.", "Integer", 0),
        new(4, "Hdr Kind", "Option", 0, OptionMembers: "A,B,C"),
        new(6, "Qty", "Decimal", 0),
    }, new List<int> { 1, 3 });

    private static ParsedTable Val() => new(ValId, "XPE Val", new List<ParsedField>
    {
        new(1, "No.", "Code", 20),
        new(2, "Kind", "Option", 0, OptionMembers: "A,B,C D"),
        new(3, "EK", "Enum", 0, EnumTypeId: 64474, EnumTypeName: "XPE Kind"),
        new(4, "D", "Date", 0),
        new(5, "Dec", "Decimal", 0),
        new(6, "I", "Integer", 0),
        new(7, "Flag", "Boolean", 0),
        new(8, "Txt", "Text", 50),
    }, new List<int> { 1 });

    // Probe 1: sorting, order, where, and LinkFields across two tables.
    private const string LinkedPortAl = """
        xmlport 64475 "XPE Probe"
        {
            schema
            {
                textelement(Root)
                {
                    tableelement(H1; "XPE Hdr")
                    {
                        SourceTableView = sorting(Kind, "Amount (LCY)") order(descending) where(Kind = const(B), "Amount (LCY)" = filter(> 10), Name = filter('a*|b'));
                        fieldattribute(No; H1."No.") { }
                        tableelement(L1; "XPE Ln")
                        {
                            LinkTable = H1;
                            LinkFields = "Doc No." = field("No."), "Hdr Kind" = field(Kind);
                            SourceTableView = order(ascending);
                            fieldattribute(Q; L1.Qty) { }
                        }
                    }
                    tableelement(H2; "XPE Hdr")
                    {
                        SourceTableView = where(Flag = const(true), Name = const('x y'));
                        fieldattribute(No2; H2."No.") { }
                        tableelement(L2; "XPE Ln")
                        {
                            LinkTable = H2;
                            LinkFields = "Doc No." = field("No.");
                            SourceTableView = sorting(Qty);
                            fieldattribute(Q2; L2.Qty) { }
                        }
                    }
                    tableelement(H3; "XPE Hdr")
                    {
                        SourceTableView = sorting("No.") order(descending);
                        fieldattribute(No3; H3."No.") { }
                    }
                }
            }
        }
        """;

    private static string ValuePortAl(string view) => $$"""
        xmlport 64475 "XPE Probe"
        {
            schema
            {
                textelement(Root)
                {
                    tableelement(A1; "XPE Val")
                    {
                        SourceTableView = {{view}};
                        fieldattribute(N1; A1."No.") { }
                    }
                }
            }
        }
        """;

    private static XmlDocument Emit(string al, Dictionary<string, string>? properties = null)
    {
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(
            64475, "XPE Probe", properties ?? new Dictionary<string, string>(), al);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc;
    }

    /// <summary>Each tableelement's own element, by its AL node name.</summary>
    private static string? TableNodeChild(XmlDocument doc, string nodeName, string child)
    {
        foreach (XmlNode n in doc.SelectNodes("/XmlPort/Node")!)
            if (n.SelectSingleNode("NodeName")?.InnerText == nodeName)
                return n.SelectSingleNode(child)?.InnerText;
        throw new InvalidOperationException($"no node named {nodeName}");
    }

    [Fact]
    public void SourceTableView_IsWrittenAsBcWritesIt()
    {
        WithTables(() =>
        {
            var doc = Emit(LinkedPortAl);
            Assert.Equal("SORTING(Field2,Field5) ORDER(1) WHERE(Field2=0(1),Field5=1(>10),Field7=1(a*|b))",
                TableNodeChild(doc, "H1", "SourceTableView"));
            Assert.Equal("ORDER(0)", TableNodeChild(doc, "L1", "SourceTableView"));
            Assert.Equal("WHERE(Field9=0(1),Field7=0(x y))", TableNodeChild(doc, "H2", "SourceTableView"));
            Assert.Equal("SORTING(Field6)", TableNodeChild(doc, "L2", "SourceTableView"));
            Assert.Equal("SORTING(Field1) ORDER(1)", TableNodeChild(doc, "H3", "SourceTableView"));
        });
    }

    /// <summary>
    /// The left half resolves against the node's own table and the right against the
    /// LinkTable node's. <c>"Hdr Kind" = field(Kind)</c> is 4 on the left and 2 on the right,
    /// so resolving both against one table cannot produce the expected string.
    /// </summary>
    [Fact]
    public void LinkFields_ResolveEachHalfAgainstItsOwnTable()
    {
        WithTables(() =>
        {
            var doc = Emit(LinkedPortAl);
            Assert.Equal("Field1=FIELD(Field1),Field4=FIELD(Field2)", TableNodeChild(doc, "L1", "LinkFields"));
            Assert.Equal("Field1=FIELD(Field1)", TableNodeChild(doc, "L2", "LinkFields"));
            Assert.Equal("", TableNodeChild(doc, "H1", "LinkFields"));
        });
    }

    [Fact]
    public void Permissions_AreWrittenAsBcWritesThem()
    {
        WithTables(() =>
        {
            var doc = Emit(LinkedPortAl, new Dictionary<string, string>
            {
                ["Permissions"] = "tabledata \"XPE Hdr\" = rimd, tabledata \"XPE Ln\" = Rm, tabledata Integer = r",
            });
            Assert.Equal("TableData XPE Hdr=rimd,TableData XPE Ln=rm,TableData Integer=r",
                doc.SelectSingleNode("/XmlPort/Permissions")?.InnerText);
        });
    }

    [Theory]
    [InlineData("where(Kind = filter(A | \"C D\"), Flag = const(false))", "WHERE(Field2=1(0|2),Field7=0(0))")]
    [InlineData("where(I = filter(1 .. 5))", "WHERE(Field6=1(1..5))")]
    [InlineData("where(D = const(20240131D), Txt = filter('<>''x'''), \"No.\" = const('A-1'))",
        "WHERE(Field4=0(20240131D),Field8=1(<>'x'),Field1=0(A-1))")]
    [InlineData("sorting(I, Dec) order(ascending) where(Kind = const(\"C D\"), I = const(3))",
        "SORTING(Field6,Field5) ORDER(0) WHERE(Field2=0(2),Field6=0(3))")]
    [InlineData("where(Kind = filter(<> B), Txt = filter(''), I = filter(<> 0))",
        "WHERE(Field2=1(<>1),Field8=1(''),Field6=1(<>0))")]
    public void WhereValues_AreEncodedAsBcEncodesThem(string alView, string expected)
    {
        WithTables(() => Assert.Equal(expected, TableNodeChild(Emit(ValuePortAl(alView)), "A1", "SourceTableView")));
    }

    /// <summary>
    /// Shapes with no measured encoding refuse rather than write the AL text or a guess: an
    /// unknown field, an Enum value (ordinals the table does not carry), and a fractional
    /// Decimal (BC wrote <c>1,5</c> for <c>1.5</c> on an en_DK box, so its encoding is
    /// culture-dependent and not something to reproduce).
    /// </summary>
    [Theory]
    [InlineData("sorting(Nope)", "Nope")]
    [InlineData("where(EK = const(Five))", "EK")]
    [InlineData("where(Dec = filter(> 1.5))", "Dec")]
    [InlineData("where(Kind = const(Z))", "Z")]
    public void UnencodableSourceTableView_Refuses(string alView, string named)
    {
        WithTables(() =>
        {
            var ex = Assert.Throws<RecordPatches.XmlPortPropertyNotEncodableException>(
                () => RecordPatches.EmitXmlPortXmlFromAlForTests(
                    64475, "XPE Probe", new Dictionary<string, string>(), ValuePortAl(alView)));
            Assert.Contains(named, ex.Message);
            Assert.Contains("SourceTableView", ex.Message);
        });
    }

    [Fact]
    public void LinkFieldsWithoutAResolvableLinkTable_Refuses()
    {
        WithTables(() =>
        {
            var al = LinkedPortAl.Replace("LinkTable = H2;", "LinkTable = Nowhere;");
            var ex = Assert.Throws<RecordPatches.XmlPortPropertyNotEncodableException>(
                () => RecordPatches.EmitXmlPortXmlFromAlForTests(
                    64475, "XPE Probe", new Dictionary<string, string>(), al));
            Assert.Contains("LinkFields", ex.Message);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IDictionary ParsedTables() => (IDictionary)typeof(RecordPatches)
        .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void WithTables(Action act)
    {
        var tables = new[] { Hdr(), Ln(), Val() };
        var parsed = ParsedTables();
        foreach (var t in tables)
            Assert.False(parsed.Contains(t.TableId), $"table id {t.TableId} is already in _parsedTables; pick another range");
        try
        {
            foreach (var t in tables) parsed[t.TableId] = t;
            act();
        }
        finally
        {
            foreach (var t in tables) parsed.Remove(t.TableId);
        }
    }
}
