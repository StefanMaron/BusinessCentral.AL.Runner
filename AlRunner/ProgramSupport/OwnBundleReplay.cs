namespace AlRunner;

// #3250: RunBundleForServer's cross-bundle reuse hands a bundle at directory B the module an
// earlier request loaded from directory A. The request's ResetForNewBundleReload has already
// cleared the emit-derived registries, and the AL-output cache block that replays them is
// skipped on reuse — so the replay is captured when A's module is registered and applied
// when it is handed back.
internal static partial class ProgramSupport
{
    private const string InjectCaptureFailureEnvVar = "AL_RUNNER_TEST_FAIL_OWN_BUNDLE_REPLAY_CAPTURE";

    /// <summary>
    /// The replay for a module just loaded from <paramref name="moduleName"/>. Prefers the
    /// AL-output cache's own sidecars when they are on disk; otherwise (no cache, NOKEY, a
    /// failed cache write) writes a per-process snapshot, because the registries are about to
    /// be cleared by the next request and nothing else keeps them.
    /// </summary>
    /// <param name="querySymbolsJson">
    /// This bundle's query-symbols file, or null when it declares no query. Not
    /// <c>BcCompiler.LastBundleQuerySymbolsPath</c>: the incremental emit path does not reset it,
    /// so it can name another module's file.
    /// </param>
    internal static OwnBundleRegistryReplay CaptureOwnBundleReplay(
        Guid appId, string moduleName, string? cacheEnumSidecar, string? querySymbolsJson, string? cacheQuerySidecar)
    {
        // Test-only seam, same family and gating as AL_RUNNER_TEST_FAIL_COMPANY_INIT: no shipped
        // path sets it, and a capture failure has no other deterministic trigger. Its value
        // becomes the exception message.
        if (Environment.GetEnvironmentVariable(InjectCaptureFailureEnvVar) is { Length: > 0 } injected)
            throw new IOException(injected);
        string? dir = null;
        string ScratchDir() => dir ??= AlRunner.Infrastructure.PerProcessScratch.Dir(
            "al-runner-own-bundle-replay", $"{moduleName}-{appId:N}");

        string enumPath;
        if (cacheEnumSidecar != null && File.Exists(cacheEnumSidecar))
            enumPath = cacheEnumSidecar;
        else
        {
            enumPath = Path.Combine(ScratchDir(), "enum-registry.json");
            SaveEnumRegistrySidecar(enumPath);
        }

        string? queryPath = null;
        if (querySymbolsJson != null)
        {
            if (cacheQuerySidecar != null && File.Exists(cacheQuerySidecar))
                queryPath = cacheQuerySidecar;
            else if (File.Exists(querySymbolsJson))
            {
                // Copied: BcCompiler's per-process file is keyed on the module NAME, which a
                // later bundle of another app can share and overwrite.
                queryPath = Path.Combine(ScratchDir(), "SymbolReference.json");
                File.Copy(querySymbolsJson, queryPath, overwrite: true);
            }
        }
        return new OwnBundleRegistryReplay(enumPath, queryPath);
    }

    /// <summary>
    /// Replays <paramref name="replay"/> into the registries. Returns the enum entries replayed.
    /// Throws when a recorded file is gone: running the reused module without its metadata is the
    /// silent wrong answer #3250 is about (loud-failures.md).
    /// </summary>
    internal static int ApplyOwnBundleReplay(OwnBundleRegistryReplay replay)
    {
        if (replay.CaptureFailure != null || replay.EnumRegistrySidecar == null)
            throw new InvalidOperationException(
                $"capturing the registries failed when the module was loaded: {replay.CaptureFailure}");
        if (!File.Exists(replay.EnumRegistrySidecar))
            throw new FileNotFoundException(
                $"the registry snapshot recorded for the reused module is gone: {replay.EnumRegistrySidecar}",
                replay.EnumRegistrySidecar);
        int replayed = LoadEnumRegistrySidecar(replay.EnumRegistrySidecar);
        if (replay.QuerySymbolsJson != null)
        {
            // The fallback when BC's captured query document is missing, as on the cache-HIT
            // branch (#4040).
            if (!File.Exists(replay.QuerySymbolsJson))
                throw new FileNotFoundException(
                    $"the query symbols recorded for the reused module are gone: {replay.QuerySymbolsJson}",
                    replay.QuerySymbolsJson);
            AlRunner.Patches.RecordPatches.RegisterBundleQuerySymbolsJson(replay.QuerySymbolsJson);
        }
        return replayed;
    }
}
