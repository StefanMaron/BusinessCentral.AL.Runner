// ServerGcConfigTests — the shipped runner must run under Server GC (issue #2577).
//
// Nothing declared a GC mode, so every user got the .NET default (Workstation). A cold AL
// compile allocates heavily and keeps most of it reachable until Roslyn finishes, which is
// the workload Server GC exists for.
//
// Measured here on the al-language corpus (2361 tests), cold cache, same Release binary,
// DOTNET_gcServer the only variable, 12-core Linux box. Instructions retired (user mode)
// rather than wall clock, because wall clock on a shared box moves for reasons that have
// nothing to do with the change:
//
//   Workstation   707.8  700.4  679.8  701.8  699.9  714.8  G instructions
//   Server        658.1  645.2  658.9  657.2  656.1         G instructions
//
// 6.2% fewer, and the two sets do not overlap. A warm single-bundle run — the common case
// for a user with one project — moved the same direction (38.10 G to 36.16 G, 5.1%), so the
// small case does not pay for the large one.
//
// The cost is peak resident memory: 2.2 GB to 4.9 GB on this 12-core box, because Server GC
// scales its heap count with core count. See AlRunner.csproj's note for the full table.
//
// Why a CONFIG test and not only the behavioral one
// -------------------------------------------------
// PhaseLogIntegrationTests.TheRunnerProcess_RunsUnderServerGc spawns a real runner and reads
// GCSettings.IsServerGC back out of its phase log. That is the stronger claim, because it
// proves the setting reached the process. But it is also satisfiable for the wrong reason: a
// developer or CI runner with DOTNET_gcServer=1 exported makes it pass with the csproj
// property deleted. This file closes that hole by asserting the shipped runtimeconfig.json —
// the only thing a user who sets no environment variables gets.
//
// There is deliberately no test asserting background/concurrent GC stays on. That is the
// .NET default and nothing here sets it, so the test would assert nothing on every run that
// matters.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerGcConfigTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The runtimeconfig.json beside the al-runner.dll the tests actually spawn — resolved
    /// through <see cref="TestBuildConfig"/> so this can never assert about a different build
    /// than the rest of the suite exercises.
    /// </summary>
    private static string RuntimeConfigPath => Path.Combine(
        RepoRoot, "AlRunner", "bin", TestBuildConfig.Configuration,
        TestBuildConfig.Framework, "al-runner.runtimeconfig.json");

    [Fact]
    public void ShippedRuntimeConfig_EnablesServerGc()
    {
        Assert.True(File.Exists(RuntimeConfigPath),
            $"no runtimeconfig at '{RuntimeConfigPath}' — build AlRunner before running this suite");

        using var doc = JsonDocument.Parse(File.ReadAllText(RuntimeConfigPath));
        var props = doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        // Absent is a distinct failure from present-and-false: it means the csproj property
        // was removed and every user silently fell back to Workstation GC, which is the
        // state this repo sat in while every benchmark harness exported DOTNET_gcServer=1
        // by hand and no shipped run ever got it.
        Assert.True(props.TryGetProperty("System.GC.Server", out var serverGc),
            "System.GC.Server is absent from the shipped runtimeconfig — restore "
            + "<ServerGarbageCollection>true</ServerGarbageCollection> in AlRunner.csproj");
        Assert.Equal(JsonValueKind.True, serverGc.ValueKind);
    }
}
