using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class DepCompilerTests
{
    [Fact]
    public void CompileDepMultiApp_AppJsonPlaceholderVersion_UsesFallbackVersion()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-depcompiler-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "AppA");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "AppA",
              "publisher": "Microsoft",
              "version": "$(app_currentVersion)"
            }
            """);

            File.WriteAllText(Path.Combine(appDir, "Trivial.al"),
                "codeunit 99500 Trivial { procedure P() begin end; }");

            var result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());

            Assert.Equal(0, result);

            var expectedDll = Path.Combine(outputDir, "Microsoft_AppA_1.0.0.0.dll");
            Assert.True(File.Exists(expectedDll), $"Expected compiled DLL {expectedDll} to exist");

            var sidecarPath = Path.ChangeExtension(expectedDll, ".app.json");
            Assert.True(File.Exists(sidecarPath), $"Expected sidecar {sidecarPath} to exist");

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            Assert.Equal("1.0.0.0", doc.RootElement.GetProperty("version").GetString());
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void SanitizeManifestVersions_ReplacesPlaceholderVersionsWithDefaults()
    {
        var input = """
        {
          "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
          "name": "Base Application",
          "publisher": "Microsoft",
          "version": "$(app_currentVersion)",
          "platform": "$(app_platformVersion)",
          "application": "$(app_applicationVersion)",
          "dependencies": [
            { "id": "f3552374-a1f2-4356-848e-196002525837",
              "name": "Business Foundation",
              "publisher": "Microsoft",
              "version": "$(app_minimumVersion)" }
          ],
          "idRanges": [{ "from": 1, "to": 50000 }]
        }
        """;

        var sanitized = DepCompiler.SanitizeManifestVersions(input);

        using var doc = JsonDocument.Parse(sanitized);
        var root = doc.RootElement;
        // Identity preserved.
        Assert.Equal("Base Application", root.GetProperty("name").GetString());
        Assert.Equal("Microsoft", root.GetProperty("publisher").GetString());
        Assert.Equal("437dbf0e-84ff-417a-965d-ed2bb9650972", root.GetProperty("id").GetString());

        // Top-level version placeholder replaced with parseable default.
        Assert.True(Version.TryParse(root.GetProperty("version").GetString(), out _));

        // platform / application / dependencies all dropped when placeholders are present:
        // feeding the BC reference resolver synthetic versions (e.g. 1.0.0.0 platform when
        // the real binary is 27.0.46893.0) creates a phantom "(Unknown)" module that
        // collides with the slice's own namespace declarations and fires AL0275.
        // Dropping them lets the runner's "load all .app packages as symbols" fallback
        // discover concrete versions from the package directory.
        Assert.False(root.TryGetProperty("platform", out _),
            "platform should be dropped when placeholder is present");
        Assert.False(root.TryGetProperty("application", out _),
            "application should be dropped when placeholder is present");
        Assert.False(root.TryGetProperty("dependencies", out _),
            "dependencies should be dropped when any entry has unparseable version");

        // Other top-level non-version content preserved.
        Assert.Equal(1, root.GetProperty("idRanges")[0].GetProperty("from").GetInt32());
    }

    [Fact]
    public void SanitizeManifestVersions_KeepsDependenciesWhenAllVersionsParseable()
    {
        var input = """
        {
          "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
          "name": "App",
          "publisher": "Microsoft",
          "version": "1.2.3.4",
          "platform": "27.0.0.0",
          "application": "27.0.0.0",
          "dependencies": [
            { "id": "f3552374-a1f2-4356-848e-196002525837",
              "name": "Business Foundation",
              "publisher": "Microsoft",
              "version": "27.5.46862.49619" }
          ]
        }
        """;

        var sanitized = DepCompiler.SanitizeManifestVersions(input);

        using var doc = JsonDocument.Parse(sanitized);
        var root = doc.RootElement;
        Assert.Equal("1.2.3.4", root.GetProperty("version").GetString());
        Assert.Equal("27.0.0.0", root.GetProperty("platform").GetString());
        Assert.Equal("27.0.0.0", root.GetProperty("application").GetString());
        Assert.True(root.TryGetProperty("dependencies", out var deps));
        Assert.Equal("27.5.46862.49619", deps[0].GetProperty("version").GetString());
    }

    /// <summary>
    /// Regression for the AL0275 'Microsoft' ambiguity reported when compiling DWF's Base
    /// Application (issue #1521 final stretch). Microsoft's BC source ships app.json with
    /// <c>"version": "$(app_currentVersion)"</c> — a build-time placeholder that is not a
    /// valid <see cref="Version"/>. ExtractAppIdentity used <c>Version.Parse</c> which threw,
    /// the catch swallowed the entire identity, and the runner fell back to its hard-coded
    /// "AlRunnerApp by AlRunner (1.0.0.0)" defaults. The BC compiler then attributed every
    /// <c>namespace Microsoft.X;</c> declaration in Base App AL files to the AlRunner
    /// extension and reported it as ambiguous against the real Microsoft platform.
    ///
    /// Fix: parse with <see cref="Version.TryParse"/> and fall back to the default
    /// <see cref="Version"/> only — preserve the real Name/Publisher/Id from the manifest.
    /// </summary>
    [Fact]
    public void ExtractAppIdentity_PlaceholderVersion_PreservesNameAndPublisher()
    {
        var manifestDir = Path.Combine(Path.GetTempPath(), "al-runner-identity-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "app.json"), """
            {
              "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
              "name": "Base Application",
              "publisher": "Microsoft",
              "version": "$(app_currentVersion)"
            }
            """);

            var identity = AlTranspiler.ExtractAppIdentity(new List<string> { manifestDir });

            // The bug: identity collapses to "AlRunnerApp" by "AlRunner". The fix must
            // preserve the manifest's Name/Publisher/Id even when version is unparseable.
            Assert.Equal("Base Application", identity.Name);
            Assert.Equal("Microsoft", identity.Publisher);
            Assert.Equal(Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"), identity.AppId);
        }
        finally
        {
            if (Directory.Exists(manifestDir))
                Directory.Delete(manifestDir, true);
        }
    }

    /// <summary>
    /// Regression: when a "provider" app's app.json declares <c>internalsVisibleTo</c>
    /// granting access to a "consumer" app, multi-app compile must propagate that grant
    /// to the provider's compiled module. Otherwise the consumer hits AL0161 when it
    /// references an internal member of the provider — see issue #1521 (BFTL accessing
    /// BF internal field "Temp Current Sequence No.").
    /// </summary>
    [Fact]
    public void CompileDep_RespectsInternalsVisibleTo_WhenGrantorListsConsumer()
    {
        var providerId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var consumerId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-ivt-" + Guid.NewGuid().ToString("N")[..8]);
        var providerDir = Path.Combine(rootDir, "Provider");
        var consumerDir = Path.Combine(rootDir, "Consumer");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(providerDir);
            Directory.CreateDirectory(consumerDir);
            Directory.CreateDirectory(outputDir);

            // Provider grants Consumer access to its internal members.
            File.WriteAllText(Path.Combine(providerDir, "app.json"), $$"""
            {
              "id": "{{providerId}}",
              "name": "Provider",
              "publisher": "Test",
              "version": "1.0.0.0",
              "internalsVisibleTo": [
                { "id": "{{consumerId}}", "name": "Consumer", "publisher": "Test" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(providerDir, "Provider.al"), """
            table 50100 "My Tab"
            {
                fields
                {
                    field(1; "X"; Integer) { Access = Internal; }
                }
                keys { key(PK; "X") { Clustered = true; } }
            }
            """);

            // Consumer depends on Provider and pokes the internal field.
            File.WriteAllText(Path.Combine(consumerDir, "app.json"), $$"""
            {
              "id": "{{consumerId}}",
              "name": "Consumer",
              "publisher": "Test",
              "version": "1.0.0.0",
              "dependencies": [
                { "id": "{{providerId}}", "name": "Provider", "publisher": "Test", "version": "1.0.0.0" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(consumerDir, "Use.al"), """
            codeunit 50101 "Use"
            {
                procedure P()
                var
                    T: Record "My Tab";
                begin
                    T.X := 1;
                end;
            }
            """);

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int result;
            try
            {
                result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            }
            finally
            {
                Console.SetError(origErr);
            }

            var stderr = errCapture.ToString();
            var providerDll = Path.Combine(outputDir, "Test_Provider_1.0.0.0.dll");
            var consumerDll = Path.Combine(outputDir, "Test_Consumer_1.0.0.0.dll");

            Assert.False(stderr.Contains("AL0161"),
                $"AL0161 (inaccessible due to protection level) should not appear when grantor lists consumer in internalsVisibleTo. stderr:\n{stderr}");
            Assert.Equal(0, result);
            Assert.True(File.Exists(providerDll), $"Expected provider DLL {providerDll}");
            Assert.True(File.Exists(consumerDll), $"Expected consumer DLL {consumerDll}");
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// Regression for the silent-transpile-failure mode (DWF/Base Application 2026-04):
    /// when TranspileMulti aborts before producing C# (parse errors, unresolved
    /// declarations, etc.), CompileDepMultiApp must surface a uniquely-tagged
    /// [TranspileMulti-NoOutput-X] line so downstream debugging can identify which
    /// path fired without re-running under a debugger.
    /// </summary>
    [Fact]
    public void CompileDepMultiApp_OnSilentFailure_PrintsRootCauseTag()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-silentfail-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "Broken");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "ccccdddd-cccc-cccc-cccc-cccccccccccc",
              "name": "Broken",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);

            // Deliberately malformed AL — unterminated procedure body. Parses with errors,
            // forcing TranspileMulti down the [TranspileMulti-NoOutput-A] (parse-error) path.
            File.WriteAllText(Path.Combine(appDir, "Broken.al"),
                "codeunit 99700 Broken { procedure P() begin this is not valid AL syntax @@@");

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int result;
            try
            {
                result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            }
            finally
            {
                Console.SetError(origErr);
            }

            var stderr = errCapture.ToString();

            Assert.NotEqual(0, result);
            Assert.Contains("[TranspileMulti-NoOutput-", stderr);
            // CompileDepMultiApp must also dump its own pre-call counters so the call
            // site is identifiable even if the inner tag is missed.
            Assert.Contains("[CompileDepMultiApp-NoOutput]", stderr);
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// Regression for issue #1521 final stretch: a CLEANSCHEMA<N> guard in the slice
    /// strips a field that a consumer in the same slice references unguarded.
    /// Defining CLEANSCHEMA<N> blindly produces AL0132 / AL0118; the per-slice
    /// auto-detection must drop <N> from the preprocessor set so the field stays.
    ///
    /// Concrete BC 27.x example: GenJournalLine."IC Partner G/L Acc. No." is wrapped
    /// in <c>#if not CLEANSCHEMA25</c> while UpgradeBaseApp.Codeunit.al references
    /// the field unguarded.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_SkipsN_WhenStrippedFieldHasUnguardedConsumer()
    {
        var tableSrc = """
            table 50100 "Bank Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA25
                    field(116; "IC Partner G/L Acc. No."; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var consumerSrc = """
            codeunit 50101 "Use"
            {
                procedure P()
                var T: Record "Bank Tab";
                begin
                    T.SetFilter("IC Partner G/L Acc. No.", '<>''''');
                end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, consumerSrc });

        Assert.DoesNotContain("CLEANSCHEMA25", symbols);
        // Other CLEANSCHEMA<N> not implicated by this slice should still be defined,
        // matching the historical default behavior.
        Assert.Contains("CLEANSCHEMA1", symbols);
        Assert.Contains("CLEANSCHEMA24", symbols);
    }

    /// <summary>
    /// Companion: when a CLEANSCHEMA<N> guard wraps a field that nothing else
    /// references, defining <N> is safe and should remain in the symbol set.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_KeepsN_WhenStrippedFieldHasNoOtherConsumer()
    {
        var tableSrc = """
            table 50100 "Lonely Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA25
                    field(99; "Unused Obsolete Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var unrelatedSrc = """
            codeunit 50101 "Other"
            {
                procedure P() begin end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, unrelatedSrc });

        Assert.Contains("CLEANSCHEMA25", symbols);
    }

    /// <summary>
    /// Inverse of the negative-guard case: an enum (or other top-level object) is
    /// declared <em>only</em> inside <c>#if CLEANSCHEMA&lt;N&gt; ... #endif</c>, and
    /// a consumer references it unguarded. To keep the declaration visible, the
    /// auto-detector must include <c>CLEANSCHEMA&lt;N&gt;</c> in the symbol set.
    ///
    /// Concrete BC 27.x example: enum "Sales Order Print Option" is declared inside
    /// <c>#if CLEANSCHEMA29</c> but DocumentPrint.Codeunit.al references it unguarded.
    /// Without this, AL0118 fires for every reference site.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_AddsN_WhenPositiveGuardDeclarationHasUnguardedConsumer()
    {
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "My Print Option"
            {
                Extensible = true;
                value(0; "First") { Caption = 'First'; }
                value(1; "Second") { Caption = 'Second'; }
            }
            #endif
            """;
        var consumerSrc = """
            codeunit 50201 "Use Print"
            {
                procedure P()
                var Opt: Enum "My Print Option";
                begin
                    Opt := "My Print Option"::First;
                end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { enumSrc, consumerSrc }, defaultMax: 25, scanMax: 50);

        Assert.Contains("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Conflict tradeoff: the SAME N appears as a negative guard hiding a field
    /// (drop N → strip field) AND as a positive guard hiding a declaration with
    /// unguarded consumers (add N → reveal declaration). Pick the side with the
    /// majority of unguarded consumers; on a tie, prefer negative (drop N).
    /// Here the negative-side has 1 consumer, positive-side has 3 — positive wins.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_PositiveWins_WhenMorePositiveConsumers()
    {
        var tableSrc = """
            table 50100 "Conf Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA29
                    field(116; "Old Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var oldConsumer = """
            codeunit 50101 "Old Use"
            {
                procedure P()
                var T: Record "Conf Tab";
                begin
                    T.SetFilter("Old Field", '<>''''');
                end;
            }
            """;
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "New Enum" { value(0; "X") { } }
            #endif
            """;
        var newConsumerA = """
            codeunit 50202 "New Use A"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;
        var newConsumerB = """
            codeunit 50203 "New Use B"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;
        var newConsumerC = """
            codeunit 50204 "New Use C"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, oldConsumer, enumSrc, newConsumerA, newConsumerB, newConsumerC },
            defaultMax: 25, scanMax: 50);

        // Positive side has more consumers (3) than negative side (1) → add N.
        Assert.Contains("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Conflict tie-breaker: equal consumer counts on both sides (or negative
    /// strictly more) prefer the negative side (drop N), preserving the
    /// historical default behaviour.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_NegativeWins_OnTieOrMoreNegativeConsumers()
    {
        var tableSrc = """
            table 50100 "Conf Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA29
                    field(116; "Old Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var oldConsumerA = """
            codeunit 50101 "Old Use A"
            { procedure P() var T: Record "Conf Tab"; begin T.SetFilter("Old Field", ''); end; }
            """;
        var oldConsumerB = """
            codeunit 50102 "Old Use B"
            { procedure P() var T: Record "Conf Tab"; begin T.SetFilter("Old Field", ''); end; }
            """;
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "New Enum" { value(0; "X") { } }
            #endif
            """;
        var newConsumer = """
            codeunit 50202 "New Use"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, oldConsumerA, oldConsumerB, enumSrc, newConsumer },
            defaultMax: 25, scanMax: 50);

        // Negative side has more consumers (2) than positive side (1) → drop N.
        Assert.DoesNotContain("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Regression for the auto-stubbed-page-method-call gap: a consumer calls
    /// <c>MyPage.SomeMethod(arg1, arg2)</c> on a page that exists only as an
    /// auto-stub. The stub must include forgiving Variant-arg procedures so
    /// AL0132 doesn't fire.
    ///
    /// Concrete BC 27.x example: PowerBIEmbeddedReportPart.Page.al calls
    /// <c>PowerBIElementAddinHost.SetDisplayedElement(Rec)</c> on the auto-stubbed
    /// "Power BI Element Addin Host" page.
    /// </summary>
    [Fact]
    public void ExtractDeps_AutoStubPage_EmitsProceduresForObservedMethodCalls()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-pagestub-" + Guid.NewGuid().ToString("N")[..8]);
        var srcDir = Path.Combine(rootDir, "src");
        var depDir = Path.Combine(rootDir, "dep", "FakeBaseApp");
        var outputDirAfterExtract = Path.Combine(rootDir, "extracted");
        try
        {
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(depDir);

            // Extension app — references a codeunit defined in the dep, which in
            // turn references the missing page. This mirrors the real DWF flow:
            // user app -> BaseApp -> Power BI Element Addin Host (missing).
            File.WriteAllText(Path.Combine(srcDir, "app.json"), """
            {
              "id": "44444444-4444-4444-4444-444444444444",
              "name": "ConsumerApp",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);
            File.WriteAllText(Path.Combine(srcDir, "MyExt.al"), """
            codeunit 50500 "MyExt"
            {
                procedure Run()
                var
                    DepC: Codeunit "DepCaller";
                begin
                    DepC.Trigger();
                end;
            }
            """);

            // Dep source — contains the consumer that calls into the missing page.
            File.WriteAllText(Path.Combine(depDir, "DepCaller.al"), """
            codeunit 60500 "DepCaller"
            {
                procedure Trigger()
                var
                    Host: Page "Mystery Host Page";
                begin
                    Host.SetDisplayedElement(1);
                    Host.SetPowerBIFilter('foo');
                end;
            }
            """);

            int rc = AlRunner.DepExtractor.ExtractDeps(
                srcDir,
                new[] { Path.Combine(rootDir, "dep") },
                outputDirAfterExtract,
                new List<string>());
            Assert.Equal(0, rc);

            // The extractor must have generated a stub for the missing page that
            // includes the two methods seen at the call site.
            var stubFiles = Directory.EnumerateFiles(outputDirAfterExtract, "*MysteryHostPage*.al", SearchOption.AllDirectories).ToList();
            Assert.NotEmpty(stubFiles);
            var stubContent = File.ReadAllText(stubFiles[0]);
            Assert.Contains("SetDisplayedElement", stubContent);
            Assert.Contains("SetPowerBIFilter", stubContent);
            // Procedures must accept a Variant arg so any call shape compiles.
            Assert.Contains("Variant", stubContent);
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }
}
