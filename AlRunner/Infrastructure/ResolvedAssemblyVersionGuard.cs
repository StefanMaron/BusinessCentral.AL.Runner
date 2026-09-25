using System.Reflection;
using System.Runtime.Loader;

namespace AlRunner.Infrastructure;

/// <summary>
/// Version check for the Default-ALC <c>Resolving</c> handlers that serve an assembly by simple
/// name from a directory (#4569). The runtime does NOT check the version of what a handler
/// returns: a strong-named 1.0.0.0 handed back for a 2.0.0.0 request binds, and the shortfall
/// surfaces later as a <see cref="MissingMethodException"/> far from the load (measured on
/// .NET 8, docs/assembly-resolution.md#version-check).
///
/// The rule is the binder's own TPA rule: serve when the file's version is at least the
/// requested one. A higher file version is normal and stays served — every full BC artifact
/// directory carries a few hundred such references.
/// </summary>
internal static class ResolvedAssemblyVersionGuard
{
    /// <summary>
    /// Load <paramref name="path"/> for <paramref name="requested"/>, or throw
    /// <see cref="FileLoadException"/> naming both versions and the path when the file is older
    /// than the request. An unversioned request is served as before.
    /// </summary>
    internal static Assembly LoadIfSatisfies(AssemblyLoadContext ctx, AssemblyName requested, string path)
    {
        EnsureSatisfies(requested, AssemblyName.GetAssemblyName(path).Version, path);
        return ctx.LoadFromAssemblyPath(path);
    }

    /// <summary>Same check for an assembly that is already loaded.</summary>
    internal static Assembly EnsureSatisfies(AssemblyName requested, Assembly served)
    {
        EnsureSatisfies(requested, served.GetName().Version, served.Location);
        return served;
    }

    internal static void EnsureSatisfies(AssemblyName requested, Version? served, string? path)
    {
        var wanted = requested.Version;
        if (wanted == null || (served != null && served >= wanted)) return;
        var message =
            $"Could not load '{requested.FullName}': the only candidate the runner's assembly resolver found is " +
            $"version {served?.ToString() ?? "<none>"} at '{(string.IsNullOrEmpty(path) ? "<in memory>" : path)}', " +
            $"older than the requested {wanted}. Serving it would bind a different assembly than the caller was " +
            "compiled against. Usually the caller was built against a newer BC build than the one selected; " +
            "select a BC build whose artifacts carry this version (see docs/assembly-resolution.md#version-check).";
        // The runtime wraps a handler's exception in its generic 0x80131621 FileLoadException, and
        // a caller printing only .Message loses this text — so it also goes to stderr, once per request.
        if (_reported.TryAdd(requested.FullName, 0))
            Console.Error.WriteLine("[assembly-resolver] " + message);
        throw new FileLoadException(message, requested.FullName);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
}
