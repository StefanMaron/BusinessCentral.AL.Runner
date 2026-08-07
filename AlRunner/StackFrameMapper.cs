// New for v2, part of #1641 (protocol-v2 port). v1's StackFrameMapper parsed .NET
// exception stack traces (a Roslyn-emitted C# pipeline meant .NET frames WERE the
// AL frames). v2 runs unmodified MS/ISV DLLs in-process and captures the AL call
// stack separately via AlRunner.Infrastructure.AlCallStackCapture, which formats
// it in BC's own service-tier call-stack STRING syntax:
//   "ObjectName"(ObjectType N).MethodName[(Trigger)][ line L][ - App by Pub version V]
// one frame per line, deepest (closest to the throw) first. This mapper parses
// THAT format, not a .NET stack trace.
using System.Text.RegularExpressions;

namespace AlRunner;

public static class StackFrameMapper
{
    // Groups: obj (quotes doubled per BC's AppendQuoted), type, num, method,
    // trigger, line, app, pub, ver. The app/pub/ver suffix and the line segment
    // are both optional (AlCallStackCapture.FormatFrame omits line when
    // GetRelativeLine returns -1, and omits the app suffix when no assembly
    // metadata was registered for that scope's assembly).
    private static readonly Regex FramePattern = new Regex(
        @"^""(?<obj>(?:[^""]|"""")*)""\((?<type>[^\s)]+)\s+(?<num>\d+)\)\.(?<method>[^\s(]+)"
        + @"(?<trigger>\(Trigger\))?"
        + @"(?:\s+line\s+(?<line>\d+))?"
        + @"(?:\s+-\s+(?<app>.*?)\s+by\s+(?<pub>.*?)\s+version\s+(?<ver>.*))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a captured AL call-stack string (see
    /// <see cref="AlRunner.Infrastructure.AlCallStackCapture.GetCaptured()"/>) into
    /// structured frames, one per line, in the same deepest-first order as the input.
    /// Every scope AlCallStackCapture records has a non-null ApplicationObject — real
    /// AL code, never runner/C# plumbing — so every returned frame has
    /// <see cref="AlStackFrame.IsUserCode"/> = true and
    /// <see cref="AlStackFrame.Hint"/> = <see cref="FramePresentationHint.Normal"/>.
    /// A line that doesn't match BC's format is kept verbatim as <c>Name</c> rather
    /// than dropped or given an invented Line/File — see .claude/rules/loud-failures.md
    /// (never fabricate a value the source didn't actually carry).
    /// </summary>
    public static List<AlStackFrame> Walk(string? alStack)
    {
        var result = new List<AlStackFrame>();
        if (string.IsNullOrWhiteSpace(alStack)) return result;

        foreach (var rawLine in alStack.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var m = FramePattern.Match(line);
            if (!m.Success)
            {
                result.Add(new AlStackFrame(
                    File: null, Line: null, Column: null,
                    IsUserCode: true, Name: line, Hint: FramePresentationHint.Normal));
                continue;
            }

            var name = $"\"{m.Groups["obj"].Value}\"({m.Groups["type"].Value} {m.Groups["num"].Value}).{m.Groups["method"].Value}"
                       + (m.Groups["trigger"].Success ? "(Trigger)" : "");
            int? lineNo = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;

            result.Add(new AlStackFrame(
                File: null, Line: lineNo, Column: null,
                IsUserCode: true, Name: name, Hint: FramePresentationHint.Normal));
        }

        return result;
    }

    /// <summary>
    /// Un-double BC's escaped quotes in a frame's object-name segment (the part
    /// between the outer quotes in <see cref="AlStackFrame.Name"/>), for display.
    /// Returns null if the frame's Name doesn't have the quoted-object-name shape
    /// (e.g. it's a raw unparsed line).
    /// </summary>
    private static readonly Regex ObjectNamePrefix = new Regex(
        @"^""(?<obj>(?:[^""]|"""")*)""\(", RegexOptions.Compiled);

    public static string? UnquoteObjectName(AlStackFrame frame)
    {
        if (frame.Name == null) return null;
        var m = ObjectNamePrefix.Match(frame.Name);
        if (!m.Success) return null;
        return m.Groups["obj"].Value.Replace("\"\"", "\"");
    }
}
