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

    internal const string NavUserAccountHelperTypeName = "Microsoft.Dynamics.Nav.NavUserAccount.NavUserAccountHelper";
    internal const string IsUserSuperInAllCompaniesName = "IsUserSuperInAllCompanies";

    /// <summary>
    /// Replaces the one <c>methodInfo.Invoke(serverHandle.Instance, array)</c> inside
    /// <c>NavDotNet.Invoke&lt;T&gt;</c>, the reflective call every AL DotNet method/property
    /// invocation ends in (#3174). Every member except the one below is invoked exactly as BC
    /// invoked it, so exceptions still arrive wrapped in TargetInvocationException for BC's
    /// own catch blocks.
    ///
    /// Observably equivalent: <c>NavUserAccountHelper.IsUserSuperInAllCompanies()</c> is
    /// <c>Session.Permissions.IsSuperForAllCompanies</c>, whose Ncl getter answers false under
    /// effective (lowered) test permissions, true for a NAV admin user, and otherwise whether an
    /// all-companies System-scope SUPER Access Control row exists for the user
    /// (NavUserPermissions.FetchRolesFromId). The runner's Permissions is null, so the same
    /// decision is computed from the same table — see RecordPatches.IsUserSuperInAllCompanies.
    /// </summary>
    public static object? InvokeReflectedMember(System.Reflection.MethodBase method, object? target, object?[]? arguments)
    {
        if (method.IsStatic
            && method.Name == IsUserSuperInAllCompaniesName
            && (arguments == null || arguments.Length == 0)
            && method.DeclaringType?.FullName == NavUserAccountHelperTypeName)
        {
            return RecordPatches.IsUserSuperInAllCompanies(Microsoft.Dynamics.Nav.Runtime.NavCurrentThread.Session);
        }
        return method.Invoke(target, arguments);
    }
}
