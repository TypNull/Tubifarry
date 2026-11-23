#if !MASTER_BRANCH
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tubifarry.Core.Telemetry
{
    public static class TubifarrySentry
    {
        private static IDisposable? _sdk;
        private static bool _initialized;
        private static bool _disabled;
        private static readonly Lock _lock = new();

        public static bool IsEnabled => _initialized && _sdk != null && !_disabled;

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized)
                    return;

                _initialized = true;

                if (!PluginInfo.CI || Debugger.IsAttached)
                {
                    _disabled = true;
                    return;
                }

                if (string.IsNullOrEmpty(PluginInfo.SentryDsn))
                {
                    _disabled = true;
                    return;
                }

                try
                {
                    _sdk = SentrySdk.Init(o =>
                    {
                        o.Dsn = PluginInfo.SentryDsn;
                        o.Release = $"tubifarry@{PluginInfo.InformationalVersion}";
                        o.Environment = GetEnvironment();
                        o.AttachStacktrace = true;
                        o.MaxBreadcrumbs = 100;
                        o.AutoSessionTracking = false;
                        o.IsGlobalModeEnabled = false;
                        o.TracesSampleRate = 0.1;
                        o.ProfilesSampleRate = 0;
                        o.SendDefaultPii = false;

                        o.SetBeforeSend((evt, hint) => SentryEventFilter.FilterEvent(evt, hint));
                    });

                    ConfigureDefaultScope();
                }
                catch (Exception)
                {
                    _sdk = null;
                    _disabled = true;
                }
            }
        }

        private static string GetEnvironment()
        {
            if (RuntimeInfo.IsTesting)
                return "testing";

            if (RuntimeInfo.IsDevelopment || Debugger.IsAttached)
                return "development";

            return PluginInfo.Branch;
        }

        private static void ConfigureDefaultScope()
        {
            try
            {
                SentrySdk.ConfigureScope(scope =>
                {
                    scope.User = new SentryUser
                    {
                        Id = HashUtil.AnonymousToken()
                    };
                    scope.Contexts.App.Name = "Tubifarry";
                    scope.Contexts.App.Version = PluginInfo.Version;
                    scope.Contexts.App.Build = PluginInfo.GitCommit;
                    scope.SetTag("branch", PluginInfo.Branch);
                    scope.SetTag("plugin_version", PluginInfo.Version);
                    scope.SetTag("lidarr_version", BuildInfo.Version.ToString());
                    scope.SetTag("runtime_identifier", RuntimeInformation.RuntimeIdentifier);
                    scope.SetTag("culture", Thread.CurrentThread.CurrentCulture.Name);
                    scope.SetTag("ci_build", PluginInfo.CI.ToString());
                });
            }
            catch { }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    _sdk?.Dispose();
                }
                catch { }

                _sdk = null;
                _initialized = false;
                _disabled = false;
            }
        }

        public static void ConfigureScope(Action<Scope> configure)
        {
            if (!IsEnabled)
                return;

            try
            {
                SentrySdk.ConfigureScope(configure);
            }
            catch { }
        }
    }
}
#endif
