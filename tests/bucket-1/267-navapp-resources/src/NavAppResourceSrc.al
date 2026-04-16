/// Helper codeunit that exercises NavApp resource methods in standalone mode.
codeunit 85000 "NavApp Resource Src"
{
    procedure GetTextResource(ResourceName: Text): Text
    var
        ResourceText: Text;
    begin
        NavApp.GetResourceAsText(ResourceName, ResourceText);
        exit(ResourceText);
    end;

    procedure GetJsonResource(ResourceName: Text): JsonToken
    var
        Token: JsonToken;
    begin
        NavApp.GetResourceAsJson(ResourceName, Token);
        exit(Token);
    end;

    procedure ListAllResources(): Integer
    var
        ResourceList: List of [Text];
    begin
        NavApp.ListResources(ResourceList);
        exit(ResourceList.Count());
    end;
}
