/// Source codeunit exercising AL interface `is` and `as` operators — issue #972.
/// BC emits myVar.IsInterfaceOfType(id) for `is` and myVar.AsInterfaceOfType(id) for `as`.

interface ITC_Base
{
    procedure GetValue(): Integer;
}

interface ITC_Extended
{
    procedure GetValue(): Integer;
    procedure GetExtra(): Integer;
}

codeunit 135001 "ITC BaseOnly" implements ITC_Base
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}

codeunit 135002 "ITC Extended" implements ITC_Base, ITC_Extended
{
    procedure GetValue(): Integer
    begin
        exit(20);
    end;

    procedure GetExtra(): Integer
    begin
        exit(99);
    end;
}

codeunit 135003 "ITC Source"
{
    /// Returns true if v also implements ITC_Extended (AL `is` operator).
    procedure IsExtended(v: Interface ITC_Base): Boolean
    begin
        if v is ITC_Extended then
            exit(true);
        exit(false);
    end;

    /// Casts v to ITC_Extended and returns GetExtra() result (AL `as` operator).
    procedure AsExtendedGetExtra(v: Interface ITC_Base): Integer
    var
        ext: Interface ITC_Extended;
    begin
        ext := v as ITC_Extended;
        exit(ext.GetExtra());
    end;
}
