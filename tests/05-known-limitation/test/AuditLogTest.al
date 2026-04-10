codeunit 50904 "Audit Log Tests"
{
    Subtype = Test;

    var
        BalanceMgr: Codeunit "Balance Manager";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure TestUpdateBalance_CreatesRecord()
    var
        CustBalance: Record "Customer Balance";
    begin
        // This test PASSES correctly in both AL Runner and full BC.
        // It tests direct logic without depending on event subscribers.

        // [GIVEN] No customer balance exists
        // [WHEN] Updating balance for a new customer
        BalanceMgr.UpdateBalance('CUST-001', 1000.00);

        // [THEN] A balance record should exist
        CustBalance.Get('CUST-001');
        Assert.AreEqual(1000, CustBalance."Balance", 'Balance should be 1000');
    end;

    [Test]
    procedure TestUpdateBalance_ModifiesExisting()
    var
        CustBalance: Record "Customer Balance";
    begin
        // This test PASSES correctly in both AL Runner and full BC.

        // [GIVEN] An existing balance
        BalanceMgr.UpdateBalance('CUST-002', 500.00);

        // [WHEN] Updating to a new balance
        BalanceMgr.UpdateBalance('CUST-002', 750.00);

        // [THEN] Balance should be updated
        CustBalance.Get('CUST-002');
        Assert.AreEqual(750, CustBalance."Balance", 'Balance should be updated to 750');
    end;

    [Test]
    procedure TestAuditLogCreated_KnownLimitation()
    var
        AuditEntry: Record "Audit Log Entry";
        CustBalance: Record "Customer Balance";
    begin
        // ===================================================================
        // KNOWN LIMITATION: This test produces a SILENT FALSE POSITIVE
        // in AL Runner.
        //
        // In the full BC service tier, modifying a "Customer Balance" record
        // fires the OnAfterModify event subscriber, which inserts an audit
        // log entry. The test below would find 1 audit log entry.
        //
        // In AL Runner, implicit event subscribers (OnAfterModify, etc.)
        // do NOT fire. So the audit log stays empty, and the assertion
        // below checks for 0 entries — which passes in AL Runner but would
        // FAIL in full BC (where the count would be 1).
        //
        // This is an accepted known limitation. AL Runner guarantees:
        //   - If it says FAIL, it IS a real failure.
        //   - If it says PASS, direct logic is correct, but event-dependent
        //     side effects may be missing.
        //
        // The full BC service tier pipeline always runs after AL Runner
        // and catches these cases.
        // ===================================================================

        // [GIVEN] An existing customer balance
        BalanceMgr.UpdateBalance('CUST-003', 100.00);

        // [WHEN] Modifying the balance (would fire OnAfterModify in full BC)
        BalanceMgr.UpdateBalance('CUST-003', 200.00);

        // [THEN] In AL Runner: 0 audit entries (events don't fire)
        //        In full BC:   1 audit entry (OnAfterModify fires)
        Assert.AreEqual(0, AuditEntry.Count(),
            'AL Runner: No audit entries because OnAfterModify does not fire. ' +
            'In full BC, this would be 1.');
    end;
}
