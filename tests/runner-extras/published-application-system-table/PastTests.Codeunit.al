// Issue #2963, corrected by #3066. Published Application (2000000206) on the runner: where the
// rows come from when nothing was ever published, and the property that makes them mean
// anything.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   It is now BOTH, and the split matters. What this table CONTAINS on a real tier is plain BC
//   behaviour, and it lives upstream in the corpus's Target = OnPrem app,
//   tests/al-language-onprem/record/TestPublishedApplicationSysTable.al
//   (BusinessCentral.AL.Language.Tests#187) — a service tier adjudicates it there on all eight
//   OnPrem legs, and this repository's corpus leg then runs the same file against the runner.
//
//   What stays HERE is only what the RUNNER does, for a bundle that is not the corpus app:
//   that rows exist at all with nothing published, that THIS bundle's own manifest is among
//   them, that the Tenant ID the runner chose satisfies BC's own filter, that no two of the
//   rows the runner seeded collide, and that the Installed FlowField reads true because the
//   runner seeded the table it is computed over rather than by writing to the FlowField.
//
//   The table is unnameable from the Cloud-target corpus app — compiling
//   `Record "Published Application"` there reports
//
//     error AL0296: The application object or method 'Published Application' has scope
//                   'OnPrem' and cannot be used for 'Cloud' development
//
//   which is why the corpus needed a second, OnPrem-target app for it at all. This suite
//   targets OnPrem for the same reason.
//
// TWO CLAIMS THIS FILE USED TO MAKE, THAT A SERVICE TIER THEN CONTRADICTED (#3066)
//   Both were runner-local pins of BC behaviour, and both were wrong — which is the failure
//   .claude/rules/bc-behavior-tests-go-upstream.md exists to catch. They are gone from here,
//   the runner was changed to match, and the corrected claims are pinned upstream where a tier
//   measures them:
//
//     * "Package ID and Runtime Package ID must not be the same GUID for one app." All eight
//       OnPrem legs, 27.0 through 28.4, report them EQUAL for a freshly published app. What
//       discriminates is the value differing BETWEEN apps, which is what this file still
//       checks and what upstream pins as PublishedApplication_TwoApps_DoNotShareEitherPackageId.
//
//     * "Installed is a FlowField with nothing behind it here; a true would be fabricated."
//       There was something behind it: Installed is
//       `Exist("Installed Application" WHERE("Runtime Package ID" = FIELD("Runtime Package ID")))`,
//       and 2000000212 is ordinary application-database storage the runner had simply never
//       seeded. Upstream reports true; the runner now seeds the table and agrees.
//
// WHAT THE RUNNER DOES, AND WHY THE PACKAGE-ID TESTS ARE THE IMPORTANT ONES
//   On a real tier these rows exist because publishing an app wrote them. The runner never
//   publishes anything, so it seeds one row per loaded app from the manifests it already parses
//   for NavApp.GetModuleInfo, stamps a deterministic per-app package id onto both that row and
//   that app's AllObj rows, and seeds the matching Installed Application row.
//
//   System Application code compares the two sides:
//
//     AllObj."App Runtime Package ID" <> PublishedApplication."Runtime Package ID"
//
//   Both were the type default before #2963. Seeding the table while leaving them that way
//   would have made that comparison TRUE FOR EVERY APP AND EVERY TABLE — every ownership check
//   would start passing, and the tests that depend on it would go green for the wrong reason,
//   which is indistinguishable from a fix until something unrelated breaks. So
//   PackageIdsDiscriminateBetweenApps and OwnObjectsCarryTheirOwnAppsRuntimePackageId are not
//   decoration: they are the difference between a fix and a universally permissive answer.
codeunit 65572 "PAST Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "PAST Assert";

    [Test]
    procedure RowsExistEvenThoughNothingWasEverPublished()
    var
        PublishedApplication: Record "Published Application";
    begin
        // The runner has no publish step, so on main this table was simply empty and every
        // System Application module-ownership check declined.
        Assert.IsTrue(
            PublishedApplication.Count() > 0,
            'Published Application must list the apps the runner loaded.');
    end;

    [Test]
    procedure TheBundlesOwnAppIsListedWithItsManifestIdentity()
    var
        PublishedApplication: Record "Published Application";
        Mi: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Mi);

        // Found the way BC's own ModuleOwnsTable finds it: by app id.
        PublishedApplication.SetRange(ID, Mi.Id());
        Assert.IsTrue(
            PublishedApplication.FindFirst(),
            'The bundle under test must have a Published Application row of its own.');

        // The values are the manifest's, not invented ones.
        Assert.AreEqual(Mi.Name(), PublishedApplication.Name, 'Name must come from the manifest.');
        Assert.AreEqual(Mi.Publisher(), PublishedApplication.Publisher, 'Publisher must come from the manifest.');
        Assert.AreEqual(1, PublishedApplication."Version Major", 'Version Major must be the manifest version''s major part.');
        Assert.AreEqual(0, PublishedApplication."Version Minor", 'Version Minor must be the manifest version''s minor part.');
    end;

    [Test]
    procedure InstalledReadsTrueThroughTheTableItIsComputedOver()
    var
        PublishedApplication: Record "Published Application";
        InstalledApplication: Record "Installed Application";
        Mi: ModuleInfo;
        Rpid: Guid;
    begin
        // THE RUNNER-SIDE HALF of upstream's PublishedApplication_CalcFields_Installed_IsTrueForThisApp.
        // Upstream asserts the observable — a published app reads as installed. This asserts HOW
        // the runner gets there, which upstream cannot see and which is the part that can
        // silently rot: the FlowField is
        //
        //   Exist("Installed Application" WHERE("Runtime Package ID" = FIELD("Runtime Package ID")))
        //
        // so the runner seeds a real 2000000212 row carrying this app's runtime package id, and
        // BC's own CalcFields computes true from it. Writing to the FlowField instead would be
        // silently discarded, and stamping a DIFFERENT id on the 2000000212 row would leave
        // Installed false with the row present — neither is visible from the upstream test.
        //
        // "Tenant Visible" and "PerTenant Or Installed" have their own two tests below, added
        // by #3072. They are Lookup FlowFields over "NAV App Extra" (2000000157), a VIRTUAL
        // table with no row to seed, so the runner answers them through a provider instead -
        // and the tier's own answer is asked upstream in corpus PR #228, not pinned here.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.FindFirst();
        Rpid := PublishedApplication."Runtime Package ID";

        Assert.IsTrue(
            InstalledApplication.Get(Rpid, ''),
            'The runner must seed an Installed Application row keyed on this app''s runtime package id.');
        Assert.AreEqual(
            PublishedApplication."Package ID", InstalledApplication."Package ID",
            'The Installed Application row must carry the same package id as the published row.');

        PublishedApplication.CalcFields(Installed);
        Assert.IsTrue(PublishedApplication.Installed,
            'Installed must compute true from that row, through BC''s own Exist FlowField.');

        // The negative half: the FlowField selects on the runtime package id rather than
        // answering true for anything. An id nobody published has no 2000000212 row.
        Assert.IsFalse(
            InstalledApplication.Get(CreateGuid(), ''),
            'Installed Application must not answer for a runtime package id nothing was seeded under.');
    end;

    [Test]
    procedure TenantVisibleAndPerTenantOrInstalledReadTrueThroughNavAppExtra()
    var
        PublishedApplication: Record "Published Application";
        Mi: ModuleInfo;
    begin
        // THE RUNNER-SIDE HALF of upstream's two "NAV App Extra" FlowField tests (#3072,
        // corpus PR #228). Upstream asserts the observable on a real tier; this asserts that
        // the runner reaches the same answer through the mechanism BC uses, which upstream
        // cannot see.
        //
        //   field(30; "Tenant Visible";         Lookup("NAV App Extra"."Tenant Visible"         WHERE(...)))
        //   field(31; "PerTenant Or Installed"; Lookup("NAV App Extra"."PerTenant Or Installed" WHERE(...)))
        //
        // "NAV App Extra" (2000000157) is a VIRTUAL table - System.app ships it under
        // src/Virtual Tables/ and Ncl's own NavAppExtraDataProvider computes every row from
        // the session's app metadata. So there is no row to seed the way there is for
        // Installed Application (2000000212); the runner answers by providing the rows the
        // way BC's provider would.
        //
        // WHY TRUE AND NOT FALSE IS THE ASSERTION. Both columns are Boolean, so an
        // implementation that computes NOTHING reads false, and so does one that computes
        // "no". Pinning false would pass against either, which is exactly how the Installed
        // FlowField's wrong reading survived until #3066 measured it. True can only come from
        // a computation.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        Assert.IsTrue(PublishedApplication.FindFirst(), 'The bundle under test must have a Published Application row.');

        PublishedApplication.CalcFields("Tenant Visible", "PerTenant Or Installed");
        Assert.IsTrue(PublishedApplication."Tenant Visible",
            'Tenant Visible must compute true through NAV App Extra for an app this session loaded.');
        Assert.IsTrue(PublishedApplication."PerTenant Or Installed",
            'PerTenant Or Installed must compute true through NAV App Extra for an app this session loaded.');
    end;

    [Test]
    procedure NavAppExtraAnswersPerRuntimePackageIdRatherThanForEverything()
    var
        PublishedApplication: Record "Published Application";
        NavAppExtra: Record "NAV App Extra";
        Mi: ModuleInfo;
        Rpid: Guid;
        SeededCount: Integer;
    begin
        // The negative half, and what stops "answer true to everything" passing the test
        // above. NAV App Extra is keyed on "Runtime Package ID": a row exists for each app
        // this session loaded and carries THAT app's package id, and an id nobody loaded has
        // no row at all. A provider that returned one row for every key, or answered a
        // constant, fails here.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.FindFirst();
        Rpid := PublishedApplication."Runtime Package ID";

        Assert.IsTrue(NavAppExtra.Get(Rpid),
            'NAV App Extra must have a row for an app this session loaded.');
        Assert.AreEqual(PublishedApplication."Package ID", NavAppExtra."Package ID",
            'The NAV App Extra row must carry the same package id as the published row, not a second value.');
        Assert.IsTrue(NavAppExtra."Tenant Visible", 'The row itself must carry Tenant Visible true, not just the FlowField.');
        Assert.IsTrue(NavAppExtra."PerTenant Or Installed", 'The row itself must carry PerTenant Or Installed true.');

        Assert.IsFalse(NavAppExtra.Get(CreateGuid()),
            'NAV App Extra must not answer for a runtime package id nothing was loaded under.');

        // EVERY app the runner listed as published must have its own row here, checked row by
        // row rather than by comparing two counts. Two equal counts prove nothing when both
        // are 1, and they would also be satisfied by a table carrying the right NUMBER of
        // rows under the wrong keys - which is the state that makes some apps read true and
        // others false, from a table that looks the right size.
        Clear(PublishedApplication);
        PublishedApplication.FindSet();
        repeat
            Clear(NavAppExtra);
            Assert.IsTrue(
                NavAppExtra.Get(PublishedApplication."Runtime Package ID"),
                'Every published app needs its own NAV App Extra row, or that app reads false.');
            Assert.AreEqual(
                PublishedApplication."Package ID", NavAppExtra."Package ID",
                'Each row must carry its own app''s package id, not another app''s.');
            Assert.IsTrue(NavAppExtra."Tenant Visible", 'Every published app must read visible.');
            Assert.IsTrue(NavAppExtra."PerTenant Or Installed", 'Every published app must read per-tenant or installed.');
            SeededCount += 1;
        until PublishedApplication.Next() = 0;

        // And nothing EXTRA: a row the runner cannot name a published app for would answer
        // true for an id no Published Application row carries.
        Clear(NavAppExtra);
        Assert.AreEqual(SeededCount, NavAppExtra.Count(),
            'NAV App Extra must carry exactly one row per published app - no more.');
        Assert.IsTrue(SeededCount > 0, 'The loop above must have checked at least one app.');
    end;

    [Test]
    procedure TenantIdIsBlankSoBcsOwnFilterFindsTheRow()
    var
        PublishedApplication: Record "Published Application";
        Mi: ModuleInfo;
    begin
        // ModuleOwnsTable filters Tenant ID with '%1|%2' against ('', the tenant id). Blank is
        // the value that matches without the runner inventing a tenant id it does not have —
        // and this is the filter, not the field, so it fails if the runner ever fills it in.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.SetFilter("Tenant ID", '%1|%2', '', 'some-other-tenant');
        Assert.IsTrue(
            PublishedApplication.FindFirst(),
            'BC''s own Tenant ID filter must find the row the runner seeded.');
    end;

    [Test]
    procedure PackageIdsDiscriminateBetweenApps()
    var
        PublishedApplication: Record "Published Application";
        Seen: List of [Guid];
        Rpid: Guid;
        Empty: Guid;
    begin
        // The property the whole mechanism rests on, over EVERY row the runner seeded — not
        // just the two apps a corpus bundle happens to carry. Every row must have a non-blank
        // Runtime Package ID and no two rows may share one.
        //
        // What this deliberately no longer asserts: that a row's two package columns differ
        // from each other. They are EQUAL on a real tier for a freshly published app (all eight
        // OnPrem legs, 27.0-28.4, BusinessCentral.AL.Language.Tests#187), the runner matches
        // that since #3066, and the within-a-row relation is upstream's claim to make.
        PublishedApplication.FindSet();
        repeat
            Rpid := PublishedApplication."Runtime Package ID";
            Assert.AreNotEqual(Empty, Rpid,
                'Every listed app needs a Runtime Package ID; a blank one matches every other blank.');
            Assert.IsFalse(Seen.Contains(Rpid),
                'Two apps must not share a Runtime Package ID — then either would own the other''s tables.');
            Seen.Add(Rpid);
        until PublishedApplication.Next() = 0;

        Assert.AreEqual(PublishedApplication.Count(), Seen.Count(),
            'Every row must have been checked.');
    end;

    [Test]
    procedure OwnObjectsCarryTheirOwnAppsRuntimePackageId()
    var
        PublishedApplication: Record "Published Application";
        AllObj: Record AllObj;
        Mi: ModuleInfo;
    begin
        // The actual predicate System Application evaluates, asserted end to end: a table this
        // app owns carries this app's runtime package id in AllObj, and it is the same value as
        // this app's Published Application row.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.FindFirst();

        AllObj.Get(AllObj."Object Type"::Table, Database::"PAST Owned");
        Assert.AreEqual(
            PublishedApplication."Runtime Package ID", AllObj."App Runtime Package ID",
            'An object this app owns must carry this app''s runtime package id, or ModuleOwnsTable declines.');
        Assert.AreEqual(
            PublishedApplication."Package ID", AllObj."App Package ID",
            'The same has to hold for the App Package ID column.');
    end;

    [Test]
    procedure AnotherAppsObjectDoesNotCarryThisAppsPackageId()
    var
        PublishedApplication: Record "Published Application";
        AllObj: Record AllObj;
        Mi: ModuleInfo;
    begin
        // The negative half of the test above, and what stops "stamp everything with one value"
        // passing it. AllObjWithCaption (2000000058) is a platform object this app does not own.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.FindFirst();

        AllObj.Get(AllObj."Object Type"::Table, Database::AllObjWithCaption);
        Assert.AreNotEqual(
            PublishedApplication."Runtime Package ID", AllObj."App Runtime Package ID",
            'A platform object must not carry this app''s runtime package id — then this app would own it.');
    end;
}
