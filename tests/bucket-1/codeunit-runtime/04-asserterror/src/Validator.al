codeunit 50104 "Input Validator"
{
    procedure ValidateEmail(Email: Text[250])
    begin
        if Email = '' then
            Error('Email address must not be empty');
        if StrPos(Email, '@') = 0 then
            Error('Email address must contain @');
        if StrPos(Email, '.') = 0 then
            Error('Email address must contain a domain');
    end;

    procedure ValidateQuantity(Qty: Integer)
    begin
        if Qty < 0 then
            Error('Quantity must not be negative');
        if Qty > 99999 then
            Error('Quantity must not exceed 99999');
    end;

    procedure ValidatePercentage(Pct: Decimal)
    begin
        if Pct < 0 then
            Error('Percentage must not be negative');
        if Pct > 100 then
            Error('Percentage must not exceed 100');
    end;
}
