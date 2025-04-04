namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Defines an interface for Python bridge implementations, providing methods for initialization, code execution, package management, and logging.
    /// </summary>
    public interface IPythonBridge : IDisposable
    {
        /// <summary>
        /// Indicates whether the Python bridge has been successfully initialized.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Retrieves the version of the Python runtime being used.
        /// </summary>
        string PythonVersion { get; }

        /// <summary>
        /// Gets the logger for capturing standard output from Python operations.
        /// </summary>
        public PythonConsoleLogger OutLogger { get; }

        /// <summary>
        /// Gets the logger for capturing error output from Python operations.
        /// </summary>
        public PythonConsoleLogger ErrLogger { get; }

        /// <summary>
        /// Asynchronously initializes the Python bridge with optional required packages.
        /// </summary>
        /// <param name="requiredPackages">Optional Python packages to install during initialization.</param>
        /// <returns>A task that returns true if initialization is successful, otherwise false.</returns>
        Task<bool> InitializeAsync(params string[] requiredPackages);

        /// <summary>
        /// Executes the specified Python code and returns the execution result.
        /// </summary>
        /// <param name="code">The Python code to be executed.</param>
        /// <returns>A result object containing the outcome of the code execution.</returns>
        PythonExecutionResult ExecuteCode(string code);

        /// <summary>
        /// Asynchronously installs the specified Python packages.
        /// </summary>
        /// <param name="packages">The names of packages to install.</param>
        /// <returns>A task that returns the result of the package installation process.</returns>
        Task<PythonExecutionResult> InstallPackagesAsync(params string[] packages);
    }
}