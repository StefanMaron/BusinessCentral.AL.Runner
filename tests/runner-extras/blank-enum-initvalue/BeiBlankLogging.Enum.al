// The exact shape from issue #1674: an enum whose ONLY value is blank-named.
// The lone blank name is what made the mismatch fatal — with no other members,
// nothing in the options list could ever match, so every InitValue evaluation
// threw instead of falling back.
enum 64200 "BEI Blank Logging"
{
    Extensible = true;

    value(0; " ")
    {
        Caption = ' ', Locked = true;
    }
}
