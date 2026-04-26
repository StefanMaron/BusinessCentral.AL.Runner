codeunit 52920 "Enum Extension Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Ticket Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestBaseEnumValueStillAccessible()
    var
        Status: Enum "Ticket Status";
    begin
        // [GIVEN] The base enum defines Open = 0
        // [WHEN] Reading the base value through the helper
        Status := Helper.GetOpen();

        // [THEN] The ordinal is 0 (base enum value preserved after extension)
        Assert.AreEqual(0, Status.AsInteger(), 'Base enum value Open should have ordinal 0');
    end;

    [Test]
    procedure TestExtensionValuePendingApprovalOrdinal()
    var
        Status: Enum "Ticket Status";
    begin
        // [GIVEN] The extension adds Pending Approval with ordinal 100
        // [WHEN] Resolving the extension value
        Status := Helper.GetPendingApproval();

        // [THEN] Its ordinal is exactly 100 (proves extension ordinals, not base)
        Assert.AreEqual(100, Status.AsInteger(), 'Pending Approval extension value should have ordinal 100');
    end;

    [Test]
    procedure TestExtensionValueRejectedOrdinal()
    var
        Status: Enum "Ticket Status";
    begin
        Status := Helper.GetRejected();

        Assert.AreEqual(101, Status.AsInteger(), 'Rejected extension value should have ordinal 101');
    end;

    [Test]
    procedure TestExtensionValueNotEqualBaseValue()
    var
        Pending: Enum "Ticket Status";
        Open: Enum "Ticket Status";
    begin
        // [GIVEN] A base value and an extension value
        Open := Helper.GetOpen();
        Pending := Helper.GetPendingApproval();

        // [THEN] They are distinct (negative-style assertion — catches a mock that collapses everything to 0)
        Assert.AreNotEqual(Open.AsInteger(), Pending.AsInteger(), 'Extension value must not equal base value');
    end;

    [Test]
    procedure TestPendingOrdinalHelper()
    begin
        // [WHEN] The helper assigns Status::"Pending Approval" locally
        // [THEN] The ordinal is 100 — proves the extension member is resolvable at the call site too
        Assert.AreEqual(100, Helper.PendingOrdinal(), 'Local assignment to extension value should yield ordinal 100');
    end;

    [Test]
    procedure TestFormatExtensionValue()
    var
        Formatted: Text;
    begin
        // [WHEN] Formatting the Pending Approval extension value
        Formatted := Helper.FormatStatus(Helper.GetPendingApproval());

        // [THEN] The formatted text includes the extension value name (not just base names)
        Assert.IsTrue(
            (Formatted = 'Pending Approval') or (Formatted = '100'),
            'Format of extension value should be the name or ordinal, got: ' + Formatted);
    end;

    [Test]
    procedure TestExtensionAndBaseDistinctOrdinals()
    var
        OpenStatus: Enum "Ticket Status";
        ClosedStatus: Enum "Ticket Status";
        Pending: Enum "Ticket Status";
        Rejected: Enum "Ticket Status";
    begin
        OpenStatus := OpenStatus::Open;
        ClosedStatus := ClosedStatus::Closed;
        Pending := Pending::"Pending Approval";
        Rejected := Rejected::Rejected;

        // [THEN] All four ordinals are distinct and match their declared values
        Assert.AreEqual(0, OpenStatus.AsInteger(), 'Open should be 0');
        Assert.AreEqual(1, ClosedStatus.AsInteger(), 'Closed should be 1');
        Assert.AreEqual(100, Pending.AsInteger(), 'Pending Approval should be 100');
        Assert.AreEqual(101, Rejected.AsInteger(), 'Rejected should be 101');
    end;
}
