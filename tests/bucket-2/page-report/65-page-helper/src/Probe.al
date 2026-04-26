page 56650 "PH Helper Page"
{
    PageType = Card;

    internal procedure FormatLineBreaksForHTML(Value: Text): Text
    begin
        exit(Value.Replace('\', '<br />'));
    end;

    internal procedure IsYes(Value: Text): Boolean
    begin
        exit(Value = 'yes');
    end;
}
