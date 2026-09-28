// Issue #4896: what a source-compiled reportextension adds to or changes on a report's request
// page, read from the extension's own emitted delta document. What BC answers for a
// [RequestPageHandler] is the corpus's claim (the PR body's Corpus-PR: line).
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourceReportExtensionRequestPageAreaTests : IDisposable
{
    private const int ReportId = 94961;
    private const int ExtensionId = 94962;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SourceReportExtensionRequestPageAreaTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-source-rext-area-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedReports", ReportId);
        RemoveFromDict("_parsedReportExtensions", ExtensionId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    private const string FixtureAl = """
        table 94961 "SRXA Record"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        report 94961 "SRXA Report"
        {
            ApplicationArea = Basic;
            UsageCategory = ReportsAndAnalysis;
            ProcessingOnly = true;
            dataset { dataitem(Item; "SRXA Record") { } }
            requestpage
            {
                layout
                {
                    area(Content)
                    {
                        group(Options)
                        {
                            field(BaseCtl; BaseValue) { }
                            field(BaseServiceCtl; BaseValue) { ApplicationArea = Service; }
                        }
                    }
                }
            }
            var BaseValue: Text[30];
        }

        reportextension 94962 "SRXA Report Ext" extends "SRXA Report"
        {
            requestpage
            {
                layout
                {
                    addlast(Options)
                    {
                        field(ExtServiceCtl; ExtValue) { ApplicationArea = Service; }
                        field(ExtNoneCtl; ExtValue) { }
                    }
                    modify(BaseServiceCtl) { ApplicationArea = Suite; }
                }
            }
            var ExtValue: Text[30];
        }
        """;

    private void Emit()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        File.WriteAllText(Path.Combine(_root, "Srxa.al"), FixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "SrxaModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        typeof(RecordPatches).GetMethod("TryParseReportFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { FixtureAl });
    }

    private static int Id(string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet("ReportExtension", ExtensionId, out var ext));
        Assert.True(AlObjectMetadataRegistry.TryGet("Report", ReportId, out var report));
        var m = System.Text.RegularExpressions.Regex.Match(ext + report, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"{name} not in the emitted documents");
        return int.Parse(m.Groups[1].Value);
    }

    [SkippableFact]
    public void AddedFields_AnswerTheirOwnArea_OrNone_AndAModifyIsAChangeToTheBaseField()
    {
        Emit();
        var (added, changes) = RecordPatches.SourceReportExtensionRequestPageAreas(ReportId);
        var fields = added.ToDictionary(a => a.Id, a => a.ApplicationArea);
        Assert.Equal(2, fields.Count);
        Assert.Equal("#Service", fields[Id("ExtServiceCtl")]);
        Assert.Null(fields[Id("ExtNoneCtl")]);
        Assert.Equal(new[] { Id("BaseServiceCtl") }, changes.Keys);
        Assert.Equal("#Suite", changes[Id("BaseServiceCtl")]);
    }

    [SkippableFact]
    public void AReportExtensionWhoseDeltaDocumentIsMissing_Refuses()
    {
        Emit();
        AlObjectMetadataRegistry.Clear();
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.SourceReportExtensionRequestPageAreas(ReportId));
        Assert.Contains($"reportextension {ExtensionId}", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void AReportWithNoSourceExtension_ContributesNothing()
    {
        Emit();
        var (added, changes) = RecordPatches.SourceReportExtensionRequestPageAreas(94969);
        Assert.Empty(added);
        Assert.Empty(changes);
    }
}
