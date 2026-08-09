// SiblingParserSyntaxTreeTests — coverage for the six non-table AL parsers after they moved
// onto BC's own AL syntax tree (#1696).
//
// Four of these parsers (query, xmlport, object-decl, object-caption) had NO tests at all
// before this. The two that did — page and report — were covered only for comment shadowing
// (SiblingParserCommentTests, #1697).
//
// The cases below are chosen to be ones the regex implementations got WRONG, so they prove the
// migration rather than merely re-describing it, plus the two places where the tree is
// deliberately stricter than the regex was.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class SiblingParserSyntaxTreeTests
{
    private static readonly Type RP = typeof(AlRunner.Patches.RecordPatches);

    private static void Parse(string method, string source) =>
        RP.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
          .Invoke(null, new object[] { source });

    private static System.Collections.IDictionary Dict(string field) =>
        (System.Collections.IDictionary)RP
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static object? Get(string field, object key)
    {
        var d = Dict(field);
        return d.Contains(key) ? d[key] : null;
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ─── Report: brace inside a caption used to desynchronise the body scan ──────────────

    [Fact]
    public void Report_CaptionContainingAnUnbalancedBrace_DoesNotCorruptTheNextObject()
    {
        // The old body extraction counted `{` and `}` with no string-literal awareness, so a
        // caption containing a lone brace shifted the end of the object body and every
        // brace-depth calculation after it. The second report in the file is the victim:
        // its own properties were read at the wrong depth, or attributed to the first report.
        var src = """
            report 61950 "Braced Report"
            {
                Caption = 'Config { Beta';
                ProcessingOnly = true;
                dataset { }
            }

            report 61951 "Plain Report"
            {
                Caption = 'Plain';
                dataset
                {
                    dataitem(Cust; "Customer")
                    {
                        column(No; "No.") { }
                    }
                }
            }
            """;
        try
        {
            Parse("TryParseReportFile", src);

            var braced = Get("_parsedReports", 61950)!;
            Assert.Equal("Config { Beta", Prop(braced, "Caption"));
            Assert.True((bool)Prop(braced, "ProcessingOnly")!);

            var plain = Get("_parsedReports", 61951)!;
            Assert.Equal("Plain", Prop(plain, "Caption"));
            Assert.False((bool)Prop(plain, "ProcessingOnly")!);

            var items = (System.Collections.IEnumerable)Prop(plain, "DataItems")!;
            var names = items.Cast<object>().Select(d => (string)Prop(d, "Name")!).ToArray();
            Assert.Equal(new[] { "Cust" }, names);
        }
        finally { Dict("_parsedReports").Remove(61950); Dict("_parsedReports").Remove(61951); }
    }

    [Fact]
    public void Report_NestedDataItems_CarryTheirRealIndentation()
    {
        var src = """
            report 61952 "Nested"
            {
                dataset
                {
                    dataitem(Parent; "Customer")
                    {
                        column(A; "No.") { }
                        dataitem(Child; "Sales Line")
                        {
                            DataItemTableView = sorting("Document No.");
                            column(B; "Line No.") { }
                        }
                    }
                    dataitem(Sibling; "Item") { }
                }
            }
            """;
        try
        {
            Parse("TryParseReportFile", src);
            var rep = Get("_parsedReports", 61952)!;
            var items = ((System.Collections.IEnumerable)Prop(rep, "DataItems")!).Cast<object>().ToArray();

            // Declaration order, with real nesting depth — a child must never read as a root.
            Assert.Equal(
                new[] { ("Parent", 0), ("Child", 1), ("Sibling", 0) },
                items.Select(d => ((string)Prop(d, "Name")!, (int)Prop(d, "Indentation")!)).ToArray());
            Assert.Equal(new[] { "Customer", "Sales Line", "Item" },
                items.Select(d => (string)Prop(d, "RelatedTable")!).ToArray());
            Assert.Equal(1, (int)Prop(items[0], "Ordinal")!);
            Assert.Equal("""sorting("Document No.")""", Prop(items[1], "DataItemTableView"));
            Assert.Null(Prop(items[0], "DataItemTableView"));
        }
        finally { Dict("_parsedReports").Remove(61952); }
    }

    // ─── Page: control bindings ──────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<int, string> PageBindings(int pageId)
    {
        var page = Get("_parsedPages", pageId)!;
        return (IReadOnlyDictionary<int, string>)Prop(page, "ControlIdToFieldName")!;
    }

    [Fact]
    public void Page_FieldControls_BindOnlyRecQualifiedExpressions()
    {
        // The old regex looked for the text `Rec.` anywhere after the control's semicolon, so
        // `field(Total; Rec.Amount + 1)` bound the control to Amount — a field that control is
        // not bound to. A whole-expression match refuses it. `field(Local; SomeVar)` was
        // already excluded and must stay excluded.
        var src = """
            page 61953 "Binding Page"
            {
                SourceTable = "Customer";
                layout
                {
                    area(content)
                    {
                        group(General)
                        {
                            field(Simple; Rec.Name) { }
                            field("Post Code"; Rec."Post Code") { }
                            repeater(Lines)
                            {
                                field(Deep; Rec."Balance (LCY)") { }
                            }
                        }
                        field(Compound; Rec.Amount + 1) { }
                        field(Local; SomeVariable) { }
                    }
                }
                actions
                {
                    area(processing)
                    {
                        action(Post) { }
                    }
                }
            }
            """;
        try
        {
            Parse("TryParsePageFile", src);
            var bound = PageBindings(61953).Values.OrderBy(v => v).ToArray();

            // Nesting depth is irrelevant; actions contribute nothing; the two unbound
            // controls stay unbound.
            Assert.Equal(new[] { "Balance (LCY)", "Name", "Post Code" }, bound);
            Assert.Equal("Customer", Prop(Get("_parsedPages", 61953)!, "SourceTableName"));
            Assert.True((bool)Prop(Get("_parsedPages", 61953)!, "InsertAllowed")!);
        }
        finally { Dict("_parsedPages").Remove(61953); }
    }

    [Fact]
    public void Page_WithNoSourceTable_ReportsEmptyRatherThanBorrowingFromANeighbour()
    {
        // The old object-extent guess scanned forward for the next `page|table|codeunit|…`
        // keyword. `enum` was not in that list, so an enum sitting between two pages did not
        // end the slice. This pins the negative direction: a page that declares no SourceTable
        // reports none, whatever follows it in the file.
        var src = """
            page 61954 "No Source" { layout { area(content) { } } }

            enum 61954 "Some Enum" { value(0; A) { } }

            page 61955 "Has Source" { SourceTable = "Item"; layout { area(content) { } } }
            """;
        try
        {
            Parse("TryParsePageFile", src);
            Assert.Equal("", Prop(Get("_parsedPages", 61954)!, "SourceTableName"));
            Assert.Equal("Item", Prop(Get("_parsedPages", 61955)!, "SourceTableName"));
        }
        finally { Dict("_parsedPages").Remove(61954); Dict("_parsedPages").Remove(61955); }
    }

    // ─── Query / XmlPort: previously untested entirely ───────────────────────────────────

    [Fact]
    public void Query_IsRegisteredByIdAndName_AndCommentedOutOneIsNot()
    {
        var src = """
            query 61956 "Real Query"
            {
                QueryType = Normal;
                elements { dataitem(C; "Customer") { column(N; "No.") { } } }
            }
            // query 61957 "Ghost Query" { }
            """;
        try
        {
            Parse("TryParseQueryFile", src);
            var q = Get("_parsedQueries", 61956)!;
            Assert.Equal("Real Query", Prop(q, "Name"));
            Assert.False((bool)Prop(q, "IsExtension")!);
            Assert.Null(Get("_parsedQueries", 61957));
        }
        finally { Dict("_parsedQueries").Remove(61956); Dict("_parsedQueries").Remove(61957); }
    }

    [Fact]
    public void XmlPort_IsRegisteredByIdAndName_AndProseMentioningOneIsNot()
    {
        var src = """
            xmlport 61958 "Real Port"
            {
                schema { textelement(Root) { } }
            }

            codeunit 61959 "Doc Holder"
            {
                // Compare against xmlport 61960 "Ghost Port" before shipping.
                procedure Noop() begin end;
            }
            """;
        try
        {
            Parse("TryParseXmlPortFile", src);
            Assert.Equal("Real Port", Prop(Get("_parsedXmlPorts", 61958)!, "Name"));
            Assert.Null(Get("_parsedXmlPorts", 61960));
        }
        finally { Dict("_parsedXmlPorts").Remove(61958); Dict("_parsedXmlPorts").Remove(61960); }
    }

    // ─── Object declarations and captions: previously untested entirely ──────────────────

    [Fact]
    public void ObjectDecl_RecordsKindIdAndName_ButNotVariableDeclarations()
    {
        var src = """
            codeunit 61961 "Decl Probe"
            {
                var
                    Helper: Codeunit 61962;
                procedure Run()
                begin
                    Codeunit.Run(Codeunit::"Decl Probe");
                end;
            }

            enum 61961 "Decl Enum" { value(0; A) { } }
            """;
        try
        {
            Parse("TryParseObjectDeclFile", src);

            // Keyed per kind: a codeunit and an enum may share a numeric id.
            var cu = Get("_parsedObjectDecls", ("Codeunit", 61961))!;
            Assert.Equal("Decl Probe", Prop(cu, "Name"));
            var en = Get("_parsedObjectDecls", ("Enum", 61961))!;
            Assert.Equal("Decl Enum", Prop(en, "Name"));

            // `Helper: Codeunit 61962;` is a variable declaration, not an object.
            Assert.Null(Get("_parsedObjectDecls", ("Codeunit", 61962)));
        }
        finally
        {
            Dict("_parsedObjectDecls").Remove(("Codeunit", 61961));
            Dict("_parsedObjectDecls").Remove(("Enum", 61961));
        }
    }

    [Fact]
    public void ObjectCaption_ReadsTheObjectsOwnCaption_NotANestedOne()
    {
        // The nested `field`'s Caption must not become the table's. Absent Caption stays null
        // — "declares none", which the consumer turns into AL's name fallback, and which is a
        // different answer from an empty caption.
        var src = """
            table 61963 "Captioned Table"
            {
                Caption = 'The Table';
                fields { field(1; A; Integer) { Caption = 'The Field'; } }
            }

            codeunit 61964 "Uncaptioned" { }
            """;
        try
        {
            Parse("TryParseObjectCaptionFile", src);
            Assert.Equal("The Table", Dict("_parsedObjectCaptions")[("Table", 61963)]);
            Assert.True(Dict("_parsedObjectCaptions").Contains(("Codeunit", 61964)));
            Assert.Null(Dict("_parsedObjectCaptions")[("Codeunit", 61964)]);
        }
        finally
        {
            Dict("_parsedObjectCaptions").Remove(("Table", 61963));
            Dict("_parsedObjectCaptions").Remove(("Codeunit", 61964));
        }
    }

    [Fact]
    public void AllSiblingParsers_IgnoreUnparseableInput_RatherThanThrowing()
    {
        foreach (var method in new[]
                 {
                     "TryParsePageFile", "TryParseReportFile", "TryParseQueryFile",
                     "TryParseXmlPortFile", "TryParseObjectDeclFile", "TryParseObjectCaptionFile",
                 })
        {
            Parse(method, "");
            Parse(method, "not AL at all { { {");
            Parse(method, "interface IFoo { procedure Do(); }"); // legal AL, but has no object id
        }
    }
}
