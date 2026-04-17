/// Exercises XmlProcessingInstruction — Create, GetTarget, GetData,
/// SetTarget, SetData, WriteTo.
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
}
