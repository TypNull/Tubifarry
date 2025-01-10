Here’s the complete README with the added short notice under the **Troubleshooting** section:
# Tubifarry for Lidarr 🎶  
![Downloads](https://img.shields.io/github/downloads/TypNull/Tubifarry/total)  ![GitHub release (latest by date)](https://img.shields.io/github/v/release/TypNull/Tubifarry)  ![GitHub last commit](https://img.shields.io/github/last-commit/TypNull/Tubifarry)  ![License](https://img.shields.io/github/license/TypNull/Tubifarry)  ![GitHub stars](https://img.shields.io/github/stars/TypNull/Tubifarry)  

Tubifarry is a versatile plugin for **Lidarr** that enhances your music library by fetching metadata from **Spotify** and enabling direct music downloads from **YouTube**. While it is not explicitly a Spotify-to-YouTube downloader, it leverages the YouTube API to seamlessly integrate music downloads into your Lidarr setup. Built on the foundation of trevTV's projects, Tubifarry also supports **Slskd**, the Soulseek client, as both an **indexer** and **downloader**, allowing you to tap into the vast music collection available on the Soulseek network. 🛠️  

Additionally, Tubifarry supports fetching soundtracks from **Sonarr** (series) and **Radarr** (movies) and adding them to Lidarr using the **Arr-Soundtracks** import list feature. This makes it easy to manage and download soundtracks for your favorite movies and TV shows. 🎬🎵  

---

## Installation 🚀  
To use Tubifarry, ensure your Lidarr setup is on the `plugins` branch. Follow the steps below to get started.  

### Docker Setup (Hotio Image) 🐳  
For Docker users using Hotio's image, use the following path:  
```yml  
    image: ghcr.io/hotio/lidarr:pr-plugins  
```  

### Non-Docker Installation  
To switch to the Plugins Branch:  
1. Open Lidarr and navigate to `System -> General`.  
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
   - **Include Only Audio Files**: Enable to filter search results to audio files only (beta).  

#### **Setting Up the Soulseek Download Client**:  
1. Go to `Settings -> Download Clients` and click **Add**.  
2. Select `Slskd` from the list of download clients.  
3. Set the **download path** where downloaded files will be downloaded.  

---

### YouTube Downloader Setup 🎥  
Tubifarry allows you to download music directly from YouTube. Follow the steps below to configure the YouTube downloader.  

#### **Setting Up the YouTube Download Client**:  
1. Go to `Settings -> Download Clients` and click **Add**.  
2. Select `Youtube` from the list of download clients.  
3. Set the download path and adjust other settings as needed.  
4. **Optional**: If using FFmpeg, ensure the FFmpeg path is correctly configured.  

#### **FFmpeg and Audio Conversion**:  
1. **FFmpeg**: FFmpeg can be used to extract audio from downloaded files, which are typically embedded in MP4 containers. If you choose to use FFmpeg, ensure it is installed and accessible in your system's PATH or the specified FFmpeg path. If not, the plugin does attempt to download it automatically during setup. Without FFmpeg, songs will be downloaded in their original format, which may not require additional processing.  

   **Important Note**: If FFmpeg is not used, Lidarr may incorrectly interpret the MP4 container as corrupt. While FFmpeg usage is **recommended**, it is not strictly necessary. However, to avoid potential issues, you can choose to extract audio without re-encoding, but this may lead to better compatibility with Lidarr.

2. **Max Audio Quality**: Tubifarry supports a maximum audio quality of **256kb/s AAC** for downloaded files through YouTube. While most files are in 128kbps AAC by default, they can be converted to higher-quality formats like **AAC, Opus or MP3v2** if FFmpeg is used.  

   **What is AAC?**  
   AAC (Advanced Audio Coding) is a high-quality audio format that offers better sound quality than MP3 at similar bitrates. It is commonly used in MP4 containers, making it a versatile and widely supported format.  

   **Note**: For higher-quality audio (e.g., 256kb/s), you need a **YouTube Premium subscription**.  

3. **Saving .lrc Files**:  
   If you want to save **.lrc files** (lyric files), navigate to **Media Management > Advanced Settings > Import Extra Files** and add `lrc` to the list of supported file types. This ensures that lyric files are imported and saved alongside your music files.  

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

### Troubleshooting 🛠️  
- **Slskd Download Path Permissions**: Ensure Lidarr has read/write access to the Slskd download path. Verify folder permissions and ensure the user running Lidarr has the necessary access. For Docker setups, confirm the volume is correctly mounted and permissions are set.  
- **Optional: FFmpeg Issues**: If you choose to use FFmpeg and songs fail to process, verify that FFmpeg is correctly installed and accessible in your system's PATH. If not, try reinstalling or downloading it manually.  
- **Metadata Issues**: If metadata is not being added to downloaded files, confirm that the files are in a supported format. If using FFmpeg, ensure it is extracting audio to formats like AAC embedded in MP4 containers (check debug logs).  
- **No Release Found**: If no release is found, YouTube might flag the plugin as a bot (which it technically is). To avoid this and access higher-quality audio, you can log in using cookies.  
  - **Steps to Use Cookies**:  
    1. Install the **cookies.txt** extension for your browser:  
       - [Get cookies.txt for Chrome](https://chrome.google.com/webstore/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc)  
       - [Get cookies.txt for Firefox](https://addons.mozilla.org/en-US/firefox/addon/cookies-txt/)  
    2. Log in to YouTube and save the cookies.txt file in a folder accessible by Lidarr.  
    3. Go to the **Indexer and Downloader** settings in Lidarr and add the file path to the cookies.txt file.  

---

## Acknowledgments 🙌  
Special thanks to [**trevTV**](https://github.com/TrevTV) for laying the groundwork with his plugins. Additionally, thanks to [**IcySnex**](https://github.com/IcySnex) for providing the YouTube API. 🎉  

---

## Contributing 🤝  
If you'd like to contribute to Tubifarry, feel free to open issues or submit pull requests on the [GitHub repository](https://github.com/TypNull/Tubifarry). Your feedback and contributions are highly appreciated!  

---

## License 📄  
Tubifarry is licensed under the MIT License. See the [LICENSE](https://github.com/TypNull/Tubifarry/blob/master/LICENSE) file for more details.  

---

Enjoy seamless music downloads with Tubifarry! 🎧