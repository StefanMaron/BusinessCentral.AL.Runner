// RunnerOutOfScopeException — loud failure when AL code reaches a surface the
// runner cannot faithfully support.
//
// See:
//   .claude/rules/loud-failures.md — the rule.
//   docs/scope.md                  — the manifest. Anchors land developers in the right row.
//
// Plain System.Exception, NOT derived from any BC exception type, for two reasons that
// hold: it is unmistakable in test output (no BC error path produces it), and it carries
// typed Api/Reason fields that tests/expectations/ matches on — something a
// MissingFieldException or an InvalidOperationException cannot offer.
//
// It is NOT uncatchable from AL, and an earlier version of this comment claimed it was.
// AL `asserterror` DOES catch it. The runner's asserterror replacement —
// BcRuntime.NavMethodScope_AssertError in AlRunner/Patches/MethodScopePatches.cs, bound
// over NavMethodScope::AssertError/1 in NclCecilRewrite.Runtime.cs — is an unfiltered
// `catch (Exception)`, so a refusal raised inside an `asserterror` block makes that
// asserterror PASS. Runner-extras suites rely on this deliberately:
// tests/runner-extras/table-connection-live-oos and tests/runner-extras/
// date-virtual-table-window both do `asserterror <oos surface>` followed by
// `Assert.ExpectedError('out-of-scope: ...')`. Do not write a new refusal on the
// assumption that AL cannot trap it.
//
// An AL [TryFunction] is the deliberate exception to that, and the asymmetry is the point:
// BcRuntime.NavApplicationObjectBase_TryInvoke traps a PERMANENTLY out-of-scope refusal
// into `false` (matching a real BC environment that also lacks the surface) but lets a
// "not-yet-implemented" one tear through, so a runner gap can never read as a green test.
// See AlRunner.Tests/TryFunctionOutOfScopeTrapTests.cs.
//
// Whether AL should be able to swallow a refusal at all is a live design question — see
// .claude/rules/loud-failures.md and issue #2871 — and a maintainer decision, not something
// to change from a patch site. AlRunner.Tests/AssertErrorOutOfScopeCatchabilityTests.cs pins
// today's answer so a change to it is visible rather than silent.

using System;

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown by runner patches when AL code reaches a surface that is either:
///   (a) permanently out of scope (e.g. SMTP) → reason cites §3.x of scope.md, or
///   (b) in scope but not yet implemented    → reason = "not-yet-implemented".
/// Distinct from any BC runtime exception so the failure is unmistakable in
/// test output, and carrying typed <see cref="Api"/> / <see cref="Reason"/>
/// fields that <c>tests/expectations/</c> matches on. It is NOT uncatchable
/// via AL <c>asserterror</c> — see this file's header for what actually
/// happens, and why.
/// </summary>
public sealed class RunnerOutOfScopeException : Exception
{
    public string Api { get; }
    public string Reason { get; }
    public string? DocAnchor { get; }

    // Reason is normalised on BOTH paths — the message and the property — so a manifest that
    // matched on the property and a developer reading the message can never see different text.
    public RunnerOutOfScopeException(string api, string reason, string? docAnchor = null)
        : base(BuildMessage(api, TrimTrailingDocPointer(reason), docAnchor))
    {
        Api = api;
        Reason = TrimTrailingDocPointer(reason);
        DocAnchor = docAnchor;
    }

