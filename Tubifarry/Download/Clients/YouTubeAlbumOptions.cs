﻿using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.YouTube;
using NzbDrone.Core.Organizer;
using Requests.Options;
using YouTubeMusicAPI.Client;

namespace Tubifarry.Download.Clients
{
    public record YouTubeAlbumOptions : RequestOptions<string, string>
    {
        public YouTubeMusicClient? YouTubeMusicClient { get; set; }

        public DownloadClientItemClientInfo? ClientInfo { get; set; }

        public bool TryIncludeLrc { get; set; }

        public bool TryIncludeSycLrc { get; set; }

        public string DownloadPath { get; set; } = string.Empty;

        public string LRCLIBInstance { get; set; } = "https://lrclib.net";

        public int Chunks { get; set; } = 2;

        public ReEncodeOptions ReEncodeOptions { get; set; }

        public bool UseID3v2_3 { get; set; }

        public NamingConfig? NameingConfig { get; set; }

        public YouTubeAlbumOptions() { }

        protected YouTubeAlbumOptions(YouTubeAlbumOptions options) : base(options)
        {
            YouTubeMusicClient = options.YouTubeMusicClient;
            ClientInfo = options.ClientInfo;
            TryIncludeSycLrc = options.TryIncludeSycLrc;
            TryIncludeLrc = options.TryIncludeLrc;
            Chunks = options.Chunks;
            NameingConfig = options.NameingConfig;
            UseID3v2_3 = options.UseID3v2_3;
            ReEncodeOptions = options.ReEncodeOptions;
            LRCLIBInstance = options.LRCLIBInstance;
            DownloadPath = options.DownloadPath;
        }
    }
}
