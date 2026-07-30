// CompanyInitializer — make the runner's company look like a company that exists.
//
// WHY
//   Install triggers are not what creates a BC company's baseline rows. In real BC that is
//   company CREATION, which runs codeunit 2 "Company-Initialize" — and that is what inserts
//   the Company Information record, the Source Code Setup, the setup No. Series and so on.
//   The runner fired install triggers and nothing else, so those rows simply did not exist.
//
//   That went unnoticed for as long as a failed Record.Get silently succeeded: the caller got
//   a blank record and carried on. Once Get was made to raise (as AL requires when the return
//   value is not consumed), the missing rows surfaced — as a swallowed error five layers up,
//   reported as "report 1306 requires filter information".
//
//   Running BC's own codeunit 2 is the faithful answer rather than fabricating rows here: the
//   rows are then exactly the ones Base App creates. It runs once per bundle, immediately
//   before the install baseline is captured, so its rows are part of the committed baseline
//   every test is restored to.
//
// KNOWN INCOMPLETE — and deliberately not hidden
//   Codeunit 2 does not currently run to completion here: it reaches InitSourceCodeSetup and
//   the OnBeforeSourceCodeSetupInsert subscriber in Manufacturing codeunit 99000790 NREs.
//   Everything it inserted before that point IS committed, and that partial state is worth
//   +5 Pageworks tests over not running it at all (measured both ways). So this deliberately
//   keeps the partial result rather than rolling back — but it reports the failure loudly,
//   because a half-initialized company is a real limitation a developer needs to know about
//   when something downstream reads a setup table that codeunit 2 never got to.
using System.Reflection;

namespace AlRunner;

internal static class CompanyInitializer
{
    // Codeunit 2 "Company-Initialize" (Base App). Absent in bundles that do not carry Base
    // App, which is not an error: those have no company-setup concept to initialize.
    private const int CompanyInitializeCodeunitId = 2;

    private static bool _ranForThisBundle;

    internal static void ResetForNewBundle() => _ranForThisBundle = false;

    /// <summary>
    /// Run BC's own company initialization once per bundle. Call after install triggers and
    /// before CaptureInstallBaseline so the rows it creates are part of the restored baseline.
    /// </summary>
    internal static void EnsureCompanyInitialized()
    {
        if (_ranForThisBundle) return;
        _ranForThisBundle = true;

        if (BcRuntime.FindCodeunitTypePublic(CompanyInitializeCodeunitId) == null)
            return; // no Base App in this bundle — nothing to initialize.

        try
        {
            BcRuntime.NavCodeunit_RunCodeunit(
                Microsoft.Dynamics.Nav.Types.DataError.ThrowError, CompanyInitializeCodeunitId, null);
            PerfTrace.Log("CompanyInitializer: ran codeunit 2 Company-Initialize");
        }
        catch (Exception ex)
        {
            // Loud, never silent — see the KNOWN INCOMPLETE note above. Not fatal: the rows it
            // did insert stay, and a bundle that never reads the rest still runs correctly.
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Console.Error.WriteLine(
                $"[CompanyInitializer] codeunit 2 \"Company-Initialize\" did not complete: " +
                $"{inner.GetType().Name}: {inner.Message} — the company is PARTIALLY initialized; " +
                $"setup tables it had not reached yet are missing, and AL that reads them will fail.");
        }
    }
}
