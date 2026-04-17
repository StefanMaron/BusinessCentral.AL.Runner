/// Exercises CaptionClassTranslate — BC's resource translation system.
codeunit 60420 "CCT Src"
{
    procedure TranslateCaption(expr: Text): Text
    begin
        exit(CaptionClassTranslate(expr));
    end;
}
