using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CppAst;

namespace ApiDiff
{
    internal class Program
    {

        [MaybeNull]
        private static CppPointerType Il2CppClassPointer, Il2CppObjectPointer, Il2CppArrayPointer;
        private static readonly SearchValues<string> KnownTypes = SearchValues.Create(["Il2CppClass", "Il2CppClass_0", "Il2CppClass_1", "Il2CppRGCTXData", "MonitorData", "Il2CppObject", "Il2CppArray", "VirtualInvokeData", "Action", "String", "void"], StringComparison.Ordinal);
        private static readonly CppCommentText UnresolvedComment = new() { Text = "Unresolved" };
        private static readonly HashSet<string> StackedClasses = [], InsertedDeclarations = [];
        private static readonly Dictionary<CppTypeDeclaration, List<CppTypeDeclaration>> InsertionMap = [];

        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                return;
            }

            CppParserOptions cppParserOptions = new() { TargetCpu = CppTargetCpu.ARM64, TargetSystem = "linux", ParseMacros = true };
            cppParserOptions.Defines.Add("_IDACLANG_=1");
            var sysRootInclude = new DirectoryInfo(args[2]);
            cppParserOptions.IncludeFolders.Add(new FileInfo(args[1]).Directory!.FullName);
            cppParserOptions.SystemIncludeFolders.Add(Path.Combine(sysRootInclude.FullName, "c++", "v1"));
            cppParserOptions.SystemIncludeFolders.Add(sysRootInclude.FullName);

            CppCompilation inputCompilation = TryParseCpp(File.ReadAllText(args[0]), "input", cppParserOptions);
            var targetFileContent = File.ReadAllText(args[1]);
            CppCompilation targetCompilation = TryParseCpp(targetFileContent.Replace("#pragma once", $"#pragma once\ntypedef unsigned long size_t;"), "target", cppParserOptions);

            var appNamespace = targetCompilation.Namespaces.FirstOrDefault(@namespace => @namespace.Name == "app");
            ArgumentNullException.ThrowIfNull(appNamespace);

            List<CppTypeDeclaration> inputDeclarations = [.. inputCompilation.Typedefs, .. inputCompilation.Enums, .. inputCompilation.Classes];
            List<CppTypeDeclaration> targetDeclarations = [.. targetCompilation.Typedefs, .. targetCompilation.Enums, .. targetCompilation.Classes, .. appNamespace.Enums, .. appNamespace.Classes];
            inputDeclarations.SortBySourceLocation();
            targetDeclarations.SortBySourceLocation(false);

            Console.WriteLine($"{inputDeclarations.Count} pending declarations found in input");
            Console.WriteLine($"{targetDeclarations.Count} pending declarations found in target");

            var rawData = CollectionsMarshal.AsSpan(targetDeclarations);
            ref var il2cppClassType = ref inputDeclarations.TryFindType("Il2CppClass");
            ref var il2cppObjectType = ref inputDeclarations.TryFindType("Il2CppObject");
            ref var il2cppArrayType = ref inputDeclarations.TryFindType("Il2CppArray");
            if (Unsafe.IsNullRef(ref il2cppClassType))
                throw new InvalidOperationException($"Can not load critical cpp type {nameof(Il2CppClassPointer)}");
            if (Unsafe.IsNullRef(ref il2cppObjectType))
                throw new InvalidOperationException($"Can not load critical cpp type {nameof(Il2CppObjectPointer)}");
            if (Unsafe.IsNullRef(ref il2cppArrayType))
                throw new InvalidOperationException($"Can not load critical cpp type {nameof(Il2CppArrayPointer)}");

            Il2CppClassPointer = new(il2cppClassType);
            Il2CppObjectPointer = new(il2cppObjectType);
            Il2CppArrayPointer = new(il2cppArrayType);
            for (int i = rawData.Length - 1; i >= 0; --i)
            {
                ref var originalType = ref rawData[i];
                Console.WriteLine($"Try to resolve {originalType}");
                ref var resolvedType = ref inputDeclarations.TryFindType(originalType);
                if (Unsafe.IsNullRef(ref resolvedType))
                {
                    originalType.Comment = UnresolvedComment;
                    Console.WriteLine($"Failed to resolve {originalType}, not found!");
                    continue;
                }

                if (!TryFindResolvedType(targetDeclarations, inputDeclarations, resolvedType, i))
                {
                    originalType.Comment = UnresolvedComment;
                    Console.WriteLine($"Failed to resolve {originalType}, errored out!");
                    continue;
                }

                Console.WriteLine($"{originalType} resolved successfully.");
                originalType = resolvedType;
            }

