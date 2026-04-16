codeunit 50259 "Report Trigger State"
{
    SingleInstance = true;

    var
        _PreFired: Boolean;
        _PostFired: Boolean;
        _PreOrder: Integer;
        _PostOrder: Integer;
        _CallSeq: Integer;

    procedure SetPreFired()
    begin
        _CallSeq += 1;
        _PreFired := true;
        _PreOrder := _CallSeq;
    end;

    procedure SetPostFired()
    begin
        _CallSeq += 1;
        _PostFired := true;
        _PostOrder := _CallSeq;
    end;

    procedure WasPreFired(): Boolean
    begin
        exit(_PreFired);
    end;

    procedure WasPostFired(): Boolean
    begin
        exit(_PostFired);
    end;

    procedure PreBeforePost(): Boolean
    begin
        exit(_PreFired and _PostFired and (_PreOrder < _PostOrder));
    end;

    procedure Reset()
    begin
        _PreFired := false;
        _PostFired := false;
        _PreOrder := 0;
        _PostOrder := 0;
        _CallSeq := 0;
    end;
}
