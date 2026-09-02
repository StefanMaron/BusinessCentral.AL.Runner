// SkeletonDatabaseTenantExecutionContextTests — AlRunner#2353.
//
// RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable half —
// "an ordinary test session reports ExecutionContext::Normal" — is already in the upstream
// corpus (session/TestSessionExtended.al, session/TestBCPlatformContracts.al), measured
// against a live service tier.
//
// What this pins is our own skeleton state, which the corpus cannot see:
//
//   * The runner builds NavDatabase with RuntimeHelpers.GetUninitializedObject, so BC's own
//     ctor never ran and the private `tenant` field stayed null — while NavSession.Database
//     and NavTenant.Database are both Cecil-rewritten to hand out that one instance. Every
//     session therefore saw Database.Tenant == null.
//
//   * BC's NavDatabase.UpgradeManager is
//         upgradeManager ??= new NavDataUpgradeManager(SystemTenant.UpgradeMetadata, Tenant);
//     whose two-argument ctor chains through `tenant.Id`. With a null tenant that is a bare
//     NullReferenceException raised inside a BC constructor, and it is on the path of
//     NavSession.ExecutionContext, GetCurrentModuleExecutionContext and
//     GetModuleExecutionContext — reached from ordinary AL, e.g. BaseApp's Company-Initialize
//     asking for the execution context from its OnCompanyOpen subscriber.
//
//   * NavSystemTenant.upgradeMetadata is the same shape (its real ctor sets it, the skeleton
//     skipped the ctor), so fixing only the tenant moves the failure to
//     ArgumentNullException("upgradeMetadata").
//
// The last test is the one that keeps this honest. Both the source-level shim in
// BcAssembler.cs and the Cecil replacement of NavSession.get_ExecutionContext used to answer
// a hardcoded ExecutionContext.Normal; both are gone, because BC's own getter now runs. A
// regression that reintroduces either would still satisfy "reports Normal in a plain test",
// so this asserts the getter answers Upgrade once the session carries an app upgrade
// context — which only a real evaluation of BC's body can do.

using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SkeletonDatabaseTenantExecutionContextTests
{
    private readonly BcEngineFixture _engine;

    public SkeletonDatabaseTenantExecutionContextTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private object Session()
    {
        var session = AlRunner.BcRuntime.SkeletonSession;
        Assert.NotNull(session);
        return session!;
    }

    private static object? Get(object target, string member)
    {
        var t = target.GetType();
        for (var walk = t; walk != null; walk = walk.BaseType)
        {
            var prop = walk.GetProperty(member, AnyInstance);
            if (prop != null) return prop.GetValue(target);
        }
        throw new InvalidOperationException($"{t.FullName}.{member} not found — Ncl shape changed.");
    }

    private static FieldInfo Field(object target, string name)
    {
        for (var walk = target.GetType(); walk != null; walk = walk.BaseType)
        {
            var f = walk.GetField(name, AnyInstance);
            if (f != null) return f;
        }
        throw new InvalidOperationException(
            $"{target.GetType().FullName}.{name} field not found — Ncl shape changed.");
    }

    [SkippableFact]
    public void SkeletonDatabase_ReportsATenant()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var database = Get(Session(), "Database");
        Assert.NotNull(database);

        var tenant = Get(database!, "Tenant");
        Assert.NotNull(tenant);

        // Not merely non-null: the tenant BC hands out must be the same skeleton the session
        // itself carries, otherwise Database.Tenant and Session.Tenant would disagree about
        // tenant id, encoding and settings.
        Assert.Same(Get(Session(), "Tenant"), tenant);

        // NavTenant.Id => id ?? tenantSettings.Id; the runner seeds "default". A null here is
        // what NavDataUpgradeManager's ctor dereferences.
        Assert.Equal("default", Get(tenant!, "Id"));
    }

    [SkippableFact]
    public void SkeletonSystemTenant_CarriesUpgradeMetadata()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var systemTenant = Get(Session(), "SystemTenant");
        Assert.NotNull(systemTenant);

        var upgradeMetadata = Get(systemTenant!, "UpgradeMetadata");
        Assert.NotNull(upgradeMetadata);
        Assert.Equal("NavDataUpgradeMetadata", upgradeMetadata!.GetType().Name);
    }

    [SkippableFact]
    public void UpgradeManager_ConstructsAndReportsNoUpgradeStarted()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // This is the exact expression that used to raise NullReferenceException from inside
        // NavDataUpgradeManager..ctor.
        var upgradeManager = Get(Get(Session(), "Database")!, "UpgradeManager");
        Assert.NotNull(upgradeManager);

        var info = upgradeManager!.GetType()
            .GetMethod("GetUpgradeInformation", AnyInstance)!
            .Invoke(upgradeManager, null);
        Assert.NotNull(info);

        // No data upgrade workflow was ever started here, so BC's own answer is NotStarted —
        // which is what makes GetModuleExecutionContext fall through to Normal.
        Assert.Equal("NotStarted", Get(info!, "State")!.ToString());
    }

    [SkippableFact]
    public void ExecutionContext_IsNormal_AndModuleScopedFormsAgree()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var session = Session();
        Assert.Equal("Normal", Get(session, "ExecutionContext")!.ToString());

        var current = session.GetType()
            .GetMethod("GetCurrentModuleExecutionContext", AnyInstance)!
            .Invoke(session, null);
        Assert.Equal("Normal", current!.ToString());

        var byModule = session.GetType()
            .GetMethod("GetModuleExecutionContext", AnyInstance, binder: null,
                new[] { typeof(Guid) }, modifiers: null)!
            .Invoke(session, new object[] { Guid.Empty });
        Assert.Equal("Normal", byModule!.ToString());
    }

    [SkippableFact]
    public void ExecutionContext_IsComputed_NotHardcodedNormal()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var session = Session();
        var field = Field(session, "appUpgradeContext");
        var saved = field.GetValue(session);
        Assert.Null(saved);

        // BC's getter only NULL-CHECKS this one, so an uninitialised instance is enough to
        // drive the branch and cannot reach into anything the skeleton does not have.
        var context = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            field.FieldType);
        try
        {
            field.SetValue(session, context);
            Assert.Equal("Upgrade", Get(session, "ExecutionContext")!.ToString());
        }
        finally
        {
            field.SetValue(session, saved);
        }

        Assert.Equal("Normal", Get(session, "ExecutionContext")!.ToString());
    }
}
