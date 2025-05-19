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

        // Maps platform and architecture to latest installer names
        private static readonly Dictionary<(OSPlatform, string), string> LatestInstallers = new()
        {
            { (OSPlatform.Windows, "x86_64"), "Miniconda3-latest-Windows-x86_64.exe" },
            { (OSPlatform.Windows, "x86"), "Miniconda3-latest-Windows-x86.exe" },
            { (OSPlatform.Linux, "x86_64"), "Miniconda3-latest-Linux-x86_64.sh" },
            { (OSPlatform.Linux, "x86"), "Miniconda3-latest-Linux-x86.sh" },
            { (OSPlatform.Linux, "arm64"), "Miniconda3-latest-Linux-aarch64.sh" },
            { (OSPlatform.MacOS, "x86_64"), "Miniconda3-latest-MacOSX-x86_64.sh" },
            { (OSPlatform.MacOS, "arm64"), "Miniconda3-latest-MacOSX-arm64.sh" }
        };

        /// <summary>
        /// Finds the Conda environment and returns an installation path.
        /// If Conda is not found, it downloads and installs Miniconda.
        /// </summary>
        public static async Task<string?> FindOrInstallCondaAsync(params string[] installPaths)
        {
            if (installPaths.Length == 0)
                installPaths = new string[] { Environment.CurrentDirectory };

            string? systemConda = FindCondaInSystemPath();
            if (!string.IsNullOrWhiteSpace(systemConda))
            {
                _logger.Info($"Found system Conda at: {systemConda}");
                return systemConda;
            }

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
        /// Looks for Conda in the system path
        /// </summary>
        private static string? FindCondaInSystemPath()
        {
            try
            {
                OSPlatform platform = PlatformUtils.GetCurrentPlatform();
                string condaExecutableName = "conda" + PlatformUtils.GetExecutableExtension();
                string command = platform == OSPlatform.Windows ? "where" : "which";

                (int exitCode, string output, string _) = CrossPlatformProcessRunner.ExecuteShellCommand($"{command} {condaExecutableName}")
                    .GetAwaiter().GetResult();

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    string condaPath = output.Trim().Split(Environment.NewLine)[0];
                    if (File.Exists(condaPath))
                        return Path.GetDirectoryName(Path.GetDirectoryName(condaPath));
                }
            }
            catch (Exception ex)
            {
                _logger.Trace($"Error checking for system Conda: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Checks if the system is running Alpine Linux
        /// </summary>
        private static bool IsAlpineLinux() => File.Exists("/etc/alpine-release");


        /// <summary>
        /// Finds the path to the Conda installation.
        /// </summary>
        private static string? FindCondaPath(string[] installPaths)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string condaExecutableName = "conda" + PlatformUtils.GetExecutableExtension();

            foreach (string basePath in installPaths)
            {
                if (!Directory.Exists(basePath))
                    continue;

                try
                {
                    string[] minicondaDirs = Directory.GetDirectories(basePath, "miniconda3*", SearchOption.TopDirectoryOnly);

                    foreach (string dir in minicondaDirs)
                    {
                        string? condaPath = platform switch
                        {
                            OSPlatform.Windows => Path.Combine(dir, "Scripts", condaExecutableName),
                            OSPlatform.Linux or OSPlatform.MacOS => Path.Combine(dir, "bin", condaExecutableName),
                            _ => null
                        };

                        if (condaPath != null && File.Exists(condaPath))
                            return dir;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error searching for conda: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Sets up the Alpine Linux environment for Miniconda installation.
        /// FAILS HARD on any permission errors.
        /// </summary>
        private static async Task SetupAlpineEnvironmentAsync(string tempDir)
        {
            _logger.Info("Setting up Alpine Linux environment for Miniconda installation");

            // 1. Install required base packages
            _logger.Debug("Installing required base packages for Alpine");
            (int exitCode1, _, string error1) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                "apk add --no-cache wget ca-certificates bash libstdc++",
                tempDir,
                captureStdErr: true);

            if (exitCode1 != 0)
            {
                _logger.Error($"Failed to install base packages: {error1}");
                throw new Exception($"Cannot install Alpine base packages. Exit code: {exitCode1}. Error: {error1}");
            }

            // 2. Download and install glibc packages
            _logger.Trace("Installing glibc packages for Alpine");
            string glibcDir = Path.Combine(tempDir, "alpine-glibc");
            Directory.CreateDirectory(glibcDir);

            using (HttpClient client = new())
            {
                // Download sgerrand.rsa.pub key
                const string keyUrl = "https://alpine-pkgs.sgerrand.com/sgerrand.rsa.pub";
                string keyPath = Path.Combine(glibcDir, "sgerrand.rsa.pub");
                _logger.Trace($"Downloading {keyUrl}");
                byte[] keyData = await client.GetByteArrayAsync(keyUrl);
                await File.WriteAllBytesAsync(keyPath, keyData);

                // Download glibc packages
                string[] glibcPackages = new[]
                {
                    "glibc-2.35-r0.apk",
                    "glibc-bin-2.35-r0.apk",
                    "glibc-i18n-2.35-r0.apk"
                };

                foreach (string package in glibcPackages)
                {
                    string packageUrl = $"https://github.com/sgerrand/alpine-pkg-glibc/releases/download/2.35-r0/{package}";
                    string packagePath = Path.Combine(glibcDir, package);
                    _logger.Trace($"Downloading {packageUrl}");
                    byte[] packageData = await client.GetByteArrayAsync(packageUrl);
                    await File.WriteAllBytesAsync(packagePath, packageData);
                }
            }

            // Install key
            (int exitCode2, _, string error2) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                $"cp {Path.Combine(glibcDir, "sgerrand.rsa.pub")} /etc/apk/keys/",
                tempDir,
                captureStdErr: true);

            if (exitCode2 != 0)
            {
                _logger.Error($"Failed to install sgerrand key: {error2}");
                throw new Exception($"Cannot install sgerrand key. Exit code: {exitCode2}. Error: {error2}. This requires root privileges.");
            }

            // Install glibc packages
            string packagesPath = string.Join(" ", Directory.GetFiles(glibcDir, "*.apk"));
            (int exitCode3, _, string error3) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                $"apk add --no-cache --force-overwrite {packagesPath}",
                tempDir,
                captureStdErr: true);

            if (exitCode3 != 0)
            {
                _logger.Error($"Failed to install glibc packages: {error3}");
                throw new Exception($"Cannot install glibc packages. Exit code: {exitCode3}. Error: {error3}. This requires root privileges.");
            }

            // 3. Set up locale
            _logger.Debug("Setting up locale for Alpine");
            (int exitCode4, _, string error4) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                "/usr/glibc-compat/bin/localedef -i en_US -f UTF-8 en_US.UTF-8",
                tempDir,
                captureStdErr: true);

            if (exitCode4 != 0)
            {
                _logger.Error($"Failed to set up locale: {error4}");
                throw new Exception($"Cannot set up locale. Exit code: {exitCode4}. Error: {error4}");
            }

            // 4. Create critical symlinks
            _logger.Debug("Creating critical symlinks for glibc compatibility");

            (int exitCode5, _, string error5) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                "ln -sf /usr/glibc-compat/lib/ld-linux-x86-64.so.2 /lib/",
                tempDir,
                captureStdErr: true);

            if (exitCode5 != 0)
            {
                _logger.Error($"Failed to create ld-linux symlink: {error5}");
                throw new Exception($"Cannot create ld-linux symlink. Exit code: {exitCode5}. Error: {error5}. This requires root privileges.");
            }

            (int exitCode6, _, string error6) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                "ln -sf /usr/glibc-compat/lib/libc.so.6 /lib/",
                tempDir,
                captureStdErr: true);

            if (exitCode6 != 0)
            {
                _logger.Error($"Failed to create libc symlink: {error6}");
                throw new Exception($"Cannot create libc symlink. Exit code: {exitCode6}. Error: {error6}. This requires root privileges.");
            }

            // 5. Set environment variables
            _logger.Trace("Setting glibc environment variables");
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", "/usr/glibc-compat/lib", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("PATH", $"/usr/glibc-compat/bin:{Environment.GetEnvironmentVariable("PATH")}", EnvironmentVariableTarget.Process);

            _logger.Debug("Alpine Linux environment setup completed successfully");
        }

        /// <summary>
        /// Downloads and installs Miniconda.
        /// </summary>
        private static async Task DownloadAndInstallMinicondaAsync(string installFolder)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string architecture = PlatformUtils.GetArchitecture();
            bool isAlpine = platform == OSPlatform.Linux && IsAlpineLinux();

            try
            {
                (string downloadUrl, string hash) = await GetMinicondaDownloadUrlAsync(platform, architecture);
                Directory.CreateDirectory(installFolder);

                string tempDir = Path.Combine(Path.GetTempPath(), "miniconda_installer_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                _logger.Debug($"Created temp directory: {tempDir}");

                if (isAlpine)
                    await SetupAlpineEnvironmentAsync(tempDir);

                string installerPath = Path.Combine(tempDir, Path.GetFileName(downloadUrl));
                _logger.Debug($"Downloading Miniconda installer to: {installerPath}");

                using (HttpClient client = new())
                {
                    byte[] installerData = await client.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(installerPath, installerData);
                }

                string fileHash = CalculateFileHash(installerPath);
                if (hash != fileHash)
                    throw new Exception("File hash does not match the expected hash.");

                _logger.Info("Installing Miniconda...");

                if (platform == OSPlatform.Windows)
                    await InstallMinicondaWindowsAsync(installerPath, installFolder);
                else
                    await InstallMinicondaUnixAsync(installerPath, installFolder, isAlpine);

                _logger.Info("Miniconda installation complete.");

                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
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
        /// Installs Miniconda on Windows.
        /// </summary>
        private static async Task InstallMinicondaWindowsAsync(string installerPath, string installFolder)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = installerPath,
                Arguments = $"/S /D={installFolder}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory
            };

            using Process process = Process.Start(startInfo) ?? throw new Exception("Failed to start Miniconda installer process");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new Exception($"Miniconda installation failed with exit code {process.ExitCode}");

            string condaExecutable = Path.Combine(installFolder, "Scripts", "conda.exe");
            if (!File.Exists(condaExecutable))
                throw new Exception("Miniconda installation did not create conda.exe");

            _logger.Info($"Successfully installed Miniconda at: {installFolder}");
        }

        private static async Task InstallMinicondaUnixAsync(string installerPath, string installFolder, bool isAlpine = false)
        {
            (int chmodExitCode, _, string chmodError) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                $"chmod +x \"{installerPath}\"",
                Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
                captureStdErr: true);

            if (chmodExitCode != 0)
                throw new Exception($"Failed to make installer executable: exit code {chmodExitCode}: {chmodError}");

            bool needsUpdate = Directory.Exists(installFolder);
            string installCmd = needsUpdate
                ? $"bash \"{installerPath}\" -b -u -p \"{installFolder}\""  // Add -u flag for update
                : $"bash \"{installerPath}\" -b -p \"{installFolder}\"";   // Normal install

            // For Alpine Linux, ensure the environment variables are set
            if (isAlpine)
                installCmd = $"export LD_LIBRARY_PATH=/usr/glibc-compat/lib && export PATH=/usr/glibc-compat/bin:$PATH && {installCmd}";

            _logger.Debug($"Running installer with command: {installCmd}");

            (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                installCmd,
                Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
                captureStdErr: true);

            if (exitCode != 0)
                throw new Exception($"Miniconda installation failed with exit code {exitCode}: {error}");

            string condaExecutable = Path.Combine(installFolder, "bin", "conda");
            if (!File.Exists(condaExecutable))
                throw new Exception($"Conda executable not found at expected location: {condaExecutable}");

            _logger.Info($"Successfully installed Miniconda at: {installFolder}");
        }

        /// <summary>
        /// Retrieves the download URL and hash for the latest Miniconda installer.
        /// </summary>
        private static async Task<(string, string)> GetMinicondaDownloadUrlAsync(OSPlatform platform, string architecture)
        {
            if (!LatestInstallers.TryGetValue((platform, architecture), out string? latestInstallerName))
                throw new PlatformNotSupportedException($"No installer available for platform ({platform}) and architecture ({architecture})");

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