            foreach (var (key, values) in InsertionMap)
            {
                int keyIndex = targetDeclarations.IndexOf(key);
                targetDeclarations.InsertRange(keyIndex, values);
            }

            var constHeader = @"#pragma once

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
#endif";

        }

        private static CppCompilation TryParseCpp(string content, string name, CppParserOptions parserOptions)
        {
            Console.WriteLine($"Compiling {name}...");
            CppCompilation compilation = CppParser.Parse(content, parserOptions, name);
            if (compilation.Diagnostics.HasErrors)
            {
                var errors = compilation.Diagnostics.Messages.Where(message => message.Type == CppLogMessageType.Error);
                Console.WriteLine($"Compilation ended with {errors.Count()} errors generated.");
                foreach (var error in errors)
                {
                    Console.WriteLine(error);
                }
            }
            else
            {
                Console.WriteLine($"Compilation ended with {compilation.Diagnostics.Messages.Count} diagnostics generated.");
            }

            return compilation;
        }

        private static bool TryFindResolvedType(List<CppTypeDeclaration> knownTypes, List<CppTypeDeclaration> allTypes, CppTypeDeclaration type, int knownTypeIndex)
        {
            if (Debugger.IsAttached)
            {
                string assertTarget = "Ability";
                if (type.GetDisplayName() == assertTarget)
                    Debug.Assert(false);
            }

            if (type.TypeKind is CppTypeKind.Primitive or CppTypeKind.Typedef)
                return true;

            if (KnownTypes.Contains(type.GetDisplayName()))
                return true;

            if (type is CppClass cppClass)
            {
                foreach (var field in cppClass.Fields)
                {
                    var fieldType = field.Type!;
                    if (fieldType.TypeKind is CppTypeKind.Primitive or CppTypeKind.Typedef)
                        continue;

                    bool isPointer = fieldType.TypeKind == CppTypeKind.Pointer;
                    var typeName = isPointer ? (fieldType as CppTypeWithElementType)!.ElementType.FullName : fieldType.FullName;
                    if (KnownTypes.Contains(typeName))
                        continue;

                    if (!StackedClasses.Add(typeName))
                        continue;

                    ref CppTypeDeclaration resolvedFieldType = ref knownTypes.TryFindType(typeName);
                    if (Unsafe.IsNullRef(ref resolvedFieldType))
                    {
                        if (isPointer)
                        {
                            if (typeName.EndsWith("__Class"))
                            {
                                field.Type = Il2CppClassPointer;
                            }
                            else if (typeName.EndsWith("__Array"))
                            {
                                field.Type = Il2CppArrayPointer;
                            }
                            else if (typeName != "void")
                            {
                                field.Type = Il2CppObjectPointer;
                            }
                            continue;
                        }

                        ref var resolvedFromTypeName = ref allTypes.TryFindType(typeName);
                        if (Unsafe.IsNullRef(ref resolvedFromTypeName))
                        {
                            goto FAILED;
                        }
                        else
                        {
                            var currentKnownType = knownTypes[knownTypeIndex];
                            ref var insertionList = ref CollectionsMarshal.GetValueRefOrAddDefault(InsertionMap, currentKnownType, out var exists);
                            if (!exists)
                                insertionList = [];

                            if (!InsertedDeclarations.Add(typeName))
                            {
                                InsertedDeclarations.Remove(typeName);
                                insertionList!.Insert(0, resolvedFromTypeName);
                            }

                            TryFindResolvedType(knownTypes, allTypes, resolvedFromTypeName, knownTypeIndex);
                            continue;
                        }
                    }
                    else
                    {
                        ref var resolvedFromKnownType = ref allTypes.TryFindType(resolvedFieldType);
                        if (Unsafe.IsNullRef(ref resolvedFromKnownType))
                        {
                            goto FAILED;
                        }
                        else
                        {
                            TryFindResolvedType(knownTypes, allTypes, resolvedFromKnownType, knownTypeIndex);
                            continue;
                        }
                    }

                FAILED:
                    field.Comment = UnresolvedComment;
                    continue;
                }
            }
            else if (type is CppEnum cppEnum)
            {
                ref CppTypeDeclaration resolvedEnumType = ref knownTypes.TryFindType(cppEnum.FullName);
                if (Unsafe.IsNullRef(ref resolvedEnumType))
                {
                    cppEnum.Name = "int32_t";
                }
            }

            return true;
        }

    }
}
