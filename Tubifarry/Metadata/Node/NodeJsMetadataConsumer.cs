// NodeJsMetadataConsumer.cs
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.NodeBridge;

namespace Tubifarry.Metadata.Node
{
    /// <summary>
    /// Consumes metadata for music files using Node.js scripts and the Node.js bridge.
    /// </summary>
    public class NodeJsMetadataConsumer : MetadataBase<NodeJsMetadataConsumerSettings>, IProvider
    {
        private readonly Logger _logger;
        private readonly INodeJsBridge _nodeJsBridge;
        private bool _isNodeJsInitialized;

        public override string Name => "Node.js Metadata Processor";

        /// <summary>
        /// Initializes a new instance of the NodeJsMetadataConsumer class.
        /// </summary>
        public NodeJsMetadataConsumer(INodeJsBridge nodeJsBridge, Logger logger)
        {
            _logger = logger;
            _nodeJsBridge = nodeJsBridge;
        }

        ValidationResult IProvider.Test()
        {
            _logger.Info("Testing Node.js metadata consumer");
            _ = EnsureNodeJsInitializedAsync();
            return new();
        }

        /// <summary>
        /// Initialize Node.js bridge and ensure required packages are installed
        /// </summary>
        private async Task EnsureNodeJsInitializedAsync()
        {
            if (_isNodeJsInitialized)
                return;

            _logger.Debug("NodeJs: Initializing Node.js bridge");

            // Get required packages from settings
            string[] requiredPackages = Settings.RequiredPackages
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToArray();

            // Always include common metadata packages
            string[] defaultPackages = new[] { "fs-extra", "path" };
            requiredPackages = requiredPackages.Concat(defaultPackages).Distinct().ToArray();

            bool initialized = await _nodeJsBridge.InitializeAsync(requiredPackages);

            if (!initialized)
            {
                _logger.Error("NodeJs: Failed to initialize Node.js bridge");
                throw new InvalidOperationException("Failed to initialize Node.js bridge");
            }

            _logger.Debug($"NodeJs: Successfully initialized Node.js {_nodeJsBridge.NodeVersion}");
            _isNodeJsInitialized = true;
        }

