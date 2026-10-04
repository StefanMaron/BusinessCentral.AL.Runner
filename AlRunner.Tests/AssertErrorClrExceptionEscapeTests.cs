// AssertErrorClrExceptionEscapeTests — which exceptions AL `asserterror` catches (#4976).
//
// Real BC's NavMethodScope.AssertError catches an exception only when it is, or is
// remapped to, a NavBaseException (an AL runtime error), or carries one on its InnerException
// chain. Anything else — a raw CLR exception such as a NullReferenceException raised inside the
// platform — escapes, and the test fails with "Unexpected CLR exception thrown". Measured on the
// service tier by corpus PR #505 (withdrawn as a measurement): every cloud leg, 27.0 through
// 28.5, and the Windows nightly, run 36603989739. The runner's replacement was an unfiltered
// `catch (Exception)`, so the same shape made the asserterror PASS — green where BC is red.
//
// Citation for the rule: NavMethodScope.AssertError and RemapToALExceptionAndThrow, Ncl
// 27.5.46862.53931 and 28.4.53241.54346 (the decompiled AssertError body hashes identically).
//
// A corpus test cannot express this (a test that reaches it always fails on BC), so the proof
// is runner-side: call the runner's own helper directly, with self = null, as
// AssertErrorOutOfScopeCatchabilityTests does. With a null scope the reflective remap declines,
// which is why the four remap-covered types are asserted separately below.
using System;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public sealed class AssertErrorClrExceptionEscapeTests
{
    private static Exception? Run(Action body) =>
        Record.Exception(() => BcRuntime.NavMethodScope_AssertError(null!, body));

    // ── A raw CLR exception escapes, with the original instance and stack ──

    [Fact]
    public void NullReferenceException_Escapes_AsTheSameInstance()
    {
        var thrown = new NullReferenceException("probe");

        var escaped = Run(() => throw thrown);

        Assert.Same(thrown, escaped);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(System.Collections.Generic.KeyNotFoundException))]
    public void OtherRawClrExceptions_Escape(Type type)
    {
        var thrown = (Exception)Activator.CreateInstance(type, "probe")!;

        var escaped = Run(() => throw thrown);

        Assert.Same(thrown, escaped);
    }

    [Fact]
    public void ClrExceptionWrappingAnotherClrException_Escapes()
    {
        var inner = new NullReferenceException("inner");
        var outer = new InvalidOperationException("outer", inner);

        Assert.Same(outer, Run(() => throw outer));
    }

    // ── What BC does catch is still caught ──

    [Fact]
    public void NavALException_IsCaught()
    {
        Assert.Null(Run(() => throw new NavALException("an AL error")));
    }

    [Theory]
    [InlineData(typeof(DivideByZeroException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(OverflowException))]
    public void ClrExceptionsBcRemapsToAnALError_AreCaught(Type type)
    {
        // RemapToALExceptionAndThrow maps exactly these four to NavALException before the
        // catch below it decides, so BC's asserterror passes on them.
        var thrown = (Exception)Activator.CreateInstance(type, "probe")!;

        Assert.Null(Run(() => throw thrown));
    }

    [Fact]
    public void ClrExceptionWithAnALErrorOnItsInnerChain_IsCaught()
    {
        // BC's second catch walks InnerException looking for a NavBaseException.
        var wrapped = new TargetInvocationException(new NavALException("an AL error"));

        Assert.Null(Run(() => throw wrapped));
    }

    [Fact]
    public void RunnerOutOfScopeRefusal_IsStillCaught()
    {
        // The deliberate, pinned contract of #2871 — see AssertErrorOutOfScopeCatchabilityTests.
        Assert.Null(Run(() => throw new RunnerOutOfScopeException("NavFile.Upload", "browser-roundtrip", "file-storage")));
    }

    [Fact]
    public void OutOfScopeMessageConvention_RaisedAsAnInvalidOperationException_IsStillCaught()
    {
        // Refusals raised inside BC code arrive as an InvalidOperationException carrying
        // the "out-of-scope: " convention; runner-extras rely on asserterror catching them.
        Assert.Null(Run(() => throw new InvalidOperationException(
            "out-of-scope: NavReport.<Layout> — layout rendering requires service tier — see docs/scope.md#report-rendering")));
    }

    [Fact]
    public void PlainInvalidOperationException_IsNotMistakenForAnOutOfScopeRefusal()
    {
        var plain = new InvalidOperationException("boom");

        Assert.Same(plain, Run(() => throw plain));
    }

    // ── The runner's own stand-ins for AL errors must BE AL errors, or the escape above hits them ──
    // Both sites used to build an InvalidOperationException when NavCSideException could not be
    // resolved from the Ncl assembly (it is defined in Types and forwarded), and asserterror's
    // catch-all hid that; with the catch narrowed they must resolve the real type.

    [Theory]
    [InlineData("AlRunner.Patches.AllProfileWritePatches", "NavCSideError")]
    [InlineData("AlRunner.Patches.RecordPatches", "BuildFeatureKeyReadOnlyError")]
    public void RunnerRefusalsBuiltAsNavCSideException_AreCaughtByAsserterror(string typeName, string method)
    {
        var type = typeof(BcRuntime).Assembly.GetType(typeName)!;
        var mi = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = method == "NavCSideError"
            ? new object?[] { "refused {0}", new object?[] { "x" } }
            : new object?[] { "Description" };

        var built = (Exception)mi.Invoke(null, args)!;

        Assert.IsAssignableFrom<NavBaseException>(built);
        Assert.Null(Run(() => throw built));
    }

    // ── Controls: the verdict is still driven by the body ──

    [Fact]
    public void BodyThatDoesNotThrow_StillFailsTheAsserterror()
    {
        var ex = Run(() => { });

        Assert.IsType<NavNCLAssertErrorException>(ex);
    }

    [Fact]
    public void ShapeGapRefusal_StillEscapes()
    {
        var gap = new BcShapeGapException("NavX", "NavX.Y", "probe");

        Assert.Same(gap, Run(() => throw gap));
    }
}
