// What al-runner reports about its own version, and why it is not what the assembly's
// numeric version says.
//
// These are unit tests over AlRunner.Infrastructure.RunnerVersion with a supplied assembly,
// not over the running one: the running assembly carries whatever <Version> the repo happens
// to be at, so asserting against it would pin a moving number and prove nothing about the
// rule. Constructing assemblies with each attribute combination is what states the rule.

using System.Reflection;
using System.Reflection.Emit;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerVersionTests
{
    /// <summary>
    /// An in-memory assembly carrying exactly the version attributes named, so each test can
    /// state one rule without depending on how this repo is currently versioned.
    /// </summary>
    private static Assembly Build(string name, string? numeric, string? informational)
    {
        var asmName = new AssemblyName(name);
        if (numeric != null) asmName.Version = Version.Parse(numeric);

        var builder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        if (informational != null)
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!,
                new object[] { informational }));
        return builder;
    }

    // The defect: AssemblyVersion is a numeric quad and .NET drops a prerelease suffix from
    // it, so a 2.0.0-preview.1 build reported "2.0.0.0" and a fork build stamped
    // 2.1.2-performance reported the same thing. The suffix is the part that says which
    // build someone is holding.
    [Fact]
    public void InformationalVersion_KeepsThePrereleaseSuffix()
        => Assert.Equal(
            "2.0.0-preview.1",
            RunnerVersion.Informational(Build("V1", "2.0.0.0", "2.0.0-preview.1")));

    [Fact]
    public void InformationalVersion_KeepsANonNumericSuffix()
        => Assert.Equal(
            "2.1.2-performance",
            RunnerVersion.Informational(Build("V2", "2.1.0.0", "2.1.2-performance")));

    // SemVer build metadata is dropped. publish.yml packs the release with
    // IncludeSourceRevisionInInformationalVersion=true, which appends the 40-character commit
    // sha; a released version number already identifies its build, and --guide asks coding
    // agents to paste this line into gap reports.
    [Fact]
    public void BuildMetadata_IsStripped()
        => Assert.Equal(
            "2.11.0",
            RunnerVersion.Informational(
                Build("V3", "2.11.0.0", "2.11.0+0123456789abcdef0123456789abcdef01234567")));

    // The same source must report the same string whether it was built here or packed for
    // release. That is the whole reason for stripping rather than printing the sha.
    [Fact]
    public void ThePackedAndUnpackedFormsOfOneVersion_ReportTheSameString()
    {
        var packed = RunnerVersion.Informational(
            Build("V4", "2.11.0.0", "2.11.0-rc.1+0123456789abcdef0123456789abcdef01234567"));
        var unpacked = RunnerVersion.Informational(Build("V5", "2.11.0.0", "2.11.0-rc.1"));

        Assert.Equal("2.11.0-rc.1", packed);
        Assert.Equal(packed, unpacked);
    }

    // Negative: no informational attribute at all falls back to the numeric version rather
    // than reporting nothing.
    [Fact]
    public void NoInformationalAttribute_FallsBackToTheNumericVersion()
        => Assert.Equal("3.4.5.6", RunnerVersion.Informational(Build("V6", "3.4.5.6", null)));

    // Negative: an informational attribute that is present but blank must not win over the
    // numeric version, and must not produce an empty version line.
    [Fact]
    public void BlankInformationalAttribute_FallsBackToTheNumericVersion()
        => Assert.Equal("3.4.5.6", RunnerVersion.Informational(Build("V7", "3.4.5.6", "   ")));

    // Negative: an informational version that is nothing but build metadata has no version in
    // it, so it must fall back rather than report an empty string.
    [Fact]
    public void InformationalVersionThatIsOnlyBuildMetadata_FallsBackToTheNumericVersion()
        => Assert.Equal("3.4.5.6", RunnerVersion.Informational(Build("V8", "3.4.5.6", "+abcdef")));
}
