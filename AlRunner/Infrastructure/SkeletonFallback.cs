using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Infrastructure;

/// <summary>
/// Installs an uninitialised stand-in into a BC singleton's static <c>instance</c> field
/// when the real factory could not build one (see <c>BcRuntime.ApplyAllPatches</c>).
/// </summary>
internal static class SkeletonFallback
{
    internal static void InstallOrThrow(Type type, FieldInfo instanceField, Exception? factoryFailure, string hint)
    {
        // A type whose static constructor failed rethrows on every later static access,
        // including the SetValue below, so no skeleton can be installed (#2064).
        if (OwnInitializerFailure(type, factoryFailure) is { } tie)
        {
            var root = tie.InnerException ?? tie;
            throw new InvalidOperationException(
                $"{type.FullName}'s static constructor threw {root.GetType().FullName}: {root.Message} " +
                $"(type loaded from '{type.Assembly.Location}'). A type whose initializer failed cannot be " +
                $"used at all, so the runner cannot fall back to a skeleton instance. {hint}",
                tie);
        }

        var skel = RuntimeHelpers.GetUninitializedObject(type);
        var instLock = type.GetField("lockObject", BindingFlags.NonPublic | BindingFlags.Instance);
        if (instLock != null) instLock.SetValue(skel, new object());
        instanceField.SetValue(null, skel);
    }

    private static TypeInitializationException? OwnInitializerFailure(Type type, Exception? failure)
    {
        for (var e = failure; e != null; e = e.InnerException)
            if (e is TypeInitializationException tie && tie.TypeName == type.FullName)
                return tie;
        return null;
    }
}
