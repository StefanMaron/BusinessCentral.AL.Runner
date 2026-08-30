/// Arm 3: an action a pageextension adds to a page that ships PRECOMPILED inside Base
/// Application. As written for #1923 this was the "silent no-op" arm — the more dangerous
/// half, where nothing threw at Invoke() time at all and a test only caught the miss one step
/// later on the effect the action was supposed to have.
///
/// CORRECTION, measured 2026-08-30 while fixing #2113: that is no longer what this arm
/// exercises. "Item Attributes" (page 7500) DOES resolve a live RunnerPageInstance today, so
/// LiveNavTestPage.GetAction takes the RaiseOnAction path, not the ExtensionOnlyTestAction /
/// TryRaiseExtensionOnlyAction path, and an unresolvable action here THROWS. Measured on
/// origin/main with #2113's fix reverted, the equivalent promoted-actionref arm failed with
/// `TestPage action 157710999 on page 7500 — testpage-action — the page declares no OnAction
/// trigger for this action`, not with a silent no-op. The arm still earns its place — it
/// proves dispatch does not quietly depend on the base page having been compiled from source
/// in this bundle — but it is no longer coverage for the silent-no-op path, and nothing in
/// tests/runner-extras currently reaches TryRaiseExtensionOnlyAction.
pageextension 64523 "Pad Item Attr Ext" extends "Item Attributes"
{
    actions
    {
        addlast(Processing)
        {
            action(ExtActionOnBaseAppPage)
            {
                ApplicationArea = All;
                Caption = 'Ext Action On Base App Page';

                trigger OnAction()
                var
                    Row: Record "Pad Row";
                begin
                    Row.Log('EXT-BASEAPP-PAGE');
                end;
            }
        }
    }
}
