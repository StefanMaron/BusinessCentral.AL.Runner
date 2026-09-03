// BcCompilerIncrementalContainerCloneTests — issue #2567. The structural half of #2531.
//
// #2531 fixed the instance: `ExcludeObjectsRecursive` skipped `prop.SetValue` on a cloned
// NamespaceDefinition whenever nothing of that kind was being excluded, and
// `CloneContainerShallow`'s hand-rolled NamespaceDefinition branch had left every mergeable array
// at its CLR default (null). BC's binder resolved a reference into that null array far enough to
// bind a symbol without a real NavTypeKind, and codegen died inside EmitFieldInitializer with
// "Unexpected value 'None' of type NavTypeKind".
//
// What #2531 could not fix is the invariant it left behind: "every caller of CloneContainerShallow
// must explicitly set every mergeable property, for every kind, not just the kinds it is touching",
// enforced by nothing but a doc comment. The same shape reappears whenever a new caller is added,
// and — worse — whenever Microsoft adds a property to NamespaceDefinition, because the hand-rolled
// branch names its properties one by one.
//
// This test states the invariant the clone itself must satisfy, and it is deliberately written
// against the TYPE rather than against a list of property names: it fabricates a distinguishable
// value for every public read/write property BC declares on NamespaceDefinition, clones, and
// requires every one of them to survive. A property added to BC's type next version is covered the
// day it appears, with no edit here.
//
// The "unknown container implementation throws" arm is not pinned here; see the note at the
// bottom of this file for why.
//
// Credit: vhn's fork does not have the #2531 shape at all, because
// AlRunner/Rad/ModuleDefinitionOps.ShallowCopy is exactly this generic reflective copy rather than
// a per-type switch. The fix below is that idea applied to our own clone.
using System.Reflection;
using Xunit;
using AlRunner;
using NavSymRef = Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner.Tests;

public sealed class BcCompilerIncrementalContainerCloneTests
{
    /// <summary>
    /// Every public instance property BC declares on <paramref name="type"/> that this test can
    /// both write and compare.
    /// </summary>
    private static PropertyInfo[] WritableProperties(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// A value distinguishable from the CLR default for <paramref name="type"/>, or null when this
    /// test cannot fabricate one. Arrays are created with one (null) element: what matters is that
    /// the array reference itself is non-null, since "left at null" is the whole defect.
    /// </summary>
    private static object? Fabricate(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "clone-probe";
        if (underlying == typeof(int)) return 4242;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(Guid)) return Guid.Parse("b515c0de-0000-4000-8000-00000000dead");
        if (underlying.IsArray) return Array.CreateInstance(underlying.GetElementType()!, 1);
        if (underlying.IsEnum) return Enum.GetValues(underlying).Cast<object>().LastOrDefault();
        return null;
    }

    /// <summary>
    /// A namespace clone must carry EVERY property forward, not the three the old hand-rolled
    /// branch happened to name.
    ///
    /// <para>RED on main: only <c>Id</c>, <c>Name</c> and <c>Namespaces</c> survive, so every kind
    /// array and anything else BC declares comes back null on the clone.</para>
    /// </summary>
    [Fact]
    public void CloneContainerShallow_NamespaceDefinition_CarriesEveryProperty()
    {
        var type = typeof(NavSymRef.NamespaceDefinition);
        var source = new NavSymRef.NamespaceDefinition();

        var populated = new List<PropertyInfo>();
        foreach (var property in WritableProperties(type))
        {
            var value = Fabricate(property.PropertyType);
            if (value == null) continue;
            property.SetValue(source, value);
            populated.Add(property);
        }

        // Guard against a vacuous pass: BcCompiler's own mergeable-kind list is 18 array
        // properties, and Id/Name/Namespaces are three more. If the fabricator ever stops
        // covering them the assertions below would hold trivially.
        Assert.True(populated.Count >= 20,
            $"the fabricator only populated {populated.Count} propert(ies) on "
            + $"{type.Name} ({string.Join(", ", populated.Select(p => p.Name))}). It must cover "
            + "essentially the whole type, or this test proves nothing about what a clone drops.");
        Assert.Contains(populated, p => p.Name == "Tables");
        Assert.Contains(populated, p => p.Name == "Codeunits");
        Assert.Contains(populated, p => p.Name == "Reports");
        Assert.Contains(populated, p => p.Name == "Namespaces");

        var clone = BcCompiler.CloneContainerShallowForTests(source);

        Assert.IsType<NavSymRef.NamespaceDefinition>(clone);
        Assert.NotSame(source, clone);

        var dropped = populated
            .Where(p => !Equals(p.GetValue(source), p.GetValue(clone)))
            .Select(p => $"{p.Name} ({p.PropertyType.Name}): source={Describe(p.GetValue(source))} clone={Describe(p.GetValue(clone))}")
            .ToArray();

        Assert.True(dropped.Length == 0,
            "cloning a NamespaceDefinition dropped propert(ies) the caller never named. A caller "
            + "that excludes only Codeunits leaves every OTHER kind's array null on the clone, and "
            + "BC's binder resolves a reference into a null array far enough to bind a symbol "
            + "without a real NavTypeKind — Compilation.Emit then dies inside EmitFieldInitializer "
            + "with \"Unexpected value 'None' of type NavTypeKind\" (#2531). Dropped:"
            + Environment.NewLine + string.Join(Environment.NewLine, dropped));
    }

    /// <summary>
    /// The module side of the same claim. BC's own <c>ModuleDefinition.Clone()</c> is a
    /// MemberwiseClone and already carries everything; this pins that we did not regress it while
    /// making the namespace branch generic, and that both branches now answer the same way.
    /// </summary>
    [Fact]
    public void CloneContainerShallow_ModuleDefinition_CarriesEveryProperty()
    {
        var type = typeof(NavSymRef.ModuleDefinition);
        var source = new NavSymRef.ModuleDefinition();

        var populated = new List<PropertyInfo>();
        foreach (var property in WritableProperties(type))
        {
            var value = Fabricate(property.PropertyType);
            if (value == null) continue;
            property.SetValue(source, value);
            populated.Add(property);
        }
        Assert.True(populated.Count >= 20,
            $"the fabricator only populated {populated.Count} propert(ies) on {type.Name}.");

        var clone = BcCompiler.CloneContainerShallowForTests(source);

        Assert.IsType<NavSymRef.ModuleDefinition>(clone);
        Assert.NotSame(source, clone);
        var dropped = populated
            .Where(p => !Equals(p.GetValue(source), p.GetValue(clone)))
            .Select(p => $"{p.Name} ({p.PropertyType.Name})")
            .ToArray();
        Assert.True(dropped.Length == 0,
            "cloning a ModuleDefinition dropped: " + string.Join(", ", dropped));
    }

    // NOTE: the "unknown IObjectContainerDefinition implementation throws" arm is deliberately
    // NOT pinned by a test. Implementing that interface from the test assembly means writing out
    // all 20 of its members by hand against BC's own definition types, which is a maintenance
    // burden that buys very little: the arm exists so that a future BC model change is loud rather
    // than silently mis-cloned, and BC ships exactly two implementations. The generic copy below
    // keeps the arm rather than accepting anything, which is the part that matters.

    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        Array a => $"{a.GetType().Name}[{a.Length}]",
        _ => value.ToString() ?? "<null>",
    };

}
