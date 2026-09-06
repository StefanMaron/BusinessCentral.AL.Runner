// DependencyControlsSharingSourceExpressionTests — pins the runner's own C# mechanism for
// issue #3211's precompiled arm.
//
// THE DEFECT
// ----------
// The AL compiler registers ONE <Expression> per distinct binding TEXT on a page, named after
// the FIRST control that uses it, and every later control over the same text points at that one
// through its own DataColumnName. RunnerPageInstance.TryGetSourceExpression looked the binding
// up by "Control" + controlId alone, so the SECOND control over a page global resolved to
// nothing and was refused as unbound — blaming the source table for a control that is bound
// perfectly well. Measured on Microsoft's Base Application 28.1: page 1612 "Office Admin.
// Credentials" registers Control1176233145 (O365Password) and NOT Control492048779
// (OnPremPassword), though both are declared over the same page global PasswordText.
//
// On a page the runner compiled itself, DataColumnName is readable straight off the merged
// metadata. On a page that ships PRECOMPILED in a dependency .app it is not — that page's
// reconstructed metadata carries no control tree at all (see DependencyPageMetadataXml's
// "what is deliberately omitted"). What the dependency DOES state per control is the AL binding
// text, and the compiler's dedup key IS that text, so the siblings can be named exactly.
//
// WHAT THIS PROVES, AND WHAT IT DELIBERATELY DOES NOT
// ---------------------------------------------------
// The BC-observable claim — that a TestPage resolves the second control over one page binding
// as readily as the first, in both directions — is a statement about BC, not about the runner,
// and is adjudicated by a real service tier in corpus PR
// StefanMaron/BusinessCentral.AL.Language.Tests#209 (codeunit 60773 "TP Shared Bind Tests").
// This file pins the narrower runner-only mechanism underneath the PRECOMPILED half of the fix,
// at both ends: it must name the siblings a dependency page really declares, and it must name
// nothing for a control that shares its binding with no other.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as RecordPatchesGetPageControlFieldMapDependencyTests: RecordPatches' dependency
// page state (_bcAppPaths and the symbol caches behind it) resolves through the process-global
// CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyControlsSharingSourceExpressionTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // Distinctive ids: RecordPatches' dependency-page state is process-global, so an id another
    // test or fixture also declares would risk reading back that one's cached answer.
    private const int PageId = 88220501;

    private const int SharedFirstId = 640645001;   // SourceExpression = PasswordText
    private const int SharedSecondId = 640645002;  // SourceExpression = PasswordText  (same binding)
    private const int SharedThirdId = 640645003;   // SourceExpression = passwordtext  (same binding, other case)
    private const int LoneControlId = 640645004;   // SourceExpression = OtherText     (its own binding)
    private const int UnknownControlId = 640645099; // declared by nothing

    // Modelled on the real shape: page 1612 "Office Admin. Credentials" declares two controls
    // over the page global PasswordText and one over a different global. The third control here
    // spells the same identifier in another case, which AL treats as the same variable.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88220501,
              "Name": "DCSSE Dep Page",
              "Properties": [
                { "Name": "PageType", "Value": "Card" }
              ],
              "Controls": [
                {
                  "Kind": 1,
                  "Id": 1,
                  "Name": "content",
                  "Controls": [
                    {
                      "Kind": 8,
                      "Id": 640645001,
                      "Name": "O365Password",
                      "Properties": [ { "Name": "SourceExpression", "Value": "PasswordText" } ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640645002,
                      "Name": "OnPremPassword",
                      "Properties": [ { "Name": "SourceExpression", "Value": "PasswordText" } ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640645003,
                      "Name": "ThirdPassword",
                      "Properties": [ { "Name": "SourceExpression", "Value": "passwordtext" } ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640645004,
                      "Name": "Unshared",
                      "Properties": [ { "Name": "SourceExpression", "Value": "OtherText" } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-shared-source-expression-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecondControlOverOneBinding_NamesTheFirstAsASibling()
        => WithDependencyApp(() =>
        {
            var siblings = RecordPatches.DependencyControlsSharingSourceExpression(PageId, SharedSecondId);

            // The one that matters: the control the compiler did NOT register an expression for
            // must be able to name the control it DID register one for. Asserting membership
            // rather than the whole set would let an implementation that returns every control
            // on the page pass, so the exact set is asserted instead.
            Assert.Equal(new[] { SharedFirstId, SharedThirdId }, siblings.OrderBy(x => x).ToArray());
        });

    [Fact]
    public void FirstControlOverOneBinding_NamesTheLaterOnesAndNotItself()
        => WithDependencyApp(() =>
        {
            var siblings = RecordPatches.DependencyControlsSharingSourceExpression(PageId, SharedFirstId);

            // Symmetric, and never self-referential: returning the control itself would make
            // RunnerPageInstance retry the same key it has already missed.
            Assert.Equal(new[] { SharedSecondId, SharedThirdId }, siblings.OrderBy(x => x).ToArray());
            Assert.DoesNotContain(SharedFirstId, siblings);
        });

    [Fact]
    public void ControlWithItsOwnBinding_NamesNoSibling()
        => WithDependencyApp(() =>
        {
            // Negative. A control whose binding nothing else declares must name nothing, so a
            // control the page genuinely never registered stays unresolved and still refuses
            // loudly instead of borrowing an unrelated control's value.
            Assert.Empty(RecordPatches.DependencyControlsSharingSourceExpression(PageId, LoneControlId));
        });

    [Fact]
    public void ControlTheDependencyDoesNotDeclare_NamesNoSibling()
        => WithDependencyApp(() =>
        {
            // Negative. An id the symbol file says nothing about has no binding text to match
            // on, so it must not collect the page's controls indiscriminately.
            Assert.Empty(RecordPatches.DependencyControlsSharingSourceExpression(PageId, UnknownControlId));
        });

    [Fact]
    public void PageNoDependencyDeclares_NamesNoSibling()
        => WithDependencyApp(() =>
        {
            // Negative. A page the runner compiled itself (or one nothing declares) must fall
            // through this path entirely — it is answered from DataColumnName instead.
            Assert.Empty(RecordPatches.DependencyControlsSharingSourceExpression(88220502, SharedSecondId));
        });
}
