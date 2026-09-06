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
        // "Tenant Visible" and "PerTenant Or Installed" are deliberately NOT asserted here.
        // They are Lookup FlowFields over "NAV App Extra" (2000000157), which System.app ships
        // as a VIRTUAL table, so there is no row for the runner to seed the way there is for
        // 2000000212, and no service tier has been asked what it reports for them (#3072).
        // An unmeasured pin is exactly what #3066 had to undo.
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
