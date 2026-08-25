// Full-flow reproducer for #2008: the exact call chain the reporter used —
// PurchasesWarehouseManagement.PurchLine2ReceiptLine -> Whse.-Create Source
// Document.SetQtysOnRcptLine -> OnAfterSetQtysOnRcptLine subscriber -> a
// Reservation Entry item-tracking lookup. The direct SetSourceFilter probe in
// FieldVirtualTableItemTrackingTests.Codeunit.al already proves the Field
// virtual table works when called directly; this bundle exists to prove (or
// disprove) that going through the real subscriber-dispatch path changes
// anything.
//
// PurchaseLine/WarehouseReceiptHeader are built with raw field assignment +
// Insert(false) rather than Library-Purchase's Validate-heavy helpers — those
// helpers chain through Vendor/Dimension/User-Profile lookups that need a much
// larger default-company setup (General Ledger Setup, Marketing Setup, a
// resolvable user profile, ...) which is orthogonal to what #2008 is actually
// about and, on some runner builds, hits its own unrelated gaps before ever
// reaching PurchLine2ReceiptLine. PurchLine2ReceiptLine itself only reads
// fields off the PurchaseLine parameter — it never re-fetches Purchase Header
// or validates the line — so a raw, unvalidated PurchaseLine record is
// faithful to what the procedure actually consumes.
codeunit 61102 "FVTIT Whse Flow Tests"
{
    Subtype = Test;
    EventSubscriberInstance = Manual;

    var
        Assert: Codeunit "FVTIT Assert";
        LibraryWarehouse: Codeunit "Library - Warehouse";

    [Test]
    procedure PurchLine2ReceiptLine_LotTrackedWithReservation_TriggersFieldVirtualTableLookupInSubscriber()
    var
        WarehouseSetup: Record "Warehouse Setup";
        InventorySetup: Record "Inventory Setup";
        WarehouseReceiptHeader: Record "Warehouse Receipt Header";
        PurchaseLine: Record "Purchase Line";
        ReservEntry: Record "Reservation Entry";
        PurchasesWarehouseManagement: Codeunit "Purchases Warehouse Mgt.";
    begin
        BindSubscription(this);

        // Older runner builds (v2.3.1, the version #2008 was reported against) did not
        // seed the Warehouse Setup singleton row via install triggers; current main does.
        // Seed it explicitly so this test is portable across both, instead of silently
        // depending on install-trigger completeness (a separate, unrelated area).
        if not WarehouseSetup.Get() then begin
            WarehouseSetup.Init();
            WarehouseSetup.Insert();
        end;
        LibraryWarehouse.NoSeriesSetup(WarehouseSetup);

        if not InventorySetup.Get() then begin
            InventorySetup.Init();
            InventorySetup.Insert();
        end;

        // Raw Purchase Line, no Item/Vendor/Dimension machinery — see file header.
        PurchaseLine.Init();
        PurchaseLine."Document Type" := PurchaseLine."Document Type"::Order;
        PurchaseLine."Document No." := 'PO0001';
        PurchaseLine."Line No." := 10000;
        PurchaseLine.Type := PurchaseLine.Type::Item;
        PurchaseLine."No." := 'FVTIT-ITEM';
        PurchaseLine.Description := 'FVTIT lot-tracked item';
        PurchaseLine."Unit of Measure Code" := 'PCS';
        PurchaseLine."Qty. per Unit of Measure" := 1;
        PurchaseLine.Quantity := 4;
        PurchaseLine."Quantity (Base)" := 4;
        PurchaseLine."Quantity Received" := 0;
        PurchaseLine."Expected Receipt Date" := WorkDate();
        PurchaseLine.Insert(false);

        // A lot-tracked Reservation Entry with Qty. to Handle (Base) = 2, sourced to this
        // purchase line — the exact seed #2008 describes.
        ReservEntry.Init();
        ReservEntry."Entry No." := 1;
        ReservEntry."Item No." := PurchaseLine."No.";
        ReservEntry."Source Type" := Database::"Purchase Line";
        ReservEntry."Source Subtype" := PurchaseLine."Document Type".AsInteger();
        ReservEntry."Source ID" := PurchaseLine."Document No.";
        ReservEntry."Source Ref. No." := PurchaseLine."Line No.";
        ReservEntry."Qty. to Handle (Base)" := 2;
        ReservEntry."Item Tracking" := ReservEntry."Item Tracking"::"Lot No.";
        ReservEntry."Lot No." := 'ALR-LOT-1';
        ReservEntry.Insert();

        LibraryWarehouse.CreateWarehouseReceiptHeader(WarehouseReceiptHeader);

        // This is the exact standard procedure from #2008. It fires the
        // Whse.-Create Source Document.OnAfterSetQtysOnRcptLine event, which our
        // subscriber below uses to run the exact standard tracking API from the issue —
        // the surface the issue reported as throwing RunnerOutOfScopeException for the
        // Field virtual table (2000000041).
        PurchasesWarehouseManagement.PurchLine2ReceiptLine(WarehouseReceiptHeader, PurchaseLine);

        UnbindSubscription(this);

        Assert.IsTrue(FieldVirtualTableLookupRan, 'The OnAfterSetQtysOnRcptLine subscriber must have run and reached the item-tracking lookup');
        Assert.IsTrue(FieldVirtualTableLookupFound, 'The seeded lot-tracked Reservation Entry must be found by ItemTrackingExistsOnDocumentLine');
    end;

    var
        FieldVirtualTableLookupRan: Boolean;
        FieldVirtualTableLookupFound: Boolean;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Whse.-Create Source Document", 'OnAfterSetQtysOnRcptLine', '', false, false)]
    local procedure OnAfterSetQtysOnRcptLine(var WarehouseReceiptLine: Record "Warehouse Receipt Line"; Qty: Decimal; QtyBase: Decimal)
    var
        ItemTrackingManagement: Codeunit "Item Tracking Management";
    begin
        // The exact standard API from #2008.
        FieldVirtualTableLookupFound := ItemTrackingManagement.ItemTrackingExistsOnDocumentLine(
            WarehouseReceiptLine."Source Type", WarehouseReceiptLine."Source Subtype",
            WarehouseReceiptLine."Source No.", WarehouseReceiptLine."Source Line No.");
        FieldVirtualTableLookupRan := true;
    end;
}
