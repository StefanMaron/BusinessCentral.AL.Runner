codeunit 88000 "VI Src"
{
    procedure IsJsonObjectTrue(): Boolean
    var
        v: Variant;
        j: JsonObject;
    begin
        v := j;
        exit(v.IsJsonObject());
    end;

    procedure IsJsonObjectFalse(): Boolean
    var
        v: Variant;
        i: Integer;
    begin
        v := i;
        exit(v.IsJsonObject());
    end;

    procedure IsJsonArrayTrue(): Boolean
    var
        v: Variant;
        a: JsonArray;
    begin
        v := a;
        exit(v.IsJsonArray());
    end;

    procedure IsJsonArrayFalseWhenObject(): Boolean
    var
        v: Variant;
        j: JsonObject;
    begin
        v := j;
        exit(v.IsJsonArray());
    end;

    procedure IsJsonTokenTrueForObject(): Boolean
    var
        v: Variant;
        j: JsonObject;
    begin
        v := j;
        exit(v.IsJsonToken());
    end;

    procedure IsJsonTokenTrueForArray(): Boolean
    var
        v: Variant;
        a: JsonArray;
    begin
        v := a;
        exit(v.IsJsonToken());
    end;

    procedure IsJsonTokenFalse(): Boolean
    var
        v: Variant;
        i: Integer;
    begin
        v := i;
        exit(v.IsJsonToken());
    end;

    procedure IsJsonValueTrue(): Boolean
    var
        v: Variant;
        jv: JsonValue;
    begin
        v := jv;
        exit(v.IsJsonValue());
    end;

    procedure IsJsonValueFalse(): Boolean
    var
        v: Variant;
        j: JsonObject;
    begin
        v := j;
        exit(v.IsJsonValue());
    end;

    procedure IsNotificationTrue(): Boolean
    var
        v: Variant;
        n: Notification;
    begin
        v := n;
        exit(v.IsNotification());
    end;

    procedure IsNotificationFalse(): Boolean
    var
        v: Variant;
        i: Integer;
    begin
        v := i;
        exit(v.IsNotification());
    end;

    procedure IsTextBuilderTrue(): Boolean
    var
        v: Variant;
        tb: TextBuilder;
    begin
        v := tb;
        exit(v.IsTextBuilder());
    end;

    procedure IsTextBuilderFalse(): Boolean
    var
        v: Variant;
        i: Integer;
    begin
        v := i;
        exit(v.IsTextBuilder());
    end;

    procedure IsListTrue(): Boolean
    var
        v: Variant;
        lst: List of [Text];
    begin
        v := lst;
        exit(v.IsList());
    end;

    procedure IsListFalse(): Boolean
    var
        v: Variant;
        i: Integer;
    begin
        v := i;
        exit(v.IsList());
    end;

    procedure IsJsonObjectFalseForArray(): Boolean
    var
        v: Variant;
        a: JsonArray;
    begin
        v := a;
        exit(v.IsJsonObject());
    end;
}
