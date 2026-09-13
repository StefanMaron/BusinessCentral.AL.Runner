// DesignerProfileCopyPatches — NavDesignerALFunctions.CopyProfile refuses by name (#2324).
//
// Base Application's "Conf./Personalization Mgt".CopyProfile copies a profile through this
// platform call. BC's own body builds a NavConfigurationCopyExtension (profile repository,
// combined profile definition cache, designer compiler, page-customization store) and runs it
// inside NavDesignerCopyManager.CopyProfileInternal's `catch (Exception)`, which turns ANY
// failure into a failed response. On the skeleton that chain does not complete, so AL saw
// Base Application's "The profile could not be copied." for a copy that succeeds on a real
// tier, with nothing naming the runner gap. A refusal thrown anywhere inside that chain is
// swallowed the same way, so it is raised here, at the entry, outside the catch.
//
// Observably equivalent? No, and deliberately so: this is a loud refusal, not a substitute.
// The faithful copy is tracked in the issue named in the reason; the corpus test that
// adjudicates it is profile/TestCopyProfile.al (codeunit 60908).
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class DesignerProfileCopyPatches
{
    internal const string Api = "NavDesignerALFunctions.CopyProfile";

    /// <summary>Prepended to NavDesignerALFunctions.CopyProfile by NclCecilRewrite.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RefuseCopyProfile()
        => RunnerScope.ThrowNotYetImplemented(
            Api,
            "profile-copy: BC's designer copy (NavConfigurationCopyExtension) does not run on the "
            + "skeleton session, and its failures are swallowed into Base Application's 'The profile "
            + "could not be copied.' -- tracked in #4124");
}
