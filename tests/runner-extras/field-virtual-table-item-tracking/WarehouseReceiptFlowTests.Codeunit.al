// Full-flow reproducer for #2008: the exact call chain the reporter used —
// PurchasesWarehouseManagement.PurchLine2ReceiptLine -> Whse.-Create Source
// Document.SetQtysOnRcptLine -> OnAfterSetQtysOnRcptLine subscriber -> a
// Reservation Entry item-tracking lookup. The direct SetSourceFilter probe in
// FieldVirtualTableItemTrackingTests.Codeunit.al already proves the Field
// virtual table works when called directly; this bundle exists to prove (or
// disprove) that going through the real subscriber-dispatch path changes
// anything.
codeunit 61102 "FVTIT Whse Flow Tests"
{
    Subtype = Test;
    EventSubscriberInstance = Manual;

    var
        Assert: Codeunit "FVTIT Assert";
        LibraryPurchase: Codeunit "Library - Purchase";
        LibraryInventory: Codeunit "Library - Inventory";
        LibraryItemTracking: Codeunit "Library - Item Tracking";
        LibraryWarehouse: Codeunit "Library - Warehouse";
        LibraryUtility: Codeunit "Library - Utility";

    [Test]
    procedure PurchLine2ReceiptLine_LotTrackedWithReservation_TriggersFieldVirtualTableLookupInSubscriber()
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
    begin
        BindSubscription(this);

        LibraryWarehouse.NoSeriesSetup(WarehouseSetup);

        PurchasesPayablesSetup.Get();
        PurchasesPayablesSetup.Validate("Order Nos.", LibraryUtility.GetGlobalNoSeriesCode());
        PurchasesPayablesSetup.Modify(true);

        // Company-Initialize (codeunit 2) does not run to completion in this runner today
        // (a Manufacturing subscriber NREs partway through InitSourceCodeSetup — see
        // AlRunner/CompanyInitializer.cs "KNOWN INCOMPLETE"). That is a separate, already
        // documented gap unrelated to #2008: it blocks Purchase Header's dimension-default
        // lookup (CreateDim -> SourceCodeSetup.Get()) before this test ever reaches the
        // Field-virtual-table surface under test. Insert the blank singleton row directly so
        // this test proves #2008, not the unrelated company-init gap.
        if not SourceCodeSetup.Get() then begin
            SourceCodeSetup.Init();
            SourceCodeSetup.Insert();
        end;

        // Lot-tracked item, matching #2008's exact setup.
        LibraryItemTracking.CreateLotItem(Item);
        ItemTrackingCode.Get(Item."Item Tracking Code");

        LibraryPurchase.CreatePurchaseDocumentWithItem(
            PurchaseHeader, PurchaseLine, PurchaseHeader."Document Type"::Order,
            '', Item."No.", 4, '', 0D);

        // A lot-tracked Reservation Entry with Qty. to Handle (Base) = 2, sourced to this
        // purchase line — the exact seed #2008 describes.
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
