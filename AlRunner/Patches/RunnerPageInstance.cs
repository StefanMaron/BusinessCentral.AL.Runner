// RunnerPageInstance — a live AL page object behind a TestPage.
//
// WHY
//   The runner's TestPage was a record cursor: it mapped only controls bound to a Rec
//   field, and on a miss passed the control's own id to the record as a field number,
//   producing "The supplied field number '1167935535' cannot be found in the 'X' table"
//   — a control-name FNV hash landing where a field number was expected.
//
//   A control does not have to bind to a table field. Binding one to a page global
//   variable is ordinary AL and the standard shape for a mode/filter selector above a
//   repeater. Resolving those needs the page's own control -> value binding table, which
//   only exists on an initialised NavForm: BC publishes it as NavForm.SourceExpressions,
//   keyed "Control{controlId}".
//
// WHAT THIS DOES
//   Constructs the compiled Page{id} (a real NavForm subclass carrying the page's AL
//   triggers as methods), opts it into BC's real initialisation (RunnerFormInit), and calls
//   SetSourceTable — which is BC's own front door: it funnels through EnsureMetadataLoaded
//   -> InitializeFromMetadata, binding the record, resolving controls against the source
//   table and running the page's own OnMetadataLoaded, the step that registers the source
//   expressions.
//
//   Driving those sub-steps individually is NOT an option, and the failure is instructive:
//   SetSourceTable already triggers them, so calling them again registers every expression
//   twice ("An item with the same key has already been added. Key: Control1167935535").
//   The service-tier state InitializeFromMetadata needs on the way (permissions, designer
//   customizations, tenant personalization) is handled where it belongs — in
//   NclCecilRewrite, once, for every caller — not by tiptoeing around the method here.
//
// SCOPE
//   Only pages the runner compiled itself have the metadata to build a control tree from
//   (see AlPageMetadataRegistry). For anything else TryCreate returns null and the caller
//   keeps its record-only behaviour, which is exactly what it had before.
using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

internal sealed partial class RunnerPageInstance
{
    private readonly object _form;
    private readonly object _owner;
    private readonly NavRecord? _record;
    private readonly int _pageId;
    private readonly System.Collections.IDictionary _sourceExpressions;
    private Dictionary<string, object?> _expressionValuesAtOpen;

    // Lazily-constructed NavFormExtension instances for the pageextensions that extend
    // this page (issue #1923) — one per extension id, built on first trigger lookup and
    // reused after that. See FindTrigger/GetOrCreateExtensionInstance.
    private readonly Dictionary<int, object?> _extensionInstances = new();

