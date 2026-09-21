// XrmExternalDataRefusal — refuse BC's Xrm proxy-version surface by name, instead of serving
// AL a registry the runner cannot faithfully populate (#3515).
//
// Observably equivalent (loud-failures.md): it is NOT. That is the point — the runner cannot
// answer this faithfully, so it refuses rather than returning a default. A service tier's
// XrmServiceProvider registry is filled at NavEnvironment construction from
// <service tier>/DataSources/DataSources.json, one entry per proxy the deployment declares,
// and every id in it is a deployment's own configuration. The runner's artifact directory is
// flat and ships no such file, so the registry is empty and CrmHelper.GetProxyIdList() answers
// []. Both available alternatives are forbidden: returning [] silently is the silent-default
// that loud-failures.md exists to stop (BaseApp then fails three frames away on a FindLast over
// the temp table it fills), and synthesising ids from the shipped proxy assemblies would invent
// the deployment configuration that file carries. External data — a live Dataverse org over the
// Xrm connector stack — is out of scope (docs/scope.md#table-connections).
//
// Citation: Page5330/Page7200.InitializeDefaultProxyVersion -> Codeunit5330/Codeunit7201
// .GetLastProxyVersionItem -> InitializeProxyVersionList -> DotNet CrmHelper.GetProxyIdList()
// -> XrmServiceProvider.ProxyIds. GetProxyIdList is defined in
// Microsoft.Dynamics.Nav.CrmCustomizationHelper.dll, NOT in Ncl.dll — read off that assembly's
// MethodDefinition/MemberReference tables on 28.1.49838.53910 — which is why the refusal cannot
// be a Cecil rewrite (NclCecilRewrite.RewriteNcl rewrites Ncl.dll alone) and rides the
// NavDotNet.Invoke<T> redirect instead. Proven by tests/runner-extras/crm-proxy-version.
//
// Trap: this MUST stay on the lazy AL-invocation path and must never move to startup.
// NavEnvironment's ctor calls ExternalDataServiceManager.InitializeExternalDataServiceProviders
// ("Xrm", XrmServiceProvider.RegisterXrmService) unconditionally on every run — Ncl.dll's only
// MemberReference to that method — so a refusal raised at registration time would fail every
// test run in the repository, not the AL that touches the surface.
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// The external-data (Xrm / CDS / Dataverse) proxy surface AL reaches through
/// <c>DotNet CrmHelper</c>, refused by name at the point AL invokes it.
/// </summary>
internal static class XrmExternalDataRefusal
{
    internal const string CrmHelperTypeName =
        "Microsoft.Dynamics.Nav.CrmCustomizationHelper.CrmHelper";
    internal const string GetProxyIdListName = "GetProxyIdList";

    /// <summary>Reason token, matched by <c>tests/expectations/</c> and by the AL suite.</summary>
    internal const string DocAnchor = "table-connections";

    /// <summary>
    /// True when <paramref name="method"/> is the external-data proxy-version lookup. Keyed on
    /// the declaring type's full name as well as the member name, so a same-named member on any
    /// other type is invoked normally — the discrimination
    /// <see cref="NavDotNetPatches.InvokeReflectedMember"/> already makes for its other case.
    /// </summary>
    internal static bool IsExternalDataProxyLookup(MethodBase method) =>
        Matches(method.IsStatic, method.Name, method.DeclaringType?.FullName);

    /// <summary>
    /// The decision above, over the three values it reads. Separated so a test can drive every
    /// arm: the real BC type is not loadable in a unit test, so a test double can never satisfy
    /// the <see cref="CrmHelperTypeName"/> comparison and the TRUE arm would otherwise be
    /// unreachable — leaving the refusal untested in the only direction that matters.
    /// </summary>
    internal static bool Matches(bool isStatic, string name, string? declaringTypeFullName) =>
        isStatic
        && name == GetProxyIdListName
        && declaringTypeFullName == CrmHelperTypeName;

    /// <summary>
    /// Refuse, naming the API and the reason. Never returns.
    /// </summary>
    internal static object Throw() =>
        throw new RunnerOutOfScopeException(
            $"CrmHelper.{GetProxyIdListName}",
            "table-connections — the Xrm proxy-version registry is a service tier's external-data "
            + "configuration, read at startup from DataSources/DataSources.json; a BC artifact "
            + "directory ships no such file, so the runner can neither read it nor invent it. "
            + "BaseApp's CRM/CDS Connection Setup pages (5330, 7200) consume this list from "
            + "OnOpenPage, so they cannot open here",
            DocAnchor);
}
