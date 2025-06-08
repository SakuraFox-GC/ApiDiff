using System.Runtime.CompilerServices;

namespace ApiDiff
{
    internal class Program
    {

        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                PrintHelp();
                return;
            }

            SetUnhandledExceptionHandler(null, OnUnhandledException);

            string inputHeader = args[0], targetHeader = args[1], includeDir = args[2];
            if (!File.Exists(inputHeader))
                throw new FileNotFoundException("InputHeaderFilePath not exists.", inputHeader);
            if (!File.Exists(targetHeader))
                throw new FileNotFoundException("TargetHeaderFilePath not exists.", targetHeader);
            if (!Directory.Exists(includeDir))
                throw new DirectoryNotFoundException($"AndroidNDKSysrootIncludeDir {includeDir} not exists.");

            var headerDiffer = new Differ(inputHeader, targetHeader, includeDir);
            Log.Info($"\n{headerDiffer.Generate()}");
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Usage:
  ApiDiff.exe <InputHeaderFilePath> <TargetHeaderFilePath> <AndroidNDKSysrootIncludeDir>

Description:
  This little programme expects exactly three arguments:
  
    1. Input Header File – a proper path to your C++ header file.
    2. Target Header File – a path to the header file where the output will be written. Mind that this file may get overwritten.
    3. Android NDK Sysroot Include Directory – the path to the include directory in your Android NDK sysroot.

Notes:
  - Wrap paths with spaces in quotes.
  - The arguments must be provided in the correct order, otherwise the programme will be proper baffled.");
            Console.ReadKey(true);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is FileNotFoundException or DirectoryNotFoundException)
            {
                Log.Error(((Exception)e.ExceptionObject).Message, null, "WatchDog");
                PrintHelp();
            }
            else
            {
                Log.Error("This programme has been proper baffled.", (Exception)e.ExceptionObject, "WatchDog");
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "add_UnhandledException")]
        private static extern void SetUnhandledExceptionHandler([UnsafeAccessorType("System.AppContext, System.Private.CoreLib")] object? c, UnhandledExceptionEventHandler handler);

    }
}
