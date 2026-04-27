// Blank-shell stub for BC's Image codeunit (ID 3971) from System Application.
// Every method returns the type-default (0, empty string) and does not throw.
// No real header parsing or image processing is performed.
//
// This stub is needed so AL that references "Codeunit Image" compiles when
// the SA package is not present in --packages. Users who need real image
// behaviour must provide their own stub (see docs/limitations.md).
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
