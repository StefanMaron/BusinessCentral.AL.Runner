// AlDapWireTests — #3901 (a source-less stack frame must report line/column 0) and #3906 (the
// Locals scope's variablesReference must be positive). Runner-only behaviour: the Debug Adapter
// Protocol is the contract, not BC, so these belong here and not in the AL corpus.
//
// Pure tests over AlDapWire. No DAP session here can produce a frame the source map does not
// know — every Dap* fixture's frames resolve, and a dependency's code is itself mapped since
// #4272 — so the projection is driven with a synthetic AlDapFrame, and DapStackScopesWireTests
// pins the same two properties through a real session.

using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class AlDapWireTests
{
    // The transport drops nulls on the wire (DapTransport.WriteOpts); serialize the same way.
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement Wire(object o) => JsonSerializer.SerializeToElement(o, WireOpts);

    // Scope is not read by the projection; null! keeps the frame constructible without a live
    // NavMethodScope.
    private static AlDapFrame Frame(int id, string? path, int line) => new(id, "Proc" + id, path, line, null!);

    private static readonly Func<int, int> OneBasedLine = l => l;
    private static readonly Func<int, int> OneBasedColumn = c => c;
    private static readonly Func<int, int> ZeroBasedLine = l => l <= 0 ? l : l - 1;
    private static readonly Func<int, int> ZeroBasedColumn = c => c <= 0 ? c : c - 1;

    [Fact]
    public void SourceLessFrame_IsKept_WithNoSourceAndLineAndColumnZero_InEitherBase()
    {
        // The reported shape: a frame whose object the source map does not know still carries the
        // line the scope's own [SourceSpans] resolved — a number in the DEPENDENCY object's text.
        var frame = Frame(3, null, 41);

        foreach (var (toLine, toColumn) in new[] { (OneBasedLine, OneBasedColumn), (ZeroBasedLine, ZeroBasedColumn) })
        {
            var json = Wire(AlDapWire.StackFrame(frame, toLine, toColumn));
            Assert.Equal(3, json.GetProperty("id").GetInt32());
            Assert.Equal("Proc3", json.GetProperty("name").GetString());
            Assert.False(json.TryGetProperty("source", out _), json.ToString());
            Assert.Equal(0, json.GetProperty("line").GetInt32());
            Assert.Equal(0, json.GetProperty("column").GetInt32());
        }
    }

    [Fact]
    public void LocatedFrame_KeepsItsSourceAndConvertsLineAndColumnToTheClientBase()
    {
        var frame = Frame(1, "/src/Foo.Codeunit.al", 12);

        var oneBased = Wire(AlDapWire.StackFrame(frame, OneBasedLine, OneBasedColumn));
        Assert.Equal("/src/Foo.Codeunit.al", oneBased.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal("Foo.Codeunit.al", oneBased.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(12, oneBased.GetProperty("line").GetInt32());
        Assert.Equal(1, oneBased.GetProperty("column").GetInt32());

        var zeroBased = Wire(AlDapWire.StackFrame(frame, ZeroBasedLine, ZeroBasedColumn));
        Assert.Equal(11, zeroBased.GetProperty("line").GetInt32());
        Assert.Equal(0, zeroBased.GetProperty("column").GetInt32());
    }

    [Fact]
    public void FrameWithAPathButNoResolvedLine_ReportsNoSource_SoZeroIsNotReadAsTheFirstLine()
    {
        // The mirror case. For a 0-based client `source` + `line: 0` is the first line of the
        // file, so a frame whose line did not resolve must not send both.
        var json = Wire(AlDapWire.StackFrame(Frame(0, "/src/Foo.Codeunit.al", 0), ZeroBasedLine, ZeroBasedColumn));
        Assert.False(json.TryGetProperty("source", out _), json.ToString());
        Assert.Equal(0, json.GetProperty("line").GetInt32());
        Assert.Equal(0, json.GetProperty("column").GetInt32());

        // And the converse that keeps it honest: a real first-line frame DOES keep its source and
        // reports line 0 for that client, because 0 is then a genuine coordinate.
        var firstLine = Wire(AlDapWire.StackFrame(Frame(0, "/src/Foo.Codeunit.al", 1), ZeroBasedLine, ZeroBasedColumn));
        Assert.Equal("/src/Foo.Codeunit.al", firstLine.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal(0, firstLine.GetProperty("line").GetInt32());
    }

    [Fact]
    public void StoppedLine_IsTheTopFramesLineInTheClientBase_AndOmittedWhenThereIsNone()
    {
        var located = new[] { Frame(0, "/src/Foo.Codeunit.al", 12), Frame(1, "/src/Foo.Codeunit.al", 5) };
        Assert.Equal(12, AlDapWire.StoppedLine(located, OneBasedLine));
        Assert.Equal(11, AlDapWire.StoppedLine(located, ZeroBasedLine));

        // A failed walk leaves no frames; a top frame with no source has no line to report.
        Assert.Null(AlDapWire.StoppedLine(Array.Empty<AlDapFrame>(), ZeroBasedLine));
        Assert.Null(AlDapWire.StoppedLine(new[] { Frame(0, null, 41) }, ZeroBasedLine));

        // On the wire "none" is an absent property, never the 0 a 0-based client reads as line 0.
        var absent = Wire(new { reason = "breakpoint", line = AlDapWire.StoppedLine(Array.Empty<AlDapFrame>(), ZeroBasedLine) });
        Assert.False(absent.TryGetProperty("line", out _), absent.ToString());
    }

    [Fact]
    public void VariableHandles_ArePositive_EvenForTheTopFrameWhoseIdIsZero()
    {
        var handles = new AlDapVariableHandles();
        Assert.True(handles.ForFrame(0) > 0);
        Assert.True(handles.ForFrame(1) > 0);
    }

    [Fact]
    public void VariableHandles_ResolveBackToTheFrameTheyWereIssuedFor_NotANeighbour()
    {
        var handles = new AlDapVariableHandles();
        var h0 = handles.ForFrame(0);
        var h1 = handles.ForFrame(1);
        Assert.NotEqual(h0, h1);

        Assert.True(handles.TryResolveFrame(h0, out var f0));
        Assert.True(handles.TryResolveFrame(h1, out var f1));
        Assert.Equal(0, f0);
        Assert.Equal(1, f1);

        // Asking again within one stop is the same handle, not a fresh one.
        Assert.Equal(h0, handles.ForFrame(0));
    }

    [Fact]
    public void VariableHandles_ZeroAndNeverIssuedHandlesDoNotResolve()
    {
        var handles = new AlDapVariableHandles();
        var issued = handles.ForFrame(0);
        // 0 is "no children" and a frame id of 0 must not make it resolve (#3906).
        Assert.False(handles.TryResolveFrame(0, out _));
        Assert.False(handles.TryResolveFrame(issued + 100, out _));
        Assert.False(handles.TryResolveFrame(-1, out _));
    }

    [Fact]
    public void VariableHandles_AreInvalidatedByAStop_AndANewHandleNeverAliasesAnOldOne()
    {
        var handles = new AlDapVariableHandles();
        var before = handles.ForFrame(0);
        handles.Reset();

        Assert.False(handles.TryResolveFrame(before, out _));
        var after = handles.ForFrame(0);
        Assert.NotEqual(before, after);
        // The old handle misses even though the new stop has a frame 0 of its own.
        Assert.False(handles.TryResolveFrame(before, out _));
        Assert.True(handles.TryResolveFrame(after, out var frame));
        Assert.Equal(0, frame);
    }
}
