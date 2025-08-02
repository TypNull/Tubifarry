using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace Tubifarry.Core.NodeBridge
{
    /// <summary>
    /// Main Node.js bridge implementation providing complete Node.js environment management.
    /// Coordinates installation, initialization, and execution of Node.js code and packages.
    /// </summary>
    public class NodeJSBridge : INodeJsBridge
    {
        private readonly Logger _logger;
        private readonly IHttpClient _httpClient;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly string _installBasePath = string.Empty;
        private NodeJSClient? _nodeClient;
        private bool _isDisposed;

        public bool IsInitialized { get; private set; }
        public string NodeVersion { get; private set; } = "Unknown";
        public string NpmVersion { get; private set; } = "Unknown";
        public NodeInstallInfo? InstallInfo { get; private set; }

        /// <summary>
        /// Initializes a new instance of the NodeJsBridge class.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostic information</param>
        public NodeJSBridge(IAppFolderInfo appFolderInfo, IHttpClient httpClient, Logger logger)
        {
            _logger = logger;
            _httpClient = httpClient;
            _installBasePath = Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name, "NodeJS");
        }

        /// <summary>
        /// Initializes the Node.js bridge with comprehensive environment setup.
        /// Handles installation, verification, and package management.
        /// </summary>
        /// <param name="requiredPackages">npm packages to install during initialization</param>
        /// <returns>True if initialization succeeds</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the bridge has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown when SetPath has not been called</exception>
        public async Task<bool> InitializeAsync(params string[] requiredPackages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NodeJSBridge));

            if (string.IsNullOrWhiteSpace(_installBasePath))
                throw new InvalidOperationException("SetPath must be called before InitializeAsync");

            if (IsInitialized)
                return true;

            await _initLock.WaitAsync();

            try
            {
                _logger.Trace("Initializing Node.js bridge");
                string? installedNodePath = await NodeJSInstaller.FindOrInstallNodeAsync(_httpClient, _installBasePath);
                if (installedNodePath == null)
                {
                    _logger.Error("Failed to find or install Node.js");
                    return false;
                }

                _logger.Trace($"Using Node.js installation at: {installedNodePath}");
                _nodeClient = new NodeJSClient(installedNodePath, _installBasePath);
                try
                {
                    InstallInfo = await _nodeClient.GetNodeInfoAsync();
                    NodeVersion = InstallInfo.Version;
                    NpmVersion = InstallInfo.NpmVersion;

                    _logger.Info($"Node.js version: {NodeVersion}");
                    _logger.Trace($"npm version: {NpmVersion}");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get Node.js installation information");
                    return false;
                }

                if (requiredPackages.Length > 0)
                    await InstallRequiredPackagesAsync(requiredPackages);

                IsInitialized = true;
                _logger.Info("Node.js bridge initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error initializing Node.js bridge");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Installs required packages with multiple installation strategies.
        /// </summary>
        private async Task InstallRequiredPackagesAsync(string[] requiredPackages)
        {
            _logger.Debug($"Installing {requiredPackages.Length} required packages...");

            List<string> failedPackages = new();

            foreach (string package in requiredPackages)
            {
                bool success = await TryInstallPackageWithFallback(package);
                if (!success)
                {
                    failedPackages.Add(package);
                }
            }

            if (failedPackages.Count > 0)
            {
                _logger.Warn($"Failed to install some required packages: {string.Join(", ", failedPackages)}");
            }
            else
            {
                _logger.Debug($"Successfully installed all required packages: {string.Join(", ", requiredPackages)}");
            }
        }

        /// <summary>
        /// Tries to install a package using multiple strategies (isolated, then global).
        /// </summary>
        private async Task<bool> TryInstallPackageWithFallback(string packageName)
        {
            if (_nodeClient == null)
                return false;
            bool success = await _nodeClient.InstallPackageIsolatedAsync(packageName);
            if (success)
                return true;
            success = await _nodeClient.InstallPackageAsync(packageName);
            if (success)
                return true;

            _logger.Warn($"All installation methods failed for package: {packageName}");
            return false;
        }

        /// <summary>
        /// Executes Node.js code asynchronously.
        /// </summary>
        /// <param name="code">The Node.js code to execute</param>
        /// <returns>Execution result with output and status</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the bridge has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown when the bridge is not initialized</exception>
        /// <exception cref="ArgumentException">Thrown when code is null or empty</exception>
        public async Task<NodeExecutionResult> ExecuteCodeAsync(string code)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NodeJSBridge));

            if (!IsInitialized || _nodeClient == null)
                throw new InvalidOperationException("Node.js bridge is not initialized. Call InitializeAsync first.");

            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Code cannot be empty.", nameof(code));

            return await _nodeClient.ExecuteNodeAsync(code);
        }

        /// <summary>
        /// Executes a Node.js script file asynchronously.
        /// </summary>
        /// <param name="scriptPath">Path to the script file</param>
        /// <param name="arguments">Optional command line arguments</param>
        /// <returns>Execution result with output and status</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the bridge has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown when the bridge is not initialized</exception>
        /// <exception cref="ArgumentException">Thrown when scriptPath is null or empty</exception>
        public async Task<NodeExecutionResult> ExecuteScriptAsync(string scriptPath, string? arguments = null)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NodeJSBridge));

            if (!IsInitialized || _nodeClient == null)
                throw new InvalidOperationException("Node.js bridge is not initialized. Call InitializeAsync first.");

            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentException("Script path cannot be empty.", nameof(scriptPath));

            return await _nodeClient.ExecuteScriptAsync(scriptPath, arguments);
        }

        /// <summary>
        /// Installs npm packages asynchronously.
        /// </summary>
        /// <param name="packages">Package names to install</param>
        /// <returns>Installation result</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the bridge has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown when the bridge is not initialized</exception>
        public async Task<NodeExecutionResult> InstallPackagesAsync(params string[] packages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NodeJSBridge));

            if (!IsInitialized || _nodeClient == null)
                throw new InvalidOperationException("Node.js bridge is not initialized. Call InitializeAsync first.");

            if (packages == null || packages.Length == 0)
                return new NodeExecutionResult { Success = true };

            try
            {
                _logger.Trace($"Installing npm packages: {string.Join(", ", packages)}");

                List<string> failedPackages = new();
                List<string> successfulPackages = new();

                foreach (string package in packages)
                {
                    bool success = await _nodeClient.InstallPackageAsync(package);
                    if (success)
                        successfulPackages.Add(package);
                    else
                        failedPackages.Add(package);
                }

                if (failedPackages.Count > 0)
                {
                    return new NodeExecutionResult
                    {
                        Success = false,
                        StandardError = $"Failed to install packages: {string.Join(", ", failedPackages)}",
                        StandardOutput = successfulPackages.Count > 0
                            ? $"Successfully installed: {string.Join(", ", successfulPackages)}"
                            : string.Empty,
                        ExitCode = 1
                    };
                }

                return new NodeExecutionResult
                {
                    Success = true,
                    StandardOutput = $"Successfully installed packages: {string.Join(", ", packages)}",
                    ExitCode = 0
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error installing npm packages");
                return new NodeExecutionResult
                {
                    Success = false,
                    StandardError = ex.Message,
                    ExitCode = 1
                };
            }
        }

        /// <summary>
        /// Disposes the Node.js bridge and releases all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected disposal method for derived classes.
        /// </summary>
        /// <param name="disposing">True if disposing managed resources</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _logger.Trace("Disposing Node.js bridge");
                _initLock.Dispose();
            }

            _isDisposed = true;
        }
    }
}