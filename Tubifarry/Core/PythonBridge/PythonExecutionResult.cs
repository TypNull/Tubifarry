namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Represents the result of a Python code or function execution.
    /// </summary>
    public class PythonExecutionResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the execution was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the exit code of the Python execution.
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Gets or sets the standard output from the Python execution.
        /// </summary>
        public string StandardOutput { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the standard error from the Python execution.
        /// </summary>
        public string StandardError { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the return value from a Python function execution.
        /// </summary>
        public object? ReturnValue { get; set; }

        /// <summary>
        /// Gets a combined output string (standard output and standard error).
        /// </summary>
        public string Output => string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + Environment.NewLine + StandardError;

        /// <summary>
        /// Gets a value indicating whether there is any output (either standard output or standard error).
        /// </summary>
        public bool HasOutput => !string.IsNullOrEmpty(StandardOutput) || !string.IsNullOrEmpty(StandardError);

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>A string that represents the current object.</returns>
        public override string ToString() => $"Success: {Success}, ExitCode: {ExitCode}, HasOutput: {HasOutput}";
    }
}