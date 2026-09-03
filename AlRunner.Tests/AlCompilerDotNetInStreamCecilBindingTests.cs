// AlCompilerDotNetInStreamCecilBindingTests — proves the #2576 fix: ALCompiler's REAL
// DotNetToNavInStream(ITreeObject, NavDotNet) — after NclCecilRewrite has run — is a real,
// working Cecil-owned rewrite, not a body that still NREs/ArgumentNullExceptions on the
// headless skeleton's null Session.Company.SharedObjects chain.
//
// This is deliberately a RUNNER-INTERNAL claim, not a BC-behaviour one: it asserts that OUR
// Cecil rewrite pipeline actually reaches ALCompiler.DotNetToNavInStream (registered in
// NclCecilRewrite.CecilOwned) and that the helper it delegates to
// (BcRuntime.ALCompiler_DotNetToNavInStream / AlCompilerStreamPatches.cs) produces a real,
// readable NavInStream over the given .NET stream, refuses null and non-Stream values the
// same way the real body does. Whether AL source code that assigns a DotNet MemoryStream to
// an InStream variable reads back the exact content is a plain BC-behaviour claim and
// belongs upstream — see StefanMaron/BusinessCentral.AL.Language.Tests#137
// (tests/al-language/streams/TestDotNetInStream.al), and the equivalent end-to-end proof
// run locally against that corpus branch (RED before this fix, GREEN after — see the PR
// description for #2576).
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process (must share the serial bc-engine collection — see
// BcEngineCollection.cs comment header).
[Collection(BcEngineCollection.Name)]
public sealed class AlCompilerDotNetInStreamCecilBindingTests
{
    private readonly BcEngineFixture _engine;

    public AlCompilerDotNetInStreamCecilBindingTests(BcEngineFixture engine) => _engine = engine;

    [Fact]
    public void DotNetToNavInStream_KeyIsCecilOwned()
    {
        Assert.Contains(
            "Microsoft.Dynamics.Nav.Runtime.ALCompiler::DotNetToNavInStream/2",
            NclCecilRewrite.CecilOwned);
    }

    private static (Type AlCompiler, MethodInfo DotNetToNavInStream, Type NavDotNet, ConstructorInfo NavDotNetCtor)
        ResolveNclSurface()
    {
        var nclAsm = typeof(ITreeObject).Assembly;
        var alCompiler = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompiler")!;
        var tITreeObject = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
        var tNavDotNet = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavDotNet")!;
        var method = alCompiler.GetMethod("DotNetToNavInStream",
            BindingFlags.Public | BindingFlags.Static, null, new[] { tITreeObject, tNavDotNet }, null)!;
        // Public .ctor(ITreeObject parent, Object dotNetValue, Boolean runOnClient, Boolean suppressDispose).
        var ctor = tNavDotNet.GetConstructor(new[] { tITreeObject, typeof(object), typeof(bool), typeof(bool) })!;
        return (alCompiler, method, tNavDotNet, ctor);
    }

    [SkippableFact]
    public void DotNetToNavInStream_NullObj_ReturnsDefaultInStream_NotNRE()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (_, method, _, _) = ResolveNclSurface();

        var result = method.Invoke(null, new object?[] { BcRuntime.RootTreeStub, null });

        Assert.NotNull(result);
        Assert.Equal("Microsoft.Dynamics.Nav.Runtime.NavInStream", result!.GetType().FullName);
    }

    [SkippableFact]
    public void DotNetToNavInStream_WrappedMemoryStream_ProducesAReadableNavInStream()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (_, method, _, navDotNetCtor) = ResolveNclSurface();

        var payload = System.Text.Encoding.UTF8.GetBytes("mechanism test payload");
        var mem = new MemoryStream(payload);
        var navDotNet = navDotNetCtor.Invoke(new object?[] { BcRuntime.RootTreeStub, mem, false, true });

        var navInStream = method.Invoke(null, new object?[] { BcRuntime.RootTreeStub, navDotNet });

        Assert.NotNull(navInStream);
        Assert.Equal("Microsoft.Dynamics.Nav.Runtime.NavInStream", navInStream!.GetType().FullName);

        // Prove it is genuinely readable and reads OUR bytes — not just an object of the
        // right type — via the real INavStreamReader.ReadByte() (single-byte reads, the
        // narrowest surface NavInStream exposes without pulling in AL's own ReadText
        // plumbing, which is a separate compiler-emitted mechanism this test has no
        // reason to reach through).
        var reader = (INavStreamReader)navInStream;
        Assert.Equal(payload[0], reader.ReadByte());
        Assert.Equal(payload[1], reader.ReadByte());
    }

    [SkippableFact]
    public void DotNetToNavInStream_NonStreamValue_ThrowsConversionException()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (_, method, _, navDotNetCtor) = ResolveNclSurface();

        var navDotNet = navDotNetCtor.Invoke(new object?[] { BcRuntime.RootTreeStub, "not a stream", false, true });

        var ex = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, new object?[] { BcRuntime.RootTreeStub, navDotNet }));

        Assert.Equal(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLConversionException",
            ex.InnerException!.GetType().FullName);
    }
}
