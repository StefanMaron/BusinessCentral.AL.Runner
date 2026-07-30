// RunnerOutOfScopeException — loud failure when AL code reaches a surface the
// runner cannot faithfully support.
//
// See:
//   .claude/rules/loud-failures.md — the rule.
//   docs/scope.md                  — the manifest. Anchors land developers in the right row.
//
// Plain System.Exception (NOT derived from any BC exception type) so AL
// `asserterror` cannot swallow it. The developer must see the failure.

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown by runner patches when AL code reaches a surface that is either:
///   (a) permanently out of scope (e.g. SMTP) → reason cites §3.x of scope.md, or
///   (b) in scope but not yet implemented    → reason = "not-yet-implemented".
/// Distinct from any BC runtime exception so the failure is unmistakable in
/// test output and uncatchable via AL `asserterror`.
/// </summary>
public sealed class RunnerOutOfScopeException : Exception
{
    public string Api { get; }
    public string Reason { get; }
    public string? DocAnchor { get; }

    public RunnerOutOfScopeException(string api, string reason, string? docAnchor = null)
        : base(BuildMessage(api, reason, docAnchor))
    {
        Api = api;
        Reason = reason;
        DocAnchor = docAnchor;
    }

    // Stable contract format. AL tests match with:
    //     Assert.ExpectedError('out-of-scope: <api>')
    // or just 'out-of-scope:' for any-OOS. Keep the prefix + " — " separators stable.
    private static string BuildMessage(string api, string reason, string? docAnchor)
    {
        var link = docAnchor != null
            ? $"docs/scope.md{(docAnchor.StartsWith("#") ? docAnchor : "#" + docAnchor)}"
            : "docs/scope.md";
        return $"out-of-scope: {api} — {reason} — see {link}";
    }
}

/// <summary>
/// Helpers for raising the loud-failure exception from hook bodies. Keep call
/// sites short and grep-able.
/// </summary>
public static class RunnerScope
{
    /// <summary>
    /// Permanently-out-of-scope API. <paramref name="docAnchor"/> is the
    /// section anchor under <c>docs/scope.md</c> (e.g. "email", "external-http").
    /// </summary>
    public static void ThrowOutOfScope(string api, string reason, string docAnchor)
        => throw new RunnerOutOfScopeException(api, reason, docAnchor);

    /// <summary>
    /// In-scope surface that's not yet implemented. <paramref name="plan"/> is
    /// a short note about where the work is tracked (e.g. "HANDOFF §6 Tier 1C").
    /// </summary>
    public static void ThrowNotYetImplemented(string api, string plan)
        => throw new RunnerOutOfScopeException(api, $"not-yet-implemented — {plan}", "todo");
}
