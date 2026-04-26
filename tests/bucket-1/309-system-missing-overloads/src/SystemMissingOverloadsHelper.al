/// Helper codeunit for testing missing System overloads from issue #1375.
codeunit 309901 "Sys Missing Overloads Helper"
{
    // ── CalcDate(Text, Date) ──────────────────────────────────────────────────

    procedure CalcDateTextDate(formula: Text; baseDate: Date): Date
    begin
        exit(CalcDate(formula, baseDate));
    end;

    // ── Clear(Joker) — variant variable ──────────────────────────────────────

    procedure ClearVariant(var v: Variant)
    begin
        Clear(v);
    end;

    // ── Clear(SecretText) ────────────────────────────────────────────────────

    procedure ClearSecret(var s: SecretText)
    begin
        Clear(s);
    end;

    procedure MakeSecret(t: Text): SecretText
    var
        st: SecretText;
    begin
        st := t;
        exit(st);
    end;

    // ── Format(Joker, Integer, Text) — format string overload ────────────────

    procedure FormatWithMask(value: Integer; length: Integer; mask: Text): Text
    begin
        exit(Format(value, length, mask));
    end;

    procedure FormatDecimalWithMask(value: Decimal; length: Integer; mask: Text): Text
    begin
        exit(Format(value, length, mask));
    end;

    // ── GetLastErrorText(Boolean) — clearError flag ───────────────────────────

    procedure GetLastErrorWithClear(clearError: Boolean): Text
    begin
        exit(GetLastErrorText(clearError));
    end;

    procedure TriggerError(msg: Text)
    begin
        Error(msg);
    end;

    // ── GetUrl 6-arg (Table, Boolean) ─────────────────────────────────────────

    procedure GetUrlSixArgTable(): Text
    var
        SomeRec: Record "Sys MO Dummy";
    begin
        exit(GetUrl(ClientType::Web, 'CRONUS', ObjectType::Page, 22, SomeRec, false));
    end;

    // ── GetUrl 7-arg (Table, Boolean, Text) ──────────────────────────────────

    procedure GetUrlSevenArgTable(): Text
    var
        SomeRec: Record "Sys MO Dummy";
    begin
        exit(GetUrl(ClientType::Web, 'CRONUS', ObjectType::Page, 22, SomeRec, false, 'No=1..10'));
    end;

    // ── GetUrl 6-arg (RecordRef, Boolean) ────────────────────────────────────

    procedure GetUrlSixArgRecordRef(): Text
    var
        RR: RecordRef;
        SomeRec: Record "Sys MO Dummy";
    begin
        RR.Open(Database::"Sys MO Dummy");
        exit(GetUrl(ClientType::Web, 'CRONUS', ObjectType::Page, 22, RR, false));
    end;

    // ── GetUrl 7-arg (RecordRef, Boolean, Text) ───────────────────────────────

    procedure GetUrlSevenArgRecordRef(): Text
    var
        RR: RecordRef;
    begin
        RR.Open(Database::"Sys MO Dummy");
        exit(GetUrl(ClientType::Web, 'CRONUS', ObjectType::Page, 22, RR, false, 'No=1..10'));
    end;
}
