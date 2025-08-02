using NLog;
using NzbDrone.Common.Http;
using System.IO.Compression;
using System.Text.Json;
using Tubifarry.Core.PythonBridge;

namespace Tubifarry.Core.NodeBridge
{
    /// <summary>
    /// Provides functionality to download and install Node.js across all platforms and architectures.
    /// Handles proper extraction without nested directory structures.
    /// </summary>
    public static class NodeJSInstaller
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static IHttpClient? _httpClient;

        // Official Node.js distribution endpoints
        private const string NODE_DIST_URL = "https://nodejs.org/dist/";
        private const string NODE_VERSIONS_URL = "https://nodejs.org/dist/index.json";
        private const string DEFAULT_NODE_VERSION = "24.5.0";

        // Platform/architecture to package mapping
        private static readonly Dictionary<(OSPlatform, string), string[]> NodePackagePatterns = new()
        {
            // Windows packages
            { (OSPlatform.Windows, "x86_64"), new[] { "node-v{0}-win-x64.zip", "node-v{0}-win-x64.7z" } },
            { (OSPlatform.Windows, "x86"), new[] { "node-v{0}-win-x86.zip", "node-v{0}-win-x86.7z" } },
            { (OSPlatform.Windows, "arm64"), new[] { "node-v{0}-win-arm64.zip", "node-v{0}-win-arm64.7z" } },
            // Linux packages
            { (OSPlatform.Linux, "x86_64"), new[] { "node-v{0}-linux-x64.tar.gz", "node-v{0}-linux-x64.tar.xz" } },
            { (OSPlatform.Linux, "x86"), new[] { "node-v{0}-linux-x86.tar.gz", "node-v{0}-linux-x86.tar.xz" } },
            { (OSPlatform.Linux, "arm64"), new[] { "node-v{0}-linux-arm64.tar.gz", "node-v{0}-linux-arm64.tar.xz" } },
            { (OSPlatform.Linux, "armv7l"), new[] { "node-v{0}-linux-armv7l.tar.gz", "node-v{0}-linux-armv7l.tar.xz" } },
            { (OSPlatform.Linux, "ppc64le"), new[] { "node-v{0}-linux-ppc64le.tar.gz", "node-v{0}-linux-ppc64le.tar.xz" } },
            { (OSPlatform.Linux, "s390x"), new[] { "node-v{0}-linux-s390x.tar.gz", "node-v{0}-linux-s390x.tar.xz" } },
            // macOS packages
            { (OSPlatform.MacOS, "x86_64"), new[] { "node-v{0}-darwin-x64.tar.gz", "node-v{0}-darwin-x64.tar.xz" } },
            { (OSPlatform.MacOS, "arm64"), new[] { "node-v{0}-darwin-arm64.tar.gz", "node-v{0}-darwin-arm64.tar.xz" } }
        };

        /// <summary>
        /// Finds existing Node.js installation or downloads and installs it to the specified location.
        /// </summary>
        /// <param name="installPath">Base installation path</param>
        /// <param name="version">Node.js version to install (defaults to latest LTS)</param>
        /// <param name="forceReinstall">Force reinstallation even if Node.js exists</param>
        /// <returns>Path to Node.js installation or null if failed</returns>
        public static async Task<string?> FindOrInstallNodeAsync(IHttpClient client, string installPath, string? version = null, bool forceReinstall = false)
        {
            if (string.IsNullOrWhiteSpace(installPath))
                throw new ArgumentException("Install path cannot be empty.", nameof(installPath));

            _httpClient = client;

            try
            {
                Directory.CreateDirectory(installPath);

                if (!forceReinstall)
                {
                    string? systemNode = await FindNodeInSystemPathAsync();
                    if (!string.IsNullOrWhiteSpace(systemNode) && await IsCompatibleNodeVersionAsync(systemNode, version))
                    {
                        _logger.Debug($"Found compatible system Node.js at: {systemNode}");
                        return systemNode;
                    }
                    string? localNode = FindLocalNodeInstallation(installPath);
                    if (!string.IsNullOrWhiteSpace(localNode) && await IsCompatibleNodeVersionAsync(localNode, version))
                    {
                        _logger.Debug($"Found compatible local Node.js at: {localNode}");
                        return localNode;
                    }
                }
                string targetVersion = version ?? await GetLatestLtsVersionAsync();
                _logger.Debug($"Installing Node.js {targetVersion} to {installPath}");
                await DownloadAndInstallNodeAsync(installPath, targetVersion);

                string? installedPath = FindLocalNodeInstallation(installPath);
                if (!string.IsNullOrWhiteSpace(installedPath))
                {
                    _logger.Debug($"Node.js {targetVersion} installed successfully at: {installedPath}");
                    return installedPath;
                }

                _logger.Error("Node.js installation verification failed");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error in FindOrInstallNodeAsync: {ex.Message}");
                throw new NodeJsConfigurationException("Failed to find or install Node.js", ex);
            }
        }

