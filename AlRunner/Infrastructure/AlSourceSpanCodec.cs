// AlSourceSpanCodec — decodes the `long` values BC's AL compiler packs into the
// [SourceSpans(...)] / [SignatureSpan(...)] attributes it emits on every generated
// NavMethodScope subclass (one entry per AL statement, plus one for the method
// signature). Two call sites need this: AlCallStackCapture (relative "line L" in AL
// stack traces) and AlCoverageTracker (absolute AL source line for --coverage). Both
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
}
