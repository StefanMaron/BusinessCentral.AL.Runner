codeunit 1320514 "Variant Dict Src"
{
    procedure GetDictFromVariant(reference: Variant): Dictionary of [Text, Integer]
    var
        dict: Dictionary of [Text, Integer];
    begin
        dict := reference;
        exit(dict);
    end;

    procedure GetDictFromVariantChecked(reference: Variant): Dictionary of [Text, Integer]
    var
        dict: Dictionary of [Text, Integer];
    begin
        if not reference.IsDictionary() then
            Error('Expected dictionary');
        dict := reference;
        exit(dict);
    end;
}
