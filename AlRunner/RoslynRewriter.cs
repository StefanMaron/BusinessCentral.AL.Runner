using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

// No namespace - must be accessible from Program.cs top-level statements

/// <summary>
/// Roslyn CSharpSyntaxRewriter that transforms BC-generated C# into standalone code.
/// Replaces the fragile regex-based CSharpRewriter (now RegexRewriter) with a proper
/// syntax-tree-based approach.
/// </summary>
public class RoslynRewriter : CSharpSyntaxRewriter
{
    // Track the current top-level class name during visitation so that
    // VisitPropertyDeclaration can extract the table ID for Rec/xRec properties.
    private string? _currentClassName;

    private static readonly HashSet<string> BcAttributeNames = new(StringComparer.Ordinal)
    {
        "NavCodeunitOptions",
        "NavFunctionVisibility",
        "NavCaption",
        "NavName",
        // "NavTest" — kept for test method discovery by the executor
        // "SourceSpans" — kept for coverage line mapping
        "SignatureSpan",
        "ReturnValue",
        "NavObjectId",
        "NavByReferenceAttribute",
    };

    private static readonly HashSet<string> RemoveUsings = new(StringComparer.Ordinal)
    {
        "Microsoft.Dynamics.Nav.Runtime.Extensions",
        "Microsoft.Dynamics.Nav.Runtime.Report",
        "Microsoft.Dynamics.Nav.EventSubscription",
        "Microsoft.Dynamics.Nav.Common.Language",
    };

    /// <summary>
    /// Methods that take ITreeObject as first arg which we strip (e.g., value.ALByValue(this))
    /// </summary>
    // Methods on BC types that accept ITreeObject/NavRecord but should be no-ops in standalone mode.
    // NOTE: RunEvent is NOT stripped here anymore — it's intercepted specifically in the
    // statement-level rewriter so we can dispatch to registered event subscribers (#32).
    private static readonly HashSet<string> StripEntireCallMethods = new(StringComparer.Ordinal)
    {
        "ALCommit",   // ALDatabase.ALCommit() — SQL transaction commit, no-op standalone
        "ALSelectLatestVersion", // ALDatabase.ALSelectLatestVersion() — no-op standalone
        "ALAlterKey", // ALDatabase.ALAlterKey() — DDL not supported standalone; no-op
        "ALCheckLicenseFile", // ALDatabase.ALCheckLicenseFile() — no license system standalone; no-op
        "ALChangeUserPassword",  // ALDatabase.ALChangeUserPassword(err, old, new) — user system not modeled; no-op standalone
        "ALSetUserPassword",     // ALDatabase.ALSetUserPassword(userId, newPwd) — service-tier user system; no-op standalone
        "ALCopyCompany",  // ALDatabase.ALCopyCompany(src, dest) — no multi-company store standalone; no-op
        "ALImportData",   // ALDatabase.ALImportData(tableNo, path, create) — no file I/O standalone; no-op
        "ALExportData",   // ALDatabase.ALExportData(fileName, ...) — no file I/O standalone; no-op
        "ALImportFile",   // NavXmlPort.ALImportFile(portId, fileName) — file-based XmlPort.Import; no file I/O standalone; no-op
        "ALDataFileInformation", // ALDatabase.ALDataFileInformation(showDialog, ref name, ...) — queries BC data file; no real DB standalone; no-op
        "ALRegisterTableConnection",  // ALDatabase.ALRegisterTableConnection(target, ct, name, conn) — no external connections standalone; no-op
        "ALSetDefaultTableConnection",  // ALDatabase.ALSetDefaultTableConnection(ct, name) — no external connections standalone; no-op
        "ALUnregisterTableConnection",  // ALDatabase.ALUnregisterTableConnection(ct, name) — no external connections standalone; no-op
        "ALAddAction",  // <navALErrorInfo | mockNotification>.ALAddAction(caption, codeunitId, method [, desc]) —
                        // NavALErrorInfo.ALAddAction crashes standalone (null parent in NavApplicationObjectBaseHandle ctor),
                        // and neither interactive ErrorInfo drill-down actions nor Notification action dispatch happen
                        // without a UI. Stripping is safe — MockNotification tests that exist don't assert on stored action state.
        "ALAddNavigationAction",  // NavALErrorInfo.ALAddNavigationAction(caption [, description]) —
                        // navigation drill-downs require a UI client to open; no-op in standalone mode.
        "ALSendTraceTag",  // ALSession.ALSendTraceTag(session, tag, category, verbosity, msg, classification) — telemetry; no-op standalone
        "ALLogSecurityAudit",  // ALSession.ALLogSecurityAudit(session, desc, result, resultDesc, category, ...) — needs OpenTelemetry DLL; no-op standalone
        "ALEnableVerboseTelemetry",  // ALSession.ALEnableVerboseTelemetry(session, enabled, duration) — telemetry config; no-op standalone
        "ALSetDocumentServiceToken",  // ALSession.ALSetDocumentServiceToken(session, token) — OneDrive integration; no-op standalone
        "ALCodeCoverageLoadFromTable",  // ALCodeCoverage.ALCodeCoverageLoadFromTable() — no code coverage engine standalone; no-op
        "ALCodeCoverageLog",            // ALCodeCoverage.ALCodeCoverageLog(enabled) — no code coverage engine standalone; no-op
        "ALCodeCoverageRefreshTable",   // ALCodeCoverage.ALCodeCoverageRefreshTable() — no code coverage engine standalone; no-op
        "ALCodeCoverageInclude",        // ALCodeCoverage.ALCodeCoverageInclude(record) — no code coverage engine standalone; no-op
        "ALImportObjects",              // ALDatabase.ALImportObjects(stream) — object import requires BC runtime; no-op standalone
        "ALExportObjects",              // ALDatabase.ALExportObjects(stream, ...) — object export requires BC runtime; no-op standalone
    };

    private static readonly HashSet<string> StripITreeObjectArgMethods = new(StringComparer.Ordinal)
    {
        "ALByValue", "ModifyLength", "ALRecord",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be replaced with null!,
    /// keeping the rest of the call intact. These are BC runtime methods on real BC types
    /// that require ITreeObject but work with null in standalone mode.
    /// e.g. blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
    /// </summary>
    private static readonly HashSet<string> NullifyFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALCreateInStream", "ALCreateOutStream",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be removed entirely,
    /// keeping the rest of the call intact. These are methods on mock types (MockRecordRef)
    /// where the mock doesn't need the ITreeObject parameter.
    /// e.g. recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
    /// </summary>
    private static readonly HashSet<string> StripFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALField",
        "ALFieldIndex",
    };

    /// <summary>
    /// Names of .Target methods on record handles that should have .Target stripped.
    /// </summary>
    private static readonly HashSet<string> RecordTargetMethods = new(StringComparer.Ordinal)
    {
        "ALInit", "ALInsert", "ALModify", "ALGet", "ALFind", "ALNext", "ALDelete",
        "ALDeleteAll", "ALCount", "ALSetRange", "ALSetFilter", "ALFindSet",
        "ALFindFirst", "ALFindLast", "ALIsEmpty", "ALCalcFields", "ALSetCurrentKey",
        "ALReset", "ALCopy", "ALCopyFilter", "ALCopyFilters", "ALTestField", "ALTestFieldSafe", "ALValidate", "ALValidateSafe", "ALRename",
        "ALLockTable", "ALCalcSums", "ALSetLoadFields", "ALAddLoadFields", "ALAreFieldsLoaded", "ALFieldCaption", "ALSetRecFilter",
        "ALTableCaption", "ALTableName", "ALTestFieldNavValueSafe",
        "ALFilterGroup", "ALSetRangeSafe", "ALReadIsolation",
        "ALGetView", "ALSetView", "ALGetFilter",
        "ALTransferFields", "ALMark", "ALMarkedOnly", "ALClearMarks",
        "ALGetFilters", "ALGetRangeMinSafe", "ALGetRangeMaxSafe",
        "ALHasFilter", "ALCurrentKey", "ALAscending", "ALCountApprox",
        "ALConsistent", "ALFieldActive", "ALAddLink", "ALDeleteLink", "ALDeleteLinks",
        "ALHasLinks", "ALCopyLinks", "ALWritePermission", "ALSetPermissionFilter",
        "SetFieldValueSafe", "GetFieldValueSafe", "GetFieldRefSafe",
    };

    public static string Rewrite(string csharp)
    {
        return RewriteToTree(csharp).GetRoot().ToFullString();
    }

