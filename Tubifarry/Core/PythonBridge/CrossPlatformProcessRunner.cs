using NLog;
using System.Diagnostics;
using System.Text;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Provides cross-platform process execution capabilities.
    /// </summary>
    public static class CrossPlatformProcessRunner
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Creates a process with platform-specific settings.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <param name="arguments">The arguments for the command.</param>
        /// <param name="workingDirectory">The working directory for the process.</param>
        /// <param name="redirectInput">Whether to redirect standard input.</param>
        /// <param name="redirectOutput">Whether to redirect standard output.</param>
        /// <param name="redirectError">Whether to redirect standard error.</param>
        /// <param name="createNoWindow">Whether to create the process with no window.</param>
        public static Process CreateProcess(
            string command,
            string arguments,
            string workingDirectory,
            bool redirectInput = true,
            bool redirectOutput = true,
            bool redirectError = true,
            bool createNoWindow = true)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();

            return platform switch
            {
                OSPlatform.Windows => CreateWindowsProcess(command, arguments, workingDirectory,
                                      redirectInput, redirectOutput, redirectError, createNoWindow),
                OSPlatform.Linux => CreateUnixProcess(command, arguments, workingDirectory,
                                   redirectInput, redirectOutput, redirectError, createNoWindow),
                OSPlatform.MacOS => CreateUnixProcess(command, arguments, workingDirectory,
                                   redirectInput, redirectOutput, redirectError, createNoWindow),
                _ => throw new PlatformNotSupportedException($"Unsupported platform: {platform}")
            };
        }

        /// <summary>
        /// Creates a Windows-specific process.
        /// </summary>
        private static Process CreateWindowsProcess(
            string command,
            string arguments,
            string workingDirectory,
            bool redirectInput,
            bool redirectOutput,
            bool redirectError,
            bool createNoWindow)
        {
            _logger.Trace($"Creating Windows process: {command} {arguments} (in {workingDirectory})");
            bool useShellExecute = !redirectInput && !redirectOutput && !redirectError;
            bool isCommandLine = command.EndsWith(".bat") || command.EndsWith(".cmd");
            string quotedCommand = QuotePathIfNeeded(command);

            ProcessStartInfo startInfo = new();

            if (isCommandLine)
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c {quotedCommand} {arguments}";
            }
            else
            {
                startInfo.FileName = quotedCommand;
                if (!string.IsNullOrWhiteSpace(arguments))
                    startInfo.Arguments = arguments;
            }

            startInfo.WorkingDirectory = workingDirectory;
            startInfo.UseShellExecute = useShellExecute;
            startInfo.RedirectStandardInput = redirectInput;
            startInfo.RedirectStandardOutput = redirectOutput;
            startInfo.RedirectStandardError = redirectError;
            startInfo.CreateNoWindow = createNoWindow;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.StandardOutputEncoding = Encoding.UTF8;

            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONLEGACYWINDOWSSTDIO"] = "utf-8";

            Process process = new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            return process;
        }

        /// <summary>
        /// Creates a Unix-like (Linux or macOS) process.
        /// </summary>
        private static Process CreateUnixProcess(
            string command,
            string arguments,
            string workingDirectory,
            bool redirectInput,
            bool redirectOutput,
            bool redirectError,
            bool createNoWindow)
        {
            _logger.Debug($"Creating Unix process: {command} {arguments} (in {workingDirectory})");

            bool useShellExecute = !redirectInput && !redirectOutput && !redirectError;
            bool isShellScript = command.EndsWith(".sh");
            string quotedCommand = QuotePathIfNeeded(command);
            ProcessStartInfo startInfo = new();

            if (isShellScript || command == "/bin/bash")
            {
                startInfo.FileName = command;

                if (command == "/bin/bash" && !arguments.StartsWith("-c"))
                    startInfo.Arguments = $"-c \"{arguments}\"";
                else
                    startInfo.Arguments = arguments;
            }
            else if (command.StartsWith("source "))
            {
                startInfo.FileName = "/bin/bash";
                startInfo.Arguments = $"-c \"{command} {arguments}\"";
            }
            else
            {
                startInfo.FileName = quotedCommand;
                if (!string.IsNullOrWhiteSpace(arguments))
                    startInfo.Arguments = arguments;
            }

            startInfo.WorkingDirectory = workingDirectory;
            startInfo.UseShellExecute = useShellExecute;
            startInfo.RedirectStandardInput = redirectInput;
            startInfo.RedirectStandardOutput = redirectOutput;
            startInfo.RedirectStandardError = redirectError;
            startInfo.CreateNoWindow = createNoWindow;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.StandardOutputEncoding = Encoding.UTF8;

            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["LANG"] = "en_US.UTF-8";
            startInfo.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");

            Process process = new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            return process;
        }

        /// <summary>
        /// Quotes a path if it contains spaces and isn't already quoted.
        /// </summary>
        private static string QuotePathIfNeeded(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (path.Contains(' ') && !(path.StartsWith('\"') && path.EndsWith('\"')) && !(path.StartsWith('\'') && path.EndsWith('\'')))
                return $"\"{path}\"";

            return path;
        }

        /// <summary>
        /// Executes a shell command and returns its output.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <param name="workingDirectory">The working directory for the command. If null, uses the current directory.</param>
        /// <param name="captureStdErr">Whether to capture and return stderr output.</param>
        /// <returns>A tuple containing the exit code, stdout, and optionally stderr.</returns>
        public static async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteShellCommand(
            string command,
            string? workingDirectory = null,
            bool captureStdErr = true)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string shellExecutable;
            string shellArgPrefix;

            if (platform == OSPlatform.Windows)
            {
                shellExecutable = "cmd.exe";
                shellArgPrefix = "/c";
            }
            else // Linux or macOS
            {
                shellExecutable = "/bin/bash";
                shellArgPrefix = "-c";
            }

            if (platform == OSPlatform.Windows)
                command = command.Replace("\"", "\\\"");

            _logger.Debug($"Executing shell command: {shellExecutable} {shellArgPrefix} \"{command}\" (in {workingDirectory ?? Environment.CurrentDirectory})");

            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shellExecutable,
                    Arguments = $"{shellArgPrefix} \"{command}\"",
                    WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = captureStdErr,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            if (platform == OSPlatform.Windows)
                process.StartInfo.Environment["PYTHONLEGACYWINDOWSSTDIO"] = "utf-8";
            else
            {
                process.StartInfo.Environment["LANG"] = "en_US.UTF-8";
                process.StartInfo.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");
            }

            try
            {
                process.Start();
                Task<string> readOutputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> readErrorTask = captureStdErr ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
                await process.WaitForExitAsync();

                string stdOut = await readOutputTask;
                string stdErr = await readErrorTask;

                _logger.Trace($"Shell command completed with exit code {process.ExitCode}");
                if (process.ExitCode != 0)
                    _logger.Debug($"Command error output: {stdErr}");
                return (process.ExitCode, stdOut, stdErr);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error executing shell command: {command}");
                return (-1, string.Empty, ex.ToString());
            }
        }
    }
}