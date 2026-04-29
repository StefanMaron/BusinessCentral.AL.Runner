using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for the DepExtractor feature (--extract-deps command).
/// Unit tests use inline AL source only.
/// Integration tests require local .app and extension source; they are skipped (not silently passed)
/// when those paths are absent so CI shows them as skipped rather than green.
/// </summary>
public class DepExtractorTests
{
    // -----------------------------------------------------------------------
    // Paths — integration tests skipped when not present
    // -----------------------------------------------------------------------

    private static readonly string BaAppPath =
        "/home/stefan/Documents/ALPackages/Microsoft_Base Application_24.5.23489.26846.app";

    private static readonly string ExtensionSrcDir =
        "/home/stefan/Documents/Repos/SMC/CustomerBlockedModification/src";

    private static bool HasBaApp => File.Exists(BaAppPath);
    private static bool HasExtensionSrc => Directory.Exists(ExtensionSrcDir);

    // -----------------------------------------------------------------------
    // CollectExternalReferences — unit tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CollectReferences_FindsTableRefsFromSource()
    {
        const string source = """
            codeunit 99001 "Test"
            {
                procedure Run()
                var
                    Cust: Record Customer;
                    SH: Record "Sales Header";
                begin
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Customer", refs.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Sales Header", refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsEnumFieldType()
    {
        const string source = """
            table 50000 "My Table"
            {
                fields {
                    field(1; Status; Enum "Sales Line Type") { }
                    field(2; DocType; Enum "Gen. Journal Document Type") { }
                }
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Sales Line Type", refs.Enums, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Gen. Journal Document Type", refs.Enums, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsEventSubscriberTargetTable()
    {
        const string source = """
            codeunit 99002 "Test"
            {
                [EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterInsert', '', false, false)]
                local procedure OnAfterInsert(var Rec: Record "Sales Header")
                begin
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Sales Header", refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsCodeunitRefs()
    {
        const string source = """
            codeunit 99003 "Test"
            {
                procedure Run()
                begin
                    Codeunit.Run(Codeunit::"Sales-Post");
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Sales-Post", refs.Codeunits, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quoted-identifier static-call references — the AL pattern
    /// <c>"Enum Name".FromInteger(123)</c> parses as a MemberAccessExpression
    /// where the LHS is an IdentifierName carrying a quoted text token.
    /// Without explicit handling, this reference is invisible to the BFS,
    /// so the enum is never pulled into the slice and AL0118 fires at compile
    /// time. Concrete BC 27 example: DocumentPrint.Codeunit.al calls
    /// <c>"Sales Order Print Option".FromInteger(...)</c> on an enum that lives
    /// in a separate file.
    /// </summary>
    [Fact]
    public void CollectExternalReferences_QuotedEnumStaticCall_IsCollectedAsEnum()
    {
        const string source = """
            codeunit 99100 "X"
            {
                procedure F()
                var x: Integer;
                begin
                    x := "My Enum".FromInteger(123);
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("My Enum", refs.Enums, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Companion: <c>"Enum Name"::"Member"</c> parses as OptionAccessExpression
    /// with a quoted-identifier prefix. The existing OptionAccess switch only
    /// recognises bare keyword prefixes (Codeunit, Page, …). When the prefix
    /// itself is a quoted identifier, treat it as an enum reference.
    /// </summary>
    [Fact]
    public void CollectExternalReferences_QuotedEnumOptionAccess_IsCollectedAsEnum()
    {
        const string source = """
            codeunit 99101 "X"
            {
                procedure F()
                begin
                    if true then
                        case 1 of
                            1: exit;
                        end;
                    Foo("My Enum"::"Some Value");
                end;
                local procedure Foo(v: Variant) begin end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("My Enum", refs.Enums, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Negative guard: a regular method call on a record variable
    /// (<c>Cust.Insert()</c>) must NOT spuriously add "Cust" as an enum.
    /// Only quoted-identifier LHSes count as object-name references.
    /// </summary>
    [Fact]
    public void CollectExternalReferences_UnquotedMethodCall_DoesNotAddBogusEnum()
    {
        const string source = """
            codeunit 99102 "X"
            {
                procedure F()
                var Cust: Record Customer;
                begin
                    Cust.Insert();
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.DoesNotContain("Cust", refs.Enums, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsPageRefFromVariableDeclaration()
    {
        const string source = """
            codeunit 99004 "Test"
            {
                procedure Run()
                var P: Page "Customer Card";
                begin end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Customer Card", refs.Pages, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsPageRefFromOptionAccess()
    {
        const string source = """
            codeunit 99005 "Test"
            {
                procedure Run()
                begin
                    Page.Run(Page::"Customer Card");
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Customer Card", refs.Pages, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsReportAndQueryRefs()
    {
        const string source = """
            codeunit 99006 "Test"
            {
                procedure Run()
                var
                    R: Report "Sales Invoice";
                    Q: Query "My Query";
                begin
                    Report.Run(Report::"Customer List");
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("Sales Invoice", refs.Reports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("My Query", refs.Queries, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Customer List", refs.Reports, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsInterfaceRef()
    {
        const string source = """
            codeunit 99007 "Test"
            {
                procedure Run(Handler: Interface IMyHandler)
                begin end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("IMyHandler", refs.Interfaces, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_EmptySource_ReturnsEmptyRefs()
    {
        var refs = DepExtractor.CollectExternalReferences(new[] { "" });

        Assert.Empty(refs.Tables);
        Assert.Empty(refs.Codeunits);
        Assert.Empty(refs.Enums);
        Assert.Empty(refs.Pages);
        Assert.Empty(refs.Reports);
        Assert.Empty(refs.Queries);
        Assert.Empty(refs.XmlPorts);
        Assert.Empty(refs.Interfaces);
    }

    // -----------------------------------------------------------------------
    // SourceMatchesAnyRef — unit tests covering all object kinds
    // -----------------------------------------------------------------------

    [Fact]
    public void SourceMatchesAnyRef_MatchesTableDefinition()
    {
        const string src = """
            table 18 Customer
            {
                fields { field(1; "No."; Code[20]) { } }
            }
            """;

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Tables = { "Customer" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesTableExtensionByBaseTable()
    {
        // A tableextension of Customer must be included when Customer is in the slice,
        // even though the extension's own name is different.
        const string src = """
            tableextension 50001 "My Customer Ext" extends Customer
            {
                fields { field(50000; "My Field"; Text[50]) { } }
            }
            """;

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Tables = { "Customer" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_DoesNotMatchTableExtensionOfUnrelatedTable()
    {
        const string src = """
            tableextension 50002 "My Vendor Ext" extends Vendor
            {
                fields { field(50000; "My Field"; Text[50]) { } }
            }
            """;

        Assert.False(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Tables = { "Customer" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesEnumDefinition()
    {
        const string src = """
            enum 5800 "Item Journal Template Type"
            {
                value(0; Purchase) { }
                value(1; Sales) { }
            }
            """;

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Enums = { "Item Journal Template Type" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesEnumExtensionByBaseEnum()
    {
        const string src = """
            enumextension 50003 "My Enum Ext" extends "Item Journal Template Type"
            {
                value(50000; MyValue) { }
            }
            """;

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Enums = { "Item Journal Template Type" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesPageDefinition()
    {
        const string src = "page 21 \"Customer Card\" { layout { area(content) { } } }";

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Pages = { "Customer Card" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesPageExtensionByBasePage()
    {
        const string src = "pageextension 50010 MyExt extends \"Customer Card\" { }";

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Pages = { "Customer Card" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesReportDefinition()
    {
        const string src = "report 206 \"Sales Invoice\" { dataset { } }";

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Reports = { "Sales Invoice" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_MatchesInterfaceDefinition()
    {
        const string src = "interface IMyHandler { procedure Handle(); }";

        Assert.True(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Interfaces = { "IMyHandler" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_DoesNotMatchUnrelatedObject()
    {
        const string src = """
            table 999 "Unrelated Table"
            {
                fields { field(1; "No."; Code[20]) { } }
            }
            """;

        Assert.False(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Tables = { "Customer" } }));
    }

    [Fact]
    public void SourceMatchesAnyRef_DoesNotMatchReferencingFile()
    {
        // A file that only REFERENCES Customer (calls Customer.Get) must NOT match
        // — the text-fallback bug would have incorrectly included this.
        const string src = """
            codeunit 50004 "My Logic"
            {
                procedure Run()
                var
                    Cust: Record Customer;
                begin
                    Cust.Get('C001');
                end;
            }
            """;

        // This codeunit is NOT named "Customer" and does not define the Customer table.
        Assert.False(DepExtractor.SourceMatchesAnyRef(src,
            new ExternalRefs { Tables = { "Customer" } }));
    }

    // -----------------------------------------------------------------------
    // Directory input — unit test using temp directory
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractDeps_DirectoryInput_ExtractsMatchingObjects()
    {
        // Build a tiny dep source directory with a Customer table and an unrelated table.
        var depDir = Path.Combine(Path.GetTempPath(), "al-dep-dir-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            // Dependency: a Customer table and an unrelated table
            File.WriteAllText(Path.Combine(depDir, "Customer.al"), """
                table 18 Customer { fields { field(1; "No."; Code[20]) { } } }
                """);
            File.WriteAllText(Path.Combine(depDir, "Unrelated.al"), """
                table 999 "Some Other Table" { fields { field(1; X; Integer) { } } }
                """);

            // Extension: references Customer only
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run()
                    var Cust: Record Customer;
                    begin Cust.FindFirst(); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("table 18 Customer"));
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("Some Other Table"));
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // -----------------------------------------------------------------------
    // App-boundary rules: extensions and event subscribers from foreign apps
    // are dropped to keep the slice scoped to the consumer's actual surface.
    // See issue #1544 for the rule rationale; #1545 tracks the future opt-in
    // for full-app inclusion via direct reference.
    // -----------------------------------------------------------------------

    /// <summary>Layout: depRoot/AppA/Customer.al, depRoot/AppB/CustomerExt.al
    /// extending Customer. Consumer references Customer only. Expected: AppA's
    /// Customer table in slice, AppB's tableextension NOT in slice.</summary>
    [Fact]
    public void ExtractDeps_DoesNotPullCrossAppTableExtension()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-cross-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-cross-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-cross-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(Path.Combine(depRoot, "AppB"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Customer.al"),
                "table 18 Customer { fields { field(1; \"No.\"; Code[20]) { } } }");
            File.WriteAllText(Path.Combine(depRoot, "AppB", "CustomerExt.al"),
                "tableextension 50001 \"Cust Ext\" extends Customer { fields { field(50000; Foo; Text[10]) { } } }");
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run() var Cust: Record Customer; begin Cust.FindFirst(); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("table 18 Customer"));
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("tableextension 50001"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>Layout: AppA holds both Customer and a tableextension of Customer.
    /// Consumer references Customer. Expected: tableextension IS in slice (same app).</summary>
    [Fact]
    public void ExtractDeps_PullsSameAppTableExtension()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-same-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-same-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-same-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Customer.al"),
                "table 18 Customer { fields { field(1; \"No.\"; Code[20]) { } } }");
            File.WriteAllText(Path.Combine(depRoot, "AppA", "CustomerExt.al"),
                "tableextension 50001 \"Cust Ext\" extends Customer { fields { field(50000; Foo; Text[10]) { } } }");
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run() var Cust: Record Customer; begin Cust.FindFirst(); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("tableextension 50001"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>Cross-app event subscriber must be dropped: AppA defines codeunit Foo,
    /// AppB has a codeunit subscribing to events on Foo. Consumer references Foo only.
    /// Expected: AppB's subscriber NOT in slice.</summary>
    [Fact]
    public void ExtractDeps_DoesNotPullCrossAppEventSubscriber()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-csub-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-csub-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-csub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(Path.Combine(depRoot, "AppB"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Foo.al"), """
                codeunit 60001 Foo
                {
                    [IntegrationEvent(false, false)]
                    procedure OnSomething() begin end;
                }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppB", "ForeignSub.al"), """
                codeunit 60002 ForeignSub
                {
                    [EventSubscriber(ObjectType::Codeunit, Codeunit::Foo, 'OnSomething', '', false, false)]
                    local procedure OnFoo() begin end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Foo); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("codeunit 60001 Foo"));
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("ForeignSub"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>Same-app event subscriber must be pulled: AppA defines Foo and a
    /// sibling codeunit that subscribes to it. Consumer references Foo. Expected:
    /// sibling subscriber IS in slice.</summary>
    [Fact]
    public void ExtractDeps_PullsSameAppEventSubscriber()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-ssub-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-ssub-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-ssub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Foo.al"), """
                codeunit 60001 Foo
                {
                    [IntegrationEvent(false, false)]
                    procedure OnSomething() begin end;
                }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Sibling.al"), """
                codeunit 60002 SiblingSub
                {
                    [EventSubscriber(ObjectType::Codeunit, Codeunit::Foo, 'OnSomething', '', false, false)]
                    local procedure OnFoo() begin end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Foo); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("SiblingSub"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // -----------------------------------------------------------------------
    // Integration tests — require local .app and extension source
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractDeps_CustomerBlockedMod_FindsExpectedTables()
    {
        if (!HasBaApp || !HasExtensionSrc) return; // requires local BA .app and CustomerBlockedModification source

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(ExtensionSrcDir, new[] { BaAppPath }, outDir);
            Assert.Equal(0, rc);

            var texts = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories)
                .Select(File.ReadAllText).ToList();

            Assert.True(texts.Any(t => t.Contains("table 18") || t.Contains("table 18 Customer")),
                "Expected Customer table definition in extracted slice");
            Assert.True(texts.Any(t => t.Contains("Sales Header")),
                "Expected Sales Header table definition in extracted slice");
            Assert.True(texts.Any(t => t.Contains("User Setup")),
                "Expected User Setup table definition in extracted slice");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ExtractDeps_CustomerBlockedMod_IncludesTableExtensions()
    {
        if (!HasBaApp || !HasExtensionSrc) return; // requires local BA .app and CustomerBlockedModification source

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(ExtensionSrcDir, new[] { BaAppPath }, outDir);
            Assert.Equal(0, rc);

            var texts = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories)
                .Select(File.ReadAllText).ToList();

            // There must be at least one tableextension in the BA slice — BA has many
            // tableextensions of Customer and Sales Header.
            Assert.True(texts.Any(t => t.Contains("tableextension")),
                "Expected at least one tableextension in extracted slice");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    // -----------------------------------------------------------------------
    // Error handling
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractDeps_NonExistentSrcDir_ReturnsNonZero()
    {
        int rc = DepExtractor.ExtractDeps(
            extensionSrcDir: "/nonexistent/path",
            depSources: new[] { BaAppPath },
            outputDir: Path.GetTempPath());

        Assert.NotEqual(0, rc);
    }

    /// <summary>
    /// Cross-app enum extension whose host app is itself in the slice (because the
    /// consumer references some other object from that app) MUST be pulled in.
    /// Otherwise downstream code that uses the extended enum value fails to compile
    /// (AL0132). Layout: AppA defines enum Color; AppB defines codeunit Painter that
    /// uses Color::Red AND enumextension ColorExt extending Color with a Red value.
    /// Consumer references Painter — both Painter and ColorExt should be in the slice.
    /// </summary>
    [Fact]
    public void ExtractDeps_PullsCrossAppEnumExtension_WhenExtensionAppInSlice()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-xenum-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-xenum-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-xenum-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(Path.Combine(depRoot, "AppB"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Color.al"),
                "enum 60050 Color { value(0; None) { } }");
            File.WriteAllText(Path.Combine(depRoot, "AppB", "ColorExt.al"),
                "enumextension 60051 ColorExt extends Color { value(1; Red) { } }");
            File.WriteAllText(Path.Combine(depRoot, "AppB", "Painter.al"), """
                codeunit 60052 Painter
                {
                    procedure Paint() var c: Enum Color; begin c := Enum::Color::Red; end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 60099 "My Codeunit"
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Painter); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            // Positive: cross-app codeunit pulled by direct ref.
            Assert.Contains(files, f => File.ReadAllText(f).Contains("codeunit 60052 Painter"));
            // Positive: base enum pulled by transitive ref from Painter.
            Assert.Contains(files, f => File.ReadAllText(f).Contains("enum 60050 Color"));
            // The fix: cross-app enumextension whose app is already in the slice IS pulled.
            Assert.Contains(files, f => File.ReadAllText(f).Contains("enumextension 60051 ColorExt"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Cross-app page extension whose host app is itself in the slice MUST be pulled in.
    /// Otherwise event subscribers in the consumer app (or in dependent apps) referring
    /// to a publisher declared inside the page extension fail with AL0280. Layout: AppA
    /// defines page BasePage; AppB defines codeunit PageRunner that runs BasePage AND
    /// pageextension BasePageExt with an action on the page. Consumer references
    /// PageRunner — BasePage, PageRunner and BasePageExt all belong in the slice.
    /// </summary>
    [Fact]
    public void ExtractDeps_PullsCrossAppPageExtension_WhenExtensionAppInSlice()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-xpage-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-xpage-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-xpage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(Path.Combine(depRoot, "AppB"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "BasePage.al"), """
                page 60060 BasePage
                {
                    PageType = Card;
                    layout { area(content) { } }
                    actions { area(processing) { } }
                }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppB", "BasePageExt.al"), """
                pageextension 60061 BasePageExt extends BasePage
                {
                    actions
                    {
                        addfirst(processing)
                        {
                            action(MyAction) { trigger OnAction() begin end; }
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppB", "PageRunner.al"), """
                codeunit 60062 PageRunner
                {
                    procedure Run() begin Page.Run(Page::BasePage); end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 60098 "My Codeunit"
                {
                    procedure Run() begin Codeunit.Run(Codeunit::PageRunner); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f => File.ReadAllText(f).Contains("codeunit 60062 PageRunner"));
            Assert.Contains(files, f => File.ReadAllText(f).Contains("page 60060 BasePage"));
            // The fix: cross-app pageextension whose app is already in the slice IS pulled.
            Assert.Contains(files, f => File.ReadAllText(f).Contains("pageextension 60061 BasePageExt"));
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    [Fact]
    public void ExtractDeps_NonExistentDepSource_ReturnsNonZero()
    {
        var extDir = Path.Combine(Path.GetTempPath(), "al-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(extDir);
        try
        {
            int rc = DepExtractor.ExtractDeps(
                extensionSrcDir: extDir,
                depSources: new[] { "/nonexistent/source" },
                outputDir: Path.GetTempPath());

            Assert.NotEqual(0, rc);
        }
        finally
        {
            Directory.Delete(extDir, true);
        }
    }

    /// <summary>
    /// When a dep codeunit declares local vars of types that exist nowhere in any dep source,
    /// ExtractDeps must auto-generate blank-shell AL stub files for those types so that the
    /// BC compiler can bind the consumer's locals without hitting NavTypeKind.None.
    /// </summary>
    [Fact]
    public void ExtractDeps_GeneratesStubs_ForUnresolvableMissingObjects()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-stubs-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-stubs-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-stubs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "AppACodeunit.al"), """
                codeunit 60100 AppACodeunit
                {
                    procedure DoSomething()
                    var
                        p: Page "Missing Phantom Page";
                        t: Record "Missing Phantom Table";
                    begin
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "Consumer.al"), """
                codeunit 60199 Consumer
                {
                    procedure Run()
                    begin
                        Codeunit.Run(Codeunit::AppACodeunit);
                    end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            var allFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);

            // Positive: a stub for the missing page is generated
            var pageStub = allFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("MissingPhantomPage", StringComparison.OrdinalIgnoreCase)
                && f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(pageStub);
            var pageStubContent = File.ReadAllText(pageStub!);
            Assert.StartsWith("page ", pageStubContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"Missing Phantom Page\"", pageStubContent);

            // Positive: a stub for the missing table is generated
            var tableStub = allFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("MissingPhantomTable", StringComparison.OrdinalIgnoreCase)
                && f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(tableStub);
            var tableStubContent = File.ReadAllText(tableStub!);
            Assert.StartsWith("table ", tableStubContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"Missing Phantom Table\"", tableStubContent);

            // Positive: stub IDs must be in the reserved range >= 1_999_900_000
            var pageIdMatch = System.Text.RegularExpressions.Regex.Match(pageStubContent, @"page\s+(\d+)");
            Assert.True(pageIdMatch.Success);
            Assert.True(int.Parse(pageIdMatch.Groups[1].Value) >= 1_999_900_000);

            var tableIdMatch = System.Text.RegularExpressions.Regex.Match(tableStubContent, @"table\s+(\d+)");
            Assert.True(tableIdMatch.Success);
            Assert.True(int.Parse(tableIdMatch.Groups[1].Value) >= 1_999_900_000);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// When a missing object is declared in a symbol package passed via packagePaths,
    /// no stub should be generated for it (regression guard for AL0197 collisions).
    /// </summary>
    [Fact]
    public void ExtractDeps_DoesNotGenerateStubs_WhenObjectIsInPackage()
    {
        var depRoot  = Path.Combine(Path.GetTempPath(), "al-dep-pkgstub-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir   = Path.Combine(Path.GetTempPath(), "al-out-pkgstub-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir   = Path.Combine(Path.GetTempPath(), "al-ext-pkgstub-" + Guid.NewGuid().ToString("N")[..8]);
        var pkgDir   = Path.Combine(Path.GetTempPath(), "al-pkg-pkgstub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            Directory.CreateDirectory(pkgDir);

            // AppA codeunit references "External Page" which is NOT in any depRoot source.
            File.WriteAllText(Path.Combine(depRoot, "AppA", "AppACodeunit.al"), """
                codeunit 60200 AppAExternalRef
                {
                    procedure DoSomething()
                    var
                        p: Page "External Page";
                    begin
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "Consumer.al"), """
                codeunit 60299 ConsumerExt
                {
                    procedure Run()
                    begin
                        Codeunit.Run(Codeunit::AppAExternalRef);
                    end;
                }
                """);

            // Create a synthetic .app package that declares "External Page" in its SymbolReference.json.
            // Format: NAVX header (8 bytes) + ZIP containing SymbolReference.json.
            var symRefJson = """
                {
                  "Id": "aaaabbbb-1111-2222-3333-ccccddddeeee",
                  "Name": "TestPackage",
                  "Publisher": "Test",
                  "Version": "1.0.0.0",
                  "Pages": [{"Id": 50001, "Name": "External Page"}]
                }
                """;
            using var ms = new System.IO.MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("SymbolReference.json");
                using var es = entry.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(symRefJson);
                es.Write(bytes, 0, bytes.Length);
            }
            var zipBytes = ms.ToArray();
            var appBytes = new byte[8 + zipBytes.Length];
            appBytes[0] = (byte)'N'; appBytes[1] = (byte)'A'; appBytes[2] = (byte)'V'; appBytes[3] = (byte)'X';
            System.BitConverter.GetBytes((uint)8).CopyTo(appBytes, 4);
            zipBytes.CopyTo(appBytes, 8);
            File.WriteAllBytes(Path.Combine(pkgDir, "TestPackage_1.0.0.0.app"), appBytes);

            // Pass the package dir so ExtractDeps knows "External Page" is package-provided.
            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir, new List<string> { pkgDir });

            Assert.Equal(0, rc);

            // Negative: no stub for "External Page" since it is declared by the package.
            var stubFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories)
                .Where(f => f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase)
                         && Path.GetFileName(f).StartsWith("ExternalPage", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(stubFiles);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir, pkgDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// When a missing object IS resolvable from the dep index, no stub should be generated.
    /// </summary>
    [Fact]
    public void ExtractDeps_DoesNotGenerateStubs_WhenObjectIsInIndex()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-nostub-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-nostub-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-nostub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "PhantomPage.al"), """
                page 60101 "Missing Phantom Page"
                {
                    PageType = Card;
                    layout { area(content) { } }
                }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "AppACodeunit.al"), """
                codeunit 60100 AppACodeunit
                {
                    procedure DoSomething()
                    var
                        p: Page "Missing Phantom Page";
                    begin
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "Consumer.al"), """
                codeunit 60199 Consumer
                {
                    procedure Run()
                    begin
                        Codeunit.Run(Codeunit::AppACodeunit);
                    end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            // Negative: no __GeneratedStubs__ directory should be created
            var stubFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories)
                .Where(f => f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(stubFiles);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// When an extracted file contains a <c>using</c> directive for a namespace that
    /// no extracted file declares, ExtractDeps must auto-generate a blank-shell
    /// namespace stub so the BC compiler can resolve the namespace reference.
    /// </summary>
    [Fact]
    public void ExtractDeps_GeneratesNamespaceStub_WhenUsingDirectiveHasNoDeclaringFile()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-nsgen-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-nsgen-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-nsgen-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Consumer.al"), """
                namespace AppA.Consumer;
                using Some.Empty.Namespace;
                codeunit 60100 Consumer { procedure Run() begin end; }
                """);
            File.WriteAllText(Path.Combine(extDir, "ExtConsumer.al"), """
                codeunit 60199 ExtConsumer
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Consumer); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            var allFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);

            // Positive: a namespace stub file is generated in the _namespaces sub-dir
            var nsStub = allFiles.FirstOrDefault(f =>
                f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase) &&
                f.Contains("_namespaces", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).StartsWith("SomeEmptyNamespace", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nsStub);

            var content = File.ReadAllText(nsStub!);
            Assert.StartsWith("namespace Some.Empty.Namespace;", content.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("interface \"__StubNamespaceAnchor_", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// When every <c>using</c>-referenced namespace is already declared somewhere in the
    /// extracted slice, no namespace stub file should be generated.
    /// </summary>
    [Fact]
    public void ExtractDeps_DoesNotGenerateNamespaceStub_WhenNamespaceIsDeclared()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-nsdecl-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-out-nsdecl-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-ext-nsdecl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);
            // Consumer.al uses the namespace...
            File.WriteAllText(Path.Combine(depRoot, "AppA", "Consumer.al"), """
                namespace AppA.Consumer;
                using Some.Empty.Namespace;
                codeunit 60100 Consumer { procedure Run() begin end; }
                """);
            // ...and SomeNs.al declares it.
            File.WriteAllText(Path.Combine(depRoot, "AppA", "SomeNs.al"), """
                namespace Some.Empty.Namespace;
                codeunit 60101 NsAnchor { }
                """);
            File.WriteAllText(Path.Combine(extDir, "ExtConsumer.al"), """
                codeunit 60199 ExtConsumer
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Consumer); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            // Negative: no namespace stub for Some.Empty.Namespace since it is declared.
            var allFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            var nsStubFiles = allFiles
                .Where(f => f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase) &&
                            f.Contains("_namespaces", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFileName(f).StartsWith("SomeEmptyNamespace", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(nsStubFiles);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // -----------------------------------------------------------------------
    // Fixup loop — page part references pull in namespace-prefixed dep pages
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dep page with PageType = HeadlinePart and a namespace declaration is
    /// not directly referenced by the extension, but IS referenced as a page part
    /// in a dep page that IS pulled into the slice.  The fixup loop (TrialCompileMissing
    /// + ExpandSlice) must detect the AL0185 and include the headline page.
    /// </summary>
    [Fact]
    public void ExtractDeps_FixupLoop_PullsInPagePartReferencedByDepPage()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-headline-dep-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-headline-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-headline-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            // A dep table that the extension references directly.
            File.WriteAllText(Path.Combine(depDir, "SalesHeader.al"), """
                table 36 "Sales Header"
                {
                    fields { field(1; "No."; Code[20]) { } }
                }
                """);

            // A dep role-center page that references the headline page as a part.
            // This is what pulls in the headline page transitively.
            File.WriteAllText(Path.Combine(depDir, "OrderProcessorRC.al"), """
                namespace Microsoft.Sales.RoleCenters;
                page 9006 "Order Processor Role Center"
                {
                    PageType = RoleCenter;
                    layout
                    {
                        area(rolecenter)
                        {
                            part(Control1; "Headline RC Sales Order Processor") { }
                        }
                    }
                }
                """);

            // The headline page itself — namespace-prefixed, not directly referenced.
            File.WriteAllText(Path.Combine(depDir, "HeadlineRC.al"), """
                namespace System.Visualization;
                page 1441 "Headline RC Sales Order Processor"
                {
                    Caption = 'Headline';
                    PageType = HeadlinePart;
                    RefreshOnActivate = true;
                }
                """);

            // Extension references the Sales Header table (pulling in OrderProcessorRC
            // transitively via the fixup loop is what we want to test).
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                pageextension 50100 "My Order RC Ext" extends "Order Processor Role Center"
                {
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.True(
                files.Any(f => File.ReadAllText(f).Contains("HeadlinePart")),
                "HeadlineRC page (PageType=HeadlinePart) must be in the dep slice");
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // -----------------------------------------------------------------------
    // BFS — page part target names must be pulled in by ExtractObjectReferences
    // (so AL0185 "Page 'X' is missing" never fires for page-part references).
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dep page in the slice has <c>part(Control1; "PartTarget") { ... }</c> in its layout.
    /// The BFS must record "PartTarget" as a referenced page so the part target file
    /// is pulled into the slice.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_PullsInPagePartTarget()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-bfs-pp-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-bfs-pp-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-bfs-pp-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depDir, "MainPage.al"), """
                page 50500 "MainPage"
                {
                    PageType = Card;
                    layout
                    {
                        area(content)
                        {
                            part(Control1; "PartTarget") { }
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "PartTarget.al"), """
                page 50501 "PartTarget"
                {
                    PageType = ListPart;
                }
                """);

            // Extension references MainPage; PartTarget is reached only via the page part.
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                pageextension 50100 "MainPage Ext" extends "MainPage" { }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("page ") && c.Contains("\"PartTarget\"");
            });
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Negative direction: when no path in the slice reaches the page that hosts the
    /// page part, the part target must NOT be pulled in.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_DoesNotPullPagePartTarget_WhenMainPageNotReached()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-bfs-ppn-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-bfs-ppn-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-bfs-ppn-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depDir, "MainPage.al"), """
                page 50500 "MainPage"
                {
                    PageType = Card;
                    layout
                    {
                        area(content)
                        {
                            part(Control1; "PartTarget") { }
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "PartTarget.al"), """
                page 50501 "PartTarget"
                {
                    PageType = ListPart;
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "OtherTable.al"),
                "table 50500 \"Other Table\" { fields { field(1; \"No.\"; Code[20]) { } } }");

            // Extension references "Other Table" only — neither MainPage nor PartTarget
            // is reachable through the BFS frontier.
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50010 "My Codeunit"
                {
                    procedure Run() var R: Record "Other Table"; begin R.FindFirst(); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("page ") && c.Contains("\"PartTarget\"");
            });
            Assert.DoesNotContain(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("page ") && c.Contains("\"MainPage\"");
            });
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Same as <see cref="ExtractDeps_BFS_PullsInPagePartTarget"/> but the page-part
    /// reference is inside a <c>pageextension</c> body rather than a <c>page</c>.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_PullsInPageExtensionPartTarget()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-bfs-pep-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-bfs-pep-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-bfs-pep-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depDir, "BasePage.al"), """
                page 50600 "BasePage"
                {
                    PageType = Card;
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "BasePageExt.al"), """
                pageextension 50601 "BasePage Ext" extends "BasePage"
                {
                    layout
                    {
                        addlast(content)
                        {
                            part(Control1; "PartTarget") { }
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "PartTarget.al"), """
                page 50602 "PartTarget"
                {
                    PageType = ListPart;
                }
                """);

            // Extension references BasePage — pulls in BasePageExt (same app/dir),
            // which references PartTarget through its page-part layout.
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                pageextension 50199 "BasePage Consumer" extends "BasePage" { }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("page ") && c.Contains("\"PartTarget\"");
            });
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Unit-level RED→GREEN proof for the BFS extractor: page-part target names
    /// in a page layout must be recorded as Page references by
    /// <see cref="DepExtractor.CollectExternalReferences"/>.
    /// </summary>
    [Fact]
    public void CollectReferences_FindsPagePartTargetInPage()
    {
        const string source = """
            page 50500 "MainPage"
            {
                PageType = Card;
                layout
                {
                    area(content)
                    {
                        part(Control1; "PartTarget") { }
                    }
                }
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("PartTarget", refs.Pages, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same as <see cref="CollectReferences_FindsPagePartTargetInPage"/> but inside
    /// a <c>pageextension</c> body.
    /// </summary>
    [Fact]
    public void CollectReferences_FindsPagePartTargetInPageExtension()
    {
        const string source = """
            pageextension 50601 "BasePage Ext" extends "BasePage"
            {
                layout
                {
                    addlast(content)
                    {
                        part(Control1; "PartTarget") { }
                    }
                }
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { source });

        Assert.Contains("PartTarget", refs.Pages, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression: when a dep file is pulled into the slice solely because it declares
    /// a namespace that an extension's <c>using</c> directive needs (the "namespace
    /// anchor" path in <see cref="DepExtractor.RunNamespaceScan"/>), its own object
    /// references must still be followed so the slice is internally consistent.
    ///
    /// Real-world failure: DWF Base Application — JobProjectManagerRC was pulled in as
    /// the anchor for namespace <c>Microsoft.Projects.RoleCenters</c>, but its
    /// <c>part(Control102; "Headline RC Project Manager")</c> target was never enqueued,
    /// so compile-dep failed silently with "AL transpilation: no C# code was generated".
    /// </summary>
    [Fact]
    public void ExtractDeps_NamespaceAnchorFile_TransitiveDepsAreFollowed()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-nsanchor-dep-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-nsanchor-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-nsanchor-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            // Dep codeunit the extension references directly (gives BFS a seed).
            // Its `using AppA.RoleCenters;` directive triggers the namespace-anchor
            // pull of AnchorRC.al — which is the path under test.
            File.WriteAllText(Path.Combine(depDir, "Consumer.al"), """
                namespace AppA.Consumer;
                using AppA.RoleCenters;
                codeunit 60100 Consumer { procedure Run() begin end; }
                """);

            // The namespace anchor: declares namespace AppA.RoleCenters and
            // contains a page-part reference to "Anchor Headline" — that page
            // must follow the anchor into the slice.
            File.WriteAllText(Path.Combine(depDir, "AnchorRC.al"), """
                namespace AppA.RoleCenters;
                page 60110 "Anchor RC"
                {
                    PageType = RoleCenter;
                    layout
                    {
                        area(rolecenter)
                        {
                            part(Control1; "Anchor Headline") { }
                        }
                    }
                }
                """);

            // The headline page that the anchor's part references — must end up in slice.
            File.WriteAllText(Path.Combine(depDir, "AnchorHeadline.al"), """
                namespace AppA.Visuals;
                page 60111 "Anchor Headline"
                {
                    PageType = HeadlinePart;
                    Caption = 'Headline';
                }
                """);

            // Extension references Consumer (pulled in by BFS).
            File.WriteAllText(Path.Combine(extDir, "ExtConsumer.al"), """
                codeunit 60199 ExtConsumer
                {
                    procedure Run() begin Codeunit.Run(Codeunit::Consumer); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);

            var allFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);

            // Sanity: the namespace anchor itself is in the slice.
            Assert.Contains(allFiles, f => Path.GetFileName(f).Equals("AnchorRC.al", StringComparison.OrdinalIgnoreCase));

            // The bug: the part target ("Anchor Headline") referenced by the anchor file
            // was not pulled in. With the fix the file IS in the slice.
            Assert.Contains(allFiles, f => Path.GetFileName(f).Equals("AnchorHeadline.al", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // RunObject's object-type keyword is case-insensitive in AL ("page", "Page",
    // "PAGE" are all valid). The dep extractor previously used a case-sensitive
    // string switch and silently dropped references written in lowercase — which
    // BC Base Application uses extensively (e.g. JobProjectManagerRC.Page.al has
    // `RunObject = page "FS Bookable Resource List"`). When such a file is
    // pulled into the slice via the namespace-anchor path, no further trial-
    // compile round runs, so the missed references never get a second chance.
    //
    // This test asserts directly on CollectExternalReferences so it pins the
    // unit-level contract regardless of which BFS path pulled the source in.
    [Fact]
    public void CollectExternalReferences_RunObjectLowercaseKeyword_IsCollected()
    {
        var src = """
            page 60810 Caller
            {
                PageType = Card;
                actions
                {
                    area(processing)
                    {
                        action(GoLowerPage)    { RunObject = page "Lower Page Target"; }
                        action(GoLowerReport)  { RunObject = report "Lower Report Target"; }
                        action(GoLowerCu)      { RunObject = codeunit "Lower Cu Target"; }
                        action(GoLowerQuery)   { RunObject = query "Lower Query Target"; }
                        action(GoLowerXmlport) { RunObject = xmlport "Lower XmlPort Target"; }
                    }
                }
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { src });

        Assert.Contains("Lower Page Target",    refs.Pages);
        Assert.Contains("Lower Report Target",  refs.Reports);
        Assert.Contains("Lower Cu Target",      refs.Codeunits);
        Assert.Contains("Lower Query Target",   refs.Queries);
        Assert.Contains("Lower XmlPort Target", refs.XmlPorts);
    }

    // Variable-declaration object-type keywords are also case-insensitive in
    // AL: `var P: page "Foo"` and `var P: Page "Foo"` are both legal. The
    // dep extractor's SubtypedDataTypeSyntax switch was previously case-
    // sensitive too — guard the lowercase form.
    [Fact]
    public void CollectExternalReferences_VarDeclLowercaseKeyword_IsCollected()
    {
        var src = """
            codeunit 60811 Caller
            {
                procedure Run()
                var
                    P: page "Var Page Target";
                    R: report "Var Report Target";
                    C: codeunit "Var Cu Target";
                    Q: query "Var Query Target";
                    X: xmlport "Var XmlPort Target";
                begin
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { src });

        Assert.Contains("Var Page Target",    refs.Pages);
        Assert.Contains("Var Report Target",  refs.Reports);
        Assert.Contains("Var Cu Target",      refs.Codeunits);
        Assert.Contains("Var Query Target",   refs.Queries);
        Assert.Contains("Var XmlPort Target", refs.XmlPorts);
    }

    // AL idiom for getting a runtime table id: DATABASE::"<Name>". The base BC
    // compiler only flags this as AL0118 in the EMIT phase (not the
    // declaration-resolution phase that the runner's fixup loop scans), so any
    // missing target slips through extract-deps silently and surfaces only
    // when compile-dep tries to emit. Treat DATABASE::X exactly like
    // Page::X / Codeunit::X — collect the right-hand side as a Table reference.
    [Fact]
    public void CollectExternalReferences_DatabaseColonColonReference_IsCollectedAsTable()
    {
        var src = """
            codeunit 60900 "Database ColonColon Caller"
            {
                procedure Run()
                var
                    Eval: Record "Data Classification Eval. Data";
                begin
                    Eval.SetTableFieldsToNormal(DATABASE::"My Quoted Table");
                    Eval.SetTableFieldsToNormal(DATABASE::MyUnquotedTable);
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { src });

        Assert.Contains("My Quoted Table",  refs.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("MyUnquotedTable",  refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression: a namespace-qualified <c>Database::Microsoft.X.Y."Some Table"</c>
    /// reference must collect the *trailing* identifier as the table name, not the
    /// leftmost namespace segment. Otherwise the BFS auto-stubs a phantom table
    /// named "Microsoft" (etc.), which collides with the <c>Microsoft.*</c>
    /// namespace at compile-dep time and produces AL0275 "ambiguous reference"
    /// errors against the merged namespace.
    /// </summary>
    [Fact]
    public void CollectExternalReferences_NamespaceQualifiedDatabaseColonColon_UsesTrailingIdentifier()
    {
        var src = """
            namespace Microsoft.Assembly.Costing;
            using Microsoft.Manufacturing.StandardCost;
            codeunit 60901 "NS Qualified Caller"
            {
                [EventSubscriber(ObjectType::Table, Database::Microsoft.Manufacturing.StandardCost."Standard Cost Worksheet", 'OnAfterGetItemCosts', '', false, false)]
                local procedure Sub(var StandardCostWorksheet: Record Microsoft.Manufacturing.StandardCost."Standard Cost Worksheet")
                begin
                end;
            }
            """;

        var refs = DepExtractor.CollectExternalReferences(new[] { src });

        Assert.Contains("Standard Cost Worksheet", refs.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft", refs.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manufacturing", refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Integration-level proof: a dep codeunit body uses
    /// <c>DATABASE::"PartTarget"</c>, the part-target table is a separate dep
    /// file, and the extension only references the codeunit. The BFS must pull
    /// the table file into the slice.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_PullsInDatabaseColonColonTarget()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-bfs-db-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-bfs-db-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-bfs-db-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depDir, "Caller.al"), """
                codeunit 50500 "Caller"
                {
                    procedure Run(): Integer
                    begin
                        exit(DATABASE::"PartTarget");
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "PartTarget.al"), """
                table 50501 "PartTarget"
                {
                    fields { field(1; PK; Code[20]) { } }
                    keys   { key(PK; PK) { Clustered = true; } }
                }
                """);

            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50100 "Caller Wrapper"
                {
                    procedure Wrap()
                    var
                        C: Codeunit "Caller";
                    begin
                        C.Run();
                    end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.Contains(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("table ") && c.Contains("\"PartTarget\"");
            });
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Negative direction for <see cref="ExtractDeps_BFS_PullsInDatabaseColonColonTarget"/>:
    /// when the extension does NOT reference the codeunit holding the
    /// <c>DATABASE::"PartTarget"</c>, the part-target table must NOT be
    /// pulled into the slice.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_DoesNotPullDatabaseColonColonTarget_WhenCallerNotReached()
    {
        var depDir = Path.Combine(Path.GetTempPath(), "al-bfs-dbn-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-bfs-dbn-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-bfs-dbn-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depDir, "Caller.al"), """
                codeunit 50500 "Caller"
                {
                    procedure Run(): Integer
                    begin
                        exit(DATABASE::"PartTarget");
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(depDir, "PartTarget.al"), """
                table 50501 "PartTarget"
                {
                    fields { field(1; PK; Code[20]) { } }
                    keys   { key(PK; PK) { Clustered = true; } }
                }
                """);

            // Extension references nothing in the dep dir.
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50100 "Standalone" { procedure Noop() begin end; }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depDir }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f =>
            {
                var c = File.ReadAllText(f);
                return c.Contains("table ") && c.Contains("\"PartTarget\"");
            });
        }
        finally
        {
            foreach (var d in new[] { depDir, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    // -----------------------------------------------------------------------
    // BFS — unresolved DATABASE::"X" targets fall through to stub generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// When the BFS encounters a pending name (e.g. seeded from a DATABASE::"X" reference)
    /// that has no definition in the dep-source index, ExpandSlice must NOT silently drop
    /// it. It surfaces downstream as AL0118 "name does not exist" at compile-dep time
    /// (TrialCompileMissing only matches AL0185 wording, so the fixup loop misses it).
    /// Fix: route unresolved BFS targets into the same blank-shell stub generator that
    /// the fixup loop's missing list feeds.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_GeneratesStubForUnresolvedDatabaseColonColonTarget()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-bfs-unres-dep-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-bfs-unres-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-bfs-unres-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);

            // AppA defines an anchor codeunit so the slice is non-empty
            // (FindFirstConsumerApp needs at least one extracted file to attribute the stub to).
            File.WriteAllText(Path.Combine(depRoot, "AppA", "AppACodeunit.al"), """
                codeunit 60100 AppACodeunit { }
                """);

            // Consumer references AppACodeunit (so AppA is pulled into the slice) and
            // DATABASE::"NonexistentTable" — the latter has no source anywhere.
            File.WriteAllText(Path.Combine(extDir, "Consumer.al"), """
                codeunit 60199 Consumer
                {
                    procedure Run()
                    var
                        TableId: Integer;
                    begin
                        Codeunit.Run(Codeunit::AppACodeunit);
                        TableId := DATABASE::"NonexistentTable";
                    end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            var allFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            var tableStub = allFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("NonexistentTable", StringComparison.OrdinalIgnoreCase)
                && f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(tableStub);
            var content = File.ReadAllText(tableStub!);
            Assert.StartsWith("table ", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"NonexistentTable\"", content);
            var idMatch = System.Text.RegularExpressions.Regex.Match(content, @"table\s+(\d+)");
            Assert.True(idMatch.Success);
            Assert.True(int.Parse(idMatch.Groups[1].Value) >= 1_999_900_000);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    /// <summary>
    /// Negative variant: when DATABASE::"X" target IS resolvable from the dep-source index,
    /// no stub must be generated — the real source is used.
    /// </summary>
    [Fact]
    public void ExtractDeps_BFS_DoesNotStubResolvedDatabaseColonColonTarget()
    {
        var depRoot = Path.Combine(Path.GetTempPath(), "al-bfs-res-dep-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir  = Path.Combine(Path.GetTempPath(), "al-bfs-res-out-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir  = Path.Combine(Path.GetTempPath(), "al-bfs-res-ext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(depRoot, "AppA"));
            Directory.CreateDirectory(extDir);

            File.WriteAllText(Path.Combine(depRoot, "AppA", "AppACodeunit.al"), """
                codeunit 60100 AppACodeunit { }
                """);
            File.WriteAllText(Path.Combine(depRoot, "AppA", "NonexistentTable.al"), """
                table 60101 "NonexistentTable"
                {
                    fields { field(1; "No."; Code[20]) { } }
                    keys { key(PK; "No.") { } }
                }
                """);

            File.WriteAllText(Path.Combine(extDir, "Consumer.al"), """
                codeunit 60199 Consumer
                {
                    procedure Run()
                    var
                        TableId: Integer;
                    begin
                        Codeunit.Run(Codeunit::AppACodeunit);
                        TableId := DATABASE::"NonexistentTable";
                    end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);

            // Negative: no stub for "NonexistentTable" (the real source is used).
            var stubFiles = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories)
                .Where(f => f.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase)
                         && Path.GetFileName(f).StartsWith("NonexistentTable", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(stubFiles);

            // Positive: the real definition was extracted.
            var realFiles = Directory.GetFiles(outDir, "NonexistentTable.al", SearchOption.AllDirectories);
            Assert.NotEmpty(realFiles);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

}
