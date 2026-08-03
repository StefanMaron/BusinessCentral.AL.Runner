// DotNetInteropShims — runner-supplied implementations for .NET types that exist in the
// AL surface but that .NET itself refuses to provide on this platform.
//
// WHY THIS EXISTS
//   AL `DotNet` interop is LATE-BOUND: the emit produces
//       new NavDotNet(this, "System.Security.Principal.Windows, …",
//                     "System.Security.Principal.SecurityIdentifier", …)
//       sid.CreateDotNet(new object[]{ "S-1-5-18" })
//       sid.InvokePropertyGet("Value")
//   so the object BC ends up holding is only ever reached by NAME. That is what makes a
//   substitute possible at all — nothing holds a compile-time reference to the framework
//   type, and no assembly has to be shadowed.
//
//   `System.Security.Principal.SecurityIdentifier` is present in the .NET 8 shared
//   framework on Linux but every entry point throws
//   `PlatformNotSupportedException: Windows Principal functionality is not supported on
//   this platform` — including the SDDL constructor, which is pure string parsing with no
//   OS involvement whatsoever. Per docs/scope.md, in-process .NET interop is IN SCOPE and
//   must run as real code; the platform, not the scope boundary, is what is missing here.
//
// FAITHFULNESS (the audit obligation from .claude/rules/loud-failures.md)
//   Only members whose value is fully determined by the SDDL string are implemented, and
//   they compute the same answer Windows does:
//     • the constructor validates the same grammar and rejects the same inputs
//     • `Value` / `ToString()` return the canonical uppercase SDDL, as on Windows
//   Everything else — Translate, IsAccountSid, AccountDomainSid, binary form, equality
//   against a Windows SID — depends on a Windows security subsystem the runner does not
//   have. Those members are deliberately NOT defined: BC's late-bound invoke then fails
//   with its own "member not found", which is loud and names the member. A shim that
//   answered them with a plausible default would be exactly the silent fake that rule
//   forbids.
using System.Globalization;

namespace AlRunner.Patches;

public static class DotNetInteropShims
{
    /// <summary>
    /// A runner-provided instance for <paramref name="typeName"/>, or null to let BC's own
    /// <c>NavAutomationHelper.CreateDotNetObject</c> run unchanged. Injected as a PROLOGUE
    /// so the happy path for every other type is byte-for-byte the original.
    ///
    /// Must never throw for a type it does not handle — returning null has to mean exactly
    /// "not mine", or an unrelated DotNet construction would start failing here.
    /// </summary>
    public static object? CreateDotNetObject(string? assemblyFullName, string? typeName, object[]? arguments)
    {
        var shim = TryCreateShim(typeName, arguments);
        if (shim != null) return shim;
        return CallOriginal(assemblyFullName, typeName, arguments);
    }

    private static object? TryCreateShim(string? typeName, object[]? arguments)
    {
        if (typeName != "System.Security.Principal.SecurityIdentifier") return null;
        if (arguments is not { Length: 1 } || arguments[0] is not string sddl) return null;
        // A malformed SDDL throws out of here on purpose: falling through would hand the
        // string to the platform type, whose PlatformNotSupported hides the real complaint.
        return new RunnerSecurityIdentifier(sddl);
    }

    private static System.Reflection.MethodInfo? _original;

    /// <summary>
    /// BC's own <c>NavAutomationHelper.CreateDotNetObject</c>. Every type this shim does not
    /// handle goes here, so the in-process interop path (MemoryStream, encoders, crypto, …)
    /// is BC's real code, unchanged — this class only intercepts, it never reimplements.
    /// </summary>
    private static object? CallOriginal(string? assemblyFullName, string? typeName, object[]? arguments)
    {
        _original ??= Type.GetType(
                "Microsoft.Dynamics.Nav.Types.NavAutomationHelper, Microsoft.Dynamics.Nav.Types")
            ?.GetMethod("CreateDotNetObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NavAutomationHelper.CreateDotNetObject not found — Types.dll shape changed");
        try
        {
            return _original.Invoke(null, new object?[] { assemblyFullName, typeName, arguments });
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Preserve BC's own exception (NavNCLDotNetCreateException drives the add-in
            // fallback in the caller's catch block) AND its stack — see the ExceptionDispatchInfo
            // note in fix_saveas_recordref_filter: `throw inner` would reset it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }
}

/// <summary>
/// The SDDL-determined half of <c>System.Security.Principal.SecurityIdentifier</c>.
/// See DotNetInteropShims for why only this half exists.
/// </summary>
public sealed class RunnerSecurityIdentifier
{
    public RunnerSecurityIdentifier(string sddlForm)
    {
        if (sddlForm == null) throw new ArgumentNullException(nameof(sddlForm));
        Value = Canonicalize(sddlForm);
    }

    /// <summary>The canonical uppercase SDDL form — what Windows returns for the same input.</summary>
    public string Value { get; }

    public override string ToString() => Value;

    public override bool Equals(object? obj) =>
        obj is RunnerSecurityIdentifier other &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Parse and re-render <c>S-R-IA[-SA]*</c>: revision, identifier authority, and up to 15
    /// sub-authorities. The identifier authority is decimal below 2^32 and 0x-hex above it,
    /// which is the same rendering rule Windows applies — so a SID written either way comes
    /// back in its canonical form rather than the caller's spelling.
    /// </summary>
    private static string Canonicalize(string sddl)
    {
        var parts = sddl.Split('-');
        if (parts.Length < 3 || !parts[0].Equals("S", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The value was invalid.", nameof(sddl));

        if (!byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            || revision != 1)
            throw new ArgumentException("The value was invalid.", nameof(sddl));

        ulong authority;
        var authorityText = parts[2];
        if (authorityText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(authorityText.AsSpan(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out authority))
                throw new ArgumentException("The value was invalid.", nameof(sddl));
        }
        else if (!ulong.TryParse(authorityText, NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out authority))
        {
            throw new ArgumentException("The value was invalid.", nameof(sddl));
        }
        // The identifier authority is a 48-bit field.
        if (authority > 0xFFFFFFFFFFFFUL)
            throw new ArgumentException("The value was invalid.", nameof(sddl));

        var subAuthorities = parts.Length - 3;
        if (subAuthorities > 15)
            throw new ArgumentException("The value was invalid.", nameof(sddl));

        var sb = new System.Text.StringBuilder("S-1-");
        sb.Append(authority <= uint.MaxValue
            ? authority.ToString(CultureInfo.InvariantCulture)
            : "0x" + authority.ToString("x12", CultureInfo.InvariantCulture));
        for (var i = 3; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sub))
                throw new ArgumentException("The value was invalid.", nameof(sddl));
            sb.Append('-').Append(sub.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
