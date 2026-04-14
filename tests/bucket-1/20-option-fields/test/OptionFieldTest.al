codeunit 50920 "Option Field Tests"
{
    Subtype = Test;

    var
        Processor: Codeunit "Order Status Processor";
        Assert: Codeunit Assert;

    [Test]
    procedure TestCreateOrderDefaultStatus()
    var
        Ord: Record "Demo Order";
    begin
        // [GIVEN] A new order is created
        Processor.CreateOrder('ORD-001', 'Test Order');

        // [WHEN] Reading it back
        Ord.Get('ORD-001');

        // [THEN] Status should be Draft (0)
        Assert.AreEqual(Ord."Status"::Draft, Ord."Status", 'New order should have Draft status');
    end;

    [Test]
    procedure TestApproveOrder()
    var
        Ord: Record "Demo Order";
    begin
        // [GIVEN] An existing order
        Processor.CreateOrder('ORD-002', 'Approval Test');

        // [WHEN] Approving it
        Processor.ApproveOrder('ORD-002');

        // [THEN] Status should be Approved
        Ord.Get('ORD-002');
        Assert.AreEqual(Ord."Status"::Approved, Ord."Status", 'Order should be Approved');
    end;

    [Test]
    procedure TestRejectOrder()
    var
        Ord: Record "Demo Order";
    begin
        Processor.CreateOrder('ORD-003', 'Rejection Test');
        Processor.RejectOrder('ORD-003');

        Ord.Get('ORD-003');
        Assert.AreEqual(Ord."Status"::Rejected, Ord."Status", 'Order should be Rejected');
    end;

    [Test]
    procedure TestIsFinalized_Approved()
    begin
        Processor.CreateOrder('ORD-004', 'Finalized Approved');
        Processor.ApproveOrder('ORD-004');

        Assert.IsTrue(Processor.IsFinalized('ORD-004'), 'Approved order should be finalized');
    end;

    [Test]
    procedure TestIsFinalized_Draft()
    begin
        Processor.CreateOrder('ORD-005', 'Not Finalized');

        Assert.IsFalse(Processor.IsFinalized('ORD-005'), 'Draft order should not be finalized');
    end;

    [Test]
    procedure TestGetStatus()
    var
        Status: Enum "Order Status";
        Ord: Record "Demo Order";
    begin
        Processor.CreateOrder('ORD-006', 'Status Check');
        Processor.ApproveOrder('ORD-006');

        Ord.Get('ORD-006');
        Assert.AreEqual(Ord."Status"::Approved, Processor.GetStatus('ORD-006'), 'GetStatus should return Approved');
    end;
}
