// Fixture for codeunit 73400 "BNR Blank Part Tests": one field of each type a page can show.
// QIntInit, QDecInit and QBoolInit carry an InitValue, so a buffer that was Init()ed reads that value.
table 73401 "BNR Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Header No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
        field(10; QTxt; Text[30]) { }
        field(11; QCd; Code[20]) { }
        field(12; QInt; Integer) { }
        field(13; QDec; Decimal) { }
        field(14; QBool; Boolean) { }
        field(15; QOpt; Option) { OptionMembers = Alpha,Beta,Gamma; }
        field(16; QDt; Date) { }
        field(17; QTm; Time) { }
        field(18; QDtTm; DateTime) { }
        field(19; QEn; Enum "BNR Kind") { }
        field(20; QBig; BigInteger) { }
        field(21; QGd; Guid) { }
        field(22; QDur; Duration) { }
        field(23; QIntInit; Integer) { InitValue = 5; }
        field(24; QDecInit; Decimal) { InitValue = 2.5; }
        field(25; QBoolInit; Boolean) { InitValue = true; }
    }

    keys
    {
        key(PK; "Header No.", "Line No.") { Clustered = true; }
    }
}
