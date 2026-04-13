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

    // ─── Internals ────────────────────────────────────────────────────────────

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

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown";

        return new TelemetryReport(
            ExceptionType: inner.GetType().FullName ?? inner.GetType().Name,
            ScrubbedMessage: ScrubMessage(inner.Message),
            StackText: string.Join("\n", frames),
            FrameCount: frames.Count,
            RunnerVersion: versionStr,
            Os: Environment.OSVersion.Platform.ToString().ToLowerInvariant());
    }

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
    /// </summary>
    private static string ScrubMessage(string message)
    {
        // Remove Windows-style paths (C:\...)
        message = Regex.Replace(message, @"[A-Za-z]:\\[^\s]+", "[path]");
        // Remove Unix-style paths (/home/..., /var/..., etc.)
        message = Regex.Replace(message, @"(/[\w.\-]+){2,}", "[path]");
        // Truncate to 200 chars
        return message.Length > 200 ? message[..200] + "…" : message;
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
