using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CppAst;

namespace ApiDiff;

internal static class CppTypeExt
{

    extension<T>(T declaration) where T : CppTypeDeclaration
    {

        public bool IsSameDeclaration(string anotherTypeName)
        {
            string leftName = declaration.FullName, rightName = anotherTypeName;
            if (declaration.Parent is CppNamespace { } @namespace)
            {
                leftName = leftName.Replace($"{@namespace.Name}::", string.Empty);
            }

            if (declaration.TypeKind == CppTypeKind.Enum)
                leftName = leftName.Replace("__Enum", string.Empty);
            rightName = rightName.Replace("__Enum", string.Empty);

            bool leftUnderscore = leftName.Contains('_'), rightUnderscore = rightName.Contains('_');
            if (leftUnderscore && !rightUnderscore)
                return leftName[(leftName.LastIndexOf('_') + 1)..] == rightName;

            if (rightUnderscore && !leftUnderscore)
                return rightName[(rightName.LastIndexOf('_') + 1)..] == leftName;

            return leftName == rightName;
        }

        public bool IsSameDeclaration(T anotherDeclaration)
        {
            if (declaration.TypeKind != anotherDeclaration.TypeKind)
                return false;

            var rightName = anotherDeclaration.FullName;
            if (anotherDeclaration.Parent is CppNamespace { } anotherNamespace)
                rightName = rightName.Replace($"{anotherNamespace.Name}::", string.Empty);

            return declaration.IsSameDeclaration(rightName);
        }

    }

    extension<T>(List<T> declarations) where T : CppElement
    {

        public bool ContainsType(string typeName)
        {
            if (declarations is not List<CppTypeDeclaration> cppTypeDeclarations)
                throw new NotSupportedException();

            var target = cppTypeDeclarations.Find(declaration => declaration.IsSameDeclaration(typeName));
            return target is not null && declarations.ContainsType(target);
        }

        public bool ContainsType(CppTypeDeclaration cppType)
        {
            if (declarations is not List<CppTypeDeclaration> cppTypeDeclarations)
                throw new NotSupportedException();

            return cppTypeDeclarations.Find(declaration => declaration.IsSameDeclaration(cppType)) is not null;
        }

        public ref CppTypeDeclaration TryFindType(string typeName)
        {
            if (declarations is not List<CppTypeDeclaration> cppTypeDeclarations)
                throw new NotSupportedException();

            var rawData = CollectionsMarshal.AsSpan(cppTypeDeclarations);
            for (int i = rawData.Length - 1; i >= 0; --i)
            {
                ref var target = ref rawData[i];
                if (target.IsSameDeclaration(typeName))
                    return ref target!;
            }

            return ref Unsafe.NullRef<CppTypeDeclaration>();
        }

        public ref CppTypeDeclaration TryFindType(CppTypeDeclaration cppType)
        {
            if (declarations is not List<CppTypeDeclaration> cppTypeDeclarations)
                throw new NotSupportedException();

            var rawData = CollectionsMarshal.AsSpan(cppTypeDeclarations);
            var index = rawData.IndexOf([cppType], new CppTypeDeclarationComparer());
            if (index == -1)
                return ref Unsafe.NullRef<CppTypeDeclaration>();


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

    private class CppTypeDeclarationComparer : IEqualityComparer<CppTypeDeclaration>
    {
        public bool Equals(CppTypeDeclaration? x, CppTypeDeclaration? y)
        {
            if (x == null || y == null)
                throw new InvalidOperationException();

            return x.IsSameDeclaration(y);
        }

        public int GetHashCode([DisallowNull] CppTypeDeclaration obj)
        {
            return obj.GetHashCode();
        }
    }

}
