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
}
