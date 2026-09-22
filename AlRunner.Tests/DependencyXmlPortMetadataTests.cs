// Mechanism tests for RecordPatches.DependencyXmlPortMetadata (#3797) — the runner-side half
// of the claim whose AL-observable half is tests/runner-extras/xmlport-precompiled-dep-metadata.
//
// WHY BOTH HALVES EXIST, since neither is the other's duplicate. The AL suite proves that BC's
// own XmlPort engine accepts the synthesized document and exports every node — which is the
// user-visible claim, and the only place BC's real parser is in the loop. It cannot cheaply
// prove WHICH VALUES the document carries: reading a bound node's exported value needs a
// persisted row, and the export then depends on the install/persistence closure rather than on
// the node schema this issue is about.
//
// These tests take the other half: they assert the emitted document's own content —
// SourceTable, SourceField, DataType, ParentID, Indentation and the object-level properties —
// against xmlport AL parsed by the same code path the real one uses. A port whose bindings
// were derived wrongly emits different text HERE, deterministically, with no company and no
// BC service tier.
//
// The emitter is driven through EmitXmlPortXmlFromAlForTests, which takes AL SOURCE rather
// than a pre-built node list on purpose: a test handed a node list would pass with the schema
// parser completely broken, which is the defect the whole issue is about.
using System.Collections.Generic;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class DependencyXmlPortMetadataTests
{
    // The fixture xmlport, matching tests/runner-extras/xmlport-precompiled-dep-metadata's dep
    // source byte for byte in the parts that matter. Kept here rather than read from the .app
    // so these tests need no fixture file on disk.
    private const string ExportPortAl = """
        xmlport 61602 "XPDDep Export Port"
        {
            Format = Xml;
            Direction = Export;

            schema
            {
                textelement(Root)
                {
                    tableelement(Header; "XPDDep Header")
                    {
                        XmlName = 'Header';
                        fieldelement(No; Header."No.") { }
                        fieldelement(Description; Header.Description) { }
                    }
                }
            }
        }
        """;

    private static XmlDocument EmitExportPort(
        Dictionary<string, string>? properties = null, string? al = null, int id = 61602)
    {
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(
            id, "XPDDep Export Port", properties ?? new Dictionary<string, string>(), al ?? ExportPortAl);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc;
    }

    private static XmlNodeList Nodes(XmlDocument doc) => doc.SelectNodes("/XmlPort/Node")!;

    private static string? Child(XmlNode node, string name) => node.SelectSingleNode(name)?.InnerText;

    /// <summary>
    /// The whole node tree, in declaration order, with the nesting the AL states. Four nodes
    /// from four AL keywords — a parser that dropped the recursive descent would emit one.
    /// </summary>
    [Fact]
    public void ExportPort_EmitsEveryDeclaredNode_InOrderWithItsNesting()
    {
        var nodes = Nodes(EmitExportPort());
        Assert.Equal(4, nodes.Count);

        Assert.Equal("Root", Child(nodes[0]!, "NodeName"));
        Assert.Equal("0", Child(nodes[0]!, "Indentation"));
        // XmlName = 'Header' is an AL STRING literal. Its quotes reaching NodeName made BC's
        // exporter refuse the element name outright ("Invalid name character in ''Header''"),
        // so this assertion is pinned to the unquoted text rather than to mere presence.
        Assert.Equal("Header", Child(nodes[1]!, "NodeName"));
        Assert.Equal("1", Child(nodes[1]!, "Indentation"));
        Assert.Equal("No", Child(nodes[2]!, "NodeName"));
        Assert.Equal("2", Child(nodes[2]!, "Indentation"));
        Assert.Equal("Description", Child(nodes[3]!, "NodeName"));
        Assert.Equal("2", Child(nodes[3]!, "Indentation"));
    }

    /// <summary>
    /// Each AL keyword maps to its own (NodeType, SourceType) pair. Collapsing any two of the
    /// five would still produce four nodes, which is why the pairs are asserted rather than
    /// the count.
    /// </summary>
    [Fact]
    public void ExportPort_MapsEachAlKeywordToItsNodeTypeAndSourceType()
    {
        var nodes = Nodes(EmitExportPort());

        Assert.Equal("Element", Child(nodes[0]!, "NodeType"));
        Assert.Equal("Text", Child(nodes[0]!, "SourceType"));

        Assert.Equal("Element", Child(nodes[1]!, "NodeType"));
        Assert.Equal("Table", Child(nodes[1]!, "SourceType"));

        Assert.Equal("Element", Child(nodes[2]!, "NodeType"));
        Assert.Equal("Field", Child(nodes[2]!, "SourceType"));
    }

    /// <summary>
    /// An attribute is a different NodeType AND takes Occurrence where an element takes
    /// Max/MinOccurs — a different element name, not a different spelling. Measured against
    /// BC's own emitted document for a probe xmlport declaring all five keywords.
    /// </summary>
    [Fact]
    public void Attributes_AreNodeTypeAttribute_AndCarryOccurrenceRatherThanOccursRange()
    {
        var doc = EmitExportPort(al: """
            xmlport 61603 "XPD Probe Port"
            {
                schema
                {
                    textelement(Root)
                    {
                        textattribute(RootAttr) { }
                    }
                }
            }
            """, id: 61603);
        var nodes = Nodes(doc);
        Assert.Equal(2, nodes.Count);

        var attr = nodes[1]!;
        Assert.Equal("RootAttr", Child(attr, "NodeName"));
        Assert.Equal("Attribute", Child(attr, "NodeType"));
        Assert.Equal("Required", Child(attr, "Occurrence"));
        Assert.Null(attr.SelectSingleNode("MaxOccurs"));
        Assert.Null(attr.SelectSingleNode("MinOccurs"));

        // The element it sits inside takes the opposite shape, so this is a discrimination
        // rather than a one-sided claim.
        Assert.Null(nodes[0]!.SelectSingleNode("Occurrence"));
        Assert.NotNull(nodes[0]!.SelectSingleNode("MaxOccurs"));
    }

    /// <summary>
    /// The value-binding half the AL suite cannot cheaply reach: a bound node names the FIELD
    /// it binds, in BC's own <c>Record::Field</c> spelling, with the AL quoting removed.
    ///
    /// <para><c>Header."No."</c> -> <c>Header::No.</c> is the case that discriminates: a
    /// splitter that split on every dot would answer <c>Header::No</c> and lose the field
    /// entirely, and one that kept the quotes would answer <c>Header::"No."</c>, which matches
    /// no field on the table.</para>
    /// </summary>
    [Fact]
    public void BoundNodes_NameTheirFieldInBcsRecordColonColonFieldSpelling()
    {
        var nodes = Nodes(EmitExportPort());

        Assert.Equal("Header::No.", Child(nodes[2]!, "SourceField"));
        Assert.Equal("Header::Description", Child(nodes[3]!, "SourceField"));
        // A text node binds nothing and must say so by omission, not by an empty element.
        Assert.Null(nodes[0]!.SelectSingleNode("SourceField"));
    }

    /// <summary>
    /// A bound node's <c>DataType</c> is the AL type WITHOUT its length suffix.
    ///
    /// <para>Not cosmetic: BC's MetaXmlPort reader takes DataType through a case-sensitive
    /// <c>Enum.Parse</c> over <c>NavType</c>, which has no <c>Code[20]</c> member, so the
    /// declared spelling throws <c>ArgumentException: Requested value 'Code[20]' was not
    /// found</c> and kills the whole document over one node — measured on this very fixture
    /// while implementing #3797.</para>
    ///
    /// <para>Skipped rather than failed when the fixture's table is not resolvable in this
    /// process: DataType is the one element that needs the runner's table inventory, so a bare
    /// emit with no dependency registered legitimately omits it. Asserting the element's
    /// ABSENCE would be the wrong claim (that is the honest answer for an unknown table), and
    /// asserting its presence unconditionally would fail for a correct reason. What is pinned
    /// unconditionally is the property that matters — whatever is written must never carry a
    /// bracket, because that is the value BC cannot parse.</para>
    /// </summary>
    [Fact]
    public void BoundNodeDataType_NeverCarriesTheAlLengthSuffix()
    {
        var nodes = Nodes(EmitExportPort());
        foreach (var index in new[] { 2, 3 })
        {
            var dataType = Child(nodes[index]!, "DataType");
            if (dataType == null) continue;   // table not resolvable here; see the summary
            Assert.DoesNotContain("[", dataType);
            // And it is a real NavType member rather than arbitrary text, which is the other
            // half of what Enum.Parse demands.
            Assert.Matches("^[A-Za-z]+$", dataType);
        }
    }

    /// <summary>
    /// A bound node whose enclosing tableelement names a table the runner cannot resolve omits
    /// <c>DataType</c> rather than guessing one. An unparseable value costs the whole document;
    /// an absent one costs a node its declared type, so omission is the strictly safer answer
    /// and the one this pins.
    /// </summary>
    [Fact]
    public void BoundNodeUnderAnUnresolvableTable_OmitsDataTypeRatherThanGuessing()
    {
        var nodes = Nodes(EmitExportPort(al: """
            xmlport 61602 "XPDDep Export Port"
            {
                schema
                {
                    textelement(Root)
                    {
                        tableelement(Header; "No Such Table Anywhere 61602")
                        {
                            fieldelement(No; Header."No.") { }
                        }
                    }
                }
            }
            """));

        var tableElement = nodes[1]!;
        // The table is unresolvable, so SourceTable is unstated rather than written as 0 — a
        // fabricated id would bind the element to an unrelated table.
        Assert.Null(tableElement.SelectSingleNode("SourceTable"));
        // The bound node keeps its field reference (that IS recoverable) and drops only the
        // type, which is not.
        Assert.Equal("Header::No.", Child(nodes[2]!, "SourceField"));
        Assert.Null(nodes[2]!.SelectSingleNode("DataType"));
    }

    /// <summary>
    /// A bound node's ParentID resolves to the enclosing tableelement's own emitted ID, while
    /// a text node's is the all-zero GUID — which is what BC's own emitter writes, measured on
    /// a probe port whose textelement nested inside a tableelement carried the all-zero id
    /// while its fieldelement sibling carried the tableelement's.
    /// </summary>
    [Fact]
    public void BoundNodes_ParentIdIsTheEnclosingTableElement_TextNodesAreUnparented()
    {
        var nodes = Nodes(EmitExportPort());
        var tableElementId = Child(nodes[1]!, "ID");

        Assert.Equal(tableElementId, Child(nodes[2]!, "ParentID"));
        Assert.Equal(tableElementId, Child(nodes[3]!, "ParentID"));
        Assert.Equal("{00000000-0000-0000-0000-000000000000}", Child(nodes[0]!, "ParentID"));
        Assert.Equal("{00000000-0000-0000-0000-000000000000}", Child(nodes[1]!, "ParentID"));
    }

    /// <summary>
    /// The node id scheme BC's emitter uses: <c>{0000XXXX-NNNN-0000-0F00-0000836BD2D2}</c>,
    /// XXXX the xmlport id in hex and NNNN the 1-based node sequence. Identical on two distinct
    /// BC 28 builds (28.1.49838.53910 and 28.3.52162.54347).
    ///
    /// <para>The xmlport id being PART of the id is the property that matters, not the exact
    /// constant: BC resolves a node's parent by matching ParentID against another node's ID, so
    /// a bare sequence would collide across two xmlports in one run and reparent nodes between
    /// ports. The second assertion is what pins that.</para>
    /// </summary>
    [Fact]
    public void NodeIds_EncodeTheXmlPortIdAndTheNodeSequence()
    {
        var nodes = Nodes(EmitExportPort());
        Assert.Equal("{0000F0A2-0001-0000-0F00-0000836BD2D2}", Child(nodes[0]!, "ID"));
        Assert.Equal("{0000F0A2-0002-0000-0F00-0000836BD2D2}", Child(nodes[1]!, "ID"));
        Assert.Equal("{0000F0A2-0004-0000-0F00-0000836BD2D2}", Child(nodes[3]!, "ID"));

        // A different xmlport's node 1 must not share an id with this one's node 1.
        var other = Nodes(EmitExportPort(al: ExportPortAl.Replace("61602", "61604"), id: 61604));
        Assert.NotEqual(Child(nodes[0]!, "ID"), Child(other[0]!, "ID"));
    }

    /// <summary>
    /// The four properties #3797 measured the runner answering wrongly. Each is asserted in
    /// BOTH directions — stated and not stated — because the defect was that a stated value
    /// was dropped in favour of a default, which a one-sided test cannot tell from a fix.
    /// </summary>
    [Fact]
    public void StatedObjectProperties_AreWrittenRatherThanDefaulted()
    {
        var doc = EmitExportPort(new Dictionary<string, string>
        {
            ["Direction"] = "Export",
            ["Encoding"] = "UTF8",
            ["PreserveWhiteSpace"] = "1",
            ["UseRequestPage"] = "0",
        });

        Assert.Equal("Export", doc.SelectSingleNode("/XmlPort/Direction")!.InnerText);
        // The symbol file spells it UTF8; BC's document spells it UTF-8.
        Assert.Equal("UTF-8", doc.SelectSingleNode("/XmlPort/Encoding")!.InnerText);
        Assert.Equal("1", doc.SelectSingleNode("/XmlPort/PreserveWhiteSpace")!.InnerText);
        Assert.Equal("0", doc.SelectSingleNode("/XmlPort/UseRequestForm")!.InnerText);
    }

    /// <summary>
    /// The other direction: an xmlport stating none of them gets AL's own defaults, which are
    /// what BC's emitter wrote for a probe port declaring none. <c>UseRequestPage</c> is the
    /// one that is TRUE by default, so a writer defaulting every boolean to false would pass
    /// the three around it and fail here.
    /// </summary>
    [Fact]
    public void UnstatedObjectProperties_TakeAlsOwnDefaults_IncludingTheTrueOne()
    {
        var doc = EmitExportPort();

        Assert.Equal("Both", doc.SelectSingleNode("/XmlPort/Direction")!.InnerText);
        Assert.Equal("UTF-16", doc.SelectSingleNode("/XmlPort/Encoding")!.InnerText);
        Assert.Equal("0", doc.SelectSingleNode("/XmlPort/PreserveWhiteSpace")!.InnerText);
        Assert.Equal("1", doc.SelectSingleNode("/XmlPort/UseRequestForm")!.InnerText);
    }

    /// <summary>
    /// SymbolReference.json writes booleans as <c>"1"</c> and never as the word, so a reader
    /// matching only "true" answers false for every xmlport that declares one — the measured
    /// shape of #3790 on the codeunit side, which is why this routes through SymbolBoolValue.
    /// Both spellings must reach the same document.
    /// </summary>
    [Theory]
    [InlineData("1", "1")]
    [InlineData("true", "1")]
    [InlineData("True", "1")]
    [InlineData("0", "0")]
    [InlineData("false", "0")]
    public void BooleanProperties_ReadBothSpellingsTheSymbolFileUses(string stated, string expected)
    {
        var doc = EmitExportPort(new Dictionary<string, string> { ["PreserveWhiteSpace"] = stated });
        Assert.Equal(expected, doc.SelectSingleNode("/XmlPort/PreserveWhiteSpace")!.InnerText);
    }

    /// <summary>
    /// The RequestPage subtree is not optional: BC's compiled xmlport class declares a nested
    /// RequestPage whose ctor takes a MasterPage and InitializeComponent calls it
    /// unconditionally, so a document without it kills the xmlport's own constructor with a
    /// bare NullReferenceException — measured on fixture xmlport 61602.
    ///
    /// <para>PageDefinition must be the FIRST child, because BC reads
    /// <c>val.FirstChild</c> rather than a child found by name.</para>
    /// </summary>
    [Fact]
    public void RequestPage_IsPresent_AndPageDefinitionIsItsFirstChild()
    {
        var doc = EmitExportPort();
        var requestPage = doc.SelectSingleNode("/XmlPort/RequestPage");
        Assert.NotNull(requestPage);
        Assert.Equal("PageDefinition", requestPage!.FirstChild!.LocalName);
    }

    /// <summary>
    /// An xmlport whose node tree cannot be recovered is NOT answered with a schema-less
    /// document. That is the refusal tests/expectations/oos-xmlport-precompiled.json's Note
    /// warned about: an empty document reads to BC as "this port has an empty schema" and lets
    /// the port silently export nothing, which is worse than the loud out-of-scope throw the
    /// caller raises when this answers null.
    ///
    /// <para>A symbols-only .app — one shipping SymbolReference.json and no src/ — is exactly
    /// this case, and it is a legitimate package shape rather than an error.</para>
    /// </summary>
    [Fact]
    public void XmlPortWithNoRecoverableSchema_AnswersNull_RatherThanAnEmptyDocument()
    {
        // A schema block with no nodes in it: the parse succeeds and finds nothing, which is
        // the same answer an unreadable source file produces.
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(
            61602, "XPDDep Export Port", new Dictionary<string, string>(), """
            xmlport 61602 "XPDDep Export Port"
            {
                Format = Xml;
                schema { }
            }
            """);
        Assert.Null(xml);
    }

    /// <summary>
    /// And the id is matched, not assumed: a source file declaring a DIFFERENT xmlport answers
    /// null rather than handing back that other port's schema under the requested id. One
    /// source file may declare several objects, so "the only xmlport in the file" is not a
    /// safe identification.
    /// </summary>
    [Fact]
    public void SourceDeclaringADifferentXmlPort_AnswersNull_RatherThanTheWrongSchema()
    {
        var xml = RecordPatches.EmitXmlPortXmlFromAlForTests(
            61602, "XPDDep Export Port", new Dictionary<string, string>(),
            ExportPortAl.Replace("61602", "61699"));
        Assert.Null(xml);
    }
}
