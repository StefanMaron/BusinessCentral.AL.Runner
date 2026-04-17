namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory stand-in for NavSessionSettings. Real NavSessionSettings.ALInit()
/// dereferences NavSession (null in standalone mode). MockSessionSettings keeps
/// the settings as local fields; RequestSessionUpdate is a no-op because there
/// is no service-tier session to refresh.
///
/// Note: BC emits string (not NavText) for Text-typed properties on this type,
/// so all text-valued members are declared as <see cref="string"/>. NavText has
/// implicit conversions to/from string, so callers that supply NavText values
/// still compile.
/// </summary>
public class MockSessionSettings
{
    public MockSessionSettings() { }

    /// <summary>ALInit — populates with standalone defaults. Always safe to call.</summary>
    public void ALInit()
    {
        ALCompany = "";
        ALLanguageId = 0;
        ALLocaleId = 0;
        ALProfileId = "";
        ALProfileAppId = new NavGuid(System.Guid.Empty);
        ALProfileSystemScope = false;
        ALTimeZone = "";
    }

    /// <summary>Company — AL Text property.</summary>
    public string ALCompany { get; set; } = "";

    /// <summary>LanguageId — Windows LCID integer.</summary>
    public int ALLanguageId { get; set; }

    /// <summary>LocaleId — Windows LCID integer.</summary>
    public int ALLocaleId { get; set; }

    /// <summary>ProfileId — Text[30] identifier.</summary>
    public string ALProfileId { get; set; } = "";

    /// <summary>ProfileAppId — GUID.</summary>
    public NavGuid ALProfileAppId { get; set; } = new NavGuid(System.Guid.Empty);

    /// <summary>ProfileSystemScope — Boolean (true = System scope, false = Tenant scope).</summary>
    public bool ALProfileSystemScope { get; set; }

    /// <summary>TimeZone — IANA/Windows time-zone name.</summary>
    public string ALTimeZone { get; set; } = "";

    /// <summary>
    /// ALRequestSessionUpdate — service-tier API for pushing the new settings to the
    /// BC session. In standalone there is no session to refresh; the local state is
    /// already authoritative so the call is a no-op.
    /// </summary>
    public void ALRequestSessionUpdate(bool reloadUserProfile) { }
    public void ALRequestSessionUpdate() { }

    /// <summary>Default factory — mirrors NavSessionSettings.Default(ITreeObject).</summary>
    public static MockSessionSettings Default() => new MockSessionSettings();
}
