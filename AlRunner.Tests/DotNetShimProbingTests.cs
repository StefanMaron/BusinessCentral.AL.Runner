// DotNetShimProbingTests — issues #3745 (Newtonsoft.Json) and #3876 (AspNetCore.StaticFiles).
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
//   THE SECOND SHIM, #3876: Base Application's dotnet.al declares
//   assembly(Microsoft.AspNetCore.StaticFiles), which BC ships in no artifact, so its emit
//   produced zero objects too — the same atomic-per-module shape. Staging that one file from the
//   ASP.NET Core reference pack takes it to errors=0, objects=7850, 7,842 documents. It binds
//   despite AL0451 naming `PublicKeyToken=null` because the AL declaration states NO token and
//   BC's AssemblyLocatorBase.IsAssemblyCompatible guards its token check on the search name
//   carrying one — asymmetric, not token-blind. docs/dependency-metadata-from-bc.md has the
//   measurement and the locator detail.
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

    /// <summary>
    /// An owned scratch directory that EXISTS.
    ///
    /// <para><c>TestScratch.Dir</c> reserves the path and writes the <c>.owner</c> sidecar that
    /// lets a later runner start reclaim a killed host's leftovers, but it deliberately does NOT
    /// create the leaf — see <c>TestScratch.cs</c>, where some callers rely on observing whether
    /// the runner created it. These tests hand the path to <c>BuildDotNetProbingPaths</c>, which
    /// filters on <c>Directory.Exists</c>, so the directory has to be real: hence the explicit
    /// create here rather than an assumption that the helper did it.</para>
    /// </summary>
    private static string OwnedDir(string prefix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        return dir;
    }

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
    /// The second shim, and the one that makes Base Application emittable at all (#3876).
    ///
    /// <para>Base Application's <c>src/Modules/System/DotNetAliases/dotnet.al</c> line 23
    /// declares <c>assembly(Microsoft.AspNetCore.StaticFiles)</c> for
    /// <c>FileExtensionContentTypeProvider</c>. BC ships no copy of that assembly in its
    /// artifacts, so the declaration phase raised AL0451 + AL0185 and the app produced ZERO
    /// objects — recorded in three places as a permanent blocker. It is not: the assembly is an
    /// ordinary part of the ASP.NET Core reference pack.</para>
    ///
    /// <para>Asserted on the real build output, like the Newtonsoft case above and for the same
    /// reason: a test staging its own copy would pass with the csproj item deleted. The
    /// <c>FileExtensionContentTypeProvider</c> assertion is what stops a same-named file
    /// satisfying this — it is the one type the AL declaration names, so a file without it
    /// would leave AL0185 in place while the filename assertion still went green.</para>
    /// </summary>
    [Fact]
    public void BuildStagesTheAspNetCoreStaticFilesShimBesideTheRunnerBinary()
    {
        var dir = Path.Combine(RunnerOutputDir(), "dotnet-shims");
        Assert.True(Directory.Exists(dir), $"no dotnet-shims directory at {dir}");

        var shim = Path.Combine(dir, "Microsoft.AspNetCore.StaticFiles.dll");
        Assert.True(File.Exists(shim),
            $"Microsoft.AspNetCore.StaticFiles.dll missing from {dir}. AlRunner.csproj stages it " +
            "from the Microsoft.AspNetCore.App.Ref package; without it Base Application's " +
            "metadata emit yields zero objects (#3876).");

        var name = AssemblyName.GetAssemblyName(shim);
        Assert.Equal("Microsoft.AspNetCore.StaticFiles", name.Name);

        // The type BC's dotnet.al actually names. A same-named assembly lacking it would still
        // leave AL0185 'FileExtensionContentTypeProvider is missing' and emit nothing.
        var text = File.ReadAllText(shim, System.Text.Encoding.Latin1);
        Assert.Contains("FileExtensionContentTypeProvider", text);
    }

    /// <summary>
    /// The reference pack is NOT an SDK component, and that is the whole reason the csproj takes
    /// it as a <c>PackageReference</c> rather than as a path.
    ///
    /// <para>A CI leg installs the SDK with <c>actions/setup-dotnet</c>, which lays down
    /// <c>Microsoft.NETCore.App.Ref</c> and <c>NETStandard.Library.Ref</c> under
    /// <c>$DOTNET_ROOT/packs</c> and does NOT lay down <c>Microsoft.AspNetCore.App.Ref</c>. So
    /// <see cref="BcCompiler"/>'s <c>EnumerateDotNetRefAssemblyDirs</c>, which reads exactly that
    /// <c>packs</c> directory, can never supply this assembly — the staged shim is the only
    /// route, and a future edit that "simplifies" the shim away in favour of the ref-pack
    /// enumeration would pass on a developer box and fail on CI.</para>
    ///
    /// <para>This asserts the mechanism, not the machine: it pins that the ref-pack enumeration
    /// does not yield an AspNetCore directory, which is what makes the shim load-bearing. It is
    /// deliberately not an assertion that the pack is absent from this box, because a developer
    /// who has restored it into <c>~/.nuget</c> still gets nothing from
    /// <c>EnumerateDotNetRefAssemblyDirs</c>.</para>
    /// </summary>
    [Fact]
    public void TheRefAssemblyEnumerationNeverSuppliesAspNetCore_SoTheShimIsTheOnlyRoute()
    {
        var m = typeof(BcCompiler).GetMethod(
                    "EnumerateDotNetRefAssemblyDirs", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BcCompiler.EnumerateDotNetRefAssemblyDirs not found. If it was renamed, this " +
                    "test and the reasoning behind the AspNetCore shim need to move with it.");
        var refDirs = ((IEnumerable<string>)m.Invoke(null, null)!).ToList();

        Assert.DoesNotContain(refDirs,
            d => d.Replace('\\', '/').Contains("/Microsoft.AspNetCore.App.Ref/",
                     StringComparison.OrdinalIgnoreCase));

        // And the assembly is not reachable from any directory that enumeration does yield —
        // the positive half, so this cannot pass merely because the enumeration came back empty.
        foreach (var d in refDirs)
            Assert.False(File.Exists(Path.Combine(d, "Microsoft.AspNetCore.StaticFiles.dll")),
                $"{d} carries Microsoft.AspNetCore.StaticFiles.dll — if the SDK now ships the " +
                "ASP.NET Core reference pack, the staged shim may be redundant; re-measure " +
                "before removing it (#3876).");
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
    /// The runner and the ground-truth generator must stage the SAME shim set.
    ///
    /// <para>Two projects build a DotNet probing list — <c>AlRunner.csproj</c> for the runner and
    /// <c>tools/metadata-ground-truth/MetadataGroundTruth.csproj</c> for the generator — and the
    /// generator's output is the ORACLE the runner's metadata derivation is measured against
    /// (<c>MetadataEquivalenceHarnessTests</c>). A shim present in one and not the other means
    /// the two sides compile the same Microsoft app against different reference sets, so a
    /// difference the harness reports would be an artifact of the drift rather than a defect in
    /// the runner — and it would look exactly like a real finding.</para>
    ///
    /// <para>Asserted against the csproj SOURCES rather than two build outputs, because the
    /// generator is deliberately not in <c>AlRunner.slnx</c> and is not built by
    /// <c>dotnet test</c>: there is no output to compare on a normal run. #3745 staged the first
    /// shim in both; #3876 added the second and this test, because nothing was holding them
    /// together.</para>
    /// </summary>
    [Fact]
    public void TheRunnerAndTheGroundTruthGeneratorStageTheSameShimSet()
    {
        var root = RepoRoot();
        var runner = File.ReadAllText(Path.Combine(root, "AlRunner", "AlRunner.csproj"));
        var generator = File.ReadAllText(Path.Combine(
            root, "tools", "metadata-ground-truth", "MetadataGroundTruth.csproj"));

        // The shim files each project stages, by the file name that lands in dotnet-shims.
        string[] expected = { "Newtonsoft.Json.dll", "Microsoft.AspNetCore.StaticFiles.dll" };

        foreach (var dll in expected)
        {
            Assert.True(runner.Contains(dll, StringComparison.Ordinal),
                $"AlRunner.csproj does not stage {dll} into dotnet-shims.");
            Assert.True(generator.Contains(dll, StringComparison.Ordinal),
                $"MetadataGroundTruth.csproj does not stage {dll} into dotnet-shims. The " +
                "generator produces the ground truth the runner is measured against, so a shim " +
                "in one project and not the other compares two different reference sets (#3876).");
        }

        // And the same package versions, so the two do not bind different builds of one assembly.
        foreach (var pkg in new[] { "Newtonsoft.Json", "Microsoft.AspNetCore.App.Ref" })
        {
            var v1 = PackageVersion(runner, pkg);
            var v2 = PackageVersion(generator, pkg);
            Assert.Equal(v1, v2);
        }
    }

    /// <summary>
    /// The <c>Version</c> of a <c>PackageReference</c>, read out of csproj text. Deliberately a
    /// regex over the source rather than an MSBuild evaluation: the point is to compare what the
    /// two files DECLARE, and an evaluation would need the generator project restored, which a
    /// normal <c>dotnet test</c> run does not do.
    /// </summary>
    private static string PackageVersion(string csproj, string package)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            csproj,
            "<PackageReference\\s+Include=\"" + System.Text.RegularExpressions.Regex.Escape(package) +
            "\"\\s+Version=\"([^\"]+)\"");
        Assert.True(m.Success, $"no <PackageReference Include=\"{package}\" Version=...> found");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// The repository root, walked up from the test assembly's location by looking for a marker
    /// that exists in a checkout and nowhere else — so this works under any configuration or
    /// output layout, like <see cref="RunnerOutputDir"/> above.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlRunner.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
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
        var probeDir = OwnedDir("al-runner-shim-order");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", probeDir);

            var paths = InvokeProbingPaths();
            var shimIdx = paths.FindIndex(
                p => string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),
                                   probeDir.TrimEnd(Path.DirectorySeparatorChar),
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
            try { Directory.Delete(probeDir, recursive: true); } catch { /* best effort */ }
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
        var a = OwnedDir("al-runner-shim-a");
        var b = OwnedDir("al-runner-shim-b");
        // Deliberately NOT a TestScratch path, and allowlisted in ScratchDirOwnershipGuardTests
        // for that reason: this path's whole point is that nothing is there. Reserving it would
        // create the parent and drop a .owner sidecar beside a path the assertion below measures
        // the ABSENCE of, which is the guard's own "a path that must NOT exist" category.
        var missing = Path.Combine(Path.GetTempPath(), "al-runner-shim-absent-" + Guid.NewGuid());
        try
        {
            Environment.SetEnvironmentVariable(
                "AL_RUNNER_DOTNET_SHIMS",
                string.Join(Path.PathSeparator, a, missing, b));

            var yielded = ShimDirs().Select(p => p.TrimEnd(Path.DirectorySeparatorChar)).ToList();
            Assert.Equal(a.TrimEnd(Path.DirectorySeparatorChar), yielded[0]);
            Assert.Equal(missing.TrimEnd(Path.DirectorySeparatorChar), yielded[1]);
            Assert.Equal(b.TrimEnd(Path.DirectorySeparatorChar), yielded[2]);

            // The enumeration yields candidates; the caller filters. A directory that does not
            // exist must not reach the assembled probing paths.
            var paths = InvokeProbingPaths().Select(Path.GetFullPath).ToList();
            Assert.Contains(Path.GetFullPath(a), paths);
            Assert.Contains(Path.GetFullPath(b), paths);
            Assert.DoesNotContain(Path.GetFullPath(missing), paths);

            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", "");
            var defaults = ShimDirs();
            Assert.DoesNotContain(a.TrimEnd(Path.DirectorySeparatorChar),
                defaults.Select(p => p.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.All(defaults, p => Assert.Equal("dotnet-shims", Path.GetFileName(p)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
            try { Directory.Delete(a, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(b, recursive: true); } catch { /* best effort */ }
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
        var probeDir = OwnedDir("al-runner-shim-dup");
        try
        {
            // The same directory twice, plus a trailing-separator spelling of it, which is the
            // shape the shadow-copy case actually produces.
            Environment.SetEnvironmentVariable(
                "AL_RUNNER_DOTNET_SHIMS",
                string.Join(Path.PathSeparator,
                    probeDir,
                    probeDir,
                    probeDir + Path.DirectorySeparatorChar));

            var paths = InvokeProbingPaths();
            var matches = paths.Count(p => string.Equals(
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),
                probeDir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal));
            Assert.Equal(1, matches);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DOTNET_SHIMS", null);
            try { Directory.Delete(probeDir, recursive: true); } catch { /* best effort */ }
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