    /// <summary>
    /// Rewrite BC-generated C# and return the result as a SyntaxTree.
    /// NormalizeWhitespace is applied to fix trivia left by node removal.
    /// The returned tree can be passed directly to Roslyn compilation,
    /// avoiding a redundant parse round-trip through string form.
    /// </summary>
    public static SyntaxTree RewriteToTree(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var root = tree.GetRoot();

        var rewriter = new RoslynRewriter();

        var newRoot = rewriter.Visit(root);
        return CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot.NormalizeWhitespace());
    }

    // -----------------------------------------------------------------------
    // Using directives
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var name = node.NamespaceOrType.ToString();
        if (RemoveUsings.Contains(name))
            return null;
        return base.VisitUsingDirective(node);
    }

    // -----------------------------------------------------------------------
    // Namespace: inject "using AlRunner.Runtime;" after opening brace
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        // First recurse into children
        var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

        // Add using AlRunner.Runtime if not already present
        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // Also handle file-scoped namespaces
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Attribute lists: remove BC-specific attributes
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var kept = new SeparatedSyntaxList<AttributeSyntax>();
        bool anyReplaced = false;
        foreach (var attr in node.Attributes)
        {
            var attrName = GetSimpleAttributeName(attr);
            if (attrName == "NavCodeunitOptions" && IsManualEventSubscriber(attr))
            {
                // Replace NavCodeunitOptions with ManualEventSubscriber marker
                kept = kept.Add(SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("AlRunner.Runtime.ManualEventSubscriber")));
                anyReplaced = true;
            }
            else if (!BcAttributeNames.Contains(attrName))
                kept = kept.Add(attr);
        }

        if (kept.Count == 0)
            return null; // remove entire attribute list

        if (!anyReplaced && kept.Count == node.Attributes.Count)
            return base.VisitAttributeList(node); // nothing changed

        return node.WithAttributes(kept);
    }

    /// <summary>
    /// Check if a NavCodeunitOptions attribute indicates EventSubscriberInstance = Manual.
    /// In generated C#: <c>[NavCodeunitOptions(NavCodeunitOptions.EventManualBinding, ...)]</c>
    /// The first arg is a flags enum — <c>EventManualBinding</c> or a bitwise OR containing it.
    /// </summary>
    private static bool IsManualEventSubscriber(AttributeSyntax attr)
    {
        if (attr.ArgumentList == null || attr.ArgumentList.Arguments.Count == 0)
            return false;
        var firstArg = attr.ArgumentList.Arguments[0].Expression.ToString();
        return firstArg.Contains("EventManualBinding");
    }

    private static string GetSimpleAttributeName(AttributeSyntax attr)
    {
        // Extract the last identifier from potentially qualified name
        var name = attr.Name;
        if (name is QualifiedNameSyntax qns)
            return qns.Right.Identifier.Text;
        if (name is IdentifierNameSyntax ins)
            return ins.Identifier.Text;
        return name.ToString();
    }

    // -----------------------------------------------------------------------
    // Class declarations: handle base classes, remove BC members, add _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // XmlPort object classes (XmlPortNNNN : NavXmlPort) have complex schema
        // initialization code that cannot compile in standalone mode. Replace the
        // entire class with a minimal stub that extends MockXmlPortHandle.
        if (node.BaseList != null && node.BaseList.Types.Any(
                t => t.Type.ToString() == "NavXmlPort"))
        {
            var stubClass = node
                .WithBaseList(SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(
                            SyntaxFactory.ParseTypeName("AlRunner.Runtime.MockXmlPortHandle")))))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(new[]
                {
                    SyntaxFactory.ParseMemberDeclaration(
                        $"public {node.Identifier.Text}() {{ }}")!
                }))
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
            return stubClass;
        }

        // Query object classes (QueryNNNN : NavQuery) reference NCLMetaQuery,
        // data-item handles, and other service-tier infrastructure that cannot
        // compile in standalone mode. Replace the entire class with a minimal
        // stub that extends MockQueryHandle, identical to the XmlPort pattern.
        if (node.BaseList != null && node.BaseList.Types.Any(
                t => t.Type.ToString() == "NavQuery"))
        {
            var stubClass = node
                .WithBaseList(SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(
                            SyntaxFactory.ParseTypeName("AlRunner.Runtime.MockQueryHandle")))))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(new[]
                {
                    SyntaxFactory.ParseMemberDeclaration(
                        $"public {node.Identifier.Text}() {{ }}")!
                }))
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
            return stubClass;
        }

        // Report / ReportExtension generated classes pull in BC runtime types and
        // layout infrastructure that do not exist in standalone mode. Keep the type
        // shape and generated helper-procedure dispatch members, but strip BC-only
        // inheritance and unsupported runtime/layout members.
        if (node.BaseList != null && node.BaseList.Types.Any(
                t => t.Type.ToString() is "NavReport" or "NavReportExtension"
                    or "RequestPageBase" or "NavRequestPageExtension"))
        {
            bool isReportExtension = node.BaseList.Types.Any(
                t => t.Type.ToString() == "NavReportExtension");

            var preservedMembers = new List<MemberDeclarationSyntax>();

            foreach (var member in node.Members)
            {
                // Skip constructors — they call base() which no longer exists.
                if (member is ConstructorDeclarationSyntax)
                    continue;

                if (member is MethodDeclarationSyntax method)
                {
                    var methodName = method.Identifier.Text;
                    // Skip problematic override methods that reference base class
                    // infrastructure we removed (OnClear, OnInvoke, OnMetadataLoaded).
                    // Report lifecycle triggers (OnPreReport, OnPostReport) are overrides
                    // too but we preserve them — strip just the 'override' keyword so
                    // they compile without a base class and MockReportHandle can invoke them.
                    if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
                    {
                        if (methodName is "OnPreReport" or "OnPostReport")
                        {
                            // Preserve the method body but remove 'override' so it compiles
                            // without the NavReport base class.
                            var newModifiers = method.Modifiers.Where(
                                m => !m.IsKind(SyntaxKind.OverrideKeyword) && !m.IsKind(SyntaxKind.VirtualKeyword));
                            method = method.WithModifiers(
                                SyntaxFactory.TokenList(newModifiers));
                        }
                        else
                        {
                            continue;
                        }
                    }
                    // Skip InitializeComponent — references BC fields/properties
                    // (BeginInitialization, Add, EndInitialization, RequestOptionsPage).
                    if (methodName == "InitializeComponent")
                        continue;
                    // Skip __Construct factory methods — they call removed constructors.
                    if (methodName == "__Construct")
                        continue;

                    if (Visit(method) is MemberDeclarationSyntax visitedMethod)
                        preservedMembers.Add(visitedMethod);
                }
                else if (member is FieldDeclarationSyntax)
                {
                    // Preserve fields (e.g. label NavTextConstant/NavText constants)
                    // so nested scope classes can reference them.
                    if (Visit(member) is MemberDeclarationSyntax visitedMember)
                        preservedMembers.Add(visitedMember);
                }
                else if (member is PropertyDeclarationSyntax prop)
                {
                    // Skip override properties — they reference base class
                    // infrastructure we removed (IsCompiledForOnPremise, ObjectName).
                    if (prop.Modifiers.Any(SyntaxKind.OverrideKeyword))
                        continue;
                    // Skip properties referencing RequestOptionsPage (removed base member).
                    if (prop.Identifier.Text == "RequestOptionsPage")
                        continue;
                    // Skip CurrReport on report extensions — it casts
                    // ParentObject to NavReport which is gone after stripping base.
                    // We inject a stub CurrReport below.
                    if (isReportExtension && prop.Identifier.Text == "CurrReport")
                        continue;
                    if (Visit(member) is MemberDeclarationSyntax visitedMember)
                        preservedMembers.Add(visitedMember);
                }
                else if (member is ClassDeclarationSyntax
                    or StructDeclarationSyntax
                    or InterfaceDeclarationSyntax
                    or EnumDeclarationSyntax
                    or DelegateDeclarationSyntax)
                {
                    if (Visit(member) is MemberDeclarationSyntax visitedMember)
                        preservedMembers.Add(visitedMember);
                }
            }

            preservedMembers.Insert(0,
                SyntaxFactory.ParseMemberDeclaration(
                    $"public {node.Identifier.Text}() {{ }}")!);

            // Inject CurrReport.* stubs that come from the stripped NavReport base class.
            // BC compiler emits these as instance calls on the report class, but after
            // stripping the base class they must be provided explicitly.
            preservedMembers.Add(
                SyntaxFactory.ParseMemberDeclaration(
                    "public void Skip() { }")!);
            preservedMembers.Add(
                SyntaxFactory.ParseMemberDeclaration(
                    "public void Break() { }")!);
            // Quit — CurrReport.Quit() ends the report run; no-op in standalone mode.
            preservedMembers.Add(
                SyntaxFactory.ParseMemberDeclaration(
                    "public void Quit() { }")!);
            // PrintOnlyIfDetail — CurrReport.PrintOnlyIfDetail (get/set bool property).
            preservedMembers.Add(
                SyntaxFactory.ParseMemberDeclaration(
                    "public bool PrintOnlyIfDetail { get; set; }")!);

            // For report extensions: inject a CurrReport stub.
            // BC generates a CurrReport property that casts this.ParentObject to the
            // report type. After stripping the NavReportExtension base class, ParentObject
            // is undefined and that cast fails. Inject a self-referencing stub so that
            // CurrReport.Skip() / CurrReport.Break() still compile.
            if (isReportExtension)
            {
                preservedMembers.Add(
                    SyntaxFactory.ParseMemberDeclaration(
                        $"public {node.Identifier.Text} CurrReport => this;")!);
            }

            var stubClass = node
                .WithBaseList(null)
                .WithMembers(SyntaxFactory.List(preservedMembers))
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
            return stubClass;
        }

        // Detect if this is a scope class BEFORE visiting children.
        // We need to know the enclosing class name for _parent field type.
        bool isScopeClass = false;
        bool isRecordClass = false;
        bool isPageExtensionClass = false;
        bool isPageClass = false;
        bool isCodeunitClass = false;
        string? enclosingClassName = null;
        if (node.BaseList != null)
        {
            foreach (var baseType in node.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();
                if (typeText == "NavCodeunit" || typeText == "NavTestCodeunit"
                    || typeText == "NavUpgradeCodeunit" || typeText == "NavTestRunnerCodeUnit")
                    isCodeunitClass = true;
                if (typeText == "NavRecord" || typeText == "NavRecordExtension")
                    isRecordClass = true;
                if (typeText == "NavFormExtension")
                    isPageExtensionClass = true;
                if (typeText == "NavForm")
                    isPageClass = true;
                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    isScopeClass = true;
                    // Extract the generic type parameter as the enclosing class name
                    // NavMethodScope<Codeunit139771> -> Codeunit139771
                    var ltIdx = typeText.IndexOf('<');
                    var gtIdx = typeText.IndexOf('>');
                    if (ltIdx >= 0 && gtIdx > ltIdx)
                        enclosingClassName = typeText.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                    break;
                }
            }
        }

        // Track the current class name for Rec/xRec table ID extraction.
        // Only set for top-level (non-nested) classes that are record types.
        var previousClassName = _currentClassName;
        if (isRecordClass)
            _currentClassName = node.Identifier.Text;

        // First, visit children recursively
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        // Handle base class list
        if (visited.BaseList != null)
        {
            var newTypes = new SeparatedSyntaxList<BaseTypeSyntax>();
            foreach (var baseType in visited.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();

                if (typeText == "NavCodeunit" || typeText == "NavTestCodeunit" || typeText == "NavTestRunnerCodeUnit" || typeText == "NavRecord"
                    || typeText == "NavFormExtension" || typeText == "NavRecordExtension"
                    || typeText == "NavEventScope" || typeText == "NavUpgradeCodeunit"
                    || typeText == "NavForm")
                {
                    // Remove these base classes entirely
                    continue;
                }

                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    // Replace with AlScope
                    var alScopeType = SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName("AlScope"));
                    newTypes = newTypes.Add(alScopeType);
                    continue;
                }

                newTypes = newTypes.Add(baseType);
            }

            if (newTypes.Count == 0)
                visited = visited.WithBaseList(null);
            else
                visited = visited.WithBaseList(SyntaxFactory.BaseList(newTypes));
        }

        // Remove specific members
        var membersToKeep = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (ShouldRemoveMember(member, visited))
                continue;
            membersToKeep = membersToKeep.Add(member);
        }

        visited = visited.WithMembers(membersToKeep);

        // For scope classes: add a _parent field and public Parent property of the enclosing class type.
        // BC scope classes inherit Parent from NavMethodScope<T>. After replacing with AlScope,
        // we inject both the backing field (_parent, used by the base.Parent → _parent rewrite)
        // and a public property (Parent, used when BC emits bare Parent access in
        // report/reportextension scope classes). This fixes CS1061 'Parent' errors.
        if (isScopeClass && enclosingClassName != null)
        {
            var parentField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(enclosingClassName))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("_parent"))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            var parentProperty = SyntaxFactory.ParseMemberDeclaration(
                $"public {enclosingClassName} Parent => _parent;")!;

            // Insert _parent field and Parent property at the beginning
            visited = visited.WithMembers(
                visited.Members
                    .Insert(0, parentProperty)
                    .Insert(0, parentField));
        }

        // For Record classes: add delegating methods that forward to Rec (MockRecordHandle).
        // In the original code, Record2632 : NavRecord, so it inherits GetFieldValueSafe, ALModify, etc.
        // After rewriting, the base class is removed and Rec is a separate MockRecordHandle property.
        // Scope classes reference _parent.GetFieldValueSafe(...) which needs to work on the Record class.
        if (isRecordClass)
        {
            var delegatingCode = @"
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType) => Rec.GetFieldValueSafe(fieldNo, expectedType);
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType, bool useLocale) => Rec.GetFieldValueSafe(fieldNo, expectedType, useLocale);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.SetFieldValueSafe(fieldNo, expectedType, value);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value, bool validate) => Rec.SetFieldValueSafe(fieldNo, expectedType, value, validate);
public NavValue GetFieldRefSafe(int fieldNo, NavType expectedType) => Rec.GetFieldRefSafe(fieldNo, expectedType);
public bool ALInsert(DataError errorLevel) => Rec.ALInsert(errorLevel);
public bool ALInsert(DataError errorLevel, bool runTrigger) => Rec.ALInsert(errorLevel, runTrigger);
public bool ALModify(DataError errorLevel) => Rec.ALModify(errorLevel);
public bool ALModify(DataError errorLevel, bool runTrigger) => Rec.ALModify(errorLevel, runTrigger);
public bool ALGet(DataError errorLevel, params NavValue[] keyValues) => Rec.ALGet(errorLevel, keyValues);
public bool ALFind(DataError errorLevel, string searchMethod = ""-"") => Rec.ALFind(errorLevel, searchMethod);
public bool ALFindSet(DataError errorLevel = DataError.ThrowError, bool forUpdate = false) => Rec.ALFindSet(errorLevel, forUpdate);
public bool ALFindFirst(DataError errorLevel = DataError.ThrowError) => Rec.ALFindFirst(errorLevel);
public bool ALFindLast(DataError errorLevel = DataError.ThrowError) => Rec.ALFindLast(errorLevel);
public int ALNext() => Rec.ALNext();
public int ALNext(int steps) => Rec.ALNext(steps);
public bool ALDelete(DataError errorLevel, bool runTrigger = false) => Rec.ALDelete(errorLevel, runTrigger);
public void ALDeleteAll(bool runTrigger) => Rec.ALDeleteAll(runTrigger);
public void ALDeleteAll(DataError errorLevel = DataError.ThrowError, bool runTrigger = false) => Rec.ALDeleteAll(errorLevel, runTrigger);
public void ALInit() => Rec.ALInit();
public void ALReset() => Rec.ALReset();
public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRange(fieldNo, expectedType, fromValue, toValue);
public void ALSetRangeSafe(int fieldNo, NavType expectedType) => Rec.ALSetRangeSafe(fieldNo, expectedType);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALSetRangeSafe(fieldNo, expectedType, value);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRangeSafe(fieldNo, expectedType, fromValue, toValue);
public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, filterExpression, args);
public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, expectedType, filterExpression, args);
public void ALCopy(MockRecordHandle source, bool shareFilters = false) => Rec.ALCopy(source, shareFilters);
public void ALCopyFilter(int fromFieldNo, MockRecordHandle target) => Rec.ALCopyFilter(fromFieldNo, target);
public void ALCopyFilter(int fromFieldNo, MockRecordHandle target, int toFieldNo) => Rec.ALCopyFilter(fromFieldNo, target, toFieldNo);
public void ALCopyFilters(MockRecordHandle source) => Rec.ALCopyFilters(source);
public void ALValidateSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidateSafe(fieldNo, expectedType, value);
public void ALValidate(DataError errorLevel, int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidate(errorLevel, fieldNo, expectedType, value);
public void ALTestFieldSafe(int fieldNo, NavType expectedType) => Rec.ALTestFieldSafe(fieldNo, expectedType);
public void ALTestFieldSafe(int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestFieldSafe(fieldNo, expectedType, expectedValue);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType) => Rec.ALTestField(errorLevel, fieldNo, expectedType);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestField(errorLevel, fieldNo, expectedType, expectedValue);
public void ALCalcFields(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcFields(errorLevel, fieldNos);
public bool ALCalcSums(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcSums(errorLevel, fieldNos);
public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos) => Rec.ALSetCurrentKey(errorLevel, fieldNos);
public void ALSetCurrentKey(params int[] fieldNos) => Rec.ALSetCurrentKey(fieldNos);
public void ALSetAscending(int fieldNo, bool ascending) => Rec.ALSetAscending(fieldNo, ascending);
public int ALCount => Rec.ALCount;
public bool ALIsEmpty => Rec.ALIsEmpty;
public bool ALIsTemporary => Rec.ALIsTemporary;
public string ALTableCaption => Rec.ALTableCaption;
public string ALTableName => Rec.ALTableName;
public int ALFieldNo(string fieldName) => Rec.ALFieldNo(fieldName);
public int ALFieldNo(int fieldNo) => Rec.ALFieldNo(fieldNo);
public NavText ALFieldCaption(int fieldNo) => Rec.ALFieldCaption(fieldNo);
public void ALSetRecFilter() => Rec.ALSetRecFilter();
public void ALLockTable(DataError errorLevel = DataError.ThrowError) => Rec.ALLockTable(errorLevel);
public bool ALRename(DataError errorLevel, params NavValue[] newKeyValues) => Rec.ALRename(errorLevel, newKeyValues);
public void ALSetLoadFields(params int[] fieldNos) => Rec.ALSetLoadFields(fieldNos);
public void ALSetAutoCalcFields(params object[] fields) => Rec.ALSetAutoCalcFields(fields);
public string ALGetFilter() => Rec.ALGetFilter();
public string ALGetFilter(int fieldNo) => Rec.ALGetFilter(fieldNo);
public string ALGetView(bool useNames = true) => Rec.ALGetView(useNames);
public void ALSetView(string view) => Rec.ALSetView(view);
public void ALAssign(MockRecordHandle other) => Rec.ALAssign(other);
public void ClearFieldValue(int fieldNo) => Rec.ClearFieldValue(fieldNo);
public int ALFilterGroup { get => Rec.ALFilterGroup; set => Rec.ALFilterGroup = value; }
public object ALReadIsolation { get => Rec.ALReadIsolation; set => Rec.ALReadIsolation = value; }
public void Clear() => Rec.Clear();
public void ALTransferFields(MockRecordHandle source, bool initPrimaryKey = true) => Rec.ALTransferFields(source, initPrimaryKey);
public void ALMark(bool mark) => Rec.ALMark(mark);
public bool ALMark() => Rec.ALMark();
public void ALClearMarks() => Rec.ALClearMarks();
public bool ALMarkedOnly { get => Rec.ALMarkedOnly; set => Rec.ALMarkedOnly = value; }
public int CurrFieldNo { get; set; }
public string ALGetFilters => Rec.ALGetFilters;
public bool ALHasFilter => Rec.ALHasFilter;
public object ALGetRangeMinSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMinSafe(fieldNo, expectedType);
public object ALGetRangeMaxSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMaxSafe(fieldNo, expectedType);
public string ALCurrentKey => Rec.ALCurrentKey;
public bool ALAscending { get => Rec.ALAscending; set => Rec.ALAscending = value; }
public int ALCountApprox => Rec.ALCountApprox;
public void ALConsistent(bool consistent) => Rec.ALConsistent(consistent);
public bool ALFieldActive(int fieldNo) => Rec.ALFieldActive(fieldNo);
public int ALAddLink(string link) => Rec.ALAddLink(link);
public int ALAddLink(string link, string description) => Rec.ALAddLink(link, description);
public void ALDeleteLink(int linkId) => Rec.ALDeleteLink(linkId);
public void ALDeleteLinks() => Rec.ALDeleteLinks();
public bool ALHasLinks => Rec.ALHasLinks;
public void ALCopyLinks(MockRecordHandle source) => Rec.ALCopyLinks(source);
public bool ALWritePermission => Rec.ALWritePermission;
public void ALSetPermissionFilter() => Rec.ALSetPermissionFilter();
protected bool CallGetDecimalPlacesExtensionMethod(int fieldNo, ref string result) { return false; }
protected bool CallGetTableRelationExtensionMethod(int fieldNo, MockRecordHandle rec, ref bool result) { return false; }
protected bool CallGetFormatExtensionMethod(int fieldNo, ref string result) { return false; }
";
            var delegatingMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {delegatingCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;

            visited = visited.WithMembers(visited.Members.AddRange(delegatingMembers));
        }

        // For page extension classes: inject a ParentObject property returning Rec.
        // Page extensions reference this.ParentObject (after base.ParentObject is rewritten)
        // to access the parent record. Without this, page extensions fail compilation and
        // cascade their exclusion to dependent record types.
        if (isPageExtensionClass)
        {
            var parentObjectCode = @"
public MockRecordHandle ParentObject => Rec;
public MockCurrPage CurrPage { get; } = new MockCurrPage();
";
            var pageMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {parentObjectCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
            visited = visited.WithMembers(visited.Members.AddRange(pageMembers));
        }

        // Standalone page classes (NavForm → removed base) need CurrPage methods injected
        // because CurrPage is (PageNNNN)this — calling CurrPage.Update() calls Page.Update().
        // Also inject extension-dispatch stubs that NavForm provided as virtual methods.
        if (isPageClass)
        {
            // Pages with a SourceTable have a Rec field; pages without one do not.
            // SetSelectionFilter must only reference this.Rec when the field exists.
            bool hasRec = visited.Members
                .OfType<FieldDeclarationSyntax>()
                .Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "Rec"))
                || visited.Members
                .OfType<PropertyDeclarationSyntax>()
                .Any(p => p.Identifier.Text == "Rec");

            var setSelectionFilter = hasRec
                // SetSelectionFilter: In real BC, this applies the UI-selection filter from a
                // temporary recordset. In standalone mode we approximate by copying the page's
                // Rec and applying its record filter — sufficient for compilation but not
                // semantically identical to the real multi-selection behaviour.
                ? "public void SetSelectionFilter(MockRecordHandle rec) { rec.ALCopy(this.Rec, true); rec.ALSetRecFilter(); }"
                // Page has no SourceTable — no Rec field, so just apply the record filter.
                : "public void SetSelectionFilter(MockRecordHandle rec) { rec.ALSetRecFilter(); }";

            var pageMemberCode = $@"
public void Update(bool saveRecord = true) {{ }}
public void Close() {{ }}
public void Activate() {{ }}
public void SaveRecord() {{ }}
public void SetTableView(MockRecordHandle rec) {{ }}
{setSelectionFilter}
public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId) {{ taskId.Value = 1; }}
public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId, NavDictionary<NavText, NavText> parameters) {{ taskId.Value = 1; }}
public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId, int timeout) {{ taskId.Value = 1; }}
public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId, NavDictionary<NavText, NavText> parameters, int timeout) {{ taskId.Value = 1; }}
public void CancelBackgroundTask(int taskId) {{ }}
public void CancelBackgroundTask(DataError errorLevel, int taskId) {{ }}
protected bool CallGetDecimalPlacesExtensionMethod(int fieldNo, ref string result) {{ return false; }}
protected bool CallGetTableRelationExtensionMethod(int fieldNo, MockRecordHandle rec, ref bool result) {{ return false; }}
protected bool CallGetFormatExtensionMethod(int fieldNo, ref string result) {{ return false; }}
";
            var pageMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {pageMemberCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
            visited = visited.WithMembers(visited.Members.AddRange(pageMembers));
        }

        if (isCodeunitClass)
        {
            // BC emits a protected OnClear() on each codeunit that resets all
            // AL globals to their type defaults. AL's ClearAll() is lowered to
            // ClearApplicationMemberVariables() — delegate to OnClear via
            // reflection so codeunits that happen not to declare any globals
            // (no OnClear emitted) still compile.
            var codeunitMemberCode = @"
public void ClearApplicationMemberVariables()
{
    var m = GetType().GetMethod(""OnClear"",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public);
    m?.Invoke(this, null);
}
";
            var codeunitMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {codeunitMemberCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
            visited = visited.WithMembers(visited.Members.AddRange(codeunitMembers));
        }

        // Restore previous class name context
        _currentClassName = previousClassName;

        return visited;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, ClassDeclarationSyntax parentClass)
    {
        // Remove BC constructor: public CodeunitXXX(ITreeObject parent) : base(parent, NNN) { }
        // Remove Record constructor: public RecordXXX(ITreeObject parent, NCLMetaTable ...) : base(...) { }
        if (member is ConstructorDeclarationSyntax ctor)
        {
            var paramText = ctor.ParameterList.ToString();
            if (paramText.Contains("ITreeObject parent"))
                return true;
        }

        // Remove methods
        if (member is MethodDeclarationSyntax method)
        {
            var name = method.Identifier.Text;

            // Remove __Construct
            if (name == "__Construct")
                return true;

            // Remove OnInvoke
            if (name == "OnInvoke" && method.ParameterList.Parameters.Count == 2)
            {
                var firstParam = method.ParameterList.Parameters[0].Type?.ToString();
                var secondParam = method.ParameterList.Parameters[1].Type?.ToString();
                if (firstParam == "int" && secondParam == "object[]")
                    return true;
            }

            // Keep OnRun with parameters (the codeunit's outer OnRun wrapper).
            // It creates the scope and calls Run(), which is needed for Codeunit.Run() dispatch.
            // The override keyword and BC-specific parameter types are handled by other rewriter passes.

            // Remove GetMethodScopeFlags (BC runtime permission checking, irrelevant for standalone)
            if (name == "GetMethodScopeFlags")
                return true;

            // Remove BC interface dispatch methods (used by NavInterfaceHandle runtime)
            if (name == "IsInterfaceOfType" || name == "IsInterfaceMethod")
                return true;

            // Remove Page/Extension-specific methods that reference BC Page runtime
            if (name == "OnMetadataLoaded" || name == "EvaluateCaptionClass"
                || name == "OnEvaluateCaptionClass" || name == "RegisterDynamicCaptionExpression"
                || name == "EnsureGlobalVariablesInitialized" || name == "CallEvaluateCaptionClassExtensionMethod"
                || name == "CallOnMetadataLoadedExtensionMethod"
                || name == "RegisterUIPart")
                return true;

            // Remove Page InitializeComponent that contains NavForm-specific calls.
            // Codeunit InitializeComponent doesn't call these, so this is safe.
            if (name == "InitializeComponent" || name == "InitializeForm")
            {
                var bodyText = method.Body?.ToString() ?? "";
                if (bodyText.Contains("CallInitializeComponentExtensionMethod") ||
                    bodyText.Contains("InitializeForm") ||
                    bodyText.Contains("RegisterUIPart"))
                    return true;
            }
        }

        // Remove properties that cast to NavForm — rewritten CurrPage will be injected below.
        if (member is PropertyDeclarationSyntax propCheck)
        {
            var propText = propCheck.ToString();
            if (propText.Contains("(NavForm)"))
                return true;
        }

        // Remove specific properties
        if (member is PropertyDeclarationSyntax prop)
        {
            var name = prop.Identifier.Text;

            // public override string ObjectName => "...";
            if (name == "ObjectName")
                return true;

            // public override bool IsCompiledForOnPremise => true;
            if (name == "IsCompiledForOnPremise")
                return true;

            // public override bool IsSingleInstance => false;
            if (name == "IsSingleInstance")
                return true;

            // TestRunner metadata overrides are only meaningful on BC's
            // NavTestRunnerCodeUnit base, which we remove in standalone mode.
            if (name == "OnTestRunMethodsHaveTestPermissionsParameter"
                || name == "CommitTestCodeunits"
                || name == "CommitTestFunctions")
                return true;

            // Rec/xRec: Don't remove — rewrite to MockRecordHandle stub in VisitPropertyDeclaration
            // (removed the deletion that was here)

            // protected override uint RawScopeId { get => ...; set => ...; }
            if (name == "RawScopeId")
                return true;

            // private new NavRecord ParentObject => ...;
            // Keep ParentObject for all types that have a Rec property — it's rewritten
            // to return Rec in VisitPropertyDeclaration. This prevents page extensions
            // from failing compilation and cascading exclusions to dependent records.
            if (name == "ParentObject")
            {
                var className = parentClass.Identifier.Text;
                if (className.StartsWith("TableExtension") || className.StartsWith("Record")
                    || className.StartsWith("PageExtension"))
                    return false; // keep — will be rewritten to => Rec
                return true; // remove for codeunits, etc.
            }
            // protected override uint[] IndirectPermissionList => ...;
            if (name == "IndirectPermissionList")
                return true;

            // public override NavEventScope EventScope { get; set; }
            if (name == "EventScope")
                return true;

            // public override int MethodId => N;
            if (name == "MethodId")
                return true;
        }

        // Remove static αscopeId field (Unicode \u03b1 prefix)
        if (member is FieldDeclarationSyntax field)
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                var text = variable.Identifier.Text;
                // Match by ValueText, Text, or fallback pattern: any field ending with "scopeId"
                if (name == "\u03b1scopeId" || text == "\u03b1scopeId" ||
                    name.EndsWith("scopeId") || text.EndsWith("scopeId"))
                    return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Method declarations: remove 'override' from OnClear
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Remove 'override' keyword from methods whose base class was removed.
        // We strip NavCodeunit/NavRecord/NavFormExtension/NavRecordExtension/NavEventScope,
        // so any 'override' in those classes becomes invalid.
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
        {
            var parentClass = node.Parent as ClassDeclarationSyntax;
            if (parentClass?.BaseList != null)
            {
                bool hadRemovedBase = parentClass.BaseList.Types.Any(t =>
                {
                    var txt = t.Type.ToString();
                    return txt == "NavCodeunit" || txt == "NavTestCodeunit" || txt == "NavTestRunnerCodeUnit" || txt == "NavRecord"
                        || txt == "NavFormExtension" || txt == "NavRecordExtension"
                        || txt == "NavEventScope" || txt == "NavUpgradeCodeunit"
                        || txt == "NavForm";
                });
                if (hadRemovedBase)
                {
                    var newModifiers = SyntaxFactory.TokenList(
                        visited.Modifiers.Where(m => !m.IsKind(SyntaxKind.OverrideKeyword)));
                    visited = visited.WithModifiers(newModifiers);
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Property declarations: rewrite Rec/xRec to MockRecordHandle stubs
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var name = node.Identifier.Text;

        // Rewrite Rec/xRec properties to return a MockRecordHandle with the correct table ID.
        // Original: private NavRecord Rec => (NavRecord)this.SourceTable;
        // Original: private RecordXXX Rec => (RecordXXX)this;
        // Rewritten: public MockRecordHandle Rec { get; } = new MockRecordHandle(tableId);
        // The table ID is extracted from the enclosing class name (e.g. Record74320 -> 74320).
        if (name == "Rec" || name == "xRec")
        {
            // Extract table ID from enclosing class name (Record74320 -> 74320)
            int tableId = 0;
            if (_currentClassName != null && _currentClassName.StartsWith("Record"))
            {
                int.TryParse(_currentClassName.Substring("Record".Length), out tableId);
            }

            // Parse a simple auto-property with a default value
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.Identifier(name))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression($"new MockRecordHandle({tableId})")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        // Rewrite ParentObject to return Rec for table extensions and records.
        // Original: private new NavRecord ParentObject => (NavRecord)base.ParentObject;
        // Rewritten: public MockRecordHandle ParentObject => Rec;
        if (name == "ParentObject")
        {
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.Identifier("ParentObject"))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.IdentifierName("Rec")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        return base.VisitPropertyDeclaration(node);
    }

    // -----------------------------------------------------------------------
    // Constructor declarations: handle scope ctors with _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        // For scope constructors (internal constructors in nested classes that inherit from AlScope),
        // the base class has been replaced with AlScope which has a parameterless constructor.
        // Remove ALL base(...) initializers from these constructors.
        if (visited.Initializer != null && visited.Initializer.Kind() == SyntaxKind.BaseConstructorInitializer)
        {
            // Check if this is a scope constructor by looking for parent or βparent references,
            // or simply any base() call on an internal constructor (scope constructors are internal).
            var isInternal = visited.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var hasParentArg = visited.Initializer.ArgumentList.Arguments
                .Any(a => a.Expression is IdentifierNameSyntax id &&
                    (id.Identifier.ValueText.Contains("parent") || id.Identifier.Text.Contains("parent")));

            if (hasParentArg || isInternal)
            {
                visited = visited.WithInitializer(null);
            }
        }

        // Handle βparent parameter: KEEP it but add _parent assignment to constructor body.
        // Instead of removing the parameter, we keep it and add: this._parent = βparent;
        if (visited.ParameterList.Parameters.Count > 0)
        {
            var firstParam = visited.ParameterList.Parameters[0];
            var paramName = firstParam.Identifier.ValueText;
            if (paramName.Contains("parent") || firstParam.Identifier.Text.Contains("parent"))
            {
                var typeText = firstParam.Type?.ToString() ?? "";
                if (typeText.StartsWith("Codeunit") || typeText.StartsWith("Record") || typeText.StartsWith("Page")
                    || typeText.StartsWith("Query") || typeText.StartsWith("Report") || typeText.StartsWith("XmlPort")
                    || typeText.StartsWith("TableExtension") || typeText.StartsWith("PageExtension"))
                {
                    // Keep the parameter, but add _parent assignment at the start of the body
                    var paramIdentifier = firstParam.Identifier.Text;
                    var assignmentStatement = SyntaxFactory.ParseStatement(
                        $"this._parent = {paramIdentifier};");

                    if (visited.Body != null)
                    {
                        var newStatements = visited.Body.Statements.Insert(0, assignmentStatement);
                        visited = visited.WithBody(visited.Body.WithStatements(newStatements));
                    }
                    else if (visited.ExpressionBody != null)
                    {
                        // Convert expression body to block body with assignment + expression
                        var exprStatement = SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression);
                        var body = SyntaxFactory.Block(assignmentStatement, exprStatement);
                        visited = visited.WithExpressionBody(null)
                            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                            .WithBody(body);
                    }

                    // Do NOT remove the parameter - keep it so callers can pass 'this'
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Identifier names: type replacements
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var text = node.Identifier.Text;

        // INavRecordHandle -> MockRecordHandle
        if (text == "INavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavRecordHandle -> MockRecordHandle (used in new NavRecordHandle(...))
        if (text == "NavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavCodeunitHandle -> MockCodeunitHandle
        if (text == "NavCodeunitHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockCodeunitHandle"));

        // NavInterfaceHandle -> MockInterfaceHandle
        if (text == "NavInterfaceHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInterfaceHandle"));

        // ALNumberSequence -> MockNumberSequence
        // The real type's ALExists/ALNext/ALRestart go through NavSession,
        // which is null under standalone mode and throws NullReferenceException.
        if (text == "ALNumberSequence")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockNumberSequence"));

        // ALNavApp -> MockNavApp
        // The real type's ALGetModuleInfo reaches into
        // Microsoft.Dynamics.Nav.CodeAnalysis, which isn't shipped with
        // al-runner — any NavApp.GetModuleInfo/GetCurrentModuleInfo call
        // crashes with an assembly-load failure under standalone mode.
        if (text == "ALNavApp")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockNavApp"));

        // ALSystemOperatingSystem -> MockSystemOperatingSystem
        // The real type's ALHyperlink dispatches through NavSession to
        // open a URL in the client — NullReferenceException under
        // standalone mode. Mock is a no-op.
        if (text == "ALSystemOperatingSystem")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockSystemOperatingSystem"));

        // NavHttp* -> MockHttp* (HttpClient, HttpResponseMessage, HttpContent,
        // HttpHeaders, HttpRequestMessage)
        // All BC HTTP types derive from NavComplexValue which requires
        // Parent.Tree != null — impossible in standalone mode. The rewriter
        // replaces them with lightweight in-memory mocks.
        if (text == "NavHttpClient")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockHttpClient"));
        if (text == "NavHttpResponseMessage")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockHttpResponseMessage"));
        if (text == "NavHttpContent")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockHttpContent"));
        if (text == "NavHttpHeaders")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockHttpHeaders"));
        if (text == "NavHttpRequestMessage")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockHttpRequestMessage"));
        if (text == "NavCookie")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockCookie"));
        if (text == "NavTestHttpResponseMessage")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestHttpResponseMessage"));
        if (text == "NavTestHttpRequestMessage")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestHttpRequestMessage"));

        // NavFormHandle -> MockFormHandle
        // BC emits `Page "X"` AL variables as `NavFormHandle p` fields with
        // `new NavFormHandle(this, pageId)` initializers — both args would
        // need an ITreeObject and a real NavForm which standalone mode lacks.
        if (text == "NavFormHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFormHandle"));

        // NavReportHandle -> MockReportHandle
        // Report variables use a handle wrapper for helper procedures, SetTableView,
        // Run, and RunRequestPage. The standalone runner provides a reflection-based mock.
        if (text == "NavReportHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockReportHandle"));

        // NavTestPageHandle -> MockTestPageHandle
        // BC emits `TestPage "X"` AL variables as `NavTestPageHandle tP` fields with
        // `new NavTestPageHandle(this, pageId)` initializers. The real ctor wants
        // ITreeObject which standalone mode lacks.
        if (text == "NavTestPageHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestPageHandle"));

        // NavXmlPortHandle -> MockXmlPortHandle
        // BC emits `XmlPort "X"` AL variables as `NavXmlPortHandle xP` fields with
        // `new NavXmlPortHandle(this, xmlPortId)` initializers. The real ctor wants
        // ITreeObject which standalone mode lacks.  After the existing .Target-stripping
        // rewrite, members like Source, Destination, Import(), Export() are called
        // directly on the handle — MockXmlPortHandle satisfies those.
        if (text == "NavXmlPortHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockXmlPortHandle"));

        // NavQueryHandle -> MockQueryHandle
        // BC emits `Query "X"` AL variables as `NavQueryHandle q` fields with
        // `new NavQueryHandle(this, queryId, SecurityFiltering)` initializers.
        // The real ctor wants ITreeObject which standalone mode lacks.  After
        // the existing .Target-stripping rewrite, query API calls (ALOpen,
        // ALRead, ALSetFilter, etc.) are called directly on the handle.
        if (text == "NavQueryHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockQueryHandle"));

        // NavRecordRef -> MockRecordRef
        // NavRecordRef's real ctor wants ITreeObject; MockRecordRef has a
        // parameterless ctor and stub methods so the AL declaration compiles.
        if (text == "NavRecordRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordRef"));

        // NavFieldRef -> MockFieldRef
        // NavFieldRef's real ctor wants ITreeObject; MockFieldRef has a
        // parameterless ctor with ALValue/ALNumber/ALAssign/ALField support.
        if (text == "NavFieldRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFieldRef"));

        // NavKeyRef -> MockKeyRef
        // NavKeyRef is used by RecordRef.KeyIndex(). MockKeyRef provides
        // ALActive, ALFieldCount, ALFieldIndex, ALRecord for key inspection.
        if (text == "NavKeyRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockKeyRef"));

        // NavXmlNameTable -> MockXmlNameTable
        // The real NavXmlNameTable.ALGet throws NavNCLKeyNotFoundException when the
        // requested name is absent.  AL semantics require Get() to return false/empty
        // rather than throw.  MockXmlNameTable is a dictionary-backed drop-in that
        // satisfies the ALAdd/ALGet API with safe missing-key handling.
        if (text == "NavXmlNameTable")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockXmlNameTable"));

        // NavFileUpload -> MockFileUpload
        // NavFileUpload represents a browser-uploaded file; it requires a service tier
        // to actually receive file data. MockFileUpload is a standalone in-memory version.
        if (text == "NavFileUpload")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFileUpload"));

        // NavFile -> MockFile
        // NavFile's real implementation accesses the OS filesystem and requires a
        // service-tier session. MockFile is a standalone in-memory byte-buffer version.
        if (text == "NavFile")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFile"));

        // NavBLOB -> MockBlob
        // NavBLOB's ALCreateInStream/ALCreateOutStream pass ITreeObject to
        // NavStream ctor which crashes with null in standalone mode.
        // MockBlob is a NavValue subclass with in-memory byte[] storage.
        if (text == "NavBLOB")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockBlob"));

        // NavMedia -> MockMedia
        // NavMedia's ALImportFile/ALImportStream/ALExportFile/ALExportStream/ALMediaId
        // require a BC service-tier blob catalog and session. MockMedia is an
        // in-memory stub: imports set a HasValue flag, exports are no-ops,
        // MediaId returns a per-instance GUID, FindOrphans returns an empty list.
        if (text == "NavMedia")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockMedia"));

        // NavMediaSet -> MockMediaSet
        // NavMediaSet's ALInsert/ALRemove/ALImport/ALExport require the BC session
        // and blob catalog. MockMediaSet is an in-memory list-backed stub.
        if (text == "NavMediaSet")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockMediaSet"));

        // NavInStream -> MockInStream
        // NavInStream's ctor requires ITreeObject; MockInStream is standalone.
        if (text == "NavInStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInStream"));

        // NavOutStream -> MockOutStream
        // NavOutStream's ctor requires ITreeObject; MockOutStream is standalone.
        if (text == "NavOutStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockOutStream"));

        // ALStream -> MockStream
        // ALStream's static methods operate on INavStreamReader/INavStreamWriter
        // which require session infrastructure. MockStream works with MockInStream/MockOutStream.
        if (text == "ALStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockStream"));

        // NavComplexValue -> object
        // The BC compiler uses NavComplexValue as a base type for complex
        // value parameters (RecordRef, Variant, etc.). MockVariant and
        // MockRecordRef don't extend the BC-internal NavComplexValue class,
        // so we replace it with object to allow any mock type to be passed.
        if (text == "NavComplexValue")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
                .WithTriviaFrom(node);

        // NavScope -> object
        // The BC compiler adds a hidden NavScope γReturnValueParent parameter
        // to methods that return a Record or Interface. The parameter is used
        // for ownership tracking. After rewriting, scope classes extend AlScope
        // (not NavScope), so direct same-codeunit calls fail with CS1503. We
        // replace NavScope with object so any scope or null can be passed.
        if (text == "NavScope")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
                .WithTriviaFrom(node);

        // NavVariant -> MockVariant (Variant in AL needs Default/ALAssign methods)
        if (text == "NavVariant")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockVariant"));

        // NavTextConstant -> NavText (avoid BC runtime initialization)
        if (text == "NavTextConstant")
            return node.WithIdentifier(SyntaxFactory.Identifier("NavText"));

        // NavDialog (instance type) -> MockDialog (for dialog/progress window objects)
        // Note: NavDialog static calls (ALMessage, ALError) are handled in VisitInvocationExpression
        if (text == "NavDialog")
        {
            // Check if this is in a type context (field/variable declaration, generic arg, etc.)
            // vs a static member access (NavDialog.ALMessage) which should stay as "NavDialog"
            var parent = node.Parent;
            if (parent is MemberAccessExpressionSyntax ma && ma.Expression == node)
            {
                // NavDialog.ALMessage -- keep as-is, handled by invocation rewriter
                // But for NavDialog.ALError, NavDialog.ALMessage these go through AlDialog
                // Actually check: is this a static method call?
                var methodName = ma.Name.Identifier.Text;
                if (methodName == "ALMessage" || methodName == "ALError")
                    return base.VisitIdentifierName(node); // Keep NavDialog for static rewrite
            }
            return node.WithIdentifier(SyntaxFactory.Identifier("MockDialog"));
        }

        // NavBigText -> MockBigText
        // In BC 28+, NavBigText's static initializer loads Telemetry.Abstractions
        // which is unavailable outside the service tier, causing TypeInitializationException.
        if (text == "NavBigText")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockBigText"));

        // NavVersion -> MockVersion
        // The real NavVersion reaches into BC service-tier environment for formatting
        // and version parsing. MockVersion stores major/minor/build/revision in-memory.
        if (text == "NavVersion")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockVersion"));

        // NavTextBuilder -> MockTextBuilder (avoids NavEnvironment/TrappableOperationExecutor crashes)
        if (text == "NavTextBuilder")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTextBuilder"));

        // NavNotification -> MockNotification
        // NavNotification.ALSend/ALRecall/ALAddAction require NavSession and the BC
        // service tier. MockNotification stores state locally and makes I/O calls no-ops.
        if (text == "NavNotification")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockNotification"));

        // NavDataTransfer -> MockDataTransfer
        // NavDataTransfer constructor loads Microsoft.Dynamics.Nav.CodeAnalysis at runtime.
        // MockDataTransfer stores config but CopyRows/CopyFields are no-ops.
        if (text == "NavDataTransfer")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockDataTransfer"));

        // NavFilterPageBuilder -> MockFilterPageBuilder
        // FilterPageBuilder constructor loads BC service tier UI components unavailable
        // outside the service tier. MockFilterPageBuilder stores registrations in memory;
        // RunModal returns FormResult.OK without showing any dialog.
        if (text == "NavFilterPageBuilder")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFilterPageBuilder"));

        // NavSessionSettings -> MockSessionSettings
        // NavSessionSettings.ALInit() dereferences NavSession (null standalone).
        // MockSessionSettings stores settings in-memory; RequestSessionUpdate is a no-op.
        if (text == "NavSessionSettings")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockSessionSettings"));

        // NavWebServiceActionContext -> MockWebServiceActionContext
        // WebServiceActionContext is used in OData action handlers. The real type
        // ties into BC service-tier dispatch; the mock stores state in-memory.
        if (text == "NavWebServiceActionContext")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockWebServiceActionContext"));

        // NavEventScope -> object (event scope type used for static fields)
        // Use PredefinedType to emit the C# keyword "object" properly, avoiding
        // namespace resolution issues where "object" as an IdentifierName fails.
        if (text == "NavEventScope")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));

        return base.VisitIdentifierName(node);
    }

    // -----------------------------------------------------------------------
    // Generic names: NavArray<MockRecordHandle> -> MockRecordArray
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;

        // NavArray<MockRecordHandle> -> MockRecordArray
        // NavArray<INavRecordHandle> -> MockRecordArray (INavRecordHandle already rewritten to MockRecordHandle)
        if (visited.Identifier.Text == "NavArray" &&
            visited.TypeArgumentList.Arguments.Count == 1)
        {
            var typeArg = visited.TypeArgumentList.Arguments[0].ToString();
            if (typeArg == "MockRecordHandle")
            {
                return SyntaxFactory.IdentifierName("MockRecordArray");
            }

            // NavArray<T> -> MockArray<T> for ALL types
            // NavArray requires ITreeObject (validates parent != null even in the simple ctor).
            // Use our MockArray<T> which provides 0-based indexing without ITreeObject.
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("MockArray"),
                visited.TypeArgumentList);
        }

        // NavObjectList<T> -> MockObjectList<T>
        // BC emits `List of [Interface X]` as NavObjectList<NavInterfaceHandle>, which
        // demands `T : ITreeObject` with a valid Tree handler — unavailable standalone.
        // MockObjectList<T> exposes the subset of the API the transpiler calls.
        if (visited.Identifier.Text == "NavObjectList" &&
            visited.TypeArgumentList.Arguments.Count == 1)
        {
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("MockObjectList"),
                visited.TypeArgumentList);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Object creation expressions
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        var typeText = visited.Type.ToString();

        // new NavRecordHandle(this, NNN, temp, SecurityFiltering.XXX) -> new MockRecordHandle(NNN, temp)
        // After identifier replacement, this is already MockRecordHandle
        if (typeText == "MockRecordHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            var tableIdArg = visited.ArgumentList.Arguments[1];
            var tempArg = visited.ArgumentList.Arguments[2];
            var newArgs = SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(tableIdArg.Expression),
                    SyntaxFactory.Argument(tempArg.Expression)
                }));
            return visited.WithArgumentList(newArgs);
        }

        // new MockCodeunitHandle(this, NNN) -> MockCodeunitHandle.Create(NNN)
        if (typeText == "MockCodeunitHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var codeunitId = visited.ArgumentList.Arguments[1].Expression;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("Create")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(codeunitId))));
            }
        }

        // new MockInterfaceHandle(this) -> new MockInterfaceHandle()
        // Strip the 'this' arg since MockInterfaceHandle doesn't need ITreeObject
        if (typeText == "MockInterfaceHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockFormHandle(this, pageId) -> new MockFormHandle(pageId)
        // Preserve the page id so the runtime can dispatch helper-procedure
        // calls on the Page<N> class via reflection.
        if (typeText == "MockFormHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1)
            {
                // Single-arg `new MockFormHandle(this)` (no page id known).
                // Fall back to parameterless ctor.
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockTestPageHandle(this, pageId) -> new MockTestPageHandle(pageId)
        // Same pattern as MockFormHandle: strip ITreeObject 'this', keep page ID.
        if (typeText == "MockTestPageHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1)
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockReportHandle(this, reportId) -> new MockReportHandle(reportId)
        // Same shape as page/test-page handles: strip ITreeObject parent, keep report ID.
        if (typeText == "MockReportHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1)
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockXmlPortHandle(this, xmlPortId) -> new MockXmlPortHandle(xmlPortId)
        // BC emits `new NavXmlPortHandle(this, id)` in scope-class field initializers.
        // Strip ITreeObject 'this', keep the XmlPort ID so the mock knows which port it is.
        if (typeText == "MockXmlPortHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                // BC emits new NavXmlPortHandle(this, xmlPortId) — strip ITreeObject 'this', keep ID.
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                // Single-arg form with 'this' (no ID) — strip the ITreeObject parent.
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockQueryHandle(this, queryId, SecurityFiltering) -> new MockQueryHandle(queryId, SecurityFiltering)
        // BC emits `new NavQueryHandle(this, queryId, SecurityFiltering.Filtered)` in scope-class
        // field initializers. Strip ITreeObject 'this', keep query ID and SecurityFiltering.
        if (typeText == "MockQueryHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 3)
            {
                // 3 args: (this, queryId, SecurityFiltering) → keep args [1] and [2].
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            visited.ArgumentList.Arguments[1],
                            visited.ArgumentList.Arguments[2]
                        })));
            }
            if (visited.ArgumentList.Arguments.Count == 2 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                // 2 args: (this, queryId) → keep arg [1].
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockRecordRef(this, ...) -> new MockRecordRef()
        // BC emits `new NavRecordRef(this, SecurityFiltering.Validated)` (or
        // variants with table id / company / temp flag) at scope-class init time.
        // The stub has no ITreeObject dependency — strip all constructor args.
        if (typeText == "MockRecordRef")
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockHttp*(this) -> new MockHttp*()
        // BC emits `new NavHttpClient(this)`, `new NavHttpResponseMessage(this)`,
        // `new NavHttpContent(this)`, `new NavHttpRequestMessage(this)` in
        // scope-class field initialisers. All take a single ITreeObject parent
        // whose .Tree must not be null. Strip it — the mocks are parameterless.
        if (typeText is "MockHttpClient" or "MockHttpResponseMessage"
            or "MockHttpContent" or "MockHttpRequestMessage" or "MockCookie"
            or "MockTestHttpResponseMessage" or "MockTestHttpRequestMessage"
            && visited.ArgumentList?.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockObjectList<T>(this) -> new MockObjectList<T>()
        // After VisitGenericName, NavObjectList has already been renamed.
        // MockObjectList doesn't need the ITreeObject parent.
        if (typeText.StartsWith("MockObjectList<") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockDialog(this) -> new MockDialog()
        // After identifier replacement, NavDialog is now MockDialog.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockDialog" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockFilterPageBuilder(this) -> new MockFilterPageBuilder()
        // BC emits `new NavFilterPageBuilder(this)` in scope-class field initialisers.
        // The mock is parameterless — strip the ITreeObject parent.
        if (typeText == "MockFilterPageBuilder" && visited.ArgumentList?.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockWebServiceActionContext(this) -> new MockWebServiceActionContext()
        // BC may emit `new NavWebServiceActionContext(this)` in scope-class field initialisers.
        // The mock is parameterless — strip the ITreeObject parent.
        if (typeText == "MockWebServiceActionContext" && visited.ArgumentList?.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new NavCode(maxLen, value) -> AlCompat.CreateNavCode(maxLen, value)
        // NavCode constructor calls EnsureValueIsUppercasedIfNeeded() which triggers NavEnvironment on Linux.
        // AlCompat.CreateNavCode pre-uppercases the string to avoid NavEnvironment access.
        if (typeText == "NavCode" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var maxLenArg = visited.ArgumentList.Arguments[0].Expression;
            var valueArg = visited.ArgumentList.Arguments[1].Expression;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CreateNavCode")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(maxLenArg),
                        SyntaxFactory.Argument(valueArg)
                    })));
        }

        // new NavTextConstant(langIds, strings, null, null) -> new NavText(strings[0])
        // After VisitIdentifierName, NavTextConstant is already renamed to NavText
        // NavTextConstant triggers NavEnvironment initialization; replace with simple NavText
        if ((typeText == "NavTextConstant" || typeText == "NavText") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the string array: new string[] { "the text" }
            var stringsArg = visited.ArgumentList.Arguments[1].Expression;
            if (stringsArg is ImplicitArrayCreationExpressionSyntax implArr && implArr.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(implArr.Initializer.Expressions[0]))));
            }
            if (stringsArg is ArrayCreationExpressionSyntax arrCreate && arrCreate.Initializer != null && arrCreate.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(arrCreate.Initializer.Expressions[0]))));
            }
        }

        // new MockFieldRef(this) -> new MockFieldRef()
        // After VisitIdentifierName, NavFieldRef is now MockFieldRef.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockFieldRef" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockKeyRef(this, ...) -> new MockKeyRef()
        // After VisitIdentifierName, NavKeyRef is now MockKeyRef.
        // Strip all constructor args (ITreeObject dependency).
        if (typeText == "MockKeyRef" && visited.ArgumentList != null)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new NavArray<MockRecordHandle>(new MockRecordHandle.Factory2(this, tableId, false, SecurityFiltering.X), N)
        // -> new MockRecordArray(tableId, N)
        // Also matches after GenericName rewrite: new MockRecordArray(new MockRecordHandle.Factory2(...), N)
        // NavArray<T> requires IFactory<T> which is internal; use our MockRecordArray instead
        if ((typeText.StartsWith("NavArray<") || typeText == "MockRecordArray") &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;

            // Extract the table ID from the Factory2 constructor call
            if (factoryArg is ObjectCreationExpressionSyntax factoryCreation &&
                factoryCreation.Type.ToString().Contains("Factory2") &&
                factoryCreation.ArgumentList != null)
            {
                // Factory2(this, tableId, false, SecurityFiltering.X) — tableId is arg[1]
                // or Factory2(tableId, false, SecurityFiltering.X) — tableId is arg[0]
                var factoryArgs = factoryCreation.ArgumentList.Arguments;
                ExpressionSyntax? tableIdExpr = null;
                if (factoryArgs.Count >= 4)
                    tableIdExpr = factoryArgs[1].Expression; // (this, tableId, ...)
                else if (factoryArgs.Count >= 1)
                    tableIdExpr = factoryArgs[0].Expression; // (tableId, ...)

                if (tableIdExpr != null)
                {
                    return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("MockRecordArray"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(new[] {
                                SyntaxFactory.Argument(tableIdExpr),
                                SyntaxFactory.Argument(sizeArg)
                            })));
                }
            }
        }

        // new MockArray<MockVariant>(new NavVariant.Factory(...), N) -> new MockArray<MockVariant>(N, () => new MockVariant())
        // NavArray with Factory pattern: replace Factory-based construction with lambda-based MockArray
        if (typeText.StartsWith("MockArray<MockVariant>") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;
            if (factoryArg is ObjectCreationExpressionSyntax)
            {
                // new MockArray<MockVariant>(size, () => new MockVariant())
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("MockArray<MockVariant>"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.Argument(sizeArg),
                            SyntaxFactory.Argument(
                                SyntaxFactory.ParenthesizedLambdaExpression(
                                    SyntaxFactory.ObjectCreationExpression(
                                        SyntaxFactory.ParseTypeName("MockVariant"))
                                        .WithArgumentList(SyntaxFactory.ArgumentList())))
                        })));
            }
        }

        // Catch any remaining MockVariant.Factory or NavVariant.Factory construction
        if (typeText.Contains("MockVariant") && typeText.Contains("Factory") && visited.ArgumentList != null)
        {
            // This shouldn't be reached anymore but kept as safety net
            return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName("NavVariant.Factory"))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("MockVariant"),
                                SyntaxFactory.IdentifierName("StubTreeObject"))))));
        }

        // new NavArray<T>(this, defaultValue, size) -> new MockArray<T>(defaultValue, size)
        // Drop the ITreeObject 'this' argument from NavArray/MockArray constructors.
        // After generic name rewrite, NavArray<T> becomes MockArray<T>.
        // MockArray<T>(T initValue, params int[] dimensions) matches the remaining args.
        if ((typeText.StartsWith("MockArray") || typeText.StartsWith("NavArray")) &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count >= 3)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                for (int i = 1; i < visited.ArgumentList.Arguments.Count; i++)
                    newArgs = newArgs.Add(visited.ArgumentList.Arguments[i]);
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
            }
        }

        // NOTE: We no longer strip 'this' from scope constructor calls.
        // Scope constructors now keep the βparent parameter and store it as _parent.
        // AlScope implements ITreeObject, so 'this' is a valid non-null ITreeObject
        // for any Nav*/AL* type constructor — no CS1503 and no null-check failures.

        return visited;
    }

    // -----------------------------------------------------------------------
    // Invocation expressions: NavDialog, StmtHit, CStmtHit, NavRuntimeHelpers, ALCompiler
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Keep StmtHit(N) and CStmtHit(N) calls for coverage tracking.
        // They call through to AlScope which tracks hit statements.
        if (node.Expression is IdentifierNameSyntax stmtIdent &&
            (stmtIdent.Identifier.Text == "StmtHit" || stmtIdent.Identifier.Text == "CStmtHit"))
        {
            return base.VisitInvocationExpression(node);
        }

        // ALDatabase.ALSessionID() -> MockSession.GetSessionId()
        // BC lowers SessionId() to a 0-argument static method call on ALDatabase.
        // This must be caught before base.VisitInvocationExpression because the MemberAccess
        // visitor would otherwise replace the callee with MockSession.GetSessionId() and produce
        // the invalid double-call form MockSession.GetSessionId()().
        if (node.Expression is MemberAccessExpressionSyntax sessionMa &&
            sessionMa.Expression is IdentifierNameSyntax sessionDbIdent &&
            sessionDbIdent.Identifier.Text == "ALDatabase" &&
            (sessionMa.Name.Identifier.Text == "ALSessionID" || sessionMa.Name.Identifier.Text == "ALSessionId"))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("MockSession"),
                    SyntaxFactory.IdentifierName("GetSessionId")),
                SyntaxFactory.ArgumentList())
                .WithTriviaFrom(node);
        }

        // ALDatabase.ALTenantID() / ALDatabase.ALSerialNumber() / ALDatabase.ALServiceInstanceID()
        // BC emits these as method calls on ALDatabase; the real implementations require
        // NavSession and crash with NullReferenceException standalone. Catch the invocation
        // before recursion so the member-access rewrite in VisitMemberAccessExpression doesn't
        // produce an invalid double-call (e.g. ""()).
        // Stubs return fixed non-empty/non-zero values so consumers that branch on "has tenant
        // context" see a consistent affirmative — SerialNumber and TenantId are opaque opaque
        // identifiers (callers just need stability); ServiceInstanceId is traditionally 1.
        if (node.Expression is MemberAccessExpressionSyntax dbIdMa &&
            dbIdMa.Expression is IdentifierNameSyntax dbIdIdent &&
            dbIdIdent.Identifier.Text == "ALDatabase")
        {
            var idName = dbIdMa.Name.Identifier.Text;
            if (idName == "ALTenantID" || idName == "ALSerialNumber")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal("STANDALONE"))
                    .WithTriviaFrom(node);
            }
            if (idName == "ALServiceInstanceID" || idName == "ALServiceInstanceId")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(1))
                    .WithTriviaFrom(node);
            }
            // ALDatabase.ALSID() / ALDatabase.ALSid() / ALDatabase.ALGetSID()
            // Database.SID() requires NavSession (crashes with NullReferenceException standalone).
            // Return fixed non-real SID string. Intercepted here (before base) to avoid
            // double-call form and to handle session argument mismatch.
            // Both ALSID and ALSid checked for BC version compatibility.
            if (idName == "ALSID" || idName == "ALSid" || idName == "ALGetSID"
                || idName == "ALGetSid")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal("S-1-0-0"))
                    .WithTriviaFrom(node);
            }
        }

        // NavALErrorInfo.ALCreate(msg, ...) → AlScope.CreateErrorInfo(msg)
        // ALCreate calls UpdateWithRecordInfo(NavRecord record) which loads
        // Microsoft.Dynamics.Nav.CodeAnalysis at runtime — unavailable standalone.
        // Intercept before base.Visit so we control argument handling directly.
        if (node.Expression is MemberAccessExpressionSyntax eiCreateMa &&
            eiCreateMa.Expression is IdentifierNameSyntax eiIdent &&
            eiIdent.Identifier.Text == "NavALErrorInfo" &&
            eiCreateMa.Name.Identifier.Text == "ALCreate")
        {
            if (node.ArgumentList.Arguments.Count >= 1)
            {
                // ALCreate(message, ...) — pass the message to the safe factory
                var msgArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[0].Expression)!;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CreateErrorInfo")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(msgArg))))
                    .WithTriviaFrom(node);
            }
            else
            {
                // ALCreate() — no message, return default empty ErrorInfo
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CreateErrorInfo")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(node);
            }
        }

        // `NavOption.Create(existing.NavOptionMetadata, V)` — reassignment
        // pattern BC emits when an AL enum variable is re-assigned. Route
        // through AlCompat.CloneTaggedOption so the new instance inherits
        // the source enum-id tag.
        if (node.ArgumentList.Arguments.Count == 2 &&
            node.Expression is MemberAccessExpressionSyntax cloneCallMa &&
            cloneCallMa.Expression is IdentifierNameSyntax cloneCallIdent &&
            cloneCallIdent.Identifier.Text == "NavOption" &&
            cloneCallMa.Name.Identifier.Text == "Create" &&
            node.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax srcMetaMa &&
            srcMetaMa.Name.Identifier.Text == "NavOptionMetadata")
        {
            // Visit the captured sub-expressions so `.Target` strips and
            // other nested rewrites land on them before we embed them in
            // the outer helper call. Without this, an expression like
            // `this.r.Target.GetFieldValueSafe(...)` survives with the
            // `.Target` still attached and Roslyn rejects it later.
            var existingExpr = (ExpressionSyntax)Visit(srcMetaMa.Expression)!;
            var ordinalExpr = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CloneTaggedOption")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(existingExpr),
                    SyntaxFactory.Argument(ordinalExpr)
                })));
        }

        // Special-case: `NavOption.Create(NCLEnumMetadata.Create(N), V)`
        // must be rewritten to `AlCompat.CreateTaggedOption(N, V)` so the
        // NavOption instance remembers its source enum — later calls to
        // `.ALOrdinals` / `.ALNames` can then resolve it via the tagged
        // ConditionalWeakTable. Match on the pre-visit tree because the
        // inner NCLEnumMetadata.Create rewrite would otherwise erase N.
        if (node.ArgumentList.Arguments.Count == 2 &&
            node.Expression is MemberAccessExpressionSyntax optCreateMa &&
            optCreateMa.Expression is IdentifierNameSyntax optIdent &&
            optIdent.Identifier.Text == "NavOption" &&
            optCreateMa.Name.Identifier.Text == "Create" &&
            node.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax optMetaInv &&
            optMetaInv.Expression is MemberAccessExpressionSyntax optMetaMa &&
            optMetaMa.Expression is IdentifierNameSyntax optMetaIdent &&
            optMetaIdent.Identifier.Text == "NCLEnumMetadata" &&
            optMetaMa.Name.Identifier.Text == "Create" &&
            optMetaInv.ArgumentList.Arguments.Count == 1)
        {
            var enumIdArg = (ExpressionSyntax)Visit(optMetaInv.ArgumentList.Arguments[0].Expression)!;
            var ordinalArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CreateTaggedOption")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(enumIdArg),
                    SyntaxFactory.Argument(ordinalArg)
                })));
        }

        // Special-case: `NCLEnumMetadata.Create(N).GetOrdinals()` / `.Names()`
        // must be intercepted BEFORE recursing into children, because the
        // inner NCLEnumMetadata.Create rewrite would otherwise erase the
        // enum object ID N. Handle the original (pre-visit) pattern here.
        if (node.ArgumentList.Arguments.Count == 0 &&
            node.Expression is MemberAccessExpressionSyntax outerMa &&
            outerMa.Expression is InvocationExpressionSyntax innerInv &&
            innerInv.Expression is MemberAccessExpressionSyntax innerMa &&
            innerMa.Expression is IdentifierNameSyntax innerIdent &&
            innerIdent.Identifier.Text == "NCLEnumMetadata" &&
            innerMa.Name.Identifier.Text == "Create" &&
            innerInv.ArgumentList.Arguments.Count == 1)
        {
            var enumIdArg = innerInv.ArgumentList.Arguments[0].Expression;
            var outerMethod = outerMa.Name.Identifier.Text;
            string? helper = outerMethod switch
            {
                "GetOrdinals" => "GetEnumOrdinals",
                "GetNames" => "GetEnumNames",
                _ => null
            };
            if (helper is not null)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(helper)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(enumIdArg))));
            }
        }

        // Special-case: `NCLEnumMetadata.Create(N).FromInteger(I)` → `AlCompat.EnumFromInteger(N, I)`
        // BC emits this pattern for `Enum::"T".FromInteger(I)`. Must be intercepted before
        // recursing so the inner NCLEnumMetadata.Create(N) is not erased by the generic rewrite.
        // When the enum is known (non-extensible, declared in this compilation), valid ordinals
        // are inlined at rewrite time via `AlCompat.EnumFromIntegerValidated(N, I, new[]{...})`
        // so validation does not depend on EnumRegistry state at runtime.
        if (node.ArgumentList.Arguments.Count == 1 &&
            node.Expression is MemberAccessExpressionSyntax fiOuterMa &&
            fiOuterMa.Name.Identifier.Text == "FromInteger" &&
            fiOuterMa.Expression is InvocationExpressionSyntax fiInnerInv &&
            fiInnerInv.Expression is MemberAccessExpressionSyntax fiInnerMa &&
            fiInnerMa.Expression is IdentifierNameSyntax fiInnerIdent &&
            fiInnerIdent.Identifier.Text == "NCLEnumMetadata" &&
            fiInnerMa.Name.Identifier.Text == "Create" &&
            fiInnerInv.ArgumentList.Arguments.Count >= 1)
        {
            var enumIdArg = fiInnerInv.ArgumentList.Arguments[0].Expression;
            var ordinalArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[0].Expression)!;

            // Try to resolve the valid ordinals at rewrite time so the call
            // site carries its own validation and does not rely on EnumRegistry
            // state during test execution.
            int[]? validOrdinals = null;
            if (enumIdArg is LiteralExpressionSyntax litExpr &&
                litExpr.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                int enumIdInt = litExpr.Token.Value switch
                {
                    int i   => i,
                    long l  => (int)l,
                    uint u  => (int)u,
                    _       => -1
                };
                if (enumIdInt >= 0)
                {
                    var members = AlRunner.Runtime.EnumRegistry.GetMembers(enumIdInt);
                    if (members.Count > 0)
                        validOrdinals = members.Select(m => m.Ordinal).ToArray();
                }
            }

            if (validOrdinals != null)
            {
                // Inline validated: AlCompat.EnumFromIntegerValidated(N, I, new int[]{0,1,2,...})
                var arrayInit = SyntaxFactory.InitializerExpression(
                    SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList<ExpressionSyntax>(
                        validOrdinals.Select(o =>
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(o)))));
                var arrayCreation = SyntaxFactory.ImplicitArrayCreationExpression(arrayInit);
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("EnumFromIntegerValidated")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.Argument(enumIdArg),
                        SyntaxFactory.Argument(ordinalArg),
                        SyntaxFactory.Argument(arrayCreation)
                    })));
            }

            // Fallback (extensible or external enum): runtime registry lookup.
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("EnumFromInteger")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(enumIdArg),
                    SyntaxFactory.Argument(ordinalArg)
                })));
        }

        // NavRecordId.ALGetRecord(scope, target) -> new MockRecordRef()
        // BC emits `recId.ALGetRecord(this, CompilationTarget.OnPrem)` for RecordId.GetRecord().
        // NavRecordId.ALGetRecord reaches into BC runtime infrastructure that doesn't exist in
        // standalone mode and throws "Parent.Tree cannot be null". Return an unbound MockRecordRef.
        if (node.Expression is MemberAccessExpressionSyntax getRecordMa &&
            getRecordMa.Name.Identifier.Text == "ALGetRecord")
        {
            return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.IdentifierName("MockRecordRef"))
                .WithArgumentList(SyntaxFactory.ArgumentList())
                .WithTriviaFrom(node);
        }

        // XmlDocument node-manipulation methods: ALRemove / ALAddAfterSelf / ALAddBeforeSelf / ALReplaceWith.
        // NavXmlDocument.ALRemove() etc. call NavEnvironment (BC service-tier logging) which is
        // unavailable standalone and throws TypeInitializationException.
        // Redirect through AlCompat.XmlRemove/XmlAddAfterSelf/etc. which dispatch to the NavXmlNode
        // path when the receiver is a NavXmlNode (keeps existing XmlNode tests working) and are
        // no-ops for NavXmlDocument (standalone documents have no parent to manipulate).
        // Must be intercepted BEFORE base visit to avoid the method call being executed.
        if (node.Expression is MemberAccessExpressionSyntax xmlDocMa)
        {
            var xmlMethodName = xmlDocMa.Name.Identifier.Text;
            // ALRemove(DataError) — 1 argument, first is DataError
            if (xmlMethodName == "ALRemove" &&
                node.ArgumentList.Arguments.Count == 1 &&
                node.ArgumentList.Arguments[0].Expression.ToString().StartsWith("DataError"))
            {
                var receiverExpr = (ExpressionSyntax)Visit(xmlDocMa.Expression)!;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("XmlRemove")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.Argument(receiverExpr)
                    }))).WithTriviaFrom(node);
            }
            // ALAddAfterSelf(DataError, sibling) / ALAddBeforeSelf(DataError, sibling) / ALReplaceWith(DataError, node) — 2 args, first is DataError
            if (xmlMethodName is "ALAddAfterSelf" or "ALAddBeforeSelf" or "ALReplaceWith" &&
                node.ArgumentList.Arguments.Count == 2 &&
                node.ArgumentList.Arguments[0].Expression.ToString().StartsWith("DataError"))
            {
                var helperName = xmlMethodName switch
                {
                    "ALAddAfterSelf" => "XmlAddAfterSelf",
                    "ALAddBeforeSelf" => "XmlAddBeforeSelf",
                    _ => "XmlReplaceWith"
                };
                var receiverExpr = (ExpressionSyntax)Visit(xmlDocMa.Expression)!;
                var secondArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(helperName)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.Argument(receiverExpr),
                        SyntaxFactory.Argument(secondArg)
                    }))).WithTriviaFrom(node);
            }
        }

        // Now recurse into children first
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // `<expr>.ToText(...)` -> `AlCompat.Format(<expr>)` or `AlCompat.GuidToText(<expr>, false)`
        // BC lowers AL's `xVar.ToText()` to either an instance `navX.ToText(...)` call
        // or a static `ALCompiler.ToText(session, value)` call (the latter is handled
        // separately below). For instance calls the arguments vary by type and BC version:
        //  - `null!` (1 arg)  — most types (NavDateTime, NavDate, …); session passed as null!
        //  - real session ref (1 arg) — some types/versions pass an actual NavSession expression
        //  - session + format-number (2 args) — extended overloads on some BC versions
        // All forms end up calling NavValueFormatter → NCLManagedAdapter (OEM native code)
        // which fails without the BC service tier. AlCompat.Format handles every BC value
        // type without session access. We route the receiver (expr) through AlCompat.Format,
        // discarding all arguments since AlCompat.Format does not need them.
        // Special case: .ToText(false) with literal false → Guid no-delimiter form.
        // Exclude ALCompiler.ToText — that static form is handled separately below.
        if (visited.Expression is MemberAccessExpressionSyntax toTextMa &&
            toTextMa.Name.Identifier.Text == "ToText" &&
            !(toTextMa.Expression is IdentifierNameSyntax toTextId && toTextId.Identifier.Text == "ALCompiler"))
        {
            bool isFalseLiteralArg = visited.ArgumentList.Arguments.Count >= 1 &&
                visited.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression);
            if (isFalseLiteralArg)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("GuidToText")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.Argument(toTextMa.Expression),
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))
                        })))
                    .WithTriviaFrom(visited);
            }
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("Format")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(toTextMa.Expression))))
                .WithTriviaFrom(visited);
        }

        // `<expr>.ALToText(...)` → `AlCompat.GuidToText(<expr>, true/false)`
        // BC emits navGuid.ALToText() (with AL prefix) for Guid.ToText() in AL. NavGuid.ALToText()
        // returns format "D" (36 chars lowercase) but AL spec requires format "B" (38 chars, braces)
        // for the default and format "N" (32 chars) for ToText(false).
        // MockTextBuilder.ALToText() is also intercepted — GuidToText delegates non-Guid objects
        // to their own ALToText() to preserve correct BigText/TextBuilder behavior.
        if (visited.Expression is MemberAccessExpressionSyntax alToTextMa &&
            alToTextMa.Name.Identifier.Text == "ALToText")
        {
            bool isFalseArg = visited.ArgumentList.Arguments.Count >= 1 &&
                visited.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression);
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("GuidToText")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(alToTextMa.Expression),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            isFalseArg ? SyntaxKind.FalseLiteralExpression : SyntaxKind.TrueLiteralExpression))
                    })))
                .WithTriviaFrom(visited);
        }

        // NavCode.op_Equality(a, b) / NavCode.op_Inequality(a, b)
        // BC may emit explicit static operator calls for Code[N] comparisons in case statements
        // (rather than a binary == expression), depending on the BC version. Both forms need
        // the same NavEnvironment-free replacement.
        if (visited.ArgumentList.Arguments.Count == 2 &&
            visited.Expression is MemberAccessExpressionSyntax navCodeOpMa &&
            navCodeOpMa.Expression is IdentifierNameSyntax navCodeOpIdent &&
            navCodeOpIdent.Identifier.Text == "NavCode")
        {
            var opName = navCodeOpMa.Name.Identifier.Text;
            if (opName == "op_Equality" || opName == "op_Inequality")
            {
                var leftArg = visited.ArgumentList.Arguments[0].Expression;
                var rightArg = visited.ArgumentList.Arguments[1].Expression;
                var equalsCall = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavCodeEquals")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                            SyntaxFactory.Argument(leftArg),
                            SyntaxFactory.Argument(rightArg)
                        })));
                if (opName == "op_Inequality")
                    return SyntaxFactory.PrefixUnaryExpression(
                        SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.ParenthesizedExpression(equalsCall));
                return equalsCall;
            }
        }

        // <navCodeVar>.CompareTo(<arg>) — BC emits this for case statements on Code[N] fields.
        // NavCode.CompareTo → NavStringValue.CompareTo calls NavEnvironment (null standalone).
        // We intercept when the argument is AlCompat.CreateNavCode(...) (the case label after
        // our VisitObjectCreationExpression rewrite) and route through the safe NavCodeCompare.
        if (visited.ArgumentList.Arguments.Count == 1 &&
            visited.Expression is MemberAccessExpressionSyntax compareToMa &&
            compareToMa.Name.Identifier.Text == "CompareTo" &&
            IsCreateNavCodeCall(visited.ArgumentList.Arguments[0].Expression))
        {
            var receiver = compareToMa.Expression;
            var compareArg = visited.ArgumentList.Arguments[0].Expression;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("NavCodeCompare")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                        SyntaxFactory.Argument(receiver),
                        SyntaxFactory.Argument(compareArg)
                    })));
        }

        // NavText?.ToLowerInvariant() / ?.ToUpperInvariant() -> NavText?.ToString().ToLowerInvariant()
        // NavText implicitly converts to ReadOnlySpan<char>, which picks up the wrong
        // MemoryExtensions.ToLowerInvariant(ReadOnlySpan, Span) overload. Insert .ToString()
        // to force string.ToLowerInvariant() resolution.
        if (visited.Expression is MemberBindingExpressionSyntax memberBinding &&
            visited.ArgumentList.Arguments.Count == 0 &&
            (memberBinding.Name.Identifier.Text == "ToLowerInvariant" ||
             memberBinding.Name.Identifier.Text == "ToUpperInvariant"))
        {
            // x?.ToLowerInvariant() -> x?.ToString().ToLowerInvariant()
            var toStringCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("ToString")),
                SyntaxFactory.ArgumentList());
            return visited.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    toStringCall,
                    memberBinding.Name));
        }

        // x.ToLowerInvariant() / x.ToUpperInvariant() (non-conditional) -> x.ToString().ToLowerInvariant()
        if (visited.Expression is MemberAccessExpressionSyntax maToLower &&
            visited.ArgumentList.Arguments.Count == 0 &&
            (maToLower.Name.Identifier.Text == "ToLowerInvariant" ||
             maToLower.Name.Identifier.Text == "ToUpperInvariant"))
        {
            var toStringCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    maToLower.Expression,
                    SyntaxFactory.IdentifierName("ToString")),
                SyntaxFactory.ArgumentList());
            return visited.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    toStringCall,
                    maToLower.Name));
        }

        // NavDialog.ALMessage(this.Session, System.Guid.Parse("..."), fmt, args...)
        // -> AlDialog.Message(fmt, args...)
        if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var exprText = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            // NavDialog.ALConfirm(session, guid, question, [default]) -> MockDialog.ALConfirm(question, [default])
            if ((exprText == "NavDialog" || exprText == "MockDialog") && methodName == "ALConfirm")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip first two args (Session and Guid)
                if (args.Count >= 3)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockDialog"),
                            SyntaxFactory.IdentifierName("ALConfirm")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavDialog.ALMessage / NavDialog.ALError
            if (exprText == "NavDialog" && (methodName == "ALMessage" || methodName == "ALError"))
            {
                var newMethodName = methodName == "ALMessage" ? "Message" : "Error";
                var args = visited.ArgumentList.Arguments;

                // Skip first two args (this.Session and System.Guid.Parse("..."))
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlDialog"),
                            SyntaxFactory.IdentifierName(newMethodName)),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavRuntimeHelpers.CompilationError(...)
            if (exprText == "NavRuntimeHelpers" && methodName == "CompilationError")
            {
                // Replace with: throw new InvalidOperationException("Compilation error")
                // But this is an expression, we need to return an invocation.
                // The throw will be handled in VisitExpressionStatement.
                // Mark it for statement-level replacement by keeping it recognizable.
                return visited;
            }

            // NavCodeunit.RunCodeunit(DataError, id [, record]) -> MockCodeunitHandle.RunCodeunit(errorLevel, id [, record])
            // NavCodeunit.RunCodeunit is a static dispatch method requiring NavSession
            if (exprText == "NavCodeunit" && methodName == "RunCodeunit")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    // First argument is DataError (TrapError or ThrowError)
                    var errorLevelArg = args[0].Expression;
                    // The second argument is the codeunit ID
                    var codeunitIdArg = args[1].Expression;
                    if (args.Count >= 3)
                    {
                        // Third argument is the record — forward it so OnRun(MockRecordHandle) receives it
                        var recordArg = args[2].Expression;
                        return SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                                SyntaxFactory.IdentifierName("RunCodeunit")),
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                                    SyntaxFactory.Argument(errorLevelArg),
                                    SyntaxFactory.Argument(codeunitIdArg),
                                    SyntaxFactory.Argument(recordArg)
                                })));
                    }
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                            SyntaxFactory.IdentifierName("RunCodeunit")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                                SyntaxFactory.Argument(errorLevelArg),
                                SyntaxFactory.Argument(codeunitIdArg)
                            })));
                }
            }

            // NavXmlPort.Import(DataError, xmlPortId, stream [, rec]) -> MockXmlPortHandle.StaticImport(...)
            // NavXmlPort.Export(DataError, xmlPortId, stream [, rec]) -> MockXmlPortHandle.StaticExport(...)
            // BC emits these static calls for the short form: XmlPort.Import(XmlPort::"X", InStr).
            // NavXmlPort requires the service tier; forward to stub that throws NotSupportedException.
            if (exprText == "NavXmlPort" && (methodName == "Import" || methodName == "Export"))
            {
                var stubMethod = methodName == "Import" ? "StaticImport" : "StaticExport";
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockXmlPortHandle"),
                        SyntaxFactory.IdentifierName(stubMethod)),
                    visited.ArgumentList);
            }

            // NavReport.Run(reportId) -> MockReportHandle.StaticRun(reportId)
            // NavReport.RunModal(reportId) -> MockReportHandle.StaticRunModal(reportId)
            // BC emits these static calls for Report.Run(Report::"X") / Report.RunModal(Report::"X").
            // NavReport requires the service tier; forward to MockReportHandle which dispatches
            // to the ReportHandler if registered, or silently runs the report class.
            if (exprText == "NavReport")
            {
                string? stubMethod = methodName switch
                {
                    "Run"                     => "StaticRun",
                    "RunModal"                => "StaticRunModal",
                    "Execute"                 => "StaticExecute",
                    "Print"                   => "StaticPrint",
                    "SaveAs"                  => "StaticSaveAs",
                    "SaveAsPdf"               => "StaticSaveAsPdf",
                    "SaveAsWord"              => "StaticSaveAsWord",
                    "SaveAsExcel"             => "StaticSaveAsExcel",
                    "SaveAsHtml"              => "StaticSaveAsHtml",
                    "SaveAsXml"               => "StaticSaveAsXml",
                    "DefaultLayout"           => "StaticDefaultLayout",
                    "RdlcLayout"              => "StaticRdlcLayout",
                    "WordLayout"              => "StaticWordLayout",
                    "ExcelLayout"             => "StaticExcelLayout",
                    "GetSubstituteReportId"   => "StaticGetSubstituteReportId",
                    "RunRequestPage"          => "StaticRunRequestPage",
                    "ValidateAndPrepareLayout"=> "StaticValidateAndPrepareLayout",
                    "WordXmlPart"             => "StaticWordXmlPart",
                    _                         => null
                };
                if (stubMethod != null)
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockReportHandle"),
                            SyntaxFactory.IdentifierName(stubMethod)),
                        visited.ArgumentList);
            }

            // NavXmlPort.Run(portId [, showPage, showXml]) -> MockXmlPortHandle.StaticRun(...)
            // BC emits `NavXmlPort.Run(<id>)` for Xmlport.Run(Xmlport::"X"). No file I/O or
            // interactive target standalone — the stub completes as a no-op so caller
            // execution continues unaffected.
            if (exprText == "NavXmlPort" && methodName == "Run")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockXmlPortHandle"),
                        SyntaxFactory.IdentifierName("StaticRun")),
                    visited.ArgumentList);
            }

            // NavQuery.ALSaveAsCsv/ALSaveAsXml/ALSaveAsJson/ALSaveAsExcel -> MockQueryHandle statics
            // BC emits these static calls for Query.SaveAsCsv(queryId, ...) etc.
            // NavQuery requires the service tier; forward to MockQueryHandle stubs.
            if (exprText == "NavQuery" && (methodName == "ALSaveAsCsv" || methodName == "ALSaveAsXml"
                || methodName == "ALSaveAsJson" || methodName == "ALSaveAsExcel"))
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockQueryHandle"),
                        SyntaxFactory.IdentifierName(methodName)),
                    visited.ArgumentList);
            }

            // ALIsolatedStorage.ALSet/ALSetEncrypted/ALGet/ALContains/ALDelete -> MockIsolatedStorage
            // SetEncrypted is transparent in the mock — no real crypto needed for tests, and
            // the value must round-trip through Get/Contains like the plain ALSet path.
            if (exprText == "ALIsolatedStorage" &&
                (methodName == "ALSet" || methodName == "ALSetEncrypted"
                 || methodName == "ALGet" || methodName == "ALContains" || methodName == "ALDelete"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockIsolatedStorage"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // NavJsonToken/NavJsonObject/NavJsonArray/NavJsonValue: ALWriteTo, ALReadFrom,
            // ALSelectToken, ALSelectTokens, ALGetBoolean, and the object mutation/read methods
            // -> MockJsonHelper static methods.
            // These BC methods go through TrappableOperationExecutor -> NavEnvironment
            // which crashes in standalone mode. MockJsonHelper does the same work
            // using Newtonsoft.Json directly.
            // ALAsArray/ALAsObject/ALAsValue work natively via the BC runtime without going
            // through TrappableOperationExecutor — do NOT redirect them here.
            // ALAdd/ALGet/ALContains/ALRemove/ALReplace work natively (direct Newtonsoft mutation
            // or in-memory lookup — no TrappableOperationExecutor involved).
            // ALKeys/ALGetText/ALGetInteger/ALGetDecimal/ALGetObject/ALGetArray go through
            // TrappableOperationExecutor and crash in standalone mode — redirect those.
            // NOTE: do NOT add ALGet/ALContains/ALRemove/ALReplace here — those method names
            // also appear on record proxy classes and would cause a C# type mismatch if intercepted.
            // Guard: skip JSON intercepts when receiver is a NavXml* type.
            // NavXmlDocument.ALReadFrom() is a static XML factory method, not a JSON operation.
            // Without this guard the JSON ALReadFrom rule rewrites it to
            // MockJsonHelper.ReadFrom(NavXmlDocument, ...) which causes CS0119.
            if (!exprText.StartsWith("NavXml", StringComparison.Ordinal) &&
                methodName is "ALWriteTo" or "ALWriteWithSecretsTo" or "ALReadFrom" or "ALSelectToken" or "ALSelectTokens"
                or "ALGetBoolean" or "ALIsArray" or "ALIsObject" or "ALIsValue" or "ALClone"
                or "ALKeys"
                or "ALGetText" or "ALGetInteger" or "ALGetDecimal"
                or "ALGetObject" or "ALGetArray"
                or "ALWriteToYaml" or "ALReadFromYaml")
            {
                var helperMethod = methodName switch
                {
                    "ALWriteTo" => "WriteTo",
                    "ALWriteWithSecretsTo" => "WriteWithSecretsTo",
                    "ALReadFrom" => "ReadFrom",
                    "ALSelectToken" => "SelectToken",
                    "ALSelectTokens" => "SelectTokens",
                    "ALGetBoolean" => "GetBoolean",
                    "ALIsArray" => "IsArray",
                    "ALIsObject" => "IsObject",
                    "ALIsValue" => "IsValue",
                    "ALClone" => "Clone",
                    "ALKeys" => "Keys",

                    "ALGetText" => "GetText",
                    "ALGetInteger" => "GetInteger",
                    "ALGetDecimal" => "GetDecimal",
                    "ALGetObject" => "GetObject",
                    "ALGetArray" => "GetArray",
                    "ALWriteToYaml" => "WriteToYaml",
                    "ALReadFromYaml" => "ReadFromYaml",
                    _ => null
                };
                if (helperMethod is not null)
                {
                    // Rewrite: x.ALGetBoolean(args) -> MockJsonHelper.GetBoolean(x, args)
                    var args = visited.ArgumentList.Arguments;
                    var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    newArgs = newArgs.Add(SyntaxFactory.Argument(memberAccess.Expression));
                    foreach (var arg in args)
                        newArgs = newArgs.Add(arg);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockJsonHelper"),
                            SyntaxFactory.IdentifierName(helperMethod)),
                        SyntaxFactory.ArgumentList(newArgs));
                }
            }

            // NavForm static Page.* method calls — expression-level stubs.
            // Statement-level void calls are stripped to EmptyStatement in VisitExpressionStatement.
            // Expression-level (return value used) calls need a typed default:
            //   RunModal → default(FormResult)
            //   ObjectId → default(NavInteger)   (Page.ObjectId returns the page ID integer)
            //   LookupMode (get) → false          (handled in VisitMemberAccessExpression)
            // Accept the fully-qualified form too — older BC versions emit
            // `Microsoft.Dynamics.Nav.Runtime.NavForm.Run(...)`.
            if ((exprText == "NavForm" || exprText.EndsWith(".NavForm", StringComparison.Ordinal)))
            {
                if (methodName == "RunModal")
                {
                    return SyntaxFactory.DefaultExpression(
                        SyntaxFactory.ParseTypeName("FormResult"));
                }
                if (methodName == "ObjectId")
                {
                    // Page.ObjectId([withName]) returns the page object ID as NavInteger.
                    return SyntaxFactory.DefaultExpression(
                        SyntaxFactory.ParseTypeName("NavInteger"));
                }
                if (methodName == "GetBackgroundParameters")
                {
                    // Page.GetBackgroundParameters() — no real page session standalone;
                    // return an empty Dictionary of [Text, Text] default.
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.GenericName("NavDictionary")
                            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SeparatedList<TypeSyntax>(new[]
                                {
                                    SyntaxFactory.ParseTypeName("NavText"),
                                    SyntaxFactory.ParseTypeName("NavText"),
                                }))),
                        SyntaxFactory.IdentifierName("Default"));
                }
                // Run and all other void methods: will be stripped at statement level.
                return visited;
            }

            // NCLEnumMetadata.Create(N) -> NCLOptionMetadata.Default
            // NCLEnumMetadata.Create goes through NavGlobal.MetadataProvider -> NavEnvironment
            // NCLOptionMetadata.Default creates a simple default metadata without NavGlobal access
            if (exprText == "NCLEnumMetadata" && methodName == "Create")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("NCLOptionMetadata"),
                    SyntaxFactory.IdentifierName("Default"));
            }

            // ALSession.ALStartSession(...) -> MockSession.ALStartSession(...)
            // ALSession.ALIsSessionActive(...) -> MockSession.ALIsSessionActive(...)
            // ALSession.ALStopSession(...) -> MockSession.ALStopSession(...)
            // Session management APIs require a live BC service tier. MockSession dispatches
            // StartSession synchronously via MockCodeunitHandle (same pattern as Codeunit.Run).
            if (exprText == "ALSession" &&
                (methodName == "ALStartSession" || methodName == "ALIsSessionActive" || methodName == "ALStopSession"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockSession"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // NavSession.Sleep(ms) -> MockSession.Sleep(ms)
            // Sleep requires NavSession which doesn't exist in standalone mode.
            if (exprText == "NavSession" && methodName == "Sleep")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockSession"),
                        SyntaxFactory.IdentifierName("Sleep")));
            }

            // ErrorCollection.ALClearCollectedErrors() -> AlScope.ClearCollectedErrors()
            // ErrorCollection.ALGetCollectedErrors(bool) -> AlScope.GetCollectedErrors(bool)
            // The real ErrorCollection depends on NavSession; redirect to AlScope's
            // thread-static collected-errors list.
            if ((exprText == "ErrorCollection" || exprText.EndsWith(".ErrorCollection", StringComparison.Ordinal)) &&
                (methodName == "ALClearCollectedErrors" || methodName == "ALGetCollectedErrors"))
            {
                var targetMethod = methodName == "ALClearCollectedErrors"
                    ? "ClearCollectedErrors"
                    : "GetCollectedErrors";
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlScope"),
                        SyntaxFactory.IdentifierName(targetMethod)));
            }

            // ALSessionInformation.GetALCallstack(session) -> ""
            // The real implementation dereferences NavSession (null standalone).
            if (exprText == "ALSessionInformation" && methodName == "GetALCallstack")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(""))
                    .WithTriviaFrom(visited);
            }

            // ALTaskScheduler.ALCreateTask/ALTaskExists/ALCancelTask/ALSetTaskReady -> MockTaskScheduler
            // TaskScheduler APIs require the BC service tier. MockTaskScheduler dispatches
            // CreateTask synchronously via MockCodeunitHandle (same pattern as MockSession).
            if (exprText == "ALTaskScheduler" &&
                (methodName == "ALCreateTask" || methodName == "ALTaskExists" ||
                 methodName == "ALCancelTask" || methodName == "ALSetTaskReady"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockTaskScheduler"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // ALSession.ALApplicationIdentifier(session) -> ""
            // Real implementation returns the BC app identifier; standalone returns empty.
            if (exprText == "ALSession" && methodName == "ALApplicationIdentifier")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(""))
                    .WithTriviaFrom(visited);
            }

            // ALSession.ALApplicationArea(session) -> AlCompat.ApplicationArea()
            // Requires NavSession which doesn't exist in standalone mode.
            if (exprText == "ALSession" && methodName == "ALApplicationArea")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ApplicationArea")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(visited);
            }

            // ALSession.ALGetCurrentExecutionMode(session) -> ExecutionMode.Standard
            // Real implementation requires a NavSession (null in standalone).
            if (exprText == "ALSession" && methodName == "ALGetCurrentExecutionMode")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("ExecutionMode"),
                    SyntaxFactory.IdentifierName("Standard"))
                    .WithTriviaFrom(visited);
            }

            // ALSession.ALGetExecutionContext(session) / ALGetModuleExecutionContext(session)
            // -> AlCompat.GetExecutionContext()
            if (exprText == "ALSession" &&
                (methodName == "ALGetExecutionContext" || methodName == "ALGetModuleExecutionContext" || methodName == "ALGetCurrentModuleExecutionContext"))
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("GetExecutionContext")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(visited);
            }

            // ALCompanyProperty.ALDisplayName() / ALUrlName() -> AlCompat stubs
            // Real BC implementation requires NavEnvironment which crashes standalone.
            if (exprText == "ALCompanyProperty" && methodName == "ALDisplayName")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CompanyPropertyDisplayName")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(visited);
            }
            if (exprText == "ALCompanyProperty" && methodName == "ALUrlName")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CompanyPropertyUrlName")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(visited);
            }
            if (exprText == "ALCompanyProperty" && methodName == "ALId")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CompanyPropertyID")),
                    SyntaxFactory.ArgumentList())
                    .WithTriviaFrom(visited);
            }

            // ALSystemDate.ALRoundDateTime(session, dt, [precision], [direction])
            // -> AlCompat.RoundDateTime(dt, [precision], [direction])
            // Strip the session arg; real BC impl requires NavSession.
            if (exprText == "ALSystemDate" && methodName == "ALRoundDateTime")
            {
                var args = visited.ArgumentList.Arguments;
                var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                for (int i = 1; i < args.Count; i++)
                    keptArgs = keptArgs.Add(args[i]);
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("RoundDateTime")),
                    SyntaxFactory.ArgumentList(keptArgs))
                    .WithTriviaFrom(visited);
            }

            // ALSystemDate.ALNormalDate(date) -> AlCompat.NormalDate(date)
            // Real BC impl throws NavNCLDateInvalidException on 0D.
            if (exprText == "ALSystemDate" && methodName == "ALNormalDate")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NormalDate")));
            }

            // ALSystemDate.ALClosingDate(date) -> AlCompat.ClosingDate(date)
            // Real BC impl throws on 0D.
            if (exprText == "ALSystemDate" && methodName == "ALClosingDate")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ClosingDate")));
            }

            // ALSystemDate.ALDMY2Date(session, day, month, year) → AlCompat.DMY2Date(day, month, year)
            // ALSystemDate.ALDWY2Date(session, day, week, year) → AlCompat.DWY2Date(day, week, year)
            // ALSystemDate.ALVariant2Date(session, v) → AlCompat.Variant2Date(v)
            // ALSystemDate.ALVariant2Time(session, v) → AlCompat.Variant2Time(v)
            // ALSystemDate.ALDaTi2Variant(scope, d, t) → AlCompat.DaTi2Variant(d, t)
            // Strip the first (session/scope) arg; these are pure date-arithmetic operations.
            // ALVariant2Date/Time require interception because v is MockVariant, not NavVariant.
            // ALDaTi2Variant requires interception because scope is AlScope, not NavMethodScope.
            if (exprText == "ALSystemDate" && methodName is "ALDMY2Date" or "ALDWY2Date"
                    or "ALVariant2Date" or "ALVariant2Time" or "ALDaTi2Variant")
            {
                var args = visited.ArgumentList.Arguments;
                var compatName = methodName switch
                {
                    "ALDMY2Date" => "DMY2Date",
                    "ALDWY2Date" => "DWY2Date",
                    "ALVariant2Date" => "Variant2Date",
                    "ALVariant2Time" => "Variant2Time",
                    "ALDaTi2Variant" => "DaTi2Variant",
                    _ => methodName
                };
                // Strip first arg (session/scope), keep the rest
                var keptArgs = SyntaxFactory.SeparatedList<ArgumentSyntax>();
                for (int i = 1; i < args.Count; i++)
                    keptArgs = keptArgs.Add(args[i]);
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(compatName)),
                    SyntaxFactory.ArgumentList(keptArgs))
                    .WithTriviaFrom(visited);
            }

            // ALSystemEncryption.ALEncryptionEnabled() → false  (no key in standalone runner)
            // ALSystemEncryption.ALKeyExists()         → false
            // ALSystemEncryption.ALEncrypt(text)       → return text unchanged (encryption not enabled)
            // ALSystemEncryption.ALDecrypt(text)       → return text unchanged
            // ALSystemEncryption.ALCreateEncryptionKey() → no-op (void)
            // ALSystemEncryption.ALDeleteEncryptionKey() → no-op (void)
            // All of these go through NavSession/NavEnvironment which crashes in standalone mode.
            if (exprText == "ALSystemEncryption")
            {
                return methodName switch
                {
                    "ALEncryptionEnabled" or "ALKeyExists" =>
                        SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                            .WithTriviaFrom(visited),
                    "ALEncrypt" or "ALDecrypt" when visited.ArgumentList.Arguments.Count >= 1 =>
                        // Return the plaintext arg unchanged — stub: no real encryption
                        visited.ArgumentList.Arguments[0].Expression,
                    "ALCreateEncryptionKey" or "ALDeleteEncryptionKey"
                        or "ALImportEncryptionKey" or "ALExportEncryptionKey" =>
                        // Void methods: emit a no-op expression (0)
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(0)),
                    _ => visited
                };
            }

            // ALCompiler.ToSecretText(navText) -> AlCompat.ToSecretText(navText)
            // ToSecretText wraps a text value as NavSecretText; requires NavSession in BC.
            if (exprText == "ALCompiler" && methodName == "ToSecretText")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToSecretText")));
            }

            // NavValueFormatter.Format(session, value, length, formatNumber, formatSettings)
            // -> AlCompat.Format(value)
            // BC's NavValueFormatter.Format routes through NCLManagedAdapter for byte/char types
            // (and other types via NavSession). NCLManagedAdapter requires native OEM DLLs that
            // fail to initialize without the BC service tier. AlCompat.Format handles all BC value
            // types without NavSession. We take the second argument (value) and discard the rest.
            if (exprText == "NavValueFormatter" && methodName == "Format"
                && visited.ArgumentList.Arguments.Count >= 2)
            {
                var valueArg = visited.ArgumentList.Arguments[1];
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("Format")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(valueArg)))
                    .WithTriviaFrom(visited);
            }

            // ALCompiler.ToText(session, value[, withBraces]) -> AlCompat.Format(value) or AlCompat.GuidToText(value, false)
            // BC lowers `byteVar.ToText()` (and other scalar ToText calls) to the static
            // ALCompiler.ToText(session, value) form. We take the second argument (value).
            // 3-arg form: ALCompiler.ToText(session, navGuid, false) for Guid.ToText(false) →
            //   3rd arg literal false = no-delimiter form → GuidToText(value, false).
            if (exprText == "ALCompiler" && methodName == "ToText"
                && visited.ArgumentList.Arguments.Count >= 2)
            {
                var valueArg = visited.ArgumentList.Arguments[1];
                bool isFalseThirdArg = visited.ArgumentList.Arguments.Count >= 3 &&
                    visited.ArgumentList.Arguments[2].Expression.IsKind(SyntaxKind.FalseLiteralExpression);
                if (isFalseThirdArg)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("GuidToText")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(new[] {
                                valueArg,
                                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))
                            })))
                        .WithTriviaFrom(visited);
                }
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("Format")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(valueArg)))
                    .WithTriviaFrom(visited);
            }

            // ALCompiler.ToNavValue(x) -> AlCompat.ToNavValue(x)
            // ToNavValue chains through NavValueFormatter -> NavSession -> NavEnvironment
            if (exprText == "ALCompiler" && methodName == "ToNavValue")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToNavValue")));
            }

            // ALCompiler.ObjectToExactNavValue<T>(x) -> (T)(object)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactNavValue")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                // Extract T from the generic method name
                if (memberAccess.Name is GenericNameSyntax genericName &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var targetType = genericName.TypeArgumentList.Arguments[0];
                    return SyntaxFactory.CastExpression(
                        targetType,
                        SyntaxFactory.ParenthesizedExpression(
                            SyntaxFactory.CastExpression(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                                SyntaxFactory.ParenthesizedExpression(arg))));
                }
            }

            // ALCompiler.ObjectToDecimal -> AlCompat.ObjectToDecimal
            // ObjectToDecimal accesses NavSession for culture-aware parsing; our version is simpler.
            if (exprText == "ALCompiler" && methodName == "ObjectToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.ToInterface(this, codeunit) -> MockInterfaceHandle.Wrap(codeunit).
            // BC's generated code uses this to push a codeunit into a position that
            // expects NavInterfaceHandle (e.g. a NavObjectList<NavInterfaceHandle> entry).
            // Wrapping preserves the boxing so the target's T constraint is satisfied;
            // consumers that only need the raw implementation already unwrap via
            // MockInterfaceHandle.ALAssign / InvokeInterfaceMethod.
            if (exprText == "ALCompiler" && methodName == "ToInterface")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockInterfaceHandle"),
                            SyntaxFactory.IdentifierName("Wrap")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(args[1].Expression))));
                }
            }

            // ALCompiler.ObjectToExactINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.NavIndirectValueToDecimal(x) -> AlCompat.ObjectToDecimal(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.NavIndirectValueToBoolean(x) -> AlCompat.NavIndirectValueToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToBoolean")));
            }

            // ALCompiler.NavIndirectValueToInt32(x) -> AlCompat.NavIndirectValueToInt32(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToInt32")));
            }

            // ALCompiler.NavIndirectValueToNavValue<T>(x) -> AlCompat.NavIndirectValueToNavValue<T>(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToNavValue")
            {
                // Preserve the generic type arguments (e.g., <NavText>)
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        memberAccess.Name)); // keep GenericNameSyntax with type args
            }

            // ALCompiler.NavIndirectValueToINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.ToVariant(this, value) -> AlCompat.ToVariant(value)
            // ALCompiler.NavValueToVariant(this, value) -> AlCompat.ToVariant(value)
            if (exprText == "ALCompiler" && (methodName == "ToVariant" || methodName == "NavValueToVariant"))
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this' argument (ITreeObject)
                if (args.Count >= 2)
                {
                    var valueArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(valueArg)));
                }
            }

            // NavInStream.Default(this) -> NavInStream.Default(null!)
            // NavOutStream.Default(this) -> NavOutStream.Default(null!)
            // NavFieldRef.Default(this) -> NavFieldRef.Default(null!)
            // These BC types require ITreeObject but work with null in standalone mode.
            // MockFieldRef.Default(this) -> MockFieldRef.Default()
            // After VisitIdentifierName, NavFieldRef is now MockFieldRef.
            // MockFieldRef.Default() takes no args — strip entirely.
            if (exprText == "MockFieldRef" && methodName == "Default")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1 && args[0].Expression.ToString() == "this")
                {
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList());
                }
            }

            // MockInStream.Default(this) -> MockInStream.Default()
            // MockOutStream.Default(this) -> MockOutStream.Default()
            // After VisitIdentifierName, NavInStream/NavOutStream are now MockInStream/MockOutStream.
            // MockInStream/MockOutStream.Default() takes optional null parent — strip the arg.
            if ((exprText == "MockInStream" || exprText == "MockOutStream")
                && methodName == "Default")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1 && args[0].Expression.ToString() == "this")
                {
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList());
                }
            }

            // ALSystemString.ALLowercase(x) -> (x?.ToString().ToLowerInvariant() ?? "")
            // ALSystemString.ALUppercase(x) -> (x?.ToString().ToUpperInvariant() ?? "")
            // These BC runtime methods access NavEnvironment for CultureInfo, crashing on Linux.
            // We call .ToString() first to ensure the string overload is used, not the
            // MemoryExtensions.ToLowerInvariant(ReadOnlySpan, Span) overload from System.Memory.
            if (exprText == "ALSystemString" && (methodName == "ALLowercase" || methodName == "ALUppercase"))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    var netMethod = methodName == "ALLowercase" ? "ToLowerInvariant" : "ToUpperInvariant";
                    // Build: (x?.ToString().ToLowerInvariant() ?? "")
                    var arg = args[0].Expression;
                    var toStringCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberBindingExpression(
                            SyntaxFactory.IdentifierName("ToString")),
                        SyntaxFactory.ArgumentList());
                    var caseCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            toStringCall,
                            SyntaxFactory.IdentifierName(netMethod)),
                        SyntaxFactory.ArgumentList());
                    var nullCoalesce = SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.CoalesceExpression,
                            SyntaxFactory.ConditionalAccessExpression(arg, caseCall),
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(""))));
                    return nullCoalesce;
                }
            }

            // ALSystemString.ALStrSubstNo(fmt, arg1, arg2, ...) -> AlCompat.StrSubstNo(fmt, arg1, arg2, ...)
            // The real BC ALStrSubstNo routes each argument through NavValueFormatter.Format(NavSession, ...)
            // which crashes with NullReferenceException when NavSession is null (runner context).
            // AlCompat.StrSubstNo uses AlCompat.Format() instead, which handles all BC types without NavSession.
            if (exprText == "ALSystemString" && methodName == "ALStrSubstNo")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("StrSubstNo")));
            }

            // ALSystemString.ALSelectStr(n, s) -> AlCompat.SelectStr(n, s)
            // The real ALSelectStr calls NavSession for locale, crashing without NavSession.
            if (exprText == "ALSystemString" && methodName == "ALSelectStr")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("SelectStr")));
            }

            // ALSystemString.ALIncStr(s) -> AlCompat.IncStr(s)
            // The real ALIncStr calls NavValueFormatter, crashing without NavSession.
            if (exprText == "ALSystemString" && methodName == "ALIncStr")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("IncStr")));
            }

            // ALSystemString.ALConvertStr(s, from, to) -> AlCompat.ConvertStr(s, from, to)
            // The real ALConvertStr accesses NavSession for locale, crashing without NavSession.
            if (exprText == "ALSystemString" && methodName == "ALConvertStr")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ConvertStr")));
            }

            // ALSystemString.ALCopyStr(s, position) -> AlCompat.CopyStr(s, position)  [2-arg variant]
            // The 3-arg variant (s, position, length) is handled by the BC runtime natively.
            if (exprText == "ALSystemString" && methodName == "ALCopyStr"
                && visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CopyStr")));
            }

            // ALSystemString.ALSecretStrSubstNo(fmt, arg1, ...) -> AlCompat.SecretStrSubstNo(fmt, arg1, ...)
            // The BC runtime SecretStrSubstNo creates a NavSecretText that requires NavSession.
            // AlCompat.SecretStrSubstNo formats the string via StrSubstNo and wraps in NavSecretText.Create().
            if (exprText == "ALSystemString" && methodName == "ALSecretStrSubstNo")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("SecretStrSubstNo")));
            }

            // x.ALUnwrap() -> AlCompat.Unwrap(x)
            // BC 27.x+ NavSecretText.ALUnwrap() loads CodeAnalysis 16.4.x at runtime,
            // which is absent from the runner's DLL path. AlCompat.Unwrap uses reflection
            // to extract the string and returns NavText — the correct type for the assignment.
            if (methodName == "ALUnwrap" && visited.ArgumentList.Arguments.Count == 0)
            {
                var unwrapMa = visited.Expression as MemberAccessExpressionSyntax;
                if (unwrapMa != null)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Unwrap")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(unwrapMa.Expression))))
                        .WithTriviaFrom(visited);
                }
            }

            // ALSystemString.ALPadStr(source, length, filler) -> AlCompat.PadStr(source, length, filler)
            // Real BC impl validates length >= 0 and throws NavNCLOutsidePermittedRangeException on
            // negative length — but AL documents negative length as "left-pad". AlCompat.PadStr
            // implements the AL-level contract (negative = left-pad, truncates when source is longer).
            if (exprText == "ALSystemString" && methodName == "ALPadStr")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("PadStr")));
            }

            // ALDatabase.ALIsInWriteTransaction() -> false
            // The real ALIsInWriteTransaction calls NavSession.HasWriteTransaction() which crashes
            // with NullReferenceException when NavSession is null (runner context, no DB).
            if (exprText == "ALDatabase" && methodName == "ALIsInWriteTransaction")
            {
                return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                    .WithTriviaFrom(visited);
            }

            // ALDatabase.ALGetDefaultTableConnection(ct) -> "" (empty string)
            // Real impl requires NavSession to look up registered connections; the runner
            // has no external connections so the "default" is simply the empty string.
            if (exprText == "ALDatabase" && methodName == "ALGetDefaultTableConnection")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(""))
                    .WithTriviaFrom(visited);
            }

            // ALDatabase.ALUserSecurityId() -> AlCompat.UserSecurityId()
            // Real impl requires NavSession (crashes with NullReferenceException standalone).
            // AlCompat.UserSecurityId returns a fixed non-null Guid so stability is preserved
            // across reads within a test run.
            if (exprText == "ALDatabase" && methodName == "ALUserSecurityId")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("UserSecurityId")));
            }

            // ALDatabase.ALLastUsedRowVersion() -> 0L
            // ALDatabase.ALMinimumActiveRowVersion() -> 0L
            // BC lowers Database.LastUsedRowVersion / MinimumActiveRowVersion to method
            // calls on ALDatabase that require a live NavSession. The runner has no real
            // database — 0L (no rows ever written / no active transactions) is the
            // correct standalone value, and preserves the real BC invariant that
            // MinimumActiveRowVersion <= LastUsedRowVersion.
            if (exprText == "ALDatabase" &&
                (methodName == "ALLastUsedRowVersion" || methodName == "ALMinimumActiveRowVersion"))
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(0L))
                    .WithTriviaFrom(visited);
            }

            // NavMedia/MockMedia.ALGetDocumentUrl(mediaId) -> AlCompat.GetDocumentUrl(mediaId)
            // No BC Media service in standalone mode — return empty string stub.
            // Note: VisitIdentifierName already rewrites NavMedia→MockMedia before this rule runs,
            // so we must also check for MockMedia here.
            if ((exprText == "NavMedia" || exprText == "MockMedia") && methodName == "ALGetDocumentUrl")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("GetDocumentUrl")));
            }

            // NavMedia/MockMedia.ALImportWithUrlAccess(stream, filename, duration) -> AlCompat.ImportStreamWithUrlAccess(...)
            // No BC Media service in standalone mode — return empty string stub.
            // Note: VisitIdentifierName already rewrites NavMedia→MockMedia before this rule runs,
            // so we must also check for MockMedia here.
            if ((exprText == "NavMedia" || exprText == "MockMedia") && methodName == "ALImportWithUrlAccess")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ImportStreamWithUrlAccess")));
            }

            // ALSystemObject.ALCaptionClassTranslate(expr) -> AlCompat.CaptionClassTranslate(expr)
            // No caption class service in standalone mode — return input expression unchanged.
            if (exprText == "ALSystemObject" && methodName == "ALCaptionClassTranslate")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("CaptionClassTranslate")));
            }

            // ALDatabase.ALHasTableConnection(type, name) -> AlCompat.HasTableConnection(type, name)
            // The runner has no real external table connections; always returns false.
            if (exprText == "ALDatabase" && methodName == "ALHasTableConnection")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("HasTableConnection")));
            }


            // ALDatabase.ALCreateGuid() -> AlCompat.ALCreateGuid()
            // ALDatabase.ALCreateSequentialGuid() -> AlCompat.ALCreateSequentialGuid()
            // The real implementations require NavSession; ours use System.Guid.NewGuid().
            if (exprText == "ALDatabase" &&
                (methodName == "ALCreateGuid" || methodName == "ALCreateSequentialGuid"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // NavGuid.ALNewSequentialGuid() -> AlCompat.ALCreateSequentialGuid()
            // BC 26+ lowers Guid.CreateSequentialGuid() to NavGuid.ALNewSequentialGuid()
            // instead of the older ALDatabase.ALCreateSequentialGuid. Route both forms
            // to our AlCompat helper so the method exists across BC versions.
            if (exprText == "NavGuid" && methodName == "ALNewSequentialGuid")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ALCreateSequentialGuid")));
            }

            // ALDatabase.ALIsNullGuid(g) -> AlCompat.ALIsNullGuid(g)
            // The real implementation goes through NavSession; ours checks Guid.Empty.
            if (exprText == "ALDatabase" && methodName == "ALIsNullGuid")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ALIsNullGuid")));
            }

            // ALSystemDate.ALWorkDate(session)        → AlScope.GetWorkDate()
            // ALSystemDate.ALWorkDate(session, date)   → AlScope.SetWorkDate(date)
            // ALWorkDate requires NavSession which is null in standalone mode.
            // Route both forms through AlScope which holds an in-memory work date.
            if (exprText == "ALSystemDate" && methodName == "ALWorkDate")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    // Getter form: ALWorkDate(session) → AlScope.GetWorkDate()
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("GetWorkDate")),
                        SyntaxFactory.ArgumentList())
                        .WithTriviaFrom(visited);
                }
                if (args.Count == 2)
                {
                    // Setter form: ALWorkDate(session, date) → AlScope.SetWorkDate(date)
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("SetWorkDate")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(args[1])))
                        .WithTriviaFrom(visited);
                }
            }

            // ALCompiler.NavRecordToVariant(this, record) -> AlCompat.ToVariant(record)
            // Strip the ITreeObject first argument
            if (exprText == "ALCompiler" && methodName == "NavRecordToVariant")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    var recordArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(recordArg)));
                }
            }

            // ALCompiler.ObjectToInt32(x) -> Convert.ToInt32(x)
            // Simple numeric conversion
            if (exprText == "ALCompiler" && methodName == "ObjectToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("Convert"),
                        SyntaxFactory.IdentifierName("ToInt32")));
            }

            // ALCompiler.ObjectToBoolean(x) -> AlCompat.ObjectToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "ObjectToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToBoolean")));
            }

            // ALSystemArray.ALCompressArray(arr) -> AlCompat.ALCompressArray(arr)
            // ALSystemArray.ALCopyArray(dest, src, fromIdx, count) -> AlCompat.ALCopyArray(...)
            // ALSystemArray requires NavArray<T>; our MockArray<T> is not a NavArray<T>,
            // so C# cannot infer T and CS0411 is raised. Redirect to our generic helpers
            // that accept MockArray<T>.
            if (exprText == "ALSystemArray" && (methodName == "ALCompressArray" || methodName == "ALCopyArray"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // ALSystemNumeric.ALRandomize(seed) -> AlCompat.ALRandomize(seed)
            // ALSystemNumeric.ALRandom(max) -> AlCompat.ALRandom(max)
            // ALRandomize/ALRandom require NavSession; our versions use System.Random.
            if (exprText == "ALSystemNumeric" && (methodName == "ALRandomize" || methodName == "ALRandom"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // ALSystemNumeric.ALRound(v) single-arg -> AlCompat.ALRound(v)
            // The BC SDK's 1-arg overload defaults precision to 0 (no rounding).
            // AL semantics are that Round(v) rounds to the nearest integer, so we
            // redirect to our own helper. The 2-arg and 3-arg overloads already
            // behave correctly and stay on ALSystemNumeric.
            if (exprText == "ALSystemNumeric" && methodName == "ALRound"
                && visited.ArgumentList.Arguments.Count == 1)
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ALRound")));
            }

            // value.ALByValue(this) -> value  (strip ITreeObject calls)
            // value.ModifyLength(N) -> value  (strip length modification)
            if (StripITreeObjectArgMethods.Contains(methodName))
            {
                // Return just the expression the method is called on
                return memberAccess.Expression;
            }

            // blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
            // Replace the first 'this' (ITreeObject) argument with null! but keep the rest.
            // These are real BC runtime methods that require ITreeObject; they work with null
            // in standalone mode for the operations we support.
            if (NullifyFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 1 && args[0].Expression.ToString() == "this")
                {
                    var nullBang = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    newArgs = newArgs.Add(SyntaxFactory.Argument(nullBang));
                    for (int i = 1; i < args.Count; i++)
                        newArgs = newArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
                }
            }

            // recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
            // Strip the first 'this' (ITreeObject) argument from mock type methods.
            if (StripFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2 && args[0].Expression.ToString() == "this")
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavFormatEvaluateHelper.Format(this.Session, value) -> AlCompat.Format(value)
            if (exprText == "NavFormatEvaluateHelper" && methodName == "Format")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this.Session' argument
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Format")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavFormatEvaluateHelper.Evaluate(this.Session, ref variable, text) -> AlCompat.Evaluate(ref variable, text)
            // BC lowers AL Evaluate(var Var; Text) to a call on NavFormatEvaluateHelper with the session as the first arg.
            // Strip the session and redirect to AlCompat.Evaluate which provides type-specific overloads.
            if (exprText == "NavFormatEvaluateHelper" && methodName == "Evaluate")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this.Session' argument; keep ref variable + text
                if (args.Count >= 3)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Evaluate")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // ALSystemVariable.ALCopyStream(dataError, outStream, inStream)
            // -> MockStream.ALCopyStream(dataError, outStream, inStream)
            // BC emits COPYSTREAM as a call to ALSystemVariable.ALCopyStream which takes
            // NavOutStream/NavInStream. After the NavOutStream/NavInStream type rename those
            // args are MockOutStream/MockInStream, so we redirect to MockStream.ALCopyStream
            // which accepts the mock types.
            if (exprText == "ALSystemVariable" && methodName == "ALCopyStream")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockStream"),
                        SyntaxFactory.IdentifierName("ALCopyStream")));
            }

            // ALSystemVariable.Clear(x) -> x.Clear()
            // In this project the only surviving pattern is RecordRef.Clear,
            // which no longer matches BC's NavComplexValue overloads after
            // NavRecordRef has been rewritten to MockRecordRef.
            if (exprText == "ALSystemVariable" && methodName == "Clear" &&
                visited.ArgumentList.Arguments.Count == 1)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        visited.ArgumentList.Arguments[0].Expression,
                        SyntaxFactory.IdentifierName("Clear")),
                    SyntaxFactory.ArgumentList());
            }

            // NavFile.ALUploadIntoStream(...) -> MockFile.ALUploadIntoStream(...)
            // The BC runtime method expects ByRef<NavInStream>; after rewriting
            // the argument is ByRef<MockInStream>, so redirect to a mock helper.
            if (exprText == "NavFile" && methodName == "ALUploadIntoStream")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockFile"),
                        SyntaxFactory.IdentifierName("ALUploadIntoStream")));
            }

            // NavFile.ALDownloadFromStream(...) -> MockFile.ALDownloadFromStream(...)
            // Same pattern: after NavInStream → MockInStream rewrite, the arg type
            // no longer matches NavFile's signature.
            if (exprText == "NavFile" && methodName == "ALDownloadFromStream")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockFile"),
                        SyntaxFactory.IdentifierName("ALDownloadFromStream")));
            }

            // ALCompiler.ObjectToNavArray<T>(x) -> AlCompat.ObjectToMockArray<T>(x)
            if (exprText == "ALCompiler" && methodName == "ObjectToNavArray")
            {
                SimpleNameSyntax newName = SyntaxFactory.IdentifierName("ObjectToMockArray");
                if (memberAccess.Name is GenericNameSyntax genericName)
                {
                    newName = SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier("ObjectToMockArray"),
                        genericName.TypeArgumentList);
                }

                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        newName));
            }

            // ALSystemErrorHandling.ALClearLastError() -> AlScope.LastErrorText = ""
            // ALSystemErrorHandling.ALGetLastErrorTextFunc(...) -> AlScope.LastErrorText
            // ALSystemErrorHandling.ALGetLastErrorObject(...) -> AlScope.GetLastErrorObject()
            if (exprText == "ALSystemErrorHandling")
            {
                if (methodName == "ALClearLastError")
                {
                    // Return an assignment expression: AlScope.LastErrorText = ""
                    // Also resets AlScope.LastErrorObject = null via the property setter in AssertError,
                    // but ClearLastError is a no-arg call so we chain the null-assignment here too.
                    // Emit: (AlScope.LastErrorText = "") is a hack — instead emit a helper call:
                    //   AlScope.ClearLastErrorState()
                    // which resets both LastErrorText and LastErrorObject.
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("ClearLastErrorState")));
                }

                if (methodName == "ALGetLastErrorTextFunc")
                {
                    // Return just AlScope.LastErrorText (ignore the excludeCustomerData arg)
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlScope"),
                        SyntaxFactory.IdentifierName("LastErrorText"));
                }

                if (methodName == "ALGetLastErrorObject")
                {
                    // GetLastErrorObject() → AlScope.GetLastErrorObject()
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("GetLastErrorObject")));
                }
            }

            // NavHttpContent.ALLoadFrom(MockInStream) — CS1503 after NavInStream→MockInStream rename.
            // BC emits two distinct patterns for HttpContent.WriteFrom(InStream):
            //   • var-parameter:    content.ALLoadFrom(this.bodyStream.Value)  — ByRef<MockInStream>.Value
            //   • local-variable:   content.ALLoadFrom(this.localStream)       — direct MockInStream field
            // A guard on ".Value" alone (arg.Expression is MemberAccessExpression{Name:"Value"}) would
            // only cover the ByRef case and silently leave the local-variable case broken (still CS1503).
            // ALLoadFrom is unique to NavHttpContent in BC-generated code; the AlCompat overloads cover
            // both MockInStream (stream case) and NavText (text case, passthrough). If an unrelated
            // receiver type ever appeared, its non-NavHttpContent first arg would produce a targeted
            // CS1503 at the AlCompat call site — visible immediately and easy to fix with a new overload.
            if (methodName == "ALLoadFrom" && visited.ArgumentList.Arguments.Count == 1)
            {
                var receiver = memberAccess.Expression;
                var arg = visited.ArgumentList.Arguments[0];
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("HttpContentLoadFrom")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                            SyntaxFactory.Argument(receiver),
                            arg
                        })));
            }

            // NavHttpContent.ALReadAs(this, DataError, ByRef<MockInStream>) — CS1503.
            // BC emits content.ALReadAs(this, DataError.ThrowError, bodyStream) for
            // HttpContent.ReadAs(var Stream: InStream).  After NavInStream→MockInStream rename
            // the ByRef<MockInStream> is incompatible with ByRef<NavInStream>.
            // Only the 3-arg form (stream variant) needs the redirect; the 2-arg form
            // (text variant: ALReadAs(DataError, ByRef<NavText>)) works without changes.
            if (methodName == "ALReadAs" && visited.ArgumentList.Arguments.Count == 3)
            {
                var receiver = memberAccess.Expression;
                var args = visited.ArgumentList.Arguments;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("HttpContentReadAs")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                            SyntaxFactory.Argument(receiver),
                            args[0],
                            args[1],
                            args[2]
                        })));
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Binary expressions: intercept NavCode == / != comparisons
    // -----------------------------------------------------------------------

    /// <summary>Returns true when <paramref name="expr"/> is an <c>AlCompat.CreateNavCode(...)</c> call.</summary>
    private static bool IsCreateNavCodeCall(ExpressionSyntax expr)
        => expr is InvocationExpressionSyntax inv &&
           inv.Expression is MemberAccessExpressionSyntax ma &&
           ma.Expression.ToString() == "AlCompat" &&
           ma.Name.Identifier.Text == "CreateNavCode";

    /// <summary>
    /// Rewrites <c>X == AlCompat.CreateNavCode(...)</c> (and the != form) to
    /// <c>AlCompat.NavCodeEquals(X, AlCompat.CreateNavCode(...))</c>.
    ///
    /// BC emits <c>NavCode.op_Equality</c> for <c>case Category of 'A':</c>, which
    /// calls <c>NavEnvironment</c> (null in standalone mode → NullReferenceException).
    /// After <see cref="VisitObjectCreationExpression"/> has rewritten
    /// <c>new NavCode(N, "A")</c> → <c>AlCompat.CreateNavCode(N, "A")</c> in the
    /// children, we detect the pattern here and route through the safe helper.
    /// </summary>
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;

        bool isEq = visited.IsKind(SyntaxKind.EqualsExpression);
        bool isNe = visited.IsKind(SyntaxKind.NotEqualsExpression);
        if (!isEq && !isNe)
            return visited;

        // Only intercept when at least one side is AlCompat.CreateNavCode(...) — that
        // identifies a Code[N] literal comparison generated by BC for case statements.
        if (!IsCreateNavCodeCall(visited.Left) && !IsCreateNavCodeCall(visited.Right))
            return visited;

        var equalsCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("AlCompat"),
                SyntaxFactory.IdentifierName("NavCodeEquals")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                    SyntaxFactory.Argument(visited.Left),
                    SyntaxFactory.Argument(visited.Right)
                })));

        if (isNe)
            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(equalsCall));

        return equalsCall;
    }

    // -----------------------------------------------------------------------
    // Member access expressions: remove .Target., rewrite base.Parent._parent
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        // Pattern: base.Parent.xxx -> _parent.xxx
        // After recursion, base.Parent is a MemberAccessExpression where:
        //   Expression = BaseExpression ("base"), Name = "Parent"
        // So base.Parent.field shows up as: (base.Parent).field
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess2 &&
            innerAccess2.Name.Identifier.Text == "Parent" &&
            innerAccess2.Expression is BaseExpressionSyntax)
        {
            // Special case: base.Parent.__ThisHandle -> MockCodeunitHandle.FromInstance(_parent)
            // BC compiler emits __ThisHandle for exit(this) in codeunit-returning methods.
            // __ThisHandle is a NavCodeunitHandle property on NavCodeunit; after stripping the
            // base class it is undefined. Replace with a factory call that wraps the instance.
            if (visited.Name.Identifier.Text == "__ThisHandle")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("FromInstance")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_parent")))));
            }
            // Replace base.Parent.xxx with _parent.xxx
            return visited.WithExpression(SyntaxFactory.IdentifierName("_parent"));
        }

        // Pattern: base.ParentObject -> this.ParentObject
        // Page/table extensions reference base.ParentObject but base class is stripped to object.
        // ParentObject is rewritten to return Rec, so we just replace base with this.
        if (visited.Name.Identifier.Text == "ParentObject" &&
            visited.Expression is BaseExpressionSyntax)
        {
            return visited.WithExpression(SyntaxFactory.ThisExpression());
        }

        // Pattern: xxx.Target -> xxx (strip .Target accessor on handles)
        // This covers both xxx.Target.Method (already handled) and standalone xxx.Target
        if (visited.Name.Identifier.Text == "Target")
        {
            // Just strip .Target and return the expression
            return visited.Expression;
        }

        // (NavOptionMetadata access left as-is — NavOption type is preserved)

        // Pattern: xxx.Target.MethodName -> xxx.MethodName (legacy — now redundant but kept for safety)
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess &&
            innerAccess.Name.Identifier.Text == "Target")
        {
            var outerMethodName = visited.Name.Identifier.Text;

            // Record target methods
            if (RecordTargetMethods.Contains(outerMethodName))
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Codeunit target method: Invoke
            if (outerMethodName == "Invoke")
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Also handle ToDecimal, and other methods that may chain after Target
            // e.g. this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal()
            // The GetFieldValueSafe is caught above; ToDecimal chains on its result, not on Target.
        }

        // Pattern: ALSession.ALCurrentClientType -> NavClientType.Background
        //          ALSession.ALDefaultClientType -> NavClientType.Background
        // Both access NavSession which is null in standalone mode.
        if (visited.Expression is IdentifierNameSyntax sessionId &&
            sessionId.Identifier.Text == "ALSession")
        {
            var propName = visited.Name.Identifier.Text;
            if (propName == "ALCurrentClientType" || propName == "ALDefaultClientType")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("NavClientType"),
                    SyntaxFactory.IdentifierName("Background"));
            }
        }

        // Pattern: ALSessionInformation.ALSqlRowsRead -> 0L
        //          ALSessionInformation.ALSqlStatementsExecuted -> 0L
        //          ALSessionInformation.ALCallstack -> ""
        //          ALSessionInformation.ALAITokensUsed -> 0L
        // All dereference NavSession (null standalone). Return safe defaults.
        if (visited.Expression is IdentifierNameSyntax sessInfoId &&
            sessInfoId.Identifier.Text == "ALSessionInformation")
        {
            var prop = visited.Name.Identifier.Text;
            if (prop == "ALSqlRowsRead" || prop == "ALSqlStatementsExecuted" || prop == "ALAITokensUsed")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(0L));
            }
            if (prop == "ALCallstack")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(""));
            }
        }

        // Pattern: ALSystemErrorHandling.ALGetLastErrorText     -> AlScope.LastErrorText
        //          ALSystemErrorHandling.ALGetLastErrorCode     -> AlScope.LastErrorCode
        //          ALSystemErrorHandling.ALGetLastErrorCallStack -> AlScope.LastErrorCallStack
        // ALSystemErrorHandling accesses NavCurrentThread.Session which is null in standalone mode.
        if (visited.Expression is IdentifierNameSyntax errHandlingId &&
            errHandlingId.Identifier.Text == "ALSystemErrorHandling")
        {
            var errProp = visited.Name.Identifier.Text;
            string? scopeProp = errProp switch
            {
                "ALGetLastErrorText" => "LastErrorText",
                "ALGetLastErrorCode" => "LastErrorCode",
                "ALGetLastErrorCallStack" => "LastErrorCallStack",
                _ => null
            };
            if (scopeProp != null)
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName(scopeProp));
            }
        }

        // Pattern: ErrorCollection.ALHasCollectedErrors -> AlScope.HasCollectedErrors
        //          ErrorCollection.ALIsCollectingErrors -> AlScope.IsCollectingErrors
        // ErrorCollection depends on NavSession; redirect to AlScope's thread-static state.
        // Handles both short (`ErrorCollection`) and fully-qualified
        // (`Microsoft.Dynamics.Nav.Runtime.ErrorCollection`) forms.
        {
            var exprStr = visited.Expression.ToString();
            if ((exprStr == "ErrorCollection" || exprStr.EndsWith(".ErrorCollection", StringComparison.Ordinal)) &&
                (visited.Name.Identifier.Text == "ALHasCollectedErrors" || visited.Name.Identifier.Text == "ALIsCollectingErrors"))
            {
                var targetProp = visited.Name.Identifier.Text == "ALHasCollectedErrors"
                    ? "HasCollectedErrors"
                    : "IsCollectingErrors";
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName(targetProp))
                    .WithTriviaFrom(visited);
            }
        }

        // Pattern: ALDatabase.ALCurrentTransactionType (property get — no parentheses)
        // BC lowers CurrentTransactionType() to a PROPERTY GETTER on ALDatabase, NOT a method call.
        // The generated C# is `ALDatabase.ALCurrentTransactionType` (a MemberAccessExpressionSyntax),
        // not `ALDatabase.ALCurrentTransactionType(...)` (an InvocationExpressionSyntax).
        // Rewrite to AlCompat.ALCurrentTransactionType() which returns TransactionType::Update.
        if (visited.Expression is IdentifierNameSyntax txDbId &&
            txDbId.Identifier.Text == "ALDatabase" &&
            visited.Name.Identifier.Text == "ALCurrentTransactionType")
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("ALCurrentTransactionType")),
                SyntaxFactory.ArgumentList())
                .WithTriviaFrom(visited);
        }

        // Pattern: ALDatabase.ALLockTimeout (property get) -> true
        //          ALDatabase.ALLockTimeoutDuration (property get) -> 0L
        // BC lowers Database.LockTimeout and Database.LockTimeoutDuration to property
        // getters that require a live NavSession; they crash with NullReferenceException
        // standalone. Assignment (`ALDatabase.ALLockTimeout = val`) is already stripped
        // to no-op in VisitExpressionStatement. The runner has no real database, so
        // true (lock-timeout-enabled by default) and 0L (zero-duration) are sensible
        // standalone stubs. 0L flows through `ALCompiler.ToDuration(long)` back to
        // NavDuration without further adaptation.
        if (visited.Expression is IdentifierNameSyntax lockDbId2 &&
            lockDbId2.Identifier.Text == "ALDatabase")
        {
            if (visited.Name.Identifier.Text == "ALLockTimeout")
            {
                return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)
                    .WithTriviaFrom(visited);
            }
            if (visited.Name.Identifier.Text == "ALLockTimeoutDuration")
            {
                return SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(0L))
                    .WithTriviaFrom(visited);
            }
        }

        // Pattern: ALDatabase.ALCompanyName / ALDatabase.ALUserID / ALDatabase.ALTenantID / ALDatabase.ALSerialNumber
        // These static property accesses crash because ALDatabase requires a live BC session.
        // ALCompanyName routes to MockSession.GetCompanyName() (configurable via --company-name CLI flag).
        // ALUserID redirects to AlScope.UserId (configurable via --user-id CLI flag).
        // ALTenantID and ALSerialNumber return a fixed non-empty string ("STANDALONE") so
        // telemetry / licensing branches that test non-empty behave consistently.
        // (Note: BC sometimes emits these as method calls instead — see the invocation
        // handler at the top of VisitInvocationExpression which uses the same placeholder.)
        if (visited.Expression is IdentifierNameSyntax dbId &&
            dbId.Identifier.Text == "ALDatabase" &&
            ALDatabaseStringProps.Contains(visited.Name.Identifier.Text))
        {
            if (visited.Name.Identifier.Text == "ALCompanyName")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockSession"),
                        SyntaxFactory.IdentifierName("GetCompanyName")))
                    .WithTriviaFrom(visited);
            }
            if (visited.Name.Identifier.Text == "ALUserID")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName("UserId"))
                    .WithTriviaFrom(visited);
            }
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal("STANDALONE"))
                .WithTriviaFrom(visited);
        }

        // Pattern: value.ALIsBoolean, value.ALIsText, etc. (NavVariant type-check properties)
        // Rewrite to: AlCompat.ALIsBoolean(value) invocation
        var memberName = visited.Name.Identifier.Text;

        // Pattern: xxx.Session.IsEventSessionRecorderEnabled -> false
        // Also: xxx.Session -> null! (Session property removed with base class)
        if (memberName == "Session" &&
            (visited.Expression is ThisExpressionSyntax || visited.Expression is IdentifierNameSyntax))
        {
            return SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                .WithTriviaFrom(visited);
        }
        if (memberName == "IsEventSessionRecorderEnabled")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }
        if (memberName.StartsWith("ALIs") && NavVariantTypeCheckProps.Contains(memberName))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(memberName)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        // NavForm.LookupMode (property getter — no parentheses in C#)
        // BC lowers static `Page.LookupMode` to a PROPERTY GET on NavForm, not a method call.
        // NavForm doesn't exist standalone, so return false (default). Assignment is stripped
        // to no-op in VisitExpressionStatement.
        if (IsNavFormReference(visited.Expression) && memberName == "ALLookupMode")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }

        // NavOption.ALNames / NavOption.ALOrdinals — property getters that
        // reach into NCLOptionMetadata native code. Redirect to AlCompat
        // helpers which look up the tagged enum id via ConditionalWeakTable.
        if (memberName == "ALNames" || memberName == "ALOrdinals")
        {
            var helper = memberName == "ALNames" ? "GetNamesForOption" : "GetOrdinalsForOption";
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(helper)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        // Pattern: ALSystemLanguage.ALGlobalLanguage -> MockLanguage.ALGlobalLanguage
        // ALSystemLanguage.ALGlobalLanguage / ALWindowsLanguage crash because there
        // is no live BC session context in standalone mode.
        // ALGlobalLanguage: redirect to MockLanguage.ALGlobalLanguage (get/set in-memory).
        // ALWindowsLanguage: redirect to MockLanguage.ALWindowsLanguage (current-culture LCID).
        if (visited.Expression is IdentifierNameSyntax langId &&
            langId.Identifier.Text == "ALSystemLanguage" &&
            (memberName == "ALGlobalLanguage" || memberName == "ALWindowsLanguage"))
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("MockLanguage"),
                SyntaxFactory.IdentifierName(memberName))
                .WithTriviaFrom(visited);
        }

        // Pattern: token.ALPath → MockJsonHelper.Path(token)
        // NavJsonToken.ALPath is a property (not method) that returns Newtonsoft path format.
        // We intercept it to convert to BC $-prefixed format.
        if (memberName == "ALPath")
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("MockJsonHelper"),
                    SyntaxFactory.IdentifierName("Path")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        // Pattern: NavEnvironment.IsServiceTier → false
        // NavEnvironment.IsServiceTier returns true when a BC service tier is present.
        // In standalone runner there is no service tier, so we always return false.
        if (visited.Expression is IdentifierNameSyntax envId &&
            envId.Identifier.Text == "NavEnvironment" &&
            memberName == "IsServiceTier")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }

        // Pattern: AlCompat.NavIndirectValueToNavValue<NavDotNet>(x).IsNull → false
        // BC emits this for Variant.IsNull checks. NavDotNet cast fails when the variant holds
        // a non-DotNet value (Text, Integer, etc.). In standalone mode we never have real
        // .NET Automation objects, so IsNull is always false for any non-null variant.
        if (memberName == "IsNull" &&
            visited.Expression is InvocationExpressionSyntax isNullInvocation &&
            isNullInvocation.Expression is MemberAccessExpressionSyntax isNullMa &&
            isNullMa.Name is GenericNameSyntax isNullGeneric &&
            isNullGeneric.Identifier.Text == "NavIndirectValueToNavValue")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }

        return visited;
    }

    /// <summary>
    /// Returns true if an expression refers to the BC NavForm type, whether
    /// written as a bare `NavForm` identifier or fully-qualified such as
    /// `Microsoft.Dynamics.Nav.Runtime.NavForm`. BC compiler output varies
    /// across versions.
    /// </summary>
    private static bool IsNavFormReference(ExpressionSyntax expr)
    {
        var text = expr.ToString();
        return text == "NavForm" || text.EndsWith(".NavForm", StringComparison.Ordinal);
    }

    /// <summary>
    /// ALDatabase static property names that return strings and crash without a BC session.
    /// Rewritten to empty-string literals in standalone mode.
    /// </summary>
    private static readonly HashSet<string> ALDatabaseStringProps = new(StringComparer.Ordinal)
    {
        "ALCompanyName",    // CompanyName built-in
        "ALUserID",         // UserId built-in
        "ALTenantID",       // TenantId built-in (added for completeness)
        "ALSerialNumber",   // SerialNumber built-in (added for completeness)
    };

    private static readonly HashSet<string> NavVariantTypeCheckProps = new(StringComparer.Ordinal)
    {
        "ALIsBoolean", "ALIsOption", "ALIsInteger", "ALIsByte", "ALIsBigInteger",
        "ALIsDecimal", "ALIsText", "ALIsCode", "ALIsChar", "ALIsTextConst",
        "ALIsDate", "ALIsTime", "ALIsDuration", "ALIsDateTime", "ALIsDateFormula",
        "ALIsGuid", "ALIsRecordId", "ALIsRecord", "ALIsRecordRef", "ALIsFieldRef",
        "ALIsCodeunit", "ALIsFile",
        // JSON
        "ALIsJsonToken", "ALIsJsonObject", "ALIsJsonArray", "ALIsJsonValue",
        // Streams
        "ALIsInStream", "ALIsOutStream",
        // Collections and misc
        "ALIsNotification", "ALIsTextBuilder", "ALIsList", "ALIsDictionary",
        // Misc stubs
        "ALIsAction", "ALIsAutomation", "ALIsBinary", "ALIsClientType", "ALIsDataClassification",
        "ALIsDataClassificationType", "ALIsDefaultLayout", "ALIsExecutionMode", "ALIsFilterPageBuilder",
        "ALIsObjectType", "ALIsPromptMode", "ALIsReportFormat", "ALIsSecurityFiltering",
        "ALIsTableConnectionType", "ALIsTestPermissions", "ALIsTextConstant", "ALIsTextEncoding",
        "ALIsTransactionType", "ALIsWideChar",
    };

    // -----------------------------------------------------------------------
    // If statements: strip the `if (X.γeventScope == null && !Session.IsEventSessionRecorderEnabled) return;`
    // guard at the top of BC-generated event methods. Without this, the
    // generated method bails before reaching the rewritten FireEvent
    // statement because γeventScope stays null (no BC-level subscribers
    // registered in standalone mode).
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        if (node.Condition is BinaryExpressionSyntax bin &&
            bin.OperatorToken.IsKind(SyntaxKind.AmpersandAmpersandToken))
        {
            static bool MentionsEventScope(ExpressionSyntax expr) =>
                expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.ValueText == "γeventScope");

            if (MentionsEventScope(bin.Left) || MentionsEventScope(bin.Right))
            {
                return SyntaxFactory.EmptyStatement();
            }
        }
        return base.VisitIfStatement(node);
    }

    // -----------------------------------------------------------------------
    // Expression statements: remove StmtHit(N); and handle NavRuntimeHelpers
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // Keep StmtHit(N) calls for coverage tracking (they call AlScope.StmtHit)
        if (node.Expression is InvocationExpressionSyntax invocation)
        {

            // Remove calls to BC-only methods (ALGetTable, ALClose, RunEvent) that can't work standalone
            if (invocation.Expression is MemberAccessExpressionSyntax stripMa &&
                StripEntireCallMethods.Contains(stripMa.Name.Identifier.Text))
            {
                // Return empty statement instead of null to avoid crash inside using blocks
                return SyntaxFactory.EmptyStatement();
            }

            // ALSession.ALLogMessage(...) → no-op
            // Requires Microsoft.BusinessCentral.Telemetry.Abstractions assembly which is unavailable.
            if (invocation.Expression is MemberAccessExpressionSyntax logMa &&
                logMa.Expression.ToString() == "ALSession" &&
                logMa.Name.Identifier.Text == "ALLogMessage")
            {
                return SyntaxFactory.EmptyStatement();
            }

            // Rewrite ALSession.ALBindSubscription(DataError, target) → target.Bind()
            // and ALSession.ALUnbindSubscription(DataError, target) → target.Unbind()
            if (invocation.Expression is MemberAccessExpressionSyntax bindMa &&
                (bindMa.Name.Identifier.Text == "ALBindSubscription" ||
                 bindMa.Name.Identifier.Text == "ALUnbindSubscription") &&
                invocation.ArgumentList.Arguments.Count >= 2)
            {
                var methodName = bindMa.Name.Identifier.Text == "ALBindSubscription" ? "Bind" : "Unbind";
                // Second arg is the codeunit target — strip .Target if present
                var targetExpr = invocation.ArgumentList.Arguments[1].Expression;
                if (targetExpr is MemberAccessExpressionSyntax targetMa &&
                    targetMa.Name.Identifier.Text == "Target")
                    targetExpr = targetMa.Expression;
                var call = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        targetExpr,
                        SyntaxFactory.IdentifierName(methodName)),
                    SyntaxFactory.ArgumentList());
                return SyntaxFactory.ExpressionStatement(call);
            }

            // `βscope.RunEvent()` dispatches event subscribers. Walk the
            // ancestor tree to recover the enclosing Codeunit<N> type and
            // the enclosing event method name, then rewrite the statement
            // to a direct call into AlCompat.FireEvent which consults the
            // subscriber registry built at runtime via reflection.
            // Also forward the enclosing method's parameters so subscribers
            // receive the publisher's actual ByRef<T> / value arguments.
            if (invocation.Expression is MemberAccessExpressionSyntax runEventMa &&
                runEventMa.Name.Identifier.Text == "RunEvent" &&
                invocation.ArgumentList.Arguments.Count == 0)
            {
                int? cuId = null;
                string? eventName = null;
                MethodDeclarationSyntax? enclosingMethod = null;
                foreach (var ancestor in node.Ancestors())
                {
                    if (enclosingMethod == null && ancestor is MethodDeclarationSyntax mdecl)
                    {
                        eventName = mdecl.Identifier.Text;
                        enclosingMethod = mdecl;
                    }
                    if (ancestor is ClassDeclarationSyntax cdecl && cdecl.Identifier.Text.StartsWith("Codeunit"))
                    {
                        var idStr = cdecl.Identifier.Text.Substring("Codeunit".Length);
                        if (int.TryParse(idStr, out var id)) cuId = id;
                        break;
                    }
                }
                if (cuId.HasValue && eventName != null)
                {
                    // Check if the enclosing event method has [NavEvent(_, true, _)]
                    // where the second argument (IncludeSender) is true.
                    bool includeSender = false;
                    if (enclosingMethod != null)
                    {
                        foreach (var attrList in enclosingMethod.AttributeLists)
                        foreach (var attr in attrList.Attributes)
                        {
                            var simpleAttrName = GetSimpleAttributeName(attr);
                            if (simpleAttrName is "NavEvent" or "NavEventAttribute")
                            {
                                if (attr.ArgumentList?.Arguments.Count >= 2)
                                {
                                    var secondArg = attr.ArgumentList.Arguments[1].Expression;
                                    if (secondArg is LiteralExpressionSyntax lit &&
                                        lit.IsKind(SyntaxKind.TrueLiteralExpression))
                                        includeSender = true;
                                }
                            }
                        }
                    }

                    var argsList = new List<ArgumentSyntax>
                    {
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(cuId.Value))),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(eventName)))
                    };

                    // When IncludeSender=true, prepend a MockCodeunitHandle wrapping
                    // the publisher instance so subscribers receive it as their first arg.
                    if (includeSender)
                    {
                        argsList.Add(SyntaxFactory.Argument(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                                    SyntaxFactory.IdentifierName("FromInstance")),
                                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))))));
                    }

                    // Forward the enclosing event method's parameters
                    if (enclosingMethod != null)
                    {
                        foreach (var param in enclosingMethod.ParameterList.Parameters)
                            argsList.Add(SyntaxFactory.Argument(
                                SyntaxFactory.IdentifierName(param.Identifier.Text)));
                    }
                    var call = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("FireEvent")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argsList)));
                    return SyntaxFactory.ExpressionStatement(call);
                }
                // Fallback: strip the call, same as before.
                return SyntaxFactory.EmptyStatement();
            }

            // NavForm.Run / NavForm.RunModal / NavForm.SetRecord / NavForm.Activate / ... called
            // as bare statements (i.e. value discarded) -> no-op. Page navigation and page
            // lifecycle methods are not supported standalone; when the return value is assigned,
            // the expression-level rewriter turns it into a default value instead.
            //
            // Accept both `NavForm.Method(...)` and fully-qualified
            // `Microsoft.Dynamics.Nav.Runtime.NavForm.Method(...)` — older BC
            // compiler versions emit the qualified form, so a strict `== "NavForm"`
            // check missed them and let the broken call survive into Roslyn.
            if (invocation.Expression is MemberAccessExpressionSyntax navFormMa &&
                IsNavFormReference(navFormMa.Expression))
            {
                var navFormMethod = navFormMa.Name.Identifier.Text;
                if (navFormMethod is "Run" or "RunModal" or "SetRecord" or
                    "Activate" or "SaveRecord" or "Update" or
                    "SetTableView" or "SetSelectionFilter" or
                    "CancelBackgroundTask" or "SetBackgroundTaskResult" or
                    "GetBackgroundParameters" or "EnqueueBackgroundTask")
                {
                    return SyntaxFactory.EmptyStatement();
                }
            }

            // NavRuntimeHelpers.CompilationError(...) -> throw new InvalidOperationException("Compilation error");
            if (invocation.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString() == "NavRuntimeHelpers" &&
                ma.Name.Identifier.Text == "CompilationError")
            {
                return SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal("Compilation error")))))));
            }
        }

        // ALDatabase.ALLockTimeout = value → no-op
        // Real BC implementation requires NavEnvironment which crashes standalone.
        if (node.Expression is AssignmentExpressionSyntax lockAssignment &&
            lockAssignment.Left is MemberAccessExpressionSyntax lockMa &&
            lockMa.Expression is IdentifierNameSyntax lockDbId &&
            lockDbId.Identifier.Text == "ALDatabase" &&
            lockMa.Name.Identifier.Text == "ALLockTimeout")
        {
            return SyntaxFactory.EmptyStatement();
        }

        // NavForm.ALLookupMode = value → no-op
        // BC lowers `Page.LookupMode := value` to an assignment to NavForm.ALLookupMode.
        // NavForm doesn't exist standalone; swallow the assignment.
        if (node.Expression is AssignmentExpressionSyntax lookupModeAssignment &&
            lookupModeAssignment.Left is MemberAccessExpressionSyntax lookupModeMa &&
            IsNavFormReference(lookupModeMa.Expression) &&
            lookupModeMa.Name.Identifier.Text == "ALLookupMode")
        {
            return SyntaxFactory.EmptyStatement();
        }

        return base.VisitExpressionStatement(node);
    }
}
