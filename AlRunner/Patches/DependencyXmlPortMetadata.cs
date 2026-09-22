// DependencyXmlPortMetadata — runtime metadata XML for xmlports that live in a PRECOMPILED
// dependency .app, which the runner never source-compiles.
//
// THE GAP (#3797)
//   NavXmlPort.BeginInitialization -> MetadataProvider.GetXmlPortMetadata ->
//   NCLMetaXmlPort.get_XmlMetadata -> NCLMetaApplicationObject.GetMetadataFromLoader ->
//   INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata is BC's only route to an xmlport's
//   node schema. RunnerXmlMetadataLoader answered it from AlXmlPortMetadataRegistry, which
//   the EMIT pipeline fills — so it held every xmlport the runner compiled and none that it
//   did not, and all 44 precompiled xmlports AL can see refused there with
//   not-yet-implemented (measured on BC 28.1.49838.53910 against a real Base Application).
//
//   This is the sibling of DependencyReportMetadata.cs, which had the same gap for reports,
//   and it is deliberately built to the same shape.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   Two sources, both shipped inside the .app itself:
//
//   1. SymbolReference.json — the xmlport's Id, Name and its object-level Properties
//      (Direction, Format, Encoding, UseRequestPage, PreserveWhiteSpace, Caption, ...).
//      Measured on 28.1.49838.53910: the symbol file states an xmlport's Properties and
//      Variables and NO node tree at all (top-level "XmlPorts": [] with all 40 Base
//      Application xmlports nested under "Namespaces", each carrying only Id, Name,
//      Properties, Variables, Methods and ReferenceSourceFileName).
//
//   2. The xmlport's own AL source, read back out of the .app's embedded src/ tree, parsed
//      with BC'S OWN AL PARSER (NavSyntax.XmlPortSyntax.XmlPortSchema). That is where the
//      node tree comes from, because the symbol file does not state one. Measured on the
//      same build: all 40 Base Application xmlports state a ReferenceSourceFileName and all
//      40 of those files are present in the .app, carrying 3,904 schema nodes between them.
//
//   NOT a hand-written AL schema reader: RecordPatches.AlXmlPortParser already parses AL
//   through BC's compiler syntax tree, and XmlPortNodeSyntax exposes the whole recursive
//   schema (Name, PropertyList, Schema, plus SourceField/SourceTable on the bound kinds).
//   Reusing it is precompiled-dll-respect.md's "reuse before you re-implement".
//
// FAITHFULNESS
//   The emitted XML follows the shape BC's own Compilation.Emit produces for a
//   source-compiled xmlport (captured in AlXmlPortMetadataRegistry; the ground truth this
//   was written against is in docs/xmlport-metadata-from-bc.md). Properties the symbol file
//   does not state are written at AL's own documented default, never at a convenient one,
//   and each default is named in docs/xmlport-metadata-from-bc.md#object-properties.
//
//   AN XMLPORT WHOSE NODE TREE CANNOT BE RECOVERED IS NOT ANSWERED. Null is returned and
//   the caller's out-of-scope throw stands, because a document with no <Node> reads to BC as
//   "this port has an empty schema" and would let the port silently export nothing — the
//   exact failure tests/expectations/oos-xmlport-precompiled.json's Note warns about. A
//   symbols-only .app (no src/) is that case, and it keeps refusing.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly ConcurrentDictionary<int, string?> _depXmlPortMetadataXml = new();

    /// <summary>
    /// Runtime metadata XML for an xmlport declared by a precompiled dependency, or null
    /// when no dependency .app describes it OR its node tree cannot be recovered.
    ///
    /// <para>Result cached per id, INCLUDING the null, and dropped by
    /// <see cref="InvalidateBcAppIndexes"/> whenever the registered .app set changes — the
    /// same memo shape and the same reasoning (#2889) as
    /// <see cref="TryBuildDependencyReportMetadata"/>.</para>
    /// </summary>
    internal static string? TryBuildDependencyXmlPortMetadata(int xmlPortId)
        => _depXmlPortMetadataXml.GetOrAdd(xmlPortId, BuildDependencyXmlPortMetadata);

    private static string? BuildDependencyXmlPortMetadata(int xmlPortId)
    {
        var found = FindDependencyXmlPortSymbol(xmlPortId);
        if (found == null) return null;
        var (appPath, port) = found.Value;

        var schema = TryReadXmlPortSchema(appPath, port);
        // The refusal this file exists to preserve. See the header: an empty document is a
        // claim that the port has no schema, which is worse than the loud refusal it would
        // replace.
        if (schema == null || schema.Count == 0)
        {
            Console.Error.WriteLine(
                $"[RecordPatches] dependency xmlport metadata: XmlPort {xmlPortId} \"{port.Name}\" "
                + $"declares no recoverable node schema in {Path.GetFileName(appPath)} "
                + $"(ReferenceSourceFileName={port.ReferenceSourceFileName ?? "<none>"}); "
                + "refusing rather than handing back an empty document (#3797)");
            return null;
        }

        var xml = EmitXmlPortXml(port, schema);
        Console.Error.WriteLine(
            $"[RecordPatches] dependency xmlport metadata: synthesized XmlPort {xmlPortId} "
            + $"\"{port.Name}\" from {Path.GetFileName(appPath)} ({schema.Count} node(s))");
        return xml;
    }

    /// <summary>
    /// NOT swallowed, for the reason <see cref="FindDependencyReportSymbol"/> states: null
    /// here means "no loaded dependency declares xmlport N", and the caller turns that into a
    /// refusal naming the xmlport. A symbol READ failure throws instead of reading as absent.
    /// </summary>
    private static (string AppPath, BcAppSymbolCache.XmlPortSymbol Port)? FindDependencyXmlPortSymbol(int xmlPortId)
    {
        foreach (var (appPath, symbols) in
                 EnumerateRegisteredBcAppSymbols("xmlports (dependency xmlport metadata)"))
            foreach (var x in symbols.XmlPorts ?? new List<BcAppSymbolCache.XmlPortSymbol>())
                if (x.Id == xmlPortId)
                    return (appPath, x);
        return null;
    }

    // ── the node tree, from the .app's own AL source through BC's parser ──────

    /// <summary>
    /// One xmlport schema node, flattened in declaration order with its nesting depth — the
    /// shape BC's emitted document uses, where <c>&lt;Node&gt;</c> elements are flat siblings
    /// carrying <c>Indentation</c> and a <c>ParentID</c>.
    /// </summary>
    /// <param name="Kind">Which of AL's five schema keywords declared it.</param>
    /// <param name="Name">The node's AL name, which is also its XML name unless
    /// <c>XmlName</c> overrides it.</param>
    /// <param name="Source">The <c>SourceField</c> / <c>SourceTable</c> reference as written,
    /// or null for a text node.</param>
    private sealed record XmlPortSchemaNode(
        XmlPortNodeKind Kind, string Name, int Indentation, string? Source,
        Dictionary<string, string> Properties);

    private enum XmlPortNodeKind { TextElement, TextAttribute, TableElement, FieldElement, FieldAttribute }

    /// <summary>
    /// The xmlport's schema tree, read from its own source file inside the .app and parsed by
    /// BC's AL parser. Null when the app ships no source for it (a symbols-only package) —
    /// the caller then refuses rather than emitting a schema-less document.
    /// </summary>
    private static List<XmlPortSchemaNode>? TryReadXmlPortSchema(
        string appPath, BcAppSymbolCache.XmlPortSymbol port)
    {
        if (string.IsNullOrEmpty(port.ReferenceSourceFileName)) return null;
        var source = BcAppSymbolCache.TryReadSourceFile(appPath, port.ReferenceSourceFileName!);
        if (string.IsNullOrEmpty(source)) return null;

        foreach (var obj in ParseAlObjects(source))
        {
            if (obj is not NavSyntax.XmlPortSyntax x) continue;
            // Match on ID, not on being the only xmlport in the file: one source file may
            // declare several objects, and the symbol file's id is the authority.
            if (ObjectIdOf(x) is not int id || id != port.Id) continue;
            var nodes = new List<XmlPortSchemaNode>();
            if (x.XmlPortSchema != null)
                foreach (var n in x.XmlPortSchema.XmlPortSchema)
                    AddXmlPortSchemaNode(n, indentation: 0, nodes);
            return nodes;
        }
        return null;
    }

    /// <summary>
    /// Append one schema node then descend into its children, so the list comes out in
    /// declaration order with each entry's real nesting depth — which is what BC's
    /// <c>Indentation</c> element states.
    /// </summary>
    private static void AddXmlPortSchemaNode(
        NavCA.SyntaxNode node, int indentation, List<XmlPortSchemaNode> result)
    {
        if (node is not NavSyntax.XmlPortNodeSyntax n) return;

        var (kind, source) = n switch
        {
            NavSyntax.XmlPortTextElementSyntax => (XmlPortNodeKind.TextElement, (string?)null),
            NavSyntax.XmlPortTextAttributeSyntax => (XmlPortNodeKind.TextAttribute, null),
            NavSyntax.XmlPortTableElementSyntax te => (XmlPortNodeKind.TableElement, te.SourceTable?.ToString()?.Trim()),
            NavSyntax.XmlPortFieldElementSyntax fe => (XmlPortNodeKind.FieldElement, fe.SourceField?.ToString()?.Trim()),
            NavSyntax.XmlPortFieldAttributeSyntax fa => (XmlPortNodeKind.FieldAttribute, fa.SourceField?.ToString()?.Trim()),
            // A node kind this BC version added that the five cases above do not name. It is
            // left out of the tree rather than guessed at, and the count mismatch that
            // produces is what the caller's refusal reports.
            _ => ((XmlPortNodeKind)(-1), null),
        };
        if ((int)kind < 0) return;

        result.Add(new XmlPortSchemaNode(
            kind, IdentText(n.Name), indentation, source, XmlPortNodeProperties(n.PropertyList)));

        foreach (var child in n.Schema)
            AddXmlPortSchemaNode(child, indentation + 1, result);
    }

    /// <summary>
    /// A node's declared properties as a case-insensitive name -> AL text map. Only what the
    /// AL states: an absent key means the consumer applies AL's own default, so "not stated"
    /// and "stated as the default" stay distinguishable here.
    /// </summary>
    private static Dictionary<string, string> XmlPortNodeProperties(NavSyntax.PropertyListSyntax? props)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (props == null) return map;
        foreach (var p in props.Properties)
        {
            if (p is not NavSyntax.PropertySyntax ps) continue;
            var name = ps.Name?.Identifier.ValueText;
            if (string.IsNullOrEmpty(name)) continue;
            // AlStringLiteralText, not PropertyTextFrom: an xmlport node's XmlName is an AL
            // STRING literal (XmlName = 'Header'), and PropertyTextFrom falls back to the
            // node's raw text, so the single quotes came through as part of the value. BC then
            // wrote them into the exported element name and XmlWriter refused it —
            // "Invalid name character in ''Header''" from NavXmlPortExporter, measured on
            // fixture xmlport 61602 (#3797). The same trap AlStringLiteralText's own doc
            // comment records for ExternalName.
            //
            // The fallback keeps a non-literal value (an enum member such as
            // MinOccurs = Zero, a boolean) reaching the map as written.
            var value = AlStringLiteralText(ps.Value) ?? PropertyTextFrom(ps.Value);
            if (value != null) map[name!] = value;
        }
        return map;
    }

    // ── XML emission ─────────────────────────────────────────────────────────

    /// <summary>
    /// The trailing three GUID fields BC's emitter writes on every xmlport node id, and the
    /// scheme for the first two: <c>{0000XXXX-NNNN-0000-0F00-0000836BD2D2}</c>, where
    /// <c>XXXX</c> is the xmlport id in hex and <c>NNNN</c> the 1-based node sequence.
    ///
    /// <para>Measured against BC's own emitted documents on two distinct BC 28 builds —
    /// 28.1.49838.53910 and 28.3.52162.54347 — over two probe xmlports covering all five AL
    /// schema keywords; identical on both. See docs/xmlport-metadata-from-bc.md#node-ids.</para>
    ///
    /// <para>Trap: the id is not decorative. BC resolves a node's parent by matching
    /// <c>ParentID</c> against another node's <c>ID</c>, so a scheme that collides across two
    /// xmlports in one run would reparent nodes between ports — which is why the xmlport id
    /// is part of it rather than a bare sequence.</para>
    /// </summary>
    private const string XmlPortNodeIdSuffix = "-0000-0F00-0000836BD2D2";

    private static string XmlPortNodeId(int xmlPortId, int sequence)
        => $"{{{xmlPortId:X8}-{sequence:X4}{XmlPortNodeIdSuffix}}}";

    private const string XmlPortNoParentId = "{00000000-0000-0000-0000-000000000000}";

    /// <summary>
    /// Emit the <c>&lt;XmlPort&gt;</c> runtime metadata document, matching the shape BC's own
    /// emit produces for a source-compiled xmlport. Nodes are flat siblings carrying their
    /// nesting as <c>Indentation</c> — the same encoding the emitted XML uses.
    /// </summary>
    // internal, not private, so DependencyXmlPortMetadataTests can assert the document for one
    // xmlport's AL source without a whole loaded dependency set behind it. Takes the AL text
    // rather than a node list so the test drives the SAME parse the real path does — a test
    // handed a pre-built node list would pass with the parser broken.
    internal static string? EmitXmlPortXmlFromAlForTests(
        int id, string name, Dictionary<string, string> properties, string alSource,
        string? alNamespace = null)
    {
        var port = new BcAppSymbolCache.XmlPortSymbol(id, name, properties, null, alNamespace);
        foreach (var obj in ParseAlObjects(alSource))
        {
            if (obj is not NavSyntax.XmlPortSyntax x) continue;
            if (ObjectIdOf(x) is not int parsedId || parsedId != id) continue;
            var nodes = new List<XmlPortSchemaNode>();
            if (x.XmlPortSchema != null)
                foreach (var n in x.XmlPortSchema.XmlPortSchema)
                    AddXmlPortSchemaNode(n, indentation: 0, nodes);
            return nodes.Count == 0 ? null : EmitXmlPortXml(port, nodes);
        }
        return null;
    }

    private static string EmitXmlPortXml(
        BcAppSymbolCache.XmlPortSymbol port, List<XmlPortSchemaNode> schema)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("XmlPort");
            // BC writes ALNamespace as an attribute on the root, empty when the object states
            // none. The symbol file does not state an xmlport's namespace separately from the
            // Namespaces tree it was reached through, so the tree path is what is written.
            w.WriteAttributeString("ALNamespace", port.ALNamespace ?? "");

            w.WriteElementString("MetadataVersion", "130000");
            w.WriteStartElement("ID");
            w.WriteAttributeString("Name", port.Name);
            w.WriteString(port.Id.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteElementString("Name", port.Name);
            // BC's own default namespace for an xmlport that declares none, measured on both
            // probe ports: urn:microsoft-dynamics-nav/xmlports/x<id>.
            w.WriteElementString(
                "DefaultNamespace",
                XmlPortProperty(port, "DefaultNamespace")
                    ?? $"urn:microsoft-dynamics-nav/xmlports/x{port.Id.ToString(CultureInfo.InvariantCulture)}");

            // The object-level properties, at AL's own defaults where the symbol file states
            // none. Each default is measured against BC's emitted document rather than
            // assumed; docs/xmlport-metadata-from-bc.md#object-properties is the table.
            w.WriteElementString("DefaultFieldsValidation", XmlPortBool(port, "DefaultFieldsValidation", true));
            w.WriteElementString("FileName", XmlPortProperty(port, "FileName") ?? "");
            w.WriteElementString("FormatEvaluate", "C/SIDE Format/Evaluate");
            w.WriteElementString("InlineSchema", XmlPortBool(port, "InlineSchema", false));
            w.WriteElementString("PreserveWhiteSpace", XmlPortBool(port, "PreserveWhiteSpace", false));
            w.WriteElementString("TransactionType", XmlPortProperty(port, "TransactionType") ?? "UpdateNoLocks");
            w.WriteElementString("UseDefaultNamespace", XmlPortBool(port, "UseDefaultNamespace", false));
            w.WriteElementString("UseLax", XmlPortBool(port, "UseLax", false));
            // AL's default for UseRequestPage is TRUE, so an xmlport stating nothing gets 1 —
            // which is what BC's emitter wrote for probe port 61602, stating none (#3797).
            w.WriteElementString("UseRequestForm", XmlPortBool(port, "UseRequestPage", true));
            w.WriteElementString("XmlVersionNo", "1.0");

            if (XmlPortProperty(port, "Caption") is { Length: > 0 } caption)
            {
                w.WriteStartElement("CaptionML");
                w.WriteStartElement("Caption");
                w.WriteAttributeString("Id", "1033");
                w.WriteString(caption);
                w.WriteEndElement();
                w.WriteEndElement();
            }

            // Direction / Format / Encoding are the three the symbol file DOES state on every
            // xmlport that declares them, and the three #3797 measured the runner answering
            // wrongly (Both / UTF16 for Export / UTF8). Written from the symbol file, at AL's
            // documented default otherwise.
            w.WriteElementString("Direction", XmlPortProperty(port, "Direction") ?? "Both");
            w.WriteElementString("Format", XmlPortProperty(port, "Format") ?? "Xml");
            // The symbol file spells it UTF8/UTF16; BC's document spells it UTF-8/UTF-16.
            w.WriteElementString("Encoding", XmlPortEncoding(XmlPortProperty(port, "Encoding")));
            if (XmlPortProperty(port, "TextEncoding") is { Length: > 0 } textEncoding)
                w.WriteElementString("TextEncoding", textEncoding);

            // (node sequence, table id) per tableelement, in declaration order — the
            // request page's filter controls and expressions are keyed by that sequence.
            var tableElements = new List<(int Sequence, int TableId)>();
            // enclosing[d] = the innermost open node at indentation d: its emitted ID (what a
            // bound child's ParentID resolves to) and the table id it binds, if it is a
            // tableelement (what a bound child's field name resolves against). Carried
            // together because a bound node needs both and they come from the same ancestor.
            var enclosing = new List<(string NodeId, int TableId)>();
            for (int i = 0; i < schema.Count; i++)
            {
                var n = schema[i];
                var nodeId = XmlPortNodeId(port.Id, i + 1);

                var parent = n.Indentation > 0 && n.Indentation <= enclosing.Count
                    ? enclosing[n.Indentation - 1]
                    : ((string?)null, 0);
                int tableId = n.Kind == XmlPortNodeKind.TableElement
                    ? ResolveTableIdByName(LastNameSegment(n.Source))
                    : 0;
                WriteXmlPortNode(w, n, nodeId, parent.Item1, tableId, parent.Item2);

                if (tableId > 0) tableElements.Add((i + 1, tableId));

                if (n.Indentation < enclosing.Count)
                    enclosing.RemoveRange(n.Indentation, enclosing.Count - n.Indentation);
                if (n.Indentation == enclosing.Count)
                    // A non-table node still occupies its depth slot, carrying its parent's
                    // table id onward: a fieldelement nested inside a textelement inside a
                    // tableelement still binds to that tableelement's table.
                    enclosing.Add((nodeId, tableId > 0 ? tableId : parent.Item2));
            }

            WriteXmlPortRequestPageXml(w, port, tableElements);

            w.WriteEndElement();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The <c>&lt;RequestPage&gt;</c> subtree.
    ///
    /// <para>NOT optional, and not merely for fidelity: BC's compiled xmlport class declares a
    /// nested <c>RequestPage</c> whose constructor takes a <c>MasterPage</c>, and
    /// <c>InitializeComponent</c> calls it unconditionally. With no element,
    /// <c>NavForm..ctor(ITreeObject, MasterPage)</c> dereferences a null MasterPage and the
    /// xmlport's own constructor dies with a bare NullReferenceException — measured on fixture
    /// xmlport 61602, which is how this subtree came to be written (#3797). So an xmlport
    /// document without it is not a reduced document, it is one BC cannot construct.</para>
    ///
    /// <para>ONLY THE FRAME IS DERIVED, for the reason <c>DependencyReportMetadata</c>'s
    /// request page gives: <c>MetadataProvider</c> merges this into BC's own master-page
    /// template, so the built-in controls come from that template rather than from here. What
    /// IS derived is the per-tableelement filter control and its matching expression, because
    /// those are keyed to this xmlport's own node sequence and the template cannot know them.
    /// The four <c>*TranslationKey</c> attributes BC also states are deliberately not written
    /// — the same scope decision on translations, not a claim they are underivable.</para>
    ///
    /// <para>BC takes <c>val.FirstChild</c> rather than a child found by name, so
    /// <c>PageDefinition</c> must be the FIRST child of <c>&lt;RequestPage&gt;</c>.</para>
    /// </summary>
    private static void WriteXmlPortRequestPageXml(
        XmlWriter w, BcAppSymbolCache.XmlPortSymbol port, List<(int Sequence, int TableId)> tableElements)
    {
        w.WriteStartElement("RequestPage");
        w.WriteStartElement("PageDefinition", MetaObjectsNamespace);
        w.WriteAttributeString("MetadataVersion", "130000");
        // ID="0" on every emitted document measured: a request page is not an object in its
        // own right. Name is the XMLPORT's name, which is what BC's emitter writes.
        w.WriteAttributeString("ID", "0");
        w.WriteAttributeString("Name", port.Name);

        w.WriteStartElement("Properties", MetaObjectsNamespace);
        w.WriteAttributeString("XMLportID", port.Id.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("PageType", "XmlPort");
        w.WriteAttributeString("PromotedActionCategoriesML", "");
        w.WriteAttributeString("Editable", "1");
        // Present-but-empty, exactly as BC emits it, and load-bearing for the same reason
        // DependencyPageMetadataXml gives: MetaPageDefinition deserializes a MISSING element
        // to null rather than to an empty one, and the readers dereference it unguarded.
        w.WriteStartElement("SourceObject", MetaObjectsNamespace);
        w.WriteEndElement();
        w.WriteEndElement(); // Properties

        w.WriteStartElement("Content", MetaObjectsNamespace);
        if (tableElements.Count > 0)
        {
            w.WriteStartElement("Containers", MetaObjectsNamespace);
            w.WriteAttributeString("xsi", "type", XsiNamespace, "ControlContainerDefinition");
            w.WriteAttributeString("ContainerType", "RequestPageFilters");
            w.WriteStartElement("Controls", MetaObjectsNamespace);
            w.WriteAttributeString("xsi", "type", XsiNamespace, "ControlGroupDefinition");
            w.WriteAttributeString("ID", "1");
            foreach (var (sequence, tableId) in tableElements)
            {
                w.WriteStartElement("Controls", MetaObjectsNamespace);
                w.WriteAttributeString("xsi", "type", XsiNamespace, "FilterControlDefinition");
                w.WriteAttributeString(
                    "ID", (sequence + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("DataColumnName", XmlPortDataItemViewName(port.Id, sequence));
                w.WriteAttributeString("FilterTableID", tableId.ToString(CultureInfo.InvariantCulture));
                w.WriteStartElement("FieldVariable", MetaObjectsNamespace);
                w.WriteAttributeString("Datatype", "Binary");
                w.WriteEndElement();
                w.WriteEndElement(); // Controls (filter)
            }
            w.WriteEndElement(); // Controls (group)
            w.WriteEndElement(); // Containers
        }
        w.WriteEndElement(); // Content

        // Present-but-empty even with no tableelement, for the same reason as on a page:
        // MetadataProvider.LoadExpressionRelationTables iterates Expressions with no null
        // check.
        w.WriteStartElement("Expressions", MetaObjectsNamespace);
        foreach (var (sequence, _) in tableElements)
        {
            var name = XmlPortDataItemViewName(port.Id, sequence);
            w.WriteStartElement("Expression", MetaObjectsNamespace);
            w.WriteAttributeString("Name", name);
            w.WriteAttributeString("Datatype", "Binary");
            w.WriteAttributeString("ExpressionType", "DataItem");
            w.WriteAttributeString("SourceExpression", name);
            w.WriteEndElement();
        }
        w.WriteEndElement(); // Expressions

        w.WriteEndElement(); // PageDefinition
        w.WriteEndElement(); // RequestPage
    }

    /// <summary>
    /// The name BC's emitter gives a tableelement's request-page filter expression:
    /// <c>XmlPort&lt;id&gt;DataItem&lt;node sequence&gt;TableView</c>. Measured as
    /// <c>XmlPort61602DataItem2TableView</c> for the tableelement that is node 2 of xmlport
    /// 61602, and <c>XmlPort61603DataItem3TableView</c> for node 3 of 61603 — so the number is
    /// the node's 1-based sequence in the whole schema, NOT an index among tableelements.
    /// </summary>
    private static string XmlPortDataItemViewName(int xmlPortId, int nodeSequence)
        => $"XmlPort{xmlPortId.ToString(CultureInfo.InvariantCulture)}"
            + $"DataItem{nodeSequence.ToString(CultureInfo.InvariantCulture)}TableView";

    /// <summary>
    /// One <c>&lt;Node&gt;</c>.
    ///
    /// <para>ParentID is written as the all-zero GUID for a TEXT node and as the enclosing
    /// node's real id for a BOUND one (field element / field attribute). That is not a
    /// simplification: BC's own emitter does exactly this — measured on probe port 61603,
    /// where the nested <c>textelement(Inner)</c> inside a tableelement carries the all-zero
    /// ParentID while the sibling <c>fieldelement</c> carries the tableelement's id. A text
    /// node's containment is expressed by Indentation alone.</para>
    /// </summary>
    private static void WriteXmlPortNode(
        XmlWriter w, XmlPortSchemaNode n, string nodeId, string? parentId,
        int tableId, int enclosingTableId)
    {
        bool isAttribute = n.Kind is XmlPortNodeKind.TextAttribute or XmlPortNodeKind.FieldAttribute;
        bool isBound = n.Kind is XmlPortNodeKind.FieldElement or XmlPortNodeKind.FieldAttribute;
        bool isTable = n.Kind == XmlPortNodeKind.TableElement;

        w.WriteStartElement("Node");
        // XmlName overrides the node's XML name; the AL name stays as VariableName.
        n.Properties.TryGetValue("XmlName", out var xmlName);
        w.WriteElementString("NodeName", string.IsNullOrEmpty(xmlName) ? n.Name : xmlName);
        w.WriteElementString("NodeType", isAttribute ? "Attribute" : "Element");
        w.WriteElementString("SourceType", isBound ? "Field" : isTable ? "Table" : "Text");
        w.WriteElementString("Indentation", n.Indentation.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("ID", nodeId);

        if (isTable)
        {
            // An unresolved table is left UNSTATED rather than written as 0: a fabricated id
            // would bind the element to an unrelated table, and BC reads a missing
            // SourceTable as "not a table-bound node" rather than as table zero.
            if (tableId > 0)
                w.WriteElementString("SourceTable", tableId.ToString(CultureInfo.InvariantCulture));
        }
        else if (isBound && !string.IsNullOrEmpty(n.Source))
        {
            // BC writes `Record::Field`, which is the AL `Record.Field` with the dot replaced
            // and any quoting removed — measured as `Header::No.` for `Header."No."`.
            if (XmlPortSourceFieldReference(n.Source!) is { } sourceField)
                w.WriteElementString("SourceField", sourceField);
        }

        w.WriteElementString("ParentID", isBound ? parentId ?? XmlPortNoParentId : XmlPortNoParentId);

        if (isBound)
        {
            // DataType is the AL type of the bound field. It is NOT recoverable from the
            // xmlport's own source — only the field reference is — so it is written only when
            // the referenced table is one the runner knows, and omitted otherwise rather than
            // defaulted to Text, which would misdeclare a Decimal or Date column.
            if (XmlPortBoundFieldType(n.Source!, enclosingTableId) is { } dataType)
                w.WriteElementString("DataType", dataType);
            w.WriteElementString("AutoCalcField", XmlPortNodeBool(n, "AutoCalcField", true));
            w.WriteElementString("FieldValidate", n.Properties.TryGetValue("FieldValidate", out var fv) ? fv : "Undefined");
        }
        else
        {
            w.WriteElementString("VariableName", n.Name);
        }

        if (isTable)
        {
            w.WriteElementString("AutoReplace", XmlPortNodeBool(n, "AutoReplace", false));
            w.WriteElementString("AutoSave", XmlPortNodeBool(n, "AutoSave", true));
            w.WriteElementString("AutoUpdate", XmlPortNodeBool(n, "AutoUpdate", false));
            w.WriteElementString("CalcFields", n.Properties.TryGetValue("CalcFields", out var cf) ? cf : "");
            w.WriteElementString("LinkFields", n.Properties.TryGetValue("LinkFields", out var lf) ? lf : "");
            w.WriteElementString("LinkTableForceInsert", XmlPortNodeBool(n, "LinkTableForceInsert", true));
        }

        if (isAttribute)
        {
            // An attribute has Occurrence where an element has Max/MinOccurs — a different
            // element name, not a different spelling of the same one.
            w.WriteElementString(
                "Occurrence", n.Properties.TryGetValue("Occurrence", out var occ) ? occ : "Required");
        }
        else
        {
            w.WriteElementString(
                "MaxOccurs",
                n.Properties.TryGetValue("MaxOccurs", out var maxOcc) ? maxOcc
                    : isBound ? "Once" : "Unbounded");
            w.WriteElementString(
                "MinOccurs", n.Properties.TryGetValue("MinOccurs", out var minOcc) ? minOcc : "Once");
        }

        if (isTable)
        {
            w.WriteElementString("ReqFilterFields", n.Properties.TryGetValue("ReqFilterFields", out var rff) ? rff : "");
            w.WriteElementString("SourceTableView", n.Properties.TryGetValue("SourceTableView", out var stv) ? stv : "");
            w.WriteElementString("Temporary", XmlPortNodeBool(n, "Temporary", false));
        }
        else if (!isBound)
        {
            w.WriteElementString("TextType", n.Properties.TryGetValue("TextType", out var tt) ? tt : "Text");
        }

        w.WriteElementString("Unbound", XmlPortNodeBool(n, "Unbound", false));
        w.WriteElementString("Width", n.Properties.TryGetValue("Width", out var width) ? width : "0");
        w.WriteEndElement();
    }

    // ── property readers ─────────────────────────────────────────────────────

    private static string? XmlPortProperty(BcAppSymbolCache.XmlPortSymbol port, string name)
        => port.Properties != null && port.Properties.TryGetValue(name, out var v)
            && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>
    /// A boolean object property as BC writes it (1/0), at <paramref name="defaultValue"/>
    /// when the symbol file states none.
    ///
    /// <para>Routed through <see cref="BcAppSymbolCache.SymbolBoolValue"/>, not a hand-rolled
    /// comparison against "true": Microsoft's packages write <c>"1"</c> and never the word, so
    /// matching only "true" answers false for every xmlport that declares the property — the
    /// measured shape of #3790 on the codeunit side.</para>
    /// </summary>
    private static string XmlPortBool(BcAppSymbolCache.XmlPortSymbol port, string name, bool defaultValue)
    {
        var stated = XmlPortProperty(port, name);
        if (stated == null) return defaultValue ? "1" : "0";
        return BcAppSymbolCache.SymbolBoolValue(stated) ? "1" : "0";
    }

    private static string XmlPortNodeBool(XmlPortSchemaNode n, string name, bool defaultValue)
    {
        if (!n.Properties.TryGetValue(name, out var stated) || string.IsNullOrWhiteSpace(stated))
            return defaultValue ? "1" : "0";
        return BcAppSymbolCache.SymbolBoolValue(stated) ? "1" : "0";
    }

    /// <summary>
    /// The <c>Encoding</c> spelling BC's document uses. The symbol file and AL both write
    /// <c>UTF8</c> / <c>UTF16</c>; BC's emitted document writes <c>UTF-8</c> / <c>UTF-16</c>
    /// (#3797 measured the pair on three System Application xmlports). BC's default for an
    /// xmlport stating none is UTF-16, measured on probe port 61602.
    /// </summary>
    private static string XmlPortEncoding(string? stated)
    {
        if (string.IsNullOrEmpty(stated)) return "UTF-16";
        var t = stated!.Trim();
        if (t.Equals("UTF8", StringComparison.OrdinalIgnoreCase)) return "UTF-8";
        if (t.Equals("UTF16", StringComparison.OrdinalIgnoreCase)) return "UTF-16";
        return t;
    }

    /// <summary>
    /// <c>Header."No."</c> -> <c>Header::No.</c>, the spelling BC's emitter writes for a bound
    /// node's SourceField. Returns null for a reference this reader cannot split, so the
    /// element is omitted rather than written with a mangled value.
    /// </summary>
    private static string? XmlPortSourceFieldReference(string source)
    {
        var expr = source.Trim();
        if (expr.Length == 0) return null;
        // Only an unquoted dot outside quotes separates the record from the field: a quoted
        // segment may itself contain dots (`"Amount (LCY)"`, `"No."`).
        bool inQuotes = false;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == '.' && !inQuotes)
            {
                var rec = Unquote(expr.Substring(0, i).Trim());
                var fld = Unquote(expr.Substring(i + 1).Trim());
                if (rec.Length == 0 || fld.Length == 0) return null;
                return rec + "::" + fld;
            }
        }
        return null;
    }

    /// <summary>
    /// The AL type of a bound node's field, or null when the field cannot be resolved. Null
    /// omits <c>DataType</c> rather than defaulting it — see <see cref="WriteXmlPortNode"/>
    /// for why a default would misdeclare the column.
    ///
    /// <para>The reference's left half is a record VARIABLE name, which equals the table's AL
    /// name only when the AL did not rename it (<c>tableelement(Header; "XPDDep Header")</c>
    /// renames it to <c>Header</c>). So it is resolved against the enclosing
    /// <c>tableelement</c>'s own SourceTable, never against the variable name directly.</para>
    /// </summary>
    private static string? XmlPortBoundFieldType(string source, int enclosingTableId)
    {
        if (enclosingTableId <= 0) return null;
        if (XmlPortSourceFieldReference(source) is not { } reference) return null;
        int sep = reference.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0) return null;
        var fieldName = reference.Substring(sep + 2);

        if (!_parsedTables.TryGetValue(enclosingTableId, out var table)) return null;
        foreach (var f in table.Fields)
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return XmlPortDataTypeName(f.TypeName);
        return null;
    }

    /// <summary>
    /// The <c>DataType</c> to write for a bound node: the AL type WITHOUT its length suffix,
    /// spelled the way BC's own <c>NavType</c> enum spells it. <c>Code[20]</c> -> <c>Code</c>.
    ///
    /// <para>Not cosmetic. BC's <c>MetaXmlPort</c> reader takes DataType through a
    /// case-sensitive <c>Enum.Parse</c> over <c>NavType</c>, which has no <c>Code[20]</c>
    /// member, so the declared spelling throws <c>ArgumentException: Requested value 'Code[20]'
    /// was not found</c> and kills the whole document over one node — measured on fixture
    /// xmlport 61602 (#3797).</para>
    ///
    /// <para>Returns null — omitting the element — for a type the live enum does not know,
    /// rather than falling back to the raw text. A value BC cannot parse is worse than an
    /// absent one: the absent one costs a node its declared type, the unparseable one costs
    /// the document. Matching against the live enum also means a type this BC version added
    /// needs no change here, the same reasoning as
    /// <see cref="CanonicalNavTypeName"/>, which this delegates to.</para>
    /// </summary>
    private static string? XmlPortDataTypeName(string? declaredTypeName)
    {
        if (string.IsNullOrWhiteSpace(declaredTypeName)) return null;
        var name = declaredTypeName!.Trim();
        int bracket = name.IndexOf('[');
        if (bracket > 0) name = name.Substring(0, bracket).Trim();
        return CanonicalNavTypeName(name);
    }
}
