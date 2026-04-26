/// Helper codeunit exercising XmlProcessingInstruction navigation/tree methods (issue #780).
codeunit 107100 "XPI Pi Src"
{
    // WriteTo(var Text) — serialize to <?target data?> string
    procedure WriteToText(Target: Text; Data: Text): Text
    var
        PI: XmlProcessingInstruction;
        Result: Text;
    begin
        PI := XmlProcessingInstruction.Create(Target, Data);
        PI.WriteTo(Result);
        exit(Result);
    end;

    // AsXmlNode — returns an XmlNode wrapping the PI
    procedure CreateAsXmlNode(Target: Text; Data: Text): XmlNode
    var
        PI: XmlProcessingInstruction;
    begin
        PI := XmlProcessingInstruction.Create(Target, Data);
        exit(PI.AsXmlNode());
    end;

    // GetParent — false when detached, true after attaching to element
    procedure GetParentDetached(): Boolean
    var
        PI: XmlProcessingInstruction;
        Parent: XmlElement;
    begin
        PI := XmlProcessingInstruction.Create('test', 'data');
        exit(PI.GetParent(Parent));
    end;

    procedure GetParentAttached(): Boolean
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Parent: XmlElement;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('test', 'data');
        Root.Add(PI);
        exit(PI.GetParent(Parent));
    end;

    // GetDocument — false when detached, true when in a document
    procedure GetDocumentDetached(): Boolean
    var
        PI: XmlProcessingInstruction;
        OutDoc: XmlDocument;
    begin
        PI := XmlProcessingInstruction.Create('test', 'data');
        exit(PI.GetDocument(OutDoc));
    end;

    procedure GetDocumentInDoc(): Boolean
    var
        Doc: XmlDocument;
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        OutDoc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Doc.Add(Root);
        PI := XmlProcessingInstruction.Create('test', 'data');
        Root.Add(PI);
        exit(PI.GetDocument(OutDoc));
    end;

    // Remove — child count drops to 0 after detach
    procedure RemoveFromParent(): Integer
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Nodes: XmlNodeList;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('test', 'data');
        Root.Add(PI);
        PI.Remove();
        Nodes := Root.GetChildNodes();
        exit(Nodes.Count);
    end;

    // AddAfterSelf — parent gets 2 children
    procedure AddAfterSelf(): Integer
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Sibling: XmlProcessingInstruction;
        Nodes: XmlNodeList;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('first', 'a');
        Root.Add(PI);
        Sibling := XmlProcessingInstruction.Create('second', 'b');
        PI.AddAfterSelf(Sibling);
        Nodes := Root.GetChildNodes();
        exit(Nodes.Count);
    end;

    // AddBeforeSelf — parent gets 2 children
    procedure AddBeforeSelf(): Integer
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Sibling: XmlProcessingInstruction;
        Nodes: XmlNodeList;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('first', 'a');
        Root.Add(PI);
        Sibling := XmlProcessingInstruction.Create('before', 'b');
        PI.AddBeforeSelf(Sibling);
        Nodes := Root.GetChildNodes();
        exit(Nodes.Count);
    end;

    // ReplaceWith — parent child count stays 1
    procedure ReplaceWith(): Integer
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Replacement: XmlElement;
        Nodes: XmlNodeList;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('old', 'data');
        Root.Add(PI);
        Replacement := XmlElement.Create('replaced');
        PI.ReplaceWith(Replacement);
        Nodes := Root.GetChildNodes();
        exit(Nodes.Count);
    end;

    // SelectNodes — returns non-negative count
    procedure SelectNodesCount(): Integer
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        NodeList: XmlNodeList;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('test', 'data');
        Root.Add(PI);
        PI.SelectNodes('processing-instruction()', NodeList);
        exit(NodeList.Count);
    end;

    // SelectSingleNode — returns boolean
    procedure SelectSingleNodeFound(): Boolean
    var
        Root: XmlElement;
        PI: XmlProcessingInstruction;
        Result: XmlNode;
    begin
        Root := XmlElement.Create('root');
        PI := XmlProcessingInstruction.Create('test', 'data');
        Root.Add(PI);
        exit(PI.SelectSingleNode('processing-instruction()', Result));
    end;
}
