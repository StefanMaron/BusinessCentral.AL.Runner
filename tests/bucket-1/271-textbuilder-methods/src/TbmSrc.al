/// Helper codeunit exercising the 8 missing TextBuilder methods.
codeunit 84405 "TBM Src"
{
    // ── Insert ──────────────────────────────────────────────────────────────────
    procedure InsertAtPosition(base: Text; pos: Integer; toInsert: Text): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(base);
        TB.Insert(pos, toInsert);
        exit(TB.ToText());
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────
    procedure RemoveRange(base: Text; startPos: Integer; count: Integer): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(base);
        TB.Remove(startPos, count);
        exit(TB.ToText());
    end;

    // ── Replace ─────────────────────────────────────────────────────────────────
    procedure ReplaceText(base: Text; oldVal: Text; newVal: Text): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(base);
        TB.Replace(oldVal, newVal);
        exit(TB.ToText());
    end;

    // ── Length ──────────────────────────────────────────────────────────────────
    procedure GetLength(text: Text): Integer
    var
        TB: TextBuilder;
    begin
        TB.Append(text);
        exit(TB.Length);
    end;

    // ── Clear ───────────────────────────────────────────────────────────────────
    procedure AppendThenClear(text: Text): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(text);
        TB.Clear();
        exit(TB.ToText());
    end;

    // ── Capacity ────────────────────────────────────────────────────────────────
    procedure GetCapacity(): Integer
    var
        TB: TextBuilder;
    begin
        TB.Append('hello');
        exit(TB.Capacity);
    end;

    // ── MaxCapacity ─────────────────────────────────────────────────────────────
    procedure GetMaxCapacity(): Integer
    var
        TB: TextBuilder;
    begin
        exit(TB.MaxCapacity);
    end;

    // ── EnsureCapacity ──────────────────────────────────────────────────────────
    procedure EnsureAndGetCapacity(minCapacity: Integer): Integer
    var
        TB: TextBuilder;
    begin
        TB.EnsureCapacity(minCapacity);
        exit(TB.Capacity);
    end;
}
