// The mirror of "CMV Bound": SingleInstance declared true, no TableNo. Together the two
// show both columns follow the declaration instead of being constants.
codeunit 60763 "CMV Single"
{
    SingleInstance = true;

    procedure Ping(): Integer
    begin
        exit(7);
    end;
}
