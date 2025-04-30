using System.Runtime.InteropServices;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Represents the operating system platform.
    /// </summary>
    public enum OSPlatform
    {
        /// <summary>
        /// Windows operating system.
        /// </summary>
        Windows,

        /// <summary>
        /// Linux operating system.
        /// </summary>
        Linux,

        /// <summary>
        /// macOS operating system.
        /// </summary>
        MacOS,

        /// <summary>
        /// Unknown operating system.
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Provides utility methods for platform detection and handling.
    /// </summary>
    public static class PlatformUtils
    {
        /// <summary>
        /// Gets the current operating system platform.
        /// </summary>
        public static OSPlatform GetCurrentPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return OSPlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                return OSPlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                return OSPlatform.MacOS;

            return OSPlatform.Unknown;
        }

        /// <summary>
        /// Gets the current processor architecture.
        /// </summary>
        public static string GetArchitecture()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x86_64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "armv7l",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Gets the platform-specific file extension for executable files.
        /// </summary>
        public static string GetExecutableExtension() => GetCurrentPlatform() == OSPlatform.Windows ? ".exe" : "";

        /// <summary>
        /// Gets the platform-specific conda executable path.
        /// </summary>
        /// <param name="condaBasePath">The base path of the Conda installation.</param>
        public static string GetCondaExecutablePath(string condaBasePath)
        {
            OSPlatform platform = GetCurrentPlatform();

            return platform switch
            {
                OSPlatform.Windows => Path.Combine(condaBasePath, "Scripts", "conda.exe"),
                OSPlatform.Linux or OSPlatform.MacOS => Path.Combine(condaBasePath, "bin", "conda"),
                _ => throw new PlatformNotSupportedException("Unsupported platform for Conda")
            };
        }

        /// <summary>
        /// Gets the platform-specific shell executable.
        /// </summary>
        public static string GetShellExecutable()
        {
            OSPlatform platform = GetCurrentPlatform();

            return platform switch
            {
                OSPlatform.Windows => "cmd.exe",
                OSPlatform.Linux => "/bin/bash",
                OSPlatform.MacOS => "/bin/bash",
                _ => throw new PlatformNotSupportedException("Unsupported platform")
            };
        }

        /// <summary>
        /// Gets the environment variable PATH separator.
        /// </summary>
        public static char GetPathSeparator() => GetCurrentPlatform() == OSPlatform.Windows ? ';' : ':';
    }
}