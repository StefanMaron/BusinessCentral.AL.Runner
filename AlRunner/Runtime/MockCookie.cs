namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;

/// <summary>
/// Stub for <c>NavCookie</c> / AL's <c>Cookie</c> variable.
///
/// BC emits <c>new NavCookie(this)</c> in scope-class field initialisers.
/// The rewriter rewrites the type to <c>MockCookie</c> and strips the arg.
///
/// Implements the 7 Cookie properties: Name, Value, Domain, Path,
/// Secure, HttpOnly, and Expires.
/// </summary>
public class MockCookie
{
    public MockCookie() { }

    /// <summary>Default instance returned when BC emits <c>NavCookie.Default</c>
    /// (variable initialisation before any property is set).
    /// Returns a fresh instance each call to avoid cross-test mutation.</summary>
    public static MockCookie Default => new MockCookie();

    /// <summary>Cookie name. BC emits <c>cookie.ALName</c> get/set.</summary>
    public string ALName { get; set; } = "";

    /// <summary>Cookie value. BC emits <c>cookie.ALValue</c> get/set.</summary>
    public string ALValue { get; set; } = "";

    /// <summary>Cookie domain scope. BC emits <c>cookie.ALDomain</c> get/set.</summary>
    public string ALDomain { get; set; } = "";

    /// <summary>Cookie URL path scope. BC emits <c>cookie.ALPath</c> get/set.</summary>
    public string ALPath { get; set; } = "";

    /// <summary>HTTPS-only flag. BC emits <c>cookie.ALSecure</c> get/set.</summary>
    public bool ALSecure { get; set; } = false;

    /// <summary>JavaScript-access flag. BC emits <c>cookie.ALHttpOnly</c> get/set.</summary>
    public bool ALHttpOnly { get; set; } = false;

    /// <summary>Expiration timestamp. BC emits <c>cookie.GetALExpires()</c> for the getter.</summary>
    public NavDateTime ALExpires { get; set; } = NavDateTime.Default;

    /// <summary>BC transpiler generates <c>GetALExpires()</c> for DateTime property reads.</summary>
    public NavDateTime GetALExpires() => ALExpires;

    /// <summary>ALAssign for the ByRef pattern.</summary>
    public void ALAssign(MockCookie other)
    {
        if (other == null) return;
        ALName = other.ALName;
        ALValue = other.ALValue;
        ALDomain = other.ALDomain;
        ALPath = other.ALPath;
        ALSecure = other.ALSecure;
        ALHttpOnly = other.ALHttpOnly;
        ALExpires = other.ALExpires;
    }

    /// <summary>No-op Clear.</summary>
    public void Clear()
    {
        ALName = "";
        ALValue = "";
        ALDomain = "";
        ALPath = "";
        ALSecure = false;
        ALHttpOnly = false;
        ALExpires = NavDateTime.Default;
    }
}
