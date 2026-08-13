// RunnerOutOfScopeException — loud failure when AL code reaches a surface the
// runner cannot faithfully support.
//
// See:
//   .claude/rules/loud-failures.md — the rule.
//   docs/scope.md                  — the manifest. Anchors land developers in the right row.
//
// Plain System.Exception (NOT derived from any BC exception type) so AL
// `asserterror` cannot swallow it. The developer must see the failure.

using System;

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
        return $"{OutOfScopeMessage.Prefix}{api} — {reason} — see {link}";
    }
}

/// <summary>
/// The out-of-scope signal a failing test carries, however it was raised.
/// </summary>
/// <param name="Api">BC API that was touched, e.g. <c>HttpClient.Get</c>.</param>
/// <param name="Reason">
/// Reason as written by the throw site: a <c>docs/scope.md</c> anchor,
/// optionally followed by free-text detail after an em-dash separator.
/// Empty when the throw site did not carry one.
/// </param>
/// <param name="Typed">
/// True when the signal came from a real <see cref="RunnerOutOfScopeException"/>,
/// false when it was recovered from the message convention (Cecil-injected IL
/// cannot construct our typed exception — see #1743).
/// </param>
public readonly record struct OutOfScopeSignal(string Api, string Reason, bool Typed);

/// <summary>
/// The single parser for the out-of-scope message convention produced by
/// <see cref="RunnerOutOfScopeException"/> and by the Cecil-injected throw
/// sites in <c>NclCecilRewrite</c>:
/// <code>out-of-scope: &lt;api&gt; — &lt;reason&gt; — see docs/scope.md#&lt;anchor&gt;</code>
/// Both the reporter's failure bucketing (<c>Reporter.ClassifyTest</c>) and the
/// expectations manifest (<c>ExpectationClassifier</c>) read the convention
/// through here so there is exactly one definition of what it means (#1743).
/// </summary>
public static class OutOfScopeMessage
{
    /// <summary>Message prefix that marks a throw as out-of-scope.</summary>
    public const string Prefix = "out-of-scope: ";

    private const string Sep = " — ";

    /// <summary>
    /// Parse the convention out of a single text blob (an exception message, or
    /// a whole message+stack dump). Reads the FIRST occurrence of the prefix and
    /// stops at the end of that line.
    /// </summary>
    public static bool TryParse(string? text, out OutOfScopeSignal signal)
    {
        signal = default;
        if (string.IsNullOrEmpty(text)) return false;
        int idx = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (idx < 0) return false;

        var tail = text[(idx + Prefix.Length)..];
        int nl = tail.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) tail = tail[..nl];

        int sep = tail.IndexOf(Sep, StringComparison.Ordinal);
        if (sep < 0)
        {
            // No reason slot at all (e.g. "out-of-scope: NavReport.RunRequestPage
            // (unrecognised overload shape)"). Still an OOS signal — but with no
            // reason it can never match a manifest entry, which is correct: the
            // throw site has to name a docs/scope.md anchor first.
            signal = new OutOfScopeSignal(tail.Trim(), string.Empty, Typed: false);
            return true;
        }

        var api = tail[..sep].Trim();
        var rest = tail[(sep + Sep.Length)..];

        // Drop the trailing " — see docs/scope.md#anchor" link, keeping any
        // free-text detail the throw site appended to the reason.
        int seeIdx = rest.IndexOf(Sep + "see ", StringComparison.Ordinal);
        if (seeIdx >= 0) rest = rest[..seeIdx];

        signal = new OutOfScopeSignal(api, rest.Trim(), Typed: false);
        return true;
    }

    /// <summary>
    /// Recover the out-of-scope signal from an exception: the typed
    /// <see cref="RunnerOutOfScopeException"/> anywhere in the inner-exception
    /// chain wins; otherwise the message convention is parsed out of the chain.
    /// Returns null when the exception carries no out-of-scope signal at all —
    /// a plain <c>InvalidOperationException("boom")</c> must never be mistaken
    /// for one.
    /// </summary>
    public static OutOfScopeSignal? FromException(Exception? ex)
    {
        const int MaxDepth = 16;   // guard against self-referential inner chains

        // Typed first: an explicit RunnerOutOfScopeException outranks any message
        // text further up the chain.
        var e = ex;
        for (int d = 0; e != null && d < MaxDepth; d++, e = e.InnerException)
            if (e is RunnerOutOfScopeException oos)
                return new OutOfScopeSignal(oos.Api, oos.Reason, Typed: true);

        e = ex;
        for (int d = 0; e != null && d < MaxDepth; d++, e = e.InnerException)
            if (TryParse(e.Message, out var signal))
                return signal;

        return null;
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
