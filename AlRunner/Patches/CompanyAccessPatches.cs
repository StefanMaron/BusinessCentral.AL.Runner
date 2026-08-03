// CompanyAccessPatches — the runner's answer to "does this user have this company?".
//
// BC's CompanyHelper.ValidateUserHasAccessToCompany asks the session for its allowed
// companies, which reads the Company system table through the tenant database and then
// consults the user's entitlements. Neither exists here: the runner is a single-company,
// single-user, SQL-less process.
//
// It matters because it is the ONLY thing that decides whether AL's
// `Record.ChangeCompany(<name>)` succeeds. BC's RecordImplementation.ChangeCompany:
//
//     if (!parentRecord.MetaTable.DataPerCompany)          return true;
//     if (!GetCompanyNameToken(errorLevel, name, out tok)) return false;   // ← here
//
// so with no answer at all, every ChangeCompany — including to a company that does not
// exist — would have to be reported as success.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

/// <summary>
/// Must be public: the rewritten Ncl body calls straight into this helper and the CLR
/// enforces accessibility on that call.
/// </summary>
public static class CompanyAccessPatches
{
    /// <summary>
    /// Replacement for the extension method
    /// <c>CompanyHelper.ValidateUserHasAccessToCompany(NavSession, string, out string)</c>.
    ///
    /// The runner has exactly one company. It is accessible, and nothing else is.
    /// Matching is case-insensitive, which is what BC's own collation-aware company-name
    /// comparison gives for the ASCII names a test can use.
    ///
    /// <paramref name="realCompanyName"/> is the canonical name the caller then feeds to
    /// <c>CompanyTokens.Get(name)</c>, so it must be the name that company has IN THE TOKEN
    /// TABLE — and that is <see cref="string.Empty"/>: <c>CompanyTokens.companyNames</c>
    /// starts as <c>{ string.Empty }</c>, i.e. token 0 is the runner's single company, and
    /// that is the token the record store partitions by
    /// (<c>RecordImplementation.GetActiveCompany()</c> returns <c>""</c> to match).
    /// "My Company" is the session's DISPLAY name, which is what AL's <c>CompanyName()</c>
    /// returns; the two are different names for the same company. Returning the token-table
    /// name keeps <c>ChangeCompany(CompanyName())</c> on the partition the data is actually
    /// in, instead of allocating a second, empty one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool CompanyHelper_ValidateUserHasAccessToCompany(
        object session, string companyName, out string realCompanyName)
    {
        realCompanyName = string.Empty;
        if (string.IsNullOrEmpty(companyName)) return true;
        return string.Equals(companyName, SessionCompanyName(session),
            System.StringComparison.OrdinalIgnoreCase);
    }

    private static FieldInfo? _fCompanyName;

    /// <summary>The session's company display name, read off BC's own NavCompany.</summary>
    private static string SessionCompanyName(object? session)
    {
        var company = session?.GetType()
            .GetProperty("Company", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(session);
        if (company == null) return string.Empty;
        _fCompanyName ??= company.GetType()
            .GetField("companyName", BindingFlags.NonPublic | BindingFlags.Instance);
        return _fCompanyName?.GetValue(company) as string ?? string.Empty;
    }
}
