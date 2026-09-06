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
//
// THE OTHER HALF: WHEN THERE IS NO HONEST SUBSTITUTE (#3212)
//   A substitute is only possible where the answer is fully determined by the inputs. It is
//   not for System.Drawing, whose whole job is to produce pixels only a GDI+ implementation
//   produces — so that case gets TryClassifyPlatformRefusal below instead: BC still runs, still
//   fails, but the failure names the type, the .NET library that refused, and the reason,
//   rather than "The type initializer for 'Gdip' threw an exception".
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
            // A .NET type that exists but refuses this OS is a scope boundary, not a BC error
            // — name it before BC's own wrapper buries it. Null for everything else.
            var refusal = TryClassifyPlatformRefusal(typeName, tie.InnerException);
            if (refusal != null) throw refusal;

            // Preserve BC's own exception (NavNCLDotNetCreateException drives the add-in
            // fallback in the caller's catch block) AND its stack — see the ExceptionDispatchInfo
            // note in fix_saveas_recordref_filter: `throw inner` would reset it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// Turn "a .NET type refused this operating system" into a named refusal, or return null
    /// so the caller rethrows BC's own exception untouched.
    ///
    /// <para>WHY (#3212). BC's Base Application constructs .NET objects through AL interop, and
    /// some of those types are Windows-only in modern .NET. Table 2121 "O365 Brand Color"
    /// .MakePicture builds a <c>System.Drawing.Bitmap</c> to draw a colour swatch; on Linux the
    /// shipped <c>System.Drawing.Common</c> 8.0 fails its <c>Gdip</c> class initializer, and
    /// BC's <c>NavAutomationHelper.Create</c> catches the resulting
    /// <c>TargetInvocationException</c> and rethrows <c>NavNCLDotNetInvokeException</c>:
    /// "A call to System.Drawing.Bitmap failed with this message: The type initializer for
    /// 'Gdip' threw an exception." That names neither the surface nor the reason, which is the
    /// half <c>.claude/rules/loud-failures.md</c> forbids. Eleven Tests-SMB tests died on it.</para>
    ///
    /// <para>MEASURED, not assumed — and it settles the experiment #3212 could not run.
    /// The refusal is NOT a missing <c>libgdiplus</c>: decompiled from the artifact BC ships
    /// (28.2.50931.54319), <c>SafeNativeMethods.Gdip..cctor</c> is
    /// <c>if (!OperatingSystem.IsWindows()) NativeLibrary.SetDllImportResolver(…, delegate {
    /// throw new PlatformNotSupportedException(SR.PlatformNotSupported_Unix); });</c> followed
    /// by <c>GdiplusStartup</c>, and the assembly contains no <c>libgdiplus</c> string at all —
    /// only <c>gdiplus.dll</c>. Installing a native package cannot change the outcome, and the
    /// .NET 6 <c>System.Drawing.EnableUnixSupport</c> switch no longer exists in 8.0. So this is
    /// a permanent boundary on a non-Windows host, not a runner TODO.</para>
    ///
    /// <para>FAITHFULNESS (the audit obligation in loud-failures.md). This substitutes no value
    /// and changes no successful path: it only fires where BC was already about to fail, and it
    /// fires on the exception .NET itself raised rather than on a guess about which types are
    /// Windows-only — so on a Windows host, where these constructions succeed, it never runs.
    /// Reason deliberately does NOT begin with "not-yet-implemented", so
    /// <c>NavApplicationObjectBase.TryInvoke</c> keeps trapping it into <c>false</c> exactly as
    /// it traps today's <c>NavNCLDotNetInvokeException</c> — this change makes the failure
    /// legible, it does not move its classification.</para>
    /// </summary>
    internal static AlRunner.Infrastructure.RunnerOutOfScopeException? TryClassifyPlatformRefusal(
        string? typeName, Exception? thrown)
    {
        // Walk the whole chain rather than peeking at a fixed depth: BC wraps the platform's
        // exception twice today (NavNCLDotNetInvokeException → TypeInitializationException →
        // PlatformNotSupportedException), and that nesting is Types.dll's business, not a
        // contract. A chain is acyclic by construction — InnerException is fixed at
        // construction time — so this terminates.
        PlatformNotSupportedException? refused = null;
        for (var e = thrown; e != null && refused == null; e = e.InnerException)
            refused = e as PlatformNotSupportedException;
        if (refused == null) return null;

        // Exception.Source defaults to the assembly of the throwing method, which is the .NET
        // library that refused — "System.Drawing.Common" for the #3212 case. Reported when
        // present because it, not the AL-visible type name, is what a reader has to look up.
        var lib = string.IsNullOrEmpty(refused.Source) ? null : refused.Source;
        var api = string.IsNullOrEmpty(typeName)
            ? "NavDotNet.CreateDotNet"
            : $"NavDotNet.CreateDotNet({typeName})";

        return new AlRunner.Infrastructure.RunnerOutOfScopeException(
            api,
            "dotnet-platform-unsupported — "
            + (lib == null ? "the .NET library backing this type" : lib)
            + $" refuses every entry point on this operating system ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}). "
            + "BC's own message for this is \"The type initializer … threw an exception\", which names "
            + $"neither. .NET reported: {refused.Message}",
            "dotnet-platform");
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
