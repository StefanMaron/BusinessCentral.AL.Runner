codeunit 59551 "DBI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DBI Src";

    [Test]
    procedure SerialNumber_NonEmpty()
    begin
        // Positive: standalone SerialNumber stub must return a non-empty string —
        // consumers (telemetry, licensing) branch on empty vs non-empty and an
        // empty stub makes that branch indistinguishable from a genuine missing
        // tenant context.
        Assert.AreNotEqual('', Src.GetSerialNumber(),
            'Database.SerialNumber must return a non-empty string');
    end;

    [Test]
    procedure TenantId_NonEmpty()
    begin
        Assert.AreNotEqual('', Src.GetTenantId(),
            'Database.TenantId must return a non-empty string');
    end;

    [Test]
    procedure ServiceInstanceId_NonZero()
    begin
        // ServiceInstanceId is an Integer; stub must return a positive ID since
        // a valid BC service instance always has a non-zero ID.
        Assert.AreNotEqual(0, Src.GetServiceInstanceId(),
            'Database.ServiceInstanceId must return a non-zero integer');
    end;

    [Test]
    procedure SerialNumber_Stable()
    begin
        // Two consecutive reads must yield the same value — the stub must be a
        // fixed string, not a fresh random value per call.
        Assert.AreEqual(Src.GetSerialNumber(), Src.GetSerialNumber(),
            'Database.SerialNumber must be stable across reads');
    end;

    [Test]
    procedure TenantId_Stable()
    begin
        Assert.AreEqual(Src.GetTenantId(), Src.GetTenantId(),
            'Database.TenantId must be stable across reads');
    end;

    [Test]
    procedure ServiceInstanceId_Stable()
    begin
        Assert.AreEqual(Src.GetServiceInstanceId(), Src.GetServiceInstanceId(),
            'Database.ServiceInstanceId must be stable across reads');
    end;

    [Test]
    procedure ReadsComplete_NegativeTrap()
    var
        a: Text;
        b: Text;
        c: Integer;
    begin
        // Negative: guard against a throwing stub — reading all three must complete.
        a := Src.GetSerialNumber();
        b := Src.GetTenantId();
        c := Src.GetServiceInstanceId();
        Assert.IsTrue((a <> '') and (b <> '') and (c <> 0),
            'All three identity reads must complete and return non-trivial values');
    end;
}
