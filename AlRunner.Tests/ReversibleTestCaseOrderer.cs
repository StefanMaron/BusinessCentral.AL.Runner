using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCaseOrderer("AlRunner.Tests.ReversibleTestCaseOrderer", "AlRunner.Tests")]

namespace AlRunner.Tests;

/// <summary>
/// #5110: xUnit's default fact order within a class, or its exact reverse when
/// <see cref="EnvVar"/> is <c>reverse</c>. A class sharing one server across its facts
/// (<see cref="SharedCliServer"/>) can pass only because an earlier fact left state a later
/// one depends on; running the class both ways is how that shows up.
/// See docs/shared-cli-server.md#reversed-fact-order.
/// </summary>
public sealed class ReversibleTestCaseOrderer : ITestCaseOrderer
{
    public const string EnvVar = "AL_RUNNER_TEST_CASE_ORDER";

    private readonly DefaultTestCaseOrderer _default;

    public ReversibleTestCaseOrderer(IMessageSink diagnosticMessageSink)
        => _default = new DefaultTestCaseOrderer(diagnosticMessageSink);

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
        => Apply(_default.OrderTestCases(testCases).ToList(), Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Unset, empty or <c>default</c> keeps the order; <c>reverse</c> reverses it. Any other
    /// value throws, so a typo in a scheduled job cannot quietly run the default order.
    /// </summary>
    internal static List<T> Apply<T>(List<T> ordered, string? mode)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case null or "" or "default":
                return ordered;
            case "reverse":
                ordered.Reverse();
                return ordered;
            default:
                throw new InvalidOperationException(
                    $"{EnvVar}='{mode}' is not a known fact order; use 'default' or 'reverse'.");
        }
    }
}
