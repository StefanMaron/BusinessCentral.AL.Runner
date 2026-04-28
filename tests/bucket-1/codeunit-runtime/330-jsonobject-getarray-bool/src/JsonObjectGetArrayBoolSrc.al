codeunit 1320416 "JsonObject Bool Src"
{
    procedure GetObjectRequireExists(): Boolean
    var
        Obj: JsonObject;
        Child: JsonObject;
    begin
        Child.Add('flag', true);
        Obj.Add('child', Child);
        exit(Obj.GetObject('child', true).Contains('flag'));
    end;

    procedure GetObjectRequireExistsMissing(): Boolean
    var
        Obj: JsonObject;
        Child: JsonObject;
    begin
        Child := Obj.GetObject('missing', true);
        exit(Child.Contains('flag'));
    end;

    procedure GetObjectMissingNoError(): Boolean
    var
        Obj: JsonObject;
        Child: JsonObject;
    begin
        Child := Obj.GetObject('missing', false);
        exit(Child.Contains('flag'));
    end;

    procedure GetArrayRequireExists(): Integer
    var
        Obj: JsonObject;
        Arr: JsonArray;
    begin
        Arr.Add(1);
        Arr.Add(2);
        Obj.Add('nums', Arr);
        exit(Obj.GetArray('nums', true).Count());
    end;

    procedure GetArrayRequireExistsMissing(): Integer
    var
        Obj: JsonObject;
        Arr: JsonArray;
    begin
        Arr := Obj.GetArray('missing', true);
        exit(Arr.Count());
    end;

    procedure GetArrayMissingNoError(): Integer
    var
        Obj: JsonObject;
        Arr: JsonArray;
    begin
        Arr := Obj.GetArray('missing', false);
        exit(Arr.Count());
    end;
}
