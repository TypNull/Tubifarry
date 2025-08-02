using NLog;
using NzbDrone.Common.Instrumentation;
using System.Collections.Concurrent;
using System.Text.Json;
using Tubifarry.Core.PythonBridge;

namespace Tubifarry.Core.NodeBridge
{
    /// <summary>
    /// Provides robust management and interaction with Node.js and npm installations.
    /// Handles all Node.js process execution and package management operations.
    /// </summary>
    public class NodeJSClient
    {
        private const int CACHE_EXPIRATION = 5;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(CACHE_EXPIRATION);

        private readonly string _nodeExecutablePath;
        private readonly string _npmExecutablePath;
        private readonly string _isolatedPackagesPath;
        private readonly Logger _logger;

        // Thread-safe cache for Node.js information
        private static readonly ConcurrentDictionary<string, CachedNodeInfo> _nodeInfoCache = new();

        private record CachedNodeInfo(NodeInstallInfo Info, DateTime CacheTimestamp);

        /// <summary>
        /// Initializes a new instance of the NodeClient.
        /// </summary>
        /// <param name="nodeInstallPath">Path to Node.js installation directory</param>
        /// <param name="isolatedPackagesPath">Base path for isolated environments</param>
        /// <exception cref="ArgumentException">Thrown when paths are invalid</exception>
        public NodeJSClient(string nodeInstallPath, string isolatedPackagesPath = "")
        {
            if (string.IsNullOrWhiteSpace(nodeInstallPath))
                throw new ArgumentException("Node.js install path cannot be empty.", nameof(nodeInstallPath));

            if (!Directory.Exists(nodeInstallPath))
                throw new ArgumentException("Node.js install path does not exist.", nameof(nodeInstallPath));

            _logger = NzbDroneLogger.GetLogger(this);
            _isolatedPackagesPath = string.IsNullOrWhiteSpace(isolatedPackagesPath) ? nodeInstallPath : isolatedPackagesPath;

            OSPlatform platform = PlatformUtils.GetCurrentPlatform();
            string nodeExe = "node" + PlatformUtils.GetExecutableExtension();

            _nodeExecutablePath = platform == OSPlatform.Windows
                ? Path.Combine(nodeInstallPath, nodeExe)
                : Path.Combine(nodeInstallPath, "bin", nodeExe);

            _npmExecutablePath = FindNpmExecutable(nodeInstallPath, platform);

            if (!File.Exists(_nodeExecutablePath))
                throw new ArgumentException($"Node.js executable not found at {_nodeExecutablePath}");

            if (string.IsNullOrEmpty(_npmExecutablePath) || !File.Exists(_npmExecutablePath))
                throw new ArgumentException($"npm executable not found in {nodeInstallPath}. Searched for npm, npm.cmd, npm.exe");
        }

