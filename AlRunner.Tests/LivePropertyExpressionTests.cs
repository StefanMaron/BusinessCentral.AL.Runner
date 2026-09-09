// LivePropertyExpressionTests — the C# half of issue #3730.
//
// The AL-observable claim (an action's Enabled, and a control's Enabled/Editable, bound to a
// source-table field follow the CURRENT ROW, while a control's Visible does not) belongs to real
// BC and is adjudicated upstream by corpus codeunit 60436 "TPAE Tests".
//
// What is provable here without a loaded BC runtime is the C# contract that fix rests on: the
// narrowing RunnerPageInstance applies to a field's ClientObject before it reaches
// PageControlExpression, and the fact that PageControlExpression really does evaluate the three
// shapes the corpus arms use once a resolver answers field names at all — including an
// Option compared against ORDINALS, which is how the AL compiler writes such a comparison into
// the page metadata.
using System;
using System.Diagnostics;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class LivePropertyExpressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData("Filled")]
    [InlineData("")]
    [InlineData(1)]
    [InlineData(0)]
    public void TryComparableFieldValue_PassesThroughAShapeCompareCanOrder(object clientObject)
    {
        Assert.True(RunnerPageInstance.TryComparableFieldValue(clientObject, out var value));
        Assert.Equal(clientObject, value);
    }

    [Fact]
    public void TryComparableFieldValue_PassesThroughDecimalAndTheOtherNumericWidths()
    {
        Assert.True(RunnerPageInstance.TryComparableFieldValue(12.5m, out var dec));
        Assert.Equal(12.5m, dec);

        Assert.True(RunnerPageInstance.TryComparableFieldValue((long)7, out var lng));
        Assert.Equal((long)7, lng);
    }

    // The negative direction, and the one that matters: a shape the evaluator's Compare has no
    // ordering for must be REFUSED, so EvaluateProperty raises naming the expression rather than
    // handing Compare an operand it would have to invent an answer for.
    [Fact]
    public void TryComparableFieldValue_RefusesAShapeCompareCannotOrder()
    {
        Assert.False(RunnerPageInstance.TryComparableFieldValue(Guid.NewGuid(), out var guid));
        Assert.Null(guid);

        Assert.False(RunnerPageInstance.TryComparableFieldValue(new object(), out var opaque));
        Assert.Null(opaque);

        Assert.False(RunnerPageInstance.TryComparableFieldValue(null, out var missing));
        Assert.Null(missing);
    }

    // The three property texts the corpus arms produce, evaluated through the real parser with a
    // resolver that answers field names — the composition the #3730 fix creates. The metadata
    // spellings here are the compiler's own (see PageControlExpression's file header): a bare
    // field name for a Boolean, a quoted comparison for Text, and ORDINALS for an Option.
    [Theory]
    [InlineData("Flag", true, true)]
    [InlineData("Flag", false, false)]
    public void ABareBooleanFieldNameEvaluatesToTheFieldsValue(string text, bool flag, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            text, Resolver(flag, "Filled", 1), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    [Theory]
    [InlineData("Filled", true)]
    [InlineData("", false)]
    public void ATextComparisonEvaluatesAgainstTheFieldsValue(string value, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            "Value <> ''", Resolver(true, value, 1), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void AnOptionComparisonEvaluatesAgainstTheFieldsOrdinal(int ordinal, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            "( LineType = 1 ) or ( LineType = 2 )",
            Resolver(true, "Filled", ordinal), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    // And the refusal survives the composition: a field the resolver does not answer must still
    // come back as a failure, never as a default, so #3693's Invoke()-time Enabled check cannot
    // silently invent an answer for a shape nobody has measured.
    [Fact]
    public void AnUnresolvedIdentifierIsStillAFailure()
    {
        Assert.False(PageControlExpression.TryEvaluateBoolean(
            "Unknown", Resolver(true, "Filled", 1), out _, out var why));
        Assert.Contains("Unknown", why);
    }

    private static PageControlExpression.ResolveIdentifier Resolver(bool flag, string value, int lineType)
        => (string name, bool quoted, out object? resolved) =>
        {
            object? raw = name switch
            {
                "Flag" => flag,
                "Value" => value,
                "LineType" => lineType,
                _ => null,
            };
            if (raw == null) { resolved = null; return false; }
            return RunnerPageInstance.TryComparableFieldValue(raw, out resolved);
        };
}

// FlowFieldBoundLivePropertyTests — the narrowing the live resolver applies BEFORE it reads a
// field: a FlowField is not a live-resolvable identifier, and must reach the caller's refusal
// rather than an invented answer.
//
// WHY A BUNDLE AND NOT A UNIT ARM
//   The guard lives in RunnerPageInstance.TryResolveSourceTableField, which needs the page's
//   record and BC's own NCLMetaField — no unit arm reaches it, and one written against the
//   narrowing helper alone would prove an enum comparison while leaving the wiring (that the
//   call site consults FieldClass at all) untested. That wiring is the defect.
//
// WHAT THE FIXTURE ESTABLISHES
//   'HasKid' is a FlowField whose formula is TRUE on the row the page is on — one child row is
//   inserted for it. An uncalculated FlowField's ClientObject is nonetheless false, which
//   narrows cleanly to bool, so without the guard the runner answers Enabled = false on a row
//   where the condition holds and Invoke() skips OnAction: a silent wrong answer, the failure
//   class .claude/rules/loud-failures.md forbids. What real BC answers for a FlowField-bound
//   Enabled is UNMEASURED — no corpus arm and no container run in #3730 covers it — so the
//   runner refuses rather than calculating.
//
// THE CONTROL ARM
//   NormalBooleanBoundEnabled_StillFollowsTheRow, on the same page and the same row, must keep
//   PASSING. Without it the guard could be satisfied by refusing every field, which would undo
//   #3730 entirely and still turn the first arm green.
public sealed class FlowFieldBoundLivePropertyTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public FlowFieldBoundLivePropertyTests()
    {
        _root = TestScratch.Dir("al-runner-flowfield-live-property");
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "d3f2a1b0-3730-4c5d-9e6f-000000003730",
          "name": "FlowField Live Property Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62480, "to": 62489 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "FfeObjects.al"), """
        table 62480 "FFE Row"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; PK; Code[10]) { }
                field(2; Flag; Boolean) { }
                field(3; Value; Text[30]) { }
                field(4; HasKid; Boolean)
                {
                    FieldClass = FlowField;
                    CalcFormula = exist("FFE Child" where(Parent = field(PK)));
                }
            }
            keys { key(K; PK) { Clustered = true; } }
        }

        table 62481 "FFE Child"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Parent; Code[10]) { }
            }
            keys { key(K; "Entry No.") { Clustered = true; } }
        }

        page 62482 "FFE Card"
        {
            PageType = Card;
            SourceTable = "FFE Row";
            ApplicationArea = All;
            UsageCategory = Administration;

            layout
            {
                area(Content)
                {
                    field(ValueCtl; Rec.Value) { ApplicationArea = All; }
                }
            }

            actions
            {
                area(Processing)
                {
                    action(FlowFieldEnabled)
                    {
                        ApplicationArea = All;
                        Enabled = Rec.HasKid;
                        trigger OnAction()
                        begin
                        end;
                    }
                    action(NormalEnabled)
                    {
                        ApplicationArea = All;
                        Enabled = Rec.Flag;
                        trigger OnAction()
                        begin
                        end;
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(dir, "FfeTests.Codeunit.al"), """
        codeunit 62483 "FFE Tests"
        {
            Subtype = Test;

            local procedure InitializeRowWithAChild(var Row: Record "FFE Row")
            var
                Child: Record "FFE Child";
            begin
                Row.DeleteAll();
                Child.DeleteAll();
                Row.Init();
                Row.PK := 'ROW1';
                Row.Flag := true;
                Row.Value := 'Filled';
                Row.Insert();
                Child.Init();
                Child."Entry No." := 1;
                Child.Parent := 'ROW1';
                Child.Insert();
            end;

            // Reports whatever Enabled() answered, so the runner's refusal and a silently
            // invented answer are told apart in the output instead of both reading as "failed".
            [Test]
            procedure FlowFieldBoundEnabled_IsRefusedNotAnsweredFalse()
            var
                Row: Record "FFE Row";
                Card: TestPage "FFE Card";
            begin
                InitializeRowWithAChild(Row);
                Card.OpenEdit();
                Card.GoToRecord(Row);
                if Card.ValueCtl.Value() <> 'Filled' then
                    Error('the page must be on the row just inserted');
                Error('OBSERVED Enabled=%1', Card.FlowFieldEnabled.Enabled());
            end;

            // The control: a Normal Boolean field on the same page and the same row still
            // answers the row, so the guard above cannot be a blanket refusal.
            [Test]
            procedure NormalBooleanBoundEnabled_StillFollowsTheRow()
            var
                Row: Record "FFE Row";
                Card: TestPage "FFE Card";
            begin
                InitializeRowWithAChild(Row);
                Card.OpenEdit();
                Card.GoToRecord(Row);
                if not Card.NormalEnabled.Enabled() then
                    Error('a Normal Boolean field bound to Enabled must follow the row (#3730)');
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner()
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{_root}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The reported header line for one test plus its indented continuation lines —
    /// what a developer reads, and the only place the refusal exists.</summary>
    private static string BlockFor(string output, string testMethod)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var block = new StringBuilder();
        var inBlock = false;
        foreach (var line in lines)
        {
            var isHeader = line.StartsWith("FAIL ", StringComparison.Ordinal)
                        || line.StartsWith("PASS ", StringComparison.Ordinal)
                        || line.StartsWith("ERROR", StringComparison.Ordinal)
                        || line.StartsWith("SKIP ", StringComparison.Ordinal);
            if (isHeader)
            {
                if (inBlock) break;
                inBlock = line.Contains($".{testMethod} (", StringComparison.Ordinal);
                if (inBlock) block.AppendLine(line);
                continue;
            }
            if (inBlock) block.AppendLine(line);
        }
        Assert.True(block.Length > 0,
            $"no reported block for '{testMethod}' — the fixture did not run as expected. Full output:\n{output}");
        return block.ToString();
    }

    [SkippableFact]
    public void AFlowFieldBoundEnabled_IsRefusedByName_AndANormalFieldStillAnswersTheRow()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner();

        var refused = BlockFor(output, "FlowFieldBoundEnabled_IsRefusedNotAnsweredFalse");
        Assert.Contains("out-of-scope", refused, StringComparison.Ordinal);
        Assert.Contains("HasKid", refused, StringComparison.Ordinal);
        // The defect, pinned: an answer at all — Yes or No — means the runner resolved an
        // uncalculated FlowField and invented a verdict for a shape nobody has measured.
        Assert.DoesNotContain("OBSERVED Enabled=", refused, StringComparison.Ordinal);

        var control = BlockFor(output, "NormalBooleanBoundEnabled_StillFollowsTheRow");
        Assert.StartsWith("PASS ", control, StringComparison.Ordinal);
    }
}
