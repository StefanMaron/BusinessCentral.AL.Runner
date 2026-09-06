// Issue #2381 — a subscriber declared by a PRECOMPILED dependency must reach the User system table.
//
// #2381 reported that "Base App table-trigger event subscribers on the User system table never
// fire". Measured on main at 718a9384 (BC 28.1.49838.53910) that is not what happens: Base
// Application codeunit 418 "User Management" raises from both arms, and two of the three
// Microsoft tests the issue named -- Codeunit139460's CannotAddNewUserOfLimitedLicenseTypeInSaaS
// and CannotModifyUserLicenseTypeToLimitedInSaaS -- pass. This suite exists so that stays true:
// the discovery scan in AlRunner/Patches/EventSubscriberPatches.cs reads the Base Application's
// R2R chunks through AssemblyTypeIndex and injects what it finds onto the User metatable, and
// nothing else in the repository fails if that stops happening for a precompiled dependency.
//
// The AL under test is Microsoft's, unmodified:
//
//     [EventSubscriber(ObjectType::Table, Database::User, 'OnAfterInsertEvent', '', false, true)]
//     local procedure ValidateLicenseTypeOnAfterInsertUser(var Rec: Record User; RunTrigger: Boolean)
//     [EventSubscriber(ObjectType::Table, Database::User, 'OnAfterModifyEvent', '', false, true)]
//     local procedure ValidateLicenseTypeOnAfterModifyUser(var Rec: Record User; var xRec: Record User; RunTrigger: Boolean)
//
// both of which call ValidateLicenseTypeOnSaaS, which raises only when EnvironmentInformation
// .IsSaaS() is true AND the licence type is outside the supported set. So each raising test
// carries a matching NON-raising control on the SAME code path: a "Full User" is a supported
// type and must insert and modify cleanly. Without those controls a broken build that refused
// every User write would look green here.
//
// UtbsSaaSIsThePrecondition is not an assertion about what BC ought to report -- it is the
// precondition the two raising tests rest on, asserted separately so that if the runner ever
// stops presenting itself as SaaS the failure names that cause instead of looking like a
// subscriber-dispatch regression.
codeunit 65631 "UTBS Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "UTBS Assert";

    local procedure SupportedLicenceTypeErrorFragment(): Text
    begin
        // Stable across BC 27.0, 27.5 and 28.4 -- verified against the "Base Application" .app of
        // each. The sentence that ENUMERATES the supported types is not stable (28.x lists Agent,
        // earlier builds do not), so only the invariant tail is asserted.
        exit('are supported in the online environment.');
    end;

    local procedure NewUser(var User: Record User; UserName: Code[50]; LicenseType: Option)
    begin
        Clear(User);
        User.Init();
        User."User Security ID" := CreateGuid();
        User."User Name" := UserName;
        User."License Type" := LicenseType;
    end;

    [Test]
    procedure UtbsSaaSIsThePrecondition()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        // Base App codeunit 418's ValidateLicenseTypeOnSaaS exits without raising unless this is
        // true, so the two raising tests below would silently become vacuous if it ever changed.
        Assert.IsTrue(
          EnvironmentInformation.IsSaaS(),
          'the runner presents itself as SaaS; codeunit 418 raises only under SaaS, so the ' +
          'licence-type tests in this suite mean nothing without it');
    end;

    [Test]
    procedure UtbsInsertArmRaisesForAnUnsupportedLicenceType()
    var
        User: Record User;
    begin
        // OnAfterInsertEvent -> Base Application codeunit 418 ValidateLicenseTypeOnAfterInsertUser.
        NewUser(User, 'UTBS-INS-LIMITED', User."License Type"::"Limited User");
        asserterror User.Insert(true);
        Assert.ExpectedError(SupportedLicenceTypeErrorFragment());
    end;

    [Test]
    procedure UtbsInsertArmAllowsASupportedLicenceType()
    var
        User: Record User;
        Reread: Record User;
    begin
        // The control for the test above: the subscriber must be SELECTIVE, not a blanket refusal
        // of every User insert.
        NewUser(User, 'UTBS-INS-FULL', User."License Type"::"Full User");
        User.Insert(true);
        Assert.IsTrue(Reread.Get(User."User Security ID"), 'the Full User row must be readable back');
        Assert.AreEqual('UTBS-INS-FULL', Reread."User Name", 'the row read back must be the row inserted');
        Assert.IsTrue(
          Reread."License Type" = Reread."License Type"::"Full User",
          'the licence type must survive the insert as Full User');
    end;

    [Test]
    procedure UtbsModifyArmRaisesForAnUnsupportedLicenceType()
    var
        User: Record User;
    begin
        // OnAfterModifyEvent -> Base Application codeunit 418 ValidateLicenseTypeOnAfterModifyUser.
        // The insert half must succeed first, which also proves the two arms are injected
        // independently rather than one standing in for the other.
        NewUser(User, 'UTBS-MOD-LIMITED', User."License Type"::"Full User");
        User.Insert(true);
        User."License Type" := User."License Type"::"Limited User";
        asserterror User.Modify(true);
        Assert.ExpectedError(SupportedLicenceTypeErrorFragment());
    end;

    [Test]
    procedure UtbsModifyArmAllowsASupportedLicenceType()
    var
        User: Record User;
        Reread: Record User;
    begin
        // The control for the test above.
        NewUser(User, 'UTBS-MOD-FULL', User."License Type"::"Full User");
        User.Insert(true);
        User."Full Name" := 'Renamed by UTBS';
        User.Modify(true);
        Assert.IsTrue(Reread.Get(User."User Security ID"), 'the modified row must still be readable');
        Assert.AreEqual('Renamed by UTBS', Reread."Full Name", 'the modification must have persisted');
    end;
}
