// Blank first value plus a SPARSE named value. The named ordinal (5, not 1)
// proves InitValue evaluation resolves through the enum's true ordinal values,
// not a 0..Count-1 array index, and that a named InitValue on the same enum
// stays healthy next to the blank one.
enum 64201 "BEI Mixed Level"
{
    Extensible = true;

    value(0; " ")
    {
        Caption = ' ', Locked = true;
    }
    value(5; Verbose)
    {
        Caption = 'Verbose';
    }
}