        /// <summary>
        /// Process an album folder with Node.js script
        /// </summary>
        private async Task<bool> ProcessAlbumWithNodeJsAsync(Artist artist, Album album, string albumPath)
        {
            try
            {
                await EnsureNodeJsInitializedAsync();

                _logger.Info($"NodeJs: Processing album {album.Title} by {artist.Name} at {albumPath}");

                if (Directory.GetFiles(albumPath).Length == 0)
                {
                    _logger.Warn($"NodeJs: Directory is empty, nothing to process: {albumPath}");
                    return true;
                }

                // Check if custom script exists
                if (!string.IsNullOrEmpty(Settings.ScriptPath) && File.Exists(Settings.ScriptPath))
                {
                    return await ExecuteCustomScriptAsync(artist, album, albumPath);
                }
                else
                {
                    return await ExecuteInlineScriptAsync(artist, album, albumPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "NodeJs: Error processing album");
                return false;
            }
        }

        /// <summary>
        /// Execute a custom Node.js script file
        /// </summary>
        private async Task<bool> ExecuteCustomScriptAsync(Artist artist, Album album, string albumPath)
        {
            try
            {
                _logger.Debug($"NodeJs: Executing custom script: {Settings.ScriptPath}");

                // Prepare arguments for the script
                string arguments = $"--artist \"{artist.Name}\" --album \"{album.Title}\" --path \"{albumPath}\" --output \"{Settings.OutputPath}\"";

                NodeExecutionResult result = await _nodeJsBridge.ExecuteScriptAsync(Settings.ScriptPath, arguments);

                if (result.Success)
                {
                    _logger.Info("NodeJs: Successfully executed custom script");
                    _logger.Debug($"NodeJs script output: {result.StandardOutput}");
                    return true;
                }
                else
                {
                    _logger.Error($"NodeJs: Failed to execute script: {result.StandardError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "NodeJs: Error executing custom script");
                return false;
            }
        }

        /// <summary>
        /// Execute inline Node.js code for metadata processing
        /// </summary>
        private async Task<bool> ExecuteInlineScriptAsync(Artist artist, Album album, string albumPath)
        {
            try
            {
                _logger.Debug("NodeJs: Executing inline metadata processing script");

                // Build Node.js code for metadata processing
                string nodeJsCode = $@"
const fs = require('fs-extra');
const path = require('path');

// Album information
const albumInfo = {{
    artist: '{artist.Name.Replace("'", "\\'")}',
    album: '{album.Title.Replace("'", "\\'")}',
    albumPath: '{albumPath.Replace("'", "\\'")}',
    outputPath: '{Settings.OutputPath.Replace("'", "\\'")}'
}};

console.log('Processing album:', albumInfo.album, 'by', albumInfo.artist);
console.log('Album path:', albumInfo.albumPath);

try {{
    // Check if directory exists
    if (!fs.existsSync(albumInfo.albumPath)) {{
        console.error('Album directory does not exist:', albumInfo.albumPath);
        process.exit(1);
    }}

    // Read directory contents
    const files = fs.readdirSync(albumInfo.albumPath);
    const audioFiles = files.filter(file => 
        /\.(mp3|flac|m4a|ogg|wav)$/i.test(file)
    );

    console.log(`Found ${{audioFiles.length}} audio files in album directory`);

    // Process each audio file
    audioFiles.forEach((file, index) => {{
        const filePath = path.join(albumInfo.albumPath, file);
        const stats = fs.statSync(filePath);
        
        console.log(`[${{index + 1}}/${{audioFiles.length}}] ${{file}} (${{(stats.size / 1024 / 1024).toFixed(2)}} MB)`);
        
        // Here you would add your metadata processing logic
        // For example: extract tags, validate metadata, generate thumbnails, etc.
    }});

    // Create output directory if specified
    if (albumInfo.outputPath && albumInfo.outputPath !== '') {{
        fs.ensureDirSync(albumInfo.outputPath);
        
        // Example: Create a metadata summary file
        const metadataFile = path.join(albumInfo.outputPath, `${{albumInfo.artist}} - ${{albumInfo.album}}.json`);
        const metadata = {{
            artist: albumInfo.artist,
            album: albumInfo.album,
            path: albumInfo.albumPath,
            fileCount: audioFiles.length,
            files: audioFiles,
            processedAt: new Date().toISOString()
        }};
        
        fs.writeFileSync(metadataFile, JSON.stringify(metadata, null, 2));
        console.log('Metadata summary written to:', metadataFile);
    }}

    console.log('Node.js metadata processing completed successfully');

}} catch (error) {{
    console.error('Error processing album:', error.message);
    process.exit(1);
}}
";

                NodeExecutionResult result = await _nodeJsBridge.ExecuteCodeAsync(nodeJsCode);

                if (result.Success)
                {
                    _logger.Info("NodeJs: Successfully processed album with inline script");
                    _logger.Debug($"NodeJs output: {result.StandardOutput}");
                    return true;
                }
                else
                {
                    _logger.Error($"NodeJs: Failed to process album: {result.StandardError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "NodeJs: Error executing inline script");
                return false;
            }
        }

        /// <summary>
        /// Test Node.js functionality with a simple script
        /// </summary>
        private async Task<bool> TestNodeJsFunctionalityAsync()
        {
            try
            {
                await EnsureNodeJsInitializedAsync();

                _logger.Info("NodeJs: Testing Node.js functionality");

                string testCode = @"
console.log('Node.js Test Script');
console.log('Node.js version:', process.version);
console.log('Platform:', process.platform);
console.log('Architecture:', process.arch);
console.log('Current working directory:', process.cwd());

// Test basic file system operations
const fs = require('fs');
const path = require('path');

try {
    const tempFile = path.join(process.cwd(), 'nodejs-test.txt');
    fs.writeFileSync(tempFile, 'Node.js bridge test successful!');
    const content = fs.readFileSync(tempFile, 'utf8');
    console.log('File operation test:', content);
    fs.unlinkSync(tempFile);
    console.log('Node.js test completed successfully');
} catch (error) {
    console.error('File operation failed:', error.message);
}
";

                NodeExecutionResult result = await _nodeJsBridge.ExecuteCodeAsync(testCode);

                if (result.Success)
                {
                    _logger.Info("NodeJs: Test script executed successfully");
                    _logger.Debug($"NodeJs test output: {result.StandardOutput}");
                    return true;
                }
                else
                {
                    _logger.Error($"NodeJs: Test script failed: {result.StandardError}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "NodeJs: Error testing functionality");
                return false;
            }
        }

        // Metadata interface implementation
        public override MetadataFile? FindMetadataFile(Artist artist, string path) => null;

        public override MetadataFileResult? ArtistMetadata(Artist artist)
        {
            _logger.Info($"NodeJs: Processing artist metadata for {artist.Name}");
            // Test Node.js functionality when processing artist
            bool success = TestNodeJsFunctionalityAsync().GetAwaiter().GetResult();
            if (success)
                _logger.Debug($"NodeJs: Successfully tested functionality for artist {artist.Name}");
            else
                _logger.Warn($"NodeJs: Failed to test functionality for artist {artist.Name}");
            return null;
        }

        public override MetadataFileResult? AlbumMetadata(Artist artist, Album album, string albumPath)
        {
            _logger.Info($"NodeJs: Processing album metadata for {album.Title}");
            bool success = ProcessAlbumWithNodeJsAsync(artist, album, albumPath).GetAwaiter().GetResult();
            if (success)
                _logger.Debug($"NodeJs: Successfully processed album {album.Title}");
            else
                _logger.Warn($"NodeJs: Failed to process album {album.Title}");
            return null;
        }

        public override MetadataFileResult? TrackMetadata(Artist artist, TrackFile trackFile)
        {
            _logger.Info($"NodeJs: Processing track metadata for {trackFile.Path}");

            // For track processing, we can execute a simpler script
            Task.Run(async () =>
            {
                try
                {
                    await EnsureNodeJsInitializedAsync();

                    string trackCode = $@"
const path = require('path');
const fs = require('fs');

const trackInfo = {{
    artist: '{artist.Name.Replace("'", "\\'")}',
    trackPath: '{trackFile.Path.Replace("'", "\\'")}',
    fileName: path.basename('{trackFile.Path.Replace("'", "\\'")}')
}};

console.log('Processing track:', trackInfo.fileName);
console.log('Artist:', trackInfo.artist);
console.log('Track path:', trackInfo.trackPath);

if (fs.existsSync(trackInfo.trackPath)) {{
    const stats = fs.statSync(trackInfo.trackPath);
    console.log('File size:', (stats.size / 1024 / 1024).toFixed(2), 'MB');
    console.log('Track processing completed');
}} else {{
    console.error('Track file not found:', trackInfo.trackPath);
}}
";

                    NodeExecutionResult result = await _nodeJsBridge.ExecuteCodeAsync(trackCode);
                    if (result.Success)
                    {
                        _logger.Debug($"NodeJs: Successfully processed track {trackFile.Path}");
                    }
                    else
                    {
                        _logger.Warn($"NodeJs: Failed to process track {trackFile.Path}: {result.StandardError}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"NodeJs: Error processing track {trackFile.Path}");
                }
            });

            return null;
        }

        public override List<ImageFileResult> ArtistImages(Artist artist) => new();

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => new();

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }
}

// NodeJsMetadataConsumerSettings.cs


// Example custom Node.js script (save as metadata-processor.js)
/*

*/