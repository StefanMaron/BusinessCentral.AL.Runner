// NavDotNetPatches — Cecil catch-block patch for NavDotNet.CreateNavServerHandle,
// plus a rethrow guard for NavDotNet.CreateDotNet.
//
// WHY: The method's try block (NavAutomationHelper.CreateDotNetObject) succeeds for
// in-process .NET types (MemoryStream, encoders, in-process crypto — all IN SCOPE).
// When it throws NavNCLDotNetCreateException (assembly genuinely absent / server
// add-in required), the catch block falls back to NavGlobal.SystemTenant.AddInProvider
// which is null on the runner skeleton → silent NRE.
//
// The Cecil patch (NclCecilRewrite.cs) replaces ONLY the catch block with a call to
// ThrowServerInteropOOS, leaving the try block (happy path) completely unchanged.
// Absent-assembly accesses now throw RunnerOutOfScopeException with the assembly name
// instead of NREing, making the failure loud and actionable.
//
// ADDITIONAL: NavDotNet.CreateDotNet has a broad catch-all (Exception) that intercepts
// any exception thrown inside its try block and wraps it in NavNCLDotNetCreateException
// if it is NOT already a NavBaseException.  Our RunnerOutOfScopeException extends
// plain System.Exception, so it gets wrapped — losing the OOS signal.  The surgical
// RethrowIfRunnerOOS check (inserted at the start of CreateDotNet's catch block by
// NclCecilRewrite.cs) rethrows OOS before the wrapping logic runs.
//
// ADDITIONAL (#2772): the `IsAvailable` probe on a [RunOnClient] DotNet variable.
// See IsUnavailableClientCapabilityProbe below for the full argument.

namespace AlRunner.Patches;

public static class NavDotNetPatches
{
    // Cecil catch-block helper for NavDotNet.CreateNavServerHandle.
    // Returns Exception (declared return type) so the "throw" opcode in the Cecil-
    // patched catch block has a valid Exception-typed value on the IL stack.
    // This method ALWAYS throws; it never returns.
    public static Exception ThrowServerInteropOOS(string assemblyName)
        => throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "NavDotNet.CreateNavServerHandle",
            $"dotnet-server-interop — external assembly/KMS unavailable: {assemblyName}",
            "crypto-external");

    // Surgical guard inserted at the start of NavDotNet.CreateDotNet's catch-all
    // (Exception) block.  If the exception is a RunnerOutOfScopeException it is
    // re-thrown preserving the original stack trace; otherwise this is a no-op and
    // the catch block's original logic (diagnostics + NavNCLDotNetCreate wrapping) runs.
    public static void RethrowIfRunnerOOS(Exception? ex)
    {
        if (ex is AlRunner.Infrastructure.RunnerOutOfScopeException oos)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(oos).Throw();
    }

    /// <summary>
    /// #2772 — predicate for the Cecil guard prepended to
    /// <c>NavDotNet.InvokeStaticPropertyGet&lt;T&gt;(string, uint)</c>. True means "this call
    /// is the client-capability availability PROBE, answer <c>false</c> and do not enter BC's
    /// body"; false means "run BC's original body unchanged".
    ///
    /// WHAT AL EMITS
    ///   AL `SomeClientVar.IsAvailable()` on a <c>[RunOnClient] DotNet</c> variable compiles to
    ///       navDotNet.InvokeStaticPropertyGet&lt;bool&gt;("IsAvailable", methodIndex)
    ///   — verified by decompiling the shipped 28.1 System Application and Base Application:
    ///   17 call sites, every one of them this exact shape — System Application codeunits
    ///   1908/1909/3726/7569 and pages 1990/8886/9451, Base Application pages
    ///   1310/1600/9060/9062/9068. There is no other emit shape for the probe, so this one
    ///   seam covers all of them.
    ///
    /// WHY BC RAISES HERE ON THE RUNNER
    ///   InvokeStaticPropertyGet calls NavDotNet.CheckTypeIsLoaded, whose RunOnClient branch is
    ///       remoteHandle = new NavClientHandle(
    ///           NavCurrentThread.Session.ClientCallback.CreateDotNetHandle(...), …)
    ///   and `NavSession.ClientCallback` is `ClientCallbackOrNull ?? throw new
    ///   NavNCLCallbackNotAllowedException()`. With no client attached the probe raises before
    ///   it can ever produce a value — which turns "no client here" into a test failure.
    ///
    /// WHY `false` IS THE FAITHFUL ANSWER, NOT A SILENT FAKE
    ///   Microsoft ships the server-side half of these types in the service tier itself,
    ///   `Microsoft.Dynamics.Nav.ClientExtensions.dll`, and every client capability type in it
    ///   (PageNotifier, CameraProvider, LocationProvider, AppSource, UserTours, Tour, Designer,
    ///   BarcodeScannerProvider, CameraBarcodeScannerProvider, DeviceContactProvider, OfficeHost)
    ///   derives from
    ///       public abstract class ClientExtension&lt;T&gt; { public static bool IsAvailable =&gt; false; }
    ///   `false` is therefore Microsoft's own answer, in Microsoft's own binary, for exactly
    ///   these types when no client is providing the capability. It is also what the AL contract
    ///   asks for: `IsAvailable` exists so AL can ask "can I use this here?" WITHOUT raising, and
    ///   every one of the 17 call sites uses it as the guard on an `if`.
    ///
    /// WHAT STILL RAISES (the other half of .claude/rules/loud-failures.md)
    ///   Only the probe is answered. Everything past the guard is a real USE of a client that is
    ///   not there, and still raises NavNCLCallbackNotAllowedException exactly as before:
    ///     • `PageNotifier.Create()`   → InvokeStaticMethod&lt;NavDotNet&gt;("Create", …)
    ///     • `PageNotifier.NotifyPageReady()` → InvokeMethod&lt;NavVoidType&gt;("NotifyPageReady", …)
    ///     • any other static property get on a client type (OfficeHost.HostName, …)
    ///     • every [RunOnClient] path that does not go through this one method
    ///   Nothing here can make a client-side call succeed: the guard returns `true` only for a
    ///   BOOLEAN result and the literal property name `IsAvailable`, and the only value it ever
    ///   produces is `false`.
    ///
    /// NOT AFFECTED: a DotNet variable declared WITHOUT [RunOnClient] over the same type. That
    /// resolves server-side through CreateNavServerHandle against the real
    /// Microsoft.Dynamics.Nav.ClientExtensions assembly and answers from Microsoft's own code
    /// (or raises RunnerOutOfScopeException naming the assembly if it is not on the probing
    /// path) — a different mechanism, deliberately left alone.
    /// </summary>
    /// <param name="runOnClient">NavDotNet's own <c>runOnClient</c> field, loaded directly by
    /// the Cecil prologue — no reflection, and false for every server-side DotNet variable.</param>
    /// <param name="propertyName">The property name BC was about to ask the client for.</param>
    /// <param name="resultType">The call's <c>typeof(T)</c>. Guarding on it keeps the generic
    /// early-return well-typed: a non-bool instantiation always runs BC's original body.</param>
    public static bool IsUnavailableClientCapabilityProbe(
        bool runOnClient, string? propertyName, Type? resultType)
        => runOnClient
           && resultType == typeof(bool)
           && string.Equals(propertyName, "IsAvailable", StringComparison.Ordinal);
}
