using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression tests for the CS0101 "already contains a definition for 'CodeunitN'"
/// collision that fires when:
///   1. The user provides App A as a SOURCE DIRECTORY input (with codeunit N),
///   2. The user also provides App B (test .app) that depends on App A,
///   3. App A's compiled .app is in the --packages directory.
///
/// Under those conditions the old <c>AutoDiscoverDependencies</c> code would
/// re-extract App A's AL from the packages cache and add it to the compilation
/// a second time, producing two Roslyn syntax trees for the same class (CS0101).
///
/// The fix: read app.json from source-directory inputs and treat their AppIds as
/// "already present" so the package-cache copy is never re-added. Additionally,
/// the multi-group compilation path now deduplicates identical class names before
/// feeding them to the Roslyn compiler (#1566).
///
/// Tests skip when alc.exe is unavailable (CI environments without a BC SDK).
/// </summary>
[Collection("Pipeline")]
public class AutoDiscoverDuplicateTests
{
    private static readonly string? AlcPath = AlcPathResolver.Default;

    /// <summary>
    /// Verify that supplying App A as a source directory + App B test.app (depending on A)
    /// + App A compiled in packages does NOT produce CS0101.
    ///
    /// Before the fix this produced:
    ///   CS0101 on 'Microsoft.Dynamics.Nav.BusinessApplication': The namespace
    ///   'Microsoft.Dynamics.Nav.BusinessApplication' already contains a definition
    ///   for 'Codeunit50105'
    /// because AutoDiscoverDependencies re-extracted App A's AL from the packages cache,
    /// adding a second Codeunit50105 syntax tree to the Roslyn compilation.
    /// </summary>
    [Fact]
    public async Task SourceDirPlusPackageCopy_AppBDotApp_NoDuplicateCS0101()
    {
        if (AlcPath == null) return; // skip when alc is not installed

        await RunScenario(async (srcDir, testDir, pkgDir) =>
        {
            var libGuid = Guid.NewGuid();
            var testGuid = Guid.NewGuid();

            // --- App A (library with codeunit 50105) ---
            File.WriteAllText(Path.Combine(srcDir, "app.json"), $$"""
                {
                  "id": "{{libGuid}}",
                  "name": "MZ2Lib",
                  "publisher": "AlRunnerTest",
                  "version": "1.0.0.0",
                  "runtime": "14.0"
                }
                """);
            File.WriteAllText(Path.Combine(srcDir, "MyCodeMgmt.al"), """
                codeunit 50105 "MZ2 MyCode Management"
                {
                    procedure Add(Num1: Integer; Num2: Integer): Integer
                    begin
                        exit(Num1 + Num2);
                    end;
                }
                """);

            // Compile App A to a .app and put it in the packages dir so that
            // AutoDiscoverDependencies can find and extract it.
            var libAppPath = await CompileAlApp(srcDir);
            if (libAppPath == null)
                throw new InvalidOperationException("Failed to compile library .app via alc");
            File.Copy(libAppPath, Path.Combine(pkgDir, Path.GetFileName(libAppPath)));

            // --- App B (test app, depends on App A — compiled to .app) ---
            File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
                {
                  "id": "{{testGuid}}",
                  "name": "MZ2Test",
                  "publisher": "AlRunnerTest",
                  "version": "1.0.0.0",
                  "runtime": "14.0",
                  "dependencies": [
                    {
                      "id": "{{libGuid}}",
                      "name": "MZ2Lib",
                      "publisher": "AlRunnerTest",
                      "version": "1.0.0.0"
                    }
                  ]
                }
                """);
            // Write App B test without using Assert (Assert codeunit is not available
            // to the alc compiler — it is only a runner-internal stub).
            // The test just calls Add() and verifies it does not error.
            File.WriteAllText(Path.Combine(testDir, "Tests.al"), """
                codeunit 50200 "MZ2 Tests"
                {
                    Subtype = Test;

                    [Test]
                    procedure AddDoesNotError()
                    var
                        Mgmt: Codeunit "MZ2 MyCode Management";
                        Result: Integer;
                    begin
                        Result := Mgmt.Add(3, 4);
                    end;
                }
                """);

            // Compile App B to .app (it depends on App A from pkgDir)
            var testAppPath = await CompileAlApp(testDir, pkgDir);
            if (testAppPath == null)
                throw new InvalidOperationException("Failed to compile test .app via alc");

            // Run: App A SOURCE dir + App B as .app, packages = pkgDir
            // (App A compiled .app is also in pkgDir — this is the trigger for the bug)
            // AutoDiscoverDependencies sees App B.app depends on App A,
            // finds App A.app in pkgDir, and (before the fix) re-extracts App A's AL.
            var pipeline = new AlRunnerPipeline();
            var result = pipeline.Run(new PipelineOptions
            {
                TestIsolation = TestIsolation.Method,
                InputPaths = { srcDir, testAppPath },
                PackagePaths = { pkgDir },
                Strict = false,
            });

            // Must not produce CS0101 and must pass the test
            if (result.StdErr.Contains("CS0101"))
                Assert.Fail($"Expected no CS0101 but got:\n{result.StdErr}");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, result.Passed);
            Assert.Equal(0, result.Failed);
        });
    }

    /// <summary>
    /// Positive control: when App A is provided as ONLY a source directory (no compiled
    /// .app in packages), no duplication occurs and the test passes as expected.
    /// </summary>
    [Fact]
    public void SourceDirOnly_TestPasses()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-autodisco-ctrl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var srcDir = Path.Combine(root, "src");
            var testDir = Path.Combine(root, "test");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testDir);

            File.WriteAllText(Path.Combine(srcDir, "MyCodeMgmt.al"), """
                codeunit 50105 "MZ2 MyCode Management"
                {
                    procedure Add(Num1: Integer; Num2: Integer): Integer
                    begin
                        exit(Num1 + Num2);
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(testDir, "Tests.al"), """
                codeunit 50200 "MZ2 Tests"
                {
                    Subtype = Test;

                    [Test]
                    procedure AddReturnsSum()
                    var
                        Assert: Codeunit Assert;
                        Mgmt: Codeunit "MZ2 MyCode Management";
                        Result: Integer;
                    begin
                        Result := Mgmt.Add(3, 4);
                        Assert.AreEqual(7, Result, 'Add should return 3+4=7');
                    end;
                }
                """);

            var pipeline = new AlRunnerPipeline();
            var result = pipeline.Run(new PipelineOptions
            {
                TestIsolation = TestIsolation.Method,
                InputPaths = { srcDir, testDir },
            });

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, result.Passed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // -----------------------------------------------------------------------

    private static async Task RunScenario(Func<string, string, string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-autodisco-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var srcDir = Path.Combine(root, "src");
            var testDir = Path.Combine(root, "test");
            var pkgDir = Path.Combine(root, "pkg");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(pkgDir);

            await body(srcDir, testDir, pkgDir);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<string?> CompileAlApp(string projectDir, string? packageCachePath = null)
    {
        if (AlcPath == null) return null;

        var pkgArg = packageCachePath != null
            ? $" /packagecachepath:\"{packageCachePath}\""
            : $" /packagecachepath:\"{projectDir}\"";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = AlcPath,
            Arguments = $"/project:\"{projectDir}\" /outfolder:\"{projectDir}\"{pkgArg}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return null;
        return Directory.GetFiles(projectDir, "*.app").FirstOrDefault();
    }
}
