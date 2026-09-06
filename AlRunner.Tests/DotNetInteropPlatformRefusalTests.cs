// DotNetInteropPlatformRefusalTests — #3212.
//
// WHAT IS BEING PINNED
//   BC's Base Application reaches .NET types through AL interop, and some of them are
//   Windows-only in .NET 8. Table 2121 "O365 Brand Color".MakePicture builds a
//   System.Drawing.Bitmap; on Linux the shipped System.Drawing.Common 8.0 throws
//   PlatformNotSupportedException out of its Gdip class initializer, BC's
//   NavAutomationHelper.Create catches the TargetInvocationException and rethrows
//   NavNCLDotNetInvokeException, and the AL author sees "The type initializer for 'Gdip'
//   threw an exception" — a message naming neither the surface nor the reason.
//   .claude/rules/loud-failures.md says that half is wrong whatever the scope verdict is.
//
// WHY A C# TEST AND NOT AN AL ONE
//   This is a claim about the RUNNER, not about BC: on a real service tier (Windows) the
//   construction succeeds, so no corpus test can express "the runner refuses it here" — see
//   the PR body for the full argument. The AL side is covered end-to-end by the Tests-SMB
//   bucket run recorded there; what is provable in-process, on every CI leg, is the
//   classifier itself and the exact chain shape BC produces around it.
//
// THE CHAIN THESE TESTS ENCODE IS MEASURED, NOT INVENTED
//   Calling the real NavAutomationHelper.CreateDotNetObject("System.Drawing.Common",
//   "System.Drawing.Bitmap", [10, 10]) against BC 28.2.50931.54319 on Linux produced exactly:
//     0 System.Reflection.TargetInvocationException          src=System.Private.CoreLib
//     1 …Types.Exceptions.NavNCLDotNetInvokeException        src=Microsoft.Dynamics.Nav.Types
//     2 System.TypeInitializationException                   src=System.Drawing.Common
//     3 System.PlatformNotSupportedException                 src=System.Drawing.Common
//   Frames 1..3 are what MeasuredGdipChain() below rebuilds.
using System;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class DotNetInteropPlatformRefusalTests
{
    /// <summary>An exception whose <see cref="Exception.Source"/> is set, as the CLR sets it
    /// to the throwing method's assembly.</summary>
    private static T WithSource<T>(T ex, string source) where T : Exception
    {
        ex.Source = source;
        return ex;
    }

    /// <summary>The exact nesting BC produces for the #3212 failure — see the file header.</summary>
    private static Exception MeasuredGdipChain()
    {
        var platform = WithSource(new PlatformNotSupportedException(
                "System.Drawing.Common is not supported on non-Windows platforms. "
                + "See https://aka.ms/systemdrawingnonwindows for more information."),
            "System.Drawing.Common");
        var typeInit = WithSource(new TypeInitializationException("Gdip", platform),
            "System.Drawing.Common");
        // Stands in for NavNCLDotNetInvokeException: the classifier reaches it only through
        // InnerException, so its concrete type is not part of the contract.
        return WithSource(new InvalidOperationException(
                "A call to System.Drawing.Bitmap failed with this message: "
                + "The type initializer for 'Gdip' threw an exception.", typeInit),
            "Microsoft.Dynamics.Nav.Types");
    }

    [Fact]
    public void GdipChain_IsRefusedByName_NamingTypeLibraryAndPlatformMessage()
    {
        var oos = DotNetInteropShims.TryClassifyPlatformRefusal(
            "System.Drawing.Bitmap", MeasuredGdipChain());

        Assert.NotNull(oos);
        // The AL-visible type has to be in the API, or the developer still cannot tell which
        // interop call died.
        Assert.Equal("NavDotNet.CreateDotNet(System.Drawing.Bitmap)", oos!.Api);
        // The anchor the expectations manifest reads (text before the first em-dash).
        Assert.StartsWith("dotnet-platform-unsupported", oos.Reason, StringComparison.Ordinal);
        // NOT "not-yet-implemented": this is a permanent host boundary, and that prefix is
        // what decides whether an AL [TryFunction] traps the refusal (scope.md §4 vs §3.16).
        Assert.DoesNotContain("not-yet-implemented", oos.Reason, StringComparison.Ordinal);
        // The .NET library that actually refused, recovered from Exception.Source.
        Assert.Contains("System.Drawing.Common", oos.Reason, StringComparison.Ordinal);
        // .NET's own sentence, which is the part that says a native package will not help.
        Assert.Contains("not supported on non-Windows platforms", oos.Reason, StringComparison.Ordinal);
        // The RID, because OSDescription alone can be a distribution name that never says
        // "not Windows" — on the box #3212 was measured on it renders as "Omarchy".
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            oos.Reason, StringComparison.Ordinal);
        Assert.Equal("dotnet-platform", oos.DocAnchor);
        // The rendered message keeps the stable contract prefix and lands on a real anchor.
        Assert.StartsWith("out-of-scope: NavDotNet.CreateDotNet(System.Drawing.Bitmap) — ",
            oos.Message, StringComparison.Ordinal);
        Assert.Contains("docs/scope.md#dotnet-platform", oos.Message, StringComparison.Ordinal);
        // The bare message #3212 complained about must not be all the reader gets: the
        // classifier's own text has to add the type name BC's wrapper omits.
        Assert.NotEqual(
            "The type initializer for 'Gdip' threw an exception.", oos.Reason);
    }

    [Fact]
    public void PlatformRefusalAtTheTopOfTheChain_IsAlsoRecognised()
    {
        // Not every Windows-only type fails through a class initializer — SecurityIdentifier
        // throws PlatformNotSupportedException directly. One level of nesting must not be a
        // precondition.
        var direct = WithSource(new PlatformNotSupportedException(
                "Windows Principal functionality is not supported on this platform."),
            "System.Security.Principal.Windows");

        var oos = DotNetInteropShims.TryClassifyPlatformRefusal(
            "System.Security.Principal.WindowsIdentity", direct);

        Assert.NotNull(oos);
        Assert.Equal("NavDotNet.CreateDotNet(System.Security.Principal.WindowsIdentity)", oos!.Api);
        Assert.Contains("System.Security.Principal.Windows", oos.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcelessPlatformRefusal_StillRefuses_WithoutInventingALibraryName()
    {
        var oos = DotNetInteropShims.TryClassifyPlatformRefusal(
            "Some.Windows.Only.Type", new PlatformNotSupportedException("nope"));

        Assert.NotNull(oos);
        Assert.Contains("the .NET library backing this type", oos!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTypeName_StillRefuses_WithTheBareApiName()
    {
        var oos = DotNetInteropShims.TryClassifyPlatformRefusal(
            null, WithSource(new PlatformNotSupportedException("nope"), "Some.Lib"));

        Assert.NotNull(oos);
        Assert.Equal("NavDotNet.CreateDotNet", oos!.Api);
    }

    // ── The negative half: nothing else may be converted into a refusal ──────────────────
    //
    // This is the part that stops the change from being a catch-all. Every one of these is a
    // failure BC handles itself (add-in fallback, "not an image", an AL authoring error), and
    // swallowing any of them behind an out-of-scope message would be a regression.

    [Fact]
    public void MissingAssembly_IsNotAPlatformRefusal()
    {
        // What BC raises when the assembly is genuinely absent — the add-in path, which has
        // its own named refusal (NavDotNetPatches.ThrowServerInteropOOS).
        Assert.Null(DotNetInteropShims.TryClassifyPlatformRefusal(
            "Some.Type", new System.IO.FileNotFoundException("assembly not found")));
    }

    [Fact]
    public void ConstructorArgumentError_IsNotAPlatformRefusal()
    {
        var chain = new InvalidOperationException("wrapped",
            new ArgumentException("value does not fall within the expected range"));
        Assert.Null(DotNetInteropShims.TryClassifyPlatformRefusal("System.IO.MemoryStream", chain));
    }

    [Fact]
    public void NotSupportedException_IsNotAPlatformRefusal()
    {
        // NotSupportedException is a DIFFERENT type from PlatformNotSupportedException in the
        // direction that matters: the base type is raised by plenty of in-scope code paths
        // (a non-seekable stream, for one) and must keep BC's own error.
        Assert.Null(DotNetInteropShims.TryClassifyPlatformRefusal(
            "System.IO.Stream", new NotSupportedException("stream does not support seeking")));
    }

    [Fact]
    public void RunnerOutOfScopeExceptionInTheChain_IsLeftAlone()
    {
        // An existing refusal (the SecurityIdentifier shim's, MediaPatches', …) must reach the
        // caller as itself rather than being re-wrapped under a second anchor.
        var already = new RunnerOutOfScopeException("Email.Send", "email-smtp", "email");
        Assert.Null(DotNetInteropShims.TryClassifyPlatformRefusal("Whatever", already));
    }

    [Fact]
    public void NullException_IsNotAPlatformRefusal()
    {
        Assert.Null(DotNetInteropShims.TryClassifyPlatformRefusal("System.IO.MemoryStream", null));
    }

    [Fact]
    public void DeeplyNestedPlatformRefusal_IsStillFound()
    {
        // The walk is a walk, not a peek at InnerException.InnerException. BC's nesting depth
        // is not a contract — a future Types.dll wrapping one level deeper must not silently
        // put the bare "type initializer" message back in front of the developer.
        Exception e = WithSource(new PlatformNotSupportedException("nope"), "Some.Lib");
        for (var i = 0; i < 6; i++) e = new InvalidOperationException($"wrap {i}", e);

        var oos = DotNetInteropShims.TryClassifyPlatformRefusal("Deep.Type", e);

        Assert.NotNull(oos);
        Assert.Contains("Some.Lib", oos!.Reason, StringComparison.Ordinal);
    }
}
