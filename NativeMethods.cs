using GroupedNativeMethodsGenerator;
using System.Reflection;
using System.Runtime.InteropServices;

namespace N0.IrohNet
{
    [GroupedNativeMethods]
    public static partial class iroh
    {
        // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/cross-platform
        // Library path will search
        // win => __DllName, __DllName.dll
        // linux, osx => __DllName.so, __DllName.dylib

        static iroh()
        {
            NativeLibrary.SetDllImportResolver(typeof(iroh).Assembly, DllImportResolver);
        }

        static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == __DllName)
            {
                var path = "runtimes/";
                var extension = "";

                if (OperatingSystem.IsWindows())
                {
                    path += "win-";
                    extension = ".dll";
                }
                else if (OperatingSystem.IsMacOS())
                {
                    path += "osx-";
                    extension = ".dylib";
                }
                else if (OperatingSystem.IsLinux())
                {
                    path += "linux-";
                    extension = ".so";
                }
                else if (OperatingSystem.IsIOS())
                {
                    path += RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ios-" : "iossimulator-";
                    extension = ".dylib";
                }

                if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                {
                    path += "x86";
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    path += "x64";
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    path += "arm64";
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm)
                {
                    path += "arm";
                }

                path += "/native/" + (OperatingSystem.IsWindows() ? "" : "lib") + __DllName + extension;

                return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, path), assembly, searchPath);
            }

            return IntPtr.Zero;
        }
    }
}
