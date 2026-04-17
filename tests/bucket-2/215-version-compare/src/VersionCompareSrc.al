/// Exercises Version comparison operators (==, <, >, <=, >=).
codeunit 60430 "VC Src"
{
    procedure IsGreater(v1: Version; v2: Version): Boolean
    begin
        exit(v1 > v2);
    end;

    procedure IsLess(v1: Version; v2: Version): Boolean
    begin
        exit(v1 < v2);
    end;

    procedure IsEqual(v1: Version; v2: Version): Boolean
    begin
        exit(v1 = v2);
    end;

    procedure IsGreaterOrEqual(v1: Version; v2: Version): Boolean
    begin
        exit(v1 >= v2);
    end;

    procedure IsLessOrEqual(v1: Version; v2: Version): Boolean
    begin
        exit(v1 <= v2);
    end;

    procedure IsNotEqual(v1: Version; v2: Version): Boolean
    begin
        exit(v1 <> v2);
    end;
}
