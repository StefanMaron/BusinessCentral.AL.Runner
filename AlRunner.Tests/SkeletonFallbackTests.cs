using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2064: a server died at startup with an unhandled TypeInitializationException out of
/// <c>RtFieldInfo.SetValue</c>, because the skeleton fallback tried to write a static field
/// of a type whose static constructor had already failed. These pin that the fallback names
/// that case instead, and still installs the skeleton when the type is usable.
/// </summary>
public class SkeletonFallbackTests
{
    private const BindingFlags StaticNonPublic = BindingFlags.NonPublic | BindingFlags.Static;
    private const string Hint = "probe-hint-2064";

    // Nested on purpose: the CLR reports a nested type's initializer failure under its simple
    // name, so this is the shape a type-name match would have missed.
    private sealed class NestedPoisonedCctor
    {
#pragma warning disable CS0169, CS0649
        private static NestedPoisonedCctor? instance;
#pragma warning restore CS0169, CS0649

        static NestedPoisonedCctor() => throw new PlatformNotSupportedException("probe-nested-cctor-failure");

        private static void Factory() { }
    }

    private static void InvokeFactoryExpectingThrow(Type t)
        => Assert.Throws<TargetInvocationException>(
            () => t.GetMethod("Factory", StaticNonPublic)!.Invoke(null, null));

    [Fact]
    public void FailedStaticConstructor_ThrowsNamedError_InsteadOfTheRawSetValueCrash()
    {
        var type = typeof(SkeletonFallbackProbePoisonedCctor);
        InvokeFactoryExpectingThrow(type);
        var field = type.GetField("instance", StaticNonPublic)!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => SkeletonFallback.InstallOrThrow(type, field, Hint));

        Assert.Contains(type.FullName!, ex.Message);
        Assert.Contains("System.PlatformNotSupportedException", ex.Message);
        Assert.Contains("probe-cctor-failure", ex.Message);
        Assert.Contains(type.Assembly.Location, ex.Message);
        Assert.Contains(Hint, ex.Message);
        var tie = Assert.IsType<TypeInitializationException>(ex.InnerException);
        Assert.Equal(type.FullName, tie.TypeName);
    }

    [Fact]
    public void FailedFieldInitializers_OnABeforeFieldInitType_ThrowNamedErrorFromTheStaticWrite()
    {
        // NavEnvironment's shape: no explicit static constructor, so the allocation succeeds
        // and the failure surfaces at the static field write, as in the #2064 CI log.
        var type = typeof(SkeletonFallbackProbeBeforeFieldInitPoisoned);
        Assert.True(type.Attributes.HasFlag(TypeAttributes.BeforeFieldInit));
        InvokeFactoryExpectingThrow(type);
        var field = type.GetField("instance", StaticNonPublic)!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => SkeletonFallback.InstallOrThrow(type, field, Hint));

        Assert.Contains("probe-field-initializer-failure", ex.Message);
        Assert.Contains(Hint, ex.Message);
        Assert.IsType<TypeInitializationException>(ex.InnerException);
    }

    [Fact]
    public void FailedStaticConstructor_OnANestedType_StillThrowsTheNamedError()
    {
        var type = typeof(NestedPoisonedCctor);
        InvokeFactoryExpectingThrow(type);
        var field = type.GetField("instance", StaticNonPublic)!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => SkeletonFallback.InstallOrThrow(type, field, Hint));

        Assert.Contains("probe-nested-cctor-failure", ex.Message);
        var tie = Assert.IsType<TypeInitializationException>(ex.InnerException);
        Assert.NotEqual(type.FullName, tie.TypeName);
    }

    [Fact]
    public void UsableType_GetsTheSkeletonInstalled()
    {
        var type = typeof(SkeletonFallbackProbeHealthyCctor);
        InvokeFactoryExpectingThrow(type);
        var field = type.GetField("instance", StaticNonPublic)!;
        field.SetValue(null, null);

        SkeletonFallback.InstallOrThrow(type, field, Hint);

        var skel = Assert.IsType<SkeletonFallbackProbeHealthyCctor>(SkeletonFallbackProbeHealthyCctor.Instance);
        Assert.NotNull(skel.Lock);
    }
}

internal sealed class SkeletonFallbackProbePoisonedCctor
{
#pragma warning disable CS0169, CS0649
    private static SkeletonFallbackProbePoisonedCctor? instance;
#pragma warning restore CS0169, CS0649

    static SkeletonFallbackProbePoisonedCctor() => throw new PlatformNotSupportedException("probe-cctor-failure");

    private static void Factory() { }
}

internal sealed class SkeletonFallbackProbeHealthyCctor
{
#pragma warning disable CS0169, CS0649
    private static SkeletonFallbackProbeHealthyCctor? instance;
    private object? lockObject;
#pragma warning restore CS0169, CS0649

    private static void Factory() => throw new InvalidOperationException("probe-ctor-body-failure");

    internal static object? Instance => instance;
    internal object? Lock => lockObject;
}

internal sealed class SkeletonFallbackProbeBeforeFieldInitPoisoned
{
#pragma warning disable CS0169, CS0649
    private static SkeletonFallbackProbeBeforeFieldInitPoisoned? instance;
#pragma warning restore CS0169, CS0649
    private static readonly int Boom = Throw();

    private static int Throw() => throw new PlatformNotSupportedException("probe-field-initializer-failure");

    private static int Factory() => Boom;
}
