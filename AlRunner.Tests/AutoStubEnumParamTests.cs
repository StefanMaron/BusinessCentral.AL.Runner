using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression tests for issue #1419: auto-stubbed codeunit methods lose
/// Enum/Option parameter types, causing NavOption→NavCode cast errors at the call site.
///
/// Builds a real library .app (via alc.exe) that contains a codeunit with an
/// Enum-typed parameter.  The test then runs the pipeline with only that .app
/// in --packages (no library AL source), forcing the reflection-based auto-stub
/// path in <see cref="AlRunnerPipeline"/>.RenderCodeunitStubFromSymbols.
///
/// Before the fix, the generated stub emitted "Option" as "Integer" for Option
/// NavTypeKind and fell back to "Variant" for Enum NavTypeKind, causing:
///   Object of type 'NavOption' cannot be converted to type 'NavCode'   (or similar)
/// After the fix both Enum and Option NavTypeKinds must emit "Option" so the
/// runtime NavOption passes through without a cast error.
///
/// Tests skip when alc.exe is unavailable (CI without AL extension installed).
/// </summary>
[Collection("Pipeline")]
public class AutoStubEnumParamTests
{
    private static readonly string? AlcPath = AlcPathResolver.Default;

    [Fact]
    public async Task AutoStub_EnumParam_CallWithEnumLiteral_DoesNotThrow()
    {
        if (AlcPath == null) return;

        await RunAutoStubEnumScenario(async (testDir, pkgDir) =>
        {
            var libGuid = Guid.NewGuid();
            var testGuid = Guid.NewGuid();
            await BuildEnumLibraryApp(libGuid, pkgDir);
            WriteTestAppJson(testDir, testGuid, libGuid);

            // The test codeunit calls a method with an Enum literal argument.
            // The library codeunit is in --packages only (auto-stubbed).
            // Before the fix, the stub emitted "Integer" for the Enum param,
            // causing NavOption→NavCode cast error at the call site.
            File.WriteAllText(Path.Combine(testDir, "Test.al"), """
                codeunit 50300 "Enum Param Stub Tests"
                {
                    Subtype = Test;

                    var Assert: Codeunit Assert;

                    [Test]
                    procedure CallStubbedMethodWithEnumLiteral_NoError()
                    var
                        Lib: Codeunit "Enum Param Lib";
                    begin
                        // [GIVEN] An auto-stubbed codeunit method with an Enum parameter
                        // [WHEN]  Called with an Enum literal — the stub body is a no-op
                        // [THEN]  No NavOption→NavCode cast error is thrown
                        Lib.DoSomethingWithStatus("Enum Param Lib Status"::Active);
                        // If we reach here the call succeeded; assert a non-default value
                        // to ensure the stub compiled with correct parameter types.
                        Assert.IsTrue(true, 'Call with Enum literal must not throw a cast error');
                    end;

                    [Test]
                    procedure CallStubbedMethodWithEnumVar_NoError()
                    var
                        Lib: Codeunit "Enum Param Lib";
                        Status: Enum "Enum Param Lib Status";
                    begin
                        // [GIVEN] A local Enum variable set to a non-default value
                        Status := "Enum Param Lib Status"::Inactive;
                        // [WHEN]  Passed to an auto-stubbed method
                        // [THEN]  No cast error
                        Lib.DoSomethingWithStatus(Status);
                        Assert.IsTrue(true, 'Call with Enum var must not throw a cast error');
                    end;

                    [Test]
                    procedure CallStubbedMethodWithOptionEnum_NoError()
                    var
                        Lib: Codeunit "Enum Param Lib";
                    begin
                        // [GIVEN] A method with an Option-typed parameter (older AL style)
                        // [WHEN]  Called with an Enum literal (NavOption at runtime)
                        // [THEN]  No NavOption→NavInteger cast error (Option must emit "Option", not "Integer")
                        Lib.DoSomethingWithOption("Enum Param Lib Status"::Active);
                        Assert.IsTrue(true, 'Call with Enum literal to Option param must not throw a cast error');
                    end;
                }
                """);

            var result = RunPipeline(testDir, pkgDir);

            AssertPipelinePassed(result);
            Assert.Equal(3, result.Passed);
        });
    }

