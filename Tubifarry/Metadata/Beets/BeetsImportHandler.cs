using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Tubifarry.Metadata.Beets
{
    public sealed class BeetsImportHandler(IMetadataFactory metadataFactory, IMediaFileService mediaFileService, IBeetsService beetsService, Logger logger) : IHandle<AlbumImportedEvent>
    {
        public void Handle(AlbumImportedEvent message)
        {
            BeetsSettings? settings = metadataFactory.GetEnabledBeetsSettings();

            if (settings?.ImportOnAlbumImport != true)
                return;

            IEnumerable<string> albumFiles = mediaFileService.GetFilesByAlbum(message.Album.Id)
                .Select(f => f.Path)
                .Concat(message.ImportedTracks.Select(t => t.Path));

            BeetsAlbumFolder album = BeetsAlbumFolder.Resolve(albumFiles, message.Artist.Path);

            switch (album.Problem)
            {
                case BeetsAlbumFolderProblem.ArtistFolder:
                    logger.Info("Skipping beets for '{0}': its files are stored directly in the artist folder. beets needs one folder per album, enable album folders in Lidarr's track naming settings.", message.Album.Title);
                    return;
                case BeetsAlbumFolderProblem.OutsideArtistFolder:
                    logger.Debug("No album folder inside the artist folder was found for '{0}', skipping beets", message.Album.Title);
                    return;
                case BeetsAlbumFolderProblem.ForeignFiles:
                    logger.Warn("Skipping beets for '{0}': '{1}' also holds {2} audio file(s) of other albums. Use a separate folder per album in Lidarr's naming settings.", message.Album.Title, album.Folder, album.ForeignFiles);
                    return;
            }

            beetsService.Enqueue(album.Folder!, message.Album.Title, settings);
        }
    }
}
