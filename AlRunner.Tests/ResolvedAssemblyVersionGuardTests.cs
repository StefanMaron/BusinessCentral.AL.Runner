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

    // End to end through the handler DependencyLoader installs. Microsoft.Dynamics.Nav.Service.Dev
    // is the developer endpoint: nothing in-process loads it, so the request reaches the handler.
    private const string UnloadedArtifactAssembly = "Microsoft.Dynamics.Nav.Service.Dev";

    [SkippableFact]
    public void ServiceTierHandler_RefusesAnArtifactFileOlderThanTheRequest()
    {
        var (probe, fileVersion) = ArtifactProbe();
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            a => a.GetName().Name == UnloadedArtifactAssembly);

        var requested = new Version(fileVersion.Major + 1, 0, 0, 0);
        var request = new AssemblyName(
            $"{UnloadedArtifactAssembly}, Version={requested}, Culture=neutral, PublicKeyToken=31bf3856ad364e35");

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
