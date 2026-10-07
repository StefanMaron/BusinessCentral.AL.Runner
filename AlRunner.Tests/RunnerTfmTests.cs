// RunnerTfmTests — the target framework follows the BC major the build is made against (#5382).
//
// BC 29's service tier is a .NET 10 product (Ncl.dll's TargetFrameworkAttribute is
// .NETCoreApp,Version=v10.0 and it references System.Runtime 10.0.0.0, which a net8 process cannot bind),
// while BC 27 and 28 stay on net8.0. One property, RunnerTfm in Directory.Build.props, decides, and
// everything that loads the service tier in-process (AlRunner, AlRunner.Tests) follows it. These
// evaluate the real projects with the real props rather than reading the file, because a regex over
// the props file would pass with the property wired to nothing.
//
// What each case rules out:
//   * 28.x on net10.0 — dropping BC 27/28 support to a runtime BC 28's UnsafeAccessor assumptions break on;
//   * 29 on net8.0   — the original defect: the engine cannot load at all;
//   * the BC-free libraries following the cut-over — they are referenced by the net10 build and must
//     stay loadable by the net8 one, and AlRunner.QueryJoin reflects over BC types by name only.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerTfmTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Evaluate(string project, params string[] properties)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        foreach (var a in new[] { "msbuild", project, "-getProperty:TargetFramework", "-nologo" }) psi.ArgumentList.Add(a);
        foreach (var p in properties) psi.ArgumentList.Add(p);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "dotnet msbuild hung");
        Assert.True(proc.ExitCode == 0, $"dotnet msbuild failed ({proc.ExitCode}): {stderr}{stdout}");
        return stdout.Trim();
    }

    [Theory]
    [InlineData("27.0.38460.53260", "net8.0")]
    [InlineData("28.5.54151.55132", "net8.0")]
    [InlineData("28.0", "net8.0")]
    [InlineData("29.0.54011.55816", "net10.0")]
    [InlineData("29.0", "net10.0")]
    [InlineData("30.1", "net10.0")]
    public void TheRunnerTargetsTheFrameworkOfItsBcMajor(string bcVersion, string expected)
    {
        Assert.Equal(expected, Evaluate("AlRunner/AlRunner.csproj", $"-p:_BCVersion={bcVersion}"));
    }

    [Fact]
    public void TheTestProjectFollowsTheRunner_BecauseItLoadsTheSameServiceTierInProcess()
    {
        Assert.Equal("net8.0", Evaluate("AlRunner.Tests/AlRunner.Tests.csproj", "-p:_BCVersion=28.5.54151.55132"));
        Assert.Equal("net10.0", Evaluate("AlRunner.Tests/AlRunner.Tests.csproj", "-p:_BCVersion=29.0.54011.55816"));
    }

    [Fact]
    public void TheBcFreeLibraries_StayOnNet8_WhateverTheBcVersion()
    {
        Assert.Equal("net8.0", Evaluate("AlRunner.Provisioning/AlRunner.Provisioning.csproj", "-p:_BCVersion=29.0.54011.55816"));
        Assert.Equal("net8.0", Evaluate("AlRunner.QueryJoin/AlRunner.QueryJoin.csproj", "-p:_BCVersion=29.0.54011.55816"));
    }

    [Fact]
    public void AnExplicitRunnerTfm_Wins()
    {
        Assert.Equal("net9.0", Evaluate("AlRunner/AlRunner.csproj", "-p:_BCVersion=29.0.54011.55816", "-p:RunnerTfm=net9.0"));
    }
}
