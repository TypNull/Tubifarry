# Tubifarry MBID Tagging Implementation

## Summary

This PR adds MusicBrainz ID (MBID) tagging to Tubifarry's download flow to improve Lidarr's import matching accuracy. MBID tags provide a +5.0 weight in Lidarr's import scoring, which nearly guarantees import success.

## Problem Solved

Lidarr uses a hardcoded 80% album match threshold for imports. Downloads from streaming sources often produce 60-78% match scores due to metadata variations, causing imports to be rejected. By embedding MBIDs from Lidarr's database directly into downloaded files, we provide unambiguous matching cues that bypass fuzzy matching.

## Implementation

### Approach: RemoteAlbum-based MBIDs (Primary)

This implementation extracts MBIDs that Lidarr already knows from the `RemoteAlbum` object:

- **Zero ambiguity**: Uses the exact release Lidarr matched
- **No network calls**: Extracts from local Lidarr database context
- **Handles alternate editions**: Uses the monitored release preference

### Files Changed

1. **New File: `Tubifarry/Core/Records/MusicBrainzLookup.cs`**
   - `MusicBrainzIds` record for exchanging MBID data
   - `MusicBrainzRelease` and `MusicBrainzTrack` records for API responses
   - `MusicBrainzLookup` class for blind API lookup (available for future use)

2. **Modified: `Tubifarry/Core/Model/AudioMetadataHandler.cs`**
   - Added `mbids` optional parameter to `TryEmbedMetadata()`
   - New `WriteMusicBrainzTags()` helper method
   - Backward compatible: falls back to existing `ForeignRecordingId` behavior if MBIDs not provided

3. **Modified: `Tubifarry/Download/Base/BaseDownloadRequest.cs`**
   - Added `ExtractMusicBrainzIds()` method
   - Extracts MBIDs from `RemoteAlbum` using Lidarr's database context
   - Returns partial MBIDs (album-level) if no monitored release exists

4. **Modified: Provider DownloadRequests**
   - `YouTubeDownloadRequest.cs`
   - `TripleTripleDownloadRequest.cs`
   - `SubSonicDownloadRequest.cs`
   - `DABMusicDownloadRequest.cs`

   Each provider now:
   - Calls `ExtractMusicBrainzIds()` in constructor
   - Passes MBIDs to `AudioMetadataHandler.TryEmbedMetadata()`

## TagLibSharp Property Mappings

Lidarr reads these properties from file tags (from `NzbDrone.Core.MediaFiles/AudioTag.cs`):

```csharp
file.Tag.MusicBrainzReleaseId = mbids.ReleaseId;           // MUSICBRAINZ_ALBUMID → weight 5.0
file.Tag.MusicBrainzReleaseGroupId = mbids.ReleaseGroupId;  // MUSICBRAINZ_RELEASEGROUPID
file.Tag.MusicBrainzTrackId = mbids.TrackRecordingId;       // MUSICBRAINZ_TRACKID → weight 5.0
file.Tag.MusicBrainzArtistId = mbids.ArtistId;              // MUSICBRAINZ_ARTISTID
file.Tag.MusicBrainzReleaseArtistId = mbids.ReleaseArtistId; // MUSICBRAINZ_ALBUMARTISTID
```

## Architecture

```
Lidarr RemoteAlbum
    ↓
BaseDownloadRequest.ExtractMusicBrainzIds()
    → Returns MusicBrainzIds (ReleaseId, ReleaseGroupId, ArtistId, TrackRecordingIds)
    ↓
ProviderDownloadRequest constructor
    → Stores _mbids field
    ↓
PostProcessTrackAsync()
    → Passes _mbids to AudioMetadataHandler.TryEmbedMetadata()
    ↓
AudioMetadataHandler.WriteMusicBrainzTags()
    → Writes MBIDs using TagLibSharp
```

## Verification

### Expected Behavior

After downloading with this implementation:

1. Files contain MBID tags in ID3v2.3/ID3v2.4 frames
2. Lidarr import match scores increase to 85-95% (passing 80% threshold)
3. Import status shows "Imported" (not "Rejected")

### Inspecting MBID Tags

Using ffprobe:
```bash
ffprobe -hide_banner /path/to/downloaded/track.mp3 2>&1 | grep -i musicbrainz
# Should show MUSICBRAINZ_ALBUMID, MUSICBRAINZ_TRACKID, etc.
```

### End-to-End Test

1. Trigger a download in Lidarr UI
2. Wait for download to complete
3. Check **Import Queue** → should show "Imported"
4. Check file tags to confirm MBIDs present
5. Check **Activity Log** for import success messages

## Future Enhancements

### Blind MB Lookup Fallback

The `MusicBrainzLookup` class is included but not wired in. Future work could add:

- API-based MBID lookup when `RemoteAlbum` MBIDs are unavailable
- Fallback for metadata sources without direct Lidarr integration
- Score-based matching (≥80 threshold) to filter ambiguous matches

### Soulseek Integration

Soulseek downloads via slskd could benefit from MBID tagging. The `BaseDownloadRequest.ExtractMusicBrainzIds()` method is already available and could be integrated into Soulseek's download flow.

## Compatibility

- **Backward Compatible**: Existing metadata embedding behavior preserved
- **Optional MBIDs**: Works correctly if `RemoteAlbum` MBIDs are not available
- **Graceful Degradation**: MBID tag writing errors don't fail the entire metadata embedding

## References

- Shared strategy: `SHARED-MATCHING-STRATEGY.md` (in parent workspace)
- Lidarr import matching: `NzbDrone.Core.DecisionEngine/Specifications/CloseAlbumMatchSpecification.cs`
- Lidarr audio tag reading: `NzbDrone.Core.MediaFiles/AudioTag.cs`
- MusicBrainz API policy: https://musicbrainz.org/doc/XML_Web_Service/Rate_Limiting

## Testing Notes

When testing:

1. Verify files contain MBIDs after download
2. Confirm Lidarr imports without rejections
3. Check that existing metadata (artist, album, track) is still written correctly
4. Test with albums that have alternate editions (different release IDs)
5. Verify no regressions in providers that don't use MBIDs (Lucida, Soulseek)

## License

This code follows Tubifarry's existing license (GPL-3.0).