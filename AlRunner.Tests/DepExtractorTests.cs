using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for the DepExtractor feature (--extract-deps command).
/// These tests exercise the C# logic directly; the BA .app path is skipped
/// when not available (CI / machines without the Microsoft packages).
/// </summary>
public class DepExtractorTests
{
    // -----------------------------------------------------------------------
    // Paths — skipped gracefully when not present
    // -----------------------------------------------------------------------

    private static readonly string BaAppPath =
        "/home/stefan/Documents/ALPackages/Microsoft_Base Application_24.5.23489.26846.app";

    private static readonly string ExtensionSrcDir =
        "/home/stefan/Documents/Repos/SMC/CustomerBlockedModification/src";

    private static bool HasBaApp => File.Exists(BaAppPath);
    private static bool HasExtensionSrc => Directory.Exists(ExtensionSrcDir);

    // -----------------------------------------------------------------------
    // Unit tests (no .app file required)
    // -----------------------------------------------------------------------

    [Fact]
    public void CollectReferences_FindsTableRefsFromSource()
    {
        // Simple AL source that references two external tables
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

        Assert.Contains("customer", refs.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sales header", refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_FindsEventSubscriberTargetTable()
    {
        // EventSubscriber attribute tells us which table must be in the slice
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

        Assert.Contains("sales header", refs.Tables, StringComparer.OrdinalIgnoreCase);
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

        Assert.Contains("sales-post", refs.Codeunits, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectReferences_EmptySource_ReturnsEmptyRefs()
    {
        var refs = DepExtractor.CollectExternalReferences(new[] { "" });

        Assert.Empty(refs.Tables);
        Assert.Empty(refs.Codeunits);
    }

    [Fact]
    public void FilterByObjectName_MatchesTable()
    {
        const string tableSource = """
            table 18 Customer
            {
                fields { field(1; "No."; Code[20]) { } }
            }
            """;

        bool matches = DepExtractor.SourceMatchesAnyRef(tableSource,
            new ExternalRefs { Tables = { "customer" } });

        Assert.True(matches);
    }

    [Fact]
    public void FilterByObjectName_RejectsUnrelatedObject()
    {
        const string tableSource = """
            table 999 "Unrelated Table"
            {
                fields { field(1; "No."; Code[20]) { } }
            }
            """;

        bool matches = DepExtractor.SourceMatchesAnyRef(tableSource,
            new ExternalRefs { Tables = { "customer" } });

        Assert.False(matches);
    }

    // -----------------------------------------------------------------------
    // Integration tests (require local .app and extension source)
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractDeps_CustomerBlockedMod_FindsCustomerTable()
    {
        if (!HasBaApp || !HasExtensionSrc)
            return; // skip on machines without the files

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(
                extensionSrcDir: ExtensionSrcDir,
                appPaths: new[] { BaAppPath },
                outputDir: outDir);

            Assert.Equal(0, rc);

            // At minimum, Customer table must be written
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            Assert.True(files.Length > 0, "Expected at least one extracted AL file");

            bool hasCustomer = files.Any(f =>
                File.ReadAllText(f).Contains("table", StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(f).Contains("Customer", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasCustomer, "Expected extracted output to contain a Customer table definition");
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ExtractDeps_CustomerBlockedMod_FindsSalesHeaderTable()
    {
        if (!HasBaApp || !HasExtensionSrc)
            return;

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(
                extensionSrcDir: ExtensionSrcDir,
                appPaths: new[] { BaAppPath },
                outputDir: outDir);

            Assert.Equal(0, rc);

            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            bool hasSalesHeader = files.Any(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("Sales Header", StringComparison.OrdinalIgnoreCase);
            });
            Assert.True(hasSalesHeader, "Expected extracted output to contain a Sales Header definition");
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ExtractDeps_CustomerBlockedMod_FindsUserSetupTable()
    {
        if (!HasBaApp || !HasExtensionSrc)
            return;

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(
                extensionSrcDir: ExtensionSrcDir,
                appPaths: new[] { BaAppPath },
                outputDir: outDir);

            Assert.Equal(0, rc);

            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            bool hasUserSetup = files.Any(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("User Setup", StringComparison.OrdinalIgnoreCase);
            });
            Assert.True(hasUserSetup, "Expected extracted output to contain a User Setup definition");
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ExtractDeps_NonExistentSrcDir_ReturnsNonZero()
    {
        int rc = DepExtractor.ExtractDeps(
            extensionSrcDir: "/nonexistent/path",
            appPaths: new[] { BaAppPath },
            outputDir: Path.GetTempPath());

        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void ExtractDeps_NonExistentApp_ReturnsNonZero()
    {
        if (!HasExtensionSrc)
            return;

        var outDir = Path.Combine(Path.GetTempPath(), "al-extract-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int rc = DepExtractor.ExtractDeps(
                extensionSrcDir: ExtensionSrcDir,
                appPaths: new[] { "/nonexistent/app.app" },
                outputDir: outDir);

            Assert.NotEqual(0, rc);
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
        }
    }
}
