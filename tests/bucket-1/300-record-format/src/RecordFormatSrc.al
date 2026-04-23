table 30000 "Record Format Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 30001 "Record Format Src"
{
    /// <summary>
    /// Format() a Record variable. In AL, Format(SomeRecord) returns the
    /// record's position string. The runner must not throw IConvertible.
    /// </summary>
    procedure FormatRecord(Rec: Record "Record Format Table"): Text
    begin
        exit(Format(Rec));
    end;

    /// <summary>
    /// Pass a Record to a method that accepts a Text parameter.
    /// This exercises ConvertArgInternal: MockRecordHandle -> string.
    /// </summary>
    procedure PassRecordAsText(Rec: Record "Record Format Table"): Text
    begin
        exit(AcceptText(Format(Rec)));
    end;

    local procedure AcceptText(T: Text): Text
    begin
        exit(T);
    end;

    /// <summary>
    /// Format a Record that has been populated with a key value.
    /// The result should be non-empty and contain the key.
    /// </summary>
    procedure FormatPopulatedRecord(): Text
    var
        Rec: Record "Record Format Table";
    begin
        Rec.Id := 42;
        Rec.Name := 'Test';
        Rec.Insert();
        Rec.Get(42);
        exit(Format(Rec));
    end;
}
