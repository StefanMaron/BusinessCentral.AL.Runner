// Ported from protocol-v2 (v1 architecture, PR #1607) as part of #1641 — a plain
// record with no architecture dependency.
namespace AlRunner;

/// <summary>
/// Structured filter for a <c>runTests</c> request, distinct from
/// <see cref="TestExecutor.TestFilter"/> (a single substring filter). Both fields
/// are optional; nulls = no constraint. When both are set, a test must match both
/// filters (AND). Codeunit/proc name matching is case-insensitive exact match on
/// the AL name (not a substring).
/// </summary>
public record TestFilter(
    IReadOnlySet<string>? CodeunitNames,
    IReadOnlySet<string>? ProcNames
);
