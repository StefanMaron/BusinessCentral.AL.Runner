using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2064: a server died at startup with an unhandled TypeInitializationException out of
/// <c>RtFieldInfo.SetValue</c>, because the skeleton fallback tried to write a static field
/// of a type whose static constructor had already failed. These pin that the fallback names
/// that case instead, and still installs the skeleton for every other factory failure.
/// </summary>
public class SkeletonFallbackTests
{
    private const BindingFlags StaticNonPublic = BindingFlags.NonPublic | BindingFlags.Static;
    private const string Hint = "probe-hint-2064";

    private static Exception InvokeFactory(Type t)
        => Assert.Throws<TargetInvocationException>(
            () => t.GetMethod("Factory", StaticNonPublic)!.Invoke(null, null));

    [Fact]
    public void FailedStaticConstructor_ThrowsNamedError_InsteadOfTheRawSetValueCrash()
    {
        var type = typeof(SkeletonFallbackProbePoisonedCctor);
        var failure = InvokeFactory(type);
        var field = type.GetField("instance", StaticNonPublic)!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => SkeletonFallback.InstallOrThrow(type, field, failure, Hint));

        Assert.Contains(type.FullName!, ex.Message);
        Assert.Contains("System.PlatformNotSupportedException", ex.Message);
        Assert.Contains("probe-cctor-failure", ex.Message);
        Assert.Contains(type.Assembly.Location, ex.Message);
        Assert.Contains(Hint, ex.Message);
        var tie = Assert.IsType<TypeInitializationException>(ex.InnerException);
        Assert.Equal(type.FullName, tie.TypeName);
    }

    [Fact]
    public void OrdinaryFactoryFailure_StillInstallsTheSkeleton()
    {
        var type = typeof(SkeletonFallbackProbeHealthyCctor);
        var failure = InvokeFactory(type);
        var field = type.GetField("instance", StaticNonPublic)!;
        field.SetValue(null, null);

        SkeletonFallback.InstallOrThrow(type, field, failure, Hint);

        var skel = Assert.IsType<SkeletonFallbackProbeHealthyCctor>(SkeletonFallbackProbeHealthyCctor.Instance);
        Assert.NotNull(skel.Lock);
    }

    [Fact]
    public void AnotherTypesInitializerFailure_DoesNotBlockTheSkeleton()
    {
        var type = typeof(SkeletonFallbackProbeHealthyCctor);
        var field = type.GetField("instance", StaticNonPublic)!;
        var foreign = new TargetInvocationException(
            new TypeInitializationException(typeof(SkeletonFallbackProbeOtherType).FullName, new PlatformNotSupportedException("elsewhere")));
        field.SetValue(null, null);

        SkeletonFallback.InstallOrThrow(type, field, foreign, Hint);

        Assert.IsType<SkeletonFallbackProbeHealthyCctor>(SkeletonFallbackProbeHealthyCctor.Instance);
    }
}

// Top-level on purpose: the CLR reports a NESTED type's initializer failure under its simple
// name, while NavEnvironment (top-level) is reported under its full name.
internal sealed class SkeletonFallbackProbePoisonedCctor
{
#pragma warning disable CS0169, CS0649
    private static SkeletonFallbackProbePoisonedCctor? instance;
    private object? lockObject;
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

internal sealed class SkeletonFallbackProbeOtherType { }