        /// <summary>
        /// Finds npm executable with platform-specific fallback logic.
        /// </summary>
        private static string FindNpmExecutable(string nodeInstallPath, OSPlatform platform)
        {
            if (platform == OSPlatform.Windows)
            {
                string[] npmCandidates = { "npm.cmd", "npm.exe", "npm" };

                foreach (string candidate in npmCandidates)
                {
                    string npmPath = Path.Combine(nodeInstallPath, candidate);
                    if (File.Exists(npmPath))
                        return npmPath;
                }

                return string.Empty;
            }
            else
            {
                string binPath = Path.Combine(nodeInstallPath, "bin");
                string[] npmCandidates = { "npm" };

                foreach (string candidate in npmCandidates)
                {
                    string npmPath = Path.Combine(binPath, candidate);
                    if (File.Exists(npmPath))
                        return npmPath;
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// Gets Node.js installation information with intelligent caching.
        /// </summary>
        public async Task<NodeInstallInfo> GetNodeInfoAsync()
        {
            string cacheKey = _nodeExecutablePath;

            if (_nodeInfoCache.TryGetValue(cacheKey, out CachedNodeInfo? cachedInfo) &&
                DateTime.Now - cachedInfo.CacheTimestamp < CacheExpiration)
            {
                _logger.Debug("Returning cached Node.js information");
                return cachedInfo.Info;
            }

            try
            {
                string nodeVersion = await GetNodeVersionAsync();
                string npmVersion = await GetNpmVersionAsync();

                NodeInstallInfo nodeInfo = new(
                    Version: nodeVersion,
                    InstallPath: Path.GetDirectoryName(_nodeExecutablePath)!,
                    NodeExecutable: _nodeExecutablePath,
                    NpmExecutable: _npmExecutablePath,
                    NpmVersion: npmVersion,
                    InstallDate: File.GetCreationTime(_nodeExecutablePath)
                );

                _nodeInfoCache[cacheKey] = new CachedNodeInfo(nodeInfo, DateTime.Now);
                return nodeInfo;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get Node.js information");
                throw new NodeJsConfigurationException("Failed to retrieve Node.js information", ex);
            }
        }

        /// <summary>
        /// Gets Node.js version information.
        /// </summary>
        public async Task<string> GetNodeVersionAsync()
        {
            try
            {
                string command = $"{_nodeExecutablePath} --version";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode != 0)
                    throw new NodeJsExecutionException($"Failed to get Node.js version. Exit code: {exitCode}, Error: {error}");

                return output.Trim().TrimStart('v');
            }
            catch (Exception ex) when (ex is not NodeJsExecutionException)
            {
                _logger.Error(ex, "Error getting Node.js version");
                throw new NodeJsExecutionException("Error getting Node.js version", ex);
            }
        }

        /// <summary>
        /// Gets npm version information.
        /// </summary>
        public async Task<string> GetNpmVersionAsync()
        {
            try
            {
                string command = $"{_npmExecutablePath} --version";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode != 0)
                    throw new NodeJsExecutionException($"Failed to get npm version. Exit code: {exitCode}, Error: {error}");

                return output.Trim();
            }
            catch (Exception ex) when (ex is not NodeJsExecutionException)
            {
                _logger.Error(ex, "Error getting npm version");
                throw new NodeJsExecutionException("Error getting npm version", ex);
            }
        }

        /// <summary>
        /// Installs npm packages with Docker-friendly configuration and dependency conflict resolution.
        /// </summary>
        public async Task<bool> InstallPackageAsync(string packageName, string? workingDirectory = null, bool global = false)
        {
            try
            {
                string globalFlag = global ? " -g" : "";
                string dockerFlags = Environment.UserName == "root" ? " --unsafe-perm" : "";
                string legacyFlag = !global ? " --legacy-peer-deps" : "";

                string command = $"{_npmExecutablePath} install {packageName}{globalFlag}{dockerFlags}{legacyFlag}";

                string actualWorkingDirectory = workingDirectory;
                if (!global && string.IsNullOrEmpty(workingDirectory))
                {
                    string nodeInstallDir = Path.GetDirectoryName(_nodeExecutablePath)!;
                    string packagesDir = Path.Combine(nodeInstallDir, "packages");
                    Directory.CreateDirectory(packagesDir);
                    actualWorkingDirectory = packagesDir;

                    _logger.Debug($"Installing package '{packageName}' in clean directory: {actualWorkingDirectory}");
                }

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    actualWorkingDirectory ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode == 0)
                {
                    _logger.Debug($"Successfully installed package '{packageName}'");
                    return true;
                }

                _logger.Warn($"Failed to install package '{packageName}'. Exit code: {exitCode}, Error: {error}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Package installation failed for {packageName}");
                throw new NodeJsPackageException($"Failed to install package: {packageName}", ex);
            }
        }

        /// <summary>
        /// Installs packages in a clean isolated environment to avoid conflicts.
        /// Creates isolated environment in the specified install base path.
        /// </summary>
        public async Task<bool> InstallPackageIsolatedAsync(string packageName)
        {
            try
            {
                string isolatedDir = Path.Combine(_isolatedPackagesPath, "isolated_packages", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(isolatedDir);

                string packageJsonPath = Path.Combine(isolatedDir, "package.json");
                const string packageJsonContent = @"{
  ""name"": ""isolated-packages"",
  ""version"": ""1.0.0"",
  ""description"": ""Isolated package installation for Tubifarry"",
  ""private"": true
}";
                await File.WriteAllTextAsync(packageJsonPath, packageJsonContent);
                _logger.Debug($"Created isolated package.json at: {packageJsonPath}");

                string dockerFlags = Environment.UserName == "root" ? " --unsafe-perm" : "";
                string command = $"{_npmExecutablePath} install {packageName} --legacy-peer-deps{dockerFlags}";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    isolatedDir,
                    captureStdErr: true);

                bool success = exitCode == 0;
                if (!success)
                    _logger.Warn($"Failed to install package '{packageName}' in isolated environment. Exit code: {exitCode}, Error: {error}");
                try
                {
                    Directory.Delete(isolatedDir, recursive: true);
                    _logger.Debug($"Cleaned up isolated directory: {isolatedDir}");
                }
                catch (Exception cleanupEx)
                {
                    _logger.Warn($"Failed to cleanup isolated directory {isolatedDir}: {cleanupEx.Message}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Isolated package installation failed for {packageName}");
                throw new NodeJsPackageException($"Failed to install package in isolated environment: {packageName}", ex);
            }
        }

