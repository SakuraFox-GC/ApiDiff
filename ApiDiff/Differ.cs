using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using CppAst;

namespace ApiDiff;

internal class Differ(string InputHeader, string TargetHeader, string IncludeDir)
{

    private static readonly CppCommentText UnresolvedComment = new() { Text = "Unresolved" }, MacroIdArray = new() { Text = "DO_ARRAY_DEFINE" }, MacroIdList = new() { Text = "DO_LIST_DEFINE" };

    private readonly List<CppTypeDeclaration> _inputDeclarations = [], _targetDeclarations = [], _targetGlobalDeclarations = [];
    private readonly Dictionary<string, CppType> _prebuiltTypes = [];
    private readonly List<CppType> _insertedTypes = [];
    private readonly Dictionary<CppTypeDeclaration, List<CppType>> _insertionMap = [];
    private readonly Dictionary<CppCommentText, List<int>> _macrosExpansionIndex = [];
    private HashSet<string> _targetIncludes = [], _walkedClasses = [];
    private CppCompilation? _inputCompilation, _targetCompilation;
    private bool _typeSystemWalked = false;

    public bool BuildTypeModel()
    {
        if (_typeSystemWalked)
            return false;

        CppParserOptions cppParserOptions = new() { TargetCpu = CppTargetCpu.ARM64, TargetSystem = "linux", ParseMacros = true };
        cppParserOptions.Defines.Add("_IDACLANG_=1");
        var sysRootInclude = new DirectoryInfo(IncludeDir);
        cppParserOptions.IncludeFolders.Add(new FileInfo(TargetHeader).Directory!.FullName);
        cppParserOptions.SystemIncludeFolders.Add(Path.Combine(sysRootInclude.FullName, "c++", "v1"));
        cppParserOptions.SystemIncludeFolders.Add(sysRootInclude.FullName);

        _inputCompilation = TryParseHeader(File.ReadAllText(InputHeader), "input", cppParserOptions);
        if (_inputCompilation.HasErrors)
            return false;

        var targetFileContent = File.ReadAllText(TargetHeader).Replace("#pragma once", $"#pragma once\ntypedef unsigned long size_t;");
        _macrosExpansionIndex.Add(MacroIdArray, FindAllOccurrencesMacroIndex(targetFileContent, "DO_ARRAY_DEFINE"));
        _macrosExpansionIndex.Add(MacroIdList, FindAllOccurrencesMacroIndex(targetFileContent, "DO_LIST_DEFINE"));
        _targetCompilation = TryParseHeader(targetFileContent, "target", cppParserOptions);
        if (_targetCompilation.HasErrors)
            return false;

        _targetIncludes = [.. _targetCompilation.InclusionDirectives.Select(targetInclude => { return targetInclude.FileName; })];
        var appNamespace = _targetCompilation.Namespaces.FirstOrDefault(@namespace => @namespace.Name == "app");
        if (appNamespace is null)
            return false;

        _inputDeclarations.AddRange([.. _inputCompilation.Typedefs, .. _inputCompilation.Enums, .. _inputCompilation.Classes]);
        _targetDeclarations.AddRange([.. appNamespace.Enums, .. appNamespace.Classes]);

        _inputDeclarations.AddRange([.. _inputCompilation.Typedefs, .. _inputCompilation.Enums, .. _inputCompilation.Classes]);
        _targetGlobalDeclarations.AddRange(_targetCompilation.Children().OfType<CppTypeDeclaration>());
        _targetDeclarations.AddRange(_targetGlobalDeclarations.Where(def => def.Parent is CppNamespace));
        _inputDeclarations.SortBySourceLocation();
        _targetDeclarations.SortBySourceLocation();

        foreach (var targetDeclaration in _targetDeclarations)
        {
            SetElementParent(targetDeclaration, null!);
            foreach (var (macroID, indexes) in _macrosExpansionIndex)
            {
                if (indexes.Exists(idx => idx == targetDeclaration.Span.Start.Offset))
                    targetDeclaration.Comment = macroID;
            }
        }

        foreach (var knownName in CppTypeExt.GlobalConfig.KnownNames)
        {
            LoadPrebuiltType(knownName);
        }
        foreach (var knownName in CppTypeExt.GlobalConfig.KnownReservedSuffixes.Values)
        {
            LoadPrebuiltType(knownName);
        }

        _targetDeclarations.RemoveAll(def => def.SizeOf == 0);
        var rawData = CollectionsMarshal.AsSpan(_targetDeclarations);
        for (int i = rawData.Length - 1; i >= 0; --i)
        {
            ref var originalType = ref rawData[i];
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
            else if (originalType is CppEnum)
            {
                if (TryWalkEnum(ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref originalType)))
                {
                    Log.Info($"{typeKind} {originalType.TypeName} resolved successfully.");
                    continue;
                }
            }

            if (originalType.Comment.Equals(MacroIdArray) || originalType.Comment.Equals(MacroIdList))
            {
                Log.Info($"Skipping expanded {typeKind} {originalType.TypeName}.");
            }
            else
            {
                Log.Warn($"Skipping invalid {typeKind} {originalType.TypeName}.");
                originalType.Comment = UnresolvedComment;
            }
        }

