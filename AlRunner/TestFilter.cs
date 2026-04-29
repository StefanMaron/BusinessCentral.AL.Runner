namespace AlRunner;

/// <summary>
/// Filter passed to <see cref="Executor.RunTests"/> to limit which tests execute.
/// Both fields are optional; nulls = no constraint.
/// When both are set, a test must match both filters (AND).
/// </summary>
public record TestFilter(
    IReadOnlySet<string>? CodeunitNames,
    IReadOnlySet<string>? ProcNames
);
