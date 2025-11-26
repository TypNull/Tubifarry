using Tubifarry.Download.Base;

namespace Tubifarry.Download.Clients.SubSonic
{
    /// <summary>
    /// Download options specific to SubSonic downloads
    /// </summary>
    public record SubSonicDownloadOptions : BaseDownloadOptions
    {
        /// <summary>
        /// SubSonic username for authentication
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// SubSonic password for authentication
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Whether to use token-based authentication
        /// </summary>
        public bool UseTokenAuth { get; set; } = true;

        /// <summary>
        /// Preferred audio format for transcoding
        /// </summary>
        public PreferredFormatEnum PreferredFormat { get; set; } = PreferredFormatEnum.Raw;

        /// <summary>
        /// Maximum bit rate in kbps (0 for original quality)
        /// </summary>
        public int MaxBitRate { get; set; } = 0;

        public SubSonicDownloadOptions() : base() { }

        protected SubSonicDownloadOptions(SubSonicDownloadOptions options) : base(options)
        {
            Username = options.Username;
            Password = options.Password;
            UseTokenAuth = options.UseTokenAuth;
            PreferredFormat = options.PreferredFormat;
            MaxBitRate = options.MaxBitRate;
        }
    }
}