/// Source helpers for the not-tested sweep: simple type overloads.
/// Covers (issue #1400):
///   BigText.AddText (Text, Integer)
///   BigText.GetSubText (Text, Integer, Integer)
///   Boolean.ToText (Boolean)
///   Byte.ToText (Boolean)
///   Decimal.ToText (Boolean)
///   Guid.ToText (Boolean)
///   FieldRef.FieldError (Text)
///   Notification.AddAction (Text, Integer, Text, Text)
codeunit 313500 "Sweep Simple Src"
{
    // ── BigText.AddText (Text, Integer) ──────────────────────────────────────
    // Inserts text at a 1-based position within an existing BigText.

    procedure BigText_AddText_TextAtPos(base: Text; insert: Text; pos: Integer): Text
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(base);
        BT.AddText(insert, pos);
        BT.GetSubText(Result, 1);
        exit(Result);
    end;

    // ── BigText.GetSubText (Text, Integer, Integer) ─────────────────────────
    // Extracts a substring of a specified length from a BigText into a Text var.

    procedure BigText_GetSubText_TextFromTo(input: Text; fromPos: Integer; len: Integer): Text
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(input);
        BT.GetSubText(Result, fromPos, len);
        exit(Result);
    end;

    // ── Boolean.ToText (Boolean) ─────────────────────────────────────────────
    // Format(BoolValue, 0, '<Standard Format,2>') returns '1'/'0'.

    procedure Boolean_ToText_Raw(b: Boolean): Text
    begin
        exit(Format(b, 0, '<Standard Format,2>'));
    end;

    // ── Byte.ToText (Boolean) ───────────────────────────────────────────────
    // Same Format trick for a Byte (Character) value.

    procedure Byte_ToText_Raw(b: Byte): Text
    begin
        exit(Format(b, 0, '<Standard Format,2>'));
    end;

    // ── Decimal.ToText (Boolean) ────────────────────────────────────────────

    procedure Decimal_ToText_Raw(d: Decimal): Text
    begin
        exit(Format(d, 0, '<Standard Format,2>'));
    end;

    // ── Guid.ToText (Boolean) ────────────────────────────────────────────────
    // Format(GuidValue, 0, '<Standard Format,2>') — the 'raw' format.

    procedure Guid_ToText_Raw(g: Guid): Text
    begin
        exit(Format(g, 0, '<Standard Format,2>'));
    end;

    // ── FieldRef.FieldError (Text) ───────────────────────────────────────────
    // FieldError with a custom text message.

    procedure FieldRef_FieldError_Text(): Text
    var
        Rec: Record "SST Rec";
        RecRef: RecordRef;
        FR: FieldRef;
        Caught: Text;
    begin
        Rec.Init();
        Rec.Id := 1;
        RecRef.GetTable(Rec);
        FR := RecRef.Field(2);  // Name field
        asserterror FR.FieldError('Custom error message for Name');
        Caught := GetLastErrorText();
        exit(Caught);
    end;

    // ── Notification.AddAction (Text, Integer, Text, Text) ──────────────────
    // 4-arg variant includes a description parameter.

    procedure Notification_AddAction_4Arg_NoOp(): Integer
    var
        N: Notification;
    begin
        N.AddAction('Dismiss', Codeunit::"Sweep Simple Src", 'DummyHandler', 'some-params');
        exit(1); // just to prove it didn't throw
    end;

    /// Dummy handler for AddAction — must exist for AL compilation.
    procedure DummyHandler(N: Notification)
    begin
    end;
}

table 313500 "SST Rec"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
