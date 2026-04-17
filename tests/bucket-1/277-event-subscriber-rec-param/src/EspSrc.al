/// Event Subscriber Record Parameter tests (issue #818).
/// Proves that subscribers receiving var Rec: Record X and Sender: Codeunit X
/// can read and modify the record, with changes visible to the event publisher's caller.

// ── Shared table ──────────────────────────────────────────────────────────────
table 118000 "ESP Item"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
        field(2; Name; Text[50]) { DataClassification = ToBeClassified; }
        field(3; Price; Decimal) { DataClassification = ToBeClassified; }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

// ── Publisher codeunit ────────────────────────────────────────────────────────
codeunit 118001 "ESP Publisher"
{
    var Tag: Text[50];

    [IntegrationEvent(false, false)]
    procedure OnBeforeProcess(var Item: Record "ESP Item"; var Cancel: Boolean)
    begin
    end;

    [IntegrationEvent(true, false)]
    procedure OnAfterProcess(var Item: Record "ESP Item")
    begin
    end;

    procedure GetTag(): Text[50]
    begin
        exit(Tag);
    end;

    procedure SetTag(NewTag: Text[50])
    begin
        Tag := NewTag;
    end;

    /// Returns false if a subscriber cancelled, true otherwise.
    procedure ProcessItem(ItemId: Integer): Boolean
    var
        Item: Record "ESP Item";
        Cancel: Boolean;
    begin
        Item.Get(ItemId);
        Cancel := false;
        OnBeforeProcess(Item, Cancel);
        if Cancel then
            exit(false);
        // Apply a fixed markup if nobody cancelled
        Item.Price := Item.Price * 2;
        Item.Modify();
        OnAfterProcess(Item);
        exit(true);
    end;
}

// ── Subscriber codeunit (no-sender) ──────────────────────────────────────────
codeunit 118002 "ESP Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"ESP Publisher", 'OnBeforeProcess', '', true, true)]
    local procedure HandleBeforeProcess(var Item: Record "ESP Item"; var Cancel: Boolean)
    begin
        // Modify the record name so the test can detect the subscriber ran
        Item.Name := 'Touched by subscriber';
        // Cancel if price > 100
        if Item.Price > 100 then
            Cancel := true;
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"ESP Publisher", 'OnAfterProcess', '', true, true)]
    local procedure HandleAfterProcess(var Sender: Codeunit "ESP Publisher"; var Item: Record "ESP Item")
    begin
        // Use the Sender to call a method on the publisher
        Sender.SetTag('AfterProcessFired');
        Item.Name := 'AfterProcessTouched';
        Item.Modify();
    end;
}
