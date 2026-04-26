// Test to verify that cross-extension pageextension name collisions are suppressed
// as runner artifacts (single-pass compilation collapsing extension identities).
// Both AppA and AppB define "SharedPageExt" — valid in real BC (separate compilations).
// The runner must suppress the false AL0197 error and proceed to test execution.
codeunit 310512 "CrossExt PageExt Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure CrossExtPageExtCompiles()
    begin
        // If we reach here, the cross-extension pageextension collision was correctly
        // suppressed as a runner artifact (line 1482 IsCrossExtensionDuplicateDeclaration
        // filter in Program.cs prevents extension-type AL0197 from being treated as genuine).
        Assert.IsTrue(true, 'Cross-extension pageextension collision must be suppressed as runner artifact');
    end;
}