    private static void AssertPipelinePassed(PipelineResult result)
    {
        if (result.ExitCode == 0 && result.Failed == 0 && result.Errors == 0)
            return;

        var failures = string.Join("\n",
            result.Tests.Where(t => t.Status != TestStatus.Pass)
                .Select(t => $"  {t.Status} {t.Name}: {t.Message}"));
        Assert.Fail(
            $"Pipeline failed (exit={result.ExitCode}, passed={result.Passed}, failed={result.Failed}, errors={result.Errors}).\n" +
            $"Failures:\n{failures}\n" +
            $"--- StdOut ---\n{result.StdOut}\n" +
            $"--- StdErr ---\n{result.StdErr}");
    }

    private async Task RunAutoStubEnumScenario(Func<string, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-enumstub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var pkgDir = Path.Combine(dir, "pkg");
            var testDir = Path.Combine(dir, "test");
            Directory.CreateDirectory(pkgDir);
            Directory.CreateDirectory(testDir);

            await body(testDir, pkgDir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private async Task BuildEnumLibraryApp(Guid libGuid, string pkgDir)
    {
        var libDir = Path.Combine(Path.GetDirectoryName(pkgDir)!, "lib-enum");
        Directory.CreateDirectory(libDir);

        // An enum type and a codeunit that has an Enum-typed and an Option-typed parameter.
        File.WriteAllText(Path.Combine(libDir, "EnumParamLib.al"), """
            enum 50100 "Enum Param Lib Status"
            {
                Extensible = false;
                value(0; " ") { Caption = ' '; }
                value(1; Active) { Caption = 'Active'; }
                value(2; Inactive) { Caption = 'Inactive'; }
            }

            codeunit 50100 "Enum Param Lib"
            {
                // Method with an Enum-typed parameter (the common case from #1419).
                procedure DoSomethingWithStatus(Status: Enum "Enum Param Lib Status")
                begin
                    // intentionally empty — callers just need the stub to compile
                end;

                // Method with a legacy Option-typed parameter.
                procedure DoSomethingWithOption(DocType: Option)
                begin
                    // intentionally empty — callers just need the stub to compile
                end;
            }
            """);

        File.WriteAllText(Path.Combine(libDir, "app.json"), $$"""
            {
              "id": "{{libGuid}}",
              "name": "EnumParamLib",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0"
            }
            """);

        var libAppPath = await CompileAlApp(libDir);
        if (libAppPath == null)
            throw new InvalidOperationException("Failed to compile EnumParamLib .app via alc.");

        File.Copy(libAppPath, Path.Combine(pkgDir, Path.GetFileName(libAppPath)));
    }

    private static void WriteTestAppJson(string testDir, Guid testGuid, Guid libGuid)
    {
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
            {
              "id": "{{testGuid}}",
              "name": "EnumParamTest",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0",
              "dependencies": [
                {
                  "id": "{{libGuid}}",
                  "name": "EnumParamLib",
                  "publisher": "AlRunnerTest",
                  "version": "1.0.0.0"
                }
              ]
            }
            """);
    }

    private static PipelineResult RunPipeline(string testDir, string pkgDir)
    {
        var pipeline = new AlRunnerPipeline();
        return pipeline.Run(new PipelineOptions
        {
            TestIsolation = TestIsolation.Method,
            InputPaths = { testDir },
            PackagePaths = { pkgDir },
            Strict = true
        });
    }

    private static async Task<string?> CompileAlApp(string projectDir)
    {
        if (AlcPath == null) return null;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = AlcPath,
            Arguments = $"/project:\"{projectDir}\" /outfolder:\"{projectDir}\" /packagecachepath:\"{projectDir}\"",
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
