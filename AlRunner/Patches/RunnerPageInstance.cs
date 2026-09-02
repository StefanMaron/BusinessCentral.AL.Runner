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

namespace AlRunner.Patches;

internal sealed class RunnerPageInstance
{
    private readonly object _form;
    private readonly object _owner;
    private readonly NavRecord? _record;
    private readonly int _pageId;
    private readonly System.Collections.IDictionary _sourceExpressions;

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
    }

    internal object Form => _form;

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
            // stdout on purpose throughout this class: the test-execution child's stderr is
            // not captured, so a Console.Error line would be invisible exactly when needed.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: no emit-captured metadata, so no control tree; "
                    + "TestPage stays record-only");
            return null;
        }

        var pageType = FindPageType(pageId);
        if (pageType == null)
        {
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: no compiled Page{pageId} type found");
            return null;
        }

        var ctor = pageType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2
                              && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
        if (ctor == null)
        {
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: Page{pageId} has no (ITreeObject, NavRecord) ctor");
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
                    $"[RunnerPageInstance] page {pageId}: the page object initialised but published no "
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
            // stdout on purpose: the test-execution child's stderr is not captured, so a
            // Console.Error line here would be invisible exactly when it is needed.
            Console.Out.WriteLine(
                $"[RunnerPageInstance] page {pageId}: could not build the AL page object "
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
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: no compiled Page{pageId} type found");
            return null;
        }

        var ctor = pageType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                              && typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                                     .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (ctor == null)
        {
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: Page{pageId} has no (ITreeObject) ctor");
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
                    $"[RunnerPageInstance] page {pageId}: the record-less page object initialised but "
                    + "published no source-expression table");
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
            // stdout on purpose — see TryCreate's identical reasoning above.
            Console.Out.WriteLine(
                $"[RunnerPageInstance] page {pageId}: could not build the record-less AL page object "
                + $"({inner.GetType().Name}: {inner.Message})");
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(inner.StackTrace);
            return null;
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
    /// </summary>
    internal object? TryGetSourceExpression(int controlId)
        => _sourceExpressions[SourceExpressionKey(controlId)];

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

    /// <summary>Editable for a data-bound control, combined with the page's own state.</summary>
    internal bool ControlEditable(int controlId)
        => PageEditable && EvaluateProperty(ControlDefinition(controlId)?.Editable, "Editable", controlId);

    internal bool ControlEnabled(int controlId)
        => EvaluateProperty(ControlDefinition(controlId)?.Enabled, "Enabled", controlId);

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
        if (!EvaluateProperty(ControlDefinition(controlId)?.Visible, "Visible", controlId))
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

            if (!EvaluateProperty(group.Visible, "Visible", group.ID))
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

        if (IsLiteralFalse(ControlDefinition(controlId)?.Visible)) return true;

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
    /// True only for the compile-time literal spelling ("false"/"0", case-insensitive on the
    /// word form) — the same literal recognition <see cref="EvaluateProperty"/> uses, minus the
    /// expression-name fallback, because an expression must never be treated as eliminating.
    /// Internal (not private) so <c>AlRunner.Tests</c> can pin this literal-vs-expression
    /// distinction directly, without needing a live NavForm/MetadataHelper to exercise it.
    /// </summary>
    internal static bool IsLiteralFalse(string? raw)
        => raw != null && (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0");

    internal bool ActionEnabled(int actionId)
        => EvaluateProperty(ActionDefinition(actionId)?.Enabled, "Enabled", actionId);

    internal bool ActionVisible(int actionId)
        => EvaluateProperty(ActionDefinition(actionId)?.Visible, "Visible", actionId);

    private Microsoft.Dynamics.Nav.Types.Metadata.ControlDefinition? ControlDefinition(int controlId)
        => _form is NavForm form && form.MetadataHelper.TryGetControlDefinitionById(controlId, out var d) ? d : null;

    private Microsoft.Dynamics.Nav.Types.Metadata.ActionCommonPropsDefinition? ActionDefinition(int actionId)
        => _form is NavForm form && form.MetadataHelper.TryGetCommonActionDefinitionById(actionId, out var d) ? d : null;

    /// <summary>
    /// Resolve one of the boolean control properties. Absent means the AL declared none, and
    /// the AL default for all three is true.
    /// </summary>
    private bool EvaluateProperty(string? raw, string propertyName, int elementId)
    {
        if (string.IsNullOrEmpty(raw)) return true;

        // The literal arrives in more than one spelling: the emitted XML carries "true" /
        // "false" on controls and actions and "1" / "0" in the page's Properties block, and
        // BC's own metadata merge normalises an ABSENT property to "True" / "False". An
        // AL identifier cannot collide with these, so case-insensitive matching is safe.
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;

        var expression = _sourceExpressions[raw];
        if (expression == null)
            // Loudly, not true-by-default: this property IS the page's read-only contract,
            // and answering "editable" for one we could not evaluate makes every test of
            // that contract unfailable. Naming the expression is what makes the gap fixable.
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage page {_pageId} element {elementId} — {propertyName}",
                $"testpage-control-property — the property is bound to expression '{raw}', which "
                + "the page publishes no binding for, so its value cannot be evaluated. "
                + "See docs/scope.md");

        var value = GetValue(expression);
        return value?.ClientObject is bool b
            ? b
            : throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage page {_pageId} element {elementId} — {propertyName}",
                $"testpage-control-property — expression '{raw}' evaluated to "
                + $"'{value?.ClientObject ?? "null"}', which is not a Boolean. See docs/scope.md");
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

    /// <summary>BC's key convention for a control's source expression.</summary>
    internal static string SourceExpressionKey(int controlId) => "Control" + controlId;

    internal static NavValue? GetValue(object expression)
        => (NavValue?)expression.GetType()
            .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null)!
            .Invoke(expression, null);

    internal static void SetValue(object expression, NavValue value)
        => expression.GetType()
            .GetMethod("Set", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(NavValue) }, modifiers: null)!
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
        var trigger = FindTrigger(controlId, "_OnValidate", "OnValidate");
        // A control with no OnValidate simply has no such method, which is not an error.
        if (trigger != null) Invoke(trigger.Value);
    }

    /// <summary>
    /// Run the action's OnAction trigger.
    ///
    /// Unlike OnValidate, a missing trigger is NOT benign here: the AL test asked for the
    /// action to happen. An action with no OnAction is one whose effect is RunObject (open
    /// another page/report), which the runner cannot perform, so it refuses by name rather
    /// than doing nothing — silently doing nothing is what made an unrun action surface one
    /// step later as an assertion about its missing effect.
    ///
    /// Issue #1923: an action a PAGEEXTENSION contributes is compiled onto the extension's
    /// OWN type (<c>PageExtension{extId}</c>), not the base page's, and its member id hashes
    /// from the extension's OWN object id — never the page's. FindTrigger now also searches
    /// every pageextension that extends this page (own-bundle-source-compiled or a real
    /// PRECOMPILED dependency page, e.g. Base App "Item Attributes") before giving up. Before
    /// this fix a source-compiled base page's extension action was misclassified as this very
    /// RunnerOutOfScopeException (a real, dispatchable action reported as unsupported
    /// RunObject); a precompiled base page's extension action reached nowhere to throw
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
        if (trigger == null)
        {
            // Naming the actionref's TARGET matters: without it the message blames the
            // actionref for "declaring no trigger", which is true of every actionref by
            // construction and tells the reader nothing about why nothing ran.
            var target = TryResolveActionRef(actionId);
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                (target == null
                    ? "testpage-action — the page declares no OnAction trigger for this action. "
                    : $"testpage-action — this action is an actionref delegating to '{target.Value.TargetName}', "
                      + "and neither the page nor any pageextension of it declares an OnAction "
                      + "trigger for that action. ")
                + "An action whose effect is RunObject (opening another page or report) cannot be "
                + "performed here. See docs/scope.md");
        }
        Invoke(trigger.Value);
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
    internal NavText? RaiseOnLookup(int controlId, NavText current)
    {
        var trigger = FindTrigger(controlId, "_OnLookup", "OnLookup", arity: 1)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "testpage-lookup — the control declares no OnLookup trigger, so its lookup comes "
                + "from a TableRelation and would open the related table's list page, which the "
                + "runner cannot stand up. See docs/scope.md");

        var value = current;
        var byRef = new ByRef<NavText>(() => value, v => value = v);

        object? result;
        // AwaitTriggerResult, not a bare Invoke: BC emits `trigger OnLookup(...): Boolean` as
        // `ValueTask<bool>`, so the raw Invoke result never pattern-matches `is true` and every
        // lookup read as "the user cancelled" — see AwaitTriggerResult's remarks.
        try { result = AwaitTriggerResult(trigger.Method.Invoke(trigger.Target, new object?[] { byRef })); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        return result is true ? value : null;
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
        => InvokeRecordTrigger("OnOpenPage", Type.EmptyTypes, Array.Empty<object>());

    /// <summary>
    /// Run the page's OnQueryClosePage / OnClosePage triggers, in BC's order. OnQueryClosePage
    /// returning false vetoes the close, which is how a page refuses to be dismissed with
    /// unsaved work; NavForm's base returns true, so a page declaring none closes normally.
    /// </summary>
    internal bool RaiseOnClosePage(Microsoft.Dynamics.Nav.Types.FormResult closeAction)
    {
        if (InvokeRecordTrigger("OnQueryClosePage",
                new[] { typeof(Microsoft.Dynamics.Nav.Types.FormResult) },
                new object[] { closeAction }) is false)
            return false;
        InvokeRecordTrigger("OnClosePage", Type.EmptyTypes, Array.Empty<object>());
        return true;
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
    /// </summary>
    private object? InvokeRecordTrigger(string name, Type[] parameterTypes, object[] arguments)
    {
        var trigger = _form.GetType().GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: parameterTypes, modifiers: null);
        if (trigger == null) return null;
        try { return AwaitTriggerResult(trigger.Invoke(_form, arguments)); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // An Error() inside the trigger is the trigger's own outcome, not a runner
            // failure — rethrow it unwrapped so the AL stack survives.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
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
        return null;
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

            if (match != null)
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage {surface} (member {memberId})",
                    $"testpage-{surface.ToLowerInvariant()} — both '{match.Name}' and '{m.Name}' on "
                    + $"{target.GetType().Name} resolve to member {memberId}; the runner cannot "
                    + "tell which trigger belongs to it. See docs/scope.md");
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
                    // tried. stdout on purpose — see TryCreate's identical reasoning above.
                    Console.Out.WriteLine(
                        $"[RunnerPageInstance] pageextension {extensionId} on page {_pageId}: could not "
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
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] pageextension {extensionId} on page {pageId} (no live base page "
                    + $"object): could not construct the AL page extension object "
                    + $"({inner.GetType().Name}: {inner.Message})");
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
        try { AwaitTriggerResult(trigger.Method.Invoke(trigger.Target, null)); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // An Error() inside the AL trigger is the trigger's own outcome, not a runner
            // failure — rethrow it unwrapped so the AL stack survives.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
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
    /// trigger-method naming scheme, empirically pinned against BC's own emit (probe page with
    /// one action per character class, decompiled): a space becomes <c>_</c>
    /// (<c>"Spaced Stamp"</c> → <c>Spaced_Stamp</c>, each space separately: <c>"A  C"</c> →
    /// <c>A__C</c>); letters (Unicode included: <c>"Ærø Løb"</c> → <c>Ærø_Løb</c>), digits and
    /// <c>_</c> pass through; any other character becomes <c>a</c> + its decimal code point
    /// (<c>-</c>→<c>a45</c>, <c>.</c>→<c>a46</c>, <c>&amp;</c>→<c>a38</c>, <c>%</c>→<c>a37</c>,
    /// <c>/</c>→<c>a47</c>); a leading digit gets a <c>_</c> prefix (<c>"2Start"</c> →
    /// <c>_2Start</c>). This is deliberately NOT invertible — that irreversibility is exactly
    /// why FindTriggerOnTarget mangles forward instead of un-mangling (#1968).
    /// </summary>
    internal static string EmittedIdentifier(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (c == ' ') sb.Append('_');
            else sb.Append('a').Append(((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

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
