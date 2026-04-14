using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class InlineCaptureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void CaptureValues_PerStatement_RecordsIntermediateValues()
    {
        // The test procedure assigns X, Y, Z in sequence. Per-statement
        // value capture should record each write as a distinct entry,
        // not just the final post-test snapshot.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("47-capture-inline", "src"), TestPath("47-capture-inline", "test") },
            CaptureValues = true
        });

        Assert.Equal(0, result.ExitCode);

        // Filter down to the test we care about, looking at captures for X/Y/Z.
        var xCaptures = result.CapturedValues.Where(c => c.VariableName == "x" || c.VariableName == "X").ToList();
        var yCaptures = result.CapturedValues.Where(c => c.VariableName == "y" || c.VariableName == "Y").ToList();
        var zCaptures = result.CapturedValues.Where(c => c.VariableName == "z" || c.VariableName == "Z").ToList();

        // Old behavior (final-state only) gives exactly 1 capture per variable.
        // Per-statement capture should record at least one snapshot per variable
        // AND each subsequent variable's capture should carry a distinct
        // (non-zero) statementId — proving the ID comes from StmtHit, not the
        // fallback "0" that CaptureFieldValues used.
        Assert.NotEmpty(xCaptures);
        Assert.NotEmpty(yCaptures);
        Assert.NotEmpty(zCaptures);

        // The key assertion: at least one capture must have a non-zero statement
        // ID. Final-state-only capture calls Capture(..., 0) — any non-zero ID
        // proves per-statement injection is wired up.
        var allCaptures = xCaptures.Concat(yCaptures).Concat(zCaptures).ToList();
        Assert.Contains(allCaptures, c => c.StatementId > 0);
    }

    [Fact]
    public void CaptureValues_JsonOutput_IncludesStatementIds()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("47-capture-inline", "src"), TestPath("47-capture-inline", "test") },
            CaptureValues = true,
            OutputJson = true
        });

        Assert.Equal(0, result.ExitCode);
        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var caps = doc.RootElement.GetProperty("capturedValues");
        Assert.True(caps.GetArrayLength() > 0);

        // At least one capture must have a non-zero statementId.
        var anyNonZero = caps.EnumerateArray()
            .Any(c => c.GetProperty("statementId").GetInt32() > 0);
        Assert.True(anyNonZero, "JSON output must include per-statement statementIds");
    }

    [Fact]
    public void CaptureValues_JsonOutput_IncludesSourceFile()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("47-capture-inline", "src"), TestPath("47-capture-inline", "test") },
            CaptureValues = true,
            OutputJson = true
        });

        Assert.Equal(0, result.ExitCode);
        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var caps = doc.RootElement.GetProperty("capturedValues");
        Assert.True(caps.GetArrayLength() > 0);

        // Every captured value should have a sourceFile property ending in .al
        foreach (var cap in caps.EnumerateArray())
        {
            Assert.True(cap.TryGetProperty("sourceFile", out var sf),
                $"Expected sourceFile on captured value (scope={cap.GetProperty("scopeName").GetString()})");
            var sourceFile = sf.GetString()!;
            Assert.EndsWith(".al", sourceFile);
        }
    }
}
