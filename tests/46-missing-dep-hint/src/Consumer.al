namespace My.App;

using System.Reflection;

codeunit 50460 "MDH Consumer"
{
    procedure IsHelloText(Value: Variant): Boolean
    var
        TypeHelper: Codeunit "Type Helper";
    begin
        exit(TypeHelper.IsText(Value));
    end;
}
