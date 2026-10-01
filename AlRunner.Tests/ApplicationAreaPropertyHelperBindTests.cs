// ApplicationAreaPropertyHelperBindTests — #5104.
//
// RUNNER-MECHANISM test: ApplicationAreaControlRemoval.Bind finds BC's PropertyHelper through
// AssemblyTypeIndex instead of Assembly.GetTypes(). Pins that the lookup binds the type the
// full scan bound, and that a missing type or method still refuses rather than binding nothing.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class ApplicationAreaPropertyHelperBindTests
{
    private static Type[] IsFalseSignature() => new[]
    {
        typeof(InfopartPageDefinition).GetProperty(nameof(InfopartPageDefinition.Visible))!.PropertyType,
    };

    private static Assembly BcTypes => typeof(MasterPage).Assembly;

    [Fact]
    public void FindPropertyHelper_BindsTheTypeTheFullScanBound()
    {
        var signature = IsFalseSignature();
        var viaScan = BcTypes.GetTypes().First(t => t.Name == "PropertyHelper"
            && t.GetMethod("PropertyIsFalse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, signature, null) != null);

        // An assembly without the type is skipped, not an answer.
        var bound = ApplicationAreaControlRemoval.FindPropertyHelper(
            new[] { typeof(object).Assembly, BcTypes }, signature);

        Assert.Same(viaScan, bound);
        Assert.Equal("Microsoft.Dynamics.Nav.Types.Metadata.PropertyHelper", bound.FullName);
    }

    [Fact]
    public void FindPropertyHelper_Refuses_WhenNoAssemblyDeclaresTheType()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApplicationAreaControlRemoval.FindPropertyHelper(
            new[] { typeof(object).Assembly }, IsFalseSignature()));

        Assert.Contains("PropertyHelper.PropertyIsFalse(", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BC metadata shape changed", ex.Message, StringComparison.Ordinal);
    }

    // This test assembly declares a type named PropertyHelper whose PropertyIsFalse takes the
    // wrong parameter, so the name alone would bind it; the method check must reject it.
    [Fact]
    public void FindPropertyHelper_Refuses_WhenThePropertyHelperFoundLacksTheMethod()
    {
        // The name lookup does reach the decoy, so the refusal below is the method check's.
        Assert.Same(typeof(PropertyHelper), AlRunner.Infrastructure.AssemblyTypeIndex
            .For(typeof(ApplicationAreaPropertyHelperBindTests).Assembly).FindFirst("PropertyHelper"));

        var ex = Assert.Throws<InvalidOperationException>(() => ApplicationAreaControlRemoval.FindPropertyHelper(
            new[] { typeof(ApplicationAreaPropertyHelperBindTests).Assembly }, IsFalseSignature()));

        Assert.Contains("PropertyHelper.PropertyIsFalse(", ex.Message, StringComparison.Ordinal);
    }

    // And the same decoy, placed first, does not shadow BC's own type.
    [Fact]
    public void FindPropertyHelper_SkipsADecoyWithoutTheMethod_AndBindsTheRealOne()
    {
        var bound = ApplicationAreaControlRemoval.FindPropertyHelper(
            new[] { typeof(ApplicationAreaPropertyHelperBindTests).Assembly, BcTypes }, IsFalseSignature());

        Assert.Equal("Microsoft.Dynamics.Nav.Types.Metadata.PropertyHelper", bound.FullName);
    }

    internal static class PropertyHelper
    {
        public static bool PropertyIsFalse(int notAPropertyExpression) => notAPropertyExpression == 0;
    }
}
