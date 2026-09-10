// DotNetShimProbingTests — issue #3745.
//
// RUNNER-MECHANISM test. The claim is about the runner's own compile configuration, not about
// what Business Central does, so a service tier cannot adjudicate it and there is nothing for
// the corpus to express: the corpus compiles its apps and runs them, and this is about which
// assembly BC's AL binder resolves a DotNet parameter type from while compiling somebody else's
// app. See the PR body for the measured end-to-end result the shim produces.
//
// WHAT WENT WRONG, AND WHY IT COST THE WHOLE APP
//   System Application's SamplingPerfProfilerImpl calls JsonSerializer.Deserialize(TextReader,
//   Type). The BC service tier ships only the net6.0 build of Newtonsoft.Json, whose TextReader
//   parameter is typed against System.Runtime 6.0.0.0. No .NET 6 reference assemblies exist on a
//   net8 box, so that parameter type never resolves and the call raises AL0133. Compilation.Emit
//   is atomic per module, so that ONE unresolvable reference yielded zero metadata documents for
//   all 1,319 of System Application's source files rather than a partial result.
//
//   The netstandard2.0 build of the SAME Newtonsoft version binds against netstandard, which
//   does resolve. AlRunner.csproj's CopyDotNetShims target stages it into a `dotnet-shims`
//   directory and GetOrCreateDotNetFactory probes that directory FIRST.
//
// WHY "FIRST" IS THE LOAD-BEARING PART, AND WHAT THIS FILE PINS
//   A shim exists precisely because the service tier's own copy of that assembly does not bind.
//   Probing the tier ahead of the shim would find the net6.0 copy again and defeat the whole
//   mechanism, silently — the compile would go back to producing zero documents with nothing
//   naming the cause. So ordering is not a detail here, it is the fix, and OrderPutsShimsFirst
//   is the assertion that fails if a later edit appends the shim directory instead of
//   prepending it.
//
//   These are unit tests over the probing-path construction, deliberately not a full System
//   Application compile: that compile is ~14s and needs provisioned BC artifacts, so it belongs
//   to the measurement recorded in docs/dependency-metadata-from-bc.md rather than to every
//   `dotnet test` run.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class DotNetShimProbingTests : IDisposable
{
    private readonly string? _savedOverride =
        Environment.GetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS");

    public void Dispose() =>
        Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", _savedOverride);

    private static IReadOnlyList<string> ShimDirs()
    {
        var m = typeof(BcCompiler).GetMethod(
                    "EnumerateDotNetShimDirs", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BcCompiler.EnumerateDotNetShimDirs not found. If it was renamed, this test " +
                    "and the probing-order guarantee it protects need to move with it.");
        return ((IEnumerable<string>)m.Invoke(null, null)!).ToList();
    }

    /// <summary>
    /// The shim the fix exists for is actually staged next to the RUNNER binary. Asserted on the
    /// real build output rather than a fixture: a test that stages its own copy would pass with
    /// the csproj target deleted, which is the failure mode this pins against.
    ///
    /// <para>The runner's output directory, not <c>AppContext.BaseDirectory</c> — the test host
    /// runs out of AlRunner.Tests' own output, which is a different directory, so asserting on
    /// the ambient one would measure the test project's build rather than the shipped runner's
    /// (measured while writing this: the shim was correctly staged and the assertion still
    /// failed).</para>
    /// </summary>
    [Fact]
    public void BuildStagesTheNewtonsoftNetstandardShimBesideTheRunnerBinary()
    {
        var dir = Path.Combine(RunnerOutputDir(), "dotnet-shims");
        Assert.True(Directory.Exists(dir),
            $"no dotnet-shims directory at {dir}. AlRunner.csproj's CopyDotNetShims target " +
            "stages it; without it System Application's emit yields zero documents (#3745).");

        var shim = Path.Combine(dir, "Newtonsoft.Json.dll");
        Assert.True(File.Exists(shim), $"Newtonsoft.Json.dll missing from {dir}");

        // The netstandard2.0 build specifically. The net6.0 build is the one that does NOT bind,
        // and the service tier already ships that — staging it here would be a no-op wearing the
        // shape of a fix.
        var name = AssemblyName.GetAssemblyName(shim);
        Assert.Equal("Newtonsoft.Json", name.Name);

        var text = File.ReadAllText(shim, System.Text.Encoding.Latin1);
        Assert.Contains(".NETStandard,Version=v2.0", text);
        Assert.DoesNotContain(".NETCoreApp,Version=v6.0", text);
    }

    /// <summary>
    /// The staged directory is one the probing enumeration actually yields. Without this the
    /// file could ship to a path nothing reads — green csproj, green probing list, no fix.
    /// </summary>
    [Fact]
    public void StagedShimDirectoryIsOneOfTheProbedDirectories()
    {
        Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
        var probed = ShimDirs().Select(Path.GetFullPath).ToList();

        // In a shipped run these two are the same directory; under the test host they are not,
        // which is exactly why the enumeration yields both.
        Assert.Contains(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "dotnet-shims")),
            probed);
        Assert.Contains(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(BcCompiler).Assembly.Location)!, "dotnet-shims")),
            probed);
    }

    /// <summary>
    /// The runner project's build output. Derived from the location of the assembly under test
    /// rather than from a path relative to the source tree, so it stays correct under any
    /// configuration or target-framework layout.
    /// </summary>
    private static string RunnerOutputDir()
    {
        var dir = Path.GetDirectoryName(typeof(BcCompiler).Assembly.Location);
        Assert.False(string.IsNullOrEmpty(dir),
            "could not locate the runner assembly on disk; the staging assertion has nothing to measure.");
        return dir!;
    }

    /// <summary>
    /// The ordering guarantee, and the reason the whole mechanism works: every shim directory
    /// precedes the service-tier directory in the assembled probing path. A later edit that
    /// appends instead of prepends leaves a compile that silently resolves the net6.0 copy again.
    /// </summary>
    [Fact]
    public void OrderPutsShimsAheadOfTheServiceTier()
    {
        var probeDir = Directory.CreateTempSubdirectory("al-runner-shim-order-");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", probeDir.FullName);

            var paths = InvokeProbingPaths();
            var shimIdx = paths.FindIndex(
                p => string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),
                                   probeDir.FullName.TrimEnd(Path.DirectorySeparatorChar),
                                   StringComparison.Ordinal));
            Assert.True(shimIdx >= 0,
                "the AL_RUNNER_DOTNET_SHIMS directory did not reach the probing paths: " +
                string.Join(", ", paths));
            Assert.Equal(0, shimIdx);

            var tierIdx = paths.FindIndex(
                p => string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),
                                   Path.GetFullPath(BcCompiler.DefaultServiceTierDir)
                                       .TrimEnd(Path.DirectorySeparatorChar),
                                   StringComparison.Ordinal));
            if (tierIdx >= 0)
                Assert.True(shimIdx < tierIdx,
                    $"shim dir at {shimIdx} must precede the service tier at {tierIdx}; " +
                    "the tier ships the copy that does not bind.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
            try { probeDir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The override takes several paths, and a non-existent one is dropped rather than handed to
    /// BC's AssemblyLocator. Negative direction: an empty or unset override contributes nothing,
    /// so the default staged directory is the only shim source.
    /// </summary>
    [Fact]
    public void OverrideAcceptsSeveralPathsAndUnsetContributesNone()
    {
        var a = Directory.CreateTempSubdirectory("al-runner-shim-a-");
        var b = Directory.CreateTempSubdirectory("al-runner-shim-b-");
        var missing = Path.Combine(Path.GetTempPath(), "al-runner-shim-absent-" + Guid.NewGuid());
        try
        {
            Environment.SetEnvironmentVariable(
                "AL_RUNNER_DOTNET_SHIMS",
                string.Join(Path.PathSeparator, a.FullName, missing, b.FullName));

            var yielded = ShimDirs().Select(p => p.TrimEnd(Path.DirectorySeparatorChar)).ToList();
            Assert.Equal(a.FullName.TrimEnd(Path.DirectorySeparatorChar), yielded[0]);
            Assert.Equal(missing.TrimEnd(Path.DirectorySeparatorChar), yielded[1]);
            Assert.Equal(b.FullName.TrimEnd(Path.DirectorySeparatorChar), yielded[2]);

            // The enumeration yields candidates; the caller filters. A directory that does not
            // exist must not reach the assembled probing paths.
            var paths = InvokeProbingPaths().Select(Path.GetFullPath).ToList();
            Assert.Contains(Path.GetFullPath(a.FullName), paths);
            Assert.Contains(Path.GetFullPath(b.FullName), paths);
            Assert.DoesNotContain(Path.GetFullPath(missing), paths);

            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", "");
            var defaults = ShimDirs();
            Assert.DoesNotContain(a.FullName.TrimEnd(Path.DirectorySeparatorChar),
                defaults.Select(p => p.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.All(defaults, p => Assert.Equal("dotnet-shims", Path.GetFileName(p)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
            try { a.Delete(recursive: true); } catch { /* best effort */ }
            try { b.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// One entry per directory. The runner shadow-copies its own directory before re-exec, so
    /// <c>AppContext.BaseDirectory</c> and the assembly's directory are then the SAME path and
    /// the enumeration yields it twice — measured in a real run, where the probing-path dump
    /// printed the shadow directory on two consecutive lines. Harmless to BC's locator, but a
    /// probing list that double-counts is a list nobody can read a verdict off.
    /// </summary>
    [Fact]
    public void DuplicateShimDirectoriesAreCollapsedInTheProbingPaths()
    {
        var probeDir = Directory.CreateTempSubdirectory("al-runner-shim-dup-");
        try
        {
            // The same directory twice, plus a trailing-separator spelling of it, which is the
            // shape the shadow-copy case actually produces.
            Environment.SetEnvironmentVariable(
                "AL_RUNNER_DOTNET_SHIMS",
                string.Join(Path.PathSeparator,
                    probeDir.FullName,
                    probeDir.FullName,
                    probeDir.FullName + Path.DirectorySeparatorChar));

            var paths = InvokeProbingPaths();
            var matches = paths.Count(p => string.Equals(
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),
                probeDir.FullName.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal));
            Assert.Equal(1, matches);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
            try { probeDir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The probing list PRODUCTION builds, not a reconstruction of it. This is the whole point:
    /// an earlier version of this file rebuilt the list from the same two enumerations, and when
    /// the production order was inverted so that shims came after the service tier, every
    /// assertion here still passed — the test was measuring its own copy. Calling
    /// <c>BuildDotNetProbingPaths</c> is what makes the ordering assertions mean anything.
    /// </summary>
    private static List<string> InvokeProbingPaths() => BcCompiler.BuildDotNetProbingPaths();
}
