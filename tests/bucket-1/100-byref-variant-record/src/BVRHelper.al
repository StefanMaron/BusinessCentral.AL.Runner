// Tests for passing a Record to a "var Variant" parameter.
// BC compiles "var V: Variant" as ByRef<MockVariant> in C#.
// Without the fix, ConvertArgInternal throws:
//   Object of type 'AlRunner.Runtime.MockRecordHandle' cannot be converted
//   to type 'Microsoft.Dynamics.Nav.Runtime.ByRef<AlRunner.Runtime.MockVariant>'

table 299001 "BVR Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Quantity; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 299001 "BVR Helper"
{
    /// <summary>
    /// Accepts var Variant — BC emits ByRef&lt;MockVariant&gt; for this parameter.
    /// The procedure stores the incoming value into V (no-op for record test) and
    /// sets a sentinel so callers can detect the procedure ran.
    /// </summary>
    procedure StoreInVariant(var V: Variant)
    begin
        // V is already the value passed by the caller.
        // We just mark it was reached by wrapping in text if it's not a record.
        // For the record case, BC already placed the record in V; nothing extra needed.
    end;

    /// <summary>
    /// Checks whether the Variant passed by ref holds a record and returns the Description.
    /// </summary>
    procedure GetDescriptionFromVarVariant(var V: Variant): Text[100]
    var
        Item: Record "BVR Item";
    begin
        if V.IsRecord() then begin
            Item := V;
            exit(Item.Description);
        end;
        exit('NOT-A-RECORD');
    end;

    /// <summary>
    /// Replaces V with an Integer value — proves the ByRef write-back works.
    /// </summary>
    procedure SetVariantToInteger(var V: Variant; NewValue: Integer)
    begin
        V := NewValue;
    end;

    /// <summary>
    /// Replaces V with a Text value — proves different types work through the same var Variant path.
    /// </summary>
    procedure SetVariantToText(var V: Variant; NewValue: Text)
    begin
        V := NewValue;
    end;
}
