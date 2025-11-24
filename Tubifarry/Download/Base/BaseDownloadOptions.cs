using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Organizer;
using Requests.Options;

namespace Tubifarry.Download.Base
{
    /// <summary>
    /// Base options for download requests containing common configuration
    /// </summary>
    public record BaseDownloadOptions : RequestOptions<string, string>
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
        /// Base URL of the service instance
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Timeout for HTTP requests in seconds
        /// </summary>
        public int RequestTimeout { get; set; } = 60;

        /// <summary>
        /// Maximum download speed in bytes per second (0 = unlimited)
        /// </summary>
        public int MaxDownloadSpeed { get; set; }


        /// <summary>
        /// Number of chunks for download
        /// </summary>
        public int Chunks { get; set; } = 1;

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
        /// The item ID/URL to download from
        /// </summary>
        public string ItemId { get; set; } = string.Empty;

        /// <summary>
        /// Request tnterceptors for requests
        /// </summary>
        public IEnumerable<IHttpRequestInterceptor> RequestInterceptors { get; set; } = [];

        public BaseDownloadOptions() { }

        protected BaseDownloadOptions(BaseDownloadOptions options) : base(options)
        {
            ClientInfo = options.ClientInfo;
            DownloadPath = options.DownloadPath;
            BaseUrl = options.BaseUrl;
            RequestTimeout = options.RequestTimeout;
            MaxDownloadSpeed = options.MaxDownloadSpeed;
            ConnectionRetries = options.ConnectionRetries;
            NamingConfig = options.NamingConfig;
            IsTrack = options.IsTrack;
            Chunks = options.Chunks;
            ItemId = options.ItemId;
            RequestInterceptors = options.RequestInterceptors;
        }
    }
}