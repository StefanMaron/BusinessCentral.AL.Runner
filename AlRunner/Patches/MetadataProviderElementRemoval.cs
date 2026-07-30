// MetadataProviderElementRemoval — stop BC stripping every AL-declared control off a page
// because the runner has no license file.
//
// THE SYMPTOM
//   A page's MasterPage came back real (correct ID, correct type) but with an EMPTY control
//   tree, so NavFormMetadataHelper.ControlDefinitions — a FindAll over that tree — was an
//   empty list and TryGetControlDefinitionById never matched anything. Every per-control
//   metadata question (an Option control's OptionCaption, a control's Editable/Enabled)
//   therefore had no source to answer from, which reads like missing metadata rather than
//   like discarded metadata.
//
// THE CAUSE
//   MetadataProvider.GetMasterPage ends with, for a table-sourced page:
//       if (elementRemovalOption != ElementRemovalOption.None && ...)
//           masterPage = RemoveItemsOnPageBasedOnLicenseAndApplicationArea(masterPage, null);
//   That pass drops every control the current LICENSE does not grant and every control whose
//   ApplicationArea is not enabled for the session. The runner has no license file and no
//   application-area configuration, so the pass matches nothing and removes everything. It
//   was measured, not inferred: for the runner-extras PGV page the merged MasterPage came
//   back with ContentArea.Controls = 1 (the bare container) and RemovedControls = 12.
//
// WHY None IS THE FAITHFUL VALUE HERE
//   This is NOT "skip a check we find inconvenient". The removal pass answers the question
//   "which of these controls is this tenant licensed and configured to SEE?" — a question
//   about a license and a user profile, neither of which exists in the runner. BC's own
//   `ElementRemovalOption.None` is the configuration for "do not filter the page by license
//   or permissions", i.e. exactly the shape of a runner that has no license to filter by.
//   The result is the page as its AL author declared it, which is the only thing an AL test
//   can meaningfully assert about — a test asserting on a control it declared should not
//   fail because a license file the runner never had did not grant it.
//
//   The alternative — leaving removal on — is strictly worse than out-of-scope: it does not
//   throw, it silently answers "that control does not exist" for controls that plainly do,
//   which is precisely the silent-wrong-answer the loud-failures rule exists to prevent.
//
//   Nothing else observable changes: the field is read ONLY to gate these removal passes.
//   AL cannot read it, and no in-scope AL surface can distinguish "not removed" from
//   "removed but fully licensed" — which is what a real BC with a full license produces.
//
// MECHANISM
//   A static field poke rather than a Cecil rewrite: the field is read at half a dozen call
//   sites, so setting the value once is both smaller and closer to what a differently
//   configured server tier would itself do. The enum type is resolved from the field, so
//   this does not hardcode a type name that a BC version bump could move.
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class MetadataProviderElementRemoval
{
    /// <summary>
    /// Set MetadataProvider.elementRemovalOption to ElementRemovalOption.None. Throws if the
    /// field or the enum member is gone — a silent miss would restore the empty-control-tree
    /// behaviour with no signal, and that cost days to find once already.
    /// </summary>
    public static void Apply(Assembly navNcl)
    {
        var providerType = navNcl.GetType("Microsoft.Dynamics.Nav.XmlMetadata.MetadataProvider")
            ?? throw new InvalidOperationException(
                "MetadataProvider type not found in Ncl — BC metadata shape changed; do not commit");

        var field = providerType.GetField("elementRemovalOption", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "MetadataProvider.elementRemovalOption not found — BC metadata shape changed; do not commit");

        if (!field.FieldType.IsEnum || !Enum.IsDefined(field.FieldType, "None"))
            throw new InvalidOperationException(
                $"MetadataProvider.elementRemovalOption is {field.FieldType.FullName} with no 'None' member "
                + "— BC metadata shape changed; do not commit");

        // Touch the type first: the field is initialised in the static ctor from
        // ServerUserSettings, so poking before that runs would be overwritten by it.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(providerType.TypeHandle);

        var none = Enum.Parse(field.FieldType, "None");
        FieldPoke.SetStatic(providerType, "elementRemovalOption", none);

        Console.Error.WriteLine(
            "[Patch] MetadataProvider.elementRemovalOption → None (no license file / application-area "
            + "configuration in the runner, so BC's removal pass would strip every AL-declared control)");
    }
}
