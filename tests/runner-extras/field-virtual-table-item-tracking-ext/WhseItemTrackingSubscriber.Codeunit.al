// Non-test half of the two-bundle #2008 reproducer. Mirrors the issue's exact
// description: "The extension subscribes to the standard event
// Whse.-Create Source Document.OnAfterSetQtysOnRcptLine and calls the standard
// tracking API [ItemTrackingManagement.ItemTrackingExistsOnDocumentLine]." This
// lives in a REGULAR (non-test) codeunit with automatic event binding — not a
// manually-bound test-codeunit subscriber — because that is what "an extension
// subscribes" means for an installed app.
codeunit 61200 "FVTIT Ext Whse Subscriber"
{
    SingleInstance = true;

    var
        LookupRan: Boolean;
        LookupFound: Boolean;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Whse.-Create Source Document", 'OnAfterSetQtysOnRcptLine', '', false, false)]
    local procedure OnAfterSetQtysOnRcptLine(var WarehouseReceiptLine: Record "Warehouse Receipt Line"; Qty: Decimal; QtyBase: Decimal)
    var
        ItemTrackingManagement: Codeunit "Item Tracking Management";
    begin
        LookupFound := ItemTrackingManagement.ItemTrackingExistsOnDocumentLine(
            WarehouseReceiptLine."Source Type", WarehouseReceiptLine."Source Subtype",
            WarehouseReceiptLine."Source No.", WarehouseReceiptLine."Source Line No.");
        LookupRan := true;
    end;

    procedure GetLookupRan(): Boolean
    begin
        exit(LookupRan);
    end;

    procedure GetLookupFound(): Boolean
    begin
        exit(LookupFound);
    end;
}
