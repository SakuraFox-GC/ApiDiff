using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CppAst;

namespace ApiDiff;

internal static class CppTypeExt
{

    internal static readonly SearchValues<string> KnownTypes = SearchValues.Create(["Il2CppClass", "Il2CppClass_0", "Il2CppClass_1", "Il2CppRGCTXData", "MonitorData", "Il2CppObject", "Il2CppArray", "Il2CppArrayBounds", "VirtualInvokeData", "Action", "String", "Vector2", "Vector3", "void"], StringComparison.Ordinal);

    private static bool CompareTypeName(string left, string right)
    {
        left = left.Replace("__Enum", string.Empty);
        right = right.Replace("__Enum", string.Empty);

        bool leftUnderscore = left.Contains('_'), rightUnderscore = right.Contains('_');
        if (leftUnderscore && !rightUnderscore)
            return left[(left.LastIndexOf('_') + 1)..] == right;

        if (rightUnderscore && !leftUnderscore)
            return right[(right.LastIndexOf('_') + 1)..] == left;

        return left == right;
    }

    private static bool CheckGeneric(string typeName)
    {
        var indexOfUnderscore = typeName.IndexOf('_');
        if (indexOfUnderscore == -1)
            return false;

        var subName = typeName[(indexOfUnderscore + 1)..];
        if (subName.IsWhiteSpace())
            return false;

        return char.IsNumber(subName[0]);
    }

    extension(CppType type)
    {

        public bool IsKnownType => KnownTypes.Contains(type.TypeName);

        public bool IsPointerType => type.TypeKind is CppTypeKind.Pointer;

        public bool IsPrimitiveType => type.TypeKind is CppTypeKind.Primitive;

        public bool IsTypeDef => type.TypeKind is CppTypeKind.Typedef;

        public bool IsGenericType => CheckGeneric(type.TypeName);

        public bool HasElementType => type is CppTypeWithElementType;

        public string TypeName => type is CppTypeWithElementType eType ? eType.ElementType.FullName : type.FullName;

        public bool IsSameType(string anotherTypeName)
        {
            string leftName = type.FullName, rightName = anotherTypeName;
            if (type.Parent is CppNamespace { } @namespace)
            {
                leftName = leftName.Replace($"{@namespace.Name}::", string.Empty);
            }

            return CompareTypeName(leftName, rightName);
        }

        public bool IsSameType(CppType anotherType)
        {
            if (object.ReferenceEquals(type, anotherType))
                return true;

            if (type.TypeKind != anotherType.TypeKind)
                return false;

            var rightName = anotherType.FullName;
            if (anotherType.Parent is CppNamespace { } anotherNamespace)
                rightName = rightName.Replace($"{anotherNamespace.Name}::", string.Empty);

            return type.IsSameType(rightName);
        }

    }

    extension<T>(List<T> declarations) where T : CppType
    {

        public bool ContainsType(string typeName)
        {
            var target = declarations.Find(declaration => declaration.IsSameType(typeName));
            return target is not null && declarations.ContainsType(target);
        }

        public bool ContainsType(CppType cppType)
        {
            return declarations.Find(declaration => declaration.IsSameType(cppType)) is not null;
        }

        public ref T TryFindType(string typeName)
        {
            var rawData = CollectionsMarshal.AsSpan(declarations);
            for (int i = rawData.Length - 1; i >= 0; --i)
            {
                ref var target = ref rawData[i];
                if (target.IsSameType(typeName))
                    return ref target!;
            }

            return ref Unsafe.NullRef<T>();
        }

        public ref T TryFindType(CppType cppType)
        {
            var rawData = CollectionsMarshal.AsSpan(declarations);
            var index = rawData.IndexOf([cppType], new CppTypeComparer());
            if (index == -1)
                return ref Unsafe.NullRef<T>();


            return ref rawData[index];
        }

        public void SortBySourceLocation(bool throwIfSourceNotSame = true)
        {
            declarations.Sort(new CppElementDeclarationSourceComparer(throwIfSourceNotSame));
        }

    }

    private class CppElementDeclarationSourceComparer(bool ThrowIfSourceNotSame) : IComparer<CppElement>
    {
        public int Compare(CppElement? x, CppElement? y)
        {
            if (x == null || y == null)
                throw new InvalidOperationException();
            if (x.SourceFile != y.SourceFile && ThrowIfSourceNotSame)
                throw new InvalidOperationException();

            int offsetLeft = x.Span.Start.Offset, offsetRight = y.Span.Start.Offset;
            if (offsetLeft < offsetRight)
                return -1;
            if (offsetLeft > offsetRight)
                return 1;
            return 0;
        }
    }

    private class CppTypeComparer : IEqualityComparer<CppType>
    {
        public bool Equals(CppType? x, CppType? y)
        {
            if (x == null || y == null)
                throw new InvalidOperationException();

            return x.IsSameType(y);
        }

        public int GetHashCode([DisallowNull] CppType obj)
        {
            return obj.GetHashCode();
        }
    }

}
