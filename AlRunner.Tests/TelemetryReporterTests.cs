using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for TelemetryReporter utility methods.
/// These cover the scrubbing / enrichment logic that is critical for actionable
/// auto-created GitHub issues (issue #1039).
/// </summary>
public class TelemetryReporterTests
{
    // ─── DeduplicateCompilationErrors ─────────────────────────────────────────

    [Fact]
    public void Dedup_CS1061_ExtractsMemberNames()
    {
        // Typical Roslyn CS1061 errors on the same type — member name must be preserved.
        var errors = new List<string>
        {
            "ReportExtension50500.cs(12,10): error CS1061: 'ReportExtension50500' does not contain a definition for 'ParentObject' and no accessible extension method 'ParentObject' accepting a first argument of type 'ReportExtension50500' could be found",
            "ReportExtension50500.cs(18,5): error CS1061: 'ReportExtension50500' does not contain a definition for 'GetReportDataItems' and no accessible extension method 'GetReportDataItems' accepting a first argument of type 'ReportExtension50500' could be found",
            "ReportExtension50500.cs(22,3): error CS1061: 'ReportExtension50500' does not contain a definition for 'OnPreDataItem' and no accessible extension method 'OnPreDataItem' accepting a first argument of type 'ReportExtension50500' could be found",
        };

        var result = TelemetryReporter.DeduplicateCompilationErrors(errors);

        Assert.Single(result);
        var (key, count, _) = result[0];
        Assert.Equal(3, count);
        // Key must mention the type name
        Assert.Contains("ReportExtension50500", key);
        // Key must list all three distinct member names
        Assert.Contains("ParentObject", key);
        Assert.Contains("GetReportDataItems", key);
        Assert.Contains("OnPreDataItem", key);
    }

    [Fact]
    public void Dedup_CS1061_SingleMember_IncludesMemberInKey()
    {
        var errors = new List<string>
        {
            "Codeunit50100.cs(5,1): error CS1061: 'Codeunit50100' does not contain a definition for 'ALRun' and no accessible extension method 'ALRun' could be found",
        };

        var result = TelemetryReporter.DeduplicateCompilationErrors(errors);

        Assert.Single(result);
        var (key, count, _) = result[0];
        Assert.Equal(1, count);
        Assert.Contains("ALRun", key);
    }

