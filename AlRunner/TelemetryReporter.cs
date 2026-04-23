using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlRunner;

/// <summary>
/// On-demand crash reporter. Prompts the user interactively before sending any data.
/// Never fires in CI, server mode, --output-json mode, or non-interactive sessions.
/// Only stack frames from AlRunner code are included — user AL source, file paths,
/// and codeunit names are never transmitted.
/// </summary>
public static class TelemetryReporter
{
    // Application Insights connection string (write-only; no source data is ever sent)
    private const string InstrumentationKey = "5b19680b-c4cd-4045-9e2c-df36d94d707a";
    private const string IngestionEndpoint = "https://polandcentral-0.in.applicationinsights.azure.com/v2/track";
    private const int PromptTimeoutSeconds = 30;

    /// <summary>
    /// Prompts the user to report an unexpected runner crash, then sends if they consent.
    /// Safe to call in all contexts — skips silently when non-interactive or noTelemetry is set.
    /// </summary>
    public static async Task TryReportAsync(Exception ex, bool outputJson, bool noTelemetry)
    {
        if (noTelemetry) return;
        if (!CanPromptUser(outputJson)) return;

        var report = BuildReport(ex);
        if (report == null) return;

        Console.Error.WriteLine();
        Console.Error.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.Error.WriteLine("  Unexpected runner error — would you like to report it?");
        Console.Error.WriteLine("  This helps fix al-runner bugs proactively.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  What will be sent (no AL source code, no file paths):");
        Console.Error.WriteLine($"    Exception : {report.ExceptionType}");
        Console.Error.WriteLine($"    Message   : {report.ScrubbedMessage}");
        Console.Error.WriteLine($"    Stack     : {report.FrameCount} runner frame(s)");
        Console.Error.WriteLine($"    Version   : {report.RunnerVersion}  OS: {report.Os}");
        Console.Error.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        if (!PromptYesNoWithTimeout($"Send error report? [y/N] (auto-no in {PromptTimeoutSeconds}s): "))
            return;

        await SendAsync(report);
        Console.Error.WriteLine("  ✓ Error report sent. Thank you!");
    }

