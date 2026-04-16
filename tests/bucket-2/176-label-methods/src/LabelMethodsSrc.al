/// Helper codeunit exercising string-manipulation dot-methods on Label values.
codeunit 60010 "LBM Src"
{
    procedure LabelContains(substr: Text): Boolean
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.Contains(substr));
    end;

    procedure LabelStartsWith(prefix: Text): Boolean
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.StartsWith(prefix));
    end;

    procedure LabelEndsWith(suffix: Text): Boolean
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.EndsWith(suffix));
    end;

    procedure LabelToLower(): Text
    var
        lbl: Label 'HELLO World';
    begin
        exit(lbl.ToLower());
    end;

    procedure LabelToUpper(): Text
    var
        lbl: Label 'Hello world';
    begin
        exit(lbl.ToUpper());
    end;

    procedure LabelTrim(): Text
    var
        lbl: Label '  padded  ';
    begin
        exit(lbl.Trim());
    end;

    procedure LabelReplace(oldVal: Text; newVal: Text): Text
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.Replace(oldVal, newVal));
    end;

    procedure LabelIndexOf(sub: Text): Integer
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.IndexOf(sub));
    end;
}
