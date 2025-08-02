using System.Text.Json.Serialization;

namespace Tubifarry.Core.NodeBridge
{
    /// <summary>
    /// Result of Node.js code execution.
    /// </summary>
    public class NodeExecutionResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public object? ReturnValue { get; set; }

        public string Output => string.IsNullOrEmpty(StandardError)
            ? StandardOutput
            : StandardOutput + Environment.NewLine + StandardError;

        public bool HasOutput => !string.IsNullOrEmpty(StandardOutput) || !string.IsNullOrEmpty(StandardError);

        public override string ToString() => $"Success: {Success}, ExitCode: {ExitCode}, HasOutput: {HasOutput}";
    }

    /// <summary>
    /// Information about an npm package.
    /// </summary>
    public record NodePackage(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("homepage")] string? Homepage,
        [property: JsonPropertyName("keywords")] string[]? Keywords,
        [property: JsonPropertyName("dependencies")] Dictionary<string, string>? Dependencies
    );

    /// <summary>
    /// Node.js installation information.
    /// </summary>
    public record NodeInstallInfo(
        string Version,
        string InstallPath,
        string NodeExecutable,
        string NpmExecutable,
        string NpmVersion,
        DateTime InstallDate
    );

    /// <summary>
    /// Node.js version information from the official index.
    /// </summary>
    public record NodeVersion(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("date")] DateTime Date,
        [property: JsonPropertyName("files")] string[] Files,
        [property: JsonPropertyName("npm")] string Npm,
        [property: JsonPropertyName("v8")] string V8,
        [property: JsonPropertyName("uv")] string Uv,
        [property: JsonPropertyName("zlib")] string Zlib,
        [property: JsonPropertyName("openssl")] string Openssl,
        [property: JsonPropertyName("modules")] string Modules,
        [property: JsonPropertyName("lts")] object Lts,
        [property: JsonPropertyName("security")] bool Security
    );

    /// <summary>
    /// Result of npm operations.
    /// </summary>
    public record NpmResult(
        bool Success,
        string? Message,
        string? Error,
        Dictionary<string, object>? Data
    );

    /// <summary>
    /// Exception thrown when there is an error configuring the Node.js environment.
    /// </summary>
    public class NodeJsConfigurationException : Exception
    {
        public NodeJsConfigurationException() : base() { }
        public NodeJsConfigurationException(string message) : base(message) { }
        public NodeJsConfigurationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when there is an error executing Node.js code.
    /// </summary>
    public class NodeJsExecutionException : Exception
    {
        public NodeJsExecutionException() : base() { }
        public NodeJsExecutionException(string message) : base(message) { }
        public NodeJsExecutionException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when there is an error with npm package operations.
    /// </summary>
    public class NodeJsPackageException : Exception
    {
        public NodeJsPackageException() : base() { }
        public NodeJsPackageException(string message) : base(message) { }
        public NodeJsPackageException(string message, Exception innerException) : base(message, innerException) { }
    }
}