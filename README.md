# Tubifarry for Lidarr 🎶
![Downloads](https://img.shields.io/github/downloads/TypNull/Tubifarry/total)  ![GitHub release (latest by date)](https://img.shields.io/github/v/release/TypNull/Tubifarry)  ![GitHub last commit](https://img.shields.io/github/last-commit/TypNull/Tubifarry)  ![License](https://img.shields.io/github/license/TypNull/Tubifarry)  ![GitHub stars](https://img.shields.io/github/stars/TypNull/Tubifarry)

Tubifarry is a plugin for **Lidarr** that adds multiple music sources to your library management. It uses **Spotify's catalog** as an [indexer](https://wiki.servarr.com/en/lidarr/supported#indexers) to search for music, then downloads the actual audio files from **YouTube**. Tubifarry also supports **Slskd**, the Soulseek client, as both an **indexer** and **downloader**, allowing you to tap into the vast music collection available on the Soulseek network. 🛠️

Additionally, Tubifarry supports fetching soundtracks from **Sonarr** (series) and **Radarr** (movies) and adding them to Lidarr using the **Arr-Soundtracks** import list feature. This makes it easy to manage and download soundtracks for your favorite movies and TV shows. 🎬🎵
For further customization, Codec Tinker lets you convert audio files between formats using FFmpeg, helping you optimize your library.⚙️

> **Note**: Some details in this documentation may vary from the current implementation.

---

## Table of Contents 📑

1. [Installation 🚀](#installation-)
2. [Soulseek (Slskd) Setup 🎧](#soulseek-slskd-setup-)
3. [YouTube Downloader Setup 🎥](#youtube-downloader-setup-)
4. [WebClients 📻](#web-clients-)
5. [Fetching Soundtracks 🎬🎵](#fetching-soundtracks-from-sonarr-and-radarr-)
6. [Queue Cleaner 🧹](#queue-cleaner-)
7. [Codec Tinker 🎛️](#codec-tinker-️)
8. [Lyrics Fetcher 📜](#lyrics-fetcher-)
9. [Search Sniper 🏹](#search-sniper-)
10. [Custom Metadata Sources 🧩](#custom-metadata-sources-)
11. [Similar Artists 🧷](#similar-artists-)
12. [Troubleshooting 🛠️](#troubleshooting-%EF%B8%8F)

----

## Installation 🚀
To use Tubifarry, ensure your Lidarr setup is on the `plugins` branch. Follow the steps below to get started.

### Docker Setup (Hotio Image) 🐳
For Docker users using Hotio's image, use the following path:
```yml
image: ghcr.io/hotio/lidarr:pr-plugins
```

### Non-Docker Installation
To switch to the Plugins Branch:
1. Open Lidarr and navigate to `Settings -> General`.
2. Scroll down to the **Branch** section.
3. Replace "master" with "plugins".
4. Force an update check to update Lidarr to the plugins branch.

---

### Plugin Installation 📥
- In Lidarr, go to `System -> Plugins`.
- Paste `https://github.com/TypNull/Tubifarry` into the GitHub URL box and click **Install**.

---

### Soulseek (Slskd) Setup 🎧
Tubifarry supports **Slskd**, the Soulseek client, as both an **indexer** and **downloader**. Follow the steps below to configure it.

#### **Setting Up the Soulseek Indexer**:
1. Navigate to `Settings -> Indexers` and click **Add**.
2. Select `Slskd` from the list of indexers.
3. Configure the following settings:
   - **URL**: The URL of your Slskd instance (e.g., `http://localhost:5030`).
   - **API Key**: The API key for your Slskd instance (found in Slskd's settings under 'Options').
   - **Include Only Audio Files**: Enable to filter search results to audio files only.

#### **Setting Up the Soulseek Download Client**:
1. Go to `Settings -> Download Clients` and click **Add**.
2. Select `Slskd` from the list of download clients.
3. The download path is fetched from slskd, if it does not match use `Remote Path` settings.

---

### YouTube Downloader Setup 🎥 
> #### YouTube Bot Detection ⚠️
> YouTube actively detects and blocks automated downloaders. To bypass this, configure the Trusted Session Generator and provide cookie authentication (see setup steps below).

The YouTube downloader extracts audio from YouTube and converts them to audio files using FFmpeg. 

#### **Configure the Indexer**:
1. Navigate to `Settings -> Indexers` and click **Add**.
2. In the modal, select `Tubifarry` (located under **Other** at the bottom).

#### **Setting Up the YouTube Download Client**:
1. Go to `Settings -> Download Clients` and click **Add**.
2. Select `Youtube` from the list of download clients.
3. Set the download path and adjust other settings as needed.
4. **Optional**: If using FFmpeg, ensure the FFmpeg path is correctly configured.

#### **FFmpeg and Audio Conversion**:
1. **FFmpeg**: Required to extract audio from YouTube. The plugin will attempt to download FFmpeg automatically if not found. Without FFmpeg, files will be downloaded in their original format, which Lidarr may cannot properly import.
   - Ensure FFmpeg is in your system PATH or specify its location in settings
   - Used for extracting audio tracks and converting between formats

2. **Audio Quality**: YouTube provides audio at various bitrates:
   - Standard quality: 128kbps AAC (free users)
   - High quality: 256kbps AAC (YouTube Premium required)
   - The plugin can convert to other formats (MP3, Opus) using FFmpeg

---

### Web Clients 📻

Tubifarry supports multiple web clients. These are web services that provide music. Some work better than others and Tubifarry is not responsible for the uptime or stability of these services.

##### Supported Clients
- **Lucida** - A music downloading service that supports multiple sources.
- **DABmusic** - A high-resolution audio streaming platform.
- **Subsonic** - A music streaming API standard with broad compatibility

All clients share the same base architecture, making it relatively straightforward to add new ones. The Subsonic Indexer and Client is a generic client, making it possible for any online service to connect with it. The Subsonic specifications are documented on the [API page](https://www.subsonic.org/pages/api.jsp).

If you have a suggestion to add a web client and the service does not want to support Subsonic as a generic indexer, please open a feature request.

---

### Fetching Soundtracks from Sonarr and Radarr 🎬🎵
Tubifarry also supports fetching soundtracks from **Sonarr** (for TV series) and **Radarr** (for movies) and adding them to Lidarr using the **Arr-Soundtracks** import list feature. This allows you to easily manage and download soundtracks for your favorite movies and TV shows.

To enable this feature:
1. **Set Up the Import List**:
   - Navigate to `Settings -> Import Lists` in Lidarr.
   - Add a new import list and select the option for **Arr-Soundtracks**.
   - Configure the settings to match your Sonarr and Radarr instances.
   - Provide a cache path to store responses from MusicBrainz for faster lookups.

2. **Enjoy Soundtracks**:
   - Once configured, Tubifarry will automatically fetch soundtracks from your Sonarr and Radarr libraries and add them to Lidarr for download and management.

---

### Queue Cleaner 🧹

The **Queue Cleaner** automatically handles downloads that fail to import into your library. When Lidarr can't import a download (due to missing tracks, incorrect metadata, etc.), Queue Cleaner can rename files based on their embedded tags, retry the import, blocklist the release, or remove the files.

1. **Key Options**:
   - *Blocklist*: Choose to remove, blocklist, or both for failed imports.
   - *Rename*: Automatically rename album folders and tracks using available metadata.
   - *Clean Imports*: Decide when to clean—when tracks are missing, metadata is incomplete, or always.
   - *Retry Finding Release*: Automatically retry searching for a release if the import fails.

2. **How to Enable**:
   - Navigate to `Settings -> Connect` in Lidarr.
   - Add a new connection and select the **Queue Cleaner**.
   - Configure the settings to match your needs.

---

### Codec Tinker 🎛️

**Codec Tinker** automatically converts audio files between formats using FFmpeg when they're imported into your library. You can set up rules to convert specific formats (e.g., convert all WAV files to FLAC, or convert high-bitrate AAC to MP3). Note: Lossy formats (MP3, AAC) cannot be converted to lossless formats (FLAC, WAV) as quality cannot be restored.

#### How to Enable Codec Tinker

1. Go to `Settings > Metadata` in Lidarr.
2. Open the **Codec Tinker** MetadataConsumer.
3. Toggle the switch to enable the feature.

#### How to Use Codec Tinker

1. **Set Default Conversion Settings**
   - **Target Format**:
     Choose the default format for conversions (e.g., FLAC, Opus, MP3).

   - **Custom Conversion Rules**:
     Define rules like `wav -> flac`, `AAC>=256k -> MP3:300k` or `all -> alac` for more specific conversions.

   - **Custom Conversion Rules On Artists**:
     Define tags like `opus-192` for one specific conversion on all albums of an artist.

   **Note**: Lossy formats (e.g., MP3, AAC) cannot be converted to non-lossy formats (e.g., FLAC, WAV).

2. **Enable Format-Specific Conversion**
   Toggle checkboxes or use custom rules to enable conversion for specific formats:
   - **Convert MP3**, **Convert FLAC**, etc.

---

###  Lyrics Fetcher 📜

**Lyrics Fetcher** automatically downloads lyrics for your music files. It fetches synchronized lyrics from LRCLIB and plain lyrics from Genius. Lyrics can be saved as separate .lrc files and embedded directly into the audio files' metadata.

#### How to Enable Lyrics Fetcher

1. Go to `Settings > Metadata` in Lidarr.
2. Open the **Lyrics Fetcher** MetadataConsumer.
3. Toggle the switch to enable the feature.

#### How to Use Lyrics Fetcher

You can configure the following options:

- **Create LRC Files**: Enables creating external `.lrc` files that contain time-synced lyrics.
- **Embed Lyrics in Audio Files**: Instead of (or in addition to) creating separate LRC files, this option embeds the lyrics directly into the audio file's.
- **Overwrite Existing LRC Files**: When enabled, this will replace any existing LRC files with newly downloaded ones.

---

### Search Sniper 🏹

**Search Sniper** automatically triggers searches for missing albums in your wanted list. Instead of searching for everything at once, which can overload indexers, it randomly selects a few albums from your wanted list at regular intervals and searches for them. It keeps track of what has been searched recently to avoid repeating searches too often. 
Search Sniper can be triggered manually from the Tasks tab.

#### How to Enable Search Sniper
1. Go to `Settings > Metadata` in Lidarr.
2. Open the **Search Sniper** option.
3. Configure the following options:
   - **Picks Per Interval**: How many items to search each cycle
   - **Min Refresh Interval**: How often to run searches
   - **Cache Type**: Memory or Permanent
   - **Cache Retention Time**: Days to keep cache
   - **Pause When Queued**: Stop when queue reaches this number
   - **Search Options**: Enable at least one - Missing albums, Missing tracks, or Cutoff not met

---

###  Custom Metadata Sources 🧩

Tubifarry can fetch metadata from additional sources beyond MusicBrainz, including **Discogs**, **Deezer**, and **Last.fm**. These sources can provide additional artist information, album details, and cover art when MusicBrainz data is incomplete. The MetaMix feature intelligently combines data from multiple sources to create more complete metadata profiles.

#### How to Enable Individual Metadata Sources

1. Go to `Settings > Metadata` in Lidarr.
2. Open a specific **Metadata Source**.
3. Toggle the switch to enable the feature.
4. Configure the required settings:
   - **User Agent**: Set a custom identifier that follows the format "Name/Version" to help the metadata service identify your requests properly.
   - **API Key**: Enter your personal access token or API key for the service.
   - **Caching Method**: Choose between:
     - **Memory Caching**: Faster but less persistent (only recommended if your system has been running stably for 5+ days)
     - **Permanent Caching**: More reliable but requires disk storage
   - **Cache Directory**: If using Permanent caching, specify a folder where metadata can be stored to reduce API calls.

#### How to Enable Multiple Metadata Sources

MetaMix is an advanced feature that intelligently combines metadata from multiple sources to create more complete artist profiles. It can fill gaps in one source with information from another, resulting in a more comprehensive music library.

1. Go to `Settings > Metadata` in Lidarr. 
2. Open the **MetaMix** settings.
3. Configure the following options:
   - **Priority Rules**: Establish a hierarchy among your metadata sources. For example, set MusicBrainz as primary and Discogs as secondary. Lower numbers indicate higher priority.
   - **Dynamic Threshold**: Controls how aggressively MetaMix switches between sources:
     - Higher values make MetaMix more willing to use lower-priority sources
     - Lower values make MetaMix stick more closely to your primary source
   - **Multi-Source Population**: When enabled, missing album information from your primary source will be automatically supplemented with data from secondary sources.

The feature currently works best with artists that are properly linked across different metadata systems. Which is typically the case on MusicBrainz.

---

### Similar Artists 🧷

**Similar Artists** lets you discover related artists using Last.fm's
recommendation data directly in Lidarr's search. Search for an artist
with the `~` prefix and get back a list of similar musicians ready to
be added to your library.

#### How to Enable Similar Artists

1. Go to `Settings > Metadata` in Lidarr.
2. Enable these three metadata sources:
   - **Similar Artists** - Enter your Last.fm API key
   - **Lidarr Default** - Required to handle normal searches
   - **MetaMix** - Required to coordinate the search
3. Optional: Adjust result limit, enable image fetching, and configure caching.

**Examples:**
- `similar:Pink Floyd`
- `~20244d07-534f-4eff-b4d4-930878889970`

---

## Troubleshooting 🛠️

- **Slskd Download Path Permissions**:
  Ensure Lidarr has read/write access to the Slskd download path. Verify folder permissions and confirm the user running Lidarr has the necessary access. For Docker setups, double-check that the volume is correctly mounted and permissions are properly configured.

- **FFmpeg Issues (Optional)**:
  If you're using FFmpeg and songs fail to process, ensure FFmpeg is installed correctly and accessible in your system's PATH. If issues persist, try reinstalling FFmpeg or downloading it manually.

- **Metadata Issues**:
  If metadata isn't being added to downloaded files, confirm the files are in a supported format. If using FFmpeg, check that it's extracting audio to compatible formats like AAC embedded in MP4 containers. Review debug logs for further details.

- **No Release Found**:
  If no release is found, YouTube may flag the plugin as a bot. To avoid this and access higher-quality audio, use a combination of cookies and the Trusted Session Generator:
  1. Install the **cookies.txt** extension for your browser:
     - [Chrome](https://chrome.google.com/webstore/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc)
     - [Firefox](https://addons.mozilla.org/en-US/firefox/addon/cookies-txt/)
  2. Log in to YouTube and save the `cookies.txt` file in a folder accessible by Lidarr.
  3. In Lidarr, go to **Indexer and Downloader Settings** and provide the path to the `cookies.txt` file.
  4. **Trusted Session Generator**: Creates authentication tokens that mimic regular browser sessions to bypass YouTube's bot detection.
     - It generates tokens locally, which requires Node.js installed and available in your system's PATH
	 - It can generate tokens using the (bgutil-ytdlp-pot-provider)[https://github.com/Brainicism/bgutil-ytdlp-pot-provider]
  
  The combination of cookies and trusted sessions significantly improves success rates when downloading from YouTube, and can help access higher quality audio streams.

- **No Lyrics Imported**:
  To save `.lrc` files (lyric files), navigate to **Media Management > Advanced Settings > Import Extra Files** and add `lrc` to the list of supported file types. This ensures lyric files are imported and saved alongside your music files.

- **Unsupported Formats**: Verify custom rules and target formats.

--- 

## Acknowledgments 🙌
Special thanks to [**trevTV**](https://github.com/TrevTV) for laying the groundwork with his plugins. Additionally, thanks to [**IcySnex**](https://github.com/IcySnex) for providing the YouTube API. 🎉

---

## Contributing 🤝
If you'd like to contribute to Tubifarry, feel free to open issues or submit pull requests on the [GitHub repository](https://github.com/TypNull/Tubifarry). Your feedback and contributions are highly appreciated!

---

## License 📄
Tubifarry is licensed under the MIT License. See the [LICENSE](https://github.com/TypNull/Tubifarry/blob/master/LICENSE.txt) file for more details.

---

Enjoy seamless music downloads with Tubifarry! 🎧
