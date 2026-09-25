// XmlPortFieldListEncodingTests — #4602.
//
// A tableelement's CalcFields and RequestFilterFields are written as BC's emitter writes them:
// a comma-separated list of Field<n> ids, in the order the AL states, with no spaces. The AL
// property is RequestFilterFields; BC's document element (and the request page's filter-control
// attribute) is ReqFilterFields. Every expected string was produced by BC's own emitter
// (tools/metadata-ground-truth, BC 28.1.49838.53910) compiling the same AL against a table with
// the same field ids; see docs/xmlport-metadata-from-bc.md#calcfields-and-reqfilterfields.
using System.Collections;
using System.Reflection;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class XmlPortFieldListEncodingTests
{
    // Ids no other test uses, so a leak is obvious.
    private const int HdrId = 64602;
    private const int LnId = 64603;

    private static ParsedTable Hdr() => new(HdrId, "XFL Hdr", new List<ParsedField>
    {
        new(1, "No.", "Code", 20),
        new(2, "Kind", "Option", 0, OptionMembers: "A,B,C D"),
        new(5, "Amount (LCY)", "Decimal", 0),
        new(6, "I", "Integer", 0),
        new(7, "Name", "Text", 50),
        new(11, "Line Count", "Integer", 0),
        new(12, "Line Sum", "Decimal", 0),
        new(13, "Has Lines", "Boolean", 0),
        new(20, "Date Filter", "Date", 0),
    }, new List<int> { 1 });

    private static ParsedTable Ln() => new(LnId, "XFL Ln", new List<ParsedField>
    {
        new(1, "Doc No.", "Code", 20),
        new(2, "Line No.", "Integer", 0),
        new(3, "Amt", "Decimal", 0),
    }, new List<int> { 1, 2 });

    // The probe compiled with BC's compiler, with the table names swapped for this file's.
    private const string PortAl = """
        xmlport 64604 "XFL Probe"
        {
            schema
            {
                textelement(Root)
                {
                    tableelement(A1; "XFL Hdr") { CalcFields = "Line Count"; fieldattribute(N1; A1."No.") { } }
                    tableelement(A2; "XFL Hdr") { CalcFields = "Line Sum", "Line Count", "Has Lines"; RequestFilterFields = "No."; fieldattribute(N2; A2."No.") { } }
                    tableelement(A3; "XFL Hdr") { RequestFilterFields = Kind, "Amount (LCY)", Name, "Date Filter"; fieldattribute(N3; A3."No.") { } }
                    tableelement(A4; "XFL Hdr") { CalcFields = "Has Lines","Line Sum"; RequestFilterFields = I,"No."; fieldattribute(N4; A4."No.") { } }
                    tableelement(A5; "XFL Ln") { RequestFilterFields = "Doc No.", Amt; fieldattribute(N5; A5."Doc No.") { } }
                }
            }
        }
        """;

    [Fact]
    public void CalcFields_IsWrittenAsBcWritesIt()
    {
        WithTables(() =>
        {
            var doc = Emit(PortAl);
            Assert.Equal("Field11", TableNodeChild(doc, "A1", "CalcFields"));
            Assert.Equal("Field12,Field11,Field13", TableNodeChild(doc, "A2", "CalcFields"));
            Assert.Equal("", TableNodeChild(doc, "A3", "CalcFields"));
            Assert.Equal("Field13,Field12", TableNodeChild(doc, "A4", "CalcFields"));
            Assert.Equal("", TableNodeChild(doc, "A5", "CalcFields"));
        });
    }

    /// <summary>
    /// The AL states <c>RequestFilterFields</c>; the document element is <c>ReqFilterFields</c>.
    /// A5's list resolves against its own table (XFL Ln), where <c>Amt</c> is 3.
    /// </summary>
    [Fact]
    public void RequestFilterFields_IsWrittenAsBcWritesIt()
    {
        WithTables(() =>
        {
            var doc = Emit(PortAl);
            Assert.Equal("", TableNodeChild(doc, "A1", "ReqFilterFields"));
            Assert.Equal("Field1", TableNodeChild(doc, "A2", "ReqFilterFields"));
            Assert.Equal("Field2,Field5,Field7,Field20", TableNodeChild(doc, "A3", "ReqFilterFields"));
            Assert.Equal("Field6,Field1", TableNodeChild(doc, "A4", "ReqFilterFields"));
            Assert.Equal("Field1,Field3", TableNodeChild(doc, "A5", "ReqFilterFields"));
        });
    }

    /// <summary>
    /// BC states the same list on the request page's filter control for that tableelement,
    /// and omits the attribute when the AL states none (A1's control carried none).
    /// </summary>
    [Fact]
    public void RequestFilterFields_IsStatedOnTheRequestPageFilterControl()
    {
        WithTables(() =>
        {
            var controls = Emit(PortAl).SelectNodes(
                "/XmlPort/RequestPage//*[local-name()='Controls'][@DataColumnName]")!;
            // One control per tableelement, in schema order (A1..A5).
            var stated = controls.Cast<XmlElement>()
                .Select(c => c.HasAttribute("ReqFilterFields") ? c.GetAttribute("ReqFilterFields") : null)
                .ToArray();
            Assert.Equal(new string?[] { null, "Field1", "Field2,Field5,Field7,Field20", "Field6,Field1", "Field1,Field3" },
                stated);
        });
    }

    [Theory]
    [InlineData("CalcFields = \"Line Count\", Nope;", "CalcFields", "Nope")]
    [InlineData("RequestFilterFields = Nope;", "RequestFilterFields", "Nope")]
    [InlineData("RequestFilterFields = Kind, const(A);", "RequestFilterFields", "on 'A1'")]
    public void UnencodableFieldList_Refuses(string property, string named, string what)
    {
        WithTables(() =>
        {
            var al = PortAl.Replace("CalcFields = \"Line Count\";", property);
            var ex = Assert.Throws<RecordPatches.XmlPortPropertyNotEncodableException>(
                () => RecordPatches.EmitXmlPortXmlFromAlForTests(
                    64604, "XFL Probe", new Dictionary<string, string>(), al));
            Assert.Contains(named, ex.Message);
            Assert.Contains(what, ex.Message);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static XmlDocument Emit(string al)
    {
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(
            64604, "XFL Probe", new Dictionary<string, string>(), al);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc;
    }

    private static string? TableNodeChild(XmlDocument doc, string nodeName, string child)
    {
        foreach (XmlNode n in doc.SelectNodes("/XmlPort/Node")!)
            if (n.SelectSingleNode("NodeName")?.InnerText == nodeName)
                return n.SelectSingleNode(child)?.InnerText;
        throw new InvalidOperationException($"no node named {nodeName}");
    }

    private static IDictionary ParsedTables() => (IDictionary)typeof(RecordPatches)
        .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void WithTables(Action act)
    {
        var tables = new[] { Hdr(), Ln() };
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
