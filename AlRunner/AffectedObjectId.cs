namespace AlRunner;

/// <summary>
/// One changed/tracked AL object, in a shape that carries NO reference to
/// <c>Microsoft.Dynamics.Nav.CodeAnalysis</c>.
///
/// This exists so the affected-test selection state can cross into Program.cs without
/// dragging BC's compiler assembly into type resolution there. <see
/// cref="RadObjectIdentity"/> is the same three values, but its <c>Kind</c> is a
/// <c>NavCA.SymbolKind</c>, and because that record struct is a VALUE type the CLR must
/// know its exact layout to JIT any method that merely mentions it in a signature or a
/// local. Naming it from Program.cs therefore forced
/// <c>Microsoft.Dynamics.Nav.CodeAnalysis</c> to load during argument handling — before
/// the runner has selected a BC version, and long before that assembly is resolvable.
///
/// The symptom was `provision` and every other artifact-cache path dying with an
/// unhandled `FileNotFoundException` naming the engine's own CodeAnalysis build, with no
/// managed stack, on a cold artifact cache. Measured: 20 of the 22 provisioning tests in
/// AlRunner.Tests failed with RadObjectIdentity named in Program.cs and all 22 pass with
/// this type named instead, same worktree and same build commands.
///
/// Keep Kind a plain string. Every consumer only ever formats it.
/// </summary>
internal readonly record struct AffectedObjectId(string Kind, int? Id, string Name);

/// <summary>
/// #2539: one changed AL PROCEDURE (or, when <see cref="ScopeName"/> is null, one changed
/// whole OBJECT — the widened fallback for a non-statement edit, an added/removed file, or
/// any case <c>PeekChangedScopes</c> cannot attribute with confidence). Carries no NavCA
/// type for the exact same reason <see cref="AffectedObjectId"/> does not — see that type's
/// doc comment.
/// </summary>
internal readonly record struct AffectedScopeId(AffectedObjectId Object, string? ScopeName);