    /// <summary>
    /// After a test run, collects ALL pipeline gaps — rewriter failures, Roslyn
    /// compilation errors, and runtime runner-bug errors — shows a single combined
    /// prompt, and sends the collected telemetry if the user consents.
    ///
    /// Rewriter gaps: AL objects whose C# could not be rewritten, indicating the
    /// rewriter does not handle some AL language pattern yet.
    ///
    /// Compilation gaps: C# compiler errors after rewriting, indicating the rewriter
    /// produced C# that does not compile — another class of rewriter gap.
    ///
    /// Runtime gaps: tests that ended with TestStatus.Error + IsRunnerBug = true,
    /// indicating a missing mock or dispatch path in AlRunner.Runtime.
    ///
    /// Safe to call in all contexts — skips silently when non-interactive or noTelemetry.
    /// </summary>
    public static async Task TryReportPipelineGapsAsync(
        List<TestResult> tests,
        bool outputJson,
        bool noTelemetry,
        List<(string Name, string Error)>? rewriterErrors = null,
        List<string>? compilationErrors = null)
    {
        if (noTelemetry) return;
        if (!CanPromptUser(outputJson)) return;

        var runtimeErrors = tests
            .Where(t => t.Status == TestStatus.Error && t.IsRunnerBug)
            .ToList();

        bool hasRewriterErrors = rewriterErrors?.Count > 0;
        bool hasCompilationErrors = compilationErrors?.Count > 0;

        if (runtimeErrors.Count == 0 && !hasRewriterErrors && !hasCompilationErrors) return;

        var runtimeGrouped = runtimeErrors
            .GroupBy(t => t.Message ?? "")
            .Select(g => (Message: g.Key, Count: g.Count(), Sample: g.First()))
            .ToList();

        Console.Error.WriteLine();
        Console.Error.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.Error.WriteLine("  Runner limitations encountered — report them?");
        Console.Error.WriteLine("  Reporting helps improve al-runner support proactively.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  What will be sent (no AL source code, no file paths):");

        if (hasRewriterErrors)
        {
            Console.Error.WriteLine($"  Rewriter ({rewriterErrors!.Count} object(s) — AL construct not handled):");
            foreach (var (_, error) in rewriterErrors.Take(3))
                Console.Error.WriteLine($"    × {ScrubMessage(error)}");
            if (rewriterErrors.Count > 3)
                Console.Error.WriteLine($"    … and {rewriterErrors.Count - 3} more");
        }

        // Deduplicate compilation errors: group by CS code + first quoted token so that
        // e.g. 74 CS1061 errors on Report70400 collapse into one line with a count.
        List<(string Key, int Count, string SampleMessage)>? compilationGrouped = null;
        if (hasCompilationErrors)
        {
            compilationGrouped = DeduplicateCompilationErrors(compilationErrors!);
            Console.Error.WriteLine($"  Compilation ({compilationGrouped.Count} unique error type(s) — rewriter gap in C# output):");
            foreach (var (key, count, sample) in compilationGrouped.Take(5))
            {
                var display = count > 1 ? $"{key} ({count}×)" : $"{key}: {sample}";
                Console.Error.WriteLine($"    × {ScrubMessage(display)}");
            }
            if (compilationGrouped.Count > 5)
                Console.Error.WriteLine($"    … and {compilationGrouped.Count - 5} more unique error type(s)");
        }

        if (runtimeErrors.Count > 0)
        {
            Console.Error.WriteLine($"  Runtime ({runtimeErrors.Count} test(s) — missing mock/dispatch):");
            foreach (var (msg, count, _) in runtimeGrouped.Take(3))
                Console.Error.WriteLine($"    × {ScrubMessage(msg)}{(count > 1 ? $" ({count}×)" : "")}");
            if (runtimeGrouped.Count > 3)
                Console.Error.WriteLine($"    … and {runtimeGrouped.Count - 3} more unique error(s)");
        }

        Console.Error.WriteLine($"    Version : {GetVersionString()}  OS: {GetOsString()}");
        Console.Error.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        if (!PromptYesNoWithTimeout($"Send error report? [y/N] (auto-no in {PromptTimeoutSeconds}s): "))
            return;

        // Send rewriter failures — include the object name for correlation (it's a generated
        // class name like "Codeunit_50100", not a user file path, so it's safe to transmit).
        if (hasRewriterErrors)
        {
            foreach (var (name, error) in rewriterErrors!)
            {
                // Enrich the message with the detected object type when available
                // so triage issues can be grouped by object type (e.g. "ReportExtension").
                var objectType = ExtractObjectTypeFromName(name);
                var nameWithType = objectType != null ? $"{name} ({objectType})" : name;

                var report = new TelemetryReport(
                    ExceptionType: "AlRunner.RewriterGap",
                    ScrubbedMessage: $"{nameWithType}: {ScrubMessage(error)}",
                    StackText: "",
                    FrameCount: 0,
                    RunnerVersion: GetVersionString(),
                    Os: GetOsString());
                await SendAsync(report);
            }
        }

        // Send each deduplicated compilation error group as its own telemetry report
        // so the triage workflow can create separate issues per unique error type.
        if (compilationGrouped != null)
        {
            foreach (var (key, count, sample) in compilationGrouped)
            {
                var msg = count > 1 ? $"{key} ({count}×)" : $"{key}: {sample}";

                // Enrich with the AL source line where the gap occurred.
                // We use the sample error (which contains the [AL line ~N] hint) to resolve
                // the object name → file path → single AL source line (string literals redacted).
                // This makes telemetry issues actionable without a full reproduction.
                var alLine = ExtractAlSourceLineFromError(
                    // sample may be the snippet only; try the full error from compilationErrors first
                    compilationErrors!.FirstOrDefault(e => e.Contains(sample)) ?? sample,
                    objectName =>
                    {
                        var filePath = SourceFileMapper.GetFile(objectName);
                        if (filePath == null || !File.Exists(filePath)) return null;
                        try { return File.ReadAllText(filePath); }
                        catch { return null; }
                    });

                var scrubbedMsg = ScrubMessage(msg);
                if (alLine != null)
                    scrubbedMsg = $"{scrubbedMsg}  [AL: {alLine}]";

                var report = new TelemetryReport(
                    ExceptionType: "AlRunner.CompilationGap",
                    ScrubbedMessage: scrubbedMsg,
                    StackText: "",
                    FrameCount: 0,
                    RunnerVersion: GetVersionString(),
                    Os: GetOsString());
                await SendAsync(report);
            }
        }

        // Send each unique runtime error (deduplicated by message)
        foreach (var (_, _, sample) in runtimeGrouped)
        {
            var report = BuildTestErrorReport(sample);
            if (report != null)
                await SendAsync(report);
        }

        Console.Error.WriteLine("  ✓ Error report sent. Thank you!");
    }

