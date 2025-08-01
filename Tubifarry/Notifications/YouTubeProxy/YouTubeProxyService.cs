using DownloadAssistant.Base;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace Tubifarry.Notifications.YouTubeProxy
{
    public class YouTubeProxyService : IHandle<ApplicationStartedEvent>
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly Logger _logger;

        public YouTubeProxyService(INotificationFactory notificationFactory, INotificationStatusService notificationStatusService, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            try
            {
                ConfigureProxySettings();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to configure proxy settings on application startup");
            }
        }

        private void ConfigureProxySettings()
        {
            foreach (INotification? notification in (List<INotification>)_notificationFactory.GetAvailableProviders())
            {
                if (notification is not YouTubeProxyNotification proxyNotification)
                    continue;

                if (notification.Definition is not NotificationDefinition definition || !definition.Enable)
                {
                    _logger.Debug("YouTubeProxy notification is disabled, skipping proxy configuration");
                    continue;
                }

                YouTubeProxySettings settings = (YouTubeProxySettings)proxyNotification.Definition.Settings;

                try
                {
                    ConfigureHttpClientWithProxy(settings);
                    _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    _logger.Info("Successfully configured HTTP client with SOCKS5 proxy: {0}:{1}", settings.ProxyHost, settings.ProxyPort);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Error(ex, "Failed to configure HTTP client with proxy settings");
                }
            }
        }

        private void ConfigureHttpClientWithProxy(YouTubeProxySettings settings)
        {
            _logger.Info("Configuring HTTP client with SOCKS5 proxy: {0}:{1}", settings.ProxyHost, settings.ProxyPort);

            WebProxy proxy;
            if (settings.RequireAuthentication && !string.IsNullOrWhiteSpace(settings.Username))
            {
                proxy = new WebProxy($"socks5://{settings.ProxyHost}:{settings.ProxyPort}")
                {
                    Credentials = new NetworkCredential(settings.Username, settings.Password)
                };
                _logger.Debug("Proxy configured with authentication for user: {0}", settings.Username);
            }
            else
            {
                proxy = new WebProxy($"socks5://{settings.ProxyHost}:{settings.ProxyPort}");
                _logger.Debug("Proxy configured without authentication");
            }

            SocketsHttpHandler handler = new()
            {
                Proxy = proxy,
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 10,
                SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }
            };

            HttpClient client = new(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentBuilder.Generate());
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to set user agent header, continuing without it");
            }
            HttpGet.HttpClient = client;
        }
    }
}