        /// <summary>
        /// Finds Node.js in the system PATH.
        /// </summary>
        private static async Task<string?> FindNodeInSystemPathAsync()
        {
            try
            {
                OSPlatform platform = PlatformUtils.GetCurrentPlatform();
                const string nodeExecutableName = "node";

                string[] commands = platform == OSPlatform.Windows
                    ? new[] { $"where {nodeExecutableName}" }
                    : new[] { $"which {nodeExecutableName}", $"command -v {nodeExecutableName}" };

                foreach (string command in commands)
                {
                    try
                    {
                        (int exitCode, string output, string _) = await CrossPlatformProcessRunner.ExecuteShellCommand(command);

                        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        {
                            string nodePath = output.Trim().Split(Environment.NewLine)[0];
                            if (File.Exists(nodePath))
                                return Path.GetDirectoryName(nodePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Trace($"Command '{command}' failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Trace($"Error checking for system Node.js: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Finds local Node.js installation in the specified path.
        /// </summary>
        private static string? FindLocalNodeInstallation(string basePath)
        {
            if (!Directory.Exists(basePath))
                return null;

            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string nodeExecutableName = "node" + PlatformUtils.GetExecutableExtension();

            try
            {
                foreach (string nodeFile in Directory.GetFiles(basePath, nodeExecutableName, SearchOption.AllDirectories))
                {
                    string nodeDir = Path.GetDirectoryName(nodeFile)!;

                    if (platform == OSPlatform.Windows)
                        return nodeDir;

                    if (nodeDir.EndsWith("bin"))
                        return Path.GetDirectoryName(nodeDir);

                }

                _logger.Debug($"No Node.js executable found in: {basePath}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error searching for local Node.js: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Checks if the Node.js installation is compatible with the requested version.
        /// </summary>
        private static async Task<bool> IsCompatibleNodeVersionAsync(string nodePath, string? requestedVersion)
        {
            try
            {
                OSPlatform platform = PlatformUtils.GetCurrentPlatform();
                string nodeExe = platform == OSPlatform.Windows
                    ? Path.Combine(nodePath, "node.exe")
                    : Path.Combine(nodePath, "bin", "node");

                if (!File.Exists(nodeExe))
                    return false;

                (int exitCode, string output, string _) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    $"{nodeExe} --version");

                if (exitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    string version = output.Trim().TrimStart('v');

                    if (string.IsNullOrEmpty(requestedVersion))
                        return true;

                    string[] installedParts = version.Split('.');
                    string[] requestedParts = requestedVersion.Split('.');

                    if (installedParts.Length > 0 && requestedParts.Length > 0)
                        return installedParts[0] == requestedParts[0];

                    return version.StartsWith(requestedVersion);
                }
            }
            catch (Exception ex)
            {
                _logger.Trace($"Error checking Node.js version compatibility: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Downloads and installs Node.js with proper directory flattening.
        /// </summary>
        private static async Task DownloadAndInstallNodeAsync(string installPath, string version)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string architecture = PlatformUtils.GetArchitecture();

            try
            {
                string? downloadUrl = await GetNodeDownloadUrlAsync(platform, architecture, version) ?? throw new PlatformNotSupportedException($"No Node.js package available for platform ({platform}) and architecture ({architecture})");
                Directory.CreateDirectory(installPath);

                _logger.Trace($"Downloading Node.js from: {downloadUrl}");

                HttpRequest downloadRequest = new HttpRequestBuilder(downloadUrl)
                    .Build();

                downloadRequest.AllowAutoRedirect = true;
                downloadRequest.RequestTimeout = TimeSpan.FromSeconds(300);

                HttpResponse downloadResponse = _httpClient.Get(downloadRequest);

                string fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
                string downloadPath = Path.Combine(installPath, fileName);

                await File.WriteAllBytesAsync(downloadPath, downloadResponse.ResponseData);
                _logger.Debug($"Downloaded Node.js archive to: {downloadPath}");

                await ExtractNodeArchiveAsync(downloadPath, installPath, platform);
                try
                {
                    File.Delete(downloadPath);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to delete downloaded archive: {ex.Message}");
                }

                string? extractedNodeDir = FindExtractedNodeDirectory(installPath) ?? throw new NodeJsConfigurationException("Could not find Node.js directory in extracted archive");
                if (!string.Equals(extractedNodeDir, installPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info($"Flattening Node.js structure from {extractedNodeDir} to {installPath}");
                    await FlattenDirectoryStructureAsync(extractedNodeDir, installPath);
                }

                if (platform != OSPlatform.Windows)
                    await SetExecutablePermissionsAsync(installPath);

                _logger.Info("Node.js installation complete.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error installing Node.js: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Finds the actual Node.js directory inside the installation path.
        /// </summary>
        private static string? FindExtractedNodeDirectory(string installPath)
        {
            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string nodeExecutableName = "node" + PlatformUtils.GetExecutableExtension();
            foreach (string nodeFile in Directory.GetFiles(installPath, nodeExecutableName, SearchOption.AllDirectories))
            {
                string nodeDir = Path.GetDirectoryName(nodeFile)!;

                if (platform == OSPlatform.Windows)
                    return nodeDir;
                else if (nodeDir.EndsWith("bin"))
                    return Path.GetDirectoryName(nodeDir);

            }
            return null;
        }

        /// <summary>
        /// Flattens directory structure by moving contents from nested directory to parent.
        /// </summary>
        private static async Task FlattenDirectoryStructureAsync(string sourceDir, string destinationDir)
        {
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destinationDir, fileName);
                File.Move(file, destFile, true);
            }
            foreach (string directory in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(directory);
                string destDir = Path.Combine(destinationDir, dirName);
                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, true);
                Directory.Move(directory, destDir);
            }
            try
            {
                Directory.Delete(sourceDir, true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to delete nested directory {sourceDir}: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Gets the latest LTS version of Node.js.
        /// </summary>
        private static async Task<string> GetLatestLtsVersionAsync()
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder(NODE_VERSIONS_URL)
                    .Build();

                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);

                HttpResponse response = _httpClient.Get(request);
                string json = response.Content;

                using JsonDocument doc = JsonDocument.Parse(json);
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("lts", out JsonElement ltsElement) &&
                        ltsElement.ValueKind != JsonValueKind.False &&
                        ltsElement.ValueKind != JsonValueKind.Null)
                    {
                        string? version = element.GetProperty("version").GetString();
                        if (!string.IsNullOrEmpty(version))
                            return version.TrimStart('v');
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to get LTS version from {NODE_VERSIONS_URL}: {ex.Message}");
                _logger.Info($"Falling back to default Node.js version {DEFAULT_NODE_VERSION}");
                return DEFAULT_NODE_VERSION;
            }

            throw new NodeJsConfigurationException("Could not determine LTS version and fallback failed");
        }

        /// <summary>
        /// Gets the download URL for Node.js with fallback support.
        /// </summary>
        private static async Task<string?> GetNodeDownloadUrlAsync(OSPlatform platform, string architecture, string version)
        {
            if (!NodePackagePatterns.TryGetValue((platform, architecture), out string[]? patterns))
                return null;

            foreach (string pattern in patterns)
            {
                string fileName = string.Format(pattern, version);
                string url = $"{NODE_DIST_URL}v{version}/{fileName}";

                try
                {
                    HttpRequest request = new HttpRequestBuilder(url)
                        .Build();

                    request.AllowAutoRedirect = true;
                    request.RequestTimeout = TimeSpan.FromSeconds(15);

                    HttpResponse response = _httpClient.Head(request);

                    if (response.HasHttpError == false)
                        return url;
                }
                catch (Exception ex)
                {
                    _logger.Trace($"Failed to check URL {url}: {ex.Message}");
                }
            }

            _logger.Error($"No Node.js package found for {platform}-{architecture} v{version}");
            return null;
        }

        /// <summary>
        /// Sets executable permissions on Unix systems.
        /// </summary>
        private static async Task SetExecutablePermissionsAsync(string installPath)
        {
            try
            {
                IEnumerable<string> nodeFiles = Directory.GetFiles(installPath, "node*", SearchOption.AllDirectories)
                    .Where(f => !Path.HasExtension(f) || Path.GetExtension(f) == ".exe");

                IEnumerable<string> npmFiles = Directory.GetFiles(installPath, "npm*", SearchOption.AllDirectories)
                    .Where(f => !Path.HasExtension(f) || Path.GetExtension(f) == ".cmd");

                foreach (string execPath in nodeFiles.Concat(npmFiles))
                {
                    if (File.Exists(execPath))
                        await CrossPlatformProcessRunner.ExecuteShellCommand($"chmod +x \"{execPath}\"");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to set executable permissions: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts Node.js archive to the specified directory.
        /// </summary>
        private static async Task ExtractNodeArchiveAsync(string archivePath, string extractPath, OSPlatform platform)
        {
            string extension = Path.GetExtension(archivePath).ToLowerInvariant();
            string fullExtension = Path.GetFileName(archivePath).ToLowerInvariant();

            try
            {
                if (platform == OSPlatform.Windows && extension == ".zip")
                    ExtractZip(archivePath, extractPath);
                else if (fullExtension.EndsWith(".tar.gz"))
                    await ExtractTarGz(archivePath, extractPath);
                else if (fullExtension.EndsWith(".tar.xz"))
                    await ExtractTarXz(archivePath, extractPath);
                else if (extension == ".7z")
                    await Extract7z(archivePath, extractPath);
                else
                    throw new NotSupportedException($"Unsupported archive format: {fullExtension}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to extract archive: {archivePath}");
                throw;
            }
        }

        /// <summary>
        /// Extracts ZIP file.
        /// </summary>
        private static void ExtractZip(string zipPath, string extractPath)
        {
            try
            {
                _logger.Debug($"Extracting ZIP: {zipPath} to {extractPath}");
                ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);
                _logger.Debug("ZIP extraction completed");
            }
            catch (Exception ex)
            {
                throw new NodeJsConfigurationException($"Failed to extract ZIP: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Extracts tar.gz file.
        /// </summary>
        private static async Task ExtractTarGz(string tarGzPath, string extractPath)
        {
            try
            {
                _logger.Debug($"Extracting tar.gz: {tarGzPath} to {extractPath}");
                try
                {
                    await using FileStream fileStream = File.OpenRead(tarGzPath);
                    await using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);

                    Type? tarFileType = Type.GetType("System.Formats.Tar.TarFile, System.Formats.Tar");
                    if (tarFileType != null)
                    {
                        System.Reflection.MethodInfo? extractMethod = tarFileType.GetMethod("ExtractToDirectory",
                            new[] { typeof(Stream), typeof(string), typeof(bool) });

                        if (extractMethod != null)
                        {
                            extractMethod.Invoke(null, new object[] { gzipStream, extractPath, true });
                            _logger.Debug("tar.gz extracted using .NET native support");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($".NET native extraction failed: {ex.Message}");
                }

                (int exitCode, string _, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    $"tar -xzf \"{tarGzPath}\" -C \"{extractPath}\"", extractPath, captureStdErr: true);

                if (exitCode != 0)
                    throw new NodeJsConfigurationException($"tar command failed: {error}");

                _logger.Debug("tar.gz extracted using tar command");
            }
            catch (Exception ex)
            {
                throw new NodeJsConfigurationException($"Failed to extract tar.gz: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Extracts tar.xz file.
        /// </summary>
        private static async Task ExtractTarXz(string tarXzPath, string extractPath)
        {
            try
            {
                _logger.Debug($"Extracting tar.xz: {tarXzPath} to {extractPath}");

                (int exitCode, string _, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    $"tar -xJf \"{tarXzPath}\" -C \"{extractPath}\"", extractPath, captureStdErr: true);

                if (exitCode != 0)
                    throw new NodeJsConfigurationException($"tar.xz extraction failed: {error}");

                _logger.Debug("tar.xz extraction completed");
            }
            catch (Exception ex)
            {
                throw new NodeJsConfigurationException($"Failed to extract tar.xz: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Extracts 7z file.
        /// </summary>
        private static async Task Extract7z(string archivePath, string extractPath)
        {
            try
            {
                _logger.Debug($"Extracting 7z: {archivePath} to {extractPath}");

                (int exitCode, string _, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    $"7z x \"{archivePath}\" -o\"{extractPath}\" -y", extractPath, captureStdErr: true);

                if (exitCode != 0)
                    throw new NodeJsConfigurationException($"7z extraction failed: {error}");

                _logger.Debug("7z extraction completed");
            }
            catch (Exception ex)
            {
                throw new NodeJsConfigurationException($"Failed to extract 7z: {ex.Message}", ex);
            }
        }
    }
}