    // NavFormExtension.ParentObject ("protected internal NavForm ParentObject { get;
    // private set; }") — resolved from its DECLARING type, never from a derived
    // PageExtension{id} instance's own Type. Issue #1966: PropertyInfo.SetValue against a
    // PropertyInfo obtained via instance.GetType().GetProperty(...) throws "Property set
    // method not found." for an inherited property whose SETTER is `private` — .NET
    // reflection only exposes a private accessor through the type that actually declares
    // it, even though the property's GETTER is `protected internal` and freely visible on
    // the derived type. GetCallerRecordPatches.cs's _pFormExtensionParentObject already
    // resolves this same property the correct way (via typeof(NavFormExtension)); this
    // mirrors that, so both call sites use one working pattern instead of two, one broken.
    private static readonly PropertyInfo? _pFormExtensionParentObject =
        typeof(Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension).GetProperty(
            "ParentObject", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

    private RunnerPageInstance(object form, object owner, NavRecord? record, int pageId, System.Collections.IDictionary sourceExpressions)
    {
        _form = form;
        _owner = owner;
        _record = record;
        _pageId = pageId;
        _sourceExpressions = sourceExpressions;
        _expressionValuesAtOpen = SnapshotExpressionValues(sourceExpressions);
        RegisterPageExtensionsOnTheForm();
    }

    /// <summary>
    /// Bind this page's pageextension instances to the form the way BC does, so BC's own
    /// <c>NavForm.RaiseOn&lt;trigger&gt;Async</c> runs each extension's copy of the trigger.
    ///
    /// <para>Every <c>RaiseOn…Async</c> ends with a <c>PageExtensions.ForEachAsync(ext =&gt;
    /// ext.On…())</c> pass over <c>NavForm.pageExtensions</c>, which only
    /// <c>RegisterPageExtension</c> fills — called by
    /// <c>NCLPageExtension.CreateExtensionInstanceAndBindToParent</c> inside
    /// <c>NCLMetaForm.CreateObjectInstance</c>, a path the runner replaces. Nothing registered
    /// them, so a pageextension's <c>OnOpenPage</c> / <c>OnAfterGetRecord</c> and the seven
    /// others never ran (corpus codeunit 60658, BusinessCentral.AL.Language.Tests#290).</para>
    ///
    /// <para>Registration is eager and happens here rather than at first trigger lookup because
    /// <c>OnOpenPage</c> is raised before anything asks for a trigger. A record-less page
    /// registers none: <see cref="GetOrCreateExtensionInstance"/> needs a record for the
    /// extension's <c>(NavForm, NavRecord)</c> ctor.</para>
    /// </summary>
    private void RegisterPageExtensionsOnTheForm()
    {
        var extensionIds = RecordPatches.GetPageExtensionIdsForPage(_pageId);
        if (extensionIds.Count == 0) return;

        if (_record == null)
        {
            // Loud rather than silent: GetOrCreateExtensionInstance needs a record for the
            // extension's (NavForm, NavRecord) ctor, so a record-less page binds none of its
            // extensions and every trigger they declare quietly does not run - the exact silence
            // this whole change exists to remove. `[warn]` - see the tag note in TryCreate.
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {_pageId} was built without a record, so its "
                + $"{extensionIds.Count} pageextension(s) are not bound to it and the page triggers "
                + "they declare will not run");
            return;
        }

        var register = BcShape.FindMethod(_form.GetType(), "RegisterPageExtension",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            surface: "page extension triggers", member: "NavForm.RegisterPageExtension",
            detail: "the runner binds a page's extensions to the form so BC's own "
                  + "RaiseOn<trigger>Async runs each extension's copy of the trigger");
        if (register == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "page extension triggers", "NavForm.RegisterPageExtension",
                "BC no longer declares it, so the runner cannot bind this page's extensions and "
                + "every trigger they declare would silently not run");

        foreach (var extensionId in extensionIds)
        {
            var instance = GetOrCreateExtensionInstance(extensionId);
            if (instance == null) continue;
            try { register.Invoke(_form, new[] { instance }); }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                // Loud, not fatal, for the same reason GetOrCreateExtensionInstance is: the page
                // still works, this extension's triggers do not, and saying so beats a silent
                // no-op. `[warn]` - see the tag note in TryCreate.
                Console.Out.WriteLine(
                    $"[warn] RunnerPageInstance: pageextension {extensionId} on page {_pageId}: could "
                    + $"not bind it to the page ({inner.GetType().Name}: {inner.Message}); the page "
                    + "triggers it declares will not run");
            }
        }
    }

    /// <summary>
    /// Every registered source expression's value as it stands when the page is built.
    ///
    /// A control's OWN Visible is answered from this, not from the live table — measured on
    /// all 8 BC versions through corpus PR #125: after a TestPage changes a page global that
    /// a control's Visible is bound to, BC keeps reporting the value from when the page was
    /// opened, while the same control's Editable and Enabled do follow the change. A group's
    /// Visible follows it too (corpus TestPageFieldVisibleGroup_Tests flips a group after
    /// open and is green on real BC), so only the control's own Visible is frozen here.
    ///
    /// Snapshotting the VALUES rather than eagerly evaluating every control's expression
    /// keeps the loud failure where it belongs: an expression that cannot be evaluated still
    /// raises at the read that asks for it, naming the control, instead of turning one
    /// unreadable property into a page-construction failure for the whole page.
    /// </summary>
    internal static Dictionary<string, object?> SnapshotExpressionValues(System.Collections.IDictionary expressions)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in expressions)
        {
            if (entry.Key is not string name || entry.Value == null) continue;
            try
            {
                snapshot[name] = GetValue(entry.Value)?.ClientObject;
            }
            catch
            {
                // One expression that cannot be read at open time must not cost the page its
                // whole snapshot. Omitting it means the control bound to it falls back to the
                // live value, which is what this did before the snapshot existed.
            }
        }
        return snapshot;
    }

    internal object Form => _form;

    private static readonly object Sentinel = new();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object>
        ClosedForms = new();

    /// <summary>
    /// Whether AL has already closed this form through <see cref="ForceCloseForm"/> — the
    /// CurrPage.Close() path. Weak-keyed on the form so a closed page cannot keep itself
    /// alive, and false for every form nobody closed that way, including one that was never
    /// opened.
    /// </summary>
    internal static bool WasClosedFromAl(object? form)
        => form != null && ClosedForms.TryGetValue(form, out _);

    /// <summary>
    /// Record a form as closed-from-AL without going through <see cref="ForceCloseForm"/>.
    /// Exists so the bookkeeping can be tested for the TRUE case too — reaching it through
    /// ForceCloseForm would need a live NavForm, and a set of tests that only ever assert
    /// false would pass just as well against a <c>WasClosedFromAl =&gt; false</c> stub.
    /// </summary>
    internal static void MarkClosedFromAlForTests(object form) => ClosedForms.AddOrUpdate(form, Sentinel);

    /// <summary>Counterpart of <see cref="MarkClosedFromAlForTests"/>, for the reopen case.</summary>
    internal static void ClearClosedFromAlForTests(object form) => ClosedForms.Remove(form);

    /// <summary>
    /// The record this instance is actually bound to — null for a record-less page. Needed
    /// by callers of <see cref="AdoptFromHost"/>: when adoption reuses an ALREADY-bound
    /// record (a SourceTableTemporary part the host already populated), that record can
    /// differ from whatever the caller was about to bind, and the caller's OWN record
    /// variable must follow this one rather than the one it almost passed in — see
    /// MockTestPage.GetPart, issue #2201.
    /// </summary>
    internal NavRecord? Record => _record;

    /// <summary>
    /// Whether the AL opened this page as a LOOKUP (<c>Picker.LookupMode(true)</c>), which
    /// decides whether its closing built-in actions are OK/Cancel or LookupOK/LookupCancel.
    /// Read off BC's own NavForm.LookupMode, so it reflects whatever the AL actually set.
    /// </summary>
    internal bool LookupMode
        => _form.GetType()
            .GetProperty("LookupMode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(_form) is true;

    /// <summary>
    /// The page's real caption. Read off BC's own NavForm.PageCaption rather than a constant:
    /// InitializeFromMetadata seeds it from the page's static Caption property, and
    /// <c>CurrPage.Caption := '…'</c> (a plain property setter the AL compiler emits onto the
    /// SAME field) overwrites it at runtime. One read site therefore answers both — a runner
    /// that only modelled the static case would go right on issue #1776's first repro and
    /// wrong on its second, which is exactly the split that shipped: TestPage.Caption()
    /// answered empty for both, because nothing read this property at all.
    /// </summary>
    internal string PageCaption
        => _form.GetType()
            .GetProperty("PageCaption", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(_form) as string ?? string.Empty;

    /// <summary>
    /// The control's own declared Caption (<c>field(Foo; Rec.Foo) { Caption = '…'; }</c>), or
    /// null when the control declares none. This is the FIRST source in the client's caption
    /// precedence — it wins over the source field's own Caption, which is why callers must
    /// check this before falling back to field metadata (see issue #1777).
    /// </summary>
    internal string? TryGetControlCaption(int controlId)
    {
        var caption = ControlDefinition(controlId)?.Caption;
        return string.IsNullOrEmpty(caption) ? null : caption;
    }

    /// <summary>
    /// Build and initialise the AL page object for <paramref name="pageId"/>, bound to
    /// <paramref name="record"/>. Returns null when the page has no compiled type or no
    /// real metadata — never a half-initialised instance, because a page whose source
    /// expressions were not registered would answer control lookups with silence rather
    /// than with the page's actual bindings.
    /// </summary>
    internal static RunnerPageInstance? TryCreate(object parent, int pageId, NavRecord record)
    {
        if (RecordPatches.EnsureRealPageMetadata(pageId) == null)
        {
            // Tag discipline for this whole class (#2461). Log.Install() wraps BOTH stdout and
            // stderr and drops any line matching ^[Tag] unless --verbose, so the stream is NOT
            // the variable — an earlier comment here claimed stdout was chosen because "the
            // test-execution child's stderr is not captured", and there is no such child.
            // A line that announces a DEMOTION (the TestPage silently stops being the real
            // page) therefore uses the exempt `[warn] RunnerPageInstance: ...` shape so it
            // survives the default filter; per-control tracing keeps the plain
            // `[RunnerPageInstance]` tag and stays suppressed, which is what the filter is for.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: no emit-captured metadata, so no control tree; "
                    + "TestPage stays record-only");
            return null;
        }

        var pageType = FindPageType(pageId);
        if (pageType == null)
        {
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: no compiled Page{pageId} type found; "
                + "TestPage falls back to record-only access");
            return null;
        }

        var ctor = pageType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2
                              && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
        if (ctor == null)
        {
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: Page{pageId} has no (ITreeObject, NavRecord) ctor; "
                + "TestPage falls back to record-only access");
            return null;
        }

        try
        {
            var form = ctor.Invoke(new object?[] { parent, record });
            // Must precede every step below: the guarded NavForm bodies (GetMasterPage,
            // RegisterSourceExpression, …) check this and no-op for anyone else.
            RunnerFormInit.MarkRealInit(form);

            // ONE call. SetSourceTable funnels through NavForm.EnsureMetadataLoaded ->
            // InitializeFromMetadata, which binds the record, resolves the controls against
            // the source table and runs the page's own OnMetadataLoaded — the step that
            // registers the source expressions. Driving those three individually registers
            // every expression twice ("An item with the same key has already been added.
            // Key: Control1167935535"), because InitializeFromMetadata has already run them.
            // clone: FALSE — the page must share the TestPage's cursor, not copy it. BC
            // clones because a real page owns a cursor separate from whatever the caller
            // passed; here the two are the same thing, and cloning gave the page its own
            // unpositioned record. The page's AL then read a blank Rec: an OnAction that
            // stamps Rec."No." wrote "" however the test had navigated. BC uses clone:false
            // itself where the caller's record IS the page's (NavForm line ~3704).
            Invoke(form, "SetSourceTable", new object?[] { record, false });

            var expressions = ReadProperty(form, "SourceExpressions") as System.Collections.IDictionary;
            if (expressions == null)
            {
                Console.Out.WriteLine(
                    $"[warn] RunnerPageInstance: page {pageId}: the page object initialised but published no "
                    + "source-expression table; TestPage falls back to record-only access");
                return null;
            }
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: built, {expressions.Count} source expression(s): "
                    + string.Join(", ", expressions.Keys.Cast<object>().Select(k => k?.ToString())));
            return new RunnerPageInstance(form, parent, record, pageId, expressions);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // Loud, but not fatal: the caller falls back to record-only behaviour, which is
            // strictly what it had before this existed. Silence here would turn a page-object
            // failure into "that control does not exist", which is a different and wronger
            // answer than "the runner could not build this page".
            // `[warn]` so it survives the default filter — see the tag note at the top of
            // TryCreate. Tagged `[RunnerPageInstance]` this line was dropped before reaching
            // the terminal, which is what let #2451 run ten tests against a substitute page
            // with nothing in the log to say so.
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: could not build the AL page object "
                + $"({inner.GetType().Name}: {inner.Message}); TestPage falls back to record-only access");
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(inner.StackTrace);
            return null;
        }
    }

    /// <summary>
    /// Build and initialise the AL page object for a page that declares NO SourceTable —
    /// the StandardDialog / Worksheet-header shape, ordinary legal AL, where every control
    /// is bound to a page global rather than to a Rec field.
    ///
    /// <see cref="TryCreate"/> cannot serve this shape: it needs a record for the
    /// <c>(ITreeObject, NavRecord)</c> ctor and for <c>SetSourceTable</c>, and there is no
    /// table to build one over. That is why the TestPage construction site in
    /// <c>CodeunitPatches.CreateTestPageClient</c> used to give up on such a page entirely
    /// and hand back the blanket navigation mock, whose every member answers a default —
    /// including <c>GetPart</c>, so a subpage part on a no-SourceTable host reported an
    /// empty rowset (issue #2090). The handler-driven construction site
    /// (<c>RunnerTestClientSession.GetPage</c>) has accepted this shape since #2007; the
    /// two sites now agree.
    ///
    /// <para>Construction goes through the one-arg <c>(ITreeObject)</c> ctor — the same one
    /// <c>BcRuntime.ConstructFormForStaticEntry</c> falls to for a record-less page on the
    /// <c>Page.RunModal</c> path — followed by <c>EnsureMetadataLoaded</c>, which is the
    /// step <c>SetSourceTable</c> itself funnels through to reach
    /// <c>InitializeFromMetadata</c>. Calling it directly is safe here precisely because
    /// <c>SetSourceTable</c> is NOT called: nothing else has run the registration, so
    /// there is no double-registration to trip over.</para>
    /// </summary>
    internal static RunnerPageInstance? TryCreateRecordless(object parent, int pageId)
    {
        if (RecordPatches.EnsureRealPageMetadata(pageId) == null)
        {
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: no emit-captured metadata, so no control tree; "
                    + "a record-less TestPage has nothing left to answer from");
            return null;
        }

        var pageType = FindPageType(pageId);
        if (pageType == null)
        {
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: no compiled Page{pageId} type found; "
                + "a record-less TestPage has nothing left to answer from");
            return null;
        }

        var ctor = pageType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                              && typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                                     .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (ctor == null)
        {
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: Page{pageId} has no (ITreeObject) ctor; "
                + "a record-less TestPage has nothing left to answer from");
            return null;
        }

        try
        {
            var form = ctor.Invoke(new object?[] { parent });
            // Must precede EnsureMetadataLoaded: the guarded NavForm bodies
            // (GetMasterPage, RegisterSourceExpression, InitializeForm) check this.
            RunnerFormInit.MarkRealInit(form);
            Invoke(form, "EnsureMetadataLoaded", Array.Empty<object?>());

            var expressions = ReadProperty(form, "SourceExpressions") as System.Collections.IDictionary;
            if (expressions == null)
            {
                Console.Out.WriteLine(
                    $"[warn] RunnerPageInstance: page {pageId}: the page object initialised but "
                    + "published no source-expression table for a record-less TestPage, so it "
                    + "falls back to the navigation mock");
                return null;
            }
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: built record-less, {expressions.Count} source expression(s)");
            return new RunnerPageInstance(form, parent, record: null, pageId, expressions);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // `[warn]` — see the tag note in TryCreate. This is the exact line #2461 measured
            // as missing: page 977 took this path on every run and the log said nothing.
            Console.Out.WriteLine(
                $"[warn] RunnerPageInstance: page {pageId}: could not build the record-less AL page object "
                + $"({inner.GetType().Name}: {inner.Message}); the TestPage falls back to the navigation mock");
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(inner.StackTrace);
            return null;
        }
    }

    // Subpage NavForm objects AdoptFromHost has already run BC's own metadata-load
    // machinery against, so a second touch (another TestPage access, or the host's own AL
    // reaching CurrPage.<part> after this ran) reuses the SAME reified wrapper instead of
    // re-registering every source expression ("An item with the same key has already been
    // added"). Weak and instance-keyed for the same reason RunnerFormInit's tables are: a
    // form that goes out of scope must not be kept alive by this cache.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RunnerPageInstance> _reifiedSubpages = new();

    /// <summary>
    /// The subpage object the HOST's own AL owns for <paramref name="controlId"/> — the
    /// same NavForm the host's compiled AL reaches through <c>CurrPage.&lt;part&gt;</c>, via
    /// BC's own <c>NavForm.GetPart(int)</c> (backed by <c>RegisterUIPart</c>/<c>uiParts</c>:
    /// one NavForm per control, lazily built and cached the first time EITHER side asks for
    /// it). Building a second, disconnected page object for the same control — which
    /// TestPageFactory.TryBuild/TryBuildRecordless still do — is issue #2201: TestPage.&lt;part&gt;
    /// and the host AL's own CurrPage.&lt;part&gt;.Page were two different instances, invisible
    /// for a Rec-bound part (its state lives in the shared record) but not for one bound to
    /// page globals.
    ///
    /// The object <c>NavForm.GetPart(int)</c> hands back may never have been through a real
    /// metadata load: nothing in Ncl.dll drives <c>SetSourceTable</c>/<c>EnsureMetadataLoaded</c>
    /// on a subpage part on the caller's behalf — SubFormLink application is client/NST-side,
    /// which the runner reimplements itself (see <c>LiveNavTestPart</c>'s SubPageLink handling
    /// in MockTestPage.cs). This method brings the object up to the same live state
    /// <see cref="TryCreate"/> gives a freshly built page, but only the FIRST time this method
    /// reifies a given form — <see cref="_reifiedSubpages"/> remembers which ones it already
    /// drove, so a later call gets the cached wrapper instead of double-registering.
    ///
    /// Returns null — the caller falls back to TryBuild/TryBuildRecordless exactly as it did
    /// before this existed — when the host has no NavForm at all (a top-level page the
    /// runner never built as a compiled AL object), the control names no part on that host
    /// (BC's own uiParts lookup fails), or reifying the adopted object throws.
    /// </summary>
    internal static RunnerPageInstance? AdoptFromHost(
        object? hostForm, int controlId, int partPageId, NavRecord? recordToBind, bool recordless)
    {
        var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1";
        if (hostForm is not Microsoft.Dynamics.Nav.Runtime.NavForm host)
        {
            if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId}: hostForm is {hostForm?.GetType().FullName ?? "null"}, not a NavForm");
            return null;
        }

        Microsoft.Dynamics.Nav.Runtime.NavForm subForm;
        try { subForm = host.GetPart(controlId); }
        catch (Exception ex)
        {
            if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId} on host page {host.FormId}: GetPart threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (subForm == null)
        {
            if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId} on host page {host.FormId}: GetPart returned null");
            return null;
        }

        if (_reifiedSubpages.TryGetValue(subForm, out var cached))
        {
            if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId}: reused cached reified subpage");
            return cached;
        }
        if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId}: adopted subpage {subForm.GetType().FullName}, reifying (recordless={recordless})");

        // A SourceTableTemporary part's own record is NOT something SetSourceTable hands it
        // — the compiled Page{partPageId} class binds its OWN temporary Rec at construction,
        // independent of any caller-supplied record (that is what let the host's own AL push
        // rows into it via CurrPage.<part>.Page.SetRows BEFORE this method ever ran — the
        // SourceTableTemporary shape in issue #2201's report). Calling SetSourceTable with a
        // freshly built EMPTY record here would silently discard every row the host already
        // wrote. So: if the object already carries a bound record — from its own
        // construction, or because a previous caller (host or runner) already reified it —
        // reuse it as-is and skip SetSourceTable/EnsureMetadataLoaded entirely, exactly the
        // way Adopt() below already does for a modal page BC built itself.
        // NavForm.SourceTable's GETTER lazily calls EnsureMetadataLoaded() itself when unset
        // — reading it here to CHECK whether the form is already bound would trigger exactly
        // the metadata load (with a freshly built, empty record) this whole check exists to
        // avoid. SourceTableWithoutLoadingMetadata reads the same backing field with no such
        // side effect.
        var existingRecord = recordless ? null : ReadProperty(subForm, "SourceTableWithoutLoadingMetadata") as NavRecord;
        var alreadyLive = existingRecord != null;
        if (trace) Console.Out.WriteLine($"[RunnerPageInstance] AdoptFromHost control {controlId}: existingRecord={(existingRecord == null ? "null" : existingRecord.GetType().FullName)} alreadyLive={alreadyLive}");
        if (!alreadyLive)
        {
            try
            {
                // Must precede the calls below: the guarded NavForm bodies check this and
                // no-op for anyone else (RunnerFormInit.cs). Mirrors TryCreate/
                // TryCreateRecordless, which mark a form they just constructed themselves —
                // here the form was constructed by BC's own NavForm.GetPart instead, so it
                // was never marked.
                RunnerFormInit.MarkRealInit(subForm);
                if (recordless)
                    Invoke(subForm, "EnsureMetadataLoaded", Array.Empty<object?>());
                else
                    Invoke(subForm, "SetSourceTable", new object?[] { recordToBind, false });
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                // `[warn]` — see the tag note in TryCreate. Missed by the first sweep for #2461,
                // and it kept the back-reference to the "stderr is not captured" reasoning that
                // sweep deleted for being false. The host built this subpage itself and its AL
                // may already have put state on the instance; rebuilding the part from scratch
                // discards that, so the TestPage answers from an object the host is not using.
                Console.Out.WriteLine(
                    $"[warn] RunnerPageInstance: part page {partPageId} (control {controlId}): could not reify "
                    + $"the host's own subpage object ({inner.GetType().Name}: {inner.Message}); the part "
                    + "falls back to a freshly constructed page, losing whatever state the host's own had");
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine(inner.StackTrace);
                return null;
            }
        }

        var expressions = ReadProperty(subForm, "SourceExpressions") as System.Collections.IDictionary;
        if (expressions == null) return null;

        var record = recordless ? null : (existingRecord ?? ReadProperty(subForm, "SourceTableWithoutLoadingMetadata") as NavRecord);
        var instance = new RunnerPageInstance(subForm, subForm, record, partPageId, expressions);
        _reifiedSubpages.Add(subForm, instance);

        // Raise the part's own OnOpenPage exactly once — the FIRST time ANY caller (the
        // host's own AL touching CurrPage.<part>, or the runner adopting on the TestPage's
        // behalf) reifies this instance — not once per TestPage-side touch. The
        // <see cref="_reifiedSubpages"/> cache above is what makes this "exactly once": a
        // second reification of the SAME subForm returns the cached instance at the
        // TryGetValue check earlier in this method and never reaches this line again.
        //
        // Issue #2689: this used to be gated on `!alreadyLive` (no bound record yet) instead
        // — conflating two different questions. `alreadyLive` answers "did the HOST's own AL
        // already write rows through this instance" (still used above to skip
        // SetSourceTable/EnsureMetadataLoaded, which would wipe those rows) — it does NOT
        // answer "has OnOpenPage already run for this instance". Measured against real BC
        // (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141): an ORDINARY Rec-bound
        // part (no SourceTableTemporary, nothing written to it yet) already has a bound record
        // the FIRST time this method's own `host.GetPart(controlId)` call above returns —
        // BC's own native part construction binds the part's statically-declared SourceTable
        // as routine construction, with no AL trigger involved. That made `alreadyLive` true
        // on the very first reification, so `!alreadyLive` never fired the part's OnOpenPage
        // at all: real BC runs it between the host's own OnOpenPage and the host's first
        // OnAfterGetCurrRecord (see RunnerTestPageState.MarkOpened / EagerlyBuildParts), the
        // runner ran it never. Gating on this method's own dedup cache instead of on record
        // presence fixes that without reopening #2201 (TestPageTempPart_Part.al, the
        // SourceTableTemporary shape #2201 pinned, declares no OnOpenPage trigger at all, so
        // running it once here is a no-op either way; codeunit 60807 stays green).
        instance.RaiseOnOpenPage();

        return instance;
    }

    /// <summary>
    /// Cecil-injected hook on <c>NavForm.GetPart(int)</c> (see NclCecilRewrite.cs) — called
    /// with the resolved subpage NavForm on EVERY successful <c>GetPart</c>, from EITHER
    /// caller: the host's own compiled AL touching <c>CurrPage.&lt;part&gt;</c> (that is
    /// exactly what <c>GetPart(int)</c> compiles to — see MockTestPage.cs's GetPart doc), or
    /// <see cref="AdoptFromHost"/> reaching the same object on the TestPage's behalf.
    ///
    /// WHY THIS EXISTS (issue #2201's page-globals shape, the part AdoptFromHost alone could
    /// not fix). A plain field write inside a part's own procedure —
    /// <c>CurrPage.&lt;part&gt;.Page.SetTag(...)</c> — compiles to
    /// <c>NavApplicationObjectBase.Invoke(methodId, args)</c>, which never touches
    /// <c>EnsureMetadataLoaded</c> at all (measured: no call chain from <c>Invoke</c> reaches
    /// it). So when the HOST's own <c>OnOpenPage</c> writes through a page-globals part
    /// before the TestPage side ever asks for it, there is no signal AdoptFromHost's own
    /// "already touched" check (used successfully for the Rec-bound/temporary shapes) can
    /// observe — and running the part's OWN <c>OnOpenPage</c> later, when the TestPage side
    /// finally does ask, clobbers whatever the host already wrote.
    ///
    /// <c>GetPart(int)</c> is the one call BOTH sides are guaranteed to go through to reach
    /// the object AT ALL (it is what <c>TryGetUIPart</c>/<c>RegisterUIPart</c> caches one
    /// instance per control against), which makes it the earliest point common to both
    /// callers — running the part's OnOpenPage here, before returning the object to
    /// EITHER caller, is what makes "the subpage opens with its host" (the architectural
    /// invariant the corpus's own subpages-overview doc describes) hold regardless of which
    /// side asks first.
    ///
    /// DELIBERATELY NARROW to a page that declares NO SourceTable. A Rec-bound or
    /// SourceTableTemporary part already reifies correctly and lazily via
    /// <see cref="AdoptFromHost"/> — the object's OWN record binding IS the "has this been
    /// used" signal there, verified against real BC (StefanMaron/BusinessCentral.AL.Language.Tests
    /// codeunit 60807). Eagerly running <c>EnsureMetadataLoaded</c>/<c>SetSourceTable</c> for
    /// those from inside a hook fired deep in arbitrary NAV internals would need a
    /// caller-supplied record this hook has no safe way to build (record construction needs
    /// the full <c>TestPageFactory</c> machinery, table-id resolution, tableextension
    /// registration — heavy, TestPage-construction-time-only machinery, not something to run
    /// from an arbitrary re-entrant call site), so this does not attempt it — those shapes
    /// keep going through the existing, already-correct path unchanged.
    ///
    /// Never throws: this runs inside BC's own IL, on a call path the AL author's trigger
    /// code did not ask to be re-entered from. A failure here silently leaves the object
    /// un-reified early; AdoptFromHost's own lazy path is still there as a fallback.
    /// </summary>
    internal static void EnsureRecordlessPartReifiedEagerly(object? formObj)
    {
        try
        {
            if (formObj is not Microsoft.Dynamics.Nav.Runtime.NavForm form) return;
            if (_reifiedSubpages.TryGetValue(form, out _)) return;

            var pageId = form.FormId;

            // Only the page-globals shape — see the doc comment above.
            if (RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(pageId)) return;

            // No metadata this runner can build a control tree from at all (neither a
            // source-compiled page nor a precompiled dependency's page) — EnsureMetadataLoaded
            // would just no-op (ShouldResolveMasterPage refuses) and SourceExpressions would
            // stay permanently empty. Nothing to gain from marking it "reified" here; leave it
            // for the existing refusal path (TryGetPartDefinition / TryBuildRecordless) to
            // answer with its usual named OOS reason instead of a silent early exit.
            if (!AlPageMetadataRegistry.TryGet(pageId, out _) && !RecordPatches.HasDependencyPageMetadata(pageId))
                return;

            RunnerFormInit.MarkRealInit(form);
            Invoke(form, "EnsureMetadataLoaded", Array.Empty<object?>());

            var expressions = ReadProperty(form, "SourceExpressions") as System.Collections.IDictionary;
            if (expressions == null) return;

            var instance = new RunnerPageInstance(form, form, null, pageId, expressions);
            if (_reifiedSubpages.TryGetValue(form, out _)) return; // lost a race with itself — never observed re-entrantly in practice, but cheap to guard
            _reifiedSubpages.Add(form, instance);
            instance.RaiseOnOpenPage();
        }
        catch
        {
            // Never let a hook running deep inside NAV internals throw — see doc comment.
        }
    }

    /// <summary>
    /// Wrap a NavForm BC already built and initialised — a page opened from AL with
    /// RunModal, which the runner never constructed and so cannot have marked or driven.
    ///
    /// Unlike TryCreate this does NOT initialise anything: the form is already live, and
    /// re-running SetSourceTable would re-register every source expression ("An item with the
    /// same key has already been added"). A form whose expressions were never registered (the
    /// runner's init guard did not admit it) yields an empty binding table, so Rec-bound
    /// controls still resolve and page-variable ones refuse by name — which is the same
    /// answer TryCreate gives for a page it could not build.
    /// </summary>
    internal static RunnerPageInstance Adopt(object form, int pageId)
    {
        var expressions = ReadProperty(form, "SourceExpressions") as System.Collections.IDictionary
                          ?? new System.Collections.Hashtable();
        // No caller-supplied owner/record for an already-live form — the form is itself a
        // real NavForm, which implements ITreeObject, and its own bound record (BC's
        // "SourceTable" property, same one SetSourceTable populates in TryCreate) is the
        // best available substitute for constructing a pageextension instance later
        // (issue #1923's extension-trigger dispatch). A form with neither yet (unbound) is
        // the pre-existing "no page object" case FindTrigger already tolerates.
        var record = ReadProperty(form, "SourceTable") as NavRecord;
        return new RunnerPageInstance(form, form, record, pageId, expressions);
    }

    /// <summary>
    /// The page's binding for a control id, or null when the control is not one the page
    /// publishes a source expression for (Rec-bound controls are resolved by the caller
    /// against the record instead).
    ///
    /// <para><b>One expression can serve several controls (issue #3211).</b> The AL compiler
    /// registers ONE <c>&lt;Expression&gt;</c> per distinct binding TEXT, named after the
    /// FIRST control that uses it, and every later control over the same text points at that
    /// one through its own <c>DataColumnName</c>. Measured on the emitted PageDefinition XML
    /// for a card page with <c>field(VarCtlA; MyVar)</c> and <c>field(VarCtlB; MyVar)</c>:</para>
    /// <code>
    /// &lt;Controls ID="158749901"  Name="VarCtlA" DataColumnName="Control158749901" /&gt;
    /// &lt;Controls ID="2071839752" Name="VarCtlB" DataColumnName="Control158749901" /&gt;
    /// &lt;Expression Name="Control158749901" SourceExpression="MyVar" … /&gt;
    /// </code>
    /// <para>so <c>Control2071839752</c> is never registered and the <c>"Control" + id</c>
    /// key alone answers null for VarCtlB — the runner then refused it as unbound, blaming
    /// the source table for a control that is bound perfectly well. Microsoft's own pages do
    /// this constantly: page 1612 "Office Admin. Credentials" shows <c>PasswordText</c>
    /// through both <c>O365Password</c> and <c>OnPremPassword</c>, and page 1327 "Adjust
    /// Inventory" shows each <c>TempItemJournalLine</c> field twice, once inside the
    /// single-location group and once inside the repeater.</para>
    /// <para><c>DataColumnName</c> is the control's OWN statement of which registered
    /// expression it reads, so following it can never fabricate a binding: a Rec-bound
    /// control carries the source-table FIELD NUMBER there (<c>DataColumnName="2"</c>), which
    /// is not a key in the expression table, and an unbound control carries none. The
    /// <c>"Control" + id</c> lookup stays first because it is the common case and needs no
    /// metadata read at all.</para>
    /// <para>A page that ships PRECOMPILED in a dependency .app has no readable
    /// <c>DataColumnName</c> — its reconstructed metadata carries no control tree at all (see
    /// <c>DependencyPageMetadataXml</c>) — so the last step asks the dependency's
    /// SymbolReference.json which other controls declare the same binding TEXT, and takes the
    /// one of those the page did register. Same rule, same dedup key, read from the only
    /// source that states it for such a page.</para>
    /// </summary>
    internal object? TryGetSourceExpression(int controlId)
    {
        var expression = _sourceExpressions[SourceExpressionKey(controlId)];
        if (expression != null) return expression;

        var column = ControlDefinition(controlId)?.DataColumnName;
        if (!string.IsNullOrEmpty(column) && _sourceExpressions[column] is { } byColumn) return byColumn;

        foreach (var sibling in RecordPatches.DependencyControlsSharingSourceExpression(_pageId, controlId))
            if (_sourceExpressions[SourceExpressionKey(sibling)] is { } shared) return shared;

        return null;
    }

    // ── control / action state properties (Editable, Enabled, Visible) ─────────────────
    //
    // A page states its read-only contract in these properties: `Editable = false` on a
    // control that is never writable, `Editable = SomeVar` on one that depends on the row.
    // The AL compiler emits both into the page metadata as the SAME attribute — a string
    // that is either the literal "true"/"false" or the NAME of a registered expression:
    //
    //   Editable="false"
    //   Editable="p62090p62090RowEditable"   <Expression Name="p62090p62090RowEditable"
    //                                          SourceExpression="RowEditable" … />
    //
    // so resolving one is "parse the literal, else look the name up in the page's own
    // binding table". The expression is live — reading it now returns whatever the page's
    // AL last assigned, which is what makes a per-row property follow the cursor.

    /// <summary>
    /// The page's own editability, as <c>CurrPage.Editable(…)</c> leaves it. Separate from
    /// any control's property: a page can be read-only while a control declares itself
    /// editable, and BC shows the field read-only regardless.
    /// </summary>
    internal bool PageEditable => _form is not NavForm form || form.Editable;

    /// <summary>
    /// What a control DECLARES for one of the three boolean properties, whichever metadata
    /// states it — the merged runtime tree for a page the runner compiled, the declaring
    /// dependency's SymbolReference.json for one that ships precompiled (issue #3504).
    ///
    /// <para>Both answer the same thing: the string the AL compiler wrote, which
    /// <see cref="EvaluateProperty"/> then resolves. The second source exists because
    /// <c>DependencyPageMetadataXml</c> reconstructs no control tree — right for a control's
    /// VALUE BINDING, which is IL — so <see cref="ControlDefinition"/> is null for every
    /// control of such a page and all three properties read as "declares none", the AL default
    /// of true. Base Application 28.1 declares them on 18,222 field controls; every one of
    /// those answered true.</para>
    ///
    /// <para>ORDER IS THE CONTRACT: the runtime tree wins whenever it has a definition, so this
    /// can only ADD an answer where there was none, never override one. A page the runner
    /// compiled itself is unaffected — <c>TryGetDependencyControlDeclaredProperty</c> is only
    /// consulted when the definition is missing, and answers null for a page no dependency
    /// declares.</para>
    ///
    /// <para>Trap: null must stay distinguishable from the empty string here. Empty is
    /// <see cref="ClientExpressionTheCompilerDropped"/>'s "the compiler had an expression and
    /// dropped it" (AL0573); null is "nothing was declared". Coalescing to <c>?? ""</c>
    /// anywhere on this path would route every undeclared control of a precompiled page into
    /// that refusal.</para>
    /// </summary>
    private string? DeclaredControlProperty(int controlId, string propertyName)
    {
        if (ControlDefinition(controlId) is { } definition)
            return propertyName switch
            {
                "Editable" => definition.Editable,
                "Visible" => definition.Visible,
                "Enabled" => definition.Enabled,
                _ => null,
            };

        return TranslateSymbolDeclarationToBindingKey(
            RecordPatches.TryGetDependencyControlDeclaredProperty(_pageId, controlId, propertyName),
            _sourceExpressions);
    }

    /// <summary>
    /// Turn a declaration read from SymbolReference.json into the spelling
    /// <see cref="EvaluateProperty"/> resolves against, or leave it alone when it is a literal.
    ///
    /// <para>THE TWO SPELLINGS. The compiled page metadata names an expression by its
    /// <c>Id</c> — <c>p790p790PageEditable</c> — and BC keys the live binding table on exactly
    /// that: <c>NavForm.RegisterSourceExpression</c> ends in
    /// <c>sourceExpressions.Add(expression.Id, expression)</c>. The SYMBOL FILE states the raw
    /// AL identifier instead — <c>PageEditable</c> — which is a different string and matches no
    /// key. Feeding it straight through made a resolvable property refuse: Base Application 790
    /// "G/L Account Categories" declares <c>Enabled = PageEditable</c> on five actions, and
    /// invoking one raised "'PageEditable' is not a name the page publishes a binding for"
    /// where BC evaluates the page global (assigned <c>PageEditable := CurrPage.Editable</c> in
    /// that page's own OnOpenPage) and runs the OnAction.</para>
    ///
    /// <para>So the raw name is matched against each registered expression's <c>Name</c>, and
    /// the dictionary KEY — which is the <c>Id</c> — is handed on. A name nothing published is
    /// returned UNCHANGED, so the refusal it then earns is the honest one about a binding the
    /// page really does not have, not an artefact of the spelling.</para>
    ///
    /// <para>THE NAME IS READ BY REFLECTION, and that is not incidental. BC exposes it through
    /// a type that only half the supported matrix has: <c>INavFormSourceExpression</c> is
    /// absent on 27.0 and 27.5 and present on 28.1 and 28.4 (measured against the provisioned
    /// artifacts), so naming it fails the BUILD on 27.x — which is exactly how this arrived.
    /// What both families do carry is a public <c>Name</c> property on the concrete
    /// <c>NavFormSourceExpression</c>, and both register with
    /// <c>sourceExpressions.Add(id, …)</c>, so the join holds on every version without a
    /// version-conditional type reference.</para>
    ///
    /// <para>Trap: this must never silently answer "no match" because the property was not
    /// found. <see cref="ReadProperty"/> returning null for an entry that HAS a name would
    /// turn the join into a no-op on one BC family and reintroduce the silent default this
    /// whole change removes — on half the matrix, where nothing would say so. A value that is
    /// present but unreadable is therefore not a miss:
    /// <c>SourceExpressionNameOrNull</c> answers null only for an entry with no such property
    /// at all, and <c>SymbolDeclarationBindingKeyTests</c> pins both arms.</para>
    ///
    /// <para>Literals never reach the lookup: <c>EvaluateProperty</c> decides
    /// literal-vs-expression itself, and 13,053 of Base Application 28.1's 20,452 declarations
    /// are literals, so short-circuiting them keeps the common case free of a dictionary walk.</para>
    ///
    /// <para>Static and internal so <c>AlRunner.Tests</c> can pin the translation directly: the
    /// live route needs a NavForm whose own compiled IL has run its
    /// <c>RegisterSourceExpression</c> calls, which only the page-build pipeline produces.</para>
    /// </summary>
    internal static string? TranslateSymbolDeclarationToBindingKey(
        string? declared, System.Collections.IDictionary sourceExpressions)
    {
        if (string.IsNullOrEmpty(declared)) return declared;
        if (string.Equals(declared, "true", StringComparison.OrdinalIgnoreCase) || declared == "1") return declared;
        if (string.Equals(declared, "false", StringComparison.OrdinalIgnoreCase) || declared == "0") return declared;

        // Already an Id (the page registered this very key) — nothing to translate.
        if (sourceExpressions[declared] != null) return declared;

        foreach (System.Collections.DictionaryEntry entry in sourceExpressions)
        {
            if (entry.Value is null) continue;
            if (SourceExpressionNameOrNull(entry.Value) is not { } name) continue;
            if (!string.Equals(name, declared, StringComparison.OrdinalIgnoreCase)) continue;
            return entry.Key as string ?? declared;
        }

        return declared;
    }

    /// <summary>
    /// The declared AL name of one registered source expression, or null when the entry does
    /// not carry one at all.
    ///
    /// <para>Reflection rather than a cast, because the interface that declares it
    /// (<c>INavFormSourceExpression</c>) exists only on BC 28.x. The concrete
    /// <c>NavFormSourceExpression</c> carries a public <c>Name</c> on 27.x AND 28.x, so
    /// reading the property by name is the one spelling that compiles and works on both —
    /// see <see cref="TranslateSymbolDeclarationToBindingKey"/> for the measurement.</para>
    ///
    /// <para>A non-string value answers null the same way an absent property does: both mean
    /// "this entry does not state a name I can join on", and neither may be reported as a
    /// match. Internal so AlRunner.Tests can pin it directly.</para>
    /// </summary>
    internal static string? SourceExpressionNameOrNull(object entry)
        => ReadProperty(entry, "Name") as string;

    /// <summary>Editable for a data-bound control, combined with the page's own state.</summary>
    internal bool ControlEditable(int controlId)
        => PageEditable && EvaluateProperty(DeclaredControlProperty(controlId, "Editable"), "Editable", controlId, PageElementKind.Control, atOpen: false);

    internal bool ControlEnabled(int controlId)
        => EvaluateProperty(DeclaredControlProperty(controlId, "Enabled"), "Enabled", controlId, PageElementKind.Control, atOpen: false);

    /// <summary>
    /// A control's effective visibility is its own <c>Visible</c> combined with EVERY
    /// group that encloses it, all the way up to the content area — not just its own
    /// declared value and not just its immediate parent's (issue #1778). A field inside
    /// <c>group(DynamicGroup) { Visible = ShowDynamic; }</c> must read hidden while
    /// <c>ShowDynamic</c> is false even though the field itself declares no <c>Visible</c>
    /// at all, and a field two groups deep must follow the OUTER group's Visible even when
    /// the immediate parent group declares none of its own.
    ///
    /// Walks the same ancestor chain as <see cref="ControlIsCompileTimeEliminated"/>, but
    /// asks the LIVE question at each level via <see cref="EvaluateProperty"/> (resolving
    /// expression names, not just literals) rather than
    /// <see cref="IsLiteralFalse"/>'s narrower "was this folded to the literal false at
    /// compile time" — a group whose Visible expression currently evaluates false must
    /// still hide its descendants even though it was not compile-time eliminated.
    /// </summary>
    internal bool ControlVisible(int controlId)
    {
        // atOpen: the control's OWN Visible is the one property real BC does not re-evaluate
        // after the page is open. See SnapshotExpressionValues.
        if (!EvaluateProperty(DeclaredControlProperty(controlId, "Visible"), "Visible", controlId, PageElementKind.Control, atOpen: true))
            return false;

        if (_form is not NavForm form) return true;

        var helper = form.MetadataHelper;
        var currentId = controlId;
        while (true)
        {
            Microsoft.Dynamics.Nav.Types.Metadata.ElementDefinition parent;
            try
            {
                parent = helper.FindParentByControlId(currentId);
            }
            catch (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLControlMetadataNotFoundException)
            {
                // Ran off the top of the hierarchy — nothing further encloses this control.
                return true;
            }

            // Only a group carries its own Visible; the content area (or anything else the
            // walk can land on) does not participate, so reaching one ends the walk visible.
            if (parent is not Microsoft.Dynamics.Nav.Types.Metadata.ControlGroupDefinition group)
                return true;

            // LIVE, unlike the control's own Visible above. Corpus
            // TestPageFieldVisibleGroup_Tests flips a group's Visible expression after the
            // page is open and reads a field inside it as newly visible, green on real BC.
            if (!EvaluateProperty(group.Visible, "Visible", group.ID, PageElementKind.Group, atOpen: false))
                return false;

            currentId = group.ID;
        }
    }

    /// <summary>
    /// Whether this control is compile-time eliminated from the runtime page — its own
    /// <c>Visible</c>, or that of ANY group enclosing it, is the compile-time LITERAL
    /// <c>false</c> (never an expression, even one that currently evaluates false). Real BC
    /// dead-code-eliminates such a control at compile time: it never exists on the runtime
    /// page object at all. That's a DIFFERENT claim from <see cref="ControlVisible"/>, which
    /// answers "is this (present) control currently visible" — a control that answers false
    /// here is not merely invisible, it is unreachable, and callers must not resolve it into
    /// an <c>ITestField</c>/<c>ITestAction</c> at all (see <c>LiveNavTestPage.GetField</c>,
    /// which turns this into BC's own "field ... is not found on the page" by returning null
    /// and letting <c>NavTestPageBase.GetField(int,bool)</c> — the precompiled method the AL
    /// compiler emits for every <c>TestPage.&lt;field&gt;</c> access — throw its own
    /// <c>NavTestFieldNotFoundException</c>, rather than the runner inventing its own message).
    ///
    /// Walks the SAME ancestor chain #1778's live evaluation needs, but asks a narrower
    /// question at each level: not "what does Visible currently evaluate to" but "is Visible
    /// spelled as the literal false in the page's own metadata". <see cref="IsLiteralFalse"/>
    /// deliberately does NOT resolve expression names the way <see cref="EvaluateProperty"/>
    /// does — an expression that happens to be false right now must stay reachable (that's
    /// #1778's live-evaluation territory), only a property the AL compiler itself folded to
    /// the literal false triggers elimination here.
    /// </summary>
    internal bool ControlIsCompileTimeEliminated(int controlId)
    {
        if (_form is not NavForm form) return false;

        if (IsLiteralFalse(OwnDeclaredVisible(
                ControlDefinition(controlId)?.Visible, TryGetPartDefinition(controlId)?.Visible)))
            return true;

        var helper = form.MetadataHelper;
        var currentId = controlId;
        while (true)
        {
            Microsoft.Dynamics.Nav.Types.Metadata.ElementDefinition parent;
            try
            {
                parent = helper.FindParentByControlId(currentId);
            }
            catch (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLControlMetadataNotFoundException)
            {
                // Ran off the top of the hierarchy walking up from currentId — nothing further
                // to check.
                return false;
            }

            // Only a group carries its own Visible; the content area (or anything else the
            // walk can land on) does not participate in elimination, so reaching one ends the
            // walk with "not eliminated at this level".
            if (parent is not Microsoft.Dynamics.Nav.Types.Metadata.ControlGroupDefinition group)
                return false;

            if (IsLiteralFalse(group.Visible)) return true;

            currentId = group.ID;
        }
    }

    /// <summary>
    /// The <c>Visible</c> an element DECLARES, whichever metadata collection it lives in.
    ///
    /// <para>Before #3313 the elimination check read only <c>ControlDefinition(id)?.Visible</c>,
    /// and that silently answered null for every subpage PART: a part is not a
    /// <c>ControlDefinition</c> at all — the AL compiler emits it as an
    /// <c>InfopartPageDefinition</c>, reached through <c>MetadataHelper.InfoPartDefinitions</c>
    /// rather than through the control lookup. So a part declared <c>Visible = false</c> read
    /// as declaring nothing, which is the AL default of true, and the part stayed reachable.</para>
    ///
    /// <para>Both element kinds derive from <c>UIElementDefinition</c>, which is where
    /// <c>Visible</c> lives, so this is ONE property read over two collections rather than a
    /// second rule: whichever collection holds the element, the declared string is the same
    /// property with the same meaning. An id is never in both — a control id and a part id come
    /// from one generated id space — so the order of the two is not a tie-break, and the null
    /// coalesce says exactly that.</para>
    ///
    /// <para>Static and internal so <c>AlRunner.Tests</c> can pin the collection-fallback
    /// directly. The alternative needs a live <c>NavForm</c> whose <c>MetadataHelper</c> carries
    /// a real <c>InfopartPageDefinition</c>, which only the page-build pipeline produces — the
    /// AL-observable half is measured upstream instead, by corpus codeunit 60346.</para>
    /// </summary>
    internal static string? OwnDeclaredVisible(string? controlVisible, string? partVisible)
        => controlVisible ?? partVisible;

    /// <summary>
    /// True only for the compile-time literal spelling ("false"/"0", case-insensitive on the
    /// word form) — the same literal recognition <see cref="EvaluateProperty"/> uses, minus the
    /// expression-name fallback, because an expression must never be treated as eliminating.
    /// Internal (not private) so <c>AlRunner.Tests</c> can pin this literal-vs-expression
    /// distinction directly, without needing a live NavForm/MetadataHelper to exercise it.
    /// </summary>
    internal static bool IsLiteralFalse(string? raw)
        => raw != null && (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0");

    /// <summary>
    /// What an action DECLARES for one of its two boolean properties, whichever metadata
    /// states it — the merged runtime tree, or the declaring dependency's SymbolReference.json
    /// for a page that ships precompiled (issue #2460).
    ///
    /// <para>The action-side twin of <see cref="DeclaredControlProperty"/>, with the same
    /// contract: the runtime tree wins whenever it has a definition, so this can only ADD an
    /// answer where there was none. An action has no <c>Editable</c> in AL, which is why only
    /// two names appear here and not three.</para>
    /// </summary>
    private string? DeclaredActionProperty(int actionId, string propertyName)
    {
        if (ActionDefinition(actionId) is { } definition)
            return propertyName switch
            {
                "Enabled" => definition.Enabled,
                "Visible" => definition.Visible,
                _ => null,
            };

        // Same two-spelling translation as the control path — the shape #2460's own page 790
        // is made of, five actions declaring Enabled = PageEditable.
        return TranslateSymbolDeclarationToBindingKey(
            RecordPatches.TryGetDependencyActionDeclaredProperty(_pageId, actionId, propertyName),
            _sourceExpressions);
    }

    internal bool ActionEnabled(int actionId)
        => EvaluateProperty(DeclaredActionProperty(actionId, "Enabled"), "Enabled", actionId, PageElementKind.Action, atOpen: false);

    /// <summary>
    /// Live, like every other property except a CONTROL's own Visible — and it follows the
    /// enclosing action groups, not just the action's own declaration.
    ///
    /// <para>An action inside <c>group(G) { Visible = false; ... }</c> declares no Visible of
    /// its own, so reading only its own property answered the AL default of true where real BC
    /// answers false. Measured on BC 28.4: an action in such a group reports
    /// <c>Visible = false</c> and <c>Enabled = true</c>, and is still invokable — see corpus
    /// codeunit 60583 "TPAR Tests". Enabled is deliberately NOT walked: the same measurement
    /// shows a hidden group does not disable the actions inside it, and a group declaring
    /// <c>Enabled = false</c> has not been measured either way.</para>
    ///
    /// <para>Unlike <see cref="ControlVisible"/> an action is never eliminated by this — the
    /// same measurement shows a hidden action stays on the page and its OnAction still runs.
    /// This changes what <c>Visible()</c> answers, nothing about reachability.</para>
    /// </summary>
    internal bool ActionVisible(int actionId)
    {
        if (!EvaluateProperty(DeclaredActionProperty(actionId, "Visible"), "Visible", actionId, PageElementKind.Action, atOpen: false))
            return false;

        foreach (var ancestor in EnclosingActionGroups(actionId))
            if (!EvaluateProperty(ancestor.Visible, "Visible", ancestor.ID, PageElementKind.Group, atOpen: false))
                return false;

        return true;
    }

    /// <summary>
    /// The action groups enclosing <paramref name="actionId"/>.
    ///
    /// <para>Actions do not live in the layout tree, so <see cref="ControlVisible"/>'s
    /// <c>FindParentByControlId</c> walk cannot reach them — that one traverses
    /// <c>masterPage.ContentArea</c> only. BC publishes a traversal that does cover actions:
    /// <c>MasterPage.FindControlBaseDefinition(id, out path)</c> searches ContentArea, then
    /// CommandBar, then InfopartsArea, descending through <c>Actions</c> and
    /// <c>ActionContainers</c>, and hands back the ancestor path. Using BC's own search keeps
    /// this from being a second, divergent notion of where an action lives.</para>
    /// </summary>
    private IEnumerable<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition> EnclosingActionGroups(int actionId)
    {
        if (_form is not NavForm form || form.MasterPage is not { } master)
            return Array.Empty<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition>();

        return master.FindControlBaseDefinition(actionId, out var path) == null
            ? Array.Empty<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition>()
            : ActionGroupsIn(path);
    }

    /// <summary>
    /// The elements of an ancestor path that carry an action group's own <c>Visible</c>.
    ///
    /// <para>Only an <c>ActionGroupBaseDefinition</c> — AL's <c>group(...)</c> inside
    /// <c>actions</c> — does. The path also carries the content area, the command bar and
    /// action CONTAINERS (AL's <c>area(Processing)</c>), none of which an AL author can give a
    /// <c>Visible</c>, so folding them in would invent a rule no measurement supports.</para>
    ///
    /// <para>Internal and static so <c>AlRunner.Tests</c> can pin the filter directly: the live
    /// route needs a NavForm over a real compiled page's metadata, which only the page-build
    /// pipeline produces.</para>
    /// </summary>
    internal static IEnumerable<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition> ActionGroupsIn(
        IEnumerable<Microsoft.Dynamics.Nav.Types.Metadata.ElementDefinition>? path)
        => path?.OfType<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition>()
           ?? Array.Empty<Microsoft.Dynamics.Nav.Types.Metadata.ActionGroupBaseDefinition>();

    /// <summary>
    /// Whether <paramref name="controlId"/> names a control this page DECLARES at all — the
    /// question "is this id in the page's control-id space", asked of the page's own merged
    /// metadata rather than of any binding the runner did or did not manage to resolve.
    ///
    /// <para>It exists to keep two different answers apart at <c>LiveNavTestPage.GetField</c>
    /// (issue #3313), which used to give both the same runner-gap refusal:</para>
    /// <list type="bullet">
    /// <item>the id names a real control here and the runner could not resolve its binding —
    /// a genuine runner gap, and still a <c>RunnerOutOfScopeException</c>;</item>
    /// <item>the id names no control here at all — BC's OWN refusal, and the runner must
    /// raise BC's own <c>NavTestFieldNotFoundException</c> instead of classifying documented
    /// BC behaviour as an unimplemented runner surface.</item>
    /// </list>
    ///
    /// <para>A control's id is compiler-generated per compilation and lives in its own id
    /// space; a table field number is not in it. That is what makes the second case reachable
    /// from ordinary AL — <c>GetField(Rec.FieldNo(X))</c> confuses the two spaces — and it is
    /// what corpus codeunit 60346 measures.</para>
    /// </summary>
    internal bool DeclaresControl(int controlId)
        => _form is NavForm form && form.MetadataHelper.TryGetControlDefinitionById(controlId, out _);

    private Microsoft.Dynamics.Nav.Types.Metadata.ControlDefinition? ControlDefinition(int controlId)
        => _form is NavForm form && form.MetadataHelper.TryGetControlDefinitionById(controlId, out var d) ? d : null;

    private Microsoft.Dynamics.Nav.Types.Metadata.ActionCommonPropsDefinition? ActionDefinition(int actionId)
        => _form is NavForm form && form.MetadataHelper.TryGetCommonActionDefinitionById(actionId, out var d) ? d : null;

    /// <summary>
    /// Resolve one of the boolean control properties. Absent means the AL declared none, and
    /// the AL default for all three is true.
    /// </summary>
    private bool EvaluateProperty(
        string? raw, string propertyName, int elementId, PageElementKind kind, bool atOpen)
    {
        // null is "this element publishes no such property at all". An element that declares
        // none publishes the AL default as a LITERAL instead — measured on BC 28.1 through this
        // very seam: an action with no Enabled arrives as "true", one declaring Enabled = Rec.Flag
        // as "Flag". So an EMPTY string is neither, and cannot be read as "none declared".
        if (raw is null) return true;
        if (raw.Length == 0) return ClientExpressionTheCompilerDropped(propertyName, elementId, kind);

        // The literal arrives in more than one spelling: the emitted XML carries "true" /
        // "false" on controls and actions and "1" / "0" in the page's Properties block, and
        // BC's own metadata merge normalises an ABSENT property to "True" / "False". An
        // AL identifier cannot collide with these, so case-insensitive matching is safe.
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;

        // The whole property text as one registered name. This is the shape a bare page global
        // takes, it is by far the most common one, and taking it first means the parser below
        // never sees a name whose own spelling happens to contain grammar characters.
        // The api carries no " — ": OutOfScopeMessage.TryParse cuts the api from the reason at
        // the FIRST one, so "TestPage page N element M — Visible" made the untyped recovery path
        // report the api as "TestPage page N element M" and fold "Visible" into the reason. Same
        // defect #2945 fixed for Feature Key Modify, live at all three sites here (#2999).
        if (atOpen && _expressionValuesAtOpen.TryGetValue(raw, out var frozen))
            return frozen is bool fb
                ? fb
                : throw TestPageShapeGap.ControlProperty(
                    $"TestPage {propertyName} on page {_pageId} element {elementId}",
                    $"expression '{raw}' evaluated to '{frozen ?? "null"}', which is not a Boolean");

        var expression = _sourceExpressions[raw];
        if (expression != null)
        {
            var direct = GetValue(expression);
            return direct?.ClientObject is bool db
                ? db
                : throw TestPageShapeGap.ControlProperty(
                    $"TestPage {propertyName} on page {_pageId} element {elementId}",
                    $"expression '{raw}' evaluated to '{direct?.ClientObject ?? "null"}', "
                    + "which is not a Boolean");
        }

        // Not one bare name, so it is an expression: `not Flag`, `A and B`, `Value <> ''`. The
        // AL compiler writes the source text of these properties into the metadata with the
        // identifiers already resolved to their emitted spelling — see PageControlExpression for
        // the measured shapes and the grammar.
        if (PageControlExpression.TryEvaluateBoolean(
                raw,
                atOpen ? ResolveExpressionIdentifierAtOpen : ResolveExpressionIdentifierLive,
                out var evaluated, out var why))
            return evaluated;

        // Loudly, not true-by-default: this property IS the page's read-only contract, and
        // answering "editable" for one we could not evaluate makes every test of that contract
        // unfailable. Naming the expression and what went wrong is what makes the gap fixable.
        throw TestPageShapeGap.ControlProperty(
            $"TestPage {propertyName} on page {_pageId} element {elementId}",
            $"the property is bound to expression '{raw}', which cannot be evaluated: {why}");
    }

    /// <summary>
    /// Which kind of page element a property was read from. An action's Enabled is the one arm a
    /// service tier measured for a dropped client expression, so the kind is what keeps the
    /// measured answer from being extended to arms nobody measured.
    /// </summary>
    internal enum PageElementKind
    {
        Control,
        Group,
        Action,
    }

    /// <summary>
    /// A property whose value arrives as the EMPTY string: the AL compiler had an expression here
    /// and dropped it, because a client expression may not call a procedure —
    /// <c>Enabled = IsAllowed()</c> compiles with AL0573 on an action ("Procedure calls is not
    /// valid for client expressions ... This warning will become an error in a future release")
    /// and with error AL0322 on a control, so only the action arms reach this at all.
    ///
    /// <para>An ACTION's Enabled answers <b>false</b>, and the OnAction is therefore skipped:
    /// measured on BC 28.4.53241.0 (onprem w1, container, test toolkit) for a procedure returning
    /// true unconditionally — issue #3731, proof in
    /// tests/runner-extras/testpage-procedure-bound-property. That claim cannot go in the corpus:
    /// AL0573 becomes an error in a future release, so a corpus codeunit carrying this AL is one
    /// BC minor away from failing every leg's compile.</para>
    ///
    /// <para>Every other arm refuses. No service tier has been asked what BC answers for an
    /// action's Visible, or for a group's, bound to a dropped expression, and borrowing the
    /// Enabled answer would be a guess presented as a measurement
    /// (.claude/rules/loud-failures.md). #3762 tracks measuring them.</para>
    ///
    /// <para>The answered arm is silent in BC, so the runner is silent too — but it warns once
    /// per element, because the AL author wrote a procedure call meaning it to be called.</para>
    /// </summary>
    private bool ClientExpressionTheCompilerDropped(string propertyName, int elementId, PageElementKind kind)
    {
        if (kind != PageElementKind.Action || propertyName != "Enabled")
            throw TestPageShapeGap.ControlProperty(
                $"TestPage {propertyName} on page {_pageId} element {elementId}",
                "the property is bound to a client expression the AL compiler dropped (a procedure "
                + "call: AL0573), and what real BC answers for this property has not been measured "
                + "on a service tier — see issue #3762");

        WarnOnceAboutDroppedClientExpression(_pageId.ToString(), elementId, propertyName);
        return false;
    }

    /// <summary>
    /// Once per (page, element, property) for the whole process: a bundle that opens the same page
    /// in ten tests must not print ten copies. `[warn]` because Log.Install() drops a tagged line
    /// at default verbosity unless the tag is a severity — see AlRunner.Tests/LoudDiagnosisReachesTheUserTests.
    /// Returns whether this call was the one that printed, so AlRunner.Tests can pin the "once"
    /// without a live NavForm.
    /// </summary>
    internal static bool WarnOnceAboutDroppedClientExpression(string pageId, int elementId, string propertyName)
    {
        if (!_droppedClientExpressionWarned.TryAdd((pageId, elementId, propertyName), true)) return false;

        Console.Error.WriteLine(
            $"[warn] RunnerPageInstance: page {pageId} element {elementId}: {propertyName} is bound to a "
            + "procedure call, which AL does not evaluate in a client expression (AL0573), so it "
            + "answers false here as it does on real BC — the procedure is never called. Bind the "
            + "property to a page variable a trigger assigns instead.");
        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Page, int Element, string Property), bool>
        _droppedClientExpressionWarned = new();

    /// <summary>
    /// The same resolution as <see cref="ResolveExpressionIdentifier"/>, but answering from the
    /// open-time snapshot. Used only for a control's own Visible — the one property real BC
    /// does not re-evaluate after the page is open.
    /// </summary>
    private bool ResolveExpressionIdentifierAtOpen(string name, bool quoted, out object? value)
    {
        if (_expressionValuesAtOpen.TryGetValue(name, out value)) return true;
        return ResolveExpressionIdentifier(name, quoted, out value);
    }

    private bool ResolveExpressionIdentifier(string name, bool quoted, out object? value)
    {
        var expression = _sourceExpressions[name];
        if (expression != null)
        {
            value = GetValue(expression)?.ClientObject;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Identifier resolution for the LIVE properties — an action's Enabled and Visible, and a
    /// control's Enabled and Editable. A registered source expression first, then a field on the
    /// page's source table, read off the record the page is currently on.
    ///
    /// <para>Measured on BC 28.4.53241.0 (container, test toolkit) and pinned upstream by corpus
    /// codeunit 60436 "TPAE Tests": all four of those properties follow the current row. An action
    /// declaring <c>Enabled = Rec.Flag</c> reports true and runs its OnAction on a row whose Flag
    /// is true, and reports false and skips it on a row whose Flag is false; a control's Enabled
    /// and Editable answer the same way. A control's own <c>Visible</c> does NOT — it reads the
    /// field's type default on every row (corpus codeunit 60755) — which is why that one property
    /// goes through <see cref="ResolveExpressionIdentifierAtOpen"/> and never reaches here.</para>
    ///
    /// <para>Before this, every such expression raised RunnerOutOfScopeException, which #3693
    /// turned into an un-invokable action — issue #3730.</para>
    ///
    /// <para>Ordering: a registered source expression is tried before a source-table field, so a
    /// page global sharing a field's name shadows the field. AL tells them apart by the
    /// <c>Rec.</c> prefix, which the metadata drops; whether BC resolves the same way here is
    /// UNMEASURED.</para>
    /// </summary>
    private bool ResolveExpressionIdentifierLive(string name, bool quoted, out object? value)
    {
        if (ResolveExpressionIdentifier(name, quoted, out value)) return true;
        return TryResolveSourceTableField(name, out value);
    }

    /// <summary>
    /// One source-table field, by the name the metadata carries, off the record the page is on.
    ///
    /// <para>The value is the field's <c>ClientObject</c>: a Boolean arrives as <c>bool</c>, a
    /// Text/Code as <c>string</c>, and an Option/Enum as its ORDINAL — which is the shape
    /// PageControlExpression needs, because the compiler writes an option comparison into the
    /// metadata with the member already lowered to a number (<c>Kind = 1</c>).</para>
    ///
    /// <para>False for a name the source table does not carry, false for a FlowField or
    /// FlowFilter, and false for a value shape the expression evaluator cannot compare, so the
    /// caller raises its refusal naming the expression rather than inventing an answer
    /// (.claude/rules/loud-failures.md).</para>
    /// </summary>
    private bool TryResolveSourceTableField(string name, out object? value)
    {
        value = null;
        if (_record?.MetaTable == null) return false;

        foreach (var field in RecordPatches.GetAllFields(_record.MetaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            if (!string.Equals(field.FieldName, name, StringComparison.OrdinalIgnoreCase)) continue;

            // GetAllFields is BC's NCLMetaTable.AllFields, FlowFields and FlowFilters included.
            // An UNCALCULATED FlowField's ClientObject is its type default, which narrows
            // cleanly below — so without this the runner would answer Enabled = false on a row
            // whose CalcFormula holds. What BC answers for a FlowField-bound live property is
            // unmeasured (no corpus arm covers it), so refuse rather than calculate: see
            // AlRunner.Tests/LivePropertyExpressionTests.cs's FlowField arm.
            if (field.FieldClass != Microsoft.Dynamics.Nav.Types.Metadata.FieldClass.Normal)
                return false;

            return TryComparableFieldValue(_record.GetFieldValue(field.FieldNo)?.ClientObject, out value);
        }

        return false;
    }

    /// <summary>
    /// A field's <c>ClientObject</c> narrowed to the shapes PageControlExpression's Compare can
    /// order: Boolean, Text/Code, and the numeric family — which is also where an Option/Enum
    /// arrives, as its ordinal, since the compiler lowers a member comparison to a number
    /// (<c>Kind = 1</c>).
    ///
    /// <para>Anything else — a Guid, a Blob, a Media, a DateFormula — is refused rather than
    /// passed through, so the caller raises its refusal naming the expression instead of handing
    /// the evaluator an operand it would have to invent an ordering for
    /// (.claude/rules/loud-failures.md). Internal so AlRunner.Tests can pin the narrowing without
    /// a live NavForm.</para>
    /// </summary>
    internal static bool TryComparableFieldValue(object? clientObject, out object? value)
    {
        switch (clientObject)
        {
            case bool:
            case string:
            case int:
            case long:
            case short:
            case byte:
            case decimal:
            case double:
            case float:
                value = clientObject;
                return true;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>
    /// The control's OptionCaption list, split on ',', or null when the control declares
    /// none. An Option control's captions live on the PAGE CONTROL, not on the option's
    /// own metadata (NCLOptionMetadata carries only the member names), and a TestPage sets
    /// an option by its caption, which is what the user sees.
    ///
    /// Read from <c>OptionCaptionML</c> rather than the plain <c>OptionCaption</c> sibling:
    /// the AL compiler emits the caption as a multi-language attribute
    /// (<c>OptionCaptionML="ENU=Fields,Blocks,Images,Fonts,Custom Fields,Labels"</c> in the
    /// emit-captured page metadata XML), and ML is the form BC resolves per language. BC's
    /// merge does also fill the plain OptionCaption, so both would answer today — ML is the
    /// one that stays correct for a non-ENU session.
    ///
    /// GetText resolves the session language and falls back to 1033 on its own, so the
    /// runner does not reimplement BC's language selection. BC's own indexed lookup does
    /// the control search — ControlDefinitions is a flat FindAll over the master page, so
    /// nesting (a control inside a repeater) needs no special handling here.
    ///
    /// <paramref name="boundOption"/> is the control's CURRENT bound value, when the caller
    /// already has one in hand (both call sites do: <c>LiveNavTestField.CurrentOption()</c>
    /// for a Rec-bound field, <c>PageVariableTestField</c>'s switch in <c>ToBoundValue</c> for
    /// a page-variable one). An Enum-typed control has no AL-level <c>OptionCaption</c>
    /// property to declare — only the <c>Option</c> primitive can — so
    /// <c>OptionCaptionML</c> is always empty for it (verified via
    /// AL_RUNNER_TRACE_PAGE_METADATA=2: an Enum-bound "KindSelector" control reports
    /// <c>OptionCaption='' OptionCaptionML=''</c>). Real BC computes an Enum's per-value
    /// captions from the enum's OWN metadata instead (see issue #1928's real-BC evidence:
    /// <c>TestPage.SetValue</c> on an Enum control resolves by the declared Caption and
    /// refuses the member name), so when the control declares no OptionCaption this falls
    /// back to <see cref="TestPageOptionValue.EnumCaptions"/>, sourced from the SAME
    /// emit-captured enum metadata already used (and already accepted as faithful) for
    /// <c>Enum::"X".Ordinals()/.Names()</c>.
    /// </summary>
    internal string[]? TryGetOptionCaptions(int controlId, NavOption? boundOption = null)
    {
        var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "2";
        var helper = _form is NavForm form ? form.MetadataHelper : null;
        if (helper == null)
        {
            if (trace) Console.Out.WriteLine($"[option-captions] control {controlId}: no MetadataHelper ({_form.GetType().Name})");
            return TestPageOptionValue.EnumCaptions(boundOption);
        }
        if (!helper.TryGetControlDefinitionById(controlId, out var definition) || definition == null)
        {
            if (trace)
            {
                Console.Out.WriteLine($"[option-captions] control {controlId}: not among the master page's control definitions");
                var mp = ((NavForm)_form).MasterPage;
                Console.Out.WriteLine($"[option-captions]   masterPage={(mp == null ? "NULL" : $"ID={mp.ID} {mp.GetType().Name}")}");
                if (mp != null)
                    Console.Out.WriteLine(
                        $"[option-captions]   contentArea.Controls={mp.ContentArea?.Controls?.Count}"
                        + $" removedControls={mp.RemovedControls?.Count}");
                // ControlDefinitions is internal to Ncl, hence reflection for the dump only.
                var defs = ReadProperty(helper, "ControlDefinitions");
                Console.Out.WriteLine($"[option-captions]   ControlDefinitions={(defs == null ? "NULL" : defs.GetType().Name)}");
                foreach (var d in defs as System.Collections.IEnumerable ?? Array.Empty<object>())
                    Console.Out.WriteLine($"[option-captions]   have {d?.GetType().Name} ID={ReadProperty(d!, "ID")} Name={ReadProperty(d!, "Name")}");
            }
            return TestPageOptionValue.EnumCaptions(boundOption);
        }
        if (trace)
            Console.Out.WriteLine(
                $"[option-captions] control {controlId} ({definition.Name}): OptionCaption='{definition.OptionCaption}' "
                + $"OptionCaptionML='{definition.OptionCaptionML?.GetText(1033)}'");

        // 1033 rather than a session lookup: the runner's skeleton session has a
        // zero-initialized culture, so HelperShims.NavSession_GlobalLanguage_1033 already
        // pins the whole runtime to en-US. Asking the session here would either return that
        // same 1033 or throw. GetText also treats 1033 as its own fallback, so a page that
        // somehow carried only a non-ENU caption set would still resolve.
        var captions = definition.OptionCaptionML?.GetText(1033);
        if (!string.IsNullOrEmpty(captions)) return captions.Split(',');

        // Option's OptionCaptionML is empty for an Enum-typed control by construction (see
        // the doc comment above) — fall back to the enum's own metadata.
        return TestPageOptionValue.EnumCaptions(boundOption);
    }

    /// <summary>
    /// The page's definition for a subpage PART control, or null when the control id is not
    /// a part on this page.
    ///
    /// A part is not a ControlDefinition — the AL compiler emits it as an
    /// <c>InfopartPageDefinition</c> carrying the hosted page's id in <c>PagePartID</c> and
    /// the SubPageLink as a <c>SubFormLink</c> list of FilterDefinitions — so it is reached
    /// through MetadataHelper.InfoPartDefinitions rather than through the control lookup.
    /// </summary>
    internal Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition? TryGetPartDefinition(int controlId)
    {
        if (_form is not NavForm form) return null;
        foreach (var definition in form.MetadataHelper.InfoPartDefinitions)
            if (definition is Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition part
                && part.ID == controlId)
                return part;
        return null;
    }

    /// <summary>
    /// Every subpage PART control id this page declares — issue #2677's eager-build list.
    /// Real BC materialises a page's declared parts (FactBoxes above all) as part of the
    /// host opening, without the host's own AL ever referencing <c>CurrPage.&lt;part&gt;</c>
    /// (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141, all 8 BC legs: with
    /// nothing touching the part, opening the host alone still fires the part's own
    /// OnOpenPage and OnAfterGetRecord/OnAfterGetCurrRecord). <see cref="TryGetPartDefinition"/>
    /// answers "is THIS control a part"; this answers "what are ALL of them" so the caller
    /// can eagerly reach every one the same way.
    /// </summary>
    internal IReadOnlyList<int> AllPartControlIds()
    {
        if (_form is not NavForm form) return Array.Empty<int>();
        var ids = new List<int>();
        foreach (var definition in form.MetadataHelper.InfoPartDefinitions)
            if (definition is Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition part)
                ids.Add(part.ID);
        return ids;
    }

    /// <summary>BC's key convention for a control's source expression.</summary>
    internal static string SourceExpressionKey(int controlId) => "Control" + controlId;

    /// <summary>BC's key convention for a control's FORMAT source expression.</summary>
    internal static string FormatExpressionKey(int controlId) => "Control" + controlId + "_Format";

    /// <summary>
    /// The .NET format string this control renders its value with, or null when the page
    /// publishes none.
    ///
    /// <para>The value is <b>BC's own</b>, not a runner approximation. The AL compiler emits,
    /// for every Decimal control, a registration of the shape</para>
    /// <code>
    /// RegisterSourceExpression("Control773217788_Format", …,
    ///     () =&gt; ALCompiler.ConvertToDotNetFormatString(
    ///               this.Session, this.GetDecimalString(this.Rec, 2, 773217788)), null);
    /// </code>
    /// <para>so evaluating it runs <c>NavForm.GetDecimalString</c>'s full cascade —
    /// the page's <c>GetAutoFormatString</c> override (which is where
    /// <c>AutoFormatType</c>/<c>AutoFormatExpression</c> reach the AutoFormat system
    /// codeunit), then the record's, then <c>GetDecimalPlaces</c> on each — and converts the
    /// BC format it produces into a .NET one. All of that already executes correctly inside
    /// the runner. The measured format strings live in
    /// <c>docs/limitations.md#testpage-decimal-formatting</c>, adjudicated upstream by corpus
    /// codeunit 60605 "ALT AutoFormat Tests" on all eight cloud legs — deliberately not
    /// restated here, because that table was copied into four places and the
    /// custom-expression arm was mislabelled in every one of them (#3406).</para>
    /// <para>Never throws: a page that published no format table, a control with no format
    /// expression, and an expression whose evaluation fails all answer null, and the caller
    /// falls back to its own historical spelling. A refusal here would turn every
    /// <c>Field.Value</c> read on an unusual page into a hard failure, which is a far worse
    /// answer than the two-decimal default this replaces for the controls it can read.</para>
    /// </summary>
    internal string? TryGetControlFormat(int controlId)
    {
        try
        {
            var expression = _sourceExpressions[FormatExpressionKey(controlId)];
            if (expression == null) return null;
            var value = GetValue(expression);
            var text = value?.ClientObject?.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            // See the remarks: unreadable format => the caller's default, never a failure.
            return null;
        }
    }

    internal static NavValue? GetValue(object expression)
        => (NavValue?)BcShape.Method(
            expression.GetType(), "Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            Type.EmptyTypes, "TestPage field expression access")
            .Invoke(expression, null);

    internal static void SetValue(object expression, NavValue value)
        => BcShape.Method(
            expression.GetType(), "Set", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            new[] { typeof(NavValue) }, "TestPage field expression access")
            .Invoke(expression, new object?[] { value });

    /// <summary>
    /// Run the control's OnValidate trigger, if it declares one.
    ///
    /// The AL compiler emits it as <c>{ControlName}_a{n}_OnValidate</c> on the page class.
    /// The control is identified by RE-DERIVING BC's own control id from each candidate's
    /// name — <c>IdSpace.GetMemberId(pageId, controlName)</c>, i.e. abs(FNV-1a over the
    /// UTF-16 bytes of pageId + name) — and comparing it to the id being set. Matching on
    /// the source expression's Name instead does not work: that is the bound VARIABLE's
    /// name (SelectedMode), not the control's (Mode), and they are routinely different.
    ///
    /// A control with no OnValidate simply has no such method, which is not an error.
    /// </summary>
    internal void RaiseOnValidate(int controlId)
    {
        // #3573: a pageextension `modify(Control)` block may wrap the base control's own
        // OnValidate with OnBeforeValidate / OnAfterValidate. BC runs all three in one
        // sequence — before, base, after — so an Error() in a before-trigger must prevent
        // both later stages, which falling out of this method on the exception achieves.
        foreach (var before in FindModifiedControlTriggers(controlId, "_OnBeforeValidate"))
            Invoke(before);

        var trigger = FindTrigger(controlId, "_OnValidate", "OnValidate");
        // A control with no OnValidate simply has no such method, which is not an error.
        if (trigger != null) Invoke(trigger.Value);

        foreach (var after in FindModifiedControlTriggers(controlId, "_OnAfterValidate"))
            Invoke(after);
    }

    /// <summary>
    /// Every pageextension trigger a <c>modify(<paramref name="controlId"/>)</c> block declares
    /// carrying <paramref name="suffix"/>, in ascending pageextension-id order.
    ///
    /// <para>Issue #3573. This is a DIFFERENT resolution from <see cref="FindTrigger"/>, and the
    /// difference is the id space. A control a pageextension ADDS belongs to the extension, so
    /// its member id hashes from the extension's own object id and FindTrigger's extension arm
    /// finds it. A control a pageextension MODIFIES still belongs to the BASE PAGE — AL's
    /// <c>modify(Name)</c> keeps the existing control's identity — so BC drives it by the base
    /// page's member id while the trigger body compiles onto the extension's type. Measured on
    /// the reproducer in #3573: the control being validated is id 709536759 =
    /// <c>MemberId(64520, "Name")</c> (the base page), while FindTrigger's extension arm asks
    /// for <c>MemberId(64521, "Name")</c> = 1257079618 and can never match. Hence the base
    /// page's id space here, against the extension's own methods.</para>
    ///
    /// <para>Scoped to control names the extension's AL source actually declares a
    /// <c>modify(...)</c> for (RecordPatches.GetModifiedControlNames), so this cannot reach a
    /// method belonging to an extension-added control that merely shares a name with a base
    /// control — those keep FindTrigger's ordinary path, which is the acceptance criterion
    /// "extension-added controls keep their ordinary trigger path".</para>
    ///
    /// <para>Order is the extensions' own id order, which GetPageExtensionIdsForPage sorts. That
    /// is deterministic but is NOT a claim about the order real BC uses when two extensions
    /// modify one control — nothing here has measured that, and no corpus test asserts it. The
    /// upstream test that accompanies this fix pins the single-extension sequence only.</para>
    /// </summary>
    private List<TriggerMatch> FindModifiedControlTriggers(int controlId, string suffix, int arity = 0)
    {
        var matches = new List<TriggerMatch>();
        foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(_pageId))
        {
            var modified = RecordPatches.GetModifiedControlNames(extensionId);
            if (modified.Count == 0) continue;

            var extInstance = GetOrCreateExtensionInstance(extensionId);
            if (extInstance == null) continue;

            foreach (var controlName in modified)
            {
                // The BASE page's id space — see the remarks. A modify() block whose target is
                // not the control being validated simply does not match.
                if (MemberId(_pageId, controlName) != controlId) continue;

                var match = FindTriggerOnTarget(extInstance, _pageId, controlId, suffix,
                    suffix.TrimStart('_'), arity, declaredName: controlName);
                if (match != null) matches.Add(match.Value);
            }
        }
        return matches;
    }

    /// <summary>
    /// Run the action's OnAction trigger.
    ///
    /// Unlike OnValidate, a missing trigger is NOT benign here: the AL test asked for the
    /// action to happen. An AL action carries EITHER an OnAction trigger or a RunObject, so a
    /// missing trigger means the effect is RunObject — performed by
    /// <see cref="TryRunActionRunObject"/> (issue #2931; see
    /// RunnerPageInstance.ActionRunObject.cs for what real BC does with one and how that was
    /// established). Only when the action declares NEITHER, or declares a RunObject shape the
    /// runner cannot yet perform faithfully, does this refuse by name rather than do nothing —
    /// silently doing nothing is what made an unrun action surface one step later as an
    /// assertion about its missing effect.
    ///
    /// Issue #1923: an action a PAGEEXTENSION contributes is compiled onto the extension's
    /// OWN type (<c>PageExtension{extId}</c>), not the base page's, and its member id hashes
    /// from the extension's OWN object id — never the page's. FindTrigger now also searches
    /// every pageextension that extends this page (own-bundle-source-compiled or a real
    /// PRECOMPILED dependency page, e.g. Base App "Item Attributes") before giving up. Before
    /// this fix a source-compiled base page's extension action was misclassified as this very
    /// RunnerOutOfScopeException (a real, dispatchable action reported as declaring no effect);
    /// a precompiled base page's extension action reached nowhere to throw
    /// against at all — Invoke() silently did nothing, in violation of loud-failures.md.
    /// (That second half no longer describes a Base App page: measured 2026-08-30, "Item
    /// Attributes" (7500) DOES resolve a live RunnerPageInstance and therefore arrives here and
    /// throws. TryRaiseExtensionOnlyAction is now reached only by a base page with no compiled
    /// type anywhere.)
    ///
    /// Issue #2113: an <c>actionref</c> — the standard promotion pattern
    /// (<c>actionref(X_Promoted; X)</c> in <c>area(Promoted)</c>) — is a delegating REFERENCE
    /// and carries no trigger of its own, so the id-keyed search above can never match it and
    /// every promoted <c>Invoke()</c> was refused as "declares no OnAction trigger" for an
    /// action that plainly declares one. <see cref="FindTriggerThroughActionRef"/> follows the
    /// reference to its target before the refusal is raised.
    /// </summary>
    internal void RaiseOnAction(int actionId)
    {
        // The action itself first — an ordinary action carries its own trigger, and an
        // actionref never does, so the ordinary case never pays for the actionref lookup.
        var trigger = FindTrigger(actionId, "_OnAction", "OnAction")
            ?? FindTriggerThroughActionRef(actionId);
        if (trigger != null)
        {
            Invoke(trigger.Value);
            return;
        }

        // No trigger. An AL action carries EITHER an OnAction trigger or a RunObject, never
        // both, so this is where the RunObject half belongs — see
        // RunnerPageInstance.ActionRunObject.cs for what real BC does with it and how that was
        // established. It returns false only when the action declares no RunObject either.
        if (TryRunActionRunObject(actionId)) return;

        // Neither a trigger nor a RunObject. Naming the actionref's TARGET matters: without it
        // the message blames the actionref for "declaring no trigger", which is true of every
        // actionref by construction and tells the reader nothing about why nothing ran.
        var refTarget = TryResolveActionRef(actionId);
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage action {actionId} on page {_pageId}",
            (refTarget == null
                ? "not-yet-implemented — the action declares neither an OnAction trigger nor a "
                  + "RunObject that the runner could resolve"
                : $"not-yet-implemented — this action is an actionref delegating to "
                  + $"'{refTarget.Value.TargetName}', and neither the page nor any pageextension "
                  + "of it declares an OnAction trigger or a resolvable RunObject for that action")
            + ". Invoking it therefore ran nothing, which would surface one step later as a "
            + "missing effect, so it is refused here instead");
    }

    /// <summary>
    /// The OnAction trigger of the action an <c>actionref</c> DELEGATES to, or null when
    /// <paramref name="actionId"/> is not an actionref (or its target declares no trigger).
    ///
    /// <para>Issue #2113. <c>actionref(X_Promoted; X)</c> in a page's own <c>area(Promoted)</c>
    /// — the standard promotion pattern — is a reference, not an action: on real BC invoking
    /// the promoted ref and invoking <c>X</c> are the same command. The AL grammar gives an
    /// actionref nowhere to put a trigger, so the emitted <c>*_OnAction</c> method carries the
    /// TARGET's name and hashes from the TARGET's member id. Asking
    /// <see cref="FindTrigger"/> about the actionref's OWN id therefore always came up empty,
    /// and a promoted <c>Invoke()</c> was refused as "the page declares no OnAction trigger"
    /// while invoking the same action directly worked.</para>
    ///
    /// <para>The target is followed by NAME across id spaces, because an actionref and its
    /// target need not be declared by the same object: a pageextension's
    /// <c>addlast(Promoted) { actionref(R; BaseAction) }</c> points at an action on the BASE
    /// page, whose member id hashes from the base page's object id. All four observed shapes —
    /// page-own flat ref, page-own ref inside a promoted <c>group</c>, extension ref to an
    /// extension action, extension ref to a base-page action — go through here.</para>
    ///
    /// <para>A ref chain (a target that is itself an actionref) is followed too, with a
    /// visited set so a malformed cycle terminates instead of hanging.</para>
    /// </summary>
    private TriggerMatch? FindTriggerThroughActionRef(int actionId)
    {
        var visited = new HashSet<(int, int)>();
        var resolved = TryResolveActionRef(actionId);
        while (resolved is { } step && visited.Add((step.DeclaringObjectId, actionId)))
        {
            var match = FindTriggerByName(step.TargetName, step.DeclaringObjectId, "_OnAction", "OnAction");
            if (match != null) return match;

            // The target names an actionref rather than an action — keep following. Legal AL
            // rarely does this, but a chain that silently stopped here would report the same
            // misleading "declares no trigger" the whole fix exists to remove.
            actionId = MemberId(step.DeclaringObjectId, step.TargetName);
            resolved = TryResolveActionRef(actionId);
        }
        return null;
    }

    /// <summary>
    /// The object that declares the <c>actionref</c> <paramref name="memberId"/> and the NAME
    /// of the action it points at, or null when the member is not an actionref of this page or
    /// of any pageextension that extends it.
    /// </summary>
    private (int DeclaringObjectId, string TargetName)? TryResolveActionRef(int memberId)
    {
        var own = RecordPatches.TryGetActionRefTarget(_pageId, memberId, isExtension: false);
        if (own != null) return (_pageId, own);

        foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(_pageId))
        {
            var target = RecordPatches.TryGetActionRefTarget(extensionId, memberId, isExtension: true);
            if (target != null) return (extensionId, target);
        }
        return null;
    }

    /// <summary>
    /// <see cref="FindTrigger"/> driven by a member NAME instead of a member id, for a target
    /// reached through an <c>actionref</c> (#2113). The id cannot be pre-computed by the
    /// caller because it depends on WHICH object turns out to declare the target: the same
    /// name hashes to a different member id in the base page's id space than in a
    /// pageextension's.
    ///
    /// <para><paramref name="preferredObjectId"/> — the object that declared the actionref —
    /// is searched first, mirroring AL's own scoping: a pageextension's actionref may target
    /// either its own action or a base-page action, and its own is the nearer binding.</para>
    /// </summary>
    private TriggerMatch? FindTriggerByName(string name, int preferredObjectId, string suffix, string surface,
        int arity = 0)
    {
        foreach (var objectId in CandidateDeclaringObjectIds(preferredObjectId))
        {
            var instance = objectId == _pageId ? _form : GetOrCreateExtensionInstance(objectId);
            if (instance == null) continue;
            var match = FindTriggerOnTarget(instance, objectId, MemberId(objectId, name),
                suffix, surface, arity, name);
            if (match != null) return match;
        }
        return null;
    }

    /// <summary>Preferred object, then the base page, then every pageextension — deduped.</summary>
    private IEnumerable<int> CandidateDeclaringObjectIds(int preferredObjectId)
    {
        var seen = new HashSet<int> { preferredObjectId };
        yield return preferredObjectId;
        if (seen.Add(_pageId)) yield return _pageId;
        foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(_pageId))
            if (seen.Add(extensionId)) yield return extensionId;
    }

    /// <summary>
    /// Run the control's OnLookup trigger.
    ///
    /// Like OnAction and unlike OnValidate, a missing trigger is NOT benign: the test asked
    /// for the lookup to happen. A control with no OnLookup gets its lookup from a TableRelation
    /// (BC opens the related table's list page), which the runner cannot stand up, so it
    /// refuses by name — doing nothing silently is what let a test compare two empty strings
    /// and call it a pass.
    /// </summary>
    /// <summary>
    /// Run the control's OnLookup trigger and return the value it selected, or null when the
    /// trigger declined (returned false) — BC's lookup contract: the text the trigger wrote
    /// back replaces the field's value only if it returned true, which is how "the user
    /// cancelled the lookup" is expressed.
    ///
    /// The AL compiler emits <c>trigger OnLookup(var Text: Text): Boolean</c> as a method
    /// taking <c>ByRef&lt;NavText&gt;</c> and returning bool, so unlike OnValidate/OnAction
    /// this one is NOT parameterless — matching only zero-arity methods is why it read as
    /// "the control declares no OnLookup trigger" for a control that plainly declares one.
    ///
    /// A control with genuinely no OnLookup gets its lookup from a TableRelation, which would
    /// open the related table's list page; the runner cannot stand that up, so it refuses by
    /// name rather than doing nothing — doing nothing let a test invoke a lookup, observe no
    /// change, and compare two empty strings successfully.
    /// </summary>
    internal NavText? RaiseOnLookup(int controlId, NavText current,
        NavRecord? sourceRecord = null, int sourceFieldNo = 0)
    {
        var found = FindTrigger(controlId, "_OnLookup", "OnLookup", arity: 1);
        if (found == null)
            return RaiseSourceFieldOnLookup(controlId, sourceRecord, sourceFieldNo);
        var trigger = found.Value;

        var value = current;
        var byRef = new ByRef<NavText>(() => value, v => value = v);

        object? result;
        // Bracketed like Invoke/InvokeRecordTrigger: an OnLookup that calls CurrPage.Update is
        // an AL trigger like any other, and it owns the refresh its call armed.
        BeginTrigger();
        var completed = false;
        try
        {
            // AwaitTriggerResult, not a bare Invoke: BC emits `trigger OnLookup(...): Boolean` as
            // `ValueTask<bool>`, so the raw Invoke result never pattern-matches `is true` and every
            // lookup read as "the user cancelled" — see AwaitTriggerResult's remarks.
            try { result = AwaitTriggerResult(trigger.Method.Invoke(trigger.Target, new object?[] { byRef })); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            completed = true;
        }
        finally { EndTrigger(completed); }

        return result is true ? value : null;
    }

    /// <summary>
    /// The second of the three places AL can put a lookup, tried when the page control has no
    /// OnLookup trigger of its own: the SOURCE TABLE FIELD's <c>trigger OnLookup()</c>.
    ///
    /// <para>#2549. These are two unrelated triggers that share a name. The control's takes
    /// <c>var Text: Text</c> and returns Boolean — the return value is how "the user cancelled"
    /// is expressed, and the text it wrote back is what replaces the field's value. The table
    /// field's is parameterless and writes into <c>Rec</c> itself, so there is nothing to hand
    /// back and nothing to gate on: this returns null, and the caller reads the field's value
    /// off the record, where the trigger already put it. Returning null is not "cancelled" here,
    /// it is "no client-side write-back applies" — which produces the same caller behaviour.</para>
    ///
    /// <para>Run through BC's own public <c>NavRecord.LookupAsync(int)</c>, not by invoking the
    /// handler this probe found. That method re-resolves the handler through
    /// <c>GetFieldTriggerHandler</c>, which refuses to fire a trigger on an untyped NavRecord and
    /// calls <c>EnsureGlobalVariablesInitialized()</c> first; its
    /// <c>InvokeFieldTriggerHandlerAsync</c> also dispatches to the right tableextension instance
    /// when the trigger came from one. Invoking the handler directly skips all three.</para>
    ///
    /// <para>A field with NEITHER trigger keeps refusing: its lookup comes from a TableRelation,
    /// which on real BC opens the related table's list page, and the runner cannot stand that up.
    /// Doing nothing there is what let a test invoke a lookup, observe no change, and compare two
    /// empty strings successfully. A control not bound to a source-table field at all — a page
    /// global — has no table field to fall back to and lands in the same refusal.</para>
    /// </summary>
    private NavText? RaiseSourceFieldOnLookup(int controlId, NavRecord? sourceRecord, int sourceFieldNo)
    {
        // A control bound to a page GLOBAL rather than to a source-table field has no table
        // field to fall back to, and saying "nor its source table field declares one" about a
        // field that does not exist points the reader at the wrong thing.
        if (sourceRecord == null || sourceFieldNo <= 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "testpage-lookup — the control declares no OnLookup trigger and is not bound to a "
                + "source-table field, so there is no table-field OnLookup to fall back to and its "
                + "lookup would come from a TableRelation, which the runner cannot stand up. "
                + "See docs/scope.md");

        var has = AlRunner.Patches.RecordPatches.TryHasFieldLookupTrigger(sourceRecord, sourceFieldNo);

        // Undeterminable is its own outcome, with its own TYPE. Saying "neither declares an
        // OnLookup trigger" when the real reason is that BC's metafield shape moved under our
        // reflection would send the reader to look at their AL, where there is nothing to find.
        //
        // A BC SHAPE GAP, not a scope claim (#2946/#2995). This site's own text already said
        // "This is a runner/BC-shape problem, not a problem with the AL under test" while
        // raising a permanence claim about scope. TryHasFieldLookupTrigger is three-valued on
        // purpose and returns null ONLY when EnsureFieldTriggerReflection could not resolve BC's
        // private backing fields, or the reflection threw — a read that SUCCEEDS and says the
        // field declares no trigger returns false and lands on the permanent refusal below.
        // That is exactly the line: the read could not be performed, versus the read succeeded
        // and the answer was unwelcome.
        if (has == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "NCLMetaField.EventTriggerDataValue / EventTriggerData.LookupHandler",
                $"could not determine whether field {sourceFieldNo} of the source table declares "
                + "an OnLookup trigger on this BC build, so the runner cannot tell a field with a "
                + "table trigger from one whose lookup comes from a TableRelation");

        if (has != true)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "testpage-lookup — neither the control nor its source table field declares an "
                + "OnLookup trigger, so the lookup comes from a TableRelation and would open the "
                + "related table's list page, which the runner cannot stand up. See docs/scope.md");

        sourceRecord.LookupAsync(sourceFieldNo).GetAwaiter().GetResult();
        return null;
    }

    /// <summary>
    /// Run the control's OnDrillDown trigger — the AL a user's drilldown click would run.
    ///
    /// Unlike OnLookup's TableRelation fallback (which needs a related list page the runner
    /// cannot stand up), a control with no OnDrillDown trigger has a documented, deterministic
    /// answer on real BC that does not depend on any UI: TestPage DrillDown() raises a fixed
    /// platform error, "The NavDrilldownAction method is not supported." — confirmed against
    /// real BC 27.5 and 28.3 in al-language's TestPageFieldDrillDown_Tests
    /// (FieldDrillDownWithNoTriggerIsRefused). That is reproducible in-process with no UI, so
    /// it is raised as a genuine AL error via NavNCLDialogException (same mechanism as
    /// BcRuntime's DataTransfer-out-of-context message), not a RunnerOutOfScopeException —
    /// this is not a capability the runner lacks, it is exactly what BC itself does here.
    /// </summary>
    internal void RaiseOnDrillDown(int controlId)
    {
        var trigger = FindTrigger(controlId, "_OnDrillDown", "OnDrillDown");
        if (trigger == null)
            throw AlRunner.BcRuntime.MakeNavDrilldownActionNotSupportedException();
        Invoke(trigger.Value);
    }

    /// <summary>
    /// Run the control's OnAssistEdit trigger — the AL a user's AssistEdit (the "…" button)
    /// would run. Issues #2362 and #3642.
    ///
    /// <para>Reached from BC's own <c>NavTestField.ALAssistEdit</c>, whose whole body is
    /// <c>CheckError(() =&gt; testField.AssistEdit())</c> — so <c>ITestField.AssistEdit</c>,
    /// which this runner implements, IS the dispatch surface, and implementing it as
    /// <c>{ }</c> is what made every declared OnAssistEdit inert.</para>
    ///
    /// <para>A control with NO OnAssistEdit does nothing here, and that absence is the whole
    /// reason this cannot follow RaiseOnDrillDown's shape. Drilldown has a loud documented
    /// answer to raise ("The NavDrilldownAction method is not supported."); assist-edit has
    /// none — BC's ALAssistEdit returns void and raises nothing — so a refusal here would
    /// invent an error real BC does not raise, which is a worse wrong answer than the silence
    /// it replaces. The silence is faithful; what was unfaithful was staying silent when the
    /// control DOES declare a trigger.</para>
    ///
    /// <para>Resolution is the ordinary <see cref="FindTrigger"/>, which is what makes this
    /// serve both forms the issues name. Measured on BC 28.1: a base-page control emits
    /// <c>Name_a45_OnAssistEdit(0)</c> on <c>Page{id}</c>, and a control a pageextension's
    /// <c>modify()</c> block contributes emits <c>Extra_a45_OnAssistEdit(0)</c> on
    /// <c>PageExtension{id}</c> — the SAME suffix and arity, differing only in which object
    /// carries it and in which id space its name hashes. FindTrigger's three arms already
    /// cover all three of those spaces after #3573, so no new resolution is needed.</para>
    /// </summary>
    internal void RaiseOnAssistEdit(int controlId)
    {
        var trigger = FindTrigger(controlId, "_OnAssistEdit", "OnAssistEdit");
        if (trigger != null) Invoke(trigger.Value);
    }

    /// <summary>
    /// Run the page's own OnAfterGetRecord trigger.
    ///
    /// BC fires it every time the page loads a row, and it is where a page computes the
    /// per-row state its control properties then read (<c>RowEditable := not Rec.Locked</c>,
    /// <c>CurrPage.Editable(…)</c>). Never firing it left that state at its default for the
    /// whole life of the page, so every row looked like the first one — and a page whose
    /// read-only rule lives entirely in this trigger behaved as if it had no rule at all.
    ///
    /// Unlike an action's OnAction, a page with no OnAfterGetRecord is the common case and
    /// not an error. The trigger is a plain parameterless method named for the trigger
    /// itself, not a member trigger, so it carries no <c>_a{n}_</c> disambiguator.
    /// </summary>
    internal void RaiseOnAfterGetRecord()
    {
        InvokeRecordTrigger("OnAfterGetRecord", Type.EmptyTypes, Array.Empty<object>());
        // OnAfterGetRecord does NOT re-fire for a record the page already fetched, so a page
        // that must refresh derived state on every move puts it here instead. Both are part
        // of "a row became current", so both belong on the same path.
        InvokeRecordTrigger("OnAfterGetCurrRecord", Type.EmptyTypes, Array.Empty<object>());
    }

    // Set the first time RaiseOnOpenPage runs. A SECOND (or later) call means the TestPage
    // was closed and reopened — issue #2658. The runner attaches its ITestPage at
    // CONSTRUCTION (see RunnerTestPageState's WHY note) and keeps the SAME RunnerPageInstance,
    // and therefore the SAME compiled page object, for the whole life of the TestPage
    // variable. Real BC does not: closing a page discards its client-side instance, and
    // reopening builds a fresh one — corpus CU60266 ReopeningThePage_IsHowTheNewVisibleIsObserved
    // measures this directly: a page global toggled true in the first open reads false again
    // (its type default) once the page is reopened, and a control's own Visible — which
    // freezes at open-time (see the snapshot note below) — follows it back to true.
    private bool _hasOpenedBefore;

    /// <summary>
    /// Run the page's OnOpenPage trigger.
    ///
    /// This is where a page establishes what it is looking at before anyone reads it — a
    /// singleton buffer fetched or created for the current user, a filter narrowed to the
    /// caller's context, derived state computed once. Skipping it left the page's record
    /// unpositioned and blank, so the first thing the page's own AL did with it (a Modify,
    /// a Validate) failed against a row that was never fetched — and the error named a
    /// missing record rather than a trigger that never ran.
    /// </summary>
    internal void RaiseOnOpenPage()
    {
        // A reopen: reset the page's OWN global variables to their AL type defaults before
        // anything (including OnOpenPage) can read or re-seed them, matching a freshly
        // built client-side page instance. Never on the first open — the object was just
        // constructed and is already at its defaults, and skipping it there keeps every
        // page's normal open path free of the extra scratch construction below.
        if (_hasOpenedBefore) ResetGlobalsForReopen();
        _hasOpenedBefore = true;

        // A reopen un-closes the page, and the mark has to go with it. RunnerPageInstance keeps
        // the SAME _form across close and reopen — that is what _hasOpenedBefore and
        // ResetGlobalsForReopen exist for (#2658) — so without this a page closed once with
        // CurrPage.Close() would have GetBuiltInAction refusing "The TestPage is not open."
        // forever, on a page BC considers open again.
        ClosedForms.Remove(_form);

        // BEFORE the trigger, exactly where BC puts it: NavForm.OpenFormAsync runs
        // ApplySourceTableViewAndSavedValuesAsync() and only then RaiseOnOpenPageAsync().
        // A page's OnOpenPage is entitled to READ the SourceTableView's filters (Base
        // Application page 7016 "Sales Price List" does — see ApplySourceTableViewFilters).
        ApplySourceTableViewFilters();

        InvokeRecordTrigger("OnOpenPage", Type.EmptyTypes, Array.Empty<object>());

        // Re-take the open-time snapshot AFTER the trigger, not before. OnOpenPage is where a
        // page seeds the globals its control properties are bound to, and the constructor runs
        // well before it — snapshotting only there froze every such global at its type default,
        // which the corpus caught immediately: the five TPCE tests whose global is true at open
        // all failed. The constructor still takes one, so a page whose OnOpenPage never runs
        // still has a snapshot rather than none.
        _expressionValuesAtOpen = SnapshotExpressionValues(_sourceExpressions);
    }

    /// <summary>
    /// Apply the page's <c>SourceTableView</c> — BC's own <c>NavForm.ApplySourceTableView</c>,
    /// on the page's own metadata, in BC's own filter group.
    ///
    /// WHY THIS EXISTS (issue #2820). The runner's TestPage machinery opens a page by
    /// invoking its lifecycle triggers directly rather than through BC's
    /// <c>NavForm.OpenFormAsync</c>, and <c>OpenFormAsync</c> is what calls
    /// <c>ApplySourceTableView</c> (via <c>ApplySourceTableViewAndSavedValuesAsync</c>; the
    /// other in-BC caller is <c>SetTableView(NavRecord)</c>). So on that route a page
    /// declaring <c>SourceTableView = where(...)</c> opened with NO view filters at all: the
    /// page showed rows the view excludes, and <c>Rec.GetFilter(...)</c> inside
    /// <c>FilterGroup(2)</c> — where BC puts them — answered blank.
    ///
    /// <para>Scope of this insertion, stated precisely because "every page open" would be
    /// wrong: all five call sites that raise a page's OnOpenPage funnel through
    /// <see cref="RaiseOnOpenPage"/> (host construction, part reification, the eager
    /// recordless-part hook, RunnerTestPageState and MockTestPage), so this one line covers
    /// all five. It is NOT the only way a page opens in this runner —
    /// <c>RunnerModalDispatch.TryOpenForm</c> invokes BC's own <c>NavForm.OpenForm()</c> for
    /// a modal page AL runs itself, and that route already calls ApplySourceTableView on its
    /// own. That route was equally broken before this change, for the other half's reason: a
    /// page from a precompiled dependency .app carried no <c>&lt;SourceTableView&gt;</c> in
    /// its synthesized metadata, so BC's own call had nothing to apply. The metadata half
    /// (RecordPatches.DependencyPageMetadataXml) is what fixes that path; this line is what
    /// fixes the TestPage path.</para>
    ///
    /// Measured on Base Application page 7016 "Sales Price List"
    /// (<c>SourceTableView = where("Price Type" = const(Sale))</c>): its OnOpenPage reaches
    /// codeunit 7018 "Price UX Management".GetFirstSourceFromFilter, whose last statement is
    /// <c>Evaluate(PriceSource."Price Type", PriceListHeader.GetFilter("Price Type"))</c>
    /// inside FilterGroup(2). With the view unapplied that GetFilter returns <c>''</c>, and
    /// evaluating <c>''</c> into enum "Price Type" — whose members are Any,Sale,Purchase,
    /// none of them blank — throws NavNCLInvalidOptionStringException. The blank was never
    /// the defect; the missing filter was.
    ///
    /// Delegated to BC rather than restated here: the method reads the view's Sorting
    /// (KeyFieldsSetByView / AscendingSetByView) as well as its TableFilters, and sets
    /// <c>ALFilterGroup = 2</c> around the SetFilter calls, restoring the previous group in a
    /// finally. Re-deriving any of that would be guesswork whose only symptom is a filter
    /// silently landing in the wrong group.
    ///
    /// Called on every open, including a reopen, which is what BC does too — a reopened page
    /// is a fresh client-side instance whose view is applied again from metadata.
    /// </summary>
    private void ApplySourceTableViewFilters()
    {
        if (_form is not NavForm form) return;

        // BC's own NavForm.SourceTableView getter dereferences MasterPage with no null check,
        // and ApplySourceTableView calls it first thing. A page the runner could build no real
        // metadata for (neither source-compiled nor described by a dependency's
        // SymbolReference.json — see RunnerFormInit.ShouldResolveMasterPage) has a null
        // MasterPage, so ask the same question BC asks, without the NRE: no metadata, or
        // metadata stating no view, means there is nothing to apply. This is not a
        // failure-swallowing guard — a page with a view that this cannot see would show
        // unfiltered rows, and that is the very defect the method exists to fix, so the two
        // states are distinguished rather than merged.
        if (form.MasterPage?.PageProperties?.SourceObject?.SourceTableView is null) return;

        var apply = FindNavFormMethod("ApplySourceTableView", Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                "NavForm.ApplySourceTableView not found on " + form.GetType().FullName
                + " — BC page shape changed");
        try { apply.Invoke(form, Array.Empty<object>()); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // A filter the page's own metadata declares that BC's filter parser rejects is
            // the page's own error and belongs to the AL test unwrapped, exactly like an
            // Error() raised in a trigger.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    /// <summary>
    /// Reset every field the compiled page TYPE ITSELF declares — its AL <c>var</c> globals —
    /// back to the value a brand-new instance of that type would carry. Fields inherited from
    /// <c>NavForm</c> (the record cursor, control-tree wiring, source-expression table, …) are
    /// deliberately untouched: <c>DeclaredOnly</c> excludes them, so this can never disturb the
    /// plumbing <see cref="TryCreate"/> already built.
    ///
    /// The "value a brand-new instance would carry" is taken from an actual scratch instance
    /// built through the SAME <c>(ITreeObject, NavRecord)</c> constructor <see cref="TryCreate"/>
    /// uses, rather than a hand-rolled CLR default per field — AL's field initializers (a Text
    /// global to <c>''</c>, a Record global to its own fresh instance, …) run inside that
    /// constructor, and reproducing them by hand would silently diverge the moment a page
    /// declares a global whose AL default is not the CLR default. The scratch instance is
    /// deliberately NOT put through <see cref="RunnerFormInit.MarkRealInit"/> or
    /// <c>SetSourceTable</c> — those are guarded to no-op without MarkRealInit (see
    /// <see cref="TryCreateRecordless"/>'s note), so building one is side-effect-free, and this
    /// method only ever reads its declared fields, never anything SetSourceTable would have
    /// wired.
    /// </summary>
    private void ResetGlobalsForReopen()
    {
        try
        {
            var formType = _form.GetType();
            var ctor = formType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 2
                                  && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
            if (ctor == null) return;

            var scratch = ctor.Invoke(new object?[] { _owner, _record });
            foreach (var field in formType.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                field.SetValue(_form, field.GetValue(scratch));
            }
        }
        catch
        {
            // Best-effort: a page whose globals could not be reset behaves as it did before
            // this existed — stale on reopen, not crashing on reopen.
        }
    }

    /// <summary>
    /// Run the page's OnQueryClosePage / OnClosePage triggers, in BC's order. OnQueryClosePage
    /// returning false vetoes the close, which is how a page refuses to be dismissed with
    /// unsaved work; NavForm's base returns true, so a page declaring none closes normally.
    /// </summary>
    internal bool RaiseOnClosePage(Microsoft.Dynamics.Nav.Types.FormResult closeAction)
        => RaiseOnClosePage(closeAction, out _);

    /// <inheritdoc cref="RaiseOnClosePage(Microsoft.Dynamics.Nav.Types.FormResult)"/>
    /// <param name="refusal">
    /// Why the close was refused, when it was. The two refusals are not interchangeable and the
    /// callers do different things with them — see <see cref="CloseRefusal"/>.
    /// </param>
    internal bool RaiseOnClosePage(
        Microsoft.Dynamics.Nav.Types.FormResult closeAction, out CloseRefusal refusal)
    {
        refusal = CloseRefusal.None;
        object? queryClose;
        try
        {
            queryClose = InvokeRecordTrigger("OnQueryClosePage",
                new[] { typeof(Microsoft.Dynamics.Nav.Types.FormResult) },
                new object[] { closeAction });
        }
        catch (Exception ex)
        {
            // An AL Error() inside OnQueryClosePage is not the caller's error to receive raw:
            // in BC the close is a client round trip, and the client's own close handler shows
            // the text as a MESSAGE and refuses the close. Same decision as the handler-driven
            // path in RunnerModalDispatch — TestPageProxy.InternalClose reaches
            // NavFormCloseHandler.ExecuteCloseCore too, so both shapes must agree. See
            // RunnerFormCloseHandler and issue #3057.
            //
            // RefuseCloseAfter either does not return (no [MessageHandler]: BC's own
            // "Unhandled UI: Message …" comes out of it) or answers false, which is BC's
            // `return false` after a handler consumed the text. Only the second reaches here.
            var mayClose = RunnerFormCloseHandler.RefuseCloseAfter(ex, TestExecutionOrNull());
            if (!mayClose) refusal = CloseRefusal.ErrorShownAsMessage;
            return mayClose;
        }

        if (queryClose is false)
        {
            refusal = CloseRefusal.TriggerReturnedFalse;
            return false;
        }
        InvokeRecordTrigger("OnClosePage", Type.EmptyTypes, Array.Empty<object>());
        return true;
    }

    /// <summary>
    /// Why <see cref="RaiseOnClosePage(Microsoft.Dynamics.Nav.Types.FormResult, out CloseRefusal)"/>
    /// refused a close. Both mean "BC did not perform this close", and they are kept apart
    /// because what the runner can faithfully do next differs between them.
    /// </summary>
    internal enum CloseRefusal
    {
        /// <summary>The close happened.</summary>
        None,

        /// <summary>
        /// <c>OnQueryClosePage</c> returned <c>false</c> — a plain veto. On the explicit
        /// <c>TestPage.Close()</c> path this stays a refusal (docs/scope.md): BC leaves the page
        /// open awaiting a user, and no service tier has been asked what a test observes after
        /// one. It is NOT the same question as the arm below, which has been asked.
        /// </summary>
        TriggerReturnedFalse,

        /// <summary>
        /// <c>OnQueryClosePage</c> raised an AL error and a <c>[MessageHandler]</c> consumed the
        /// text, so BC's close handler reached its own <c>return false</c>. Measured on a real
        /// service tier — corpus codeunit 60602 "QCM Query Close Msg Tests"
        /// (StefanMaron/BusinessCentral.AL.Language.Tests#272), green on all eight cloud legs
        /// and on the Windows nightly: the caller regains control, nothing is raised, and the
        /// page stays open and drivable. See docs/limitations.md#testpage-shape-gaps.
        /// </summary>
        ErrorShownAsMessage,
    }

    /// <summary>
    /// BC's <c>NavTestExecution</c> for this page's session, reached the way BC reaches it —
    /// <c>NavApplicationObjectBase.Session</c> on the form, then <c>NavSession.TestExecution</c>.
    /// Null outside a test session, which is what <see cref="RunnerFormCloseHandler"/> treats as
    /// "no message channel".
    /// </summary>
    private object? TestExecutionOrNull()
    {
        var session = _form.GetType()
            .GetProperty("Session", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(_form);
        return session?.GetType()
            .GetProperty("TestExecution", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(session);
    }

    /// <summary>
    /// Bring BC's own form state in line after this page has already been closed by the
    /// runner's trigger-raising path, using BC's <c>NavForm.ForceClose()</c> — which
    /// unregisters the form and clears <c>IsOpen</c> and, deliberately, raises nothing.
    ///
    /// It exists because there are two ways a modal page ends and they used to disagree about
    /// whether the page was still open. <c>CurrPage.Close()</c> reaches BC's
    /// <c>NavForm.Close()</c> → <c>NavTestExecution.ClosePage</c> → <c>ITestPage.Close()</c>,
    /// which lands on <see cref="LiveNavTestPage.Close"/> and raises OnQueryClosePage and
    /// OnClosePage itself — but never called <c>CloseForm</c>, so <c>IsOpen</c> stayed true and
    /// the modal dispatch ran the whole sequence a second time when the handler returned. Both
    /// triggers fired twice, and a page that persists from OnQueryClosePage wrote twice
    /// (issue #3091).
    ///
    /// <c>ForceClose</c> rather than <c>CloseForm</c> is the point: the triggers have already
    /// run exactly once, at the moment the AL asked for them, and <c>CloseForm</c> would raise
    /// OnClosePage again. Real BC runs each once — corpus codeunit 60296 "MQC Self Close
    /// Tests", measured on a service tier.
    /// </summary>
    internal void ForceCloseForm()
    {
        // Recorded BEFORE the call and keyed on the FORM, because that is the only object the
        // two views of this page share: RunnerTestClientSession.GetPage builds a fresh
        // LiveNavTestPage (and Adopt a fresh RunnerPageInstance) every time, so the instance
        // the handler is holding is never the one that performed the close. A flag on either
        // instance would be invisible to the other.
        //
        // Deliberately NOT read off NavForm.IsOpen: a page a test opened itself may have a form
        // that was never opened at all, and "never opened" must stay distinguishable from
        // "closed from AL" -- reading IsOpen for this refused four TRT Tests whose OpenNew() /
        // OK().Invoke() flow is entirely legitimate.
        ClosedForms.AddOrUpdate(_form, Sentinel);

        // Loud, like ApplySourceTableView and SplitKey in this file. Returning quietly would
        // leave the form MARKED but still IsOpen, which is precisely the double-close #3091
        // fixes — reintroduced with no signal at all.
        var forceClose = FindNavFormMethod("ForceClose", Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                "NavForm.ForceClose not found on " + _form.GetType().FullName
                + " — BC page shape changed");
        try { forceClose.Invoke(_form, Array.Empty<object>()); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    /// <summary>
    /// Run the page's OnNewRecord trigger — the one that seeds the defaults a blank record
    /// does not have (<c>Rec.Validate(Scope, Scope::Tenant)</c> and friends). Skipping it
    /// does not fail where the mistake is: the row still inserts, just carrying the field
    /// defaults instead of the page's, and the test complains about a value.
    /// </summary>
    internal void RaiseOnNewRecord(bool belowXRec)
        => InvokeRecordTrigger("OnNewRecord", new[] { typeof(bool) }, new object[] { belowXRec });

    /// <summary>
    /// Run the page's OnInsertRecord trigger and report whether the insert may proceed.
    ///
    /// The return value is the point of the trigger — it is the page's veto. NavForm's own
    /// base implementation returns true, so a page that declares none still inserts, and no
    /// separate "has a trigger" test is needed.
    /// </summary>
    internal bool RaiseOnInsertRecord(bool belowXRec)
        => InvokeRecordTrigger("OnInsertRecord", new[] { typeof(bool) }, new object[] { belowXRec })
           is not false;

    /// <summary>
    /// Assign the page's <c>AutoSplitKey</c> field on the row about to be inserted — BC's own
    /// <c>NavForm.SplitKey()</c>, called at exactly the point BC calls it
    /// (<c>SaveRecordAsync</c> / <c>InsertAsync(belowXRec)</c>: SplitKey, then OnInsertRecord,
    /// then the record's Insert).
    ///
    /// Reused rather than reimplemented, and the detail is why. SplitKey is not "last line no.
    /// + 10000": it reads <c>MasterPage.PageProperties.SourceObject.AutoSplitKey</c> to decide
    /// whether to act at all, takes the LAST field of the primary key, refuses a key field that
    /// is not GUID/Integer/BigInteger/Decimal, leaves a value the AL already set alone, splits
    /// the interval when the row is being inserted BETWEEN two existing ones, and falls back to
    /// "after the last row in the filtered set" when the computed key collides. Every one of
    /// those is observable from AL, and a hand-rolled version gets the easy case right while
    /// silently answering the rest differently.
    ///
    /// A page with no AutoSplitKey is a no-op inside BC's own guard, so this is safe to call
    /// unconditionally — the runner does not need to duplicate the property lookup.
    /// </summary>
    internal void SplitKey()
    {
        var splitKey = FindNavFormMethod("SplitKey", Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                "NavForm.SplitKey not found on " + _form.GetType().FullName + " — BC page shape changed");
        try { splitKey.Invoke(_form, Array.Empty<object>()); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // BC throws NavNCLNotSupportedTypeException here for a page whose last primary-key
            // field is not a splittable type. That is the page's own error and belongs to the
            // AL test unwrapped, exactly like an Error() raised in a trigger.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    /// <summary>
    /// Whether the page declares <c>AutoSplitKey</c> — BC's own <c>NavForm.NeedAutoSplitKey</c>,
    /// off the same metadata it reads (that property is private; <c>MasterPage</c> is public).
    ///
    /// SplitKey guards on this itself, so callers do not need it to decide whether to CALL
    /// SplitKey. It exists so the client-side work that feeds SplitKey — see
    /// <see cref="SetAutoKeyValue"/> — is skipped entirely for the pages where it would be
    /// thrown away, which is most of them.
    /// </summary>
    internal bool NeedsAutoSplitKey
        => _form is NavForm form
           && form.MasterPage?.PageProperties?.SourceObject?.AutoSplitKey == true;

    /// <summary>
    /// Whether the page declares <c>DelayedInsert</c> — read off the same
    /// <c>MasterPage.PageProperties.SourceObject</c> metadata as
    /// <see cref="NeedsAutoSplitKey"/>. The property's whole meaning is WHEN the row is
    /// written: <c>true</c> holds the insert back until the user leaves the line, which is the
    /// runner's existing flush-on-leave behaviour; <c>false</c> lets the platform write the row
    /// as soon as it can (issue #3441 — see MockTestPage.InsertOnCompletePrimaryKey).
    /// </summary>
    internal bool DelaysInsertUntilTheRowIsLeft
        => _form is NavForm form
           && form.MasterPage?.PageProperties?.SourceObject?.DelayedInsert == true;

    /// <summary>
    /// Whether the page writes ROWS — a repeater the cursor moves through — rather than one
    /// record it saves when it is left. Read off <c>MasterPage.PageProperties.PageType</c>, the
    /// same property <c>NavTestExecution.FindPageType</c> reads.
    ///
    /// Both directions are measured on real BC and adjudicated upstream. A List inserts the row
    /// as soon as its key is complete (corpus codeunit 60636
    /// <c>NewAndInsertRecordEvents_PageDrivenInsert_FireForTheKeyOnly</c>); a Card does not —
    /// its row does not exist until the page is closed, which corpus codeunit 60844
    /// <c>Close_WithoutOK_StillPersistsTheNewRow</c> asserts by name ("this assertion catches a
    /// test environment where the record was already inserted eagerly on SetValue"). ListPart
    /// and Worksheet are the other two repeater page types and ride the same client mechanism;
    /// no corpus test distinguishes them from List, and none contradicts them either. See
    /// MockTestPage.InsertOnCompletePrimaryKey.
    /// </summary>
    internal bool WritesRowsAsTheyAreCompleted
        => _form is NavForm form
           && form.MasterPage?.PageProperties?.PageType is
               Microsoft.Dynamics.Nav.Types.Metadata.PageType.List
               or Microsoft.Dynamics.Nav.Types.Metadata.PageType.ListPart
               or Microsoft.Dynamics.Nav.Types.Metadata.PageType.Worksheet;

    /// <summary>
    /// Hand BC's <c>NavForm.SplitKey()</c> the key the CLIENT proposes for the row about to be
    /// inserted — <c>NavForm.AutoKeyValue</c>, the first thing SplitKey consults.
    ///
    /// This is a real channel in BC's own design, not a back door. On a service tier the client
    /// computes the new row's key itself (<c>AutoKeyGenerator.GenerateKey</c> in
    /// Microsoft.Dynamics.Nav.Client.UI) and ships it in <c>NavRecordState.AutoKeyValues</c>;
    /// <c>NSDataSetState.ApplyToRecordWithoutPositioning</c> lands it on
    /// <c>NavForm.AutoKeyValue</c>, and SplitKey then VALIDATES it — takes it only if no row
    /// already holds it, and otherwise falls back to its own bound arithmetic. The runner
    /// replaces that client, so without this the field is always null and SplitKey computes
    /// from bounds nobody populated.
    ///
    /// Pass null to clear it: a stale value from a previous insert would be offered for the
    /// next row, and SplitKey has no way to tell it apart from a fresh proposal.
    /// </summary>
    internal void SetAutoKeyValue(object? value)
    {
        if (_form is NavForm form) form.AutoKeyValue = value;
    }

    private MethodInfo? FindNavFormMethod(string name, Type[] parameterTypes)
    {
        for (var t = _form.GetType(); t != null; t = t.BaseType)
        {
            var mi = t.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null, types: parameterTypes, modifiers: null);
            if (mi != null) return mi;
        }
        return null;
    }

    /// <summary>
    /// Start a new row the way the page itself would: BC's own NavForm.NewRecord, which does
    /// ALInit, then InitializeFieldsFromFilters (so the row arrives already carrying the page's
    /// filters — the header a subpage's line belongs to), then raises OnNewRecord.
    ///
    /// Reused rather than reimplemented on purpose. The filter step in particular depends on the
    /// page's own metadata (SourceObject.PopulateAllFields) and on which FILTER GROUPS count —
    /// a page's programmatic FilterGroup(2) scope is not the user's filter pane — and BC already
    /// knows both. Hand-rolling it meant guessing at that, and guessing wrong is invisible: the
    /// row simply arrives with blank keys.
    /// </summary>
    /// <returns>false when there is no form to ask, leaving the caller its own fallback.</returns>
    internal bool TryNewRecord(bool belowXRec)
    {
        if (_form == null) return false;
        var newRecord = _form.GetType().GetMethod("NewRecord",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(bool) }, modifiers: null);
        if (newRecord == null) return false;

        try { newRecord.Invoke(_form, new object[] { belowXRec }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // OnNewRecord runs inside this call; an Error() it raises is the test's result.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
        return true;
    }

    /// <summary>
    /// The page's last word before an EDIT to an existing row is written — the counterpart of
    /// OnInsertRecord, and a veto in exactly the same way. A page that stamps a "last modified
    /// by" field or refuses to save a row in a closed period does it here.
    /// </summary>
    internal bool RaiseOnModifyRecord()
        => InvokeRecordTrigger("OnModifyRecord", Type.EmptyTypes, Array.Empty<object>()) is not false;

    /// <summary>
    /// Invoke a page record trigger by name. The AL compiler emits these as overrides of
    /// NavForm's own protected virtuals, so reflection finds the base declaration and virtual
    /// dispatch reaches the page's override; a page that declares none lands on NavForm's
    /// implementation, which is the correct no-op/true.
    /// </summary>
    /// <summary>
    /// Invoke one of the page's own record triggers (OnOpenPage, OnQueryClosePage, OnNewRecord,
    /// OnInsertRecord, …) and answer with what it produced.
    ///
    /// Routed through <see cref="AwaitTriggerResult"/> for the same reason the control-trigger
    /// path is (#2359), and the consequences here are worse, because two of these triggers'
    /// RETURN VALUES are load-bearing:
    ///
    /// <list type="bullet">
    /// <item><c>RaiseOnClosePage</c> tests <c>is false</c> to honour OnQueryClosePage's veto,
    /// and <c>RaiseOnInsertRecord</c> tests <c>is not false</c> to honour OnInsertRecord's.
    /// Against a raw <c>ValueTask&lt;bool&gt;</c> neither pattern can ever match, so a page that
    /// refused to close, or refused an insert, was closed and inserted anyway.</item>
    /// <item>An <c>Error()</c> raised in OnOpenPage or OnQueryClosePage was parked on the
    /// discarded awaitable and never reached the AL that called <c>OpenNew()</c> /
    /// <c>Close()</c> — the same silent success the control-trigger half produced.</item>
    /// </list>
    ///
    /// <para>Dispatched through BC's OWN <c>NavForm.RaiseOn{trigger}Async</c> rather than by
    /// resolving the trigger's name on the page type, because resolving the name is not the
    /// same question as finding the body. BC's compiler emits a page trigger in one of two
    /// flavours — a synchronous <c>OnOpenPage()</c> override, or an asynchronous
    /// <c>OnOpenPageAsync()</c> with <c>NavApplicationObjectBase.__IsAsync</c> overridden to
    /// <c>true</c>. Both are virtuals on <c>NavForm</c> with EMPTY base bodies, so
    /// <c>GetMethod("OnOpenPage")</c> ALWAYS succeeds and virtual dispatch always runs
    /// something; on a page that emitted the async flavour, the something it runs is NavForm's
    /// empty base method. Nothing throws and nothing is skipped — the trigger simply does
    /// nothing, which is why this was invisible for as long as it was.</para>
    ///
    /// <para>The runner's own AL emit produces the SYNC flavour, so every runner-authored page
    /// worked and every page from a precompiled dependency (Base Application, System
    /// Application, any ISV .app) ran with dead lifecycle triggers. Measured on Base
    /// Application page 981 "Payment Registration": its OnOpenPage calls
    /// <c>PaymentRegistrationMgt.RunSetup()</c>, which opens the modal setup page that creates
    /// the current user's setup row. None of it ran — TestPage.OpenEdit() returned cleanly
    /// having done nothing, and Tests-ERM codeunit 134710 then lost 47 tests to a row that was
    /// never created (issue #2729).</para>
    ///
    /// <para>This is the page twin of the report defect fixed in #2732/#2734, where
    /// <c>NavReportSync.SyncRun</c> invoked only the sync virtual and every Base Application
    /// report ran with empty report-level triggers. The fix takes the same shape and for the
    /// same reason: BC's <c>RaiseOn{trigger}Async</c> is the method BC's own page pipeline
    /// calls, it applies the <c>__IsAsync</c> rule, and it then runs every PAGEEXTENSION's copy
    /// of the trigger and raises the trigger's integration event. Re-deriving the flavour rule
    /// here would get the first of those three right and silently drop the other two.</para>
    ///
    /// <para>The fallback to the plain virtual is kept for triggers BC exposes no
    /// <c>RaiseOn…Async</c> for, so an unrecognised trigger name still behaves as before rather
    /// than becoming a silent no-op.</para>
    /// </summary>
    private object? InvokeRecordTrigger(string name, Type[] parameterTypes, object[] arguments)
    {
        var trigger = FindNavFormMethod("Raise" + name + "Async", parameterTypes)
            ?? _form.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: parameterTypes, modifiers: null);
        if (trigger == null) return null;
        BeginTrigger();
        var completed = false;
        try
        {
            object? result;
            try { result = AwaitTriggerResult(trigger.Invoke(_form, arguments)); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // An Error() inside the trigger is the trigger's own outcome, not a runner
                // failure — rethrow it unwrapped so the AL stack survives.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            completed = true;
            return result;
        }
        finally { EndTrigger(completed); }
    }

    /// <summary>
    /// A resolved trigger method plus the OBJECT to invoke it on. Before issue #1923 every
    /// trigger lived on the base page's own <c>_form</c>, so a bare MethodInfo was enough; a
    /// pageextension's trigger lives on that extension's own compiled instance instead (see
    /// FindTrigger), so the target has to travel with the method from here on.
    /// </summary>
    private readonly struct TriggerMatch
    {
        internal readonly object Target;
        internal readonly MethodInfo Method;
        internal TriggerMatch(object target, MethodInfo method) { Target = target; Method = method; }
    }

    /// <summary>
    /// The method (and the object to invoke it on) carrying the trigger for
    /// <paramref name="memberId"/>.
    ///
    /// The AL compiler emits triggers as <c>{MemberName}_a{n}{suffix}</c> on the DECLARING
    /// object's class, carrying the NAME but not the id. The member is identified by
    /// RE-DERIVING BC's own id from each candidate's name —
    /// <c>IdSpace.GetMemberId(declaringObjectId, name)</c>, i.e. abs(FNV-1a over the UTF-16
    /// bytes of declaringObjectId + name) — and comparing it to the id being driven. Matching
    /// a control on its source expression's Name does not work: that is the bound VARIABLE's
    /// name (SelectedMode), not the control's (Mode), and they routinely differ.
    ///
    /// Issue #1923: a control/action a PAGEEXTENSION declares is compiled onto the extension's
    /// OWN type (<c>PageExtension{extId}</c>, a <c>NavFormExtension</c> subclass), not the base
    /// page's — and its id hashes from the EXTENSION's own object id, never the page's (see
    /// RecordPatches.GetPageControlFieldMap, which already documents and relies on this same
    /// id-space rule for field controls: <c>GetMemberId(64301, "NoteField")</c> is the id BC
    /// actually asks for, <c>GetMemberId(64300, "NoteField")</c> — the base page's id — never
    /// appears). So after the base page's own type comes up empty, this now also searches
    /// every pageextension that extends this page, in each one's own id space.
    /// </summary>
    private TriggerMatch? FindTrigger(int memberId, string suffix, string surface, int arity = 0)
    {
        var own = FindTriggerOnTarget(_form, _pageId, memberId, suffix, surface, arity,
            RecordPatches.TryGetPageMemberName(_pageId, memberId, isExtension: false));
        if (own != null) return own;

        foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(_pageId))
        {
            var extInstance = GetOrCreateExtensionInstance(extensionId);
            if (extInstance == null) continue;
            var extMatch = FindTriggerOnTarget(extInstance, extensionId, memberId, suffix, surface, arity,
                RecordPatches.TryGetPageMemberName(extensionId, memberId, isExtension: true));
            if (extMatch != null) return extMatch;
        }

        // #3573: last, the id space the two arms above cannot reach — a control an extension
        // MODIFIES rather than declares. See FindModifiedControlTriggers for why that needs the
        // BASE page's id space against the EXTENSION's methods. Last, not first, so an
        // extension-added control keeps the ordinary path above unchanged.
        //
        // Here rather than only on the validate path, because a `modify()` block carries more
        // than the before/after validate pair: the compiler's own TriggerTypeKind enum names
        // six ControlExtension* triggers. RaiseOnLookup and RaiseOnDrillDown resolve through
        // this method and were refusing a control that plainly declares the trigger. Two of
        // the six have no dispatch surface anywhere in the runner — #3642.
        var modified = FindModifiedControlTriggers(memberId, suffix, arity);
        return modified.Count > 0 ? modified[0] : null;
    }

    /// <summary>
    /// FindTrigger's inner scan, over ONE declaring object (the base page or one
    /// pageextension instance) and its own id space (<paramref name="declaringObjectId"/>).
    ///
    /// Issue #1968: matching used to work backwards only — un-mangle each candidate method's
    /// name and re-derive its member id. The emitted method name is LOSSY for any member whose
    /// AL name needed mangling: <c>action("Spaced Stamp")</c> emits
    /// <c>Spaced_Stamp_a45_OnAction</c>, which un-mangles to <c>Spaced_Stamp</c> and hashes to
    /// a different id than <c>Spaced Stamp</c> — so every spaced-name trigger read as
    /// "declares no trigger". When the AL source parser knows the member's TRUE declared name
    /// (<paramref name="declaredName"/>, from RecordPatches.TryGetPageMemberName), the match
    /// now runs FORWARD instead: mangle the true name exactly the way BC's C# emitter does and
    /// compare against the method-name skeleton. The backward scan remains the fallback for
    /// declaring objects the parser never saw (a precompiled dependency page's own members).
    /// </summary>
    private static TriggerMatch? FindTriggerOnTarget(
        object target, int declaringObjectId, int memberId, string suffix, string surface, int arity,
        string? declaredName = null)
    {
        var mangled = declaredName == null ? null : EmittedIdentifier(declaredName);
        MethodInfo? match = null;
        foreach (var m in target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.GetParameters().Length != arity) continue;
            if (!m.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var memberName = MemberNameFromTriggerMethod(m.Name, suffix);
            if (memberName == null) continue;
            if (mangled != null)
            {
                if (!string.Equals(memberName, mangled, StringComparison.Ordinal)) continue;
            }
            else if (MemberId(declaringObjectId, memberName) != memberId) continue;

            // A runner gap, not a BC-shape gap: the collision is in the RUNNER's own name→id
            // hash over emitted method names, not in anything BC's metadata states. The anchor
            // stays per-surface (testpage-onlookup / -onaction / …) so the four callers of this
            // one method remain distinguishable to a reader.
            if (match != null)
                throw TestPageShapeGap.TriggerAmbiguity(
                    $"TestPage {surface} (member {memberId})",
                    $"testpage-{surface.ToLowerInvariant()}",
                    $"both '{match.Name}' and '{m.Name}' on {target.GetType().Name} resolve to "
                    + $"member {memberId}; the runner cannot tell which trigger belongs to it");
            match = m;
        }
        return match == null ? null : new TriggerMatch(target, match);
    }

    /// <summary>
    /// The live instance of a pageextension's compiled <c>PageExtension{extensionId}</c>
    /// class, constructed once per RunnerPageInstance and cached — never rebuilt per trigger
    /// lookup, so an extension whose type could not be found or built stays a fast negative
    /// on every later call instead of retrying (and re-logging) the same failure.
    ///
    /// Constructed the same way <see cref="TryCreate"/> constructs the base page itself: the
    /// AL-compiler-emitted <c>(ITreeObject, NavRecord)</c> ctor (verified via IL: it just
    /// forwards to <c>NavFormExtension(ITreeObject, int extId, NavRecord, NCLStaticMetadata)</c>
    /// with the extension's own object id baked in), <b>passed this page's own <c>_form</c> as
    /// the <c>ITreeObject parent</c> argument</b> — <c>NavFormExtension</c>'s own ctor does
    /// <c>ParentObject = parent as NavForm</c> as its very first statement, and the extension's
    /// <c>get_Rec</c>/<c>get_CurrPage</c> overrides route through <c>ParentObject</c> (verified
    /// via IL), not through the record the ctor was handed. Real BC wires this by adding the
    /// extension to the page's own <c>PageExtensions</c> list during metadata load; the
    /// runner's skeleton always keeps that list empty (see NclCecilRewrite.cs's
    /// <c>get_PageExtensions</c> rewrite), so this is the runner-owned substitute for that step
    /// — the trigger DISPATCH here has always been the runner's own reflection scheme (see
    /// FindTrigger's remarks), never BC's real action-invoke machinery, so this is consistent
    /// with the existing architecture, not a new shortcut.
    ///
    /// Issue #1995: passing <c>_owner</c> (the TestPage's original caller, essentially never a
    /// NavForm) here used to leave <c>ParentObject</c> null for the ENTIRE constructor body,
    /// papered over afterward with a reflection <c>SetValue(instance, _form)</c> once
    /// <c>ctor.Invoke</c> returned. That is too late for any AL-compiler-emitted constructor
    /// code that touches <c>ParentObject</c> itself — a pageextension that adds a <c>part()</c>
    /// to the page layout emits an <c>InitializeComponent()</c> override that calls
    /// <c>ParentObject.RegisterUIPart(...)</c> from inside the ctor, which NREs on the still-null
    /// property and aborts construction entirely. <c>GetOrCreateExtensionInstance</c> then
    /// caches a null instance for that extension id, so EVERY trigger the extension declares —
    /// not just ones near the part — reads as "extension not found", which surfaces up through
    /// FindTrigger as the extension's actions "declaring no OnAction trigger". Passing <c>_form</c>
    /// as the ctor's <c>parent</c> argument sets <c>ParentObject</c> correctly from the extension's
    /// own base-class ctor, before any AL-emitted code runs.
    /// </summary>
    private object? GetOrCreateExtensionInstance(int extensionId)
    {
        if (_extensionInstances.TryGetValue(extensionId, out var cached)) return cached;

        object? instance = null;
        if (_record != null)
        {
            var extType = FindPageExtensionType(extensionId);
            var ctor = extType?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 2
                                  && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
            if (ctor != null)
            {
                try
                {
                    instance = ctor.Invoke(new object?[] { _form, _record });
                    // Defensive, not load-bearing: the ctor argument above already sets
                    // ParentObject correctly (see remarks). Kept in case some future
                    // extension ctor overload does not run NavFormExtension's own base ctor
                    // first.
                    _pFormExtensionParentObject?.SetValue(instance, _form);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    // Loud, but not fatal: FindTrigger treats a null instance exactly like "this
                    // extension declares no matching trigger", which for OnAction/OnLookup still
                    // surfaces as a refusal (never a silent no-op) once every extension has been
                    // tried. `[warn]` — see the tag note in TryCreate; tagged
                    // `[RunnerPageInstance]` this never reached the terminal.
                    Console.Out.WriteLine(
                        $"[warn] RunnerPageInstance: pageextension {extensionId} on page {_pageId}: could not "
                        + $"construct the AL page extension object ({inner.GetType().Name}: "
                        + $"{inner.Message}); its triggers stay unreachable");
                }
            }
        }

        _extensionInstances[extensionId] = instance;
        return instance;
    }

    private static Type? FindPageExtensionType(int extensionId)
    {
        var name = "PageExtension" + extensionId;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, typeof(Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension).IsAssignableFrom);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Dispatch <paramref name="actionId"/> against a pageextension's own OnAction trigger
    /// with NO live RunnerPageInstance for the base page to route through — issue #1923's
    /// most dangerous arm: a pageextension over a page that ships PRECOMPILED (e.g. Base App
    /// "Item Attributes") extends a page with no compiled <c>Page{id}</c> .NET type and no
    /// emit-captured metadata XML, so <see cref="TryCreate"/> returns null (see its SCOPE
    /// remarks) and the caller (MockTestPage.cs's LiveNavTestPage) had nowhere to route
    /// Invoke() except a permanently no-op MockITestAction — a real, dispatchable action
    /// silently doing nothing.
    ///
    /// Returns false when no compiled pageextension owns <paramref name="actionId"/> — an id
    /// that genuinely belongs to the (unbuildable) precompiled base page itself, which the
    /// caller is expected to keep treating exactly as it did before this method existed
    /// (that half of the gap is pre-existing and out of #1923's scope: dispatching an action
    /// on a page the runner never compiled needs a control tree the runner has no way to
    /// build at all, unlike a pageextension's own trigger, which needs nothing from the base
    /// page besides its record).
    /// </summary>
    internal static bool TryRaiseExtensionOnlyAction(object owner, NavRecord record, int pageId, int actionId)
    {
        foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(pageId))
        {
            var extType = FindPageExtensionType(extensionId);
            var ctor = extType?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 2
                                  && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
            if (ctor == null) continue;

            object instance;
            try { instance = ctor.Invoke(new object?[] { owner, record }); }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                // `[warn]` — see the tag note in TryCreate.
                Console.Out.WriteLine(
                    $"[warn] RunnerPageInstance: pageextension {extensionId} on page {pageId} (no live base page "
                    + $"object): could not construct the AL page extension object "
                    + $"({inner.GetType().Name}: {inner.Message}); its triggers stay unreachable");
                continue;
            }

            // No base NavForm exists to set ParentObject to (that is exactly why we are on
            // this path) — an OnAction trigger that only touches its own locals still runs
            // faithfully; one that reads Rec/CurrPage NREs, which surfaces as a genuine
            // runner-internal error rather than a silently wrong answer.
            var match = FindTriggerOnTarget(instance, extensionId, actionId, "_OnAction", "OnAction", arity: 0,
                RecordPatches.TryGetPageMemberName(extensionId, actionId, isExtension: true))
                // #2113's sibling on this path. This method is the fallback for a base page the
                // runner could build NO instance for at all (no compiled type anywhere), and it
                // made exactly the same assumption RaiseOnAction did: an `actionref` this
                // extension contributes carries no trigger of its own, so the id-based scan above
                // can never match it. The scan is retried against the TARGET action's name, in
                // this extension's own id space. Only same-extension targets are reachable here by
                // construction — a ref pointing at an action of the unbuildable base page has no
                // base page object to dispatch against, which is the pre-existing gap this method
                // already documents above. NOTE: unlike the RaiseOnAction path this branch is not
                // covered by an end-to-end arm in tests/runner-extras/testpage-promoted-actionref
                // — a precompiled Base App base page (Item Attributes) still resolves a live
                // RunnerPageInstance and therefore goes through RaiseOnAction. It is fixed here
                // because leaving one of two paths with the blind spot is a defect whether or not
                // anything currently reaches it, and its failure mode is the worse one: this
                // method returns false and the caller then does nothing at all, silently.
                ?? TryFindActionRefTargetTriggerOnExtension(instance, extensionId, actionId);
            if (match == null) continue;

            // AwaitTriggerResult for the same reason Invoke uses it: this OnAction is emitted
            // async too, so dropping the awaitable swallowed any Error() the action raised.
            try { AwaitTriggerResult(match.Value.Method.Invoke(match.Value.Target, null)); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The OnAction trigger of the action an <c>actionref</c> declared by pageextension
    /// <paramref name="extensionId"/> points at, when that target action is declared by the
    /// same extension. Null when <paramref name="actionId"/> is not one of that extension's
    /// actionrefs, or its target declares no trigger there. See #2113 and
    /// <see cref="FindTriggerThroughActionRef"/>, which is the same resolution on the normal
    /// (live base page) path.
    /// </summary>
    private static TriggerMatch? TryFindActionRefTargetTriggerOnExtension(
        object instance, int extensionId, int actionId)
    {
        var target = RecordPatches.TryGetActionRefTarget(extensionId, actionId, isExtension: true);
        if (target == null) return null;
        return FindTriggerOnTarget(instance, extensionId, MemberId(extensionId, target),
            "_OnAction", "OnAction", arity: 0, target);
    }

    private void Invoke(TriggerMatch trigger)
    {
        BeginTrigger();
        var completed = false;
        try
        {
            try { AwaitTriggerResult(trigger.Method.Invoke(trigger.Target, null)); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // An Error() inside the AL trigger is the trigger's own outcome, not a runner
                // failure — rethrow it unwrapped so the AL stack survives.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            completed = true;
        }
        finally { EndTrigger(completed); }
    }

    /// <summary>
    /// Block on the Task / ValueTask an AL page trigger returned, and answer with the value it
    /// produced (null for a void trigger).
    ///
    /// BC's AL compiler emits EVERY page trigger as an async method — measured on the Base App's
    /// precompiled page 9170 "Profile Card": <c>ProfileIdField_a45_OnValidate</c> returns
    /// <c>ValueTask</c> and <c>RoleCenterIdField_a45_OnLookup</c> returns
    /// <c>ValueTask&lt;bool&gt;</c>. <c>MethodInfo.Invoke</c> therefore hands back the awaitable,
    /// not the outcome, and an <c>Error()</c> the trigger raised is parked on it as a faulted
    /// state rather than thrown. Every call site here used to drop that value on the floor, so:
    ///
    /// <list type="bullet">
    /// <item>a page control's OnValidate could raise, and TestPage SetValue reported success —
    /// the AL Error() was measurably present (<c>status=Faulted</c>, e.g.
    /// "You cannot disable the profile that is used as default.") and simply discarded, which is
    /// the silent-out-of-scope failure loud-failures.md forbids; and</item>
    /// <item>OnLookup's <c>result is true</c> pattern-matched a <c>ValueTask&lt;bool&gt;</c>
    /// against a <c>bool</c>, which is never true, so every lookup on such a page reported
    /// "the user cancelled" no matter what the trigger returned.</item>
    /// </list>
    ///
    /// Blocking is the correct shape here for the same reason it already is in
    /// <c>CodeunitPatches.AwaitIfTask</c> and <c>CodeunitEventDispatcher.ObserveAsyncResult</c>
    /// (the two sibling AL dispatchers that already do this): the runner drives AL synchronously,
    /// so these are complete or complete inline, and <c>GetAwaiter().GetResult()</c> rethrows the
    /// ORIGINAL exception rather than an AggregateException — which is what the AL caller,
    /// including <c>asserterror</c>, has to observe.
    ///
    /// A trigger that returned an ordinary (non-awaitable) value is passed through unchanged, so
    /// a future non-async emit shape keeps working.
    /// </summary>
    internal static object? AwaitTriggerResult(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case System.Threading.Tasks.Task task:
                task.GetAwaiter().GetResult();
                return TaskResultOrNull(task);
            case System.Threading.Tasks.ValueTask valueTask:
                valueTask.GetAwaiter().GetResult();
                return null;
        }

        var type = result.GetType();
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>))
        {
            // ValueTask<T> has no non-generic surface to await through, so go via AsTask() —
            // the same route CodeunitEventDispatcher.ObserveAsyncResult takes.
            var asTask = type.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"ValueTask<T>.AsTask not found while awaiting an AL page trigger ({type.FullName}) "
                    + "— BC/runtime shape changed.");
            var task = (System.Threading.Tasks.Task)asTask.Invoke(result, null)!;
            task.GetAwaiter().GetResult();
            return TaskResultOrNull(task);
        }

        // Not awaitable — an ordinary return value.
        return result;
    }

    /// <summary>
    /// <c>Task&lt;T&gt;.Result</c> for a completed generic task; null for a task that carries no
    /// value.
    ///
    /// "Carries no value" is not the same as "is not generic": an <c>async Task</c> method
    /// materialises at runtime as <c>Task&lt;VoidTaskResult&gt;</c>, where <c>VoidTaskResult</c>
    /// is the BCL's internal placeholder struct. Reading <c>.Result</c> off that hands back a
    /// boxed placeholder — a non-null object standing in for a void trigger — which would then
    /// flow out of AwaitTriggerResult as if the trigger had returned something. Filter it out by
    /// name; the type is internal to System.Private.CoreLib so there is nothing to compare
    /// against.
    /// </summary>
    private static object? TaskResultOrNull(System.Threading.Tasks.Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null;
        var resultType = type.GetGenericArguments()[0];
        if (resultType.FullName == "System.Threading.Tasks.VoidTaskResult") return null;
        return type.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
    }

    /// <summary>
    /// The identifier BC's C# emitter gives an AL member name — the FORWARD half of the
    /// trigger-method naming scheme. Ported from BC's own emitter, decompiled from
    /// <c>Microsoft.Dynamics.Nav.CodeAnalysis.dll</c> (28.1),
    /// <c>Utilities.StringExtensions.MangleIdentifierName</c> →
    /// <c>MangleUnquotedIdentifierName</c> + <c>GetSafeCSharpIdentifierName</c>:
    /// <list type="bullet">
    /// <item>a space (or a <c>"</c>) becomes <c>_</c> — <c>"Spaced Stamp"</c> →
    /// <c>Spaced_Stamp</c>, each space separately (<c>"A  C"</c> → <c>A__C</c>);</item>
    /// <item>a C# identifier-part character passes through (Unicode letters included:
    /// <c>"Ærø Løb"</c> → <c>Ærø_Løb</c>), with a <c>_</c> inserted before a FIRST character
    /// that cannot start an identifier (<c>"2Start"</c> → <c>_2Start</c>);</item>
    /// <item>any other character becomes <c>a</c> + its decimal code point (<c>-</c>→<c>a45</c>,
    /// <c>.</c>→<c>a46</c>, <c>&amp;</c>→<c>a38</c>, <c>%</c>→<c>a37</c>, <c>/</c>→<c>a47</c>);</item>
    /// <item>finally, a result whose upper-invariant form is a C# RESERVED keyword — Roslyn's
    /// <c>SyntaxFacts.GetReservedKeywordKinds()</c>, the very list BC's emitter consults — or
    /// <c>FINALIZE</c> (BC's one extra entry, <c>OtherReservedWords</c>) gets a <c>_</c>
    /// prefix: <c>New</c> → <c>_New</c>, <c>Delegate</c> → <c>_Delegate</c>. Measured over the
    /// whole Base Application 28.1 DLL: of 4,911 members with an emitted trigger, exactly
    /// seven distinct names carry the prefix — <c>Default, Delegate, Event, Finalize, Internal,
    /// New, Override</c> — and <c>Delete</c>, <c>Record</c>, <c>Setup</c> do not, which is what
    /// rules out "any keyword-looking word" (issue #2723's 16 <c>_</c>-prefix failures).</item>
    /// </list>
    /// This is deliberately NOT invertible — that irreversibility is exactly why
    /// FindTriggerOnTarget mangles forward instead of un-mangling (#1968).
    /// </summary>
    internal static string EmittedIdentifier(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == ' ' || c == '"') sb.Append('_');
            else if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsIdentifierPartCharacter(c))
            {
                if (i == 0 && !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsIdentifierStartCharacter(c)) sb.Append('_');
                sb.Append(c);
            }
            else sb.Append('a').Append(((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var mangled = sb.ToString();
        return EmitterReservedIdentifiers.Contains(mangled.ToUpperInvariant()) ? "_" + mangled : mangled;
    }

    /// <summary>
    /// BC's <c>StringExtensions.ReservedKeywords</c>, built the same way BC builds it: every
    /// Roslyn reserved-keyword text, upper-invariant, plus <c>FINALIZE</c>. Contextual keywords
    /// (<c>record</c>, <c>var</c>, <c>async</c>, …) are NOT in GetReservedKeywordKinds and are
    /// therefore not prefixed — matching the measured <c>Record</c> → <c>Record_a45_OnAction</c>.
    /// </summary>
    private static readonly HashSet<string> EmitterReservedIdentifiers = new(
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetReservedKeywordKinds()
            .Select(k => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetText(k).ToUpperInvariant())
            .Append("FINALIZE"),
        StringComparer.Ordinal);

    /// <summary>
    /// "Mode_a45_OnValidate" -> "Mode". Returns null when the name does not carry the
    /// compiler's <c>_a{digits}_</c> disambiguator, rather than guessing at a split point.
    /// </summary>
    private static string? MemberNameFromTriggerMethod(string methodName, string suffix)
    {
        var head = methodName.Substring(0, methodName.Length - suffix.Length);
        var lastUnderscore = head.LastIndexOf('_');
        if (lastUnderscore <= 0) return null;
        var disambiguator = head.Substring(lastUnderscore + 1);
        if (disambiguator.Length < 2 || disambiguator[0] != 'a') return null;
        for (var i = 1; i < disambiguator.Length; i++)
            if (!char.IsDigit(disambiguator[i])) return null;
        return head.Substring(0, lastUnderscore);
    }

    /// <summary>
    /// BC's IdSpace.GetMemberId(ancestorObjectId, name): abs of the FNV-1a hash of
    /// <c>ancestorObjectId.ToString(InvariantCulture) + name</c> over its UTF-16 BYTES
    /// (not its chars — hashing chars gives different, wrong ids), with int.MinValue
    /// mapped to int.MaxValue because it has no positive counterpart.
    /// </summary>
    internal static int MemberId(int ancestorObjectId, string name)
    {
        var text = ancestorObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + name;
        var bytes = System.Text.Encoding.Unicode.GetBytes(text);
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var b in bytes) hash = (hash ^ b) * 16777619u;
            var signed = (int)hash;
            return signed == int.MinValue ? int.MaxValue : Math.Abs(signed);
        }
    }

    private static Type? FindPageType(int pageId)
    {
        var name = "Page" + pageId;
        // Metadata-backed lookup — see AlRunner/Infrastructure/AssemblyTypeIndex.cs.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, typeof(NavForm).IsAssignableFrom);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static void Invoke(object form, string methodName, object?[] args)
    {
        for (var t = form.GetType(); t != null; t = t.BaseType)
        {
            var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            if (mi == null) continue;
            mi.Invoke(form, args);
            return;
        }
        throw new InvalidOperationException(
            $"NavForm.{methodName} not found on {form.GetType().FullName} — BC page shape changed");
    }

    private static object? ReadProperty(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (pi != null) return pi.GetValue(target);
        }
        return null;
    }
}
