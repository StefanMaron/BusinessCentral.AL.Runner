// QuerySymbolDerivedMetaQueryPropertiesTests — a query in a precompiled dependency states the
// inherent masks it declares, the caption BC derives, the constants BC's emitter assigns, and
// the operator on every dataitem link.
//
// THE DEFECT THIS PINS (#3798, found by the Query metadata-equivalence comparison, #3782)
//   The runner's MetaQuery design object — the one NCLMetaQuery.CreateDynamicQuery consumes, so
//   what AL actually gets — left eight members at their defaults while BC's own emitter states
//   a value. Measured on BC 28.1.49838.53910 over System Application's 7 queries; every
//   STRUCTURAL member already agreed exactly (column ids, FieldNo, ColumnType, MethodType,
//   QueryColumnIndex, DataItemLinkType, DataItemTable), so this is entirely about the
//   query-level scalars and the one link property.
//
// CAPTION IS THE MEMBER THAT NEEDED SETTLING, AND THE ANSWER INVERTS THE ISSUE'S READING
//   #3798 records query 777 as a case where "the runner is arguably right": the symbol file
//   says Caption='RoleCenter from Plans', BC answers 'Role Center from Plans' (the object
//   NAME), so the runner looked like it was reporting the declared caption faithfully.
//
//   It is not. BC's emitter writes NO <Caption> element at all — 0 of the 1,218 documents in
//   the System Application ground-truth bundle carry one, against 113 carrying <CaptionML>, so
//   the property cannot be coming from a declared caption on any object of any kind. Feeding
//   BC's own MetaQuery(XmlNode,0,0) three synthesised documents isolates what does drive it:
//
//     <Name>My Name</Name>                                  -> Caption='My Name'
//     <Name>My Name</Name><CaptionML>ENU=My Caption</...>    -> Caption='My Name'   (ML ignored)
//     <Name>My Name</Name><Caption>My Caption</Caption>      -> Caption='My Name'   (elem ignored)
//     <CaptionML>ENU=My Caption</CaptionML>, no <Name>       -> Caption=''
//
//   So Caption tracks Name unconditionally on the document route, and the declared caption
//   reaches AL through CaptionML — a MultiLanguage object this design object does not carry.
//   Writing the symbol file's caption into Caption answers something BC never answers, on BOTH
//   of the issue's two situations rather than just the three undeclared ones.
//
// THE MASK POPULATION IS MEASURED ON QUERIES, NOT INHERITED FROM #3788
//   #3788's codeunit sweep found one lowercase mask in 1,167; a coordinator note on #3798 then
//   corrected the surrounding figure twice. Re-measured here on the kind this file is about, BC
//   28.4.53241.54407, Base Application (154 queries) + System Application (7): 6 of 161 state a
//   mask and ALL SIX spell it "X". No lowercase form occurs on queries in Microsoft's shipped
//   packages at all.
//
//   That is a reason to assert the lowercase and mixed-case forms HERE rather than to skip
//   them: the decoder is shared with the codeunit direction, the query population exercises
//   only its direct-bit path, and an indirect-bit regression would therefore be invisible to
//   every query in every Microsoft app. The fixture declares "x" and "rX" for exactly that.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   BC's own emitter output is the ground truth here and the metadata-equivalence harness
//   compares against it on every unit-test leg. What no AL test can reach is the
//   PRECOMPILED-dependency route: a bundle's own queries are source-parsed and take the BC
//   document route (#3608), so the symbol-file spelling never arises. This file drives that
//   route directly, the same argument CodeunitSymbolNamespaceAndInherentMaskTests makes.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reaches the RecordPatches AL parse statics, which are process-wide and which xunit's
// parallel collections can clear or repopulate between a write and a read (#1696, #1712).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class QuerySymbolDerivedMetaQueryPropertiesTests : IDisposable
{
    private const int DirectExecute = 61072;
    private const int IndirectExecute = 61073;
    private const int MixedCaseMask = 61074;
    private const int DeclaresNoMask = 61075;
    private const int CaptionDiffersFromName = 61076;

    private const int LinkedTable = 61077;

    private readonly string _root;

    public QuerySymbolDerivedMetaQueryPropertiesTests()
    {
        _root = TestScratch.Dir("al-runner-query-derived-properties");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Queries sit in the <c>Namespaces</c> tree the way Microsoft's packages write them —
    /// System Application's flat top-level <c>Queries</c> array is empty and all 7 of its
    /// queries are reached through the tree, so a fixture using the flat array would exercise a
    /// path no shipped app takes.
    /// </summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "6f1d9c84-3f2a-4e77-9c51-2a7b4e08d913",
          "Name": "Query Derived Properties Fixture",
          "Tables": [
            {
              "Id": {{LinkedTable}},
              "Name": "Derived Query Source",
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 20 } },
                { "Id": 2, "Name": "Amount", "TypeDefinition": { "Name": "Decimal" } }
              ]
            }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Derived",
                  "Queries": [
                    {
                      "Id": {{DirectExecute}},
                      "Name": "Direct Execute Query",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "X" },
                        { "Name": "InherentPermissions", "Value": "X" }
                      ],
                      "Elements": [
                        {
                          "Id": 1, "Name": "Root", "RelatedTable": "Derived Query Source",
                          "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ]
                        }
                      ]
                    },
                    {
                      "Id": {{IndirectExecute}},
                      "Name": "Indirect Execute Query",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "x" }
                      ],
                      "Elements": [
                        {
                          "Id": 1, "Name": "Root", "RelatedTable": "Derived Query Source",
                          "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ]
                        }
                      ]
                    },
                    {
                      "Id": {{MixedCaseMask}},
                      "Name": "Mixed Case Query",
                      "Properties": [
                        { "Name": "InherentPermissions", "Value": "rX" }
                      ],
                      "Elements": [
                        {
                          "Id": 1, "Name": "Root", "RelatedTable": "Derived Query Source",
                          "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ]
                        }
                      ]
                    },
                    {
                      "Id": {{DeclaresNoMask}},
                      "Name": "No Mask Query",
                      "Properties": [],
                      "Elements": [
                        {
                          "Id": 1, "Name": "Root", "RelatedTable": "Derived Query Source",
                          "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ]
                        }
                      ]
                    },
                    {
                      "Id": {{CaptionDiffersFromName}},
                      "Name": "Role Center from Plans",
                      "Properties": [
                        { "Name": "Caption", "Value": "RoleCenter from Plans" }
                      ],
                      "Elements": [
                        {
                          "Id": 1, "Name": "Root", "RelatedTable": "Derived Query Source",
                          "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ],
                          "DataItems": [
                            {
                              "Id": 2, "Name": "Child", "RelatedTable": "Derived Query Source",
                              "Properties": [
                                { "Name": "SqlJoinType", "Value": "InnerJoin" },
                                { "Name": "DataItemLink", "Value": "Code = Root.Code" }
                              ],
                              "Columns": [ { "Id": 12, "Name": "Amount_Col", "SourceColumn": "Amount" } ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private void Register()
    {
        var appPath = Path.Combine(_root, "query-derived.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
    }

    /// <summary>
    /// The runner's own MetaQuery design object for one query — the same object
    /// <c>NCLMetaQuery.CreateDynamicQuery</c> consumes and the metadata-equivalence harness
    /// compares, so this reads the fix's actual output rather than a second derivation written
    /// for the test.
    /// </summary>
    private static object Design(int queryId)
    {
        var design = RecordPatches.TryBuildQueryMetadataEquivalenceDesign(queryId);
        Assert.True(design is not null,
            $"the runner built no MetaQuery design for query {queryId} — that null is #3499's " +
            "shape and means this test measured nothing at all, not that a property is wrong.");
        return design!;
    }

    private static object? Read(object design, string property)
    {
        var p = design.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null,
            $"Types.Metadata.MetaQuery has no '{property}' property on this BC build — the " +
            "member this test pins has changed shape, so re-measure before trusting either side.");
        return p!.GetValue(design);
    }

    /// <summary>
    /// The mask letters the symbol file states become the numeric permission BC's emitter
    /// writes, with CASE preserved. The three spellings are asserted against three DIFFERENT
    /// expected values, so a case-collapsing parse — the natural thing to reach for, since
    /// SymbolProperties matches the property NAME case-insensitively — fails on two of them
    /// rather than passing on an aggregate.
    /// </summary>
    [Fact]
    public void The_inherent_mask_letters_a_query_declares_become_the_permission_BC_answers()
    {
        Register();

        // "X" -> bit 4 = Execute. The only spelling that occurs on queries in Microsoft's own
        // packages (6 of 161 measured on 28.4.53241.54407), so it alone proves the common case.
        Assert.Equal("Execute", Read(Design(DirectExecute), "InherentEntitlements")!.ToString());
        Assert.Equal("Execute", Read(Design(DirectExecute), "InherentPermissions")!.ToString());

        // "x" -> bit 9 = IndirectExecute, a DIFFERENT value reached only by preserving case.
        // A case-insensitive parse answers Execute here, which is what makes this the decisive
        // assertion rather than the one above.
        Assert.Equal("IndirectExecute", Read(Design(IndirectExecute), "InherentEntitlements")!.ToString());

        // "rX" -> IndirectRead | Execute. Asserted because a parse that special-cased single
        // characters would still get both of the above right and this one wrong.
        Assert.Equal("Execute, IndirectRead", Read(Design(MixedCaseMask), "InherentPermissions")!.ToString());

        // A query declaring no mask keeps BC's own enum default rather than gaining a value.
        Assert.Equal("None", Read(Design(DeclaresNoMask), "InherentEntitlements")!.ToString());
        Assert.Equal("None", Read(Design(DeclaresNoMask), "InherentPermissions")!.ToString());
    }

    /// <summary>
    /// Caption answers the query's NAME, not the caption the symbol file declares — the
    /// inversion this file's header measures. The fixture reproduces System Application's query
    /// 777 exactly, the case #3798 recorded as one where the runner was arguably right.
    /// </summary>
    [Fact]
    public void Caption_answers_the_query_name_and_not_the_declared_caption()
    {
        Register();

        // The two strings differ by one space, which is the whole point: a fix that wrote the
        // declared caption through would answer 'RoleCenter from Plans' and pass any assertion
        // that merely checked Caption was non-empty.
        Assert.Equal("Role Center from Plans", Read(Design(CaptionDiffersFromName), "Caption"));
        Assert.NotEqual("RoleCenter from Plans", Read(Design(CaptionDiffersFromName), "Caption"));

        // A query declaring NO caption gets its name too, rather than the null the runner used
        // to answer for System Application's three undeclared queries.
        Assert.Equal("No Mask Query", Read(Design(DeclaresNoMask), "Caption"));
    }

    /// <summary>
    /// The constants BC's emitter writes into every query document unconditionally. Each is
    /// asserted to its own value, so a fix that set one and left the rest defaulted cannot pass.
    /// </summary>
    [Fact]
    public void The_constants_BCs_emitter_assigns_are_stated_rather_than_left_defaulted()
    {
        Register();
        var design = Design(DeclaresNoMask);

        Assert.Equal("https://learn.microsoft.com/dynamics365/business-central/", Read(design, "HelpLink"));

        // BC writes an EMPTY element for these three, which its own reader turns into "" —
        // never null. Asserted as "" specifically: null would be the value the runner used to
        // answer, and Assert.Empty accepts both.
        Assert.Equal(string.Empty, Read(design, "QueryCategory"));
        Assert.Equal(string.Empty, Read(design, "APIGroup"));
        Assert.Equal(string.Empty, Read(design, "APIPublisher"));
    }

    /// <summary>
    /// Every dataitem link states the operator BC states. AL has no syntax for anything but
    /// equality today, so the constant is faithful — and the property was left null on all four
    /// links in the ground-truth bundle.
    /// </summary>
    [Fact]
    public void A_dataitem_link_states_the_equality_operator_BC_states()
    {
        Register();
        var design = Design(CaptionDiffersFromName);

        var dataItems = (System.Collections.IList)Read(design, "DataItems")!;
        Assert.Equal(2, dataItems.Count);

        // The nested dataitem is the one carrying the link; the root declares none.
        var child = dataItems[1]!;
        var links = (System.Collections.IList)child.GetType()
            .GetProperty("DataItemLinks", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(child)!;
        Assert.Single(links);

        var link = links[0]!;
        Assert.Equal("=", link.GetType()
            .GetProperty("LinkOperator", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(link));

        // The link still resolves the two field numbers it did before — a LinkOperator set on a
        // link that stopped joining correctly would be a regression this assertion catches.
        Assert.Equal(1, link.GetType().GetProperty("SourceFieldNo")!.GetValue(link));
        Assert.Equal(1, link.GetType().GetProperty("DestinationFieldNo")!.GetValue(link));
    }
}
