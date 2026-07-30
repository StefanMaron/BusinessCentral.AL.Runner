// DependencyLoadException — loud failure when a source dependency cannot be compiled.
//
// Thrown by DependencyLoader.LoadOne when a dependency that HAS AL source (tier 3)
// fails to build. This is distinct from RunnerOutOfScopeException (which covers
// out-of-scope BC surfaces at runtime). Symbol-only packages (no src/*.al) are
// NOT an error — they rely on service-tier DLLs loaded elsewhere.
//
// See: .claude/rules/loud-failures.md — the no-silent-skip rule.

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown when a dependency app's AL source was found but the compile pipeline
/// failed (EMIT-FAIL, EMIT-ZERO, COMPILE-FAIL, or Assembly.Load failure).
/// Surfaces app identity + failure stage + full diagnostic detail so the
/// developer sees exactly which dependency broke and why.
/// </summary>
public sealed class DependencyLoadException : Exception
{
    public string Publisher { get; }
    public string AppName { get; }
    public string Version { get; }
    public string Stage { get; }   // "EMIT-FAIL" | "EMIT-ZERO" | "COMPILE-FAIL" | "LOAD-FAIL"

    public DependencyLoadException(
        string publisher, string appName, string version,
        string stage, string detail, Exception? inner = null)
        : base(BuildMessage(publisher, appName, version, stage, detail), inner)
    {
        Publisher = publisher;
        AppName = appName;
        Version = version;
        Stage = stage;
    }

    private static string BuildMessage(
        string publisher, string appName, string version,
        string stage, string detail)
        => $"[dep-load-fail] {publisher}_{appName} v{version}: {stage} — {detail}";

    /// <summary>
    /// Flatten an exception's messages into a single compact string.
    /// For AggregateException, joins all inner-exception messages.
    /// </summary>
    public static string FlattenException(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            var inner = agg.Flatten().InnerExceptions;
            return string.Join(" | ", inner.Select(e => $"{e.GetType().Name}: {e.Message}"));
        }
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
