using System.Text.RegularExpressions;

namespace AlRunner;

/// <summary>
/// Maps .NET exception stack traces to AL stack frames.
/// Classifies each frame as user code, subtle runtime code, or de-emphasized infrastructure.
/// </summary>
public static class StackFrameMapper
{
    // Matches "   at Namespace.Class.Method(args) in path/to/file.al:line 42"
    private static readonly Regex FrameWithSource = new Regex(
        @"^\s*at\s+(?<method>[^\s][^()]*)(?:\([^)]*\))?\s+in\s+(?<file>.+?):line\s+(?<line>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Matches "   at Namespace.Class.Method(args)" without source info.
    private static readonly Regex FrameNoSource = new Regex(
        @"^\s*at\s+(?<method>[^\s][^()]*)(?:\([^)]*\))?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parse all stack frames from an exception's stack trace into <see cref="AlStackFrame"/> records.
    /// </summary>
    public static List<AlStackFrame> Walk(Exception ex)
    {
        var result = new List<AlStackFrame>();
        var trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace)) return result;

        var lines = trace.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var withSource = FrameWithSource.Match(line);
            if (withSource.Success)
            {
                var file = withSource.Groups["file"].Value.Trim();
                var lineNum = int.Parse(withSource.Groups["line"].Value);
                var method = withSource.Groups["method"].Value.Trim();
                var isUser = file.EndsWith(".al", StringComparison.OrdinalIgnoreCase);
                var hint = isUser ? FramePresentationHint.Normal : ClassifyHint(file, method);
                result.Add(new AlStackFrame(
                    File: file,
                    Line: lineNum,
                    Column: null,
                    IsUserCode: isUser,
                    Name: method,
                    Hint: hint));
                continue;
            }

            var noSource = FrameNoSource.Match(line);
            if (noSource.Success)
            {
                var method = noSource.Groups["method"].Value.Trim();
                result.Add(new AlStackFrame(
                    File: null,
                    Line: null,
                    Column: null,
                    IsUserCode: false,
                    Name: method,
                    Hint: ClassifyHint(null, method)));
            }
        }

        return result;
    }

    /// <summary>
    /// Return the first user-code frame in the list, which is the frame closest to the throw site.
    /// </summary>
    public static AlStackFrame? FindDeepestUserFrame(IReadOnlyList<AlStackFrame> frames)
    {
        // .NET stack traces list the throw site first, then each caller out to the
        // entry point. The user frame *closest to the throw* is therefore the FIRST
        // user-code frame in the list — that is the line ALchemist surfaces as the
        // inline error decoration.
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].IsUserCode) return frames[i];
        }
        return null;
    }

    /// <summary>
    /// Classify a stack frame as Normal, Subtle, or Deemphasize based on file extension and method namespace.
    /// </summary>
    public static FramePresentationHint ClassifyHint(string? file, string? methodName)
    {
        if (file != null && file.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
            return FramePresentationHint.Normal;

        if (methodName != null
            && (methodName.StartsWith("AlRunner.Runtime.", StringComparison.Ordinal)
                || methodName.StartsWith("Microsoft.Dynamics.", StringComparison.Ordinal)))
        {
            return FramePresentationHint.Subtle;
        }

        return FramePresentationHint.Deemphasize;
    }
}