    [Fact]
    public void Dedup_CS1061_DifferentTypes_DoNotMerge()
    {
        // Errors on two different types must remain separate groups.
        var errors = new List<string>
        {
            "Codeunit50100.cs(5,1): error CS1061: 'Codeunit50100' does not contain a definition for 'ALRun' and no accessible extension method could be found",
            "Report70400.cs(9,2): error CS1061: 'Report70400' does not contain a definition for 'ALRun' and no accessible extension method could be found",
        };

        var result = TelemetryReporter.DeduplicateCompilationErrors(errors);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Dedup_OtherErrorCode_DoesNotAddMemberSuffix()
    {
        // Non-CS1061 errors should still deduplicate by type name without a member list.
        var errors = new List<string>
        {
            "Codeunit50100.cs(5,1): error CS0246: The type or namespace name 'NavText' could not be found",
            "Codeunit50100.cs(6,1): error CS0246: The type or namespace name 'NavText' could not be found",
        };

        var result = TelemetryReporter.DeduplicateCompilationErrors(errors);

        // Two identical messages — should collapse to one group
        Assert.Single(result);
        Assert.Equal(2, result[0].Count);
    }

    // ─── ScrubMessage ─────────────────────────────────────────────────────────

    [Fact]
    public void Scrub_FilePath_IsReplaced()
    {
        var msg = "Error at /home/user/myproject/src/File.cs line 5";
        var result = TelemetryReporter.ScrubMessagePublic(msg);
        Assert.DoesNotContain("/home/user/myproject", result);
        Assert.Contains("[path]", result);
    }

    [Fact]
    public void Scrub_WindowsPath_IsReplaced()
    {
        var msg = @"Cannot find file C:\Users\Stefan\myproject\src\File.al";
        var result = TelemetryReporter.ScrubMessagePublic(msg);
        Assert.DoesNotContain("Stefan", result);
        Assert.Contains("[path]", result);
    }

    [Fact]
    public void Scrub_ALLineHint_IsPreserved()
    {
        // "[AL line ~42 col 5 in MyCodeunit]" must NOT be stripped — it contains
        // only a line number and an object name, which are safe to transmit.
        var msg = "Object reference not set [AL line ~42 col 5 in MyCodeunit]";
        var result = TelemetryReporter.ScrubMessagePublic(msg);
        Assert.Contains("[AL line ~42 col 5 in MyCodeunit]", result);
    }

    [Fact]
    public void Scrub_ALLineHint_WithoutFilePathPrefix_IsPreserved()
    {
        // Hint alone, no surrounding path context
        var msg = "[AL line ~7 col 1 in SomeReport]";
        var result = TelemetryReporter.ScrubMessagePublic(msg);
        Assert.Equal(msg, result);
    }

    [Fact]
    public void Scrub_ShortMessage_NotTruncated()
    {
        var msg = "NullReferenceException";
        Assert.Equal(msg, TelemetryReporter.ScrubMessagePublic(msg));
    }

    // ─── BuildTestErrorReport — test identity ─────────────────────────────────

    [Fact]
    public void RuntimeGap_IncludesCodeunitAndProcedureName()
    {
        var result = new TestResult
        {
            Name = "TestMyFeature",
            CodeunitName = "MyTestCodeunit",
            Status = TestStatus.Error,
            IsRunnerBug = true,
            Message = "System.NotSupportedException: Not implemented",
            StackTrace = "   at AlRunner.Runtime.MockRecordHandle.Insert()\n   at AlRunner.Runtime.AlScope.Dispose()",
        };

        var report = TelemetryReporter.BuildTestErrorReportPublic(result);

        Assert.NotNull(report);
        Assert.Contains("MyTestCodeunit", report!.ScrubbedMessage);
        Assert.Contains("TestMyFeature", report.ScrubbedMessage);
    }

    [Fact]
    public void RuntimeGap_NullCodeunitName_StillIncludesProcedureName()
    {
        var result = new TestResult
        {
            Name = "TestSomething",
            CodeunitName = null,
            Status = TestStatus.Error,
            IsRunnerBug = true,
            Message = "MissingMethodException: method not found",
        };

        var report = TelemetryReporter.BuildTestErrorReportPublic(result);

        Assert.NotNull(report);
        Assert.Contains("TestSomething", report!.ScrubbedMessage);
    }

    // ─── RewriterGap — object type extraction ─────────────────────────────────

    [Fact]
    public void RewriterGap_ExtractsObjectType_FromGeneratedClassName()
    {
        // The rewriter failure tuple Name is the generated C# class name like "Codeunit_50100"
        var objectType = TelemetryReporter.ExtractObjectTypeFromName("Codeunit_50100");
        Assert.Equal("Codeunit", objectType);
    }

    [Fact]
    public void RewriterGap_ExtractsObjectType_Report()
    {
        Assert.Equal("Report", TelemetryReporter.ExtractObjectTypeFromName("Report_70400"));
    }

    [Fact]
    public void RewriterGap_ExtractsObjectType_Page()
    {
        Assert.Equal("Page", TelemetryReporter.ExtractObjectTypeFromName("Page_50200"));
    }

    [Fact]
    public void RewriterGap_ExtractsObjectType_ReportExtension()
    {
        Assert.Equal("ReportExtension", TelemetryReporter.ExtractObjectTypeFromName("ReportExtension50500"));
    }

    [Fact]
    public void RewriterGap_UnrecognisedName_ReturnsNull()
    {
        Assert.Null(TelemetryReporter.ExtractObjectTypeFromName("SomethingUnknown"));
        Assert.Null(TelemetryReporter.ExtractObjectTypeFromName(""));
    }
}

/// <summary>
/// Exposes internal TelemetryReporter helpers for unit testing.
/// We use a thin wrapper rather than InternalsVisibleTo to avoid coupling
/// test assembly to internal visibility changes.
/// </summary>
public static class TelemetryReporterExtensions
{
}