        /// <summary>
        /// Uninstalls npm packages.
        /// </summary>
        public async Task<bool> UninstallPackageAsync(string packageName, string? workingDirectory = null, bool global = false)
        {
            try
            {
                string globalFlag = global ? " -g" : "";
                string command = $"{_npmExecutablePath} uninstall {packageName}{globalFlag}";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    workingDirectory ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode == 0)
                {
                    _logger.Debug($"Successfully uninstalled package '{packageName}'");
                    return true;
                }

                _logger.Warn($"Failed to uninstall package '{packageName}'. Exit code: {exitCode}, Error: {error}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Package uninstallation failed for {packageName}");
                throw new NodeJsPackageException($"Failed to uninstall package: {packageName}", ex);
            }
        }

        /// <summary>
        /// Lists installed packages.
        /// </summary>
        public async Task<List<NodePackage>> GetInstalledPackagesAsync(string? workingDirectory = null, bool global = false)
        {
            try
            {
                string globalFlag = global ? " -g" : "";
                string command = $"{_npmExecutablePath} list{globalFlag} --json --depth=0";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    workingDirectory ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode != 0)
                {
                    _logger.Warn($"npm list command failed. Exit code: {exitCode}, Error: {error}");
                    return new List<NodePackage>();
                }

                using JsonDocument doc = JsonDocument.Parse(output);
                List<NodePackage> packages = new();

                if (doc.RootElement.TryGetProperty("dependencies", out JsonElement deps))
                {
                    foreach (JsonProperty prop in deps.EnumerateObject())
                    {
                        JsonElement packageInfo = prop.Value;
                        packages.Add(new NodePackage(
                            Name: prop.Name,
                            Version: packageInfo.TryGetProperty("version", out JsonElement ver) ? ver.GetString() ?? "unknown" : "unknown",
                            Description: packageInfo.TryGetProperty("description", out JsonElement desc) ? desc.GetString() : null,
                            Homepage: packageInfo.TryGetProperty("homepage", out JsonElement home) ? home.GetString() : null,
                            Keywords: null,
                            Dependencies: null
                        ));
                    }
                }

                return packages;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get installed packages");
                return new List<NodePackage>();
            }
        }

        /// <summary>
        /// Installs packages from package.json with Docker optimizations.
        /// </summary>
        public async Task<bool> InstallFromPackageJsonAsync(string? workingDirectory = null)
        {
            try
            {
                string dockerFlags = Environment.UserName == "root" ? " --unsafe-perm" : "";
                string command = $"{_npmExecutablePath} install{dockerFlags}";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    workingDirectory ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                if (exitCode == 0)
                {
                    _logger.Trace("Successfully installed packages from package.json");
                    return true;
                }

                _logger.Warn($"Failed to install from package.json. Exit code: {exitCode}, Error: {error}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to install from package.json");
                throw new NodeJsPackageException("Failed to install from package.json", ex);
            }
        }

        /// <summary>
        /// Executes Node.js code with proper escaping for all platforms.
        /// </summary>
        public async Task<NodeExecutionResult> ExecuteNodeAsync(string code, string? workingDirectory = null)
        {
            try
            {
                string escapedCode = code
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r\n", "\\n")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");

                string command = $"{_nodeExecutablePath} -e \"{escapedCode}\"";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    workingDirectory ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                return new NodeExecutionResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    StandardOutput = output,
                    StandardError = error
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing Node.js code");
                return new NodeExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardError = ex.Message
                };
            }
        }

        /// <summary>
        /// Executes a Node.js script file.
        /// </summary>
        public async Task<NodeExecutionResult> ExecuteScriptAsync(string scriptPath, string? arguments = null, string? workingDirectory = null)
        {
            try
            {
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException($"Script file not found: {scriptPath}");

                string args = string.IsNullOrEmpty(arguments) ? "" : $" {arguments}";
                string command = $"{_nodeExecutablePath} \"{scriptPath}\"{args}";

                (int exitCode, string output, string error) = await CrossPlatformProcessRunner.ExecuteShellCommand(
                    command,
                    workingDirectory ?? Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
                    captureStdErr: true);

                return new NodeExecutionResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    StandardOutput = output,
                    StandardError = error
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error executing Node.js script: {scriptPath}");
                return new NodeExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardError = ex.Message
                };
            }
        }

        /// <summary>
        /// Invalidates the Node.js information cache.
        /// </summary>
        public void InvalidateCache()
        {
            _nodeInfoCache.Clear();
            _logger.Trace("Node.js info cache invalidated");
        }
    }
}