        foreach (ref var targetType in rawData)
        {
            ref var insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, targetType);
            if (Unsafe.IsNullRef(ref insertionList))
                continue;

            var removalList = new List<CppType>();
            foreach (var typeToInsert in insertionList!)
            {
                if (_insertedTypes.Any(insertedType => insertedType.IsSameType(typeToInsert)))
                {
                    removalList.Add(typeToInsert);
                    continue;
                }

                _insertedTypes.Add(typeToInsert);
            }

            foreach (var typeToRemove in removalList)
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
        foreach (var neededGlobalType in CppTypeExt.GlobalConfig.GetBuiltInTypes())
        {
            if (_targetCompilation.Classes.FirstOrDefault(def => def.TypeName == neededGlobalType) is not CppClass @class)
                continue;

            globalDeclarations.Add(@class);
        }
        globalDeclarations.InsertRange(0, _targetCompilation.Typedefs.Where(def => !_targetIncludes.Contains(def.SourceFile) && def.Name != "size_t"));
        foreach (var def in globalDeclarations)
        {
            headerBuilder.AppendLine($"{def.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        headerBuilder.AppendLine("namespace app {");
        headerBuilder.AppendLine();
        foreach (var @enum in _targetDeclarations.OfType<CppEnum>())
        {
            headerBuilder.AppendLine($"{@enum.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        foreach (var insertedEnum in _insertedTypes.OfType<CppEnum>())
        {
            headerBuilder.AppendLine($"{insertedEnum.ConstructDefinition()};");
            headerBuilder.AppendLine();
        }
        foreach (ref var data in CollectionsMarshal.AsSpan(_targetDeclarations))
        {
            if (data is not CppClass { } @class)
                continue;

            if (UnresolvedComment.Equals(data.Comment))
                continue;

            if (MacroIdList.Equals(data.Comment) || MacroIdArray.Equals(data.Comment))
            {
                var macroName = ((CppCommentText)data.Comment).Text;
                var typeName = @class.Name;
                if (typeName.EndsWith("__Array"))
                {
                    if (MacroIdList.Equals(data.Comment))
                        continue;
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

            ref var insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, data);
            if (!Unsafe.IsNullRef(ref insertionList))
            {
                foreach (var insertedType in insertionList)
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
            var errors = compilation.Diagnostics.Messages.Where(message => message.Type == CppLogMessageType.Error);
            Log.Error($"Compilation ended with {errors.Count()} errors generated.");
            Log.FloodColour = true;
            foreach (var error in errors)
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
            return value as CppTypeDeclaration;

        ref var requiredType = ref _inputDeclarations.TryFindType(typeName);
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
            return true;

        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetClass);
        if (Unsafe.IsNullRef(ref inputType))
            return false;

        if (inputType is not CppClass { } inputClass)
            return false;

        using var pinnedGCHandleForTargetContainer = new PinnedGCHandle<CppContainerList<CppField>>(targetClass.Fields);
        ref var targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForTargetContainer.GetAddressOfObjectData());
        CppContainerList<CppField> inputContainer = inputClass.Fields;
        List<CppField> rebuiltFields = [];
        if ((inputContainer.Count == targetFields.Count) && (inputContainer.Sum(def => def.Type.SizeOf) == targetFields.Sum(def => def.Type.SizeOf)))
        {
            goto COMPARE_SAME_LENGTH;
        }

        var lastFieldInTarget = targetFields[^1];
        var targetBaseFields = new List<CppField>();
        var baseTypes = targetClass.BaseTypes;
        while (baseTypes is List<CppBaseType> { Count: > 0 })
        {
            var baseClass = (CppClass)baseTypes[0].Type;
            if (TryWalkClassFieldsUpdate(ref baseClass))
            {
                for (int i = baseClass.Fields.Count - 1; i >= 0; --i)
                {
                    var baseField = baseClass.Fields[i];
                    if (targetBaseFields.Any(def => def.Name == baseField.Name))
                        continue;

                    targetBaseFields.Add(baseField);
                }
            }

            baseTypes = baseClass.BaseTypes;
        }

        var lastFieldInInput = inputContainer.FirstOrDefault(field => field.IsSameField(lastFieldInTarget));
        var indexOfLastField = lastFieldInInput is null ? -1 : inputContainer.IndexOf(lastFieldInInput);
        for (int i = inputContainer.Count - 1; i >= 0; --i)
        {
            if (indexOfLastField > -1 && i > indexOfLastField)
                continue;

            CppField inputField = inputContainer[i];
            if (targetFields.Find(inputField.IsSameField) is CppField { } matchedField)
            {
                CompareFieldInternal(inputField, matchedField);
                continue;
            }

            if (targetBaseFields.Find(inputField.IsSameField) is CppField { })
                continue;

            if (!TryUpdateField(targetClass, ref inputField))
                inputField.Comment = UnresolvedComment;

            rebuiltFields.Add(inputField);
        }

        goto MODIFY_AND_RETURN;

    COMPARE_SAME_LENGTH:
        for (int i = inputContainer.Count - 1; i >= 0; --i)
        {
            CppField inputField = inputContainer[i], targetField = targetFields[i];
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

            if (!TryUpdateField((CppTypeDeclaration)f2.Parent, ref f1))
                f1.Comment = UnresolvedComment;

            rebuiltFields.Add(f1);
        }
    }

    private bool TryWalkEnum(ref CppEnum targetEnum)
    {
        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetEnum);
        if (Unsafe.IsNullRef(ref inputType))
            return false;

        if (inputType is not CppEnum { } inputEnum)
            throw new InvalidOperationException("input declaration is not a enum");

        var parent = targetEnum.Parent;
        targetEnum = ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref inputType);
        SetElementParent(targetEnum, parent);

        return true;
    }

    private bool TryUpdateField(CppTypeDeclaration @base, ref CppField field, bool bypassHierarchyCheck = false)
    {
        ref var type = ref FieldTypeAsRef(field);
        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef)
            return true;    // pretend that we just insert these types.

        if (type is CppPointerType cppPointer)
        {
            var eType = cppPointer.FindPointerBaseType(out _);
            ref CppTypeDeclaration resolvedEType = ref _targetGlobalDeclarations.TryFindType(eType); // try to find the very first base type
            if (Unsafe.IsNullRef(ref resolvedEType))
                return TryWalkTypeHierarchy(@base, ref type);                                               // if not, remap type
        }

        ref CppTypeDeclaration resolvedType = ref _targetGlobalDeclarations.TryFindType(type);
        if (!Unsafe.IsNullRef(ref resolvedType))
            return true;

        ref var types = ref CollectionsMarshal.GetValueRefOrAddDefault(_insertionMap, @base, out var exists);
        if (!exists)
            types = [];

        if (bypassHierarchyCheck || TryWalkTypeHierarchy(@base, ref type))
        {
            Debug.Assert(!type.HasElementType);

            types!.Insert(0, type);
            return true;
        }

        return false;
    }

    private unsafe bool TryWalkTypeHierarchy(CppTypeDeclaration @base, ref CppType type, bool deep = false)
    {
        if (_walkedClasses.Contains(type.TypeName))
            return true;

        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef)
            return deep;

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
                type = _prebuiltTypes["int32_t"];
        }
        else if (type is CppPointerType pointerType)
        {
            ref var refinedType = ref ClassElementTypeAsRef(pointerType);
            TryRefineTypeSecondPass(ref refinedType);
        }

        return true;
    }

    private unsafe void TryWalkClassFieldsNew(CppTypeDeclaration @base, ref CppClass @class)
    {
        using var pinnedGCHandleForClassContainer = new PinnedGCHandle<CppContainerList<CppField>>(@class.Fields);
        ref var targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForClassContainer.GetAddressOfObjectData());
        foreach (ref var field in CollectionsMarshal.AsSpan(targetFields))
        {
            ref CppType fieldType = ref FieldTypeAsRef(field);

            if (!fieldType.IsPointerType)
            {
                if (TryRefineFieldTypeFirstPass(ref field))
                    continue;

                if (!TryWalkTypeHierarchy(@base, ref fieldType, true))
                    field.Comment = UnresolvedComment;
                continue;
            }

            if (!TryRefineTypeSecondPass(ref fieldType))
                field.Comment = UnresolvedComment;
        }
    }

    private unsafe bool TryRefineTypeSecondPass(ref CppType type)
    {
        var typeName = type.TypeName;
        var isGeneric = type.IsGenericType;
        ref CppTypeDeclaration resolvedTargetType = ref _targetGlobalDeclarations.TryFindType(typeName);
        bool resolvedInMap = false;
        foreach (var (_, insertionList) in _insertionMap)
        {
            ref var insertedMatched = ref insertionList.TryFindType(typeName);
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
                var remappedType = _prebuiltTypes[CppTypeExt.GlobalConfig.KnownReservedSuffixes[matched]];
                if (remappedType is not CppEnum)
                    RemapType(ref type, remappedType);
            }
            else if (isGeneric && (typeName.StartsWith("Action_") || typeName.StartsWith("Func_")))
            {
                ref var builtInActionType = ref CollectionsMarshal.GetValueRefOrNullRef(_prebuiltTypes, "Action");
                if (!Unsafe.IsNullRef(ref builtInActionType))
                {
                    RemapType(ref type, builtInActionType);
                    return true;
                }
            }

            RemapType(ref type, _prebuiltTypes["Il2CppObject"]);
        }
        return true;
    }

    private static unsafe bool TryRefineFieldTypeFirstPass(ref CppField field)
    {
        if (field.Type is not CppClass fieldClass)
            return false;

        if (field.Type.TypeName.Contains("FP"))
            return false;

        using var pinnedGCHandleForClassContainer = new PinnedGCHandle<CppContainerList<CppField>>(fieldClass.Fields);
        ref var targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForClassContainer.GetAddressOfObjectData());
        if (targetFields.Count != 1 || !targetFields.TrueForAll(f => f.Type.IsPrimitiveType || (f.Type.IsTypeDef && ((CppTypedef)f.Type).ElementType.IsPrimitiveType)))
            return false;

        var refineType = targetFields[0].Type;
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

        ref var refToElementType = ref ClassElementTypeAsRef((CppTypeWithElementType)target);
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
                break;

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
struct  E_NAME ## __Array { \
Il2CppClass *klass; \
MonitorData *monitor; \
Il2CppArrayBounds *bounds; \
il2cpp_array_size_t max_length; \
E_NAME vector[32]; \
};
#endif
#ifndef DO_LIST_DEFINE
#define DO_LIST_DEFINE(E_NAME) \
DO_ARRAY_DEFINE(E_NAME) \
struct List_1_ ## E_NAME { \
Il2CppClass *klass; \
MonitorData *monitor; \
struct E_NAME ## __Array *_items; \
int32_t _size; \
int32_t _version; \
};
#endif

#include <cstdint>
#include ""il2cpp-class.h""";

}
