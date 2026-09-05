<!--
  Exactly ONE of the two lines below the comment must survive. Delete the other.

    * keep "Closes #" and add the issue number, if this PR closes an issue; or
    * keep the "No linked issue:" line and write the reason after the colon,
      if it closes nothing.

  A pin bump, a docs typo, a revert and a test-only follow-up all close
  nothing, and all need the second form. The reason is mandatory: a bare
  opt-out marker gets pasted in reflexively, which is what it exists to stop.

  Leaving BOTH lines untouched fails the check, on purpose.

  Neither form is spelled out in full here on purpose. A closing keyword next
  to a real issue number closes that issue on merge even from inside this HTML
  comment, and a complete escape-hatch line with a reason would satisfy the
  check on the author's behalf. The check reads this whole box as text, and so
  does GitHub.

  DIFFERENT FIX, SAME CHECK: reject-bad-closing-references also scans your
  COMMIT MESSAGES. This repo squash-merges, and the merge commit's body is the
  concatenated commit messages -- not this description. GitHub's parser reads
  neither negation nor surrounding prose, so a commit message saying it does
  not close an issue still closes it. If the check names a COMMIT MESSAGE,
  editing this description will not fix it: reword the commit and force-push.
-->

Closes #
No linked issue:

## What changed, and why
