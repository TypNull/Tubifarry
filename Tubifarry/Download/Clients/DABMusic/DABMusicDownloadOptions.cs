using Tubifarry.Download.Base;

namespace Tubifarry.Download.Clients.DABMusic
{
    public record DABMusicDownloadOptions : BaseDownloadOptions
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public DABMusicDownloadOptions() : base() { }

        protected DABMusicDownloadOptions(DABMusicDownloadOptions options) : base(options)
        {
            Email = options.Email;
            Password = options.Password;
        }
    }
}