    /// <summary>
    /// Drop a "See docs/scope.md" the throw site wrote at the END of its own reason text.
    ///
    /// <para><see cref="BuildMessage"/> always appends the canonical " — see docs/scope.md…"
    /// link, so a reason that ends with its own copy renders as
    /// "… See docs/scope.md — see docs/scope.md". 47 throw sites across 13 files did exactly
    /// that (#2931); normalising here fixes all of them and every future one, instead of
    /// editing 47 strings and leaving the trap in place for the next author.</para>
    ///
    /// <para>Only a TRAILING pointer is removed, and only the bare-file form: a reason that
    /// names a specific anchor (".../scope.md#email") is carrying information the appended
    /// link does not necessarily repeat, and a pointer in the MIDDLE of a sentence is prose.
    /// <c>Reason</c> keeps its anchor either way — <c>ExpectationManifest.ReasonAnchor</c>
    /// reads the text BEFORE the first em-dash separator, which this never touches.</para>
    ///
    /// <para>"Trailing" tolerates the punctuation an author wraps the pointer in — a closing
    /// bracket, a sentence period, or both: "(see docs/scope.md)", "[see docs/scope.md]",
    /// "(see docs/scope.md).". #3073 added those. No throw site writes one today, so this is
    /// closing the hole rather than fixing a live doubling: a form passed through silently
    /// would contradict the paragraph above, which is what makes it worth handling at all.
    /// <c>OutOfScopePointerCallSiteGuardTests</c> enforces the other half — that no call site
    /// defeats this method — by reading the compiled IL.</para>
    /// </summary>
    internal static string TrimTrailingDocPointer(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return reason;
        // Peel a trailing sentence period and/or closing bracket before looking for the
        // pointer. Safe to over-peel here: the pointer itself ends in a letter, so if what is
        // underneath is not the pointer we return `reason` UNCHANGED below, never the peeled
        // text — that early return is what keeps a reason like "… Report 'X' (5)" intact.
        var trimmed = reason.TrimEnd().TrimEnd(' ', '\t', '.', ')', ']');
        const string Pointer = "docs/scope.md";
        if (!trimmed.EndsWith(Pointer, StringComparison.OrdinalIgnoreCase)) return reason;

        var head = trimmed[..^Pointer.Length].TrimEnd();
        // "See" / "see" is the only lead-in in use; anything else is prose that happens to end
        // in the file name and is left alone rather than silently truncated.
        if (!head.EndsWith("see", StringComparison.OrdinalIgnoreCase)) return reason;
        // The opening bracket of a "(see …)" wrapper is dropped with the same pass that drops
        // the lead-in punctuation, so the sentence in front of it is all that is left.
        return head[..^3].TrimEnd().TrimEnd('.', ',', ';', ':', '(', '[').TrimEnd();
    }

    // Stable contract format. AL tests match with:
    //     Assert.ExpectedError('out-of-scope: <api>')
    // or just 'out-of-scope:' for any-OOS. Keep the prefix + " — " separators stable.
    private static string BuildMessage(string api, string reason, string? docAnchor)
    {
        // A docAnchor that names its own doc file is used verbatim (#2894). scope.md is the
        // manifest of what is permanently out of scope, and it is the right target for most
        // refusals — but not for every one. An IN-SCOPE surface the runner cannot answer for
        // yet is written up in docs/limitations.md, and pointing that case at scope.md sends
        // the reader to a file with no matching section AND asserts a permanence that is not
        // true. The twelve Object Metadata (2000000071) refusals in
        // RecordPatches.ObjectMetadataSystemTable.cs are the case that showed it.
        //
        // OutOfScopeMessage.TryParse strips everything from " — see " onward, so the file name
        // here is invisible to the expectations manifest and to the reporter's bucketing —
        // this is a reader-facing pointer only, and widening it changes no classification.
        const string DefaultDoc = "docs/scope.md";
        var link = docAnchor switch
        {
            null => DefaultDoc,
            var a when a.StartsWith("docs/", StringComparison.Ordinal) => a,
            var a when a.StartsWith("#", StringComparison.Ordinal) => DefaultDoc + a,
            var a => DefaultDoc + "#" + a,
        };
        return $"{OutOfScopeMessage.Prefix}{api} — {reason} — see {link}";
    }
}

/// <summary>
/// The out-of-scope signal a failing test carries, however it was raised.
/// </summary>
/// <param name="Api">BC API that was touched, e.g. <c>HttpClient.Get</c>.</param>
/// <param name="Reason">
/// Reason as written by the throw site: an anchor (a <c>docs/scope.md</c> section
/// for a permanent refusal, or <c>not-yet-implemented</c> for an in-scope one),
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
    /// A caller documenting an in-scope refusal elsewhere may pass a full
    /// <c>docs/&lt;file&gt;.md#anchor</c> instead — see <c>BuildMessage</c>.
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
