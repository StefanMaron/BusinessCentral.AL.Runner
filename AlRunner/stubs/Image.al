// Stub for BC's Image codeunit (ID 3971) from System Application.
// At runtime, MockCodeunitHandle routes codeunit 3971 calls to MockImage.
codeunit 3971 "Image"
{
    procedure Clear(Alpha: Integer; Red: Integer; Green: Integer; Blue: Integer)
    begin
    end;

    procedure Clear(Red: Integer; Green: Integer; Blue: Integer)
    begin
    end;

    procedure Crop(X: Integer; Y: Integer; Width: Integer; Height: Integer)
    begin
    end;

    procedure GetFormatAsText(): Text
    begin
    end;

    procedure FromBase64(Base64Text: Text)
    begin
    end;

    procedure FromStream(var InStream: InStream)
    begin
    end;

    procedure GetWidth(): Integer
    begin
    end;

    procedure GetHeight(): Integer
    begin
    end;

    procedure Resize(Width: Integer; Height: Integer)
    begin
    end;

    procedure RotateFlip(RotateFlipType: Integer)
    begin
    end;

    procedure Save(var OutStream: OutStream)
    begin
    end;

    procedure ToBase64(): Text
    begin
    end;

    procedure GetRotateFlipType(): Integer
    begin
    end;
}
