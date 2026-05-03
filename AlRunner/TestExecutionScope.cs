namespace AlRunner;

/// <summary>
/// Per-test scope holding the messages and captured values produced during a single
/// test method invocation. Pushed onto AsyncLocal at the start of the test, popped
/// at the end. Mock infrastructure (MessageCapture, ValueCapture) reads
/// <see cref="Current"/> and writes into the scope state when present, falling back
/// to the global aggregates otherwise (so non-test code paths still work).
/// </summary>
public static class TestExecutionScope
{
    private static readonly AsyncLocal<TestExecutionState?> _current = new();

    /// <summary>The current per-test state, or null when outside a test scope.</summary>
    public static TestExecutionState? Current => _current.Value;

    /// <summary>
    /// Begin a new per-test scope for <paramref name="testName"/>.
    /// Dispose the returned token to restore the previous scope.
    /// </summary>
    public static IDisposable Begin(string testName)
    {
        var prev = _current.Value;
        var state = new TestExecutionState(testName);
        _current.Value = state;
        return new Scope(prev);
    }

    private sealed class Scope : IDisposable
    {
        private readonly TestExecutionState? _prev;
        private bool _disposed;

        public Scope(TestExecutionState? prev) { _prev = prev; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = _prev;
        }
    }
}

/// <summary>
/// Mutable state accumulated for a single test invocation.
/// Instances are created by <see cref="TestExecutionScope.Begin"/> and are
/// not shared across threads (each test runs sequentially in the current executor).
/// </summary>
public sealed class TestExecutionState
{
    public string TestName { get; }
    public List<string> Messages { get; } = new();
    public List<(string ScopeName, string ObjectName, string VariableName, string? Value, int StatementId)> CapturedValues { get; } = new();

    public TestExecutionState(string name) => TestName = name;
}
