// ManifestCompilationTargetTests — issue #2725.
//
// RUNNER-MECHANISM test: BcCompiler.ReadManifestCompilerInputs must carry app.json's
// `target` into NavCA.CompilationOptions on both compile paths. Every AL bundle used to be
// compiled as CompilationTarget.OnPrem regardless of its manifest, which changes what the
// emitted code tells the runtime — the AL compiler passes the target into
// ALDatabase.ALRegisterTableConnection(CompilationTarget, ...), and BC's own
// IsRegisterTableConnectionAllowed refuses ExternalSQL for a Cloud app with its "permission"
// error but lets an OnPrem app through to connection-string validation. The al-language
// corpus declares "target": "Cloud" and is validated as Cloud on a real sandbox, so its
// Database_RegisterTableConnection_InvalidConnection_Throws expects the Cloud error.
//
// The BEHAVIOURAL claim (what BC answers for each target) is adjudicated upstream by that
// corpus test; this file only pins that the manifest value reaches the compiler options and
// the AL-output cache key, so a regression fails here in milliseconds.

using Xunit;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Tests;

public sealed class ManifestCompilationTargetTests : IDisposable
{
    private readonly string _root;

    public ManifestCompilationTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-manifest-target", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_root, "app.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("Cloud", NavCA.CompilationTarget.Cloud)]
    [InlineData("cloud", NavCA.CompilationTarget.Cloud)]
    [InlineData("OnPrem", NavCA.CompilationTarget.OnPrem)]
    public void ReadManifestCompilerInputs_DeclaredTarget_ReachesEffectiveTarget(string declared, NavCA.CompilationTarget expected)
    {
        var path = WriteManifest($$"""{ "id": "00000000-0000-0000-0000-000000000001", "name": "T", "publisher": "P", "version": "1.0.0.0", "target": "{{declared}}" }""");

        var inputs = BcCompiler.ReadManifestCompilerInputs(path);

        Assert.Equal(expected, inputs.Target);
        Assert.Equal(expected, inputs.EffectiveTarget);
    }

    [Fact]
    public void ReadManifestCompilerInputs_NoTarget_KeepsOnPremDefault()
    {
        // Negative direction: a manifest that omits `target` must NOT pick up alc's own
        // default (Cloud) — every runner-extras suite and test fixture without one has been
        // compiled as OnPrem, and Cloud would newly reject any OnPrem-only API they use.
        var path = WriteManifest("""{ "id": "00000000-0000-0000-0000-000000000002", "name": "T", "publisher": "P", "version": "1.0.0.0" }""");

        var inputs = BcCompiler.ReadManifestCompilerInputs(path);

        Assert.Null(inputs.Target);
        Assert.Equal(NavCA.CompilationTarget.OnPrem, inputs.EffectiveTarget);
    }

    [Fact]
    public void ReadManifestCompilerInputs_UnknownTarget_KeepsOnPremDefault()
    {
        var path = WriteManifest("""{ "id": "00000000-0000-0000-0000-000000000003", "name": "T", "publisher": "P", "version": "1.0.0.0", "target": "Spaceship" }""");

        var inputs = BcCompiler.ReadManifestCompilerInputs(path);

        Assert.Null(inputs.Target);
        Assert.Equal(NavCA.CompilationTarget.OnPrem, inputs.EffectiveTarget);
    }

    [Fact]
    public void CacheKeyFragment_ChangesWithTarget()
    {
        // A warm AL-output cache entry compiled under one target must not be served for the
        // other: the emitted C# differs (the CompilationTarget literal the compiler passes
        // into ALRegisterTableConnection and friends), so the key has to differ too.
        var cloud = BcCompiler.ReadManifestCompilerInputs(WriteManifest("""{ "id": "00000000-0000-0000-0000-000000000004", "name": "T", "publisher": "P", "version": "1.0.0.0", "target": "Cloud" }"""));
        var onPrem = BcCompiler.ReadManifestCompilerInputs(WriteManifest("""{ "id": "00000000-0000-0000-0000-000000000004", "name": "T", "publisher": "P", "version": "1.0.0.0", "target": "OnPrem" }"""));
        var absent = BcCompiler.ReadManifestCompilerInputs(WriteManifest("""{ "id": "00000000-0000-0000-0000-000000000004", "name": "T", "publisher": "P", "version": "1.0.0.0" }"""));

        Assert.NotEqual(cloud.CacheKeyFragment, onPrem.CacheKeyFragment);
        Assert.Equal(onPrem.CacheKeyFragment, absent.CacheKeyFragment);
    }
}
