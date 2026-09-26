// RunnerFailureLines — reads the runner's failure entries out of its console output (#4566).
//
// A failure heading has two shapes, and both carry the codeunit id and the method:
//   FAIL  "Probe Customer Test".CustomerNameFails (Codeunit50150, 194 ms)
//   FAIL  Codeunit50150.CustomerNameFails (194 ms)          (no display name resolved)
// A test that asserts "this test did not fail" must match both, or its negative is vacuous.
namespace AlRunner.Tests;

internal static class RunnerFailureLines
{
    /// <summary>Every FAIL / ERROR heading line in <paramref name="output"/>.</summary>
    internal static IReadOnlyList<string> All(string output) =>
        output.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.StartsWith("FAIL  ", StringComparison.Ordinal)
                     || l.StartsWith("ERROR ", StringComparison.Ordinal))
            .ToList();

    /// <summary>The heading lines for one test, in either shape.</summary>
    internal static IReadOnlyList<string> For(string output, int codeunitId, string method) =>
        All(output).Where(l => Names(l, codeunitId, method)).ToList();

    /// <summary>Whether that test has a FAIL / ERROR entry.</summary>
    internal static bool Failed(string output, int codeunitId, string method) =>
        For(output, codeunitId, method).Count > 0;

    private static bool Names(string heading, int codeunitId, string method) =>
        heading.Contains($"Codeunit{codeunitId}.{method} (", StringComparison.Ordinal)
        || (heading.Contains($".{method} (Codeunit{codeunitId},", StringComparison.Ordinal));
}
