/// Helper codeunit for Clear(arr[i]) on arrays of complex types — issue #1448.
/// BC emits arr.Clear(int) for the AL built-in Clear(arr[n]) but
/// MockArray<T> was missing the single-int overload.
codeunit 312100 "ACSI Src"
{
    /// Populate both slots of an XmlNodeList array, clear slot [1],
    /// and return the count in slot [2] — proving that only slot [1] was cleared
    /// and that Clear(arr[i]) compiles and runs without CS1503.
    procedure ClearNodeListArrayElement(): Integer
    var
        NodeListArr: array[2] of XmlNodeList;
        Root: XmlElement;
    begin
        // Slot [1]: build an XmlNodeList with one child (will be cleared)
        Root := XmlElement.Create('root1');
        Root.Add(XmlElement.Create('child'));
        NodeListArr[1] := Root.GetChildNodes();

        // Slot [2]: list with 3 children — must be untouched
        Root := XmlElement.Create('root2');
        Root.Add(XmlElement.Create('x'));
        Root.Add(XmlElement.Create('y'));
        Root.Add(XmlElement.Create('z'));
        NodeListArr[2] := Root.GetChildNodes();

        // Clear the first slot — this is the pattern that caused CS1503
        Clear(NodeListArr[1]);

        // Slot [2] must still report 3 children
        exit(NodeListArr[2].Count());
    end;

    /// Slot [2] must still have its 2 children after only slot [1] was cleared.
    procedure NodeListSlot2UnaffectedBySlot1Clear(): Integer
    var
        NodeListArr: array[2] of XmlNodeList;
        Root: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Root.Add(XmlElement.Create('a'));
        Root.Add(XmlElement.Create('b'));
        NodeListArr[1] := Root.GetChildNodes();
        NodeListArr[2] := Root.GetChildNodes();

        Clear(NodeListArr[1]);

        exit(NodeListArr[2].Count());
    end;
}
