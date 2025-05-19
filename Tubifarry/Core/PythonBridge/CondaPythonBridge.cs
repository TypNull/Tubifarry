using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using Python.Runtime;
using Tubifarry.Core.PythonBridge.Conda;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Implements IPythonBridge using Conda for environment management and Python.NET for execution.
    /// </summary>
    public class CondaPythonBridge : IPythonBridge
    {
        private readonly Logger _logger;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly string _condaBasePath;
        private readonly string _environmentName;
        private CondaClient? _condaClient;
        private PythonNetEnv? _pythonNetEnv;
        private bool _isDisposed;

        /// <summary>
        /// Gets whether the Python bridge is initialized.
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Gets the Python version.
        /// </summary>
        public string PythonVersion { get; private set; } = "Unknown";

        /// <summary>
        /// Gets the logger for standard output.
        /// </summary>
        public PythonLogger OutLogger { get; }

        /// <summary>
        /// Gets the logger for error output.
        /// </summary>
        public PythonLogger ErrLogger { get; }

        /// <summary>
        /// Initializes a new instance of the CondaPythonBridge class.
        /// </summary>
        /// <param name="appFolderInfo">The application folder info.</param>
        /// <param name="logger">Optional logger for diagnostic information</param>
        public CondaPythonBridge(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _condaBasePath = Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name);
            _environmentName = "Test";
            _logger = logger ?? LogManager.GetCurrentClassLogger();
            OutLogger = new PythonLogger(_logger);
            ErrLogger = new PythonLogger(_logger);
        }

        /// <summary>
        /// Initializes the Python bridge.
        /// </summary>
        /// <param name="requiredPackages">Optional packages to install</param>
        /// <returns>True if initialization was successful, otherwise false</returns>
        public async Task<bool> InitializeAsync(params string[] requiredPackages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CondaPythonBridge));

            if (IsInitialized)
                return true;

            await _initLock.WaitAsync();

            try
            {
                _logger.Info("Initializing Conda Python bridge");

                // Step 1: Download and install Miniconda if not already installed
                string? installedCondaPath = await CondaInstaller.FindOrInstallCondaAsync(_condaBasePath);

                if (installedCondaPath == null)
                {
                    _logger.Error("Failed to find or install Conda");
                    return false;
                }

                _logger.Info($"Using Conda installation at: {installedCondaPath}");

                // Step 2: Create a CondaClient to manage environments
                string condaExecutablePath = PlatformUtils.GetCondaExecutablePath(installedCondaPath);

                if (!File.Exists(condaExecutablePath))
                {
                    _logger.Error($"Conda executable not found at {condaExecutablePath}");
                    return false;
                }

                _condaClient = new CondaClient(condaExecutablePath, _logger);

                // Step 3: Get Conda system information
                try
                {
                    CondaInfo condaInfo = await _condaClient.GetCondaInfoAsync();
                    _logger.Debug($"Conda version: {condaInfo.CondaVersion}");
                    _logger.Info($"Python version: {condaInfo.PythonVersion}");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get Conda info");
                    return false;
                }

                // Step 4: Check if environment exists or create a new one
                bool envExists = await _condaClient.EnvironmentExistsAsync(_environmentName);
                if (!envExists)
                {
                    _logger.Debug($"Creating environment '{_environmentName}' with Python 3.10...");
                    bool created = await _condaClient.CreateEnvironmentAsync(_environmentName, "3.10");

                    if (!created)
                    {
                        _logger.Error($"Failed to create environment: {_environmentName}");
                        return false;
                    }

                    _logger.Info($"Environment '{_environmentName}' created successfully");
                }
                else
                {
                    _logger.Debug($"Using existing environment: {_environmentName}");
                }

                // Step 5: Add conda-forge as channel
                await _condaClient.AddCondaForgeChannelAsync();

                // Step 6: Install required packages
                if (requiredPackages.Length > 0)
                {
                    _logger.Trace($"Installing {requiredPackages.Length} required packages...");
                    foreach (string package in requiredPackages)
                    {
                        bool success = await _condaClient.InstallPackageAsync(package, _environmentName);
                        if (!success)
                        {
                            _logger.Warn($"Failed to install package: {package}");
                        }
                    }
                }

                // Step 7: Get environment path and initialize PythonNetEnv
                string? envPath = await _condaClient.GetEnvironmentPathAsync(_environmentName);
                if (envPath == null)
                {
                    _logger.Error($"Could not find path for environment {_environmentName}");
                    return false;
                }

                string pythonVersion = await _condaClient.GetPythonVersionAsync(_environmentName);
                PythonVersion = pythonVersion;
                _logger.Trace($"Using Python version: {pythonVersion}");

                // Step 8: Initialize PythonNetEnv
                try
                {
                    _pythonNetEnv = new PythonNetEnv(envPath, pythonVersion, _logger);
                    OutLogger.OnOutputWritten += (sender, content) => _pythonNetEnv.OutLogger.write(content);
                    ErrLogger.OnOutputWritten += (sender, content) => _pythonNetEnv.ErrLogger.write(content);

                    _logger.Debug("Python.NET environment initialized successfully");
                    IsInitialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to initialize Python.NET environment");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error initializing Conda Python bridge");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Executes Python code.
        /// </summary>
        /// <param name="code">The Python code to execute</param>
        /// <returns>The execution result</returns>
        public PythonExecutionResult ExecuteCode(string code)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CondaPythonBridge));

            if (!IsInitialized || _pythonNetEnv == null)
                throw new InvalidOperationException("Python bridge is not initialized. Call InitializeAsync first.");

            try
            {
                OutLogger.Clear();
                ErrLogger.Clear();
                string modifiedCode = "print('=== Python execution started ===')\n" + code;
                _pythonNetEnv.ExecutePythonCodeAsync(modifiedCode, Directory.GetCurrentDirectory()).GetAwaiter().GetResult();

                _logger.Debug($"Python stdout: {OutLogger.Content}");
                _logger.Debug($"Python stderr: {ErrLogger.Content}");

                return new PythonExecutionResult
                {
                    Success = true,
                    StandardOutput = OutLogger.Content,
                    StandardError = ErrLogger.Content,
                    ExitCode = 0
                };
            }
            catch (PythonException pyEx)
            {
                _logger.Error(pyEx, "Python execution error");
                return new PythonExecutionResult
                {
                    Success = false,
                    StandardOutput = OutLogger.Content,
                    StandardError = $"{ErrLogger.Content}{Environment.NewLine}{pyEx.Message}",
                    ExitCode = 1
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing Python code");
                return new PythonExecutionResult
                {
                    Success = false,
                    StandardOutput = OutLogger.Content,
                    StandardError = $"{ErrLogger.Content}{Environment.NewLine}{ex.Message}",
                    ExitCode = 1
                };
            }
        }

        /// <summary>
        /// Installs Python packages using Conda.
        /// </summary>
        /// <param name="packages">The packages to install</param>
        /// <returns>The installation result</returns>
        public async Task<PythonExecutionResult> InstallPackagesAsync(params string[] packages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CondaPythonBridge));

            if (!IsInitialized || _condaClient == null)
                throw new InvalidOperationException("Python bridge is not initialized. Call InitializeAsync first.");

            if (packages == null || packages.Length == 0)
                return new PythonExecutionResult { Success = true };

            try
            {
                _logger.Info($"Installing Python packages: {string.Join(", ", packages)}");

                OutLogger.Clear();
                ErrLogger.Clear();

                foreach (string package in packages)
                {
                    bool success = await _condaClient.InstallPackageAsync(package, _environmentName);
                    if (!success)
                    {
                        return new PythonExecutionResult
                        {
                            Success = false,
                            StandardError = $"Failed to install package: {package}",
                            ExitCode = 1
                        };
                    }
                }

                return new PythonExecutionResult
                {
                    Success = true,
                    StandardOutput = $"Successfully installed packages: {string.Join(", ", packages)}",
                    ExitCode = 0
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error installing Python packages");
                return new PythonExecutionResult
                {
                    Success = false,
                    StandardError = ex.Message,
                    ExitCode = 1
                };
            }
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _logger.Info("Disposing Conda Python bridge");
                if (_pythonNetEnv != null)
                {
                    try
                    {
                        _pythonNetEnv.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Error disposing Python.NET environment");
                    }
                }
                OutLogger.Dispose();
                ErrLogger.Dispose();
                _initLock.Dispose();
            }

            _isDisposed = true;
        }
    }
}