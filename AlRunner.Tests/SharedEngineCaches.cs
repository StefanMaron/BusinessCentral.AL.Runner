// SharedEngineCaches — #5109. Many tests hand the runner a fresh `--cache` root (or `--no-cache`)
// to isolate AL output, and each such process used to rebuild the engine caches (ncl-cecil,
// ncl-shadow) under that root before its real run started. Every runner this assembly spawns
// inherits AL_RUNNER_ENGINE_CACHE_ROOT from the test host instead, so those caches are built once
// per box and shared. A test whose subject IS a cold or isolated engine cache calls Isolate.
// SharedEngineCachesGuardTests holds the pin and refuses a spawn site that goes around it.
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AlRunner.Tests;

internal static class SharedEngineCaches
{
    // A literal, not CacheRoots.EngineCacheRootEnvVar: a module initializer must not touch an
    // AlRunner type before BcEngineBootstrap has rewritten Ncl. The guard checks they agree.
    internal const string EnvVar = "AL_RUNNER_ENGINE_CACHE_ROOT";

    /// <summary>The directory the engine caches are shared under: the runner's own default cache
    /// root (AL_RUNNER_CACHE_ROOT, else ~/.cache/al-runner), which every test that passes no
    /// --cache already shares.</summary>
    internal static string Root { get; } = ResolveRoot();

    [ModuleInitializer]
    internal static void Pin() => Environment.SetEnvironmentVariable(EnvVar, Root);

    /// <summary>
    /// Keeps the engine caches under <paramref name="psi"/>'s own cache root, for a test that
    /// asserts a cold build, a rewrite, or where those caches land.
    /// </summary>
    internal static void Isolate(ProcessStartInfo psi) => psi.Environment.Remove(EnvVar);

    private static string ResolveRoot()
    {
        var outer = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(outer)) return Path.GetFullPath(outer);
        var cacheRoot = Environment.GetEnvironmentVariable("AL_RUNNER_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(cacheRoot)) return Path.GetFullPath(cacheRoot);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "al-runner");
    }
}
