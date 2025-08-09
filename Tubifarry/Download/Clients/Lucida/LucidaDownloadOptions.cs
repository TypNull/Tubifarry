using NzbDrone.Core.Download;
using NzbDrone.Core.Organizer;
using Requests.Options;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Options for Lucida download requests
    /// Contains all configuration needed for a download operation
    /// </summary>
    internal record LucidaDownloadOptions : RequestOptions<string, string>
    {
        /// <summary>
        /// Client info for tracking the download in Lidarr
        /// </summary>
        public DownloadClientItemClientInfo? ClientInfo { get; set; }

        /// <summary>
        /// Path where downloads will be stored
        /// </summary>
        public string DownloadPath { get; set; } = string.Empty;

        /// <summary>
        /// Base URL of the Lucida instance
        /// </summary>
        public string BaseUrl { get; set; } = "https://lucida.to";

        /// <summary>
        /// Timeout for HTTP requests in seconds
        /// </summary>
        public int RequestTimeout { get; set; } = 30;

        /// <summary>
        /// Maximum download speed in bytes per second (0 = unlimited)
        /// </summary>
        public int MaxDownloadSpeed { get; set; } = 0;

        /// <summary>
        /// Number of times to retry connections
        /// </summary>
        public int ConnectionRetries { get; set; } = 3;

        /// <summary>
        /// Naming configuration from Lidarr
        /// </summary>
        public NamingConfig? NamingConfig { get; set; }

        /// <summary>
        /// Whether this download is for a track (true) or album (false)
        /// </summary>
        public bool IsTrack { get; set; }

        /// <summary>
        /// The actual URL to download from
        /// </summary>
        public string ItemUrl { get; set; } = string.Empty;

        public LucidaDownloadOptions() { }

        protected LucidaDownloadOptions(LucidaDownloadOptions options) : base(options)
        {
            ClientInfo = options.ClientInfo;
            DownloadPath = options.DownloadPath;
            BaseUrl = options.BaseUrl;
            RequestTimeout = options.RequestTimeout;
            MaxDownloadSpeed = options.MaxDownloadSpeed;
            ConnectionRetries = options.ConnectionRetries;
            NamingConfig = options.NamingConfig;
            IsTrack = options.IsTrack;
            ItemUrl = options.ItemUrl;
        }
    }
}
