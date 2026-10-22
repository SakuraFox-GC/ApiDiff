using System.Runtime.CompilerServices;

namespace ApiDiff
{
    internal class Program
    {

        static void Main(string[] args)
        {
            Console.Title = nameof(ApiDiff);

            if (args.Length > 0 && args[0] == "--typeinfo")
            {
                UpdateTypeInfo(args);
                return;
            }

            if (args.Length < 3)
            {
                PrintHelp();
                return;
            }

            SetUnhandledExceptionHandler(null, OnUnhandledException);

            string inputHeader = args[0], targetHeader = args[1], includeDir = args[2];
            if (!File.Exists(inputHeader))
            {
                throw new FileNotFoundException("InputHeaderFilePath not exists.", inputHeader);
            }

            if (!File.Exists(targetHeader))
            {
                throw new FileNotFoundException("TargetHeaderFilePath not exists.", targetHeader);
            }

            if (!Directory.Exists(includeDir))
            {
                throw new DirectoryNotFoundException($"AndroidNDKSysrootIncludeDir {includeDir} not exists.");
            }

            var headerDiffer = new Differ(inputHeader, targetHeader, includeDir);
            if (!headerDiffer.BuildTypeModel())
            {
                Log.Error("Failed to build type model.", null, nameof(headerDiffer.BuildTypeModel));
                return;
            }

            Log.FloodColour = true;
            Log.Info("Constructing target header...", null, nameof(headerDiffer.ConstructDefinitions));
            Log.FloodColour = false;
            string generatedHeader = headerDiffer.ConstructDefinitions();
            Log.FloodColour = true;
            Log.Info($@"
Construction all done! Press any key to apply the changes to the target header

  Target Header File:
    {targetHeader}", null, nameof(headerDiffer.ConstructDefinitions));
            Console.ReadKey(true);
            File.WriteAllText(targetHeader, generatedHeader);
        }

        private static void UpdateTypeInfo(string[] args)
        {
            if (args.Length != 5)
            {
                PrintHelp();
                return;
            }

            try
            {
                TypeInfoUpdater.Update update = TypeInfoUpdater.Prepare(args[1], args[2], args[3], args[4], CppTypeExt.GlobalConfig.RemappedTypes);
                if (update.ChangedAliases.Count == 0)
                {
                    Log.Info("TypeInfo class hints are already current; no files changed.");
                    return;
                }
                Log.Info($"Class hints to update: {string.Join(", ", update.ChangedAliases)}");
                Log.Info($"Press any key to apply these class hint changes to {args[3]}.");
                Console.ReadKey(true);
                if (!File.ReadAllBytes(args[3]).AsSpan().SequenceEqual(update.Original))
                    throw new IOException("Class hints changed while the update was being reviewed; run the command again.");
                string destination = Path.GetFullPath(args[3]);
                string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, update.Proposed);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Error("TypeInfo update failed.", error);
                Environment.ExitCode = 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Usage:
  ApiDiff.exe <InputHeaderFilePath> <TargetHeaderFilePath> <AndroidNDKSysrootIncludeDir>
  ApiDiff.exe --typeinfo <InspectorJson> <TypeInfoHeader> <ClassesJson> <AssembliesJson>

Description:
  The layout update expects three arguments:
  
    1. Input Header File – a proper path to your C++ header file.
    2. Target Header File – a path to the header file where the output will be written. Mind that this file may get overwritten.
    3. Android NDK Sysroot Include Directory – the path to the include directory in your Android NDK sysroot.

TypeInfo update:
  Reads Inspector metadata and DO_TYPEINFO(name); declarations to update classes.json.
  Keeps native aliases, the declaration header and assembly aliases unchanged.
  RemappedTypes in remapping_config.json explicitly selects renamed dump types.
  Missing, ambiguous and unsupported metadata is rejected before any file is written.

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
