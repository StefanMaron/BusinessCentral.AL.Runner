interface IVCProcessor
{
    procedure Process(Value: Integer): Integer;
}

codeunit 233000 "VCU Doubler" implements IVCProcessor
{
    procedure Process(Value: Integer): Integer
    begin
        exit(Value * 2);
    end;
}

codeunit 233001 "VCU Tripler" implements IVCProcessor
{
    procedure Process(Value: Integer): Integer
    begin
        exit(Value * 3);
    end;
}

codeunit 233002 "VCU Dispatcher"
{
    /// <summary>
    /// Stores a codeunit reference in a Variant and then assigns it back.
    /// BC emits ALCompiler.NavIndirectValueToNavCodeunitHandle(variant) when
    /// extracting a codeunit from a Variant — the rewriter must handle this
    /// (mapping to (MockCodeunitHandle)(variant), mirroring NavIndirectValueToINavRecordHandle).
    /// </summary>
    procedure DispatchViaVariant(UseDoubler: Boolean; Value: Integer): Integer
    var
        Doubler: Codeunit "VCU Doubler";
        Tripler: Codeunit "VCU Tripler";
        V: Variant;
        Extracted: Codeunit "VCU Doubler";
        ExtractedTripler: Codeunit "VCU Tripler";
    begin
        if UseDoubler then begin
            V := Doubler;
            Extracted := V;
            exit(Extracted.Process(Value));
        end else begin
            V := Tripler;
            ExtractedTripler := V;
            exit(ExtractedTripler.Process(Value));
        end;
    end;

    /// <summary>
    /// Checks that Variant.IsCodeunit() returns true for a codeunit stored in a Variant.
    /// </summary>
    procedure IsCodeunitInVariant(): Boolean
    var
        Doubler: Codeunit "VCU Doubler";
        V: Variant;
    begin
        V := Doubler;
        exit(V.IsCodeunit());
    end;
}
