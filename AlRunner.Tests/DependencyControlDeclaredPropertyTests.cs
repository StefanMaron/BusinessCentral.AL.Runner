// Issue #3504 / #2460: a page shipping precompiled in a dependency .app gets no control tree
// in its synthesized runtime metadata (DependencyPageMetadataXml, by design — a control's VALUE
// BINDING is IL, not XML), so RunnerPageInstance.ControlDefinition(id) answers null for every
// control on it and EvaluateProperty's `raw is null => true` arm answered "the AL declared none"
// for a page that declares plenty.
//
// The declared string IS stated, per control, by the dependency's own SymbolReference.json — the
// same file GetPageControlFieldMap and the "Page Control Field" virtual table already read. These
// tests pin the resolver that reads it.
//
// Measured on Base Application 28.1.49838.53910 (2,610 pages / 37,185 field controls), which is
// what makes this worth a resolver rather than a special case: Editable is declared on 5,920
// controls, Visible on 11,505, Enabled on 797 — 18,222 declarations the runner answered `true`
// for regardless. 83% of the Editable ones are the compile-time literal, needing no page state
// at all. Page 46 "Sales Order Subform" — the issue's own surface — declares Editable on 19 of
// its 102 field controls, nine of them the literal "false".
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyControlsSharingSourceExpressionTests: RecordPatches' dependency page
// state (_bcAppPaths and the symbol caches behind it) resolves through the process-global
// CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyControlDeclaredPropertyTests
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
    private const int PageId = 88330501;

    private const int LiteralFalseId = 640646001;   // Editable = "false"
    private const int PageVariableId = 640646002;   // Editable = "InvDiscAmountEditable"
    private const int ExpressionId = 640646003;     // Editable = "not IsCommentLine"
    private const int UndeclaredId = 640646004;     // declares no Editable at all
    private const int VisibleEnabledId = 640646005; // Visible + Enabled, no Editable
    private const int UnknownControlId = 640646099; // declared by nothing

    // Modelled attribute-for-attribute on Base Application page 46 "Sales Order Subform", read
    // out of the shipped symbol file: `Editable = "false"` on "Document No.",
    // `Editable = "InvDiscAmountEditable"` on "Invoice Discount Amount" (the issue's own
    // control — a PAGE VARIABLE, not a table field and not a constant), and
    // `Editable = "not IsCommentLine"` on "Quantity".
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88330501,
              "Name": "DCDP Dep Page",
              "Properties": [
                { "Name": "PageType", "Value": "ListPart" }
              ],
              "Controls": [
                {
                  "Kind": 1,
                  "Id": 1,
                  "Name": "content",
                  "Controls": [
                    {
                      "Kind": 8,
                      "Id": 640646001,
                      "Name": "Document No.",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "Rec.\"Document No.\"" },
                        { "Name": "Editable", "Value": "false" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640646002,
                      "Name": "Invoice Discount Amount",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "InvoiceDiscountAmount" },
                        { "Name": "Editable", "Value": "InvDiscAmountEditable" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640646003,
                      "Name": "Quantity",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "Rec.Quantity" },
                        { "Name": "Editable", "Value": "not IsCommentLine" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640646004,
                      "Name": "Description",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "Rec.Description" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 640646005,
                      "Name": "Tax Liable",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "Rec.\"Tax Liable\"" },
                        { "Name": "Visible", "Value": "ShowTaxLiable" },
                        { "Name": "Enabled", "Value": "false" }
                      ]
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
        var dir = TestScratch.Dir("al-runner-dep-control-declared-property-tests");
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

    // ── the whole RED: the declared string must come back verbatim ────────────────────────

    [Fact]
    public void LiteralFalse_IsReadBackVerbatim()
        => WithDependencyApp(() =>
        {
            // The 83% case, and the one that needs no page state whatsoever: BC's compiler folded
            // this to a literal, so answering it correctly is pure metadata. Verbatim, NOT
            // normalised to a bool here — EvaluateProperty owns the literal-vs-expression
            // decision, and a resolver that pre-decided it would have to duplicate that rule.
            Assert.Equal("false",
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, LiteralFalseId, "Editable"));
        });

    [Fact]
    public void PageVariableExpression_IsReadBackVerbatim()
        => WithDependencyApp(() =>
        {
            // The issue's own control. `InvDiscAmountEditable` is a page variable — one of page
            // 46's 47 — so the answer is only knowable by evaluating it against live page state,
            // which is exactly what EvaluateProperty's registered-expression lookup does.
            Assert.Equal("InvDiscAmountEditable",
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, PageVariableId, "Editable"));
        });

    [Fact]
    public void CompoundExpression_IsReadBackVerbatim()
        => WithDependencyApp(() =>
        {
            // Not every declaration is a bare identifier: 347 of page-controls' 5,920 Editable
            // declarations in Base Application are a compound expression. PageControlExpression
            // parses these; the resolver must not filter them out on the way past.
            Assert.Equal("not IsCommentLine",
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, ExpressionId, "Editable"));
        });

    [Fact]
    public void EveryPropertyName_ResolvesIndependently()
        => WithDependencyApp(() =>
        {
            // Visible and Enabled are the same defect on the same control (#2460 is the action
            // half of it), so the resolver is keyed on the property name rather than hardcoding
            // Editable. Asserting all three on ONE control proves the key is read, not guessed:
            // this control declares Visible and Enabled and no Editable.
            Assert.Equal("ShowTaxLiable",
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, VisibleEnabledId, "Visible"));
            Assert.Equal("false",
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, VisibleEnabledId, "Enabled"));
            Assert.Null(
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, VisibleEnabledId, "Editable"));
        });

    // ── negatives: null must keep meaning "the AL declared none" ──────────────────────────

    [Fact]
    public void ControlDeclaringNoSuchProperty_AnswersNull()
        => WithDependencyApp(() =>
        {
            // The load-bearing negative. `null` is EvaluateProperty's "publishes no such property
            // at all", whose answer is the AL default of true — correct HERE, because this control
            // genuinely declares nothing. A resolver that answered "" or "true" would collapse
            // exactly the distinction this fix exists to restore.
            Assert.Null(
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, UndeclaredId, "Editable"));
        });

    [Fact]
    public void ControlTheDependencyDoesNotDeclare_AnswersNull()
        => WithDependencyApp(() =>
        {
            // An id the symbol file says nothing about must not borrow another control's
            // declaration — the same discipline DependencyControlsSharingSourceExpression keeps.
            Assert.Null(
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, UnknownControlId, "Editable"));
        });

    [Fact]
    public void PageNoDependencyDeclares_AnswersNull()
        => WithDependencyApp(() =>
        {
            // A page the runner compiled itself must fall through this path entirely: its real
            // ControlDefinition carries the property and is authoritative.
            Assert.Null(
                RecordPatches.TryGetDependencyControlDeclaredProperty(88330502, LiteralFalseId, "Editable"));
        });

    [Fact]
    public void UnknownPropertyName_AnswersNull()
        => WithDependencyApp(() =>
        {
            // Only the three UIElementDefinition booleans are resolvable here. A name outside
            // that set must answer null rather than being mapped onto one of them, so a future
            // caller cannot silently get Editable's value for a different question.
            Assert.Null(
                RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, LiteralFalseId, "Caption"));
        });
}
