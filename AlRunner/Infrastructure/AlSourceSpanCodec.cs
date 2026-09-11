// AlSourceSpanCodec — decodes the `long` values BC's AL compiler packs into the
// [SourceSpans(...)] / [SignatureSpan(...)] attributes it emits on every generated
// NavMethodScope subclass (one entry per AL statement, plus one for the method
// signature). Four consumers need this: AlCallStackCapture (relative "line L" in AL
// stack traces), AlCoverageTracker (absolute AL source line for --coverage), and the
// two DAP consumers, DapBreakpointResolver and AlDapStackWalker (#3786). Everything
// except AlCallStackCapture wants a FILE line, and AbsoluteFromLine alone does not give
// one: BC's number is relative to the owning object's text, so each of those three adds
// AlSourceLocationMap.LineOffset for the object. All
// used to decode the bit layout independently; this is the single place it happens now
// — see .claude/rules — "Lift the span-decoding ... into a shared helper ... Do not
// duplicate the bit layout."
//
// Layout (StructLayout.Explicit on BC's side, little-endian in the packed long):
//   bits 48-63 = from.line     bits 32-47 = from.column
//   bits 16-31 = to.line       bits  0-15 = to.column
// All four values are 0-based. Confirmed empirically (not assumed) via DUMP_CS=1 on
// AlRunner.Tests/Fixtures/RecordTriggerXRec: the decoded from.line for statement 0 is
// 25, and "Rec.Init();" — the statement BC actually instruments there — is on AL source
// line 26 (1-based). So an absolute, human-facing AL line is decoded-from-line + 1. A
// *relative* line (the "line L" BC's own stack traces print) is statement.from-line
// minus signature.from-line; because both operands are 0-based, the +1 offset cancels
// in the subtraction and no adjustment is needed there.
namespace AlRunner.Infrastructure;

public static class AlSourceSpanCodec
{
    /// <summary>
    /// Decodes one packed SourceSpans/SignatureSpan entry into its four 0-based
    /// (line, column) components.
    /// </summary>
    public static (ushort FromLine, ushort FromColumn, ushort ToLine, ushort ToColumn) Decode(long encodedSpan)
    {
        ulong v = unchecked((ulong)encodedSpan);
        var toColumn = (ushort)v;
        var toLine = (ushort)(v >> 16);
        var fromColumn = (ushort)(v >> 32);
        var fromLine = (ushort)(v >> 48);
        return (fromLine, fromColumn, toLine, toColumn);
    }

    /// <summary>
    /// Inverse of <see cref="Decode"/>: packs four 0-based components into one
    /// SourceSpans entry. Used by tests that need a span table shaped exactly like
    /// BC's without compiling AL (e.g. AlMemberSyntaxIndexTests); production code only
    /// ever decodes what the compiler emitted.
    /// </summary>
    internal static long Encode(int fromLine, int fromColumn, int toLine, int toColumn)
    {
        ulong v = ((ulong)(ushort)fromLine << 48) | ((ulong)(ushort)fromColumn << 32)
                | ((ulong)(ushort)toLine << 16) | (ushort)toColumn;
        return unchecked((long)v);
    }

    /// <summary>
    /// The AL stack-trace "line L" for a statement: its from-line relative to the
    /// enclosing method's SignatureSpan from-line. Matches BC's own service-tier output
    /// format exactly (verified against AlCallStackCapture's pre-existing behaviour,
    /// which this replaces without changing output).
    /// </summary>
    public static int RelativeLine(long statementSpan, long signatureSpan)
    {
        var stmt = Decode(statementSpan);
        var sig = Decode(signatureSpan);
        return (ushort)(stmt.FromLine - sig.FromLine);
    }

    /// <summary>
    /// The absolute, 1-based AL source line a statement span starts on — what a coverage
    /// report or an editor gutter needs, as opposed to RelativeLine's stack-trace format.
    /// </summary>
    public static int AbsoluteFromLine(long statementSpan) => Decode(statementSpan).FromLine + 1;

    /// <summary>
    /// The 1-based column a statement span starts at — the other half of
    /// <see cref="AbsoluteFromLine"/>, and what tells two statements on ONE line apart. DAP
    /// counts columns from 1 by default, which is the convention this matches; BC's packed
    /// value is 0-based like its line.
    /// </summary>
    public static int AbsoluteFromColumn(long statementSpan) => Decode(statementSpan).FromColumn + 1;

    /// <summary>The 1-based line a statement span ENDS on — equal to the from-line for a
    /// statement written on one line, greater for one that wraps.</summary>
    public static int AbsoluteToLine(long statementSpan) => Decode(statementSpan).ToLine + 1;

    /// <summary>The 1-based column a statement span ends at. With the three above, this is
    /// what decides whether a requested column falls INSIDE a statement rather than at its
    /// start — the difference between an inline breakpoint a client placed precisely and one
    /// it placed somewhere in the middle of the statement it meant.</summary>
    public static int AbsoluteToColumn(long statementSpan) => Decode(statementSpan).ToColumn + 1;
}
