/// Exercises XmlProcessingInstruction — Create, GetTarget, GetData,
/// SetTarget, SetData, WriteTo, SelectNodes, SelectSingleNode.
codeunit 60260 "XPI Src"
{
    procedure CreateAndGetTarget(): Text
    var
        pi: XmlProcessingInstruction;
        result: Text;
    begin
        pi := XmlProcessingInstruction.Create('xml-stylesheet', 'type="text/css"');
        pi.GetTarget(result);
        exit(result);
    end;

    procedure CreateAndGetData(): Text
    var
        pi: XmlProcessingInstruction;
        result: Text;
    begin
        pi := XmlProcessingInstruction.Create('xml-stylesheet', 'type="text/css"');
        pi.GetData(result);
        exit(result);
    end;

    procedure SetTargetAndRead(newTarget: Text): Text
    var
        pi: XmlProcessingInstruction;
        result: Text;
    begin
        pi := XmlProcessingInstruction.Create('original', 'data');
        pi.SetTarget(newTarget);
        pi.GetTarget(result);
        exit(result);
    end;

    procedure SetDataAndRead(newData: Text): Text
    var
        pi: XmlProcessingInstruction;
        result: Text;
    begin
        pi := XmlProcessingInstruction.Create('target', 'original');
        pi.SetData(newData);
        pi.GetData(result);
        exit(result);
    end;

    // ── WriteTo ──────────────────────────────────────────────────────────────────

    procedure WriteToText(target: Text; data: Text): Text
    var
        pi: XmlProcessingInstruction;
        result: Text;
    begin
        pi := XmlProcessingInstruction.Create(target, data);
        pi.WriteTo(result);
        exit(result);
    end;

    // ── SelectNodes ──────────────────────────────────────────────────────────────
    // PI is attached to a document before XPath queries — required for XPath
    // navigation to work consistently across all BC runtime versions.

    procedure SelectNodesCount(target: Text; data: Text; xpath: Text): Integer
    var
        doc: XmlDocument;
        root: XmlElement;
        pi: XmlProcessingInstruction;
        nodeList: XmlNodeList;
    begin
        doc := XmlDocument.Create();
        root := XmlElement.Create('root');
        pi := XmlProcessingInstruction.Create(target, data);
        root.Add(pi);
        doc.Add(root);
        pi.SelectNodes(xpath, nodeList);
        exit(nodeList.Count());
    end;

    procedure SelectNodesReturns(target: Text; data: Text; xpath: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        pi: XmlProcessingInstruction;
        nodeList: XmlNodeList;
    begin
        doc := XmlDocument.Create();
        root := XmlElement.Create('root');
        pi := XmlProcessingInstruction.Create(target, data);
        root.Add(pi);
        doc.Add(root);
        exit(pi.SelectNodes(xpath, nodeList));
    end;

    // ── SelectSingleNode ─────────────────────────────────────────────────────────

    procedure SelectSingleNodeReturns(target: Text; data: Text; xpath: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        pi: XmlProcessingInstruction;
        found: XmlNode;
    begin
        doc := XmlDocument.Create();
        root := XmlElement.Create('root');
        pi := XmlProcessingInstruction.Create(target, data);
        root.Add(pi);
        doc.Add(root);
        exit(pi.SelectSingleNode(xpath, found));
    end;
}
