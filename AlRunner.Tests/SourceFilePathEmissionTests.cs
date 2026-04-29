using AlRunner;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Verifies that Pipeline.cs registers absolute, forward-slash paths in
/// SourceFileMapper regardless of the process's current working directory.
///
/// The bug: Pipeline.cs used Path.GetRelativePath(Directory.GetCurrentDirectory(), file).
/// When spawned from VS Code's extension host the cwd is the VS Code install dir, so
/// the emitted path walks up several levels to reach the workspace.  ALchemist's
/// path.resolve(workspacePath, sourceFile) then resolves to the wrong absolute path
/// and silently drops inline captures, gutter coverage, and coverage hover.
///
/// The fix: use Path.GetFullPath(file).Replace('\\', '/') so the wire format is
/// cwd-independent.
/// </summary>
[Collection("Pipeline")]
public class SourceFilePathEmissionTests
{
    public SourceFilePathEmissionTests()
    {
        SourceFileMapper.Clear();
        AlScope.ResetCoverage();
    }

    // -----------------------------------------------------------------------
    // Fact 1: SourceFileMapper receives an absolute path regardless of cwd
    // -----------------------------------------------------------------------
    [Fact]
    public void SourceFileMapper_RegistersAbsolutePath_RegardlessOfCwd()
    {
        // Arrange: create a temp dir with a minimal AL fixture
        var projectDir = Directory.CreateTempSubdirectory("alrunner-abs-path-test-");
        var foreignCwd = Directory.CreateTempSubdirectory("alrunner-foreign-cwd-");
        try
        {
            var alFile = Path.Combine(projectDir.FullName, "CU1.al");
            File.WriteAllText(alFile, """
                codeunit 50100 CU1
                {
                    procedure DoIt(): Integer
                    begin
                        exit(1);
                    end;
                }
                """);

            var expectedAbsPath = Path.GetFullPath(alFile).Replace('\\', '/');

            // Simulate VS Code's extension host cwd by switching to a completely
            // unrelated temp directory before running the pipeline.
            var originalCwd = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(foreignCwd.FullName);

                SourceFileMapper.Clear();
                var pipeline = new AlRunnerPipeline();
                pipeline.Run(new PipelineOptions
                {
                    InputPaths = { alFile }
                });
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCwd);
            }

            // Assert
            var registered = SourceFileMapper.GetFile("CU1");
            Assert.NotNull(registered);
            Assert.True(Path.IsPathFullyQualified(registered),
                $"Expected fully-qualified path but got: {registered}");
            Assert.Equal(expectedAbsPath, registered);
        }
        finally
        {
            projectDir.Delete(recursive: true);
            foreignCwd.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Fact 2: CoverageReport.ToJson emits absolute file paths regardless of cwd
    // -----------------------------------------------------------------------
    [Fact]
    public void Coverage_EmitsAbsoluteFilePath_RegardlessOfCwd()
    {
        // Arrange: create a temp dir with source + test AL files so the pipeline
        // takes the RunTests path (required for ShowCoverage to populate SourceSpans).
        var projectDir = Directory.CreateTempSubdirectory("alrunner-abs-coverage-test-");
        var foreignCwd = Directory.CreateTempSubdirectory("alrunner-foreign-cwd-cov-");
        try
        {
            var srcFile = Path.Combine(projectDir.FullName, "CU2Src.al");
            File.WriteAllText(srcFile, """
                codeunit 50101 CU2Src
                {
                    procedure DoIt(): Integer
                    begin
                        exit(1);
                    end;
                }
                """);

            var testFile = Path.Combine(projectDir.FullName, "CU2Test.al");
            File.WriteAllText(testFile, """
                codeunit 50901 CU2Test
                {
                    Subtype = Test;

                    var
                        Src: Codeunit CU2Src;

                    [Test]
                    procedure TestDoIt()
                    begin
                        Src.DoIt();
                    end;
                }
                """);

            AlScope.ResetCoverage();
            SourceFileMapper.Clear();

            PipelineResult result;

            var originalCwd = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(foreignCwd.FullName);

                var pipeline = new AlRunnerPipeline();
                result = pipeline.Run(new PipelineOptions
                {
                    InputPaths = { projectDir.FullName },
                    ShowCoverage = true
                });
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCwd);
            }

            // The pipeline must have succeeded for coverage data to be meaningful
            Assert.Equal(0, result.ExitCode);

            var sourceSpans = result.SourceSpans;
            var scopeToObject = result.ScopeToObject;

            // SourceSpans / ScopeToObject are only populated when ShowCoverage = true
            Assert.NotNull(sourceSpans);
            Assert.NotNull(scopeToObject);

            var (hits, totals) = AlScope.GetCoverageSets();
            var fileCovs = CoverageReport.ToJson(sourceSpans, hits, totals, scopeToObject);

            // At least one file entry must be present
            Assert.NotEmpty(fileCovs);

            // Every file path in the coverage report must be absolute
            foreach (var fc in fileCovs)
            {
                Assert.True(Path.IsPathFullyQualified(fc.File),
                    $"Expected fully-qualified coverage file path but got: {fc.File}");
            }
        }
        finally
        {
            projectDir.Delete(recursive: true);
            foreignCwd.Delete(recursive: true);
        }
    }
}
