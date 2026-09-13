// ReportRequestPageSubtreeTests — the <RequestPage> subtree the Report derivation was not
// emitting at all (#3808, half B; half A was the three scalar properties, PR #3936).
//
// THE GAP, AND WHY IT IS NOT COSMETIC
//   MetaReport..ctor takes the element as
//       REQUESTPAGE -> requestPageDefinition = DeserializePageDefinition(val.FirstChild, …)
//   and MetaReport.CreateMasterPage is the only thing that turns it into a MasterPage:
//       if (requestPageDefinition != null && createRequestForm != null)
//           masterPage = createRequestForm(requestPageDefinition, captionML, Id);
//   With no <RequestPage> element the field stays null, the `if` never fires, and
//   NavReportSync falls back to BuildRequestPageStubMasterPage — the path whose own stderr
//   says "its [RequestPageHandler] will not be reachable". So the missing element is what
//   decides whether BC's own request-page construction runs for a precompiled report.
//
// WHAT BC EMITS, which is what these assertions are written against
//   Read off the ground-truth documents for report 9810 on FOUR BC builds — 27.5.46862.53931,
//   28.1.49838.53910, 28.1.49838.54308 and 28.4.53241.54407 (two distinct Ncl binaries, not
//   four). All four are byte-identical in this subtree:
//       <RequestPage>
//         <PageDefinition MetadataVersion="130000" ID="0" Name="Change Password" …>
//           <Properties ReportID="9810" PageType="ReportProcessingOnly" … Editable="1">
//             <SourceObject />
//           </Properties>
//           <Content>
//             <Containers xsi:type="ControlContainerDefinition" ContainerType="RequestPageFilters" />
//           </Content>
//           <Expressions />
//         </PageDefinition>
//       </RequestPage>
//   Note ID="0" and Name = THE REPORT'S name. The symbol file states Id 0 and the literal
//   Name "RequestOptionsPage" for all 660 reports, so the Name is taken from the report and
//   NOT from the RequestPage node — asserted below, because copying the node's own Name
//   would be the natural and wrong move.
//
// WHAT IS DELIBERATELY NOT BUILT HERE: the report-specific CONTROL TREE.
//   The symbol file does carry one (466 of 660 reports state Controls, 2,938 control nodes,
//   1,891 SourceExpression values). It is not emitted, for the reason
//   DependencyPageMetadataXml's header already gives for ordinary page field controls: those
//   SourceExpression values are AL TEXT — "NewCompanyName", "DataExchLineDef.Code" — not the
//   compiled DataColumnName bindings the real document carries, and a request-page control's
//   value binding is registered from the report's OWN IL at RunModal time
//   (RunnerFormInit.MarkSourceExpressionsWanted -> NavForm.RegisterSourceExpression), never
//   read from this XML. Emitting a guessed tree would put controls in front of BC's merge
//   that its own template did not put there. See docs/report-metadata-from-bc.md.
//
//   Crucially, that omission does NOT cost the built-in controls. BC's
//   MetadataProvider.CreateRequestPage ignores the document's PageType entirely and calls
//   CreatePage(pageDefinition, captionML, id, PageType.ReportPreview), which loads its OWN
//   MasterPageReportPreview template and merges the document into it. Every built-in control
//   (ObjectOptions, PrinterName, LayoutName, the Advanced group, …) comes from that template.
using System;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, exactly as the sibling report tests do.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ReportRequestPageSubtreeTests
{
    // Report 9810 "Change Password" as System Application states it: ProcessingOnly = 1,
    // UseRequestPage = 0, and a RequestPage node carrying only Id/Name — the SMALLEST shape
    // the symbol file produces (100 of 660 reports state exactly this).
    private const int ChangePassword = 9810;
    // States Controls and Properties on its RequestPage node — the shape 379 of 660 have.
    // Used to prove the control tree is NOT transcribed into the document.
    private const int WithControls = 9811;
    // Declares no RequestPage node AT ALL. No shipped report does this (660 of 660 carry
    // one), but the parser must not invent a subtree for a report that states none.
    private const int NoRequestPageNode = 9812;

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "6b2c19e4-8d3a-4f71-b0c5-2a7e91d4f388",
          "Name": "Report RequestPage Fixture",
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Security",
                  "Namespaces": [
                    {
                      "Name": "AccessControl",
                      "Reports": [
                        {
                          "Id": {{ChangePassword}},
                          "Name": "Change Password",
                          "DataItems": [],
                          "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" },
                          "Properties": [
                            { "Name": "ProcessingOnly", "Value": "1" },
                            { "Name": "UseRequestPage", "Value": "0" }
                          ]
                        },
                        {
                          "Id": {{WithControls}},
                          "Name": "With Controls",
                          "DataItems": [],
                          "RequestPage": {
                            "Id": 0,
                            "Name": "RequestOptionsPage",
                            "Properties": [
                              { "Name": "SaveValues", "Value": "1" }
                            ],
                            "Controls": [
                              {
                                "Id": 1133176912,
                                "Name": "Options",
                                "Kind": 1,
                                "TypeDefinition": { "Name": "None" },
                                "Controls": [
                                  {
                                    "Id": 449416613,
                                    "Name": "New Company Name",
                                    "Kind": 8,
                                    "TypeDefinition": { "Name": "Text[30]" },
                                    "Properties": [
                                      { "Name": "Caption", "Value": "New Company Name" },
                                      { "Name": "SourceExpression", "Value": "NewCompanyName" }
                                    ]
                                  }
                                ]
                              }
                            ]
                          }
                        },
                        {
                          "Id": {{NoRequestPageNode}},
                          "Name": "No Request Page Node",
                          "DataItems": []
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

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static XmlElement Emit(BcAppSymbolCache.ReportSymbol report)
    {
        var doc = new XmlDocument();
        doc.LoadXml(RecordPatches.EmitReportXml(report, sourceExprByColumn: null));
        return doc.DocumentElement!;
    }

    private static BcAppSymbolCache.ReportSymbol Report(string dir, int id)
        => Assert.Single(BcAppSymbolCache.Get(WriteApp(dir)).Reports, r => r.Id == id);

    /// <summary>
    /// The symbol side: <c>ReportSymbol</c> records THAT the report declares a request page.
    /// A report stating no node carries <c>false</c>, so "declares one" and "declares none"
    /// stay distinguishable — the emitter writes the element on exactly the first.
    /// </summary>
    [Fact]
    public void TheSymbolRecordsWhetherTheReportDeclaresARequestPage()
    {
        var dir = TestScratch.Dir("al-runner-report-requestpage-symbol");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir);
            var reports = BcAppSymbolCache.Get(appPath).Reports;

            Assert.True(Assert.Single(reports, r => r.Id == ChangePassword).HasRequestPage);
            Assert.True(Assert.Single(reports, r => r.Id == WithControls).HasRequestPage);
            // Negative: no node stated means no request page claimed.
            Assert.False(Assert.Single(reports, r => r.Id == NoRequestPageNode).HasRequestPage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The emitted document carries the subtree in the exact shape BC's reader takes:
    /// <c>&lt;RequestPage&gt;</c> whose FIRST CHILD is a <c>PageDefinition</c>, since
    /// <c>MetaReport..ctor</c> passes <c>val.FirstChild</c> to
    /// <c>DeserializePageDefinition</c> without looking for it by name.
    /// </summary>
    [Fact]
    public void TheEmittedDocumentCarriesTheRequestPageSubtreeBcsReaderExpects()
    {
        var dir = TestScratch.Dir("al-runner-report-requestpage-xml");
        Directory.CreateDirectory(dir);
        try
        {
            var root = Emit(Report(dir, ChangePassword));

            var requestPage = Assert.IsAssignableFrom<XmlElement>(root.SelectSingleNode("RequestPage"));
            // FIRST CHILD, not "a descendant named PageDefinition": BC takes val.FirstChild.
            var pageDefinition = Assert.IsAssignableFrom<XmlElement>(requestPage.FirstChild);
            Assert.Equal("PageDefinition", pageDefinition.LocalName);
            Assert.Equal("urn:schemas-microsoft-com:dynamics:NAV:MetaObjects", pageDefinition.NamespaceURI);

            // BC emits ID="0" — the request page is not an object in its own right.
            Assert.Equal("0", pageDefinition.GetAttribute("ID"));
            // ...and the REPORT's name, not the symbol file's literal "RequestOptionsPage".
            Assert.Equal("Change Password", pageDefinition.GetAttribute("Name"));
            Assert.Equal("130000", pageDefinition.GetAttribute("MetadataVersion"));

            var properties = Assert.IsAssignableFrom<XmlElement>(
                pageDefinition.SelectSingleNode("*[local-name()='Properties']"));
            Assert.Equal("9810", properties.GetAttribute("ReportID"));
            // ProcessingOnly = 1 -> ReportProcessingOnly, which is one of the three PageTypes
            // NavTestExecution.FindPageType routes to NavHandlerType.RequestPage.
            Assert.Equal("ReportProcessingOnly", properties.GetAttribute("PageType"));
            Assert.Equal("1", properties.GetAttribute("Editable"));
            // Present-but-empty, exactly as BC emits it: ModifyReportRequestPage dereferences
            // pageDefinition.Properties.SourceObject.SaveValues with no null check.
            Assert.NotNull(properties.SelectSingleNode("*[local-name()='SourceObject']"));

            var content = Assert.IsAssignableFrom<XmlElement>(
                pageDefinition.SelectSingleNode("*[local-name()='Content']"));
            var containers = Assert.IsAssignableFrom<XmlElement>(
                content.SelectSingleNode("*[local-name()='Containers']"));
            Assert.Equal("RequestPageFilters", containers.GetAttribute("ContainerType"));
            Assert.Equal("ControlContainerDefinition",
                containers.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance"));

            // Expressions is present-but-empty for the same reason it is on a page:
            // MetadataProvider.LoadExpressionRelationTables iterates it with no null check.
            Assert.NotNull(pageDefinition.SelectSingleNode("*[local-name()='Expressions']"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>PageType</c> follows the report's own <c>ProcessingOnly</c>, because those are the
    /// two of BC's 22 page types a report's request page can have, and
    /// <c>NavTestExecution.FindPageType</c> routes BOTH to
    /// <c>NavHandlerType.RequestPage</c> — so a wrong one here sends a
    /// <c>[RequestPageHandler]</c> to the modal-page branch instead.
    /// </summary>
    [Fact]
    public void PageTypeFollowsProcessingOnly()
    {
        var dir = TestScratch.Dir("al-runner-report-requestpage-pagetype");
        Directory.CreateDirectory(dir);
        try
        {
            var processingOnly = Emit(Report(dir, ChangePassword));
            Assert.Equal("ReportProcessingOnly", PageTypeOf(processingOnly));

            // WithControls states no ProcessingOnly at all, which is AL's default of false.
            var renders = Emit(Report(dir, WithControls));
            Assert.Equal("ReportPreview", PageTypeOf(renders));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string PageTypeOf(XmlElement reportRoot)
        => Assert.IsAssignableFrom<XmlElement>(
                reportRoot.SelectSingleNode(
                    "RequestPage/*[local-name()='PageDefinition']/*[local-name()='Properties']"))
            .GetAttribute("PageType");

    /// <summary>
    /// The report-specific CONTROL TREE is deliberately NOT transcribed. This is the
    /// assertion that keeps a later "improvement" from writing guessed controls into the
    /// document: the symbol file's SourceExpression is AL text, and BC's own template plus
    /// the report's IL supply the real bindings.
    /// </summary>
    [Fact]
    public void TheReportsOwnControlsAreNotTranscribedIntoTheDocument()
    {
        var dir = TestScratch.Dir("al-runner-report-requestpage-nocontrols");
        Directory.CreateDirectory(dir);
        try
        {
            var root = Emit(Report(dir, WithControls));

            // The subtree is still emitted for this report...
            Assert.NotNull(root.SelectSingleNode("RequestPage"));
            // ...and the Containers element is EMPTY: no control was copied in.
            var containers = Assert.IsAssignableFrom<XmlElement>(root.SelectSingleNode(
                "RequestPage/*[local-name()='PageDefinition']/*[local-name()='Content']"
                + "/*[local-name()='Containers']"));
            Assert.Empty(containers.ChildNodes.Cast<XmlNode>());

            // And nothing anywhere in the document carries the AL source expression the
            // symbol file states for that control.
            Assert.DoesNotContain("NewCompanyName", root.OuterXml, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Negative: a report whose symbol file states NO RequestPage node gets NO element.
    /// Writing one anyway would claim a request page the compiler never recorded, and BC
    /// would then build a MasterPage for a report that has none.
    /// </summary>
    [Fact]
    public void AReportDeclaringNoRequestPageGetsNoElement()
    {
        var dir = TestScratch.Dir("al-runner-report-requestpage-absent");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(Emit(Report(dir, NoRequestPageNode)).SelectSingleNode("RequestPage"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The end of the chain, measured through BC's OWN reader rather than asserted about the
    /// text: <c>MetaReport.RequestPageDefinition</c> is the member #3808 measured as null on
    /// the runner and present on BC. It is non-null for a report that states a request page
    /// and null for one that does not — and the null case is what makes the non-null one
    /// evidence rather than a constant.
    /// </summary>
    [SkippableFact]
    public void BcsOwnMetaReportResolvesTheSubtreeToARequestPageDefinition()
    {
        var types = Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Metadata.MetaReport, Microsoft.Dynamics.Nav.Types");
        Skip.If(types is null, "Microsoft.Dynamics.Nav.Types is not loadable on this box.");

        var ctor = types!.GetConstructors().FirstOrDefault(
            c => c.GetParameters() is { Length: 5 } ps && ps[0].ParameterType == typeof(XmlElement));
        Assert.True(ctor is not null, "MetaReport has no (XmlElement, …) constructor.");
        var property = types.GetProperty("RequestPageDefinition",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(property is not null, "MetaReport has no RequestPageDefinition property.");

        var dir = TestScratch.Dir("al-runner-report-requestpage-metareport");
        Directory.CreateDirectory(dir);
        try
        {
            var withPage = ctor!.Invoke(
                new object?[] { Emit(Report(dir, ChangePassword)), null, 0, 0, null });
            Assert.NotNull(property!.GetValue(withPage));

            // Negative, and it is what makes the line above mean anything: the SAME reader on
            // a document with no <RequestPage> answers null.
            var withoutPage = ctor.Invoke(
                new object?[] { Emit(Report(dir, NoRequestPageNode)), null, 0, 0, null });
            Assert.Null(property.GetValue(withoutPage));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
