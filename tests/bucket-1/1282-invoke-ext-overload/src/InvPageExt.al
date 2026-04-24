pageextension 1282001 "Inv Ext PgExt" extends "Inv Ext Pg"
{
    procedure GetExtNumber(Input: Integer): Integer
    begin
        exit(Input * 3);
    end;
}
