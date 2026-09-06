// Issue #2963. Published Application (2000000206) on the runner: where the rows come from when
// nothing was ever published, and the property that makes them mean anything.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   What this table CONTAINS on a real tier is plain BC behaviour and BELONGS upstream. It
//   cannot go there, and unlike its neighbour Object (2000000001, suite
//   runner-extras/object-system-table) that is established by a DIRECT measurement of THIS
//   table rather than by membership in a set whose refusal was measured on a sibling id:
//   compiling `Record "Published Application"` in a Cloud-target bundle reports
//
//     error AL0296: The application object or method 'Published Application' has scope
//                   'OnPrem' and cannot be used for 'Cloud' development
//
//   and the corpus app is "target": "Cloud". So this suite targets OnPrem.
//
//   The AL-observable CONSEQUENCE of these rows does go upstream and is service-tier
//   adjudicated there: an app may register its own table on the retention-policy allowed list
//   and only its own, BusinessCentral.AL.Language.Tests#181. That is the BC claim. What stays
//   here is what the RUNNER does to make it true.
//
// WHAT THE RUNNER DOES, AND WHY THE LAST TEST IS THE IMPORTANT ONE
//   On a real tier these rows exist because publishing an app wrote them. The runner never
//   publishes anything, so it seeds one row per loaded app from the manifests it already parses
//   for NavApp.GetModuleInfo, and stamps a deterministic per-app pair of package ids onto both
//   that row and that app's AllObj rows.
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
    procedure FlowFieldColumnsReadBlankRatherThanCarryingAFabricatedValue()
    var
        PublishedApplication: Record "Published Application";
        Mi: ModuleInfo;
    begin
        // Installed and Tenant Visible are FlowFields on this table. A runner with no
        // application database has nothing to compute them from, and — the reason this test
        // exists — a WRITE to either is silently discarded, so seeding them would look like an
        // answer and be none. They must read as their computed default, not as a value the
        // runner invented. Same shape as the blank-column pins in
        // runner-extras/object-system-table.
        NavApp.GetCurrentModuleInfo(Mi);
        PublishedApplication.SetRange(ID, Mi.Id());
        PublishedApplication.FindFirst();
        PublishedApplication.CalcFields(Installed, "Tenant Visible");

        Assert.IsFalse(PublishedApplication.Installed,
            'Installed is a FlowField with nothing behind it here; a true would be fabricated.');
        Assert.IsFalse(PublishedApplication."Tenant Visible",
            'Tenant Visible is a FlowField with nothing behind it here.');
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
        // The property the whole mechanism rests on. Every row must carry a non-blank Runtime
        // Package ID, no two rows may share one, and a row's two package columns must differ
        // from each other — real BC assigns them independently, and making them equal would let
        // a comparison of one column against the other silently succeed.
        PublishedApplication.FindSet();
        repeat
            Rpid := PublishedApplication."Runtime Package ID";
            Assert.AreNotEqual(Empty, Rpid,
                'Every listed app needs a Runtime Package ID; a blank one matches every other blank.');
            Assert.AreNotEqual(Rpid, PublishedApplication."Package ID",
                'Package ID and Runtime Package ID must not be the same GUID for one app.');
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
