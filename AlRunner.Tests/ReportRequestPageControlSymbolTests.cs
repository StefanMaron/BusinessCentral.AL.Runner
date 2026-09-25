// A precompiled report's request-page control tree, as BcAppSymbolCache reads it out of
// SymbolReference.json (#4661). The runner answers TestRequestPage.<control>.Visible() /
// .Editable() for such a report from these rows, because the request page's MasterPage carries
// none of the report's own controls. The AL-observable claim is pinned in the corpus
// (codeunit 60008 "Req Page Bound Visibility"); this pins the runner's own parse.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class ReportRequestPageControlSymbolTests
{
    private const int WithControls = 9821;
    private const int NoControls = 9822;
    private const int NoRequestPage = 9823;

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "0f3c2a91-6d4e-4b7a-9e21-5c8d7a6b4f10",
          "Name": "Request Page Control Fixture",
          "Namespaces": [
            {
              "Name": "Fixture",
              "Reports": [
                {
                  "Id": {{WithControls}},
                  "Name": "With Controls",
                  "DataItems": [],
                  "RequestPage": {
                    "Id": 0,
                    "Name": "RequestOptionsPage",
                    "Controls": [
                      {
                        "Id": 158581191,
                        "Name": "content",
                        "Controls": [
                          {
                            "Id": 1678175880,
                            "Name": "Options",
                            "Kind": 1,
                            "Properties": [ { "Name": "Visible", "Value": "ShowOptions" } ],
                            "Controls": [
                              {
                                "Id": 966077527,
                                "Name": "VATDate",
                                "Kind": 8,
                                "Properties": [
                                  { "Name": "Editable", "Value": "VATDateEnabled" },
                                  { "Name": "Visible", "Value": "VATDateEnabled" },
                                  { "Name": "SourceExpression", "Value": "VATDateReq" }
                                ]
                              },
                              {
                                "Id": 1265798802,
                                "Name": "Ship",
                                "Kind": 8,
                                "Properties": [
                                  { "Name": "Enabled", "Value": "0" },
                                  { "Name": "SourceExpression", "Value": "ShipReq" }
                                ]
                              }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                },
                {
                  "Id": {{NoControls}},
                  "Name": "No Controls",
                  "DataItems": [],
                  "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" }
                },
                {
                  "Id": {{NoRequestPage}},
                  "Name": "No Request Page",
                  "DataItems": []
                }
              ]
            }
          ]
        }
        """;

    private static BcAppSymbolCache.ReportSymbol Report(string dir, int id)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }
        return Assert.Single(BcAppSymbolCache.Get(appPath).Reports, r => r.Id == id);
    }

    private static T InScratch<T>(string name, Func<string, T> body)
    {
        var dir = TestScratch.Dir(name);
        Directory.CreateDirectory(dir);
        try { return body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AFieldCarriesItsDeclaredExpressionsAndItsEnclosingGroup()
    {
        var controls = InScratch("al-runner-rp-control-symbol-field",
            dir => Report(dir, WithControls).RequestPageControls);

        Assert.NotNull(controls);
        var vatDate = Assert.Single(controls!, c => c.Id == 966077527);
        Assert.Equal("VATDateEnabled", vatDate.VisibleExpr);
        Assert.Equal("VATDateEnabled", vatDate.EditableExpr);
        // Declares no Enabled: null is "declares none", never a defaulted literal.
        Assert.Null(vatDate.EnabledExpr);
        Assert.Equal(1678175880, vatDate.ParentId);

        var ship = Assert.Single(controls!, c => c.Id == 1265798802);
        Assert.Equal("0", ship.EnabledExpr);
        Assert.Null(ship.VisibleExpr);
        Assert.Equal(1678175880, ship.ParentId);
    }

    [Fact]
    public void TheGroupChainIsWalkableToTheTop()
    {
        var controls = InScratch("al-runner-rp-control-symbol-group",
            dir => Report(dir, WithControls).RequestPageControls)!;

        var group = Assert.Single(controls, c => c.Id == 1678175880);
        Assert.Equal("ShowOptions", group.VisibleExpr);
        Assert.Equal(158581191, group.ParentId);

        var content = Assert.Single(controls, c => c.Id == 158581191);
        Assert.Equal(0, content.ParentId);
        Assert.Equal(4, controls.Count);
    }

    [Fact]
    public void AReportStatingNoControlsCarriesNone()
    {
        var (noControls, noRequestPage) = InScratch("al-runner-rp-control-symbol-none",
            dir => (Report(dir, NoControls).RequestPageControls, Report(dir, NoRequestPage).RequestPageControls));

        Assert.Null(noControls);
        Assert.Null(noRequestPage);
    }
}
