/// Tests that the Tenant Media system table (ID 2000000184) can be referenced
/// via the auto-stub mechanism without a NavMediaSystemRecord constructor error.
codeunit 303001 "TMD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TMD Helper";

    // ── Positive: IsEmpty returns true on a fresh store ───────────────────────

    [Test]
    procedure TenantMedia_IsEmpty_ReturnsTrueWhenNoRecords()
    begin
        // Positive: fresh runner store has no Tenant Media rows.
        Assert.IsTrue(Src.IsEmpty(), 'Tenant Media must be empty in a fresh runner store');
    end;

    // ── Positive: Count returns 0 on a fresh store ────────────────────────────

    [Test]
    procedure TenantMedia_Count_ReturnsZeroWhenEmpty()
    begin
        // Positive: count on an empty table must be zero, not a negative or crash.
        Assert.AreEqual(0, Src.CountRecords(), 'Tenant Media count must be 0 in a fresh runner store');
    end;
}
