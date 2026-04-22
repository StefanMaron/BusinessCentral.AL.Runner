using System.Reflection;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for AlScope.Parent instance property — issues #1092 and #1105.
///
/// The BC compiler emits `Parent` access in scope class bodies in two patterns:
///   • Some versions use `base.Parent.xxx` (handled by the RoslynRewriter rewrite)
///   • Other versions use `this.Parent` (instance access on the scope class)
///
/// Issue #1092 (CS0117) was fixed by adding a static `Parent` stub.
/// Issue #1105 (CS0176) shows that `this.Parent` in a scope class where the
/// enclosing-type injection was skipped causes CS0176 ("static member accessed
/// with an instance reference") because the static `AlScope.Parent` is resolved.
///
/// The correct fix: `Parent` must be an **instance** property on `AlScope`.
/// This allows `this.Parent` to resolve correctly at both compile-time and runtime.
/// The injected `public TParent Parent => _parent` on concrete scope subclasses
/// hides (not overrides) the base instance property, so both levels work.
/// </summary>
public class AlScopeParentTests
{
    /// <summary>
    /// Positive: AlScope.Parent is an *instance* property (not static).
    /// An instance property prevents CS0176 when BC-generated C# accesses Parent
    /// via `this.Parent` on a scope class that inherits from AlScope.
    /// </summary>
    [Fact]
    public void AlScope_InstanceParent_Exists()
    {
        var prop = typeof(AlScope).GetProperty(
            "Parent",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(prop);
    }

    /// <summary>
    /// Negative: AlScope.Parent must NOT be static, so that `this.Parent` in scope
    /// subclasses does not trigger CS0176.
    /// </summary>
    [Fact]
    public void AlScope_Parent_IsNotStatic()
    {
        var staticProp = typeof(AlScope).GetProperty(
            "Parent",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Null(staticProp);
    }

    /// <summary>
    /// Positive: AlScope.Parent returns null on a base instance.
    /// The instance property is a safe no-op stub — concrete scope subclasses
    /// shadow it with their own injected `public T Parent => _parent` property.
    /// </summary>
    [Fact]
    public void AlScope_InstanceParent_ReturnsSelf()
    {
        var prop = typeof(AlScope).GetProperty(
            "Parent",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(prop);

        // AlScope is abstract — use a minimal concrete subclass to access via instance.
        var instance = new MinimalAlScope();
        var value = prop!.GetValue(instance);

        // Parent returns `this` (not null) so base.Parent.Bind() doesn't NPE
        Assert.Same(instance, value);
    }

    /// <summary>
    /// Minimal concrete AlScope subclass for reflection-based instance tests.
    /// Mimics the structure of a BC-generated scope class (after rewriting) that
    /// does NOT shadow Parent with its own injected property — i.e., the worst case
    /// where the fallback to AlScope.Parent must succeed as an instance access.
    /// </summary>
    private sealed class MinimalAlScope : AlScope
    {
        protected override void OnRun() { }
    }
}
