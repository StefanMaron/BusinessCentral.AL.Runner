table 84001 "VR Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Quantity; Decimal) { }
        field(4; Count; Integer) { }
        field(5; Active; Boolean) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

// Renumbered from 84001 to avoid collision in new bucket layout (#1385).
codeunit 1084001 "Variant Record Helper"
{
    /// <summary>
    /// Simulates a workflow event handler that receives a record via Variant.
    /// This is the common BC pattern for workflow event handlers.
    /// </summary>
    procedure ExtractDescriptionFromVariant(RecVariant: Variant): Text[100]
    var
        VRItem: Record "VR Item";
    begin
        VRItem := RecVariant;
        exit(VRItem.Description);
    end;

    /// <summary>
    /// Stores a Record into a Variant and returns it, exercising the round-trip.
    /// </summary>
    procedure WrapRecordInVariant(var VRItem: Record "VR Item"; var RecVariant: Variant)
    begin
        RecVariant := VRItem;
    end;

    /// <summary>
    /// Extracts a record from a Variant and returns the Quantity field.
    /// </summary>
    procedure GetQuantityFromVariant(RecVariant: Variant): Decimal
    var
        VRItem: Record "VR Item";
    begin
        VRItem := RecVariant;
        exit(VRItem.Quantity);
    end;
}
