namespace AlRunner.Infrastructure;

/// <summary>
/// `--test PATTERN` that selects no test in the whole invocation fails with exit 6 (#4055).
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
}
