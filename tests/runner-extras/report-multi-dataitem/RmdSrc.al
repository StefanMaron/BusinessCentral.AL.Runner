/// Two SIBLING data items over the Integer virtual table — the shape that produced nothing.
report 62070 "RMD Siblings"
{
    ProcessingOnly = false;

    dataset
    {
        dataitem(First; Integer)
        {
            DataItemTableView = sorting(Number) where(Number = filter(1 .. 3));
            column(FirstTag; 'FIRST-' + Format(Number)) { }
        }
        dataitem(Second; Integer)
        {
            DataItemTableView = sorting(Number) where(Number = filter(1 .. 2));
            column(SecondTag; 'SECOND-' + Format(Number)) { }
        }
    }
}

/// A NESTED data item — the inner one must re-iterate for every outer row.
report 62071 "RMD Nested"
{
    ProcessingOnly = false;

    dataset
    {
        dataitem(Outer; Integer)
        {
            DataItemTableView = sorting(Number) where(Number = filter(1 .. 2));
            column(OuterTag; 'OUTER-' + Format(Number)) { }

            dataitem(Inner; Integer)
            {
                DataItemTableView = sorting(Number) where(Number = filter(1 .. 3));
                column(InnerTag; 'INNER-' + Format(Number)) { }
            }
        }
    }
}

/// The control: a single data item, which already worked. Present so a regression in the
/// simple case cannot hide behind the multi-data-item tests.
report 62072 "RMD Single"
{
    ProcessingOnly = false;

    dataset
    {
        dataitem(Only; Integer)
        {
            DataItemTableView = sorting(Number) where(Number = filter(1 .. 3));
            column(OnlyTag; 'ONLY-' + Format(Number)) { }
        }
    }
}
