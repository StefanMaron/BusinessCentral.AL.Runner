codeunit 83800 "JV Conv Src"
{
    procedure TextRoundTrip(): Text
    var
        JV: JsonValue;
    begin
        JV.SetValue('hello');
        exit(JV.AsText());
    end;

    procedure IntegerRoundTrip(): Integer
    var
        JV: JsonValue;
    begin
        JV.SetValue(42);
        exit(JV.AsInteger());
    end;

    procedure BooleanTrueRoundTrip(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValue(true);
        exit(JV.AsBoolean());
    end;

    procedure BooleanFalseRoundTrip(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValue(false);
        exit(JV.AsBoolean());
    end;

    procedure DecimalRoundTrip(): Decimal
    var
        JV: JsonValue;
    begin
        JV.SetValue(3.14);
        exit(JV.AsDecimal());
    end;

    procedure NullIsNull(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValueToNull();
        exit(JV.IsNull());
    end;

    procedure NonNullIsNotNull(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValue('data');
        exit(JV.IsNull());
    end;

    procedure TextFromToken(): Text
    var
        JA: JsonArray;
        JT: JsonToken;
    begin
        JA.Add('world');
        JA.Get(0, JT);
        exit(JT.AsValue().AsText());
    end;

    procedure IntegerFromToken(): Integer
    var
        JA: JsonArray;
        JT: JsonToken;
    begin
        JA.Add(99);
        JA.Get(0, JT);
        exit(JT.AsValue().AsInteger());
    end;

    procedure BooleanFromToken(): Boolean
    var
        JA: JsonArray;
        JT: JsonToken;
    begin
        JA.Add(true);
        JA.Get(0, JT);
        exit(JT.AsValue().AsBoolean());
    end;

    procedure TextFromObjectGet(): Text
    var
        JO: JsonObject;
        JT: JsonToken;
    begin
        JO.Add('name', 'Alice');
        JO.Get('name', JT);
        exit(JT.AsValue().AsText());
    end;

    procedure IntegerFromObjectGet(): Integer
    var
        JO: JsonObject;
        JT: JsonToken;
    begin
        JO.Add('count', 7);
        JO.Get('count', JT);
        exit(JT.AsValue().AsInteger());
    end;
}
