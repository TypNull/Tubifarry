using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using Python.Included;
using Python.Runtime;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Service providing a simplified interface for Python integration in Tubifarry using Python.Included.
    /// </summary>
    public class PythonBridgeService : IDisposable, IPythonBridge
    {
        private readonly Logger _logger;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _isDisposed;

        public PythonConsoleLogger OutLogger { get; }
        public PythonConsoleLogger ErrLogger { get; }

        /// <summary>
        /// Gets a value indicating whether Python is available.
        /// </summary>
        public bool IsPythonAvailable => IsInitialized;

        /// <summary>
        /// Gets a value indicating whether the Python bridge is initialized.
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Gets the Python version if available.
        /// </summary>
        public string PythonVersion { get; private set; } = "Unknown";

        /// <summary>
        /// Initializes a new instance of the <see cref="PythonBridgeService"/> class.
        /// </summary>
        /// <param name="appFolderInfo">The application folder info.</param>
        /// <param name="logger">The logger.</param>
        public PythonBridgeService(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _logger = logger ?? LogManager.GetCurrentClassLogger();
            Installer.InstallPath = Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name);
            Installer.InstallDirectory = "Python";

            OutLogger = new PythonConsoleLogger(false, _logger);
            ErrLogger = new PythonConsoleLogger(true, _logger);
        }

        /// <summary>
        /// Initializes the Python bridge.
        /// </summary>
        /// <param name="requiredPackages">Optional packages to install.</param>
        /// <returns>A Task representing the asynchronous operation, containing a value indicating whether Python was successfully initialized.</returns>
        public async Task<bool> InitializeAsync(params string[] requiredPackages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PythonBridgeService));

            if (IsInitialized)
                return true;

            await _initLock.WaitAsync();

            try
            {
                _logger.Debug("Initializing Python bridge");
                await Installer.SetupPython();

                if (!Installer.IsPythonInstalled())
                {
                    _logger.Warn("Failed to install Python");
                    return false;
                }

                if (requiredPackages.Length > 0)
                {
                    bool pipInstalled = await Installer.TryInstallPip();
                    if (!pipInstalled)
                    {
                        _logger.Warn("Failed to install pip");
                        return false;
                    }

                    foreach (string package in requiredPackages)
                    {
                        _logger.Trace($"Installing Python package: {package}");
                        await Installer.PipInstallModule(package);
                    }
                }

                try
                {
                    if (!PythonEngine.IsInitialized)
                    {
                        PythonEngine.Initialize();
                        SetupOutputRedirection();
                    }

                    using (Py.GIL())
                    {
                        dynamic sys = Py.Import("sys");
                        PythonVersion = sys.version.ToString();
                    }

                    IsInitialized = true;
                    _logger.Debug($"Bridge initialized successfully with Python {PythonVersion}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to initialize Python runtime");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error initializing Python bridge");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Sets up stdout and stderr redirection.
        /// </summary>
        private void SetupOutputRedirection()
        {
            try
            {
                using (Py.GIL())
                {
                    dynamic sys = Py.Import("sys");
                    using (PyModule scope = Py.CreateScope())
                    {
                        scope.Set("stdout_logger", OutLogger.ToPython());
                        scope.Set("stderr_logger", ErrLogger.ToPython());

                        scope.Exec(@"import sys
sys.stdout = stdout_logger
sys.stderr = stderr_logger
sys.stdout.flush()
sys.stderr.flush()");
                    }

                    _logger.Trace("Python stdout/stderr redirection set up");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to set up Python stdout/stderr redirection");
            }
        }

        /// <summary>
        /// Executes Python code.
        /// </summary>
        /// <param name="code">The Python code to execute.</param>
        /// <returns>The execution result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when Python is not initialized.</exception>
        public PythonExecutionResult ExecuteCode(string code)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PythonBridgeService));

            if (!IsInitialized)
                throw new InvalidOperationException("Python bridge is not initialized. Call InitializeAsync first.");

            try
            {
                OutLogger.Clear();
                ErrLogger.Clear();

                string modifiedCode = "print('=== Python execution started ===')\n" + code;
                try
                {
                    using (Py.GIL())
                    {
                        PythonEngine.Exec(modifiedCode);

                        _logger.Trace($"Python stdout: {OutLogger.Content}");
                        _logger.Trace($"Python stderr: {ErrLogger.Content}");

                        return new PythonExecutionResult
                        {
                            Success = true,
                            StandardOutput = OutLogger.Content,
                            StandardError = ErrLogger.Content,
                            ExitCode = 0
                        };
                    }
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
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing Python code");
                return new PythonExecutionResult
                {
                    Success = false,
                    StandardError = ex.Message,
                    ExitCode = 1
                };
            }
        }

        /// <summary>
        /// Installs Python packages.
        /// </summary>
        /// <param name="packages">The packages to install.</param>
        /// <returns>The installation result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when Python is not initialized.</exception>
        public async Task<PythonExecutionResult> InstallPackagesAsync(params string[] packages)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PythonBridgeService));

            if (!IsInitialized)
                throw new InvalidOperationException("Python bridge is not initialized. Call InitializeAsync first.");

            if (packages == null || packages.Length == 0)
                return new PythonExecutionResult { Success = true };

            try
            {
                _logger.Trace($"Installing Python packages: {string.Join(", ", packages)}");

                OutLogger.Clear();
                ErrLogger.Clear();

                foreach (string package in packages)
                    await Installer.PipInstallModule(package);

                return new PythonExecutionResult
                {
                    Success = true,
                    StandardOutput = OutLogger.Content,
                    StandardError = ErrLogger.Content,
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
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _logger.Debug("Disposing Python bridge service");
                OutLogger?.Dispose();
                ErrLogger?.Dispose();
                _initLock?.Dispose();

                if (IsInitialized && PythonEngine.IsInitialized)
                {
                    try
                    {
                        PythonEngine.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Error shutting down Python engine");
                    }
                }
            }

            _isDisposed = true;
        }
    }
}