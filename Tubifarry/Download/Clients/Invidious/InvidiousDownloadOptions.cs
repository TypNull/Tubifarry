using Tubifarry.Download.Base;
using Tubifarry.Download.Clients.YouTube;

namespace Tubifarry.Download.Clients.Invidious
{
    public record InvidiousDownloadOptions : BaseDownloadOptions
    {
        public bool ProxyVideos { get; set; } = true;
        public ReEncodeOptions ReEncodeOptions { get; set; } = ReEncodeOptions.Disabled;
        public bool UseID3v2_3 { get; set; }
        public bool UseSponsorBlock { get; set; }
        public string SponsorBlockApiEndpoint { get; set; } = "https://sponsor.ajay.app";

        public InvidiousDownloadOptions() : base() { }

        protected InvidiousDownloadOptions(InvidiousDownloadOptions options) : base(options)
        {
            ProxyVideos = options.ProxyVideos;
            ReEncodeOptions = options.ReEncodeOptions;
            UseID3v2_3 = options.UseID3v2_3;
            UseSponsorBlock = options.UseSponsorBlock;
            SponsorBlockApiEndpoint = options.SponsorBlockApiEndpoint;
        }
    }
}
