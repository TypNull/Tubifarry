using NLog;
using Python.Runtime;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Manages a Python environment using the Python.NET library for direct in-process Python integration.
    /// </summary>
    public class PythonNetEnv : IAsyncDisposable
    {
        /// <summary>
        /// Gets a value indicating whether the Python.NET environment is initialized.
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Gets the path to the Python installation directory.
        /// </summary>
        public string PythonInstallPath { get; }

        /// <summary>
        /// Gets the Python version being used.
        /// </summary>
        public string PythonVersion { get; }

        private string? _originalPath;
        private string? _originalPythonhome;
        private static readonly char[] _separator = new char[] { ' ' };
        private readonly Lazy<PyModule> _scope;
        private readonly Logger _logger;
        private readonly OSPlatform _platform;
        private bool _isDisposed;
        private bool _loggersSetup;

        /// <summary>
        /// Gets the logger for standard output.
        /// </summary>
        public PythonLogger OutLogger { get; }

        /// <summary>
        /// Gets the logger for error output.
        /// </summary>
        public PythonLogger ErrLogger { get; }

        /// <summary>
        /// Initializes a new instance of the PythonNetEnv class.
        /// </summary>
        /// <param name="environmentPath">Full path to the Python environment</param>
        /// <param name="pythonVersion">Python version installed in this environment</param>
        /// <param name="logger">Optional logger for diagnostic information</param>
        public PythonNetEnv(string environmentPath, string pythonVersion, Logger? logger = null)
        {
            if (string.IsNullOrEmpty(environmentPath))
                throw new ArgumentNullException(nameof(environmentPath));

            if (string.IsNullOrEmpty(pythonVersion))
                throw new ArgumentNullException(nameof(pythonVersion));

            if (!Directory.Exists(environmentPath))
                throw new DirectoryNotFoundException($"Python environment not found at: {environmentPath}");

            PythonInstallPath = environmentPath;
            PythonVersion = pythonVersion;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
            _platform = PlatformUtils.GetCurrentPlatform();

            OutLogger = new PythonLogger();
            ErrLogger = new PythonLogger();
            _loggersSetup = false;

            _scope = new Lazy<PyModule>(CreatePythonScope);

            ConfigurePythonEnvironment();

            IsInitialized = true;
            _logger.Debug($"PythonNetEnv initialized for Python {PythonVersion} at {PythonInstallPath}");
        }

        /// <summary>
        /// Configures the Python environment by setting up necessary paths and environment variables.
        /// </summary>
        private void ConfigurePythonEnvironment()
        {
            try
            {
                _logger.Trace("Configuring Python.NET environment");

                string[] pathsToAdd = _platform switch
                {
                    OSPlatform.Windows => new[]
                    {
                        PythonInstallPath,
                        Path.Combine(PythonInstallPath, "Scripts"),
                        Path.Combine(PythonInstallPath, "Library"),
                        Path.Combine(PythonInstallPath, "bin"),
                        Path.Combine(PythonInstallPath, "Library", "bin"),
                        Path.Combine(PythonInstallPath, "Library", "mingw-w64", "bin")
                    },
                    OSPlatform.MacOS => new[]
                    {
                        PythonInstallPath,
                        Path.Combine(PythonInstallPath, "bin"),
                        Path.Combine(PythonInstallPath, "lib")
                    },
                    _ => new[] // Linux
                    {
                        PythonInstallPath,
                        Path.Combine(PythonInstallPath, "bin"),
                        Path.Combine(PythonInstallPath, "lib")
                    }
                };

                _originalPath = Environment.GetEnvironmentVariable("PATH");
                _originalPythonhome = Environment.GetEnvironmentVariable("PYTHONHOME");

                char pathSeparator = PlatformUtils.GetPathSeparator();
                string pathsString = string.Join(pathSeparator, pathsToAdd);
                string newPath = string.IsNullOrEmpty(_originalPath)
                    ? pathsString
                    : $"{pathsString}{pathSeparator}{_originalPath}";

                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("PYTHONHOME", PythonInstallPath, EnvironmentVariableTarget.Process);

                string[] versionParts = PythonVersion.Split('.');
                if (versionParts.Length < 2)
                    throw new PythonNetConfigurationException($"Invalid Python version format: {PythonVersion}. Expected format: major.minor.patch (e.g., 3.8.20)");

                string majorVersion = versionParts[0];
                string minorVersion = versionParts[1];

                // Platform-specific Python DLL path
                string pythonDllPath = _platform switch
                {
                    OSPlatform.Windows => Path.Combine(PythonInstallPath, $"python{majorVersion}{minorVersion}.dll"),
                    OSPlatform.MacOS => GetMacOSPythonDllPath(majorVersion, minorVersion),
                    _ => GetLinuxPythonDllPath(majorVersion, minorVersion)
                };

                if (!File.Exists(pythonDllPath))
                    throw new PythonNetConfigurationException($"Python shared library not found at {pythonDllPath}. Please ensure Python {PythonVersion} is correctly installed.");

                Runtime.PythonDLL = pythonDllPath;
                PythonEngine.PythonHome = PythonInstallPath;

                _logger.Trace("Python.NET environment configured successfully");
            }
            catch (Exception ex) when (ex is not PythonNetConfigurationException)
            {
                _logger.Error(ex, "Failed to configure Python.NET environment");
                throw new PythonNetConfigurationException("Failed to configure Python.NET environment", ex);
            }
        }

        private string GetMacOSPythonDllPath(string majorVersion, string minorVersion)
        {
            string path = Path.Combine(PythonInstallPath, "lib", $"libpython{majorVersion}.{minorVersion}.dylib");
            if (!File.Exists(path))
                path = Path.Combine(PythonInstallPath, "lib", $"libpython{majorVersion}{minorVersion}.dylib");
            return path;
        }

        private string GetLinuxPythonDllPath(string majorVersion, string minorVersion)
        {
            string path = Path.Combine(PythonInstallPath, "lib", $"libpython{majorVersion}.{minorVersion}.so");
            if (!File.Exists(path))
                path = Path.Combine(PythonInstallPath, "lib", $"libpython{majorVersion}.{minorVersion}.so.1");
            if (!File.Exists(path))
                path = Path.Combine(PythonInstallPath, "lib", $"libpython{majorVersion}{minorVersion}.so");
            return path;
        }

        /// <summary>
        /// Creates a new Python scope (namespace) for executing Python code.
        /// </summary>
        private PyModule CreatePythonScope()
        {
            try
            {
                _logger.Trace("Creating Python scope");

                if (!PythonEngine.IsInitialized)
                {
                    PythonEngine.Initialize();
                    _logger.Debug("Python engine initialized");
                }

                PyModule scope = Py.CreateScope();
                _logger.Trace("Python scope created successfully");
                return scope;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create Python scope");
                throw new PythonNetExecutionException("Failed to create Python scope", ex);
            }
        }

        /// <summary>
        /// Sets up Python's sys.stdout and sys.stderr to redirect to the logger objects.
        /// </summary>
        private void SetupLogger()
        {
            if (_loggersSetup)
                return;

            try
            {
                _logger.Trace("Setting up Python output loggers");

                const string loggerSrc =
                    "import sys\n" +
                    "from io import StringIO\n" +
                    "sys.stdout = Logger\n" +
                    "sys.stdout.flush()\n" +
                    "sys.stderr = ErrLogger\n" +
                    "sys.stderr.flush()\n";

                using (Py.GIL())
                {
                    _scope.Value.Set("Logger", OutLogger.ToPython());
                    _scope.Value.Set("ErrLogger", ErrLogger.ToPython());
                    PyObject pyCompile = PythonEngine.Compile(loggerSrc);
                    _scope.Value.Execute(pyCompile);
                }

                _logger.Trace("Python output loggers set up successfully");
                _loggersSetup = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to set up Python output loggers");
                throw new PythonNetExecutionException("Failed to set up Python output loggers", ex);
            }
        }

        /// <summary>
        /// Runs a Python script with the specified arguments.
        /// </summary>
        /// <param name="scriptPath">Path to the Python script</param>
        /// <param name="workingDirectory">Working directory for the script</param>
        /// <param name="arguments">Arguments to pass to the script</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task RunPythonScriptAsync(
            string scriptPath,
            string workingDirectory,
            string arguments = "",
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsInitialized)
                throw new InvalidOperationException("Python environment is not initialized");

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script file not found: {scriptPath}", scriptPath);

            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");

            _logger.Info($"Running Python script: {scriptPath} with arguments: {arguments} in directory: {workingDirectory}");

            await Task.Run(() =>
            {
                string currentDirectory = Environment.CurrentDirectory;
                try
                {
                    if (!PythonEngine.IsInitialized)
                    {
                        PythonEngine.Initialize();
                        _logger.Debug("Python engine initialized");
                    }

                    Environment.CurrentDirectory = workingDirectory;
                    _logger.Trace($"Working directory set to: {workingDirectory}");

                    SetupLogger();

                    using (Py.GIL())
                    {
                        ConfigurePythonArguments(scriptPath, arguments);

                        string moduleName = Path.GetFileNameWithoutExtension(scriptPath);
                        _logger.Debug($"Executing script as module: {moduleName}");

                        _scope.Value.Set("__name__", "__main__");

                        string scriptContent = File.ReadAllText(scriptPath);
                        _scope.Value.Exec(scriptContent);

                        _logger.Info("Python script execution completed successfully");
                    }
                }
                catch (PythonException ex)
                {
                    _logger.Error(ex, $"Python error executing script: {scriptPath}");
                    throw new PythonNetExecutionException($"Python error: {ex.Message}", ex);
                }
                catch (Exception ex) when (ex is not FileNotFoundException &&
                                          ex is not DirectoryNotFoundException &&
                                          ex is not PythonNetExecutionException)
                {
                    _logger.Error(ex, $"Error executing Python script: {scriptPath}");
                    throw new PythonNetExecutionException($"Error executing Python script: {scriptPath}", ex);
                }
                finally
                {
                    Environment.CurrentDirectory = currentDirectory;
                    _logger.Trace($"Working directory restored to: {currentDirectory}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Executes Python code directly.
        /// </summary>
        /// <param name="pythonCode">Python code to execute</param>
        /// <param name="workingDirectory">Working directory for execution</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ExecutePythonCodeAsync(
            string pythonCode,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsInitialized)
                throw new InvalidOperationException("Python environment is not initialized");

            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");

            _logger.Info("Executing Python code in directory: {0}", workingDirectory);

            await Task.Run(() =>
            {
                string currentDirectory = Environment.CurrentDirectory;
                try
                {
                    if (!PythonEngine.IsInitialized)
                    {
                        PythonEngine.Initialize();
                        _logger.Debug("Python engine initialized");
                    }

                    Environment.CurrentDirectory = workingDirectory;
                    _logger.Trace($"Working directory set to: {workingDirectory}");

                    SetupLogger();

                    using (Py.GIL())
                    {
                        _scope.Value.Exec(pythonCode);
                        _logger.Info("Python code execution completed successfully");
                    }
                }
                catch (PythonException ex)
                {
                    _logger.Error(ex, "Python error executing code");
                    throw new PythonNetExecutionException($"Python error: {ex.Message}", ex);
                }
                catch (Exception ex) when (ex is not DirectoryNotFoundException &&
                                          ex is not PythonNetExecutionException)
                {
                    _logger.Error(ex, "Error executing Python code");
                    throw new PythonNetExecutionException("Error executing Python code", ex);
                }
                finally
                {
                    Environment.CurrentDirectory = currentDirectory;
                    _logger.Debug($"Working directory restored to: {currentDirectory}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Configures sys.argv and sys.path for the Python script execution.
        /// </summary>
        private void ConfigurePythonArguments(string scriptPath, string arguments)
        {
            try
            {
                _logger.Debug($"Configuring Python arguments: {scriptPath} {arguments}");

                dynamic sys = Py.Import("sys");

                string[] argsArray = arguments.Split(_separator, StringSplitOptions.RemoveEmptyEntries);

                PyObject[] pyArgs = new PyObject[] { new PyString(scriptPath) }
                    .Concat(argsArray.Select(arg => new PyString(arg)))
                    .ToArray();

                using PyList pyList = new(pyArgs);

                sys.argv = pyList;
                sys.path.append(Environment.CurrentDirectory);

                _logger.Trace("Python arguments configured successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to configure Python arguments");
                throw new PythonNetExecutionException("Failed to configure Python arguments", ex);
            }
        }

        /// <summary>
        /// Asynchronously disposes of the Python.NET environment, releasing all resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Core implementation of the asynchronous dispose pattern.
        /// </summary>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_isDisposed)
                return;

            _logger.Trace("Disposing PythonNetEnv");

            _isDisposed = true;

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        if (_scope.IsValueCreated)
                        {
                            _logger.Trace("Disposing Python scope");
                            _scope.Value.Dispose();
                        }

                        if (PythonEngine.IsInitialized)
                        {
                            _logger.Trace("Shutting down Python engine");
                            PythonEngine.Shutdown();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Error during Python.NET cleanup");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error executing Python thread cleanup task");
            }

            try
            {
                _logger.Trace("Disposing Python loggers");
                OutLogger.Dispose();
                ErrLogger.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error disposing Python loggers");
            }

            try
            {
                _logger.Trace("Restoring environment variables");
                Environment.SetEnvironmentVariable("PATH", _originalPath, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("PYTHONHOME", _originalPythonhome, EnvironmentVariableTarget.Process);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error restoring environment variables");
            }

            _logger.Trace("PythonNetEnv disposed successfully");
        }

        /// <summary>
        /// Throws an ObjectDisposedException if this instance has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().Name, "The Python.NET environment has been disposed");
        }
    }
}