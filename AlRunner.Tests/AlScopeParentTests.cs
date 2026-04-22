using System.Reflection;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for AlScope.Parent static property — issue #1092.
///
/// The BC compiler emits `AlScope.Parent` as a static member access in some
/// scope class contexts (e.g. when a test codeunit trigger references a parent
/// scope through the class name rather than the base instance). Without a static
/// `Parent` property on AlScope, the generated C# fails with:
///   CS0117: 'AlScope' does not contain a definition for 'Parent'
///
/// The fix adds a static null-returning stub so the generated code compiles.
/// Because the real parent reference is always accessed through the injected
/// instance property on the concrete scope subclass, returning null here is safe.
/// </summary>
public class AlScopeParentTests
{
    /// <summary>
    /// Positive: AlScope.Parent is a static property accessible as a class-level member.
    /// This is a no-op stub test — the entire claim is "AlScope.Parent exists and does not crash."
    /// Without this property, CS0117 fires when BC-generated C# accesses AlScope.Parent statically.
    /// </summary>
    [Fact]
    public void AlScope_StaticParent_Exists()
    {
        var prop = typeof(AlScope).GetProperty(
            "Parent",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(prop);
    }

    /// <summary>
    /// Positive: AlScope.Parent returns null (the safe no-op stub value).
    /// Verifies the stub is not a throwing accessor — static access must succeed.
    /// </summary>
    [Fact]
    public void AlScope_StaticParent_ReturnsNull()
    {
        // Access AlScope.Parent directly at the type level (not through an instance).
        // This is exactly what the BC-generated code does when it emits AlScope.Parent.
        var prop = typeof(AlScope).GetProperty(
            "Parent",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(prop);

        var value = prop!.GetValue(null);

        Assert.Null(value);
    }
}
