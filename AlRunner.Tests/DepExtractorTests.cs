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
}
