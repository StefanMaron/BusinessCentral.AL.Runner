// TestBuildConfig — one source of truth for how the tests spawn the runner.
//
// The tests that shell out to `dotnet run --no-build --project AlRunner` used to
// disagree about the build configuration: four hardcoded `-c Release`, four passed no
// `-c` at all (so MSBuild defaulted to Debug). Whichever half did not match the
// configuration the suite was actually built in failed with
//
//   Unhandled exception: An error occurred trying to start process
//   '…/AlRunner/bin/<Config>/net8.0/al-runner' … No such file or directory
//
// so the suite could not be fully green in EITHER configuration: a local Debug run
// broke the `-c Release` half, and CI (which builds Release only) broke the other.
//
// Derive it instead from the test assembly's own output path, so the runner subprocess
// is always the one built alongside the tests invoking it.

namespace AlRunner.Tests;

internal static class TestBuildConfig
{
    /// <summary>
    /// "Debug" or "Release" — whichever configuration THIS test assembly was built in,
    /// read from its output path (…/bin/&lt;Configuration&gt;/&lt;tfm&gt;/).
    /// </summary>
    internal static string Configuration { get; } = ResolveConfiguration();

    /// <summary>Target framework moniker of the running runtime, e.g. "net8.0".</summary>
    internal static string Framework =>
        $"net{Environment.Version.Major}.{Environment.Version.Minor}";

    /// <summary>
    /// The leading `dotnet` argument for invoking the runner directly: the quoted path
    /// to the built `al-runner.dll`, so the caller's own runner arguments append straight
    /// onto it and the whole line is `dotnet "&lt;dll&gt;" &lt;args...&gt;`.
    ///
    /// This used to be `run --no-build -c {Configuration} --framework {Framework} --project
    /// "{projectPath}" --`, which pays ~800ms of MSBuild project evaluation on every spawn
    /// (issue #1808). Since AlRunner.Tests always builds AlRunner as a project reference
    /// first, the DLL this test assembly was built against is already on disk — invoking it
    /// directly skips that evaluation entirely. Dropped the trailing `--`: it was a
    /// `dotnet run` argument separator: under `dotnet &lt;dll&gt;` there is no MSBuild
    /// argument parsing to separate from, so forwarding it would hand the runner's own CLI
    /// parser a stray `--` token.
    /// </summary>
    internal static string RunArgs(string projectPath) =>
        $"\"{ResolveAssemblyPath(projectPath)}\"";

    /// <summary>
    /// Resolves the built runner assembly path and fails loudly (naming the searched path)
    /// if it is not on disk, rather than silently falling back to `dotnet run` — a fallback
    /// would hide the very MSBuild-evaluation regression this method exists to avoid, per
    /// .claude/rules/loud-failures.md.
    /// </summary>
    private static string ResolveAssemblyPath(string projectPath)
    {
        var path = Path.Combine(projectPath, "bin", Configuration, Framework, "al-runner.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Runner assembly not found at '{path}'. Build the AlRunner project " +
                $"(dotnet build -c {Configuration} AlRunner/AlRunner.csproj) before running " +
                "tests that spawn it.",
                path);
        }

        return path;
    }

    /// <summary>
    /// The ` --bc-version &lt;version&gt;` argument to pass to a spawned runner, pinned to the
    /// BC build THIS binary was compiled against.
    ///
    /// Same defect as the build configuration above, one field over: seven suites hardcoded
    /// `--bc-version 28.1`, so on any CI matrix leg building against another BC version
    /// every one of them died before testing anything —
    ///   "BC version selection failed: No BC artifact ... matches version '28.1'.
    ///    Available: 28.0.46665.53240"
    /// — 13 failures that look like the runner is broken on older BC and are really just the
    /// tests asking for a version the leg never downloaded. Deriving it means a leg tests the
    /// BC version it was actually built and provisioned for.
    ///
    /// Empty when the built version is unknown, which leaves the runner's own default
    /// selection in charge rather than pinning it to a guess.
    /// </summary>
    internal static string BcVersionArg { get; } = ResolveBcVersionArg();

    private static string ResolveBcVersionArg()
    {
        var built = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        return built == null ? string.Empty : $" --bc-version {built}";
    }

    private static string ResolveConfiguration()
    {
        // AppContext.BaseDirectory is …/AlRunner.Tests/bin/<Configuration>/<tfm>/.
        // Walk up one level from the tfm directory to read the configuration name.
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configName = tfmDir.Parent?.Name;

        // Only trust the path when it says something we recognise; otherwise fall back to
        // the compile-time configuration of this assembly rather than guessing.
        if (string.Equals(configName, "Debug", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configName, "Release", StringComparison.OrdinalIgnoreCase))
        {
            return configName!;
        }

#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
