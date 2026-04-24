codeunit 1281001 "JSON Get 3-Arg Helper"
{
    /// <summary>
    /// Helper that exercises 3-arg GetInteger, GetBoolean, and GetDecimal
    /// on a JsonObject, mirroring the BC compiler's 3-arg overloads.
    /// </summary>

    procedure GetIntegerFromJson(JsonText: Text; PropertyName: Text; RequireValue: Boolean): Integer
    var
        JsonObj: JsonObject;
    begin
        JsonObj.ReadFrom(JsonText);
        exit(JsonObj.GetInteger(PropertyName, RequireValue));
    end;

    procedure GetBooleanFromJson(JsonText: Text; PropertyName: Text; RequireValue: Boolean): Boolean
    var
        JsonObj: JsonObject;
    begin
        JsonObj.ReadFrom(JsonText);
        exit(JsonObj.GetBoolean(PropertyName, RequireValue));
    end;

    procedure GetDecimalFromJson(JsonText: Text; PropertyName: Text; RequireValue: Boolean): Decimal
    var
        JsonObj: JsonObject;
    begin
        JsonObj.ReadFrom(JsonText);
        exit(JsonObj.GetDecimal(PropertyName, RequireValue));
    end;
}
