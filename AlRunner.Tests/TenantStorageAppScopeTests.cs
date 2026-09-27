// Pins the C# contract behind #4854: TenantStoragePatches keys isolated storage on every
// argument BC's IsolatedStorageRepository keys table 2000000107 on, the app id first. The
// AL-observable claim (one app cannot see another app's keys) is corpus codeunit 67580;
// these tests pin the store the runner answers it from. Keys are unique per test because the
// store is process-wide and shared with the engine tests.
using System;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class TenantStorageAppScopeTests
{
    private static readonly NavGuid NoUser = new(Guid.Empty);

    private static string NewKey() => "tsas-" + Guid.NewGuid().ToString("N");

    private static bool Set(Guid app, DataScope scope, string company, string key, string value)
        => TenantStoragePatches.Repo_Set(DataError.ThrowError, new NavGuid(app), scope, company, NoUser,
            key, value, encryptionStatus: 0, targetValueType: 0);

    private static bool Contains(Guid app, DataScope scope, string company, string key)
        => TenantStoragePatches.Repo_Contains_5(new NavGuid(app), scope, company, NoUser, key);

    private static (bool Found, string Value) Get(Guid app, DataScope scope, string company, string key)
    {
        NavText read = new NavText("unset");
        var value = new ByRef<NavText>(() => read, v => read = v);
        var (found, _) = TenantStoragePatches.Repo_Get(DataError.ThrowError, new NavGuid(app), scope,
            targetValueType: 0, company, NoUser, key, value);
        return (found, (string)read);
    }

    [Fact]
    public void KeyStoredByOneApp_IsNotContainedInAnother()
    {
        Guid appA = Guid.NewGuid(), appB = Guid.NewGuid();
        var key = NewKey();

        Assert.True(Set(appA, DataScope.Module, string.Empty, key, "a"));

        Assert.True(Contains(appA, DataScope.Module, string.Empty, key));
        Assert.False(Contains(appB, DataScope.Module, string.Empty, key));
        Assert.Equal((false, string.Empty), Get(appB, DataScope.Module, string.Empty, key));
    }

    [Fact]
    public void SameKeyInTwoApps_HoldsEachAppsOwnValue_AndDeletesIndependently()
    {
        Guid appA = Guid.NewGuid(), appB = Guid.NewGuid();
        var key = NewKey();

        Set(appA, DataScope.Module, string.Empty, key, "value of A");
        Set(appB, DataScope.Module, string.Empty, key, "value of B");

        Assert.Equal((true, "value of A"), Get(appA, DataScope.Module, string.Empty, key));
        Assert.Equal((true, "value of B"), Get(appB, DataScope.Module, string.Empty, key));

        Assert.True(TenantStoragePatches.Repo_Delete(DataError.ThrowError, new NavGuid(appA),
            DataScope.Module, string.Empty, NoUser, key));
        Assert.False(Contains(appA, DataScope.Module, string.Empty, key));
        Assert.Equal((true, "value of B"), Get(appB, DataScope.Module, string.Empty, key));
    }

    [Fact]
    public void CompanyScope_KeysOnTheCompanyBcPassesIn()
    {
        var app = Guid.NewGuid();
        var key = NewKey();

        Set(app, DataScope.Company, "CRONUS", key, "cronus value");

        Assert.Equal((true, "cronus value"), Get(app, DataScope.Company, "CRONUS", key));
        Assert.False(Contains(app, DataScope.Company, "Other Company", key));
        // A different scope is a different row, whatever the key.
        Assert.False(Contains(app, DataScope.Module, string.Empty, key));
    }

    [Fact]
    public void Scope_IsPartOfTheKey_EvenWhenCompanyAndUserAreEqual()
    {
        var app = Guid.NewGuid();
        var key = NewKey();

        Set(app, DataScope.Module, string.Empty, key, "module value");

        // Same app, company and user; only the scope differs, so this is another row.
        Assert.False(Contains(app, DataScope.User, string.Empty, key));
        Assert.Equal((true, "module value"), Get(app, DataScope.Module, string.Empty, key));
    }
}
