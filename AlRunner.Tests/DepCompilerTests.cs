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
}
