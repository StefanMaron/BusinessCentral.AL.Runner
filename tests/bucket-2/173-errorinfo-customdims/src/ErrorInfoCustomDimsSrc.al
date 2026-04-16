/// Helper codeunit exercising ErrorInfo.CustomDimensions getter/setter.
codeunit 59940 "EICD Src"
{
    procedure SetAndGetCount(): Integer
    var
        ei: ErrorInfo;
        dims: Dictionary of [Text, Text];
    begin
        dims.Add('key1', 'value1');
        dims.Add('key2', 'value2');
        ei.CustomDimensions := dims;
        exit(ei.CustomDimensions.Count());
    end;

    procedure FreshCount(): Integer
    var
        ei: ErrorInfo;
    begin
        // A default-initialised ErrorInfo has an empty CustomDimensions dictionary.
        exit(ei.CustomDimensions.Count());
    end;

    procedure SetAndGetValue(dimKey: Text; dimValue: Text): Text
    var
        ei: ErrorInfo;
        dims: Dictionary of [Text, Text];
    begin
        dims.Add(dimKey, dimValue);
        ei.CustomDimensions := dims;
        exit(ei.CustomDimensions.Get(dimKey));
    end;

    procedure LastWriteWins_NewCount(): Integer
    var
        ei: ErrorInfo;
        first: Dictionary of [Text, Text];
        second: Dictionary of [Text, Text];
    begin
        first.Add('a', '1');
        first.Add('b', '2');
        first.Add('c', '3');
        ei.CustomDimensions := first;

        second.Add('x', '9');
        ei.CustomDimensions := second;

        exit(ei.CustomDimensions.Count());
    end;
}
