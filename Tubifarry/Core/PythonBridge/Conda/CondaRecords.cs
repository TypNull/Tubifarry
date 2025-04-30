using System.Text.Json.Serialization;

namespace Tubifarry.Core.PythonBridge.Conda
{
    /// <summary>
    /// Response from conda create command
    /// </summary>
    public record CondaCreateResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("prefix")] string? Prefix,
        [property: JsonPropertyName("actions")] CondaActions? Actions
    );

    /// <summary>
    /// Actions performed by conda
    /// </summary>
    public record CondaActions(
        [property: JsonPropertyName("FETCH")] List<object>? Fetch,
        [property: JsonPropertyName("PREFIX")] string? Prefix
    );

    /// <summary>
    /// Response from conda install command
    /// </summary>
    public record CondaInstallResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("actions")] CondaInstallActions? Actions,
        [property: JsonPropertyName("messages")] List<string>? Messages
    );

    /// <summary>
    /// Actions performed during package installation
    /// </summary>
    public record CondaInstallActions(
        [property: JsonPropertyName("FETCH")] List<CondaPackage>? Fetch,
        [property: JsonPropertyName("LINK")] List<CondaPackage>? Link,
        [property: JsonPropertyName("UNLINK")] List<CondaPackage>? Unlink,
        [property: JsonPropertyName("PREFIX")] string? Prefix
    );

    /// <summary>
    /// Information about a conda package
    /// </summary>
    public record CondaPackage(
        [property: JsonPropertyName("base_name")] string? BaseName,
        [property: JsonPropertyName("build_string")] string? BuildString,
        [property: JsonPropertyName("build_number")] int BuildNumber,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("base_url")] string? BaseUrl,
        [property: JsonPropertyName("dist_name")] string? DistName
    );

    /// <summary>
    /// Response from conda uninstall command
    /// </summary>
    public record CondaUninstallResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("actions")] CondaUninstallActions? Actions
    );

    /// <summary>
    /// Actions performed during package uninstallation
    /// </summary>
    public record CondaUninstallActions(
        [property: JsonPropertyName("UNLINK")] List<CondaPackage>? Unlink,
        [property: JsonPropertyName("PREFIX")] string? Prefix
    );

    /// <summary>
    /// System information from conda
    /// </summary>
    public record CondaInfo(
        [property: JsonPropertyName("active_prefix")] string? ActivePrefix,
        [property: JsonPropertyName("active_prefix_name")] string? ActivePrefixName,
        [property: JsonPropertyName("av_data_dir")] string? AvDataDir,
        [property: JsonPropertyName("av_metadata_url_base")] string? AvMetadataUrlBase,
        [property: JsonPropertyName("channels")] List<string>? Channels,
        [property: JsonPropertyName("conda_build_version")] string? CondaBuildVersion,
        [property: JsonPropertyName("conda_env_version")] string? CondaEnvVersion,
        [property: JsonPropertyName("conda_location")] string? CondaLocation,
        [property: JsonPropertyName("conda_prefix")] string? CondaPrefix,
        [property: JsonPropertyName("conda_shlvl")] int CondaShlvl,
        [property: JsonPropertyName("conda_version")] string? CondaVersion,
        [property: JsonPropertyName("config_files")] List<string>? ConfigFiles,
        [property: JsonPropertyName("default_prefix")] string? DefaultPrefix,
        [property: JsonPropertyName("env_vars")] Dictionary<string, string>? EnvVars,
        [property: JsonPropertyName("envs")] List<string>? Environments,
        [property: JsonPropertyName("envs_dirs")] List<string>? EnvironmentDirs,
        [property: JsonPropertyName("is_windows_admin")] bool IsWindowsAdmin,
        [property: JsonPropertyName("netrc_file")] string? NetrcFile,
        [property: JsonPropertyName("offline")] bool Offline,
        [property: JsonPropertyName("pkgs_dirs")] List<string>? PackageDirs,
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("python_version")] string? PythonVersion,
        [property: JsonPropertyName("rc_path")] string? RcPath,
        [property: JsonPropertyName("requests_version")] string? RequestsVersion,
        [property: JsonPropertyName("root_prefix")] string? RootPrefix,
        [property: JsonPropertyName("root_writable")] bool RootWritable,
        [property: JsonPropertyName("site_dirs")] List<string>? SiteDirs,
        [property: JsonPropertyName("solver")] CondaSolver? Solver,
        [property: JsonPropertyName("sys.executable")] string? SysExecutable,
        [property: JsonPropertyName("sys.prefix")] string? SysPrefix,
        [property: JsonPropertyName("sys.version")] string? SysVersion,
        [property: JsonPropertyName("sys_rc_path")] string? SysRcPath,
        [property: JsonPropertyName("user_agent")] string? UserAgent,
        [property: JsonPropertyName("user_rc_path")] string? UserRcPath,
        [property: JsonPropertyName("virtual_pkgs")] List<List<string>>? VirtualPackages
    );

    /// <summary>
    /// Information about conda solver
    /// </summary>
    public record CondaSolver(
        [property: JsonPropertyName("default")] bool Default,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("user_agent")] string? UserAgent
    );

    /// <summary>
    /// Error information from conda
    /// </summary>
    public record CondaError(
        [property: JsonPropertyName("caused_by")] string? CausedBy,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("exception_name")] string? ExceptionName,
        [property: JsonPropertyName("exception_type")] string? ExceptionType,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("filename")] string? Filename
    );

    /// <summary>
    /// Response from conda run command
    /// </summary>
    public record CondaRunResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("stdout")] string? StdOut,
        [property: JsonPropertyName("stderr")] string? StdErr,
        [property: JsonPropertyName("exit_code")] int ExitCode
    );
}