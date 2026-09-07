/// A report whose id 64547 is ALSO the id of `page 64547 "Par RunObject Target"`.
///
/// That collision is the whole point and it is entirely legal: AL gives every object kind its
/// own id namespace, so a report and a page may share a number and both exist. It is also not
/// exotic — the runner's own inventory already keeps pages and pageextensions in separate
/// dictionaries for exactly this reason (#1710).
///
/// It exists to pin a defect fixed in #2943. `ResolveRunTargetFromMetadata` named a RunObject
/// target with `TryGetAnyPageName(action.TargetID)` — a PAGE lookup — whatever kind the action
/// actually declared. With this collision in place the old code answered the PAGE's name for
/// this report, and the runner's refusal read:
///
///     RunObject = Report 'Par RunObject Target' (64547)
///
/// naming an object the AL never mentions. The refusal was loud, and confidently wrong about
/// which object it had declined — a silent wrong answer wearing a loud failure, which is the
/// shape `loud-failures.md` exists to prevent.
///
/// Nothing runs this report. Its only job is to be a report at an id a page also occupies.
report 64547 "Par Decoy Report"
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
