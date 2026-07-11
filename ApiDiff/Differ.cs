using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using CppAst;

namespace ApiDiff;

internal class Differ(string InputHeader, string TargetHeader, string IncludeDir)
{

    private static readonly CppCommentText UnresolvedComment = new() { Text = "Unresolved" }, MacroIdArray = new() { Text = "DO_ARRAY_DEFINE" }, MacroIdArrayPtr = new() { Text = "DO_ARRAY_DEFINE_PTR" }, MacroIdList = new() { Text = "DO_LIST_DEFINE" };

    private const string IgnorePushToken = "apidiff push ignore", IgnorePopToken = "apidiff pop ignore";
    private const string IgnorePushMarker = "#pragma apidiff push ignore", IgnorePopMarker = "#pragma apidiff pop ignore";

    private readonly List<CppTypeDeclaration> _inputDeclarations = [], _targetDeclarations = [], _targetGlobalDeclarations = [];
    private CppTypeLookup<CppTypeDeclaration> _inputTypeLookup = null!, _targetTypeLookup = null!, _targetGlobalTypeLookup = null!;
    private readonly Dictionary<string, CppType> _prebuiltTypes = [];
    private readonly List<CppType> _insertedTypes = [];
    private readonly Dictionary<CppTypeDeclaration, List<CppType>> _insertionMap = [];
    private readonly Dictionary<string, List<CppType>> _insertionTypesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<CppTypeDeclaration, List<ArrayDefinition>> _arrayDefinitionsByElementType = [];
    private readonly Dictionary<string, PointerArrayDefinition> _pointerArrayDefinitionsByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _definedArrayTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<CppCommentText, List<int>> _macrosExpansionIndex = [];
    private readonly List<(int Start, int End)> _ignoreRanges = [];
    private readonly HashSet<CppTypeDeclaration> _ignoredDeclarations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CppField> _pinnedFields = new(ReferenceEqualityComparer.Instance);
    private HashSet<string> _targetIncludes = [];
    private readonly HashSet<string> _walkedClasses = [];
    private readonly Dictionary<CppClass, CppClass> _effectiveInputClassCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CppClass, InputClassLayer[]> _inputClassHierarchyCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, CppTypeDeclaration?> _nearestTargetAncestorCache = new(StringComparer.Ordinal);
    private long _effectiveInputClassCacheHits, _inputClassHierarchyCacheHits, _nearestTargetAncestorCacheHits;
    private CppCompilation? _inputCompilation, _targetCompilation;
    private bool _typeSystemWalked = false;

    public bool BuildTypeModel()
    {
        long buildStarted = Stopwatch.GetTimestamp();
        if (_typeSystemWalked)
        {
            return false;
        }

        bool targetWindows = IncludeDir.Contains("Windows Kits");
        CppParserOptions cppParserOptions = targetWindows ? new() { ParseMacros = true } : new() { TargetCpu = CppTargetCpu.ARM64, TargetSystem = "linux", TargetAbi = "android21", ParseMacros = true };
        if (targetWindows)
        {
            cppParserOptions.ConfigureForWindowsMsvc(CppTargetCpu.X86_64);
        }
        cppParserOptions.Defines.Add(targetWindows ? "_GHIDRA_=1" : "_IDACLANG_=1");
        cppParserOptions.IncludeFolders.Add(new FileInfo(TargetHeader).Directory!.FullName);
        if (targetWindows)
        {
            var winSdkRoot = new DirectoryInfo(IncludeDir);
            cppParserOptions.SystemIncludeFolders.Add(Path.Combine(winSdkRoot.FullName, "ucrt"));
            cppParserOptions.SystemIncludeFolders.Add(Path.Combine(winSdkRoot.FullName, "shared"));
            cppParserOptions.SystemIncludeFolders.Add(Path.Combine(winSdkRoot.FullName, "um"));
        }
        else
        {
            var sysRootInclude = new DirectoryInfo(IncludeDir);
            cppParserOptions.SystemIncludeFolders.Add(Path.Combine(sysRootInclude.FullName, "c++", "v1"));
            cppParserOptions.SystemIncludeFolders.Add(sysRootInclude.FullName);
        }

        long inputParseStarted = Stopwatch.GetTimestamp();
        _inputCompilation = TryParseHeader(File.ReadAllText(InputHeader), "input", cppParserOptions);
        Log.Info($"Performance: input parse {Stopwatch.GetElapsedTime(inputParseStarted).TotalSeconds:F3}s.");
        if (_inputCompilation.HasErrors)
        {
            return false;
        }

        string targetFileContent = File.ReadAllText(TargetHeader).Replace("#pragma once", $"#pragma once\ntypedef {(targetWindows ? "unsigned __int64" : "unsigned long")} size_t;");
        if (targetWindows)
        {
            targetFileContent = targetFileContent.Replace("<cstdint>", "<stdint.h>");
        }
        _macrosExpansionIndex.Add(MacroIdArray, FindAllOccurrencesMacroIndex(targetFileContent, "DO_ARRAY_DEFINE"));
        _macrosExpansionIndex.Add(MacroIdArrayPtr, FindAllOccurrencesMacroIndex(targetFileContent, "DO_ARRAY_DEFINE_PTR"));
        _macrosExpansionIndex.Add(MacroIdList, FindAllOccurrencesMacroIndex(targetFileContent, "DO_LIST_DEFINE"));
        ComputeIgnoreRanges(targetFileContent);
        long targetParseStarted = Stopwatch.GetTimestamp();
        _targetCompilation = TryParseHeader(targetFileContent, "target", cppParserOptions);
        Log.Info($"Performance: target parse {Stopwatch.GetElapsedTime(targetParseStarted).TotalSeconds:F3}s.");
        if (_targetCompilation.HasErrors)
        {
            return false;
        }

        _targetIncludes = [.. _targetCompilation.InclusionDirectives.Select(targetInclude => { return targetInclude.FileName; })];
        CppNamespace? appNamespace = _targetCompilation.Namespaces.FirstOrDefault(@namespace => @namespace.Name == "app");
        if (appNamespace is null)
        {
            return false;
        }

        _inputDeclarations.AddRange([.. _inputCompilation.Typedefs, .. _inputCompilation.Enums, .. _inputCompilation.Classes]);
        _targetDeclarations.AddRange([.. appNamespace.Enums, .. appNamespace.Classes]);

        //_inputDeclarations.AddRange([.. _inputCompilation.Typedefs, .. _inputCompilation.Enums, .. _inputCompilation.Classes]);
        _targetGlobalDeclarations.AddRange(_targetCompilation.Children().OfType<CppTypeDeclaration>());
        _targetDeclarations.AddRange(_targetGlobalDeclarations.Where(def => def.Parent is CppNamespace));
        _inputDeclarations.SortBySourceLocation(false);
        _targetDeclarations.SortBySourceLocation(false);
        long inputIndexStarted = Stopwatch.GetTimestamp();
        _inputTypeLookup = new(_inputDeclarations);
        Log.Info($"Performance: input lookup index {Stopwatch.GetElapsedTime(inputIndexStarted).TotalSeconds:F3}s.");

        foreach (CppTypeDeclaration targetDeclaration in _targetDeclarations)
        {
            SetElementParent(targetDeclaration, null!);
            foreach ((CppCommentText? macroID, List<int>? indexes) in _macrosExpansionIndex)
            {
                if (indexes.Exists(idx => idx == targetDeclaration.Span.Start.Offset))
                {
                    targetDeclaration.Comment = macroID;
                }
            }

            if (_ignoreRanges.Count > 0 && IsOffsetIgnored(targetDeclaration.Span.Start.Offset))
            {
                _ignoredDeclarations.Add(targetDeclaration);
            }
        }

        foreach (string knownName in CppTypeExt.GlobalConfig.KnownNames)
        {
            LoadPrebuiltType(knownName);
        }
        foreach (string knownName in CppTypeExt.GlobalConfig.KnownReservedSuffixes.Values)
        {
            LoadPrebuiltType(knownName);
        }

        // Incomplete enums are intentional opaque declarations in Sakura.Core. Their
        // size is reported as zero by clang, but dropping them makes fields that use
        // the enum impossible to declare. Zero-sized classes are still unusable here.
        _targetDeclarations.RemoveAll(def => def is CppClass && def.SizeOf == 0 && !_ignoredDeclarations.Contains(def));
        long targetIndexStarted = Stopwatch.GetTimestamp();
        _targetTypeLookup = new(_targetDeclarations);
        _targetGlobalTypeLookup = new(_targetGlobalDeclarations);
        Log.Info($"Performance: target lookup indexes {Stopwatch.GetElapsedTime(targetIndexStarted).TotalSeconds:F3}s.");
        long walkStarted = Stopwatch.GetTimestamp();
        Span<CppTypeDeclaration> rawData = CollectionsMarshal.AsSpan(_targetDeclarations);
        for (int i = rawData.Length - 1; i >= 0; --i)
        {
            ref CppTypeDeclaration originalType = ref rawData[i];
            string typeKind = originalType.TypeKind.ToString().ToLower();
            if (_ignoredDeclarations.Contains(originalType))
            {
                Log.Info($"Keeping ignored {typeKind} {originalType.TypeName}.");
                continue;
            }

            if (originalType is CppClass cppClass)
            {
                typeKind = cppClass.ClassKind.ToString().ToLower();
                if (TryWalkClassFieldsUpdate(ref Unsafe.As<CppTypeDeclaration, CppClass>(ref originalType)))
                {
                    Log.Info($"{typeKind} {originalType.TypeName} resolved successfully.");
                    continue;
                }
            }
            else if (originalType is CppEnum cppEnum)
            {
                // An enum without items is a deliberately opaque enum. Keep its
                // declaration instead of replacing it with Inspector's full enum.
                if (cppEnum.Items.Count == 0)
                {
                    Log.Info($"Keeping opaque enum {originalType.TypeName}.");
                    continue;
                }

                if (TryWalkEnum(ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref originalType)))
                {
                    Log.Info($"{typeKind} {originalType.TypeName} resolved successfully.");
                    continue;
                }
            }

            if (originalType.Comment is { } comment && ((comment == MacroIdArray) || (comment == MacroIdArrayPtr) || (comment == MacroIdList)))
            {
                Log.Info($"Skipping expanded {typeKind} {originalType.TypeName}.");
            }
            else
            {
                Log.Error($"Skipping invalid {typeKind} {originalType.TypeName}.");
                originalType.Comment = UnresolvedComment;
            }
        }

