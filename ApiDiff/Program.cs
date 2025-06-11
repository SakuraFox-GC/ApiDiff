using System.Runtime.CompilerServices;

namespace ApiDiff
{
    internal class Program
    {

        static void Main(string[] args)
        {
            Console.Title = nameof(ApiDiff);

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
            if (!headerDiffer.BuildTypeModel())
            {
                Log.Error("Failed to build type model.", null, nameof(headerDiffer.BuildTypeModel));
                return;
            }

            Log.FloodColour = true;
            Log.Info("Constructing target header...", null, nameof(headerDiffer.ConstructDefinitions));
            Log.FloodColour = false;
            var generatedHeader = headerDiffer.ConstructDefinitions();
            Log.FloodColour = true;
            Log.Info($@"
Construction all done! Press any key to apply the changes to the target header

  Target Header File:
    {targetHeader}", null, nameof(headerDiffer.ConstructDefinitions));
            Console.ReadKey(true);
            File.WriteAllText(targetHeader, generatedHeader);
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
                Log.FloodColour = true;
                Log.Error("This programme has been proper baffled.", (Exception)e.ExceptionObject, "WatchDog");
                Thread.Sleep(5000);
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "add_UnhandledException")]
        private static extern void SetUnhandledExceptionHandler([UnsafeAccessorType("System.AppContext, System.Private.CoreLib")] object? c, UnhandledExceptionEventHandler handler);

    }
}
