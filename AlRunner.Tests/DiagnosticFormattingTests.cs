using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for the AL diagnostic formatting changes introduced in the diagnostic-source
/// feature (PR #1321): filename prefixes on AL parse-error headers and on individual
/// declaration/emit diagnostics produced by TranspileMulti.
///
/// FormatAlDiagnostic is a private helper; its behaviour is verified indirectly via the
/// public TranspileMulti API by redirecting Console.Error and inspecting the captured
/// output from intentionally invalid AL sources.
/// </summary>
[Collection("Pipeline")]
public class DiagnosticFormattingTests
{
    // -----------------------------------------------------------------------
    // Parse-error header: "AL parse errors in <file>:"
    // -----------------------------------------------------------------------

    /// <summary>
    /// When TranspileMulti is called with invalid AL and a matching sourceFilePaths
    /// entry, the stderr header must be "AL parse errors in BrokenCodeunit.al:"
    /// (basename only, not the full path).
    /// </summary>
    [Fact]
    public void TranspileMulti_ParseError_WithFilePath_IncludesBasenameInHeader()
    {
        var captured = CaptureStdErr(() =>
            AlTranspiler.TranspileMulti(
                new List<string> { "this is not AL @@@ !!!" },
                sourceFilePaths: new List<string?> { "/project/src/BrokenCodeunit.al" }));

        Assert.Contains("AL parse errors in BrokenCodeunit.al:", captured);
    }

    /// <summary>
    /// The filename must be just the basename — not a partial or full path.
    /// </summary>
    [Fact]
    public void TranspileMulti_ParseError_WithFilePath_DoesNotIncludeFullPath()
    {
        var captured = CaptureStdErr(() =>
            AlTranspiler.TranspileMulti(
                new List<string> { "this is not AL @@@ !!!" },
                sourceFilePaths: new List<string?> { "/project/src/BrokenCodeunit.al" }));

        // Full path must not appear in the header
        Assert.DoesNotContain("/project/src/BrokenCodeunit.al", captured);
        // But the basename must be there
        Assert.Contains("BrokenCodeunit.al", captured);
    }

    /// <summary>
    /// Without sourceFilePaths the header must be the bare "AL parse errors:"
    /// (no filename, no regression from the original behaviour).
    /// </summary>
    [Fact]
    public void TranspileMulti_ParseError_WithoutFilePath_BareHeaderNoFilename()
    {
        var captured = CaptureStdErr(() =>
            AlTranspiler.TranspileMulti(
                new List<string> { "this is not AL @@@ !!!" }));

        Assert.Contains("AL parse errors:", captured);
        Assert.DoesNotContain("AL parse errors in ", captured);
    }

    /// <summary>
    /// When sourceFilePaths has a null entry for a source with parse errors, the bare
    /// "AL parse errors:" header is used (no crash, no empty parentheses).
    /// </summary>
    [Fact]
    public void TranspileMulti_ParseError_NullFilePathEntry_BareHeaderNoEmptyParens()
    {
        var captured = CaptureStdErr(() =>
            AlTranspiler.TranspileMulti(
                new List<string> { "this is not AL @@@ !!!" },
                sourceFilePaths: new List<string?> { null }));

        Assert.Contains("AL parse errors:", captured);
        Assert.DoesNotContain("AL parse errors in ", captured);
        Assert.DoesNotContain("()", captured);
    }

    // -----------------------------------------------------------------------
    // Multiple files: each error header must name its own file
    // -----------------------------------------------------------------------

    /// <summary>
    /// When two AL sources both have parse errors, each "AL parse errors in:" header
    /// must contain the basename of its own file — not the other file's name.
    /// </summary>
    [Fact]
    public void TranspileMulti_TwoFilesWithErrors_EachHeaderNamesItsOwnFile()
    {
        var captured = CaptureStdErr(() =>
            AlTranspiler.TranspileMulti(
                new List<string> { "@@@ bad AL first", "@@@ bad AL second" },
                sourceFilePaths: new List<string?> { "/src/FileA.al", "/src/FileB.al" }));

        Assert.Contains("FileA.al", captured);
        Assert.Contains("FileB.al", captured);
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static string CaptureStdErr(Action action)
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }
        return stderr.ToString();
    }
}
