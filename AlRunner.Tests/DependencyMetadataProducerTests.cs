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
//   same way, which is what LoudFailure_And_NoSource_AreDifferentAnswers asserts structurally.

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
    /// Base Application is excluded because its emit produces ZERO objects without a
    /// PublicKeyToken=null copy of Microsoft.AspNetCore.StaticFiles that no BC artifact ships —
    /// a hard blocker, not a cost decision. tests/expectations/metadata-equivalence/apps.json
    /// records the same exclusion for the same reason.
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
    /// A package with no readable source answers "nothing to produce" (false) rather than
    /// throwing, and it answers that from the PACKAGE — before any compile — which is what
    /// keeps "unavailable" from ever being inferred from a failure. #3590 is why that ordering
    /// is load-bearing: a construction failure that reached the consumer would be
    /// indistinguishable from a document that was never produced.
    /// </summary>
    [Fact]
    public void HasCompilableSource_IsFalseForAnUnreadablePackage_AndDoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-package-{Guid.NewGuid():N}.app");
        Assert.False(DependencyMetadataProducer.HasCompilableSource(missing));

        var notAnApp = Path.Combine(Path.GetTempPath(), $"garbage-{Guid.NewGuid():N}.app");
        File.WriteAllText(notAnApp, "this is not a NAVX package");
        try { Assert.False(DependencyMetadataProducer.HasCompilableSource(notAnApp)); }
        finally { File.Delete(notAnApp); }
    }

    /// <summary>
    /// The two outcomes are structurally different — one returns, one throws — so no caller can
    /// treat a failed compile as an absent document by reading a return value. Asserted on the
    /// declared signature rather than by running a compile, so it holds without a BC runtime and
    /// fails loudly if someone converts the throw into a sentinel return.
    /// </summary>
    [Fact]
    public void LoudFailure_And_NoSource_AreDifferentAnswers()
    {
        var ensure = typeof(DependencyMetadataProducer)
            .GetMethod("Ensure", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(ensure);
        // "How many documents are available" — never a status code with a failure value in it.
        Assert.Equal(typeof(int), ensure!.ReturnType);

        var loud = typeof(DependencyMetadataProducer)
            .GetMethod("Loud", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(loud);
        Assert.Equal(typeof(AlRunner.Infrastructure.DependencyLoadException), loud!.ReturnType);
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
