/// <summary>
/// #4770: tables that are not per company (DataPerCompany = false; `bcbak tables` lists them
/// under company `-`) hydrate from the backup, read without `--company`.
///
/// The subject is the Media family, because that is where an empty tenant table does damage:
/// a hydrated Media or MediaSet field carries an id whose row lives in `Tenant Media` /
/// `Tenant Media Set`, and with those empty the id names nothing. So the load-bearing
/// assertions are joins — the id on a hydrated company row must name a hydrated tenant row —
/// which a made-up id fails; the negative case pins that.
///
/// Rows are chosen from the same records TestDataLobValues reads, and no media id is written
/// as a literal: ids differ per backup build (#4645).
/// </summary>
codeunit 64411 "Test Data Tenant Tables"
{
    Subtype = Test;

    var
        TdfAssert: Codeunit "TDF Assert";

    [Test]
    procedure TenantMediaIsHydrated()
    var
        TenantMedia: Record "Tenant Media";
    begin
        TdfAssert.IsFalse(TenantMedia.IsEmpty(), 'Tenant Media must be hydrated from the backup');
    end;

    [Test]
    procedure AHydratedMediaIdNamesATenantMediaRow()
    var
        WordTemplate: Record "Word Template";
        Customer: Record Customer;
        TenantMedia: Record "Tenant Media";
    begin
        WordTemplate.Get('EVENT');
        TdfAssert.IsTrue(TenantMedia.Get(WordTemplate.Template.MediaId),
            'Word Template EVENT.Template must name a Tenant Media row');
        TdfAssert.IsTrue(TenantMedia.Content.HasValue(),
            'the Tenant Media row behind Word Template EVENT must carry its content');

        Customer.Get('10000');
        TdfAssert.IsTrue(TenantMedia.Get(Customer.Image.MediaId),
            'Customer 10000.Image must name a Tenant Media row');
        TdfAssert.AreEqual('image/jpeg', TenantMedia."Mime Type",
            'Customer 10000.Image is a picture in the backup');
    end;

    [Test]
    procedure AMadeUpMediaIdNamesNoTenantMediaRow()
    var
        TenantMedia: Record "Tenant Media";
    begin
        // The negative half of the join above: Get must not succeed for any id, or the
        // positive case proves nothing.
        TdfAssert.IsFalse(TenantMedia.IsEmpty(), 'precondition: Tenant Media is hydrated');
        TdfAssert.IsFalse(TenantMedia.Get(CreateGuid()), 'a fresh GUID must name no Tenant Media row');
    end;

    [Test]
    procedure AHydratedMediaSetIdNamesTenantMediaSetRows()
    var
        ItemVariant: Record "Item Variant";
        TenantMediaSet: Record "Tenant Media Set";
        TenantMedia: Record "Tenant Media";
    begin
        ItemVariant.Get('SP-SCM1006', 'BLACK');
        TenantMediaSet.SetRange(ID, ItemVariant.Picture.MediaId);
        TdfAssert.IsTrue(TenantMediaSet.FindFirst(),
            'Item Variant SP-SCM1006/BLACK.Picture must name a Tenant Media Set');
        TdfAssert.IsTrue(TenantMedia.Get(TenantMediaSet."Media ID".MediaId),
            'the media in that set must be a Tenant Media row');
    end;
}
