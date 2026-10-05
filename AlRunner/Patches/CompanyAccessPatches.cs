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
    /// The runner has exactly one company's DATA: its tables are not partitioned by company
    /// token, so a record opened on a second company would silently read and write the session
    /// company's rows. Outside a rename cascade only the session's own company is therefore
    /// accessible, and everything else is refused as before (Record.ChangeCompany keeps answering
    /// false for it). Inside <see cref="CreateRenameCascadeRecord"/> a company with a Company row
    /// is accessible too, which is what BC answers for the runner's super user
    /// (<c>GetAllowedCompaniesFromDatabaseAsync</c>: <c>if (IsSuperForAllCompanies) return
    /// allCompanies</c>; Ncl 28.4.53241.54346 (6f2cf682), same arm in 27.5.46862.53931). A name
    /// with no row is refused in both scopes.
    /// Matching is case-insensitive (BC's company-name comparer).
    ///
    /// <paramref name="realCompanyName"/> is the canonical name the caller then feeds to
    /// <c>CompanyTokens.Get(name)</c>, so it must be the name that company has IN THE TOKEN
    /// TABLE. For the session's own company that is <see cref="string.Empty"/>: <c>CompanyTokens.companyNames</c>
    /// starts as <c>{ string.Empty }</c>, i.e. token 0 is the runner's single company, and
    /// that is the token the record store partitions by
    /// (<c>RecordImplementation.GetActiveCompany()</c> returns <c>""</c> to match).
    /// "My Company" is the session's DISPLAY name, which is what AL's <c>CompanyName()</c>
    /// returns; the two are different names for the same company. Returning the token-table
    /// name keeps <c>ChangeCompany(CompanyName())</c> on the partition the data is actually
    /// in, instead of allocating a second, empty one. Any other company is returned under the
    /// name its Company row carries, so its token is allocated once whatever casing was asked for.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool CompanyHelper_ValidateUserHasAccessToCompany(
        object session, string companyName, out string realCompanyName)
    {
        realCompanyName = string.Empty;
        if (string.IsNullOrEmpty(companyName)) return true;
        if (string.Equals(companyName, SessionCompanyName(session),
                System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (_renameCascadeDepth <= 0) return false;

        var all = AllCompanyNames(session);
        if (all == null || !all.TryGetValue(companyName, out var rowName)) return false;
        realCompanyName = rowName;
        return true;
    }

    [ThreadStatic] private static int _renameCascadeDepth;

    /// <summary>
    /// Stands in for <c>NCLMetaTable.CreateObjectInstance(parent, isTemporary, sharedTable,
    /// companyName, securityFiltering)</c> at its one call in the per-company rename update
    /// (<c>NavRecord.UpdateReferencingTableOnRenameAsync</c>, #5071), and runs it with company-row
    /// access switched on for the length of that call. The constructor is where BC validates the
    /// company (<c>RecordImplementation.Initialize</c> → <c>GetCompanyNameToken</c>), and nothing
    /// after it asks again, so the scope is exactly the construction.
    ///
    /// Observably equivalent: BC runs the update once per company in the Company table, for a user
    /// that may open all of them; here every company reads the one shared partition, so the first
    /// pass re-keys the rows and the later passes find none to change. Trap: the day the record store
    /// is partitioned per company, this scope must go and the validator answer for every company.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavRecord CreateRenameCascadeRecord(
        Microsoft.Dynamics.Nav.Runtime.NCLMetaTable table,
        Microsoft.Dynamics.Nav.Runtime.ITreeObject parent,
        bool isTemporary,
        Microsoft.Dynamics.Nav.Runtime.NavRecord sharedTable,
        string companyName,
        Microsoft.Dynamics.Nav.Runtime.SecurityFiltering securityFiltering)
    {
        _renameCascadeDepth++;
        try
        {
            return table.CreateObjectInstance(parent, isTemporary, sharedTable, companyName, securityFiltering);
        }
        finally
        {
            _renameCascadeDepth--;
        }
    }

    private static MethodInfo? _mGetAllCompanies;

    /// <summary>
    /// BC's own <c>CompanyHelper.GetAllCompaniesAsync(session, useSecurityFilter: false)</c>: the
    /// Company rows, keyed by BC's company-name comparer. The same call BC's
    /// <c>GetCompanyNameToken</c> makes to tell "not accessible" from "does not exist", so a
    /// bundle that cannot answer it could not answer that either.
    /// </summary>
    private static HashSet<string>? AllCompanyNames(object session)
    {
        _mGetAllCompanies ??= AlRunner.Infrastructure.BcShape.RequiredMethod(
            session.GetType().Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.CompanyHelper")
                ?? throw new InvalidOperationException(
                    "CompanyHelper not found — BC shape changed; the runner cannot tell which companies exist (#5071)."),
            "GetAllCompaniesAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            "Company rename cascade", "CompanyHelper.GetAllCompaniesAsync(NavSession, bool)",
            "the rename cascade could not tell which companies exist",
            new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(bool) });
        var pending = (System.Threading.Tasks.ValueTask<HashSet<string>>)
            _mGetAllCompanies.Invoke(null, new object[] { session, false })!;
        return pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Stands in for <c>session.License.CompanyNameFilter</c> inside <c>CompanyHelper</c>'s
    /// Company-table reads (Cecil redirects each <c>get_License</c> → <c>get_CompanyNameFilter</c>
    /// pair there to this). The runner runs unlicensed, and <c>NavSession.get_License()</c>
    /// NREs on the skeleton — which took down every caller of
    /// <c>CompanyHelper.GetAllCompaniesAsync</c>, including rename propagation from a
    /// per-tenant table into a per-company one (#2325).
    ///
    /// <para>Observably equivalent: an empty filter is what a license with no company
    /// restriction answers, and BC's bodies then read the Company table unfiltered — which is
    /// exactly what they still do here, over the runner's own Company rows. Trap: a test that
    /// needs a company-RESTRICTED license cannot be expressed in this runner at all.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? UnlicensedCompanyNameFilter(object? session) => null;

    /// <summary>
    /// Stands in for <c>NavDatabase.DatabaseType</c> inside <c>CompanyTokens.Get(string)</c>,
    /// whose first line refuses any non-empty name unless the database is a tenant database
    /// ("Only tenant databases have companies"). The runner's one skeleton NavDatabase is built
    /// uninitialised, so its type reads as the enum default (<c>Application</c>), yet it is where
    /// the Company table and every per-company table live: the tenant database, as far as AL can
    /// observe. Without this, the first token for any company but the session's own is refused,
    /// which is what a rename cascade or a <c>ChangeCompany</c> to a company the test inserted
    /// asks for (#5071). Observably equivalent: the guard only separates database kinds, and the
    /// runner has just the one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavDatabaseType TenantDatabaseType(object? database)
        => Microsoft.Dynamics.Nav.Runtime.NavDatabaseType.Tenant;

    /// <summary>
    /// Replacement for the private instance method <c>CompanyTokens.GetCollationAwareCompanyName</c>,
    /// which asks SQL for the stored spelling of a company name (and answers the name itself when
    /// no row has it). The runner has no SQL. Observably equivalent here because every name that
    /// reaches <c>CompanyTokens.Get</c> is already a Company row's own name:
    /// <see cref="CompanyHelper_ValidateUserHasAccessToCompany"/> returns the row's spelling, and
    /// the rename cascade walks the row names. A name with no row is the case BC's own body also
    /// answers unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string CompanyTokens_GetCollationAwareCompanyName(object tokens, string companyName)
        => companyName;

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
