/// A processing-only report that exists solely to be the TARGET of an action's
/// `RunObject = report ...`. Nothing invokes it: the point is that the runner refuses to
/// perform a RunObject naming anything other than a PAGE, and refuses it with a
/// `not-yet-implemented` reason anchor rather than the old permanent-boundary one (#2931).
report 64546 "Par Noop Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;

    dataset
    {
        dataitem(Row; "Par Row")
        {
        }
    }
}
