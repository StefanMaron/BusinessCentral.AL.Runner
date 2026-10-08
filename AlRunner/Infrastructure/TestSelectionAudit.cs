namespace AlRunner.Infrastructure;

/// <summary>
/// `--test PATTERN` / `--test-exact NAME` that selects no test to run in the whole invocation fails with
/// exit 6 (#4055), and so does an --exclude-test that removes every selected test (#5439).
/// The zero is judged once per invocation: under --jobs each worker reports its own count and
/// the parent sums them, because a pattern matching in one shard and not another is legitimate.
/// </summary>
internal static class TestSelectionAudit
{
    public const int ExitCode = 6;

    /// <summary>Set by ParallelFanOut on every worker: report the count, do not judge it.</summary>
    public const string WorkerEnvVar = "AL_RUNNER_TEST_SELECTION_WORKER";

    // Untagged on purpose: Log's FilteredWriter drops "[Component]" lines at default verbosity,
    // and the parent parses this line out of the worker's captured output.
    public const string WorkerLinePrefix = "test-selection: selected ";

    public static bool IsWorker =>
        Environment.GetEnvironmentVariable(WorkerEnvVar) == "1";

    public static string FormatWorkerLine(long selected) => $"{WorkerLinePrefix}{selected}";

    /// <summary>
    /// Sums every worker line in <paramref name="output"/>. Null when no line is present, so a
    /// worker that died before reporting reads as "could not measure", never as zero.
    /// </summary>
    public static long? ReadWorkerLines(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        long? sum = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(WorkerLinePrefix, StringComparison.Ordinal)) continue;
            if (long.TryParse(line[WorkerLinePrefix.Length..], out var n))
                sum = (sum ?? 0) + n;
        }
        return sum;
    }

    /// <summary>The diagnostic for a pattern that selected nothing.</summary>
    public static string Describe(string pattern)
    {
        var msg = $"--test '{pattern}' selected no test in this run. A pattern that matches nothing "
            + "is reported as a failure (exit 6), not as a clean run of 0 tests. PATTERN is a "
            + "case-insensitive substring of CodeunitNNNN.Method (the CLR codeunit name carries the "
            + "object id, not the AL name).";
        var trimmed = pattern.Trim().TrimStart('*').TrimEnd('*');
        if (trimmed.Contains('*'))
            msg += " Only a leading or trailing '*' is stripped; an interior '*' is matched "
                + "literally and no test name contains one.";
        return msg;
    }

    /// <summary>How a diagnostic names the selection: <c>--test 'P'</c>, <c>--test-exact 'A', 'B'</c>, or both.</summary>
    public static string SelectionText(string? pattern, IReadOnlyList<string> exact)
    {
        var parts = new List<string>();
        if (pattern != null) parts.Add($"--test '{pattern}'");
        if (exact.Count > 0) parts.Add("--test-exact " + string.Join(", ", exact.Select(e => $"'{e}'")));
        return string.Join(" with ", parts);
    }

    /// <summary>
    /// The diagnostic for a selection (#5439) that left nothing to run: <paramref name="excludedAll"/> when
    /// --exclude-test removed every test it selected (known exactly, single process);
    /// <paramref name="excludesInEffect"/> when exclusions are present but the cause is not separable
    /// (the --jobs parent only sums what workers report after exclusion).
    /// </summary>
    public static string Describe(string? pattern, IReadOnlyList<string> exact, bool excludedAll, bool excludesInEffect)
    {
        if (exact.Count == 0 && !excludedAll && !excludesInEffect && pattern != null) return Describe(pattern);
        var subject = SelectionText(pattern, exact);
        if (excludedAll)
            return $"{subject} selected tests, but --exclude-test names every one of them, so nothing is left to run. "
                + "A selection that --exclude-test empties is reported as a failure (exit 6), not as a clean run of 0 tests.";
        var msg = $"{subject} selected no test in this run. A selection that leaves nothing to run is reported as a "
            + "failure (exit 6), not as a clean run of 0 tests.";
        if (exact.Count > 0)
            msg += " --test-exact takes the WHOLE qualified name CodeunitNNNN.Method, case-insensitively, never a "
                + "substring (the CLR codeunit name carries the object id, not the AL name).";
        if (pattern != null)
            msg += " --test is a case-insensitive substring of CodeunitNNNN.Method.";
        if (excludesInEffect)
            msg += " --exclude-test is also in effect: if the selection matched anything, the exclusions removed all of it.";
        return msg;
    }
}
