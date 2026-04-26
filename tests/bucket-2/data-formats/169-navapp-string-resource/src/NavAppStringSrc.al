// Test for NavApp.GetResource* with string resource names — issue #1107.
// BC may emit the resource name as a C# string literal (CS1503: string → NavText).
// MockNavApp now has string overloads for ALGetResource, ALGetResourceAsText, and
// ALGetResourceAsJson to handle this.
codeunit 169001 "NASR Source"
{
    procedure GetResourceText(ResourceName: Text): Text
    var
        Result: Text;
    begin
        // NavApp.GetResourceAsText with a Text variable — emitted as NavText arg
        Result := NavApp.GetResourceAsText(ResourceName);
        exit(Result);
    end;
}
