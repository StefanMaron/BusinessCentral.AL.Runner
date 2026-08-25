// Two-bundle #2008 reproducer, test half. The reporter's exact command line
// passed TWO bundle paths — an extension app and a separate test app that
// depends on it — not one self-contained bundle. field-virtual-table-item-
// tracking (a single-bundle reproducer with the subscriber inlined into the
// test codeunit) already proved the surface works standalone; this bundle
// exists to prove (or disprove) that going through a SEPARATE, non-test,
// automatically-bound extension codeunit changes anything.
codeunit 61251 "FVTITX Whse Flow Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "FVTITX Assert";
        LibraryPurchase: Codeunit "Library - Purchase";
        LibraryInventory: Codeunit "Library - Inventory";
        LibraryItemTracking: Codeunit "Library - Item Tracking";
        LibraryWarehouse: Codeunit "Library - Warehouse";
        LibraryUtility: Codeunit "Library - Utility";

    [Test]
    procedure PurchLine2ReceiptLine_LotTrackedWithReservation_ExtensionSubscriberLookupWorks()
    var
        WarehouseSetup: Record "Warehouse Setup";
        PurchasesPayablesSetup: Record "Purchases & Payables Setup";
        SourceCodeSetup: Record "Source Code Setup";
        ItemTrackingCode: Record "Item Tracking Code";
        Item: Record Item;
        WarehouseReceiptHeader: Record "Warehouse Receipt Header";
        PurchaseHeader: Record "Purchase Header";
        PurchaseLine: Record "Purchase Line";
        ReservEntry: Record "Reservation Entry";
        PurchasesWarehouseManagement: Codeunit "Purchases Warehouse Mgt.";
        Subscriber: Codeunit "FVTIT Ext Whse Subscriber";
    begin
        LibraryWarehouse.NoSeriesSetup(WarehouseSetup);

        PurchasesPayablesSetup.Get();
        PurchasesPayablesSetup.Validate("Order Nos.", LibraryUtility.GetGlobalNoSeriesCode());
        PurchasesPayablesSetup.Modify(true);

        // See field-virtual-table-item-tracking/WarehouseReceiptFlowTests.Codeunit.al for why
        // this is needed (unrelated, already-documented Company-Initialize gap).
        if not SourceCodeSetup.Get() then begin
            SourceCodeSetup.Init();
            SourceCodeSetup.Insert();
        end;

        LibraryItemTracking.CreateLotItem(Item);
        ItemTrackingCode.Get(Item."Item Tracking Code");

        LibraryPurchase.CreatePurchaseDocumentWithItem(
            PurchaseHeader, PurchaseLine, PurchaseHeader."Document Type"::Order,
            '', Item."No.", 4, '', 0D);

        ReservEntry.Init();
        ReservEntry."Entry No." := 1;
        ReservEntry."Item No." := Item."No.";
        ReservEntry."Source Type" := Database::"Purchase Line";
        ReservEntry."Source Subtype" := PurchaseLine."Document Type".AsInteger();
        ReservEntry."Source ID" := PurchaseLine."Document No.";
        ReservEntry."Source Ref. No." := PurchaseLine."Line No.";
        ReservEntry."Qty. to Handle (Base)" := 2;
        ReservEntry."Item Tracking" := ReservEntry."Item Tracking"::"Lot No.";
        ReservEntry."Lot No." := 'ALR-LOT-1';
        ReservEntry.Insert();

        LibraryWarehouse.CreateWarehouseReceiptHeader(WarehouseReceiptHeader);

        // Codeunit 61200 in the DEPENDENCY app is a regular, non-test, automatically-bound
        // subscriber (not manually bound like the single-bundle reproducer) — the closest
        // match to "the extension subscribes to the standard event" from #2008.
        PurchasesWarehouseManagement.PurchLine2ReceiptLine(WarehouseReceiptHeader, PurchaseLine);

        Assert.IsTrue(Subscriber.GetLookupRan(), 'The extension subscriber must have run and reached the item-tracking lookup');
        Assert.IsTrue(Subscriber.GetLookupFound(), 'The seeded lot-tracked Reservation Entry must be found by ItemTrackingExistsOnDocumentLine');
    end;
}
