// Issue #2961. NAV App Installed App (2000000153) on the runner: where the rows come from when
// nothing was ever installed, and the properties that make them mean anything.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   It is both, and the split is the same one Published Application (2000000206) already makes
//   next door. What this table CONTAINS on a real tier is plain BC behaviour and is adjudicated
//   upstream, where a service tier answers it.
//
//   The two tables differ in one way that matters for WHERE the upstream test can live.
//   2000000206 is Scope = OnPrem, so naming it from the Cloud-target corpus app reports
//   `error AL0296`, and its upstream test needed the corpus's second, OnPrem-target app.
//   2000000153 is Scope = Cloud, so its upstream test lives in the MAIN corpus app and is
//   adjudicated on the eight required cloud legs rather than the eight non-gating OnPrem ones.
//
//   What stays HERE is only what the RUNNER does, for a bundle that is not the corpus app:
//   that rows exist at all with nothing installed, that THIS bundle's own manifest identity is
//   among them, and that the identity columns discriminate BETWEEN apps.
//
// WHY THE DISCRIMINATION TEST IS THE IMPORTANT ONE
//   On a real tier these rows exist because INSTALLING an app wrote them. The runner never
//   installs anything, so it seeds one row per loaded app from the manifests it already parses
//   for NavApp.GetModuleInfo — the same closure, and the same AppPackageIdentity values, as the
//   2000000206 and 2000000212 rows seeded in the same pass.
//
//   Seeding the table while letting every row share a package id would make BC's own join in
//   TenantApplicationStorageRepository —
//
//     ON [Tenant Application Storage].[Package ID] = [NAV App Installed App].[Package ID]
//
//   — match the wrong app, or every app. That is green-for-the-wrong-reason and is
//   indistinguishable from a fix until something unrelated breaks, which is why
//   PackageIdsDiscriminateBetweenApps and RowIdentityMatchesThePublishedApplicationRow are not
//   decoration.
codeunit 65762 "NIA Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "NIA Assert";

    [Test]
    procedure RowsExistEvenThoughNothingWasEverInstalled()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
    begin
        // Before #2961 the runner had no view of installed apps at all: this table was empty,
        // read cleanly, and every AL question about which apps are installed got a wrong
        // answer rather than a refusal.
        Assert.IsTrue(
            NavAppInstalledApp.Count() > 0,
            'NAV App Installed App must list the apps the runner loaded.');
    end;

    [Test]
    procedure TheBundlesOwnAppIsListedWithItsManifestIdentity()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
        Mi: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Mi);

        // Found the way every reader finds it: by App ID, which is this table's whole
        // primary key.
        Assert.IsTrue(
            NavAppInstalledApp.Get(Mi.Id()),
            'The bundle under test must have a NAV App Installed App row of its own.');

        // The values are the manifest's, not invented ones.
        Assert.AreEqual(Mi.Name(), NavAppInstalledApp.Name, 'Name must come from the manifest.');
        Assert.AreEqual(Mi.Publisher(), NavAppInstalledApp.Publisher, 'Publisher must come from the manifest.');
        Assert.AreEqual(1, NavAppInstalledApp."Version Major", 'Version Major must be the manifest version''s major part.');
        Assert.AreEqual(0, NavAppInstalledApp."Version Minor", 'Version Minor must be the manifest version''s minor part.');
    end;

    [Test]
    procedure TheDependencyAppIsListedWithItsOwnDistinctIdentity()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
        DepId: Guid;
    begin
        // The dependency's version is 9.7.5.3: four DIFFERENT parts, none of them 0 or 1, so a
        // seeder that echoed the consuming bundle's 1.0.0.0 back, or that parsed only the first
        // part and defaulted the rest, cannot pass this by coincidence.
        DepId := '{5F2A9C74-1B83-4E07-9D62-8A4C3E015B9F}';

        Assert.IsTrue(NavAppInstalledApp.Get(DepId),
            'The loaded dependency app must have a NAV App Installed App row of its own.');

        Assert.AreEqual('NavAppInstalledApp Dep', NavAppInstalledApp.Name, 'Name must be the dependency''s own.');
        Assert.AreEqual('AL Runner', NavAppInstalledApp.Publisher, 'Publisher must be the dependency''s own.');
        Assert.AreEqual(9, NavAppInstalledApp."Version Major", 'Version Major must be the dependency''s own 9.');
        Assert.AreEqual(7, NavAppInstalledApp."Version Minor", 'Version Minor must be the dependency''s own 7.');
        Assert.AreEqual(5, NavAppInstalledApp."Version Build", 'Version Build must be the dependency''s own 5.');
        Assert.AreEqual(3, NavAppInstalledApp."Version Revision", 'Version Revision must be the dependency''s own 3.');
    end;

    [Test]
    procedure PackageIdsDiscriminateBetweenApps()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
        Mine: Guid;
        Theirs: Guid;
        Mi: ModuleInfo;
        DepId: Guid;
        Blank: Guid;
    begin
        // If every row carried the same Package ID — or the type default — BC's own join in
        // TenantApplicationStorageRepository
        //   ON [Tenant Application Storage].[Package ID] = [NAV App Installed App].[Package ID]
        // would match the wrong app, or every app. That failure is invisible to a
        // single-app test, which is why this suite loads a dependency at all.
        DepId := '{5F2A9C74-1B83-4E07-9D62-8A4C3E015B9F}';
        NavApp.GetCurrentModuleInfo(Mi);

        NavAppInstalledApp.Get(Mi.Id());
        Mine := NavAppInstalledApp."Package ID";
        NavAppInstalledApp.Get(DepId);
        Theirs := NavAppInstalledApp."Package ID";

        Assert.AreNotEqual(Blank, Mine, 'This app''s Package ID must not be the type default.');
        Assert.AreNotEqual(Blank, Theirs, 'The dependency''s Package ID must not be the type default.');
        Assert.AreNotEqual(Mine, Theirs, 'Two different apps must not share a Package ID.');

        // And the App ID key really is the discriminator, not an accident of row order.
        Assert.AreNotEqual(Mi.Id(), DepId, 'The two apps must have different app ids.');
    end;

    [Test]
    procedure EveryRowIsReachableByItsOwnAppIdKey()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
        Probe: Record "NAV App Installed App";
        Mi: ModuleInfo;
        DepId: Guid;
        Walked: Integer;
        Blank: Guid;
        SawMine: Boolean;
        SawDep: Boolean;
    begin
        DepId := '{5F2A9C74-1B83-4E07-9D62-8A4C3E015B9F}';
        NavApp.GetCurrentModuleInfo(Mi);
        // A row present in a FindSet walk but not gettable by its own primary key would mean
        // the seeder wrote an App ID that does not match the key it inserted under — a registry
        // that lists an app it cannot then answer about.
        Assert.IsTrue(NavAppInstalledApp.FindSet(), 'There must be rows to walk.');
        repeat
            Assert.AreNotEqual(Blank, NavAppInstalledApp."App ID", 'No row may carry a blank App ID.');
            Assert.IsTrue(Probe.Get(NavAppInstalledApp."App ID"),
                'Every listed row must be gettable by its own App ID.');
            Assert.AreEqual(NavAppInstalledApp.Name, Probe.Name, 'The keyed read must return the same row.');
            if NavAppInstalledApp."App ID" = Mi.Id() then SawMine := true;
            if NavAppInstalledApp."App ID" = DepId then SawDep := true;
            Walked += 1;
        until NavAppInstalledApp.Next() = 0;

        // Deliberately NOT an exact count. How many apps share the process depends on how the
        // runner was invoked — six when this suite runs alone, 79 when the whole runner-extras
        // tree runs in one process — and pinning that number would make this test a statement
        // about the invocation rather than about the registry. What must hold either way is
        // that the walk found this bundle and its dependency among whatever else is loaded, and
        // that every row it found was reachable by its own key (asserted in the loop above).
        Assert.IsTrue(Walked >= 2,
            'The walk must reach at least this bundle and its dependency.');
        Assert.IsTrue(SawMine, 'The walk must reach this bundle''s own row.');
        Assert.IsTrue(SawDep, 'The walk must reach the dependency''s row.');
    end;

    [Test]
    procedure RowIdentityMatchesThePublishedApplicationRowForTheSameApp()
    var
        NavAppInstalledApp: Record "NAV App Installed App";
        Mi: ModuleInfo;
    begin
        // 2000000153 is seeded in the SAME pass, from the SAME loaded-app closure and the SAME
        // AppPackageIdentity values, as 2000000206/2000000212. This pins the half of that
        // agreement a Cloud-target bundle can see: the version parts and the identity columns
        // this table carries are the manifest's own, so a reader that resolves an app through
        // this table and a reader that resolves it through GetModuleInfo agree.
        //
        // The 2000000206 side of the comparison cannot be named here — that table is
        // Scope = OnPrem and this bundle is Cloud-target — which is exactly why the
        // cross-table claim is asserted in the OnPrem suite next door and only the
        // Cloud-visible half is asserted here.
        NavApp.GetCurrentModuleInfo(Mi);
        Assert.IsTrue(NavAppInstalledApp.Get(Mi.Id()), 'The bundle must have a row.');

        Assert.AreEqual(Mi.Id(), NavAppInstalledApp."App ID",
            'App ID must be the manifest app id GetModuleInfo reports.');
        Assert.AreEqual(0, NavAppInstalledApp."Version Build", 'Version Build must be the manifest version''s build part.');
        Assert.AreEqual(0, NavAppInstalledApp."Version Revision", 'Version Revision must be the manifest version''s revision part.');
    end;
}
