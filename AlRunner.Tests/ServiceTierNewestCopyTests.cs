// ServiceTierNewestCopyTests — the service-tier closure keeps the NEWEST of equal-depth duplicates (#5382).
//
// BC's platform artifact carries some assemblies twice at the same depth: Admin/ and SideServices/
// (and Management/ on BC 27). They are different builds of one assembly. The downloader used to keep the
// first one listed, which is Admin/'s — the older. BC 29's Microsoft.Extensions.Http exists only in
// SideServices/ and references Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0.10, while the
// Admin/ copy is 10.0.0.0, so the closure held a copy that could not satisfy its own sibling and every
// HttpClient surface died with FileLoadException.
//
// The selection is a pure function over the extracted bytes, so it is pinned here without a network:
//   * the higher AssemblyVersion wins, whichever is listed first — an implementation that keeps the first
//     listed (the old behaviour) passes when the newer one is first and fails when it is second;
//   * equals keep the first listed, so a re-provision of an unchanged build is byte-stable;
//   * a copy that is not a readable assembly loses to one that is, and nothing extracted is -1.
using System.Text;
using AlRunner.Provisioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServiceTierNewestCopyTests
{
    private static byte[] Assembly(string version)
    {
        var tree = CSharpSyntaxTree.ParseText(
            $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] public class C {{}}");
        var compilation = CSharpCompilation.Create(
            "Probe",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return ms.ToArray();
    }

    [Fact]
    public void ReadAssemblyVersion_ReturnsTheDeclaredVersion_AndNullForNonAssemblies()
    {
        Assert.Equal(new Version(10, 0, 0, 10), ArtifactDownloader.ReadAssemblyVersion(Assembly("10.0.0.10")));
        Assert.Null(ArtifactDownloader.ReadAssemblyVersion(Encoding.UTF8.GetBytes("not a PE file")));
        Assert.Null(ArtifactDownloader.ReadAssemblyVersion(Array.Empty<byte>()));
    }

    [Fact]
    public void TheHigherVersionWins_WhicheverIsListedFirst()
    {
        var older = Assembly("10.0.0.0");
        var newer = Assembly("10.0.0.10");

        Assert.Equal(1, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { older, newer }));
        Assert.Equal(0, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { newer, older }));
    }

    [Fact]
    public void EqualVersions_KeepTheFirstListed()
    {
        var a = Assembly("8.0.0.0");
        var b = Assembly("8.0.0.0");

        Assert.Equal(0, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { a, b }));
    }

    [Fact]
    public void AnUnreadableCopyLosesToAReadableOne_AndNothingExtractedIsMinusOne()
    {
        var junk = Encoding.UTF8.GetBytes("not a PE file");
        var real = Assembly("1.2.3.4");

        Assert.Equal(1, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { junk, real }));
        Assert.Equal(1, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { null, junk }));
        Assert.Equal(-1, ArtifactDownloader.IndexOfNewestAssembly(new byte[]?[] { null, null }));
        Assert.Equal(-1, ArtifactDownloader.IndexOfNewestAssembly(Array.Empty<byte[]?>()));
    }
}
