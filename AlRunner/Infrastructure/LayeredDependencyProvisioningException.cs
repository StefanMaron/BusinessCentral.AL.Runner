// LayeredDependencyProvisioningException — the composing wrapper the layered/source-dep
// pre-passes throw when the thing that failed underneath them is a provisioning gap
// (MissingDependencyException / DependencyVersionMismatchException), not a compile failure.
//
// See: .claude/rules/loud-failures.md, docs/limitations.md

namespace AlRunner.Infrastructure;

/// <summary>
/// Wraps an <see cref="IDependencyProvisioningDiagnostic"/> raised while a source pre-pass
/// was building one specific impl / source dependency, and is itself one — so a
/// <c>catch (… is IDependencyProvisioningDiagnostic)</c> in Program.cs still recognizes it
/// and still renders the detailed report, with the impl this happened for named on top.
/// </summary>
public sealed class LayeredDependencyProvisioningException : Exception, IDependencyProvisioningDiagnostic
{
    /// <summary>The pre-pass stage tag, e.g. <c>"layered"</c> or <c>"source-dep"</c>.</summary>
    public string Stage { get; }
    /// <summary>Name of the impl / source dependency being built when this surfaced.</summary>
    public string ImplName { get; }
    /// <summary>Source directory of that impl.</summary>
    public string ImplPath { get; }
    /// <summary>The provisioning diagnostic this composes over.</summary>
    public IDependencyProvisioningDiagnostic Diagnostic { get; }

    public LayeredDependencyProvisioningException(
        string stage, string implName, string implPath, Exception inner)
        : base($"[{stage}] Failed to emit symbols for impl '{implName}' from {implPath}: {inner.Message}", inner)
    {
        Stage = stage;
        ImplName = implName;
        ImplPath = implPath;
        Diagnostic = (IDependencyProvisioningDiagnostic)inner;
    }

    /// <summary>
    /// AUDIT (loud-failures.md): observably equivalent to the inner diagnostic's own report,
    /// plus two lines naming which impl was being built. The inner text is reproduced verbatim
    /// — nothing is summarized, reworded or dropped — so the reader gets the same missing app,
    /// searched dirs, dependency chain and fix commands #2095 built, and additionally learns
    /// which of several impls in the invocation asked for it. Rationale: PR body for #2956.
    /// </summary>
    public string ToDetailedMessage(string? bcVersion = null)
        => string.Join(Environment.NewLine, new[]
        {
            Diagnostic.ToDetailedMessage(bcVersion),
            "",
            $"  Reached while building source dependency '{ImplName}' ({Stage} pre-pass).",
            $"  Its source: {ImplPath}",
        });
}
