using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.PythonBridge;

namespace Tubifarry.Metadata.Python
{
    /// <summary>
    /// Consumes Beets metadata for music files using the Python bridge.
    /// </summary>
    public class BeetsMetadataConsumer : MetadataBase<BeetsMetadataConsumerSettings>, IProvider
    {
        private readonly Logger _logger;
        private readonly IPythonBridge _pythonBridge;
        private bool _isPythonInitialized;

        public override string Name => "Beets";

        /// <summary>
        /// Initializes a new instance of the BeetsMetadataConsumer class.
        /// </summary>
        public BeetsMetadataConsumer(IPythonBridge pythonBridge, Logger logger)
        {
            _logger = logger;
            _pythonBridge = pythonBridge;
        }

        ValidationResult IProvider.Test()
        {
            _logger.Info("Test");
            _ = EnsurePythonInitializedAsync();
            return new();
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

            // Always include beets package
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
        /// Process an album folder with Beets using Python
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

                string libraryPath = Settings.LibraryPath;
                if (Directory.Exists(libraryPath))
                {
                    libraryPath = Path.Combine(libraryPath, "beets.db");
                    _logger.Debug($"Beets: Using database file at {libraryPath}");
                }

                // Build Python code for Beets execution
                string pythonCode = $@"
import sys
import os

try:
    import beets.ui
    
    # Setup arguments for beets
    sys.argv = [
        'beet',
        '-c', r'{Settings.ConfigPath.Replace("'", @"\'")}',
        '-l', r'{libraryPath.Replace("'", @"\'")}',
        '-d', r'{albumPath.Replace("'", @"\'")}',
        'import',
        '-C',  # Always use -C to prevent file movement
        r'{albumPath.Replace("'", @"\'")}'
    ]
    
    print(f'Executing beets with arguments: {{sys.argv}}')
    beets.ui.main()
    print('Beets processing completed successfully')
    
except ImportError as e:
    print(f'Error importing beets: {{e}}')
    sys.exit(1)
except Exception as e:
    print(f'Error processing with beets: {{e}}')
    sys.exit(1)
";

                PythonExecutionResult result = _pythonBridge.ExecuteCode(pythonCode);
                if (result.Success)
                {
                    _logger.Info("Beets: Successfully processed album");
                    _logger.Debug($"Beets output: {result.StandardOutput}");
                    return true;
                }
                else
                {
                    _logger.Error($"Beets: Failed to process album: {result.StandardError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Beets: Error processing album");
                return false;
            }
        }

        // Metadata interface implementation
        public override MetadataFile? FindMetadataFile(Artist artist, string path) => null;

        public override MetadataFileResult? ArtistMetadata(Artist artist) => null;

        public override MetadataFileResult? AlbumMetadata(Artist artist, Album album, string albumPath)
        {
            _logger.Info("AlbumMetadata");
            bool success = ProcessAlbumWithBeetsAsync(artist, album, albumPath).GetAwaiter().GetResult();
            if (success)
                _logger.Debug($"Beets: Successfully processed album {album.Title}");
            else
                _logger.Warn($"Beets: Failed to process album {album.Title}");
            return null;
        }

        public override MetadataFileResult? TrackMetadata(Artist artist, TrackFile trackFile)
        {
            _logger.Info("TrackMetadata");
            //bool success = ProcessAlbumWithBeetsAsync(artist, album, albumPath).GetAwaiter().GetResult();
            //if (success)
            //    _logger.Debug($"Beets: Successfully processed album {album.Title}");
            //else
            //    _logger.Warn($"Beets: Failed to process album {album.Title}");
            return null;
        }

        public override List<ImageFileResult> ArtistImages(Artist artist) => new();

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => new();

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }
}