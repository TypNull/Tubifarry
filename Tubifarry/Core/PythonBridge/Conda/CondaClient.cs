using NLog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Tubifarry.Core.PythonBridge.Conda
{
    /// <summary>
    /// Provides robust management and interaction with Conda environments using JSON-based APIs.
    /// </summary>
    public class CondaClient
    {
        private const int CacheExpirationMinutes = 5;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes);

        private readonly string _condaExecutablePath;
        private readonly Logger _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        // Concurrent cache to store and manage Conda information
        private static readonly ConcurrentDictionary<string, CachedCondaInfo> _condaInfoCache = new();

        /// <summary>
        /// Represents a cached Conda information entry.
        /// </summary>
        private record CachedCondaInfo(CondaInfo Info, DateTime CacheTimestamp);

        /// <summary>
        /// Initializes a new instance of the CondaClient.
        /// </summary>
        /// <param name="condaExecutablePath">Full path to the Conda executable</param>
        /// <param name="logger">Optional logger for diagnostic information</param>
        public CondaClient(string condaExecutablePath, Logger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(condaExecutablePath))
                throw new ArgumentException("Conda executable path cannot be empty.", nameof(condaExecutablePath));

            if (!File.Exists(condaExecutablePath))
                throw new ArgumentException("Conda executable does not exist.", nameof(condaExecutablePath));

            _condaExecutablePath = condaExecutablePath;
            _logger = logger ?? LogManager.GetCurrentClassLogger();

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            };
        }

        /// <summary>
        /// Gets Conda system information with intelligent caching.
        /// </summary>
        public async Task<CondaInfo> GetCondaInfoAsync()
        {
            string cacheKey = _condaExecutablePath;

            if (_condaInfoCache.TryGetValue(cacheKey, out CachedCondaInfo? cachedInfo) && DateTime.Now - cachedInfo.CacheTimestamp < CacheExpiration)
            {
                _logger.Debug("Returning cached Conda information");
                return cachedInfo.Info;
            }
            CondaInfo condaInfo = await ExecuteCondaCommandAsync<CondaInfo>("info", "--json") ?? throw new InvalidOperationException("Failed to retrieve Conda information");
            _condaInfoCache[cacheKey] = new CachedCondaInfo(condaInfo, DateTime.Now);

            return condaInfo;
        }

        /// <summary>
        /// Creates a new Conda environment.
        /// </summary>
        /// <param name="environmentName">Name of the environment to create</param>
        /// <param name="pythonVersion">Optional Python version to install</param>
        public async Task<bool> CreateEnvironmentAsync(string environmentName, string? pythonVersion = null)
        {
            List<string> createArgs = new() { "create", "-n", environmentName, "--yes", "--json", "--quiet" };

            if (!string.IsNullOrEmpty(pythonVersion))
                createArgs.Add($"python={pythonVersion}");

            try
            {
                CondaCreateResponse? response = await ExecuteCondaCommandAsync<CondaCreateResponse>(createArgs.ToArray());

                if (response?.Success == true && !string.IsNullOrEmpty(response.Prefix))
                {
                    _logger.Debug($"Successfully created environment '{environmentName}' at {response.Prefix}");
                    _condaInfoCache.Clear();
                    return true;
                }

                _logger.Warn($"Failed to create environment '{environmentName}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Environment creation failed for {environmentName}");
                return false;
            }
        }

        public async Task<bool> AddCondaForgeChannelAsync()
        {
            CondaInfo condaInfo = await GetCondaInfoAsync();
            bool hasCondaForge = condaInfo.Channels?.Any(c => c.Contains("conda-forge")) == true;
            if (!hasCondaForge)
            {
                _logger.Info("Adding conda-forge channel to conda configuration");
                await ExecuteCondaCommandAsync<object>("config", "--add", "channels", "conda-forge", "--json", "--quiet");
                InvalidateCache();
                return true;
            }
            _logger.Debug("conda-forge channel already configured");
            return false;
        }

        /// <summary>
        /// Installs a package in a specified Conda environment.
        /// </summary>
        /// <param name="packageName">Name of the package to install</param>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<bool> InstallPackageAsync(string packageName, string? environmentName = null)
        {
            List<string> installArgs = new() { "install", "--yes", "--json", "--quiet" };

            if (!string.IsNullOrEmpty(environmentName))
            {
                installArgs.Add("-n");
                installArgs.Add(environmentName);
            }

            installArgs.Add(packageName);

            try
            {
                CondaInstallResponse? response = await ExecuteCondaCommandAsync<CondaInstallResponse>(installArgs.ToArray());

                if (response?.Success == true)
                {
                    _logger.Debug($"Successfully installed package '{packageName}'");
                    return true;
                }

                _logger.Warn($"Failed to install package '{packageName}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Package installation failed for {packageName}");
                return false;
            }
        }

        /// <summary>
        /// Uninstalls a package from a specified Conda environment.
        /// </summary>
        /// <param name="packageName">Name of the package to uninstall</param>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<bool> UninstallPackageAsync(string packageName, string? environmentName = null)
        {
            List<string> uninstallArgs = new() { "uninstall", "--yes", "--json", "--quiet" };

            if (!string.IsNullOrEmpty(environmentName))
            {
                uninstallArgs.Add("-n");
                uninstallArgs.Add(environmentName);
            }

            uninstallArgs.Add(packageName);

            try
            {
                CondaUninstallResponse? response = await ExecuteCondaCommandAsync<CondaUninstallResponse>(uninstallArgs.ToArray());

                if (response?.Success == true)
                {
                    _logger.Debug($"Successfully uninstalled package '{packageName}'");
                    return true;
                }

                _logger.Warn($"Failed to uninstall package '{packageName}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Package uninstallation failed for {packageName}");
                return false;
            }
        }

        /// <summary>
        /// Gets all available Conda environments.
        /// </summary>
        public async Task<List<string>> GetEnvironmentsAsync()
        {
            try
            {
                CondaInfo condaInfo = await GetCondaInfoAsync();
                return condaInfo.Environments ?? new();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get environments");
                return new();
            }
        }

        /// <summary>
        /// Gets all packages in a specified environment.
        /// </summary>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<List<CondaPackage>> GetPackagesAsync(string? environmentName = null)
        {
            List<string> listArgs = new() { "list", "--json" };

            if (!string.IsNullOrEmpty(environmentName))
            {
                listArgs.Add("-n");
                listArgs.Add(environmentName);
            }

            try
            {
                List<CondaPackage>? packages = await ExecuteCondaCommandAsync<List<CondaPackage>>(listArgs.ToArray());
                return packages ?? new();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get packages list");
                return new();
            }
        }

        /// <summary>
        /// Gets the Python version for a specified environment.
        /// </summary>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<string> GetPythonVersionAsync(string? environmentName = null)
        {
            List<string> listArgs = new() { "list", "python", "--json" };

            if (!string.IsNullOrEmpty(environmentName))
            {
                listArgs.Add("-n");
                listArgs.Add(environmentName);
            }

            try
            {
                List<CondaPackage>? packages = await ExecuteCondaCommandAsync<List<CondaPackage>>(listArgs.ToArray());
                CondaPackage? pythonPackage = packages?.FirstOrDefault(p => p.Name == "python");

                return pythonPackage?.Version ?? "unknown";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get Python version");
                return "unknown";
            }
        }

        /// <summary>
        /// Installs packages from a requirements file in a specified environment.
        /// </summary>
        /// <param name="requirementsFile">Path to the requirements.txt file</param>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<bool> InstallRequirementsAsync(string requirementsFile, string? environmentName = null)
        {
            if (!File.Exists(requirementsFile))
                throw new FileNotFoundException($"Requirements file not found at {requirementsFile}");

            List<string> installArgs = new() { "install", "--yes", "--json", "--quiet", "--file", requirementsFile };

            if (!string.IsNullOrEmpty(environmentName))
            {
                installArgs.Add("-n");
                installArgs.Add(environmentName);
            }

            try
            {
                CondaInstallResponse? response = await ExecuteCondaCommandAsync<CondaInstallResponse>(installArgs.ToArray());

                if (response?.Success == true)
                {
                    _logger.Info($"Successfully installed packages from {requirementsFile}");
                    return true;
                }

                _logger.Warn($"Failed to install packages from {requirementsFile}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Package installation failed for {requirementsFile}");
                return false;
            }
        }

        /// <summary>
        /// Gets the path to the Python executable in the specified environment.
        /// </summary>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<string> GetPythonExecutablePathAsync(string? environmentName = null)
        {
            try
            {
                string envPath;
                CondaInfo condaInfo = await GetCondaInfoAsync();
                OSPlatform platform = PlatformUtils.GetCurrentPlatform();

                if (string.IsNullOrEmpty(environmentName))
                {
                    envPath = condaInfo.RootPrefix ?? throw new InvalidOperationException("Could not determine root prefix");
                }
                else
                {
                    List<string> environments = condaInfo.Environments ?? new();
                    envPath = environments.FirstOrDefault(e => Path.GetFileName(e) == environmentName) ?? throw new InvalidOperationException($"Environment '{environmentName}' not found");
                }

                string pythonExe = "python" + PlatformUtils.GetExecutableExtension();
                string pythonPath = platform == OSPlatform.Windows ? Path.Combine(envPath, pythonExe) : Path.Combine(envPath, "bin", pythonExe);

                if (!File.Exists(pythonPath))
                    throw new FileNotFoundException($"Python executable not found at {pythonPath}");

                return pythonPath;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to get Python executable path for environment: {environmentName}");
                throw;
            }
        }

        /// <summary>
        /// Checks if an environment exists.
        /// </summary>
        /// <param name="environmentName">Name of the environment to check</param>
        public async Task<bool> EnvironmentExistsAsync(string environmentName)
        {
            try
            {
                List<string> environments = await GetEnvironmentsAsync();
                return environments.Any(e => Path.GetFileName(e) == environmentName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error checking if environment exists: {environmentName}");
                return false;
            }
        }

        /// <summary>
        /// Gets the path to a specific environment.
        /// </summary>
        /// <param name="environmentName">Name of the environment</param>
        public async Task<string?> GetEnvironmentPathAsync(string environmentName)
        {
            try
            {
                CondaInfo condaInfo = await GetCondaInfoAsync();
                List<string> environments = condaInfo.Environments ?? new();

                string? directMatch = environments.FirstOrDefault(e => Path.GetFileName(e).Equals(environmentName, StringComparison.OrdinalIgnoreCase));
                if (directMatch != null)
                    return directMatch;

                foreach (string env in environments)
                {
                    string envName = Path.GetFileName(env);
                    if (envName.Trim().Equals(environmentName.Trim(), StringComparison.OrdinalIgnoreCase))
                        return env;
                }
                _logger.Error($"Could not find environment path for '{environmentName}'. Available environments: {string.Join(", ", environments.Select(Path.GetFileName))}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error getting environment path: {environmentName}");
                return null;
            }
        }

        /// <summary>
        /// Installs packages from a requirements file in a specified environment using pip.
        /// </summary>
        /// <param name="requirementsFile">Path to the requirements.txt file</param>
        /// <param name="environmentName">Optional environment name (base environment if not specified)</param>
        public async Task<bool> InstallPipRequirementsAsync(string requirementsFile, string? environmentName = null)
        {
            List<string> runArgs = new() { "run" };

            if (!string.IsNullOrEmpty(environmentName))
            {
                runArgs.Add("-n");
                runArgs.Add(environmentName);
            }

            runArgs.AddRange(new string[] { "pip", "install", "-r", requirementsFile, "--no-input" });

            try
            {
                string output = await ExecuteRawCondaCommandAsync(runArgs.ToArray());
                _logger.Info($"Successfully installed pip requirements from {requirementsFile}");
                return !output.Contains("ERROR");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Pip requirements installation failed for {requirementsFile}");
                return false;
            }
        }

        /// <summary>
        /// Invalidates the Conda information cache.
        /// </summary>
        public void InvalidateCache()
        {
            _condaInfoCache.Clear();
            _logger.Trace("Conda info cache invalidated");
        }

        #region Private methods

        /// <summary>
        /// Executes a Conda command and deserializes its JSON response.
        /// </summary>
        private async Task<T?> ExecuteCondaCommandAsync<T>(params string[] args)
        {
            string rawOutput = await ExecuteRawCondaCommandAsync(args);

            if (string.IsNullOrWhiteSpace(rawOutput))
            {
                _logger.Warn($"Empty response from conda command: {string.Join(" ", args)}");
                return default;
            }

            try
            {
                string jsonContent = ExtractJsonContent(rawOutput);

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    _logger.Warn($"No valid JSON content found in response for command: {string.Join(" ", args)}");
                    return default;
                }

                return JsonSerializer.Deserialize<T>(jsonContent, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.Error(ex, $"JSON deserialization failed for Conda command: {string.Join(" ", args)}");
                return default;
            }
        }

        /// <summary>
        /// Executes a raw Conda command and returns its output.
        /// </summary>
        private async Task<string> ExecuteRawCondaCommandAsync(params string[] args)
        {
            string commandLine = string.Join(" ", args.Select(QuoteArgumentIfNeeded));
            _logger.Trace($"Executing conda command: {_condaExecutablePath} {commandLine}");

            using Process process = CreateCondaProcess(args);

            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    lock (outputBuilder)
                    {
                        outputBuilder.AppendLine(e.Data);
                        _logger.Trace($"Conda stdout: {e.Data}");
                    }
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    lock (errorBuilder)
                    {
                        errorBuilder.AppendLine(e.Data);
                        _logger.Trace($"Conda stderr: {e.Data}");
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            string output = outputBuilder.ToString();
            string error = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                _logger.Warn($"Conda command failed with exit code {process.ExitCode}");
                if (!string.IsNullOrEmpty(error))
                    _logger.Warn($"Error details: {error}");

                CondaError errorObj = new(
                    CausedBy: null,
                    Error: $"Command exited with code {process.ExitCode}",
                    ExceptionName: "ProcessExitException",
                    ExceptionType: null,
                    Message: error,
                    Filename: null
                );

                return JsonSerializer.Serialize(errorObj, _jsonOptions);
            }

            if (!string.IsNullOrEmpty(output))
                _logger.Trace($"Conda command result: {(output.Length > 150 ? output[..150] + "..." : output)}");

            return output;
        }

        /// <summary>
        /// Creates a process for executing Conda commands.
        /// </summary>
        private Process CreateCondaProcess(params string[] args)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _condaExecutablePath,
                Arguments = string.Join(" ", args.Select(QuoteArgumentIfNeeded)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return new Process { StartInfo = startInfo };
        }

        /// <summary>
        /// Safely quotes command arguments containing special characters.
        /// </summary>
        private static string QuoteArgumentIfNeeded(string argument)
        {
            if (string.IsNullOrEmpty(argument))
                return "\"\"";

            bool needsQuoting = argument.Any(c =>
                char.IsWhiteSpace(c) ||
                "\"&|<>^".Contains(c));

            if (!needsQuoting)
                return argument;

            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }

        /// <summary>
        /// Extracts JSON content from mixed output.
        /// </summary>
        private static string ExtractJsonContent(string output)
        {
            if (output.TrimStart().StartsWith('['))
            {
                int startIndex = output.IndexOf('[');
                if (startIndex < 0) return string.Empty;

                int bracketCount = 1;
                int endIndex = startIndex + 1;

                while (endIndex < output.Length && bracketCount > 0)
                {
                    if (output[endIndex] == '[') bracketCount++;
                    else if (output[endIndex] == ']') bracketCount--;
                    endIndex++;
                }

                return bracketCount == 0 ? output[startIndex..endIndex] : string.Empty;
            }

            int objStartIndex = output.IndexOf('{');
            if (objStartIndex < 0) return string.Empty;

            int braceCount = 1;
            int objEndIndex = objStartIndex + 1;

            while (objEndIndex < output.Length && braceCount > 0)
            {
                if (output[objEndIndex] == '{') braceCount++;
                else if (output[objEndIndex] == '}') braceCount--;
                objEndIndex++;
            }

            return braceCount == 0 ? output[objStartIndex..objEndIndex] : string.Empty;
        }
        #endregion
    }
}