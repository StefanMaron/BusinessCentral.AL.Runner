// DependencyMetadataProducerTests — the runner-side mechanism behind #3549.
//
// WHAT IS PINNED HERE, AND WHAT IS NOT
//   The BC-behaviour half — that a dependency table's key count and SystemCreatedBy relation
//   come out as BC's own answer — is not a claim this file can make: it needs BC's compiler and
//   a loaded runtime. It was proven by running the runner against Business Foundation
//   (docs/dependency-metadata-from-bc.md records the RED/GREEN and the exact values), and the
//   route itself is already covered by TableMetadataFromBcDocumentTests.
//
//   What IS provable without a BC runtime, and is what a regression would break first, is the
//   producer's decision logic: which apps it refuses to compile, what its cache key separates,
//   and — the one that matters most — that the opt-in cannot be switched on by accident.
//
//   The availability-vs-failure split (`.claude/rules/loud-failures.md`) is the property this
//   whole class exists to protect: a dependency with no source must be a quiet 0, and a
//   dependency whose compile failed must throw. Those two answers must never be spelled the
//   same way, which NoSource_ReturnsZero_WhileFailedEmit_Throws asserts by DRIVING both arms.
//
//   That test used to assert over reflection metadata -- that Ensure returns int and Loud
//   returns an exception type -- and #3749 found it would still have passed if Ensure swallowed
//   its own throw, which is the one defect it was named for. A null compiler is what makes the
//   emit fail without a BC runtime, so the conversion under test runs for real.

using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyMetadataProducerTests
{
    private static AppManifest Manifest(string name, string version = "1.0.0.0") =>
        new(Publisher: "Microsoft", Name: name, Version: Version.Parse(version),
            AppId: Guid.Parse("f3552374-a1f2-4356-848e-196002525837"),
            Dependencies: Array.Empty<DependencyRef>());

    // ---- the opt-in ----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void FeatureIsOffUnlessExplicitlyEnabled(string? value)
    {
        using var _ = new EnvVar("AL_RUNNER_DEP_METADATA_FROM_BC", value);
        Assert.Null(DependencyLoader.DependencyMetadataAppFilter());
        Assert.False(DependencyLoader.DependencyMetadataEnabled());
    }

    /// <summary>
    /// "1" means every app; an empty filter is how "no filter" is spelled. Asserted as the
    /// empty collection rather than as "enabled", because a non-null EMPTY filter and a null
    /// filter are the two states a caller must not confuse — one compiles everything, the other
    /// compiles nothing.
    /// </summary>
    [Fact]
    public void One_EnablesEveryApp()
    {
        using var _ = new EnvVar("AL_RUNNER_DEP_METADATA_FROM_BC", "1");
        var filter = DependencyLoader.DependencyMetadataAppFilter();
        Assert.NotNull(filter);
        Assert.Empty(filter!);
        Assert.True(DependencyLoader.DependencyMetadataEnabled());
    }

    /// <summary>
    /// A named list restricts the producer to those apps. This is not a convenience: BC's
    /// Compilation.Emit is atomic per module, so one object it cannot emit zeroes the whole
    /// app — System Application's `Business Chart.Initialize()` does exactly that under the
    /// runner's .NET probing paths, while Business Foundation compiles clean. See #3745.
    /// </summary>
    [Theory]
    [InlineData("Business Foundation", new[] { "Business Foundation" })]
    [InlineData("Business Foundation,System Application", new[] { "Business Foundation", "System Application" })]
    [InlineData(" Business Foundation , System Application ", new[] { "Business Foundation", "System Application" })]
    public void NamedList_RestrictsToThoseApps(string value, string[] expected)
    {
        using var _ = new EnvVar("AL_RUNNER_DEP_METADATA_FROM_BC", value);
        var filter = DependencyLoader.DependencyMetadataAppFilter();
        Assert.NotNull(filter);
        Assert.Equal(expected, filter!);
    }

    // ---- exclusions ----------------------------------------------------------------

    /// <summary>
    /// Base Application is excluded on COST — 257s and 8.83 GiB peak RSS on the dependency-load
    /// path, against ~6s for Business Foundation and ~13s for System Application.
    ///
    /// <para>This doc comment used to say the emit was impossible without a
    /// <c>PublicKeyToken=null</c> copy of <c>Microsoft.AspNetCore.StaticFiles</c> that no BC
    /// artifact ships. #3876 disproved that: the assembly ships in the ASP.NET Core reference
    /// pack, both csprojs now stage it, and the emit produces 7,842 documents with
    /// <c>errors=0</c>. The assertion below is unchanged — only the reason behind it is — and
    /// removing the exclusion is now a sizing decision rather than a blocked one.</para>
    /// </summary>
    [Fact]
    public void BaseApplication_IsNeverCompiled()
    {
        Assert.True(DependencyMetadataProducer.IsExcluded(Manifest("Base Application")));
        Assert.True(DependencyMetadataProducer.IsExcluded(Manifest("base application")));
        Assert.False(DependencyMetadataProducer.IsExcluded(Manifest("Business Foundation")));
        Assert.False(DependencyMetadataProducer.IsExcluded(Manifest("System Application")));
    }

    /// <summary>
    /// An excluded app returns 0 WITHOUT touching the package, so the exclusion cannot be
    /// defeated by a package that would throw on read. A path that does not exist is the
    /// cheapest way to prove nothing read it: any code that opened it would throw.
    /// </summary>
    [Fact]
    public void ExcludedApp_ReturnsZeroWithoutReadingThePackage()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-package-{Guid.NewGuid():N}.app");
        Assert.False(File.Exists(missing));
        Assert.Equal(0, DependencyMetadataProducer.Ensure(Manifest("Base Application"), missing, compiler: null!));
    }

    // ---- the cache key -------------------------------------------------------------

    /// <summary>
    /// The key separates the three things that change the documents: which app, which version
    /// of it, and which BC build emitted them. The BC version is the one an implementer is
    /// tempted to leave out, and leaving it out serves one BC build's metadata to another.
    /// </summary>
    [Fact]
    public void CacheKey_SeparatesAppAndVersion()
    {
        var a = DependencyMetadataProducer.CacheKey(Manifest("Business Foundation", "28.1.49838.54308"));
        var b = DependencyMetadataProducer.CacheKey(Manifest("Business Foundation", "28.2.0.0"));
        Assert.NotEqual(a, b);
        Assert.Contains("28.1.49838.54308", a);
        // The selected BC build is part of every key, so two runs against different artifacts
        // never share an entry.
        Assert.Contains(AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString(), a);
        Assert.Equal(a, DependencyMetadataProducer.CacheKey(Manifest("Business Foundation", "28.1.49838.54308")));
    }

    // ---- availability vs failure ---------------------------------------------------

    /// <summary>
    /// A package the reader cannot open at all is a LOUD failure, not an absence.
    /// <c>ReadSource</c> is the seam that separates the two (#3748): it lets
    /// <c>ExtractAlWithPaths</c>'s empty list mean "symbol-only, nothing to produce" and turns
    /// a read that THREW into <c>METADATA-SOURCE-UNREADABLE</c>. Asserted by driving
    /// <c>Ensure</c> for real, so it fails if the throw is ever softened to a sentinel return.
    /// </summary>
    [Fact]
    public void UnreadablePackage_ThrowsSourceUnreadable_RatherThanReturningZero()
    {
        var notAnApp = TestScratch.FilePath("dep-metadata-producer", "garbage.app");
        File.WriteAllText(notAnApp, "this is not a NAVX package");

        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyLoadException>(
            () => DependencyMetadataProducer.Ensure(
                Manifest("Business Foundation"), notAnApp, compiler: null!));

        Assert.Equal("METADATA-SOURCE-UNREADABLE", ex.Stage);
        Assert.Equal("Business Foundation", ex.AppName);
    }

    /// <summary>
    /// The two answers are observably different when the code actually RUNS, which is the
    /// property #3749 found the previous version of this test could not make: it asserted that
    /// <c>Ensure</c> returns <c>int</c> and that <c>Loud</c> returns an exception type, both of
    /// which stay true if <c>Ensure</c> swallows its own throw.
    ///
    /// <para>So both arms are driven here. A package that ships NO AL source returns 0 — the
    /// ordinary symbol-only case. A package that DOES ship source and whose emit fails throws
    /// <c>METADATA-EMIT-FAIL</c>. A `null` compiler is what makes the emit fail without a BC
    /// runtime: <c>Ensure</c> catches whatever the emit raises and restates it as that stage,
    /// which is exactly the conversion under test.</para>
    /// </summary>
    [Fact]
    public void NoSource_ReturnsZero_WhileFailedEmit_Throws()
    {
        // Arm 1 — source-less package: a quiet 0, no throw.
        var symbolOnly = WritePackage("no-source", ("SymbolReference.json", "{}"));
        Assert.Equal(0, DependencyMetadataProducer.Ensure(
            Manifest("Business Foundation"), symbolOnly, compiler: null!));

        // Arm 2 — the SAME call shape on a package that ships source: throws instead.
        var withSource = WritePackage("with-source",
            ("src/Thing.Table.al", "table 50000 Thing { fields { field(1; A; Integer) { } } }"));

        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyLoadException>(
            () => DependencyMetadataProducer.Ensure(
                Manifest("Business Foundation"), withSource, compiler: null!));

        Assert.Equal("METADATA-EMIT-FAIL", ex.Stage);

        // The distinction is the point: absence returned a value, failure did not.
        Assert.NotEqual("METADATA-SOURCE-UNREADABLE", ex.Stage);
    }

    // ---- NAVX package fixtures -----------------------------------------------------

    private static byte[] Navx(byte[] zipBytes)
    {
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static byte[] ZipWith(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }

    private static string WritePackage(string suffix, params (string Name, string Content)[] entries)
    {
        var path = TestScratch.FilePath("dep-metadata-producer", $"pkg-{suffix}.app");
        File.WriteAllBytes(path, Navx(ZipWith(entries)));
        return path;
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _old;
        public EnvVar(string name, string? value)
        {
            _name = name;
            _old = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);
    }
}
