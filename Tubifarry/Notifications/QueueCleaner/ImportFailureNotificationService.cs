using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Notifications.QueueCleaner
{
    public class ImportFailureNotificationService : IHandle<AlbumImportIncompleteEvent>
    {

        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly Logger _logger;

        public ImportFailureNotificationService(INotificationFactory notificationFactory, INotificationStatusService notificationStatusService, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _logger = logger;
        }

        private bool ShouldHandleArtist(ProviderDefinition definition, Artist artist)
        {
            if (definition.Tags.Empty())
            {
                _logger.Debug("No tags set for this notification.");
                return true;
            }

            if (definition.Tags.Intersect(artist.Tags).Any())
            {
                _logger.Debug("Notification and artist have one or more intersecting tags.");
                return true;
            }
            _logger.Debug("{0} does not have any intersecting tags with {1}. Notification will not be sent.", definition.Name, artist.Name);
            return false;
        }

        public void Handle(AlbumImportIncompleteEvent message)
        {
            foreach (INotification? notification in _notificationFactory.OnImportFailureEnabled())
            {
                if (notification is not QueueCleaner queue)
                    continue;
                try
                {
                    if (ShouldHandleArtist(notification.Definition, message.TrackedDownload.RemoteAlbum.Artist))
                    {
                        queue.CleanImport(message);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send CleanImport: " + notification.Definition.Name);
                }
            }
        }
    }
}
