using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for AlCompat.CloneTaggedOption — issue #null-option.
///
/// When an AL enum/option field is read before it has been explicitly
/// assigned, the underlying NavOption reference at the C# level is null.
/// The BC compiler emits NavOption.Create(existingVar.NavOptionMetadata, V),
/// which the RoslynRewriter transforms into
/// AlCompat.CloneTaggedOption(existingVar, V).  If existingVar is null,
/// ConditionalWeakTable.TryGetValue throws
/// "Value cannot be null (Parameter 'key')".
///
/// Fix: add a null-guard at the start of CloneTaggedOption that returns
/// NavOption.Create(ordinal) for a null existing argument, matching BC's
/// behaviour where uninitialized option fields default to ordinal 0.
/// </summary>
public class CloneTaggedOptionTests
{
    // -------------------------------------------------------------------------
    // Positive: CloneTaggedOption(null, 0) must NOT throw and must return
    // a NavOption whose ToInt32() is 0.
    // -------------------------------------------------------------------------

    [Fact]
    public void CloneTaggedOption_NullExisting_Ordinal0_ReturnsDefault()
    {
        // Arrange: simulate an uninitialized NavOption (null reference)
        NavOption? existing = null;

        // Act: must not throw ArgumentNullException / NullReferenceException
        var result = AlCompat.CloneTaggedOption(existing!, 0);

        // Assert: returned NavOption has ordinal 0
        Assert.NotNull(result);
        Assert.Equal(0, result.ToInt32());
    }

    // -------------------------------------------------------------------------
    // Positive: CloneTaggedOption(null, 2) must return ordinal 2 —
    // proving the guard does not always return 0.
    // -------------------------------------------------------------------------

    [Fact]
    public void CloneTaggedOption_NullExisting_NonZeroOrdinal_ReturnsRequestedOrdinal()
    {
        NavOption? existing = null;

        var result = AlCompat.CloneTaggedOption(existing!, 2);

        Assert.NotNull(result);
        Assert.Equal(2, result.ToInt32());
    }

    // -------------------------------------------------------------------------
    // Positive (non-null path): CloneTaggedOption with a valid existing option
    // still returns the requested ordinal — the fix must not break the normal path.
    // -------------------------------------------------------------------------

    [Fact]
    public void CloneTaggedOption_ValidExisting_ReturnsRequestedOrdinal()
    {
        // Arrange: create a real NavOption with ordinal 1
        var existing = MockRecordHandle.CreateOptionValue(1);

        // Act
        var result = AlCompat.CloneTaggedOption(existing, 3);

        // Assert: the cloned option reflects ordinal 3, not 1
        Assert.NotNull(result);
        Assert.Equal(3, result.ToInt32());
    }

    // -------------------------------------------------------------------------
    // Negative: the fix must not silently swallow genuinely invalid states.
    // A non-null existing with ordinal 0 should produce ordinal 0 — the
    // non-null path is not affected by the guard.
    // -------------------------------------------------------------------------

    [Fact]
    public void CloneTaggedOption_ValidExisting_OrdinalZero_ReturnsZero()
    {
        var existing = MockRecordHandle.CreateOptionValue(0);

        var result = AlCompat.CloneTaggedOption(existing, 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.ToInt32());
    }
}
