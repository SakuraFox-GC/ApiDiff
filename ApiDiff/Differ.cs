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

    private readonly List<CppTypeDeclaration> _inputDeclarations = [], _targetDeclarations = [], _targetGlobalDeclarations = [];
    private readonly Dictionary<string, CppType> _prebuiltTypes = [];
    private readonly List<CppType> _insertedTypes = [];
    private readonly Dictionary<CppTypeDeclaration, List<CppType>> _insertionMap = [];
    private readonly Dictionary<CppCommentText, List<int>> _macrosExpansionIndex = [];
    private HashSet<string> _targetIncludes = [];
    private readonly HashSet<string> _walkedClasses = [];
    private CppCompilation? _inputCompilation, _targetCompilation;
    private bool _typeSystemWalked = false;

    public bool BuildTypeModel()
    {
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

        _inputCompilation = TryParseHeader(File.ReadAllText(InputHeader), "input", cppParserOptions);
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
        _targetCompilation = TryParseHeader(targetFileContent, "target", cppParserOptions);
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
        _targetDeclarations.RemoveAll(def => def is CppClass && def.SizeOf == 0);
        Span<CppTypeDeclaration> rawData = CollectionsMarshal.AsSpan(_targetDeclarations);
        for (int i = rawData.Length - 1; i >= 0; --i)
        {
            ref CppTypeDeclaration originalType = ref rawData[i];
            string typeKind = originalType.TypeKind.ToString().ToLower();
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

        _typeSystemWalked = true;
        return true;
    }

    public string ConstructDefinitions()
    {
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
        }
        headerBuilder.AppendLine("namespace app {");
        headerBuilder.AppendLine();
        foreach (CppEnum @enum in _targetDeclarations.OfType<CppEnum>())
        {
            headerBuilder.AppendLine($"{@enum.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        foreach (CppEnum insertedEnum in _insertedTypes.OfType<CppEnum>())
        {
            headerBuilder.AppendLine($"{insertedEnum.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        foreach (ref CppTypeDeclaration data in CollectionsMarshal.AsSpan(_targetDeclarations))
        {
            if (data is not CppClass { } @class)
            {
                continue;
            }

            if (UnresolvedComment.Equals(data.Comment))
            {
                continue;
            }

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
            headerBuilder.AppendLine($"{@class.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        headerBuilder.AppendLine("}");

        return headerBuilder.ToString();
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

        ref CppTypeDeclaration requiredType = ref _inputDeclarations.TryFindType(typeName);
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

        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetClass);
        if (Unsafe.IsNullRef(ref inputType))
        {
            Log.Warn($"Can not find type {targetClass.TypeName}, use relaxed mode.");
            inputType = ref _inputDeclarations.TryFindType(targetClass, true);
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
        if ((inputFields.Count == targetFields.Count) && (inputFields.Sum(def => def.Type.SizeOf) == targetFields.Sum(def => def.Type.SizeOf)))
        {
            goto COMPARE_SAME_LENGTH;
        }

        for (int i = inputFields.Count - 1; i >= 0; --i)
        {
            CppField inputField = inputFields[i];
            if (targetFields.Find(inputField.IsSameField) is CppField { } matchedField)
            {
                CompareFieldInternal(inputField, matchedField);
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
        if (inputClass.Name.EndsWith("__Fields"))
        {
            return inputClass;
        }

        ref CppTypeDeclaration fieldsType = ref _inputDeclarations.TryFindType($"{inputClass.TypeName}__Fields");
        if (Unsafe.IsNullRef(ref fieldsType) || fieldsType is not CppClass fieldsClass)
        {
            return inputClass;
        }

        if (inputClass.Fields.Any(field => field.Name == "fields" && field.Type.TypeName == fieldsClass.TypeName))
        {
            return fieldsClass;
        }

        return inputClass;
    }

    private List<CppField> GetEffectiveInputFields(CppClass inputClass, CppClass targetClass)
    {
        List<InputClassLayer> hierarchy = GetInputClassHierarchy(inputClass);
        string? targetBaseName = targetClass.BaseTypes.FirstOrDefault()?.Type.TypeName;
        int stopIndex = targetBaseName is null
            ? hierarchy.Count
            : hierarchy.FindIndex(layer => layer.LogicalName == targetBaseName);

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

    private List<InputClassLayer> GetInputClassHierarchy(CppClass inputClass)
    {
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
                ref CppTypeDeclaration baseFieldsType = ref _inputDeclarations.TryFindType(inspectorBase.Type.TypeName);
                if (Unsafe.IsNullRef(ref baseFieldsType) || baseFieldsType is not CppClass baseFieldsClass)
                {
                    break;
                }

                storageClass = baseFieldsClass;
                logicalName = baseFieldsClass.TypeName[..^8];
                continue;
            }

            ref CppTypeDeclaration logicalType = ref _inputDeclarations.TryFindType(logicalName);
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

        return hierarchy;
    }

    private CppTypeDeclaration? FindNearestTargetAncestor(string inputTypeName)
    {
        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(inputTypeName);
        if (Unsafe.IsNullRef(ref inputType) || inputType is not CppClass inputClass)
        {
            return null;
        }

        foreach (InputClassLayer layer in GetInputClassHierarchy(inputClass).Skip(1))
        {
            ref CppTypeDeclaration targetAncestor = ref TryFindTargetType(layer.LogicalName);
            if (!Unsafe.IsNullRef(ref targetAncestor))
            {
                return targetAncestor;
            }
        }

        return null;
    }

    private readonly record struct InputClassLayer(string LogicalName, CppClass StorageClass);

    private unsafe bool TryWalkEnum(ref CppEnum targetEnum)
    {
        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetEnum);
        if (Unsafe.IsNullRef(ref inputType))
        {
            Log.Warn($"Can not find enum {targetEnum.TypeName}, use relaxed mode.");
            string itemName = targetEnum.Items.First().Name;
            string searchName = itemName.Contains("__Enum") ? itemName[..(itemName.LastIndexOf("__Enum") + 6)] : itemName;
            Log.Warn($"Searching enum {targetEnum.TypeName} use relaxed mode.");
            inputType = ref _inputDeclarations.TryFindType(searchName, true);
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
            ref CppTypeDeclaration resolvedEnumType = ref _targetDeclarations.TryFindType(cppEnum.FullName);
            if (Unsafe.IsNullRef(ref resolvedEnumType))
            {
                type = _prebuiltTypes["int32_t"];
            }
        }
        else if (type is CppPointerType pointerType)
        {
            ref CppType refinedType = ref ClassElementTypeAsRef(pointerType);
            TryRefineTypeSecondPass(ref refinedType);
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

                if (!TryWalkTypeHierarchy(@base, ref fieldType, true))
                {
                    field.Comment = UnresolvedComment;
                }

                continue;
            }

            if (!TryRefineTypeSecondPass(ref fieldType))
            {
                field.Comment = UnresolvedComment;
            }
        }
    }

    private bool TryRefineTypeSecondPass(ref CppType type)
    {
        string typeName = type.TypeName;
        bool isGeneric = type.IsGenericType;
        ref CppTypeDeclaration resolvedTargetType = ref TryFindTargetType(typeName);
        bool resolvedInMap = false;
        foreach ((CppTypeDeclaration _, List<CppType>? insertionList) in _insertionMap)
        {
            ref CppType insertedMatched = ref insertionList.TryFindType(typeName);
            if (!Unsafe.IsNullRef(ref insertedMatched))
            {
                resolvedInMap = true;
                break;
            }
        }
        if (Unsafe.IsNullRef(ref resolvedTargetType) && !resolvedInMap)
        {
            if (CppTypeExt.GlobalConfig.KnownReservedSuffixesFast.Find(suffix => typeName.EndsWith(suffix)) is string { } matched)
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

    private ref CppTypeDeclaration TryFindTargetType(CppType type)
    {
        ref CppTypeDeclaration resolvedType = ref _targetDeclarations.TryFindType(type);
        if (!Unsafe.IsNullRef(ref resolvedType))
        {
            return ref resolvedType;
        }

        return ref _targetGlobalDeclarations.TryFindType(type);
    }

    private ref CppTypeDeclaration TryFindTargetType(string typeName)
    {
        ref CppTypeDeclaration resolvedType = ref _targetDeclarations.TryFindType(typeName);
        if (!Unsafe.IsNullRef(ref resolvedType))
        {
            return ref resolvedType;
        }

        return ref _targetGlobalDeclarations.TryFindType(typeName);
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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<ElementType>k__BackingField")]
    private static extern ref CppType ClassElementTypeAsRef(CppTypeWithElementType @this);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Type>k__BackingField")]
    private static extern ref CppType FieldTypeAsRef(CppField @this);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Parent")]
    private static extern void SetElementParent(CppElement @this, ICppContainer parent);

    const string CONST_HEADER = @"#pragma once

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

}
