namespace AlRunnerV2;

/// <summary>
/// One emitted module within a bundle.
///
/// Bundled mode used to merge every suite's AL into a single synthetic module, which
/// gave every app in a multi-app bundle the same identity — so
/// NavApp.GetCurrentModuleInfo returned the synthetic name, NavApp.GetResource looked
/// in app '', and install triggers had no per-app manifest to seed from. Each
/// app.json now becomes its own AppGroup and therefore its own emitted module,
/// carrying its own identity, while the bundle still runs in one process with one
/// runtime init and one test run.
/// </summary>
/// <param name="ModuleName">Compilation module name — the app's own name from app.json.</param>
/// <param name="AppId">app.json id, or null for a suite with no manifest of its own.</param>
/// <param name="DependsOn">
/// AppIds this app declares. Only siblings inside the same bundle affect emit order;
/// dependencies on external apps are resolved from the package cache as before.
/// </param>
public sealed record AppGroup(
    string ModuleName,
    Guid? AppId,
    string? Publisher,
    Version? Version,
    List<string> Paths,
    IReadOnlyList<Guid> DependsOn);
