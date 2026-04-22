namespace AlRunner.Runtime;

/// <summary>
/// Standalone replacement for BC's <c>NavScope</c> type.
///
/// The BC compiler emits <c>using (var δretValParent1 = new NavScope(this))</c>
/// to track ownership of scope blocks (e.g., around FindSet/Find result iteration).
/// The rewriter previously replaced <c>NavScope</c> with <c>object</c>, but
/// <c>object</c> has no 1-argument constructor and does not implement
/// <c>IDisposable</c>, causing CS1729 and CS1674.
///
/// <c>MockNavScope</c> accepts the parent scope argument (discarded) and
/// implements <c>IDisposable</c> with a no-op <c>Dispose</c>, satisfying
/// both the constructor and the <c>using</c> statement contract — issues
/// #1085 and #1090.
/// </summary>
public sealed class MockNavScope : IDisposable
{
    /// <summary>
    /// Accepts the parent scope reference emitted by the BC compiler.
    /// The reference is intentionally discarded — in standalone mode there
    /// is no NavScope ownership chain to maintain.
    /// </summary>
    public MockNavScope(object? parent) { }

    /// <inheritdoc/>
    public void Dispose() { }
}
