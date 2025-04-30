using NLog;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tubifarry.Core.PythonBridge.Conda
{
    /// <summary>
    /// Provides functionality to download and install Miniconda.
    /// </summary>
    public static class CondaInstaller
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private const string condaUrl = "https://repo.anaconda.com/miniconda/";

        // Maps platform and architecture to regex patterns for installer names
        private static readonly Dictionary<(OSPlatform, string), string> InstallerPatterns = new()
        {
            { (OSPlatform.Windows, "x86_64"), @"Miniconda3-\d+\.\d+\.\d+-Windows-x86_64\.exe" },
            { (OSPlatform.Windows, "x86"), @"Miniconda3-\d+\.\d+\.\d+-Windows-x86\.exe" },
            { (OSPlatform.Linux, "x86_64"), @"Miniconda3-\d+\.\d+\.\d+-Linux-x86_64\.sh" },
            { (OSPlatform.Linux, "x86"), @"Miniconda3-\d+\.\d+\.\d+-Linux-x86\.sh" },
            { (OSPlatform.Linux, "arm64"), @"Miniconda3-\d+\.\d+\.\d+-Linux-aarch64\.sh" },
            { (OSPlatform.Linux, "armv7l"), @"Miniconda3-\d+\.\d+\.\d+-Linux-armv7l\.sh" },
            { (OSPlatform.MacOS, "x86_64"), @"Miniconda3-\d+\.\d+\.\d+-MacOSX-x86_64\.sh" },
            { (OSPlatform.MacOS, "arm64"), @"Miniconda3-\d+\.\d+\.\d+-MacOSX-arm64\.sh" },
        };

        // Maps platform and architecture to latest installer names
        private static readonly Dictionary<(OSPlatform, string), string> LatestInstallers = new()
        {
            { (OSPlatform.Windows, "x86_64"), "Miniconda3-latest-Windows-x86_64.exe" },
            { (OSPlatform.Windows, "x86"), "Miniconda3-latest-Windows-x86.exe" },
            { (OSPlatform.Linux, "x86_64"), "Miniconda3-latest-Linux-x86_64.sh" },
            { (OSPlatform.Linux, "x86"), "Miniconda3-latest-Linux-x86.sh" },
            { (OSPlatform.Linux, "arm64"), "Miniconda3-latest-Linux-aarch64.sh" },
            { (OSPlatform.Linux, "armv7l"), "Miniconda3-latest-Linux-armv7l.sh" },
            { (OSPlatform.MacOS, "x86_64"), "Miniconda3-latest-MacOSX-x86_64.sh" },
            { (OSPlatform.MacOS, "arm64"), "Miniconda3-latest-MacOSX-arm64.sh" },
        };

        /// <summary>
        /// Finds the Conda environment and returns an installation path.
        /// If Conda is not found, it downloads and installs Miniconda.
        /// </summary>
        /// <param name="installPaths">Optional paths to check for Conda installation</param>
        /// <returns>The path to the Conda installation, or null if installation fails.</returns>
        public static async Task<string?> FindOrInstallCondaAsync(params string[] installPaths)
        {
            if (installPaths.Length == 0)
                installPaths = new string[] { Environment.CurrentDirectory };

            string? minicondaPath = FindCondaPath(installPaths);

            if (string.IsNullOrWhiteSpace(minicondaPath))
            {
                _logger.Debug("Miniconda not found. Downloading and installing Miniconda...");
                string projectFolder = Path.Combine(installPaths[0], "miniconda3");
                await DownloadAndInstallMinicondaAsync(projectFolder);
                minicondaPath = FindCondaPath(installPaths);
            }

            if (!string.IsNullOrWhiteSpace(minicondaPath))
            {
                _logger.Debug($"Miniconda found at: {minicondaPath}");
                return minicondaPath;
            }

            return null;
        }

        /// <summary>
        /// Finds the path to the Conda installation.
        /// </summary>
        private static string? FindCondaPath(string[] installPaths)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string condaExecutableName = "conda" + PlatformUtils.GetExecutableExtension();

            _logger.Trace($"Looking for Conda executable: {condaExecutableName}");

            foreach (string basePath in installPaths)
            {
                if (!Directory.Exists(basePath))
                {
                    _logger.Debug($"Base path does not exist: {basePath}");
                    continue;
                }

                try
                {
                    _logger.Trace($"Searching for Miniconda in: {basePath}");
                    string[] minicondaDirs = Directory.GetDirectories(basePath, "miniconda3*", SearchOption.TopDirectoryOnly);
                    _logger.Trace($"Found {minicondaDirs.Length} potential Miniconda directories");

                    foreach (string dir in minicondaDirs)
                    {
                        string? condaPath = platform switch
                        {
                            OSPlatform.Windows => Path.Combine(dir, "Scripts", condaExecutableName),
                            OSPlatform.Linux or OSPlatform.MacOS => Path.Combine(dir, "bin", condaExecutableName),
                            _ => null
                        };

                        _logger.Trace($"Checking for Conda at: {condaPath}");

                        if (condaPath != null && File.Exists(condaPath))
                        {
                            _logger.Debug($"Found Conda at: {condaPath}");
                            return dir;
                        }
                    }

                    if (Path.GetFileName(basePath).StartsWith("miniconda3", StringComparison.OrdinalIgnoreCase))
                    {
                        string? condaPath = platform switch
                        {
                            OSPlatform.Windows => Path.Combine(basePath, "Scripts", condaExecutableName),
                            OSPlatform.Linux or OSPlatform.MacOS => Path.Combine(basePath, "bin", condaExecutableName),
                            _ => null
                        };

                        _logger.Trace($"Checking for Conda directly at: {condaPath}");

                        if (condaPath != null && File.Exists(condaPath))
                        {
                            _logger.Debug($"Found Conda at: {condaPath}");
                            return basePath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error searching for conda: {ex.Message}");
                }
            }

            _logger.Trace("No Conda installation found");
            return null;
        }

        /// <summary>
        /// Downloads and installs Miniconda.
        /// </summary>
        /// <param name="installFolder">The folder where Miniconda will be installed.</param>
        private static async Task DownloadAndInstallMinicondaAsync(string installFolder)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string architecture = PlatformUtils.GetArchitecture();

            try
            {
                (string downloadUrl, string hash) = await GetMinicondaDownloadUrlAsync(platform, architecture);
                Directory.CreateDirectory(installFolder);
                _logger.Trace($"Created installation directory: {installFolder}");
                string tempDir = Path.Combine(Path.GetTempPath(), "miniconda_installer_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                _logger.Debug($"Created temp directory: {tempDir}");
                string installerPath = Path.Combine(tempDir, Path.GetFileName(downloadUrl));
                _logger.Debug($"Downloading Miniconda installer to: {installerPath}");

                using (HttpClient client = new())
                {
                    _logger.Trace("Downloading Miniconda installer...");
                    byte[] installerData = await client.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(installerPath, installerData);
                    _logger.Trace("Download complete");
                }
                string fileHash = CalculateFileHash(installerPath);
                _logger.Trace($"Calculated hash: {fileHash}");
                _logger.Trace($"Expected hash: {hash}");

                if (hash == fileHash)
                    _logger.Trace("File hash matches the expected hash.");
                else
                    throw new Exception("File hash does not match the expected hash.");

                _logger.Trace("Installing Miniconda...");

                if (platform == OSPlatform.Windows)
                    await InstallMinicondaWindowsAsync(installerPath, installFolder);
                else
                    await InstallMinicondaUnixAsync(installerPath, installFolder);

                _logger.Trace("Miniconda installation complete.");

                if (platform == OSPlatform.Windows)
                {
                    string libraryBinPath = Path.Combine(installFolder, "Library", "bin");
                    string dllsPath = Path.Combine(installFolder, "DLLs");

                    _logger.Trace($"Setting up DLLs from {libraryBinPath} to {dllsPath}");

                    if (Directory.Exists(libraryBinPath))
                    {
                        if (!Directory.Exists(dllsPath))
                        {
                            Directory.CreateDirectory(dllsPath);
                            _logger.Debug($"Created DLLs directory: {dllsPath}");
                        }
                        CopyFiles(libraryBinPath, dllsPath, "libcrypto-1_1-x64.*");
                        CopyFiles(libraryBinPath, dllsPath, "libssl-1_1-x64.*");
                    }
                    else
                    {
                        _logger.Warn($"Warning: Library bin path not found: {libraryBinPath}");
                    }
                }

                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                        _logger.Trace($"Cleaned up temp directory: {tempDir}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to clean up temp directory: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error in DownloadAndInstallMiniconda: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Copies files from one directory to another matching a pattern.
        /// </summary>
        private static void CopyFiles(string sourceDir, string destDir, string pattern)
        {
            foreach (string file in Directory.GetFiles(sourceDir, pattern))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
                _logger.Trace($"Copied {file} to {destFile}");
            }
        }

        /// <summary>
        /// Installs Miniconda on Windows.
        /// </summary>
        private static async Task InstallMinicondaWindowsAsync(string installerPath, string installFolder)
        {
            try
            {
                bool isUpdate = Directory.Exists(installFolder);
                if (installFolder.Contains(' '))
                    _logger.Warn("Installation folder contains spaces which may cause issues with conda packages");

                string escapedPath = installFolder.Replace("\"", "\\\"");

                _logger.Trace($"Running installer: {installerPath} with arguments: /S /D={escapedPath}");

                ProcessStartInfo startInfo = new()
                {
                    FileName = installerPath,
                    // For Windows, the /D= parameter needs to be last
                    Arguments = $"/S /D={escapedPath}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory
                };

                using Process process = Process.Start(startInfo) ??
                    throw new Exception("Failed to start Miniconda installer process");

                _logger.Trace("Waiting for installer to complete...");
                await process.WaitForExitAsync();

                _logger.Trace($"Installer exited with code: {process.ExitCode}");

                if (process.ExitCode != 0)
                    throw new Exception($"Miniconda installation failed with exit code {process.ExitCode}");

                string condaExecutable = Path.Combine(installFolder, "Scripts", "conda.exe");

                if (!File.Exists(condaExecutable))
                {
                    _logger.Error($"Conda executable not found at expected location: {condaExecutable}");
                    throw new Exception("Miniconda installation did not create conda.exe");
                }

                _logger.Debug(isUpdate
                    ? $"Successfully updated Miniconda installation at: {installFolder}"
                    : $"Successfully installed Miniconda at: {installFolder}");
                process.Close();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error during Miniconda installation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Installs Miniconda on Unix-like systems (Linux and macOS).
        /// </summary>
        private static async Task InstallMinicondaUnixAsync(string installerPath, string installFolder)
        {
            (int chmodExitCode, _, string chmodError) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                $"chmod +x \"{installerPath}\"",
                Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
                captureStdErr: true);

            if (chmodExitCode != 0)
                throw new Exception($"Failed to make installer executable: exit code {chmodExitCode}: {chmodError}");

            // Check if directory already exists and add -u flag if needed
            string installArgs = Directory.Exists(installFolder)
                ? $"\"{installerPath}\" -b -u -p \"{installFolder}\""  // Add -u flag for update
                : $"\"{installerPath}\" -b -p \"{installFolder}\"";    // Normal install

            _logger.Trace($"Running installer with command: {installArgs}");

            using Process installProcess = CrossPlatformProcessRunner.CreateProcess(
                "/bin/bash",
                $"-c {installArgs}",
                Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory);

            installProcess.Start();
            string output = await installProcess.StandardOutput.ReadToEndAsync();
            string error = await installProcess.StandardError.ReadToEndAsync();
            await installProcess.WaitForExitAsync();

            if (installProcess.ExitCode != 0)
                throw new Exception($"Miniconda installation failed with exit code {installProcess.ExitCode}: {error}");

            _logger.Debug(output);
        }

        /// <summary>
        /// Retrieves the download URL and hash for the latest Miniconda installer.
        /// </summary>
        private static async Task<(string, string)> GetMinicondaDownloadUrlAsync(OSPlatform platform, string architecture)
        {
            if (!InstallerPatterns.TryGetValue((platform, architecture), out _))
                throw new PlatformNotSupportedException($"Unsupported platform ({platform}) or architecture ({architecture})");

            if (!LatestInstallers.TryGetValue((platform, architecture), out string? latestInstallerName))
                throw new PlatformNotSupportedException($"No latest installer available for platform ({platform}) and architecture ({architecture})");

            using HttpClient client = new();
            string html = await client.GetStringAsync(condaUrl);

            if (string.IsNullOrEmpty(html))
                throw new Exception("Failed to retrieve Miniconda installer information");

            string url = condaUrl + latestInstallerName;

            string hashPattern = $@"<td><a href=""{latestInstallerName}"".*?</a></td>\s*<td class=""s"">.*?</td>\s*<td>.*?</td>\s*<td>([a-fA-F0-9]{{64}})</td>";
            Match match = Regex.Match(html, hashPattern);

            if (!match.Success)
                throw new Exception($"Unable to find the hash for {latestInstallerName}");

            string hash = match.Groups[1].Value;
            return (url, hash);
        }

        /// <summary>
        /// Calculates the SHA-256 hash of a file.
        /// </summary>
        private static string CalculateFileHash(string filePath)
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hashBytes = sha256.ComputeHash(stream);
            StringBuilder sb = new();
            foreach (byte b in hashBytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}