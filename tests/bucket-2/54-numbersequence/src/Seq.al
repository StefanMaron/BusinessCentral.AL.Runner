codeunit 56540 "NS Probe"
{
    procedure ProbeExistsThenInsert(Name: Text): Integer
    begin
        // NumberSequence is a system runtime type; operations on it must
        // not throw inside test context. We exercise the common sequence:
        // check existence, insert if missing, take next value.
        if not NumberSequence.Exists(Name) then
            NumberSequence.Insert(Name);
        exit(1);
    end;

    procedure ProbeNext(Name: Text): BigInteger
    var
        Value: BigInteger;
    begin
        if not NumberSequence.Exists(Name) then
            NumberSequence.Insert(Name);
        Value := NumberSequence.Next(Name);
        exit(Value);
    end;
}
