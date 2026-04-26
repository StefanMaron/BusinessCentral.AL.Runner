codeunit 59100 "List Processor"
{
    /// Appends JsonObject items to a var List parameter (cross-codeunit ByRef).
    procedure BuildJsonList(var Items: List of [JsonObject])
    var
        JObj: JsonObject;
    begin
        JObj.Add('name', 'Alice');
        Items.Add(JObj);

        Clear(JObj);
        JObj.Add('name', 'Bob');
        Items.Add(JObj);
    end;

    /// Same pattern with List of [Text] to verify generic NavList<T> handling.
    procedure BuildTextList(var Items: List of [Text])
    begin
        Items.Add('Hello');
        Items.Add('World');
        Items.Add('!');
    end;

    /// Mixed parameters: non-var + var list, to test arg position handling.
    procedure FilterList(Prefix: Text; var Items: List of [Text])
    var
        i: Integer;
        Item: Text;
        Filtered: List of [Text];
    begin
        foreach Item in Items do
            if Item.StartsWith(Prefix) then
                Filtered.Add(Item);
        Items := Filtered;
    end;

    /// Overloaded procedure: 2-arg version (adds a specific JsonObject).
    procedure AddToList(JObj: JsonObject; var Items: List of [JsonObject])
    begin
        Items.Add(JObj);
    end;

    /// Overloaded procedure: 1-arg version (adds a default JsonObject).
    /// BC compiler emits a suffixed C# method name for the second overload,
    /// which requires the Invoke dispatch to match by member-ID suffix.
    procedure AddToList(var Items: List of [JsonObject])
    var
        JObj: JsonObject;
    begin
        JObj.Add('source', 'default');
        Items.Add(JObj);
    end;
}
