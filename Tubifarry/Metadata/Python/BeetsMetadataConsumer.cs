using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;
using Python.Runtime;
using Tubifarry.Core.PythonBridge;

namespace Tubifarry.Metadata.Python
{
    public class BeetsMetadataConsumer : MetadataBase<BeetsMetadataConsumerSettings>, IProvider
    {
        private readonly Logger _logger;
        private readonly IPythonBridge _pythonBridge;
        private bool _isPythonInitialized;

        public override string Name => "Beets";

        public BeetsMetadataConsumer(IPythonBridge pythonBridge, Logger logger)
        {
            _logger = logger;
            _pythonBridge = pythonBridge;
        }

        /// <summary>
        /// Initialize Python bridge and ensure Beets is installed
        /// </summary>
        private async Task EnsurePythonInitializedAsync()
        {
            if (_isPythonInitialized)
                return;

            _logger.Debug("Beets: Initializing Python bridge");

            // Get required packages from settings
            string[] requiredPackages = Settings.RequiredPackages
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToArray();

            requiredPackages = requiredPackages.Append("beets").ToArray();

            bool initialized = await _pythonBridge.InitializeAsync(requiredPackages);

            if (!initialized)
            {
                _logger.Error("Beets: Failed to initialize Python bridge");
                throw new InvalidOperationException("Failed to initialize Python bridge");
            }

            _logger.Debug($"Beets: Successfully initialized Python {_pythonBridge.PythonVersion}");
            _isPythonInitialized = true;
        }

        /// <summary>
        /// Process an album folder with Beets using Python.NET directly
        /// </summary>
        private async Task<bool> ProcessAlbumWithBeetsAsync(Artist artist, Album album, string albumPath)
        {
            try
            {
                await EnsurePythonInitializedAsync();

                _logger.Info($"Beets: Processing album {album.Title} by {artist.Name} at {albumPath}");

                if (Directory.GetFiles(albumPath).Length == 0)
                {
                    _logger.Warn($"Beets: Directory is empty, nothing to process: {albumPath}");
                    return true;
                }

                _pythonBridge.OutLogger.OnOutputWritten += OutLogger_OnOutputWritten;


                string libraryPath = Settings.LibraryPath;
                if (Directory.Exists(libraryPath))
                {
                    libraryPath = Path.Combine(libraryPath, "beets.db");
                    _logger.Debug($"Beets: Using database file at {libraryPath}");
                }

                using (Py.GIL())
                {
                    _logger.Trace("Beets: Acquired Python GIL");
                    dynamic sys = Py.Import("sys");
                    dynamic os = Py.Import("os");
                    dynamic beetslib = Py.Import("beets.ui");

                    PyList args = new();
                    args.Append(new PyString("beet"));
                    args.Append(new PyString("-c"));
                    args.Append(new PyString(Settings.ConfigPath));
                    args.Append(new PyString("-l"));
                    args.Append(new PyString(libraryPath));
                    args.Append(new PyString("-d"));
                    args.Append(new PyString(albumPath));
                    args.Append(new PyString("import"));

                    args.Append(new PyString("-C")); // Always use -C to prevent file movement
                    args.Append(new PyString(albumPath));

                    _logger.Debug($"Beets: Executing command with args: {string.Join(" ", args)}");
                    sys.argv = args;

                    try
                    {
                        beetslib.main();
                        _logger.Info("Beets: Successfully processed album");
                        return true;
                    }
                    catch (PythonException ex)
                    {
                        _logger.Error($"Beets: Python exception: {ex.Message}");
                        _logger.Debug($"Beets: Python exception details: {ex}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Beets: Error processing album");
                return false;

            }
            finally
            {
                _pythonBridge.OutLogger.OnOutputWritten -= OutLogger_OnOutputWritten;
            }
        }

        private void OutLogger_OnOutputWritten(object? sender, string e) => _logger.Trace(e);

        public override MetadataFile? FindMetadataFile(Artist artist, string path) => null;

        public override MetadataFileResult? ArtistMetadata(Artist artist) => null;

        public override MetadataFileResult? AlbumMetadata(Artist artist, Album album, string albumPath)
        {
            bool success = ProcessAlbumWithBeetsAsync(artist, album, albumPath).GetAwaiter().GetResult();
            if (success)
                _logger.Debug($"Beets: Successfully processed album {album.Title}");
            else
                _logger.Warn($"Beets: Failed to process album {album.Title}");
            return null;
        }

        public override MetadataFileResult? TrackMetadata(Artist artist, TrackFile trackFile) => null;
        public override List<ImageFileResult> ArtistImages(Artist artist) => new();
        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => new();
        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }
}