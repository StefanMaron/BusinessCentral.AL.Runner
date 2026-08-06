enum 62307 "EEM Action Type" implements "EEM Interface"
{
    Extensible = true;

    value(0; Blank)
    {
        Implementation = "EEM Interface" = "EEM Action Blank Impl";
    }
    value(1; "Action A")
    {
        Implementation = "EEM Interface" = "EEM Action A Impl";
    }
    value(2; "Action B")
    {
        Implementation = "EEM Interface" = "EEM Action B Impl";
    }
}
