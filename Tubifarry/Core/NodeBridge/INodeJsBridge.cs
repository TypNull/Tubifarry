namespace Tubifarry.Core.NodeBridge
{
    /// <summary>
    /// Defines the contract for Node.js bridge implementations.
    /// Provides a clean interface for Node.js environment management.
    /// </summary>
    public interface INodeJsBridge : IDisposable
    {
        /// <summary>
        /// Indicates whether the Node.js bridge has been successfully initialized.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Gets the version of the Node.js runtime being used.
        /// </summary>
        string NodeVersion { get; }

        /// <summary>
        /// Gets the version of npm being used.
        /// </summary>
        string NpmVersion { get; }

        /// <summary>
        /// Gets the Node.js installation information.
        /// </summary>
        NodeInstallInfo? InstallInfo { get; }

        /// <summary>
        /// Initializes the Node.js bridge with optional required packages.
        /// </summary>
        /// <param name="requiredPackages">npm packages to install during initialization</param>
        /// <returns>True if initialization succeeds</returns>
        Task<bool> InitializeAsync(params string[] requiredPackages);

        /// <summary>
        /// Executes the specified Node.js code and returns the result.
        /// </summary>
        /// <param name="code">The Node.js code to execute</param>
        /// <returns>Execution result with output and status</returns>
        Task<NodeExecutionResult> ExecuteCodeAsync(string code);

        /// <summary>
        /// Executes a Node.js script file.
        /// </summary>
        /// <param name="scriptPath">Path to the script file</param>
        /// <param name="arguments">Optional command line arguments</param>
        /// <returns>Execution result with output and status</returns>
        Task<NodeExecutionResult> ExecuteScriptAsync(string scriptPath, string? arguments = null);

        /// <summary>
        /// Installs the specified npm packages.
        /// </summary>
        /// <param name="packages">Package names to install</param>
        /// <returns>Installation result</returns>
        Task<NodeExecutionResult> InstallPackagesAsync(params string[] packages);
    }
}