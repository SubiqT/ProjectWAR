using FrameWork;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WorldServer.Physics
{
    /// <summary>
    /// Resolves the native Bullet library for BulletSharp.
    /// BulletSharp declares its imports against "libbulletc" and ships per platform
    /// binaries with a Mono style DllMap in BulletSharp.dll.config. .NET Core ignores
    /// DllMap entries, so the mapping is applied here instead.
    /// </summary>
    internal static class BulletNativeLibrary
    {
        private const string ImportName = "libbulletc";

        private static readonly object Gate = new object();

        private static bool _registered;

        public static void Register()
        {
            lock (Gate)
            {
                if (_registered)
                    return;

                _registered = true;

                NativeLibrary.SetDllImportResolver(typeof(BulletSharp.Math.Vector3).Assembly, Resolve);
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != ImportName)
                return IntPtr.Zero;

            string candidate = PlatformCandidate();

            if (candidate == null)
                throw new PlatformNotSupportedException(
                    $"No native Bullet binary is shipped for {RuntimeInformation.OSDescription} on {RuntimeInformation.ProcessArchitecture}.");

            string path = Path.Combine(AppContext.BaseDirectory, candidate);

            if (File.Exists(path))
            {
                Log.Debug("Occlusion", "Loading native Bullet library " + path);
                return NativeLibrary.Load(path);
            }

            // Fall back to the usual platform search paths.
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                Log.Debug("Occlusion", "Loading native Bullet library " + candidate + " from the platform search path");
                return handle;
            }

            throw new FileNotFoundException(
                $"The native Bullet library {candidate} was not found next to the server or on the library search path.", path);
        }

        private static string PlatformCandidate()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                        return "libbulletc-windows-x64.dll";
                    case Architecture.X86:
                        return "libbulletc-windows-x86.dll";
                    default:
                        return null;
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                        return "libbulletc-linux-x64.so";
                    case Architecture.X86:
                        return "libbulletc-linux-x86.so";
                    case Architecture.Arm:
                        return "libbulletc-linux-arm.so";
                    default:
                        return null;
                }
            }

            return null;
        }
    }
}
