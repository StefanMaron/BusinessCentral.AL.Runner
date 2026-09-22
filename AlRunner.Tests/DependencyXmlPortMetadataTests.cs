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
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The source-file reader half (#3797, round 2). SymbolReference.json states an object's
/// source path SINGLE-encoded while the zip may name the same folder DOUBLE-encoded, and the
/// reader matched on suffix only — so those objects read as "this .app ships no source".
///
/// <para>Measured on 28.1.49838.53910: System Application states
/// <c>Permission%20Sets/src/xmlports/…</c> for entries the zip names
/// <c>src/Permission%2520Sets/src/xmlports/…</c> (<c>%25</c> is a literal <c>%</c>, so
/// <c>%2520</c> decodes once to <c>%20</c>). 4 of System Application's 4 xmlports were
/// affected and 0 of Base Application's 40, which is why the whole-population count was 40 of
/// 44 before this and 44 of 44 after.</para>
///
/// <para>Synthetic .apps rather than platform artifacts, so these run anywhere and state the
/// property rather than a build's contents (no Base Application floor —
/// .claude/rules/no-base-app-in-csharp-tests.md).</para>
/// </summary>
public class BcAppSourceFileEncodingTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-3797-src-encoding");

    public BcAppSourceFileEncodingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <param name="zipEntryPath">How the ZIP names the file — the packaging's spelling.</param>
    private string WriteApp(string name, string zipEntryPath, string content)
    {
        var path = Path.Combine(_root, name);
        using (var fs = new FileStream(path, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry(zipEntryPath);
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(content);
        }
        return path;
    }

    /// <summary>
    /// The defect: stated single-encoded, packaged double-encoded. Before the fix this answered
    /// null, which every caller reads as "no source shipped".
    /// </summary>
    [Fact]
    public void DoubleEncodedZipEntry_IsFoundFromTheSingleEncodedStatedPath()
    {
        var app = WriteApp(
            "double-encoded.app",
            zipEntryPath: "src/Permission%2520Sets/src/xmlports/Probe.XmlPort.al",
            content: "xmlport 9862 \"Probe\" { }");

        var read = BcAppSymbolCache.TryReadSourceFile(
            app, "Permission%20Sets/src/xmlports/Probe.XmlPort.al");

        Assert.NotNull(read);
        Assert.Contains("xmlport 9862", read!);
    }

    /// <summary>
    /// The control, and the half that makes the fix a discrimination rather than a widening:
    /// an ordinary path must still resolve by the plain suffix match. A re-encoding probe that
    /// ran unconditionally, or replaced the first match, would break every app that packages
    /// its paths the normal way — which is 40 of the 44 xmlports measured.
    /// </summary>
    [Fact]
    public void PlainZipEntry_StillResolves_WithNoPercentInThePath()
    {
        var app = WriteApp(
            "plain.app",
            zipEntryPath: "src/Bank/Probe.XmlPort.al",
            content: "xmlport 1230 \"Probe\" { }");

        var read = BcAppSymbolCache.TryReadSourceFile(app, "Bank/Probe.XmlPort.al");

        Assert.NotNull(read);
        Assert.Contains("xmlport 1230", read!);
    }

    /// <summary>
    /// A path carrying a percent that the zip spells the SAME way must resolve on the first
    /// match, not the re-encoded one — so the fallback cannot mask a correctly-packaged app
    /// whose folder name legitimately contains an encoded character.
    /// </summary>
    [Fact]
    public void SingleEncodedZipEntry_ResolvesWithoutTheFallback()
    {
        var app = WriteApp(
            "single-encoded.app",
            zipEntryPath: "src/Permission%20Sets/Probe.XmlPort.al",
            content: "xmlport 9999 \"Probe\" { }");

        var read = BcAppSymbolCache.TryReadSourceFile(
            app, "Permission%20Sets/Probe.XmlPort.al");

        Assert.NotNull(read);
        Assert.Contains("xmlport 9999", read!);
    }

    /// <summary>
    /// And a genuinely absent file still answers null. A symbols-only .app is a legitimate
    /// package shape, so the reader must not start finding things that are not there — the
    /// fallback widens WHICH spellings match, never whether a miss becomes a hit.
    /// </summary>
    [Fact]
    public void AbsentSourceFile_StillAnswersNull_EvenWithAPercentInThePath()
    {
        var app = WriteApp(
            "absent.app",
            zipEntryPath: "src/Other/Unrelated.XmlPort.al",
            content: "xmlport 1 \"Other\" { }");

        Assert.Null(BcAppSymbolCache.TryReadSourceFile(
            app, "Permission%20Sets/src/xmlports/Probe.XmlPort.al"));
        Assert.Null(BcAppSymbolCache.TryReadSourceFile(app, "Bank/NotShipped.XmlPort.al"));
    }
}

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
    // ── #4467: the three node-level defects the metadata-equivalence harness exposed ───────
    //
    // None of these could be seen before #4467, because the harness rendered the runner's
    // xmlport side through a projection that wrote only (ID, Name) — so no node was compared.
    // Wiring that projection to this derivation is what made them measurable.

    /// <summary>
    /// A tree whose two plausible numbering orders DISAGREE — which the suite's own
    /// ExportPortAl fixture cannot express, because at one tableelement deep they coincide.
    ///
    /// <para>Two sibling tableelements under one root, the first having children. Depth-first
    /// declaration order numbers Second as 6; BC numbers it 3, because it numbers a node's
    /// direct children as a contiguous run BEFORE descending into any of them.</para>
    /// </summary>
    private const string TwoSiblingTablesAl = """
        xmlport 61610 "XPDDep Sibling Port"
        {
            Format = Xml;
            Direction = Export;

            schema
            {
                textelement(Root)
                {
                    tableelement(First; "XPDDep Header")
                    {
                        XmlName = 'First';
                        fieldelement(FirstNo; First."No.") { }
                        fieldelement(FirstDesc; First.Description) { }
                    }
                    tableelement(Second; "XPDDep Header")
                    {
                        XmlName = 'Second';
                        fieldelement(SecondNo; Second."No.") { }
                    }
                }
            }
        }
        """;

    /// <summary>
    /// BC numbers schema nodes BREADTH-FIRST BY PARENT while emitting them depth-first, so a
    /// tableelement declared after a sibling's subtree takes the LOWER sequence number.
    ///
    /// <para>Measured against BC's own emitted documents for all four of System Application's
    /// xmlports on 28.1.49838.53910 — 9001, 9862, 9863 and 9864, 91 nodes — where this rule
    /// reproduces every node id exactly and plain declaration order reproduces 69 of them
    /// wrongly. The clearest instance is XmlPort 9862, where <c>Permission</c> is the twelfth
    /// node written and carries sequence 9, while <c>PermissionSetRel</c>'s three children,
    /// written before it, carry 10, 11 and 12.</para>
    ///
    /// <para>Not cosmetic: BC resolves a node's parent by matching ParentID against another
    /// node's ID, so getting the sequence wrong reparents nodes. The ParentID assertions below
    /// are what pin that, and they are the reason this is asserted on a tree with two levels
    /// rather than on the sequence numbers alone.</para>
    /// </summary>
    [Fact]
    public void NodeSequence_IsBreadthFirstByParent_NotDeclarationOrder()
    {
        var nodes = Nodes(EmitExportPort(al: TwoSiblingTablesAl, id: 61610));

        // Document order is unchanged — depth-first, as declared.
        Assert.Equal(
            new[] { "Root", "First", "FirstNo", "FirstDesc", "Second", "SecondNo" },
            nodes.Cast<XmlNode>().Select(n => Child(n, "NodeName")).ToArray());

        // Numbering is NOT. Root=1; its two children First=2 and Second=3 take the contiguous
        // run; only then do First's children get 4 and 5, and Second's 6.
        string Seq(int i) => Child(nodes[i]!, "ID")!.Substring(10, 4);
        Assert.Equal("0001", Seq(0));   // Root
        Assert.Equal("0002", Seq(1));   // First
        Assert.Equal("0004", Seq(2));   // First/FirstNo
        Assert.Equal("0005", Seq(3));   // First/FirstDesc
        Assert.Equal("0003", Seq(4));   // Second — declared last, numbered third
        Assert.Equal("0006", Seq(5));   // Second/SecondNo

        // Declaration order would have given Second "0005"; that it does not is the claim.
        Assert.NotEqual("0005", Seq(4));

        // And the linkage still resolves: each bound child's ParentID is its own
        // tableelement's ID, which is the property the sequence exists to serve.
        Assert.Equal(Child(nodes[1]!, "ID"), Child(nodes[2]!, "ParentID"));
        Assert.Equal(Child(nodes[1]!, "ID"), Child(nodes[3]!, "ParentID"));
        Assert.Equal(Child(nodes[4]!, "ID"), Child(nodes[5]!, "ParentID"));
    }

    /// <summary>
    /// AL spells the property <c>UseTemporary</c>; BC's document element is <c>Temporary</c>.
    /// Reading the ELEMENT's name off the AL properties answered the default on every node.
    ///
    /// <para>Measured on XmlPort 9864, where all 6 table nodes state <c>UseTemporary = true</c>
    /// and BC's document states <c>Temporary=1</c> for all 6, against which the old read
    /// answered 0 six times (12 harness differences with the backing field).</para>
    ///
    /// <para>Both directions, because a one-sided test cannot tell a fix from a changed
    /// default: stated-true must give 1, and stated nowhere must still give 0.</para>
    /// </summary>
    [Fact]
    public void Temporary_ReadsALsUseTemporarySpelling()
    {
        var stated = Nodes(EmitExportPort(
            al: ExportPortAl.Replace("XmlName = 'Header';", "XmlName = 'Header'; UseTemporary = true;")));
        Assert.Equal("1", Child(stated[1]!, "Temporary"));

        // The fixture as written states neither spelling, so BC's own default stands.
        Assert.Equal("0", Child(Nodes(EmitExportPort())[1]!, "Temporary"));
    }

    /// <summary>
    /// <c>LinkTable</c> names the ancestor tableelement a linked one binds to. It was never
    /// written, and BC writes the AL node name UNQUOTED — measured on XmlPort 9001, whose
    /// document states <c>LinkTable=Security Group</c> for the AL's
    /// <c>LinkTable = "Security Group"</c>.
    ///
    /// <para>Absent when not stated rather than empty, because BC omits the element entirely on
    /// an unlinked tableelement — 4 of 6 table nodes state it on 9864 — and an empty one would
    /// claim a link to the node named "".</para>
    /// </summary>
    [Fact]
    public void LinkTable_IsWrittenUnquoted_AndOmittedWhenNotStated()
    {
        var quoted = Nodes(EmitExportPort(
            al: ExportPortAl.Replace("XmlName = 'Header';", "XmlName = 'Header'; LinkTable = \"Security Group\";")));
        Assert.Equal("Security Group", Child(quoted[1]!, "LinkTable"));

        var bare = Nodes(EmitExportPort(
            al: ExportPortAl.Replace("XmlName = 'Header';", "XmlName = 'Header'; LinkTable = Root;")));
        Assert.Equal("Root", Child(bare[1]!, "LinkTable"));

        Assert.Null(Child(Nodes(EmitExportPort())[1]!, "LinkTable"));
    }

    /// <summary>
    /// The four variable-text separators BC's emitter states unconditionally and AL states
    /// nowhere — measured identical on all four of System Application's xmlports.
    ///
    /// <para><c>RecordSeparator</c>/<c>TableSeparator</c> carry BC's own <c>&lt;NewLine&gt;</c>
    /// token rather than a literal newline, because BC's reader decodes the token; writing a
    /// real newline would make the reader see a newline where it expects the escape.</para>
    /// </summary>
    [Fact]
    public void FormatSeparators_AreWrittenAtBCsOwnDefaults()
    {
        var doc = EmitExportPort();
        Assert.Equal("\"", doc.SelectSingleNode("/XmlPort/FieldDelimiter")?.InnerText);
        Assert.Equal(",", doc.SelectSingleNode("/XmlPort/FieldSeparator")?.InnerText);
        Assert.Equal("<NewLine>", doc.SelectSingleNode("/XmlPort/RecordSeparator")?.InnerText);
        Assert.Equal("<NewLine><NewLine>", doc.SelectSingleNode("/XmlPort/TableSeparator")?.InnerText);

        // A declared value still wins over the default.
        var stated = EmitExportPort(properties: new Dictionary<string, string> { ["FieldSeparator"] = ";" });
        Assert.Equal(";", stated.SelectSingleNode("/XmlPort/FieldSeparator")?.InnerText);
    }

    /// <summary>
    /// <c>Permissions</c> is written only when the object declares it, because BC omits the
    /// element entirely otherwise — 1 of System Application's 4 xmlports states it. The VALUE
    /// is still BC's canonical form away from the symbol file's AL text, which is #4471.
    /// </summary>
    [Fact]
    public void Permissions_IsWrittenWhenDeclared_AndOmittedOtherwise()
    {
        var stated = EmitExportPort(
            properties: new Dictionary<string, string> { ["Permissions"] = "tabledata \"Security Group\" = r" });
        Assert.Equal("tabledata \"Security Group\" = r",
            stated.SelectSingleNode("/XmlPort/Permissions")?.InnerText);

        Assert.Null(EmitExportPort().SelectSingleNode("/XmlPort/Permissions"));
    }

}