        Log.Info($"Performance: declaration processing {Stopwatch.GetElapsedTime(walkStarted).TotalSeconds:F3}s.");
        long dedupeStarted = Stopwatch.GetTimestamp();
        foreach (ref CppTypeDeclaration targetType in rawData)
        {
            ref List<CppType> insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, targetType);
            if (Unsafe.IsNullRef(ref insertionList))
            {
                continue;
            }

            var removalList = new List<CppType>();
            foreach (CppType? typeToInsert in insertionList!)
            {
                if (_insertedTypes.Any(insertedType => insertedType.IsSameType(typeToInsert)))
                {
                    removalList.Add(typeToInsert);
                    continue;
                }

                _insertedTypes.Add(typeToInsert);
            }

            foreach (CppType typeToRemove in removalList)
            {
                insertionList.Remove(typeToRemove);
            }
        }

        Log.Info($"Performance: insertion dedupe {Stopwatch.GetElapsedTime(dedupeStarted).TotalSeconds:F3}s.");
        Log.Info($"Performance: lookup input={_inputTypeLookup.ExactHits} hit/{_inputTypeLookup.ExactMisses} miss/{_inputTypeLookup.RelaxedQueries} relaxed, " +
            $"target={_targetTypeLookup.ExactHits} hit/{_targetTypeLookup.ExactMisses} miss, global={_targetGlobalTypeLookup.ExactHits} hit/{_targetGlobalTypeLookup.ExactMisses} miss.");
        Log.Info($"Performance: cache hits effective={_effectiveInputClassCacheHits}, hierarchy={_inputClassHierarchyCacheHits}, ancestor={_nearestTargetAncestorCacheHits}; " +
            $"cache entries effective={_effectiveInputClassCache.Count}, hierarchy={_inputClassHierarchyCache.Count}, ancestor={_nearestTargetAncestorCache.Count}.");
        _typeSystemWalked = true;
        Log.Info($"Performance: model build total {Stopwatch.GetElapsedTime(buildStarted).TotalSeconds:F3}s.");
        return true;
    }

    public string ConstructDefinitions()
    {
        long constructStarted = Stopwatch.GetTimestamp();
        if (!_typeSystemWalked)
        {
            Log.FloodColour = true;
            Log.Error("Can not construct definitions before type model was built.");
            Log.FloodColour = false;
            return string.Empty;
        }

        StringBuilder headerBuilder = new();
        headerBuilder.AppendLine(CONST_HEADER);
        headerBuilder.AppendLine();

        List<CppType> globalDeclarations = [];
        foreach (string neededGlobalType in CppTypeExt.GlobalConfig.GetBuiltInTypes())
        {
            if (_targetCompilation!.Classes.FirstOrDefault(def => def.TypeName == neededGlobalType) is not CppClass @class)
            {
                continue;
            }

            globalDeclarations.Add(@class);
        }
        globalDeclarations.InsertRange(0, _targetCompilation!.Typedefs.Where(def => !_targetIncludes.Contains(def.SourceFile) && def.Name != "size_t"));
        foreach (CppType def in globalDeclarations)
        {
            headerBuilder.AppendLine($"{def.ConstructDefinition()};");
            headerBuilder.AppendLine();
            if (def is CppTypeDeclaration declaration)
            {
                AppendArrayDefinitions(headerBuilder, declaration);
            }
        }
        headerBuilder.AppendLine("namespace app {");
        headerBuilder.AppendLine();
        AppendForwardDeclarations(headerBuilder);
        var emittedPointerArrays = new HashSet<string>(StringComparer.Ordinal);
        foreach (CppEnum @enum in _targetDeclarations.OfType<CppEnum>())
        {
            if (_ignoredDeclarations.Contains(@enum))
            {
                headerBuilder.AppendLine(IgnorePushMarker);
                headerBuilder.AppendLine($"{@enum.ConstructDefinition()};");
                headerBuilder.AppendLine(IgnorePopMarker);
                headerBuilder.AppendLine();
                continue;
            }

            headerBuilder.AppendLine($"{@enum.ConstructDefinition()};");
            headerBuilder.AppendLine();
            AppendArrayDefinitions(headerBuilder, @enum);
        }
        foreach (CppEnum insertedEnum in _insertedTypes.OfType<CppEnum>())
        {
            headerBuilder.AppendLine($"{insertedEnum.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        foreach (CppTypeDeclaration data in OrderClassesByByValueDependencies())
        {
            if (data is not CppClass { } @class)
            {
                continue;
            }

            if (UnresolvedComment.Equals(data.Comment))
            {
                continue;
            }

            if (_ignoredDeclarations.Contains(data))
            {
                headerBuilder.AppendLine(IgnorePushMarker);
                headerBuilder.AppendLine($"{@class.ConstructDefinition()};");
                headerBuilder.AppendLine(IgnorePopMarker);
                headerBuilder.AppendLine();
                continue;
            }

            AppendPointerArrayDefinitions(headerBuilder, data, emittedPointerArrays);

            if (MacroIdList.Equals(data.Comment) || MacroIdArray.Equals(data.Comment) || MacroIdArrayPtr.Equals(data.Comment))
            {
                string macroName = ((CppCommentText)data.Comment).Text;
                string typeName = @class.Name;
                if (typeName.EndsWith("__Array"))
                {
                    if (MacroIdList.Equals(data.Comment))
                    {
                        continue;
                    }

                    typeName = typeName[..^7];
                }
                else
                {
                    typeName = typeName[7..];
                }

                headerBuilder.AppendLine($"{macroName}({typeName})");
                headerBuilder.AppendLine();
                AppendArrayDefinitions(headerBuilder, data);
                continue;
            }

            ref List<CppType> insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, data);
            if (!Unsafe.IsNullRef(ref insertionList))
            {
                foreach (CppType? insertedType in insertionList)
                {
                    headerBuilder.AppendLine($"{insertedType.ConstructDefinition()};");
                    headerBuilder.AppendLine();
                }
            }
            if (_pinnedFields.Count > 0 && ClassHasPinnedFields(@class))
            {
                AppendPinnedClassDefinition(headerBuilder, @class);
                headerBuilder.AppendLine(";");
            }
            else
            {
                headerBuilder.AppendLine($"{@class.ConstructDefinition()};");
            }
            headerBuilder.AppendLine();
            AppendArrayDefinitions(headerBuilder, data);
        }
        headerBuilder.AppendLine("}");
        headerBuilder.AppendLine();
        headerBuilder.AppendLine(CONST_FOOTER);

        string result = headerBuilder.ToString();
        Log.Info($"Performance: construct definitions {Stopwatch.GetElapsedTime(constructStarted).TotalSeconds:F3}s.");
        return result;
    }

    // Orders class declarations so that every base class, by-value member type, and
    // by-value array element is emitted before the class that embeds it (Issue 2). Pointer
    // members impose no ordering constraint (they rely on the forward declarations). The
    // by-value/base/array-element graph is acyclic, so a post-order DFS yields a valid order.
    private List<CppTypeDeclaration> OrderClassesByByValueDependencies()
    {
        var classes = new List<CppTypeDeclaration>();
        foreach (CppTypeDeclaration declaration in _targetDeclarations)
        {
            if (declaration is CppClass)
            {
                classes.Add(declaration);
            }
        }

        var indexOf = new Dictionary<CppTypeDeclaration, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < classes.Count; i++)
        {
            indexOf[classes[i]] = i;
        }

        var state = new byte[classes.Count];
        var ordered = new List<CppTypeDeclaration>(classes.Count);

        CppTypeDeclaration? ResolveType(CppType type)
        {
            ref CppTypeDeclaration found = ref _targetTypeLookup.Find(type);
            if (Unsafe.IsNullRef(ref found))
            {
                return null;
            }

            CppTypeDeclaration declaration = found;
            return indexOf.ContainsKey(declaration) ? declaration : null;
        }

        CppTypeDeclaration? ResolveName(string name)
        {
            ref CppTypeDeclaration found = ref _targetTypeLookup.Find(name);
            if (Unsafe.IsNullRef(ref found))
            {
                return null;
            }

            CppTypeDeclaration declaration = found;
            return indexOf.ContainsKey(declaration) ? declaration : null;
        }

        void VisitDependency(CppTypeDeclaration? dependency)
        {
            if (dependency is not null && indexOf.TryGetValue(dependency, out int index))
            {
                Visit(index);
            }
        }

        void Visit(int i)
        {
            if (state[i] != 0)
            {
                return;
            }

            state[i] = 1;
            CppTypeDeclaration data = classes[i];
            if (data is CppClass cls)
            {
                if (MacroIdList.Equals(data.Comment) || MacroIdArray.Equals(data.Comment))
                {
                    // The DO_LIST_DEFINE/DO_ARRAY_DEFINE expansion embeds the element by
                    // value, so the element must be defined before the macro is emitted.
                    string name = cls.Name;
                    string element = name.EndsWith("__Array", StringComparison.Ordinal)
                        ? name[..^7]
                        : (name.Length > 7 ? name[7..] : name);
                    VisitDependency(ResolveName(element));
                }
                else if (!MacroIdArrayPtr.Equals(data.Comment))
                {
                    if (cls.BaseTypes.FirstOrDefault()?.Type is CppType baseType)
                    {
                        VisitDependency(ResolveType(baseType));
                    }

                    foreach (CppField field in cls.Fields)
                    {
                        CppType fieldType = field.Type;
                        if (fieldType is CppClass)
                        {
                            VisitDependency(ResolveType(fieldType));
                        }
                        else if (fieldType is CppArrayType arrayType && arrayType.ElementType is CppClass)
                        {
                            VisitDependency(ResolveType(arrayType.ElementType));
                        }
                    }
                }
            }

            state[i] = 2;
            ordered.Add(data);
        }

        for (int i = 0; i < classes.Count; i++)
        {
            Visit(i);
        }

        return ordered;
    }

    // Emits `struct X;` forward declarations for every struct defined later in the
    // namespace body so pointer members referencing a type declared further down still
    // compile (Issue 2). By-value members and base classes rely on preserved source order.
    private void AppendForwardDeclarations(StringBuilder builder)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        int emitted = 0;

        void Declare(string kind, string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !declared.Add(name!))
            {
                return;
            }

            builder.AppendLine($"{kind} {name};");
            emitted++;
        }

        foreach (CppTypeDeclaration declaration in _targetDeclarations)
        {
            if (declaration is CppClass forwardClass && !UnresolvedComment.Equals(declaration.Comment))
            {
                Declare(forwardClass.ClassKind.ToString().ToLower(), forwardClass.Name);
            }
        }

        foreach (CppClass insertedClass in _insertedTypes.OfType<CppClass>())
        {
            Declare(insertedClass.ClassKind.ToString().ToLower(), insertedClass.Name);
        }

        foreach (List<ArrayDefinition> definitions in _arrayDefinitionsByElementType.Values)
        {
            foreach (ArrayDefinition definition in definitions)
            {
                Declare("struct", $"{definition.ElementTypeName}__Array");
            }
        }

        foreach (PointerArrayDefinition definition in _pointerArrayDefinitionsByName.Values)
        {
            Declare("struct", $"{definition.ElementTypeName}__Array");
        }

        if (emitted > 0)
        {
            builder.AppendLine();
        }
    }

    private void AppendArrayDefinitions(StringBuilder builder, CppTypeDeclaration elementType)
    {
        if (!_arrayDefinitionsByElementType.TryGetValue(elementType, out List<ArrayDefinition>? definitions))
        {
            return;
        }

        foreach (ArrayDefinition definition in definitions)
        {
            builder.AppendLine($"{definition.Macro.Text}({definition.ElementTypeName})");
            builder.AppendLine();
        }
    }

    private void AppendPointerArrayDefinitions(StringBuilder builder, CppTypeDeclaration owner, HashSet<string> emittedArrayTypes)
    {
        foreach (PointerArrayDefinition definition in _pointerArrayDefinitionsByName.Values)
        {
            if (!definition.Owners.Contains(owner) || !emittedArrayTypes.Add(definition.ArrayTypeName))
            {
                continue;
            }

            builder.AppendLine($"struct {definition.ElementTypeName};");
            builder.AppendLine($"{MacroIdArrayPtr.Text}({definition.ElementTypeName})");
            builder.AppendLine();
        }
    }

    private static CppCompilation TryParseHeader(string content, string name, CppParserOptions parserOptions)
    {
        Log.Debug($"Compiling {name}...");
        CppCompilation compilation = CppParser.Parse(content, parserOptions, name);
        if (compilation.Diagnostics.HasErrors)
        {
            IEnumerable<CppDiagnosticMessage> errors = compilation.Diagnostics.Messages.Where(message => message.Type == CppLogMessageType.Error);
            Log.Error($"Compilation ended with {errors.Count()} errors generated.");
            Log.FloodColour = true;
            foreach (CppDiagnosticMessage? error in errors)
            {
                Log.Error(error.ToString());
            }
            Log.FloodColour = false;
        }
        else
        {
            Log.Info($"Compilation ended with {compilation.Diagnostics.Messages.Count} diagnostics generated.");
        }

        Log.Debug($"{compilation.Children().Count()} nodes found in {name}");
        return compilation;
    }

    private CppTypeDeclaration? LoadPrebuiltType(string typeName)
    {
        if (_prebuiltTypes.TryGetValue(typeName, out CppType? value))
        {
            return value as CppTypeDeclaration;
        }

        ref CppTypeDeclaration requiredType = ref _inputTypeLookup.Find(typeName);
        if (Unsafe.IsNullRef(ref requiredType))
        {
            Log.Warn($"Can not load prebuilt type {typeName}, maybe it's a dummy type?");
            return default;
        }

        _prebuiltTypes.Add(typeName, requiredType);
        return requiredType;
    }

    private unsafe bool TryWalkClassFieldsUpdate(ref CppClass targetClass)
    {
        if (_walkedClasses.Contains(targetClass.TypeName))
        {
            return true;
        }

        ref CppTypeDeclaration inputType = ref _inputTypeLookup.Find(targetClass);
        if (Unsafe.IsNullRef(ref inputType))
        {
            Log.Warn($"Can not find type {targetClass.TypeName}, use relaxed mode.");
            inputType = ref _inputTypeLookup.Find(targetClass, true);
            if (Unsafe.IsNullRef(ref inputType))
            {
                return false;
            }
        }

        if (inputType is not CppClass { } inputClass)
        {
            return false;
        }

        using var pinnedGCHandleForTargetContainer = new PinnedGCHandle<CppContainerList<CppField>>(targetClass.Fields);
        ref List<CppField> targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForTargetContainer.GetAddressOfObjectData());
        List<CppField> inputFields = GetEffectiveInputFields(inputClass, targetClass);
        List<CppField> rebuiltFields = [];

        // Field-level ignore: fields wrapped in "#pragma apidiff push/pop ignore" keep
        // Core's original type verbatim, and no new input field is auto-added into the
        // wrapped region. Only pay this cost when the feature is actually used.
        bool hasPins = _ignoreRanges.Count > 0 && targetFields.Exists(field => IsOffsetIgnored(field.Span.Start.Offset));
        List<(int Lo, int Hi)> suppressIntervals = [];
        if (hasPins)
        {
            foreach (CppField targetField in targetFields)
            {
                if (IsOffsetIgnored(targetField.Span.Start.Offset))
                {
                    _pinnedFields.Add(targetField);
                }
            }

            suppressIntervals = ComputePinnedInputIntervals(targetFields, inputFields);
        }

        if ((inputFields.Count == targetFields.Count) && (inputFields.Sum(def => def.Type.SizeOf) == targetFields.Sum(def => def.Type.SizeOf)))
        {
            goto COMPARE_SAME_LENGTH;
        }

        for (int i = inputFields.Count - 1; i >= 0; --i)
        {
            CppField inputField = inputFields[i];
            if (targetFields.Find(inputField.IsSameField) is CppField { } matchedField)
            {
                if (hasPins && _pinnedFields.Contains(matchedField))
                {
                    rebuiltFields.Add(matchedField);
                    continue;
                }

                CompareFieldInternal(inputField, matchedField);
                continue;
            }

            if (hasPins && IsInputIndexSuppressed(i, suppressIntervals))
            {
                continue;
            }

            if (!TryUpdateField(targetClass, ref inputField))
            {
                inputField.Comment = UnresolvedComment;
            }

            rebuiltFields.Add(inputField);
        }

        goto MODIFY_AND_RETURN;

    COMPARE_SAME_LENGTH:
        for (int i = inputFields.Count - 1; i >= 0; --i)
        {
            CppField inputField = inputFields[i], targetField = targetFields[i];
            if (hasPins && _pinnedFields.Contains(targetField))
            {
                rebuiltFields.Add(targetField);
                continue;
            }

            CompareFieldInternal(inputField, targetField);
        }
    MODIFY_AND_RETURN:
        _walkedClasses.Add(targetClass.TypeName);
        rebuiltFields.Reverse();
        targetFields = rebuiltFields;
        return true;

        void CompareFieldInternal(CppField f1, CppField f2)
        {
            TryRefineFieldTypeFirstPass(ref f1);
            TryRefineFieldTypeFirstPass(ref f2);
            CppType inputFieldType = f1.Type, targetFieldType = f2.Type;
            if (inputFieldType.IsKnownType && targetFieldType.IsKnownType)
            {
                rebuiltFields.Add(f1);
                return;
            }
            else if (inputFieldType.TypeKind == targetFieldType.TypeKind)
            {
                if (inputFieldType.IsSameType(targetFieldType))
                {
                    rebuiltFields.Add(f2);
                    return;
                }
                else if (inputFieldType.IsPointerType && f1.IsSameField(f2) && targetFieldType.IsKnownType)
                {
                    rebuiltFields.Add(f2);
                    return;
                }
            }
            else if (inputFieldType.TypeKind is CppTypeKind.Primitive or CppTypeKind.Typedef && targetFieldType.TypeKind is CppTypeKind.Enum or CppTypeKind.Primitive)
            {
                rebuiltFields.Add(f2);
                return;
            }
            else if (inputFieldType.SizeOf == targetFieldType.SizeOf && f2.Name.StartsWith(f1.Name)) // dummy padding field
            {
                if (TryUpdateField((CppTypeDeclaration)f2.Parent, ref f1))
                {
                    rebuiltFields.Add(f1);
                    return;
                }
            }
            else if (f1.Name == "vector")
            {
                rebuiltFields.Add(f1);
                return;
            }

            if (!TryUpdateField((CppTypeDeclaration)f2.Parent, ref f1))
            {
                f1.Comment = UnresolvedComment;
            }

            rebuiltFields.Add(f1);
        }
    }

    private CppClass GetEffectiveInputClass(CppClass inputClass)
    {
        if (_effectiveInputClassCache.TryGetValue(inputClass, out CppClass? cachedClass))
        {
            _effectiveInputClassCacheHits++;
            return cachedClass;
        }

        CppClass effectiveClass = inputClass;
        if (inputClass.Name.EndsWith("__Fields"))
        {
            goto CACHE_AND_RETURN;
        }

        ref CppTypeDeclaration fieldsType = ref _inputTypeLookup.Find($"{inputClass.TypeName}__Fields");
        if (Unsafe.IsNullRef(ref fieldsType) || fieldsType is not CppClass fieldsClass)
        {
            goto CACHE_AND_RETURN;
        }

        if (inputClass.Fields.Any(field => field.Name == "fields" && field.Type.TypeName == fieldsClass.TypeName))
        {
            effectiveClass = fieldsClass;
        }

    CACHE_AND_RETURN:
        _effectiveInputClassCache.Add(inputClass, effectiveClass);
        return effectiveClass;
    }

    private List<CppField> GetEffectiveInputFields(CppClass inputClass, CppClass targetClass)
    {
        IReadOnlyList<InputClassLayer> hierarchy = GetInputClassHierarchy(inputClass);
        string? targetBaseName = targetClass.BaseTypes.FirstOrDefault()?.Type.TypeName;
        int stopIndex = hierarchy.Count;
        if (targetBaseName is not null)
        {
            stopIndex = -1;
            for (int i = 0; i < hierarchy.Count; i++)
            {
                if (CppTypeExt.IsSameTypeName(hierarchy[i].LogicalName, targetBaseName, relax: true))
                {
                    stopIndex = i;
                    break;
                }
            }
        }

        if (stopIndex < 0)
        {
            stopIndex = hierarchy.Count;
            if (targetBaseName != "Il2CppObject")
            {
                Log.Warn($"Can not find target base {targetBaseName} in Inspector hierarchy of {inputClass.TypeName}; using the complete input hierarchy.");
            }
        }

        var fields = new List<CppField>();
        for (int i = stopIndex - 1; i >= 0; --i)
        {
            fields.AddRange(hierarchy[i].StorageClass.Fields.Where(field => !IsInspectorBaseField(field)));
        }

        return fields;
    }

    private static bool IsInspectorBaseField(CppField field)
    {
        return field.Name == "_" && field.Type.TypeName.EndsWith("__Fields");
    }

    private IReadOnlyList<InputClassLayer> GetInputClassHierarchy(CppClass inputClass)
    {
        if (_inputClassHierarchyCache.TryGetValue(inputClass, out InputClassLayer[]? cachedHierarchy))
        {
            _inputClassHierarchyCacheHits++;
            return cachedHierarchy;
        }

        var hierarchy = new List<InputClassLayer>();
        var visited = new HashSet<string>();
        bool cycleDetected = false;
        string logicalName = inputClass.Name.EndsWith("__Fields")
            ? inputClass.TypeName[..^8]
            : inputClass.TypeName;
        CppClass storageClass = GetEffectiveInputClass(inputClass);

        while (true)
        {
            if (!visited.Add(storageClass.TypeName))
            {
                cycleDetected = true;
                break;
            }

            hierarchy.Add(new(logicalName, storageClass));

            CppField? inspectorBase = storageClass.Fields.FirstOrDefault(IsInspectorBaseField);
            if (inspectorBase is not null)
            {
                ref CppTypeDeclaration baseFieldsType = ref _inputTypeLookup.Find(inspectorBase.Type.TypeName);
                if (Unsafe.IsNullRef(ref baseFieldsType) || baseFieldsType is not CppClass baseFieldsClass)
                {
                    break;
                }

                storageClass = baseFieldsClass;
                logicalName = baseFieldsClass.TypeName[..^8];
                continue;
            }

            ref CppTypeDeclaration logicalType = ref _inputTypeLookup.Find(logicalName);
            CppClass? logicalClass = !Unsafe.IsNullRef(ref logicalType) && logicalType is CppClass resolvedLogicalClass
                ? resolvedLogicalClass
                : null;
            if (logicalClass?.BaseTypes.FirstOrDefault()?.Type is not CppClass baseClass)
            {
                break;
            }

            logicalName = baseClass.TypeName;
            storageClass = GetEffectiveInputClass(baseClass);
        }

        if (cycleDetected)
        {
            Log.Warn($"Detected a cycle in Inspector hierarchy of {inputClass.TypeName}.");
        }

        InputClassLayer[] result = [.. hierarchy];
        _inputClassHierarchyCache.Add(inputClass, result);
        return result;
    }

    private CppTypeDeclaration? FindNearestTargetAncestor(string inputTypeName)
    {
        if (_nearestTargetAncestorCache.TryGetValue(inputTypeName, out CppTypeDeclaration? cachedAncestor))
        {
            _nearestTargetAncestorCacheHits++;
            return cachedAncestor;
        }

        CppTypeDeclaration? result = null;
        ref CppTypeDeclaration inputType = ref _inputTypeLookup.Find(inputTypeName);
        if (!Unsafe.IsNullRef(ref inputType) && inputType is CppClass inputClass)
        {
            foreach (InputClassLayer layer in GetInputClassHierarchy(inputClass).Skip(1))
            {
                ref CppTypeDeclaration targetAncestor = ref TryFindTargetType(layer.LogicalName);
                if (!Unsafe.IsNullRef(ref targetAncestor))
                {
                    result = targetAncestor;
                    break;
                }
            }
        }

        _nearestTargetAncestorCache.Add(inputTypeName, result);
        return result;
    }

    private readonly record struct InputClassLayer(string LogicalName, CppClass StorageClass);

    private readonly record struct ArrayDefinition(CppCommentText Macro, string ElementTypeName);

    private sealed record PointerArrayDefinition(string ArrayTypeName, string ElementTypeName)
    {
        public HashSet<CppTypeDeclaration> Owners { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private unsafe bool TryWalkEnum(ref CppEnum targetEnum)
    {
        ref CppTypeDeclaration inputType = ref _inputTypeLookup.Find(targetEnum);
        if (Unsafe.IsNullRef(ref inputType))
        {
            Log.Warn($"Can not find enum {targetEnum.TypeName}, use relaxed mode.");
            string itemName = targetEnum.Items.First().Name;
            string searchName = itemName.Contains("__Enum") ? itemName[..(itemName.LastIndexOf("__Enum") + 6)] : itemName;
            Log.Warn($"Searching enum {targetEnum.TypeName} use relaxed mode.");
            inputType = ref _inputTypeLookup.Find(searchName, true);
            if (Unsafe.IsNullRef(ref inputType))
            {
                return false;
            }
        }

        if (inputType is not CppEnum { } inputEnum)
        {
            throw new InvalidOperationException("input declaration is not a enum");
        }

        using var pinnedGCHandleForTargetContainer = new PinnedGCHandle<CppContainerList<CppEnumItem>>(targetEnum.Items);
        ref List<CppEnumItem> targetItems = ref Unsafe.AsRef<List<CppEnumItem>>(pinnedGCHandleForTargetContainer.GetAddressOfObjectData());

        using var pinnedGCHandleForInputContainer = new PinnedGCHandle<CppContainerList<CppEnumItem>>(inputEnum.Items);
        ref List<CppEnumItem> inputItems = ref Unsafe.AsRef<List<CppEnumItem>>(pinnedGCHandleForInputContainer.GetAddressOfObjectData());

        //ICppContainer parent = targetEnum.Parent;
        //targetEnum = ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref inputType);
        //SetElementParent(targetEnum, parent);

        targetItems.Clear();
        targetItems.AddRange(inputItems);

        return true;
    }

    private bool TryUpdateField(CppTypeDeclaration @base, ref CppField field, bool bypassHierarchyCheck = false)
    {
        ref CppType type = ref FieldTypeAsRef(field);
        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef)
        {
            return true;    // pretend that we just insert these types.
        }

        if (type is CppPointerType cppPointer)
        {
            CppType eType = cppPointer.FindPointerBaseType(out _);
            ref CppTypeDeclaration resolvedEType = ref TryFindTargetType(eType); // try to find the very first base type
            if (!Unsafe.IsNullRef(ref resolvedEType))
            {
                return true;
            }
            else
            {
                return TryWalkTypeHierarchy(@base, ref type);                                               // if not, remap type
            }
        }
        else if (type is CppArrayType cppArray)
        {
            CppType eType = cppArray.ElementType;
            if (TryWalkTypeHierarchy(@base, ref eType))
            {
                type = eType;
            }
            else
            {
                type = _prebuiltTypes["Il2CppObject"];
            }
            return true;
        }

        ref CppTypeDeclaration resolvedType = ref TryFindTargetType(type);
        if (!Unsafe.IsNullRef(ref resolvedType))
        {
            return true;
        }

        ref List<CppType>? types = ref CollectionsMarshal.GetValueRefOrAddDefault(_insertionMap, @base, out bool exists);
        if (!exists)
        {
            types = [];
        }

        if (bypassHierarchyCheck || TryWalkTypeHierarchy(@base, ref type))
        {
            Debug.Assert(!type.HasElementType);

            types!.Insert(0, type);
            AddInsertionType(type);
            return true;
        }

        return false;
    }

    private bool TryWalkTypeHierarchy(CppTypeDeclaration @base, ref CppType type, bool deep = false)
    {
        if (_walkedClasses.Contains(type.TypeName))
        {
            return true;
        }

        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef)
        {
            return deep;
        }

        if (type.HasElementType && !type.IsPointerType)
        {
            type = ((CppTypeWithElementType)type).ElementType;
            return TryWalkTypeHierarchy(@base, ref type);
        }

        if (type is CppClass cppClass)
        {
            TryWalkClassFieldsNew(@base, ref cppClass);
        }
        else if (type is CppEnum cppEnum)
        {
            ref CppTypeDeclaration resolvedEnumType = ref _targetTypeLookup.Find(cppEnum.FullName);
            if (Unsafe.IsNullRef(ref resolvedEnumType))
            {
                type = _prebuiltTypes["int32_t"];
            }
        }
        else if (type is CppPointerType pointerType)
        {
            ref CppType refinedType = ref ClassElementTypeAsRef(pointerType);
            TryRefineTypeSecondPass(ref refinedType, @base);
        }

        return true;
    }

    private unsafe void TryWalkClassFieldsNew(CppTypeDeclaration @base, ref CppClass @class)
    {
        CppClass effectiveClass = GetEffectiveInputClass(@class);
        using var pinnedGCHandleForClassContainer = new PinnedGCHandle<CppContainerList<CppField>>(effectiveClass.Fields);
        ref List<CppField> targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForClassContainer.GetAddressOfObjectData());
        foreach (ref CppField field in CollectionsMarshal.AsSpan(targetFields))
        {
            if (IsInspectorBaseField(field))
            {
                continue;
            }

            ref CppType fieldType = ref FieldTypeAsRef(field);

            if (!fieldType.IsPointerType)
            {
                if (TryRefineFieldTypeFirstPass(ref field))
                {
                    continue;
                }

                // An inserted type may have a by-value class member whose type is not
                // present in the target (e.g. ACTkByte4, AttackRangeDescModel). Such a
                // member cannot be left as-is (undefined type) and cannot be walked into
                // a definition here, so fall back to Il2CppObject to keep the emitted
                // header compilable. Members resolvable in the target still walk normally.
                if (fieldType is CppClass)
                {
                    ref CppTypeDeclaration resolvedFieldType = ref TryFindTargetType(fieldType);
                    if (Unsafe.IsNullRef(ref resolvedFieldType))
                    {
                        RemapType(ref fieldType, _prebuiltTypes["Il2CppObject"]);
                        continue;
                    }
                }

                if (!TryWalkTypeHierarchy(@base, ref fieldType, true))
                {
                    field.Comment = UnresolvedComment;
                }

                continue;
            }

            if (!TryRefineTypeSecondPass(ref fieldType, @base))
            {
                field.Comment = UnresolvedComment;
            }
        }
    }

    private bool TryRefineTypeSecondPass(ref CppType type, CppTypeDeclaration owner)
    {
        string typeName = type.TypeName;
        bool isGeneric = type.IsGenericType;
        ref CppTypeDeclaration resolvedTargetType = ref TryFindTargetType(typeName);
        bool resolvedInMap = ContainsInsertionType(typeName);
        if (Unsafe.IsNullRef(ref resolvedTargetType) && !resolvedInMap)
        {
            if (TryDefineArray(owner, type))
            {
                return true;
            }
            else if (CppTypeExt.GlobalConfig.KnownReservedSuffixesFast.Find(suffix => typeName.EndsWith(suffix)) is string { } matched)
            {
                CppType remappedType = _prebuiltTypes[CppTypeExt.GlobalConfig.KnownReservedSuffixes[matched]];
                if (remappedType is not CppEnum)
                {
                    RemapType(ref type, remappedType);
                    return true;
                }
            }
            else if (isGeneric && (typeName.StartsWith("Action_") || typeName.StartsWith("Func_")))
            {
                ref CppType builtInActionType = ref CollectionsMarshal.GetValueRefOrNullRef(_prebuiltTypes, "Action");
                if (!Unsafe.IsNullRef(ref builtInActionType))
                {
                    RemapType(ref type, builtInActionType);
                    return true;
                }
            }

            if (FindNearestTargetAncestor(typeName) is CppTypeDeclaration nearestTargetAncestor)
            {
                RemapType(ref type, nearestTargetAncestor);
                return true;
            }

            RemapType(ref type, _prebuiltTypes["Il2CppObject"]);
        }
        return true;
    }

    private bool TryDefineArray(CppTypeDeclaration owner, CppType type)
    {
        if (type is not CppClass arrayClass || !arrayClass.TypeName.EndsWith("__Array", StringComparison.Ordinal))
        {
            return false;
        }

        CppField? vectorField = arrayClass.Fields.FirstOrDefault(field => field.Name == "vector");
        if (vectorField?.Type is not CppArrayType vectorType)
        {
            return false;
        }

        bool storesPointers = vectorType.ElementType is CppPointerType;
        CppType elementType = storesPointers
            ? ((CppPointerType)vectorType.ElementType).FindPointerBaseType(out _)
            : vectorType.ElementType;
        ref CppTypeDeclaration targetElementType = ref TryFindTargetType(elementType);
        if (Unsafe.IsNullRef(ref targetElementType))
        {
            return false;
        }

        if (storesPointers && targetElementType is CppClass && !_targetGlobalDeclarations.Contains(targetElementType))
        {
            ref PointerArrayDefinition? definition = ref CollectionsMarshal.GetValueRefOrAddDefault(_pointerArrayDefinitionsByName, arrayClass.TypeName, out bool exists);
            if (!exists)
            {
                definition = new(arrayClass.TypeName, targetElementType.TypeName);
                Log.Debug($"Defining {arrayClass.TypeName} with {MacroIdArrayPtr.Text}({targetElementType.TypeName}).");
            }

            definition!.Owners.Add(owner);
        }
        else if (_definedArrayTypes.Add(arrayClass.TypeName))
        {
            ref List<ArrayDefinition>? definitions = ref CollectionsMarshal.GetValueRefOrAddDefault(_arrayDefinitionsByElementType, targetElementType, out bool exists);
            if (!exists)
            {
                definitions = [];
            }

            CppCommentText macro = storesPointers ? MacroIdArrayPtr : MacroIdArray;
            definitions!.Add(new(macro, targetElementType.TypeName));
            Log.Debug($"Defining {arrayClass.TypeName} with {macro.Text}({targetElementType.TypeName}).");
        }

        return true;
    }

    private void AddInsertionType(CppType type)
    {
        string key = CppTypeExt.GetLookupKey(type);
        ref List<CppType>? candidates = ref CollectionsMarshal.GetValueRefOrAddDefault(_insertionTypesByName, key, out bool exists);
        if (!exists)
        {
            candidates = [];
        }

        candidates!.Add(type);
    }

    private bool ContainsInsertionType(string typeName)
    {
        string remappedName = CppTypeExt.RemapLookupTypeName(typeName);
        string key = CppTypeExt.GetLookupKey(remappedName, false);
        return _insertionTypesByName.TryGetValue(key, out List<CppType>? candidates)
            && candidates.Exists(candidate => candidate.IsSameType(remappedName));
    }

    private ref CppTypeDeclaration TryFindTargetType(CppType type)
    {
        ref CppTypeDeclaration resolvedType = ref _targetTypeLookup.Find(type);
        if (!Unsafe.IsNullRef(ref resolvedType))
        {
            return ref resolvedType;
        }

        return ref _targetGlobalTypeLookup.Find(type);
    }

    private ref CppTypeDeclaration TryFindTargetType(string typeName)
    {
        ref CppTypeDeclaration resolvedType = ref _targetTypeLookup.Find(typeName);
        if (!Unsafe.IsNullRef(ref resolvedType))
        {
            return ref resolvedType;
        }

        return ref _targetGlobalTypeLookup.Find(typeName);
    }

    private static unsafe bool TryRefineFieldTypeFirstPass(ref CppField field)
    {
        if (field.Type is not CppClass fieldClass)
        {
            return false;
        }

        if (field.Type.TypeName.Contains("FP"))
        {
            return false;
        }

        using var pinnedGCHandleForClassContainer = new PinnedGCHandle<CppContainerList<CppField>>(fieldClass.Fields);
        ref List<CppField> targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForClassContainer.GetAddressOfObjectData());
        if (targetFields.Count != 1 || !targetFields.TrueForAll(f => f.Type.IsPrimitiveType || (f.Type.IsTypeDef && ((CppTypedef)f.Type).ElementType.IsPrimitiveType)))
        {
            return false;
        }

        CppType refineType = targetFields[0].Type;
        field.Type = refineType.IsTypeDef ? ((CppTypedef)refineType).ElementType : refineType;
        return true;
    }

    private static void RemapType(ref CppType target, CppType type)
    {
        if (!target.HasElementType)
        {
            Log.Debug($"{target.FullName} => {type.FullName}");
            target = type;
            return;
        }

        ref CppType refToElementType = ref ClassElementTypeAsRef((CppTypeWithElementType)target);
        string oldName = target.FullName;
        refToElementType = type;
        Log.Debug($"{oldName} => {target.FullName}");
    }

    private static List<int> FindAllOccurrencesMacroIndex(string source, string macroName)
    {
        var searchValues = SearchValues.Create([macroName], StringComparison.Ordinal);
        List<int> indexes = [];

        ReadOnlySpan<char> span = source.AsSpan();
        int baseIndex = 0;
        while (true)
        {
            int foundIndex = span.IndexOfAny(searchValues);
            if (foundIndex == -1)
            {
                break;
            }

            indexes.Add(baseIndex + foundIndex);

            baseIndex += foundIndex + macroName.Length;
            span = source.AsSpan(baseIndex);
        }

        return indexes;
    }

    // Pairs "#pragma apidiff push ignore" / "#pragma apidiff pop ignore" markers in the
    // (already-mutated) target text into sorted, non-overlapping offset ranges. Nesting is
    // handled with a depth counter; an unclosed push extends to end of file and a stray pop
    // is ignored, so malformed markers degrade deterministically instead of throwing.
    private void ComputeIgnoreRanges(string source)
    {
        List<int> pushes = FindAllOccurrencesMacroIndex(source, IgnorePushToken);
        List<int> pops = FindAllOccurrencesMacroIndex(source, IgnorePopToken);
        if (pushes.Count == 0)
        {
            return;
        }

        var events = new List<(int Offset, bool IsPush)>(pushes.Count + pops.Count);
        foreach (int offset in pushes)
        {
            events.Add((offset, true));
        }
        foreach (int offset in pops)
        {
            events.Add((offset, false));
        }
        events.Sort((left, right) => left.Offset.CompareTo(right.Offset));

        int depth = 0, start = -1;
        foreach ((int offset, bool isPush) in events)
        {
            if (isPush)
            {
                if (depth++ == 0)
                {
                    start = offset;
                }
            }
            else if (depth > 0 && --depth == 0)
            {
                _ignoreRanges.Add((start, offset));
            }
        }

        if (depth > 0)
        {
            _ignoreRanges.Add((start, int.MaxValue));
        }
    }

    private bool IsOffsetIgnored(int offset)
    {
        foreach ((int start, int end) in _ignoreRanges)
        {
            if (offset >= start && offset < end)
            {
                return true;
            }
        }

        return false;
    }

    // For each maximal run of pinned target fields, maps the run to an open interval of
    // input indices (bounded by the input positions of the unpinned Core fields adjacent
    // to the run). Unmatched input fields inside such an interval are the ones the user
    // deleted, and must not be auto-added back.
    private List<(int Lo, int Hi)> ComputePinnedInputIntervals(List<CppField> targetFields, List<CppField> inputFields)
    {
        var inputIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < inputFields.Count; k++)
        {
            inputIndex.TryAdd(inputFields[k].Name, k);
        }

        var intervals = new List<(int Lo, int Hi)>();
        int count = targetFields.Count;
        int t = 0;
        while (t < count)
        {
            if (!IsOffsetIgnored(targetFields[t].Span.Start.Offset))
            {
                t++;
                continue;
            }

            int runStart = t;
            while (t < count && IsOffsetIgnored(targetFields[t].Span.Start.Offset))
            {
                t++;
            }

            int lo = -1;
            if (runStart - 1 >= 0 && inputIndex.TryGetValue(targetFields[runStart - 1].Name, out int headIndex))
            {
                lo = headIndex;
            }

            int hi = inputFields.Count;
            if (t < count && inputIndex.TryGetValue(targetFields[t].Name, out int tailIndex))
            {
                hi = tailIndex;
            }

            intervals.Add((lo, hi));
        }

        return intervals;
    }

    private static bool IsInputIndexSuppressed(int index, List<(int Lo, int Hi)> intervals)
    {
        foreach ((int lo, int hi) in intervals)
        {
            if (index > lo && index < hi)
            {
                return true;
            }
        }

        return false;
    }

    private bool ClassHasPinnedFields(CppClass @class)
    {
        foreach (CppField field in @class.Fields)
        {
            if (_pinnedFields.Contains(field))
            {
                return true;
            }
        }

        return false;
    }

    // Emits a class definition with "#pragma apidiff push/pop ignore" markers wrapping the
    // pinned field run(s), so field-level ignores round-trip into the regenerated header.
    private void AppendPinnedClassDefinition(StringBuilder builder, CppClass @class)
    {
        builder.Append(@class.ClassKind.ToString().ToLower());
        if (!string.IsNullOrWhiteSpace(@class.Name))
        {
            builder.Append($" {@class.Name}");
        }

        List<CppBaseType> baseTypes = @class.BaseTypes;
        if (baseTypes.Count != 0)
        {
            builder.Append(" : ");
            for (int i = baseTypes.Count - 1; i >= 0; --i)
            {
                builder.Append(baseTypes[i].Type.TypeName);
                if (i != 0)
                {
                    builder.Append(',');
                }
            }
        }

        builder.AppendLine(" {");
        bool inPinnedRun = false;
        foreach (CppField field in @class.Fields)
        {
            bool pinned = _pinnedFields.Contains(field);
            if (pinned && !inPinnedRun)
            {
                builder.AppendLine($"    {IgnorePushMarker}");
                inPinnedRun = true;
            }
            else if (!pinned && inPinnedRun)
            {
                builder.AppendLine($"    {IgnorePopMarker}");
                inPinnedRun = false;
            }

            builder.AppendLine($"    {field.ConstructDefinition()};");
        }

        if (inPinnedRun)
        {
            builder.AppendLine($"    {IgnorePopMarker}");
        }

        builder.Append('}');
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<ElementType>k__BackingField")]
    private static extern ref CppType ClassElementTypeAsRef(CppTypeWithElementType @this);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Type>k__BackingField")]
    private static extern ref CppType FieldTypeAsRef(CppField @this);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Parent")]
    private static extern void SetElementParent(CppElement @this, ICppContainer parent);

    const string CONST_HEADER = @"#pragma once

#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored ""-Wunknown-pragmas""
#elif defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable: 4068)
#endif

#if defined(__i386__) || defined(__arm__)
#define IS_32BIT
#endif

#ifndef DO_ARRAY_DEFINE
#define DO_ARRAY_DEFINE(E_NAME) \
struct  E_NAME ## __Array : Il2CppObject { \
Il2CppArrayBounds *bounds; \
il2cpp_array_size_t max_length; \
E_NAME vector[32]; \
};
#endif
#ifndef DO_ARRAY_DEFINE_PTR
#define DO_ARRAY_DEFINE_PTR(E_NAME) \
struct  E_NAME ## __Array : Il2CppObject { \
Il2CppArrayBounds *bounds; \
il2cpp_array_size_t max_length; \
E_NAME *vector[32]; \
};
#endif
#ifndef DO_LIST_DEFINE
#define DO_LIST_DEFINE(E_NAME) \
DO_ARRAY_DEFINE(E_NAME) \
struct List_1_ ## E_NAME : Il2CppObject { \
struct E_NAME ## __Array *_items; \
int32_t _size; \
int32_t _version; \
};
#endif

#include <cstdint>
#include ""il2cpp-class.h""";

    const string CONST_FOOTER = @"#if defined(__clang__)
#pragma clang diagnostic pop
#elif defined(_MSC_VER)
#pragma warning(pop)
#endif";

}
