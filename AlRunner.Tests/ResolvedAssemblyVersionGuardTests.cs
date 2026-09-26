// #4569: the Default-ALC Resolving handlers serve an assembly by simple name, and the runtime
// binds whatever a handler returns without checking its version. These tests pin the rule
// (serve when the file is at least the requested version, refuse loudly otherwise) and that the
// service-tier handler in DependencyLoader applies it. Measurement: docs/assembly-resolution.md.

using System.Reflection;
using System.Runtime.Loader;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResolvedAssemblyVersionGuardTests
{
    private static AssemblyName Request(string name, string? version)
        => new(version == null ? name : $"{name}, Version={version}");

    [Fact]
    public void OlderFileThanRequested_IsRefused_NamingBothVersionsAndThePath()
    {
        var ex = Assert.Throws<FileLoadException>(() =>
            ResolvedAssemblyVersionGuard.EnsureSatisfies(
                Request("Some.Lib", "8.0.0.1"), new Version(8, 0, 0, 0), "/artifacts/Some.Lib.dll"));

        Assert.Contains("Some.Lib, Version=8.0.0.1", ex.Message);
        Assert.Contains("version 8.0.0.0", ex.Message);
        Assert.Contains("/artifacts/Some.Lib.dll", ex.Message);
        Assert.Contains("older than the requested 8.0.0.1", ex.Message);
        Assert.Equal("Some.Lib, Version=8.0.0.1", ex.FileName);
    }

    [Theory]
    [InlineData("17.0.40.3339", 17, 0, 41, 14626)] // measured: CodeAnalysis requested below the artifact's copy
    [InlineData("8.0.0.0", 10, 0, 0, 0)]           // measured: Microsoft.Extensions.Logging 8 served 10
    [InlineData("28.0.0.0", 28, 0, 0, 0)]          // the ordinary equal case
    public void EqualOrNewerFile_IsServed(string requested, int a, int b, int c, int d)
        => ResolvedAssemblyVersionGuard.EnsureSatisfies(
            Request("Some.Lib", requested), new Version(a, b, c, d), "/artifacts/Some.Lib.dll");

    // Microsoft.Dynamics.* is the BC platform: the runner serves the SELECTED build's copy to every
    // caller, whatever build the caller was compiled against, so its version is never a refusal.
    [Theory]
    [InlineData("Microsoft.Dynamics.Nav.CodeAnalysis", "17.0.40.3339", 17, 0, 39, 53543)] // measured: 28.1.49838.53249
    [InlineData("Microsoft.Dynamics.Nav.CodeAnalysis", "17.0.40.3339", 17, 0, 0, 0)]
    // measured on CI run 36124578301 (BC 27.0.38460.55036 and 27.5.46862.55139): runner-extras'
    // precompiled dependency apps reference Ncl 28.0.0.0 and are served 27.0.0.0; refusing it failed
    // 25 tests in nine codeunits (65701, 65871, ...) that pass on main.
    [InlineData("Microsoft.Dynamics.Nav.Ncl", "28.0.0.0", 27, 0, 0, 0)]
    [InlineData("Microsoft.Dynamics.Nav.Types", "28.0.0.0", 27, 0, 0, 0)]
    public void DynamicsAssembly_OlderBuild_IsServed(string name, string requested, int a, int b, int c, int d)
        => ResolvedAssemblyVersionGuard.EnsureSatisfies(
            Request(name, requested), new Version(a, b, c, d), "/artifacts/x.dll");

    [Fact]
    public void NonDynamicsAssembly_OlderBuildOfTheSameMajor_IsStillRefused()
        => Assert.Throws<FileLoadException>(() => ResolvedAssemblyVersionGuard.EnsureSatisfies(
            Request("Microsoft.Dynamics", "17.0.40.3339"), new Version(17, 0, 39, 0), "/a.dll"));

    [Fact]
    public void UnversionedRequest_IsServed()
        // measured: `Microsoft.BusinessCentral.SystemApp` arrives with no version at all
        => ResolvedAssemblyVersionGuard.EnsureSatisfies(
            Request("Microsoft.BusinessCentral.SystemApp", null), new Version(28, 0, 0, 0), "/x.dll");

    [Fact]
    public void LoadedAssembly_OlderThanRequested_IsRefused_NewerOrEqualIsReturned()
    {
        var asm = typeof(ResolvedAssemblyVersionGuard).Assembly;
        var v = asm.GetName().Version!;
        var higher = new Version(v.Major + 1, 0, 0, 0);

        Assert.Same(asm, ResolvedAssemblyVersionGuard.EnsureSatisfies(Request(asm.GetName().Name!, v.ToString()), asm));
        var ex = Assert.Throws<FileLoadException>(() =>
            ResolvedAssemblyVersionGuard.EnsureSatisfies(Request(asm.GetName().Name!, higher.ToString()), asm));
        Assert.Contains($"older than the requested {higher}", ex.Message);
    }

    // #4725: reading a file's version (AssemblyName.GetAssemblyName) loads System.Reflection.Metadata,
    // and when that load itself reaches the handler, the handler reads another version: unbounded
    // recursion and a `Stack overflow.` exit 134. The two tests below drive the version reader as a seam.

    [Fact]
    public void UnversionedRequest_IsLoadedWithoutReadingTheFileVersion()
    {
        var reads = 0;
        var loaded = new List<string>();
        var asm = ResolvedAssemblyVersionGuard.LoadIfSatisfies(
            Request("System.Reflection.Metadata", null), "/artifacts/System.Reflection.Metadata.dll",
            _ => { reads++; return new Version(9, 0, 0, 0); },
            path => { loaded.Add(path); return typeof(ResolvedAssemblyVersionGuardTests).Assembly; });

        Assert.Equal(0, reads);
        Assert.Equal(new[] { "/artifacts/System.Reflection.Metadata.dll" }, loaded);
        Assert.Same(typeof(ResolvedAssemblyVersionGuardTests).Assembly, asm);
    }

    [Fact]
    public void VersionRead_ThatReentersTheGuard_FailsLoudlyInsteadOfRecursing()
    {
        var depth = 0;
        Version? ReenteringRead(string path)
        {
            // Bounded, so a missing guard shows up as a wrong answer rather than a crashed test host.
            if (++depth > 5) return new Version(99, 0, 0, 0);
            ResolvedAssemblyVersionGuard.LoadIfSatisfies(
                Request("Some.Metadata.Reader", "9.0.0.0"), "/artifacts/Some.Metadata.Reader.dll",
                ReenteringRead, _ => typeof(ResolvedAssemblyVersionGuardTests).Assembly);
            return new Version(99, 0, 0, 0);
        }

        var ex = Assert.Throws<FileLoadException>(() => ResolvedAssemblyVersionGuard.LoadIfSatisfies(
            Request("Some.Lib", "8.0.0.0"), "/artifacts/Some.Lib.dll",
            ReenteringRead, _ => typeof(ResolvedAssemblyVersionGuardTests).Assembly));

        Assert.Equal(1, depth);
        Assert.Contains("Some.Metadata.Reader, Version=9.0.0.0", ex.Message);
        Assert.Contains("Some.Lib, Version=8.0.0.0", ex.Message);
        Assert.Contains("re-entered", ex.Message);

        // The guard is released afterwards: an ordinary request on this thread is served again.
        ResolvedAssemblyVersionGuard.LoadIfSatisfies(
            Request("Some.Lib", "8.0.0.0"), "/artifacts/Some.Lib.dll",
            _ => new Version(8, 0, 0, 0), _ => typeof(ResolvedAssemblyVersionGuardTests).Assembly);
    }

    // End to end through the handler DependencyLoader installs. Both assemblies ship in every artifact
    // directory and nothing in-process loads them, so each request reaches the handler.
    private const string UnloadedArtifactAssembly = "Azure.Messaging.ServiceBus";
    private const string UnloadedDynamicsAssembly = "Microsoft.Dynamics.Nav.Service.Dev";

    [SkippableFact]
    public void ServiceTierHandler_RefusesAnArtifactFileOlderThanTheRequest()
    {
        var (probe, fileVersion) = ArtifactProbe();
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            a => a.GetName().Name == UnloadedArtifactAssembly);

        var requested = new Version(fileVersion.Major + 1, 0, 0, 0);
        var request = new AssemblyName($"{UnloadedArtifactAssembly}, Version={requested}");

        // The runtime wraps a handler's exception in its own generic 0x80131621 FileLoadException,
        // so the diagnosis is the inner one.
        var outer = Assert.Throws<FileLoadException>(() => Assembly.Load(request));
        var ex = Assert.IsType<FileLoadException>(outer.InnerException);
        Assert.Contains($"older than the requested {requested}", ex.Message);
        Assert.Contains($"version {fileVersion}", ex.Message);
        Assert.Contains(probe, ex.Message);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            a => a.GetName().Name == UnloadedArtifactAssembly);
    }

    [SkippableFact]
    public void ServiceTierHandler_StillServesAnArtifactFileNewerThanTheRequest()
    {
        // A different assembly from the refusal test, so neither depends on the other's order.
        var (probe, _) = ArtifactProbe("Microsoft.Dynamics.Nav.Service.SOAP");
        var asm = Assembly.Load(new AssemblyName(
            "Microsoft.Dynamics.Nav.Service.SOAP, Version=0.0.0.1, Culture=neutral, PublicKeyToken=31bf3856ad364e35"));
        Assert.Equal(Path.GetFullPath(probe), Path.GetFullPath(asm.Location));
    }

    [SkippableFact]
    public void ServiceTierHandler_ServesADynamicsArtifactFileOfAnOlderMajorThanTheRequest()
    {
        // The shape of a precompiled dependency built on a newer BC major than the one selected.
        var (probe, fileVersion) = ArtifactProbe(UnloadedDynamicsAssembly);
        var asm = Assembly.Load(new AssemblyName(
            $"{UnloadedDynamicsAssembly}, Version={fileVersion.Major + 1}.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"));
        Assert.Equal(Path.GetFullPath(probe), Path.GetFullPath(asm.Location));
    }

    private static (string Probe, Version FileVersion) ArtifactProbe(string simpleName = UnloadedArtifactAssembly)
    {
        string dir = string.Empty;
        string? selectionError = null;
        try { dir = BcArtifacts.ServiceTierDir; }
        catch (Exception e) { selectionError = e.Message; }
        TestArtifacts.SkipIf(selectionError != null,
            $"no BC service-tier artifact directory could be selected: {selectionError}");
        var probe = Path.Combine(dir, simpleName + ".dll");
        TestArtifacts.SkipIf(!File.Exists(probe), $"BC artifacts are incomplete: '{probe}' does not exist.");
        DependencyLoader.EnsureResolverInstalled_Public();
        return (probe, AssemblyName.GetAssemblyName(probe).Version!);
    }
}
