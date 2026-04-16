/// Helper codeunit that exercises NavApp resource methods in standalone mode.
codeunit 85100 "NavApp Resource Src"
{
    procedure GetTextResource(ResourceName: Text): Text
    begin
        exit(NavApp.GetResourceAsText(ResourceName));
    end;

    procedure GetJsonResource(ResourceName: Text): JsonToken
    begin
        exit(NavApp.GetResourceAsJson(ResourceName));
    end;

    procedure ListAllResources(): Integer
    var
        ResourceList: List of [Text];
    begin
        ResourceList := NavApp.ListResources();
        exit(ResourceList.Count());
    end;
}