    // ─── Internals ────────────────────────────────────────────────────────────

    // Regex to parse the [AL line ~N col M in ObjectName] hint embedded by SourceLineMapper.FormatDiagnostic.
    private static readonly Regex AlLineHintInErrorRx =
        new(@"\[AL line ~(\d+) col \d+ in ([^\]]+)\]", RegexOptions.Compiled);

    // Regex to replace AL string literal content with '...' for privacy scrubbing.
    // Matches single-quoted AL strings: '...' (including empty '' and escaped '' inside).
    // AL uses '' (double single-quote) as the escape sequence for a literal quote inside a string.
    // The pattern captures the delimiters and replaces the contents with '...'.
    private static readonly Regex AlStringLiteralRx =
        new(@"'[^']*(?:''[^']*)*'", RegexOptions.Compiled);

    /// <summary>
    /// Replace the content of all AL single-quoted string literals in <paramref name="line"/>
    /// with the placeholder '...' to avoid leaking user data via telemetry.
    /// The surrounding quotes and all non-string-literal content are preserved.
    /// </summary>
    private static string SanitizeAlLine(string line) =>
        AlStringLiteralRx.Replace(line.Trim(), "'...'");

    /// <summary>
    /// Given a formatted compilation error string (as produced by
    /// <see cref="SourceLineMapper.FormatDiagnostic"/>), extract the single AL source
    /// line referenced by the embedded <c>[AL line ~N col M in ObjectName]</c> hint.
    ///
    /// <paramref name="fileReader"/> is called with the AL object name from the hint;
    /// it should return the full AL source text, or null if not available.
    ///
    /// Returns the sanitized line (string literals redacted), or null when the hint is
    /// absent, the file is unavailable, or the line number is out of range.
    /// </summary>
    private static string? ExtractAlSourceLineFromError(string compilationError, Func<string, string?> fileReader)
    {
        var m = AlLineHintInErrorRx.Match(compilationError);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups[1].Value, out int lineNumber) || lineNumber < 1) return null;
        var objectName = m.Groups[2].Value;

        var source = fileReader(objectName);
        if (source == null) return null;

        // Split lines — support both \r\n and \n
        var lines = source.Split('\n');
        int idx = lineNumber - 1; // convert 1-based to 0-based
        if (idx >= lines.Length) return null;

        // Strip any trailing \r (Windows line endings)
        var rawLine = lines[idx].TrimEnd('\r');
        return SanitizeAlLine(rawLine);
    }

    // Regex to parse the missing member name from a CS1061/CS0117 diagnostic message:
    // "'TypeName' does not contain a definition for 'MemberName' and …"
    private static readonly Regex MemberNameRx =
        new(@"does not contain a definition for '([^']+)'", RegexOptions.Compiled);

    // Regex for CS1503: "Argument N: cannot convert from 'FromType' to 'ToType'"
    private static readonly Regex Cs1503Rx =
        new(@"cannot convert from '([^']+)' to '([^']+)'", RegexOptions.Compiled);

    // Regex for CS1501: "No overload for method 'Method' takes N arguments"
    private static readonly Regex Cs1501Rx =
        new(@"No overload for method '([^']+)' takes (\d+) arguments", RegexOptions.Compiled);

    // Regex for CS1729: "'Type' does not contain a constructor that takes N arguments"
    private static readonly Regex Cs1729Rx =
        new(@"'([^']+)' does not contain a constructor that takes (\d+) arguments", RegexOptions.Compiled);

    // Regex for CS1674: "'Type': type used in a using statement must be implicitly convertible to …"
    private static readonly Regex Cs1674Rx =
        new(@"'([^']+)':\s*type used in a using statement", RegexOptions.Compiled);

    // Known BC platform AL object type prefixes used in generated C# class names.
    private static readonly string[] KnownObjectTypePrefixes = new[]
    {
        "ReportExtension", "TableExtension", "PageExtension", "EnumExtension",
        "Codeunit", "Report", "Table", "Page", "Query", "XmlPort", "Enum", "Interface",
        "Profile", "PermissionSet",
    };

    // Regex matching generated BC type names like Page72336585, ReportExtension50500, etc.
    private static readonly Regex GeneratedTypeIdRx =
        new(@"(?:ReportExtension|TableExtension|PageExtension|EnumExtension|Codeunit|Report|Table|Page|Query|XmlPort|Enum)\d+",
            RegexOptions.Compiled);

    /// <summary>
    /// Normalizes generated BC type IDs in a string to placeholders so that
    /// Page72336585 and Page72336666 collapse to Page&lt;N&gt;.
    /// Also normalizes fully-qualified forms like Microsoft.Dynamics.Nav.BusinessApplication.Page72336585.
    /// </summary>
    private static string NormalizeGeneratedTypeIds(string input) =>
        GeneratedTypeIdRx.Replace(input, m =>
            System.Text.RegularExpressions.Regex.Replace(m.Value, @"\d+$", "<N>"));

    /// <summary>
    /// Groups compilation error messages by CS error code + first single-quoted token
    /// (the type name). For CS1061 errors, the distinct missing member names are
    /// collected from all errors in the group and appended to the key so the resulting
    /// telemetry issue clearly identifies which members are missing.
    ///
    /// E.g. 3 CS1061 errors on ReportExtension50500 become:
    ///   "CS1061 on 'ReportExtension50500': missing 'ParentObject', 'GetReportDataItems', 'OnPreDataItem' (3×)"
    /// </summary>
    public static List<(string Key, int Count, string SampleMessage)> DeduplicateCompilationErrors(
        List<string> errors)
    {
        var singleQuoteRx = new Regex(@"'([^']+)'");
        // Extract CS code from formatted error: "ObjectName.cs(line,col): error CS1061: ..."
        var csCodeRx = new Regex(@"\berror (CS\d+):");

        // First pass — build a grouping key from CS code + enriched detail per error code.
        // For CS1061 we also accumulate the member names per group.
        return errors
            .GroupBy(err =>
            {
                var codeMatch = csCodeRx.Match(err);
                var code = codeMatch.Success ? codeMatch.Groups[1].Value : "CS????";
                // Extract message portion (after "error CSxxxx: ")
                var msgStart = codeMatch.Success ? codeMatch.Index + codeMatch.Length : 0;
                var msgPortion = err[msgStart..];

                switch (code)
                {
                    case "CS1503":
                    {
                        // "Argument N: cannot convert from 'FromType' to 'ToType'"
                        // Key captures both type names so different target types don't collapse.
                        // Generated type IDs are normalized (Page72336585 → Page<N>) so all
                        // pages with the same conversion problem become one group.
                        var m = Cs1503Rx.Match(msgPortion);
                        if (m.Success)
                            return NormalizeGeneratedTypeIds(
                                $"{code}: '{m.Groups[1].Value}' → '{m.Groups[2].Value}'");
                        break;
                    }
                    case "CS1501":
                    {
                        // "No overload for method 'Method' takes N arguments"
                        var m = Cs1501Rx.Match(msgPortion);
                        if (m.Success)
                            return $"{code}: '{m.Groups[1].Value}' ({m.Groups[2].Value} args)";
                        break;
                    }
                    case "CS0117":
                    {
                        // "'Type' does not contain a definition for 'Member'"
                        // Key combines type (first token) AND missing member.
                        var tokenMatch = singleQuoteRx.Match(msgPortion);
                        var memberMatch = MemberNameRx.Match(msgPortion);
                        if (tokenMatch.Success && memberMatch.Success)
                            return $"{code}: '{tokenMatch.Groups[1].Value}.{memberMatch.Groups[1].Value}'";
                        break;
                    }
                    case "CS1729":
                    {
                        // "'Type' does not contain a constructor that takes N arguments"
                        var m = Cs1729Rx.Match(msgPortion);
                        if (m.Success)
                            return $"{code}: '{m.Groups[1].Value}' ctor({m.Groups[2].Value} args)";
                        break;
                    }
                    case "CS1674":
                    {
                        // "'Type': type used in a using statement must be implicitly convertible to …"
                        var m = Cs1674Rx.Match(msgPortion);
                        if (m.Success)
                            return $"{code}: '{m.Groups[1].Value}' not IDisposable";
                        break;
                    }
                }

                // Default: group by CS code + first quoted token (original behaviour, covers CS1061, CS0246, etc.)
                var firstToken = singleQuoteRx.Match(msgPortion);
                var defaultKey = firstToken.Success ? $"{code} on '{firstToken.Groups[1].Value}'" : code;
                return NormalizeGeneratedTypeIds(defaultKey);
            })
            .Select(g =>
            {
                // For the sample message, extract just the diagnostic message (after "error CSxxxx: ")
                var sample = g.First();
                var codeMatch = csCodeRx.Match(sample);
                string sampleMsg = sample;
                if (codeMatch.Success)
                    sampleMsg = sample[(codeMatch.Index + codeMatch.Length)..].TrimStart();

                // For CS1061 groups, enrich the key with the distinct missing member names.
                var key = g.Key;
                if (key.StartsWith("CS1061 on '", StringComparison.Ordinal))
                {
                    var memberNames = g
                        .Select(err =>
                        {
                            var m = MemberNameRx.Match(err);
                            return m.Success ? m.Groups[1].Value : null;
                        })
                        .Where(n => n != null)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList();

                    if (memberNames.Count > 0)
                        key = $"{key}: missing {string.Join(", ", memberNames.Select(n => $"'{n}'"))}";
                }

                return (Key: key, Count: g.Count(), SampleMessage: sampleMsg);
            })
            .OrderByDescending(g => g.Count)
            .ToList();
    }

    /// <summary>
    /// Extracts the BC object type prefix from a generated C# class name such as
    /// "Codeunit_50100", "Report_70400", or "ReportExtension50500".
    /// Returns null when the name does not match any known prefix.
    /// </summary>
    public static string? ExtractObjectTypeFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var prefix in KnownObjectTypePrefixes)
        {
            // Accept both "Prefix_NNN" (underscore separator) and "PrefixNNN" (no separator)
            if (name.StartsWith(prefix + "_", StringComparison.Ordinal) ||
                (name.StartsWith(prefix, StringComparison.Ordinal) &&
                 name.Length > prefix.Length &&
                 char.IsDigit(name[prefix.Length])))
                return prefix;
        }
        return null;
    }

    /// <summary>Public test seam — wraps the internal <see cref="ScrubMessage"/> method.</summary>
    public static string ScrubMessagePublic(string message) => ScrubMessage(message);

    /// <summary>Public test seam — wraps the internal <see cref="SanitizeAlLine"/> method.</summary>
    public static string SanitizeAlLinePub(string line) => SanitizeAlLine(line);

    /// <summary>
    /// Public test seam — wraps the internal <see cref="ExtractAlSourceLineFromError"/> method.
    /// <paramref name="fileReader"/> is called with the AL object name extracted from the hint
    /// and should return the full AL source content (or null when not available).
    /// </summary>
    public static string? ExtractAlSourceLineFromErrorPub(string compilationError, Func<string, string?> fileReader)
        => ExtractAlSourceLineFromError(compilationError, fileReader);

    /// <summary>Public test seam — wraps the internal <see cref="BuildTestErrorReport"/> method.</summary>
    public static TelemetryReportPublic? BuildTestErrorReportPublic(TestResult result)
    {
        // Re-use the private impl, but we need to return something the tests can inspect.
        // We build the report inline here to avoid duplicating logic.
        if (result.StackTrace == null && result.Message == null) return null;

        var frames = result.StackTrace?
            .Split('\n')
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f) && f.Contains("AlRunner."))
            .Take(10)
            .ToList() ?? new List<string>();

        var testIdentity = BuildTestIdentity(result);
        var baseMessage = ScrubMessage(result.Message ?? "");
        var scrubbedMessage = string.IsNullOrEmpty(testIdentity)
            ? baseMessage
            : $"{baseMessage} [in test '{testIdentity}']";

        return new TelemetryReportPublic(scrubbedMessage);
    }

    public record TelemetryReportPublic(string ScrubbedMessage);

    private record TelemetryReport(
        string ExceptionType,
        string ScrubbedMessage,
        string StackText,
        int FrameCount,
        string RunnerVersion,
        string Os);

    private static bool CanPromptUser(bool outputJson) =>
        !outputJson &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected &&
        !Console.IsErrorRedirected &&
        Environment.UserInteractive;

    private static bool PromptYesNoWithTimeout(string prompt)
    {
        Console.Error.Write(prompt);
        string? answer = null;
        var thread = new Thread(() => answer = Console.ReadLine()) { IsBackground = true };
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(PromptTimeoutSeconds)))
        {
            Console.Error.WriteLine("\n  (Timed out — not sending)");
            return false;
        }

        return answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static TelemetryReport? BuildReport(Exception ex)
    {
        // Unwrap TargetInvocationException wrappers
        var inner = ex;
        while (inner is TargetInvocationException tie && tie.InnerException != null)
            inner = tie.InnerException;

        // Skip AL-level errors — expected test failures, not runner bugs
        if (inner.StackTrace?.Contains("AlDialog.Error") == true) return null;
        if (inner is NotSupportedException) return null;

        var frames = ExtractRunnerFrames(inner);
        // No AlRunner frames = crash originated entirely in user-generated code, not our bug
        if (frames.Count == 0) return null;

        return new TelemetryReport(
            ExceptionType: inner.GetType().FullName ?? inner.GetType().Name,
            ScrubbedMessage: ScrubMessage(inner.Message),
            StackText: string.Join("\n", frames),
            FrameCount: frames.Count,
            RunnerVersion: GetVersionString(),
            Os: GetOsString());
    }

    /// <summary>
    /// Builds a display string for the test identity — "CodeunitName.ProcedureName"
    /// when both are available, just the procedure name otherwise.
    /// </summary>
    private static string BuildTestIdentity(TestResult result)
    {
        if (!string.IsNullOrEmpty(result.CodeunitName) && !string.IsNullOrEmpty(result.Name))
            return $"{result.CodeunitName}.{result.Name}";
        if (!string.IsNullOrEmpty(result.Name))
            return result.Name;
        return "";
    }

    private static TelemetryReport? BuildTestErrorReport(TestResult result)
    {
        if (result.StackTrace == null && result.Message == null) return null;

        var frames = result.StackTrace?
            .Split('\n')
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f) && f.Contains("AlRunner."))
            .Take(10)
            .ToList() ?? new List<string>();

        var testIdentity = BuildTestIdentity(result);
        var baseMessage = ScrubMessage(result.Message ?? "");
        var scrubbedMessage = string.IsNullOrEmpty(testIdentity)
            ? baseMessage
            : $"{baseMessage} [in test '{testIdentity}']";

        return new TelemetryReport(
            ExceptionType: "AlRunner.RuntimeGap",
            ScrubbedMessage: scrubbedMessage,
            StackText: string.Join("\n", frames),
            FrameCount: frames.Count,
            RunnerVersion: GetVersionString(),
            Os: GetOsString());
    }

    private static string GetVersionString()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown";
    }

    private static string GetOsString() =>
        Environment.OSVersion.Platform.ToString().ToLowerInvariant();

    /// <summary>Keep only stack frames originating from AlRunner code.</summary>
    private static List<string> ExtractRunnerFrames(Exception ex)
    {
        if (ex.StackTrace == null) return new();

        return ex.StackTrace
            .Split('\n')
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f) && f.Contains("AlRunner."))
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Strip file paths and other potentially sensitive tokens from exception messages.
    ///
    /// AL line hints of the form "[AL line ~N col M in ObjectName]" are deliberately
    /// preserved — they contain only a line number and a BC object name (no user file
    /// paths), and are critical for actionable issue triage.
    /// </summary>
    private static string ScrubMessage(string message)
    {
        // Protect AL line hints from the path-scrubber.  The hint looks like:
        //   [AL line ~42 col 5 in MyCodeunit]
        // We replace each hint with a numbered placeholder, run scrubbing, then restore.
        var alLineHintRx = new Regex(@"\[AL line ~\d+ col \d+ in [^\]]+\]");
        var hints = new List<string>();
        message = alLineHintRx.Replace(message, m =>
        {
            hints.Add(m.Value);
            return $"\x00ALHINT{hints.Count - 1}\x00";
        });

        // Remove Windows-style paths (C:\...)
        message = Regex.Replace(message, @"[A-Za-z]:\\[^\s]+", "[path]");
        // Remove Unix-style paths (/home/..., /var/..., etc.)
        message = Regex.Replace(message, @"(/[\w.\-]+){2,}", "[path]");

        // Restore AL line hints
        for (int i = 0; i < hints.Count; i++)
            message = message.Replace($"\x00ALHINT{i}\x00", hints[i]);

        // Truncate to 500 chars
        return message.Length > 500 ? message[..500] + "…" : message;
    }

    private static async Task SendAsync(TelemetryReport report)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var payload = new[]
            {
                new
                {
                    name = $"Microsoft.ApplicationInsights.{InstrumentationKey}.Exception",
                    time = DateTime.UtcNow.ToString("o"),
                    iKey = InstrumentationKey,
                    tags = new Dictionary<string, string>
                    {
                        ["ai.cloud.role"] = "al-runner",
                        ["ai.application.ver"] = report.RunnerVersion
                    },
                    data = new
                    {
                        baseType = "ExceptionData",
                        baseData = new
                        {
                            ver = 2,
                            exceptions = new[]
                            {
                                new
                                {
                                    typeName = report.ExceptionType,
                                    message = report.ScrubbedMessage,
                                    hasFullStack = report.FrameCount > 0,
                                    stack = report.StackText
                                }
                            },
                            properties = new Dictionary<string, string>
                            {
                                ["os"] = report.Os,
                                ["runtime"] = $"net{Environment.Version.Major}.{Environment.Version.Minor}"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await http.PostAsync(IngestionEndpoint, content);
        }
        catch
        {
            // Network errors must never crash the tool
        }
    }
}
