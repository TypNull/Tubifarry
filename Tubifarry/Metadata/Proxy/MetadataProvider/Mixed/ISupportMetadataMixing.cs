using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    public enum MetadataSupportLevel { Unsupported, ImplicitSupported, Supported }

    public interface ISupportMetadataMixing : IProxy
    {
        MetadataSupportLevel CanHandleSearch(string? albumTitle = null, string? artistName = null);
        MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds);
        MetadataSupportLevel CanHandleChanged();
        MetadataSupportLevel CanHandleId(string albumId);
        string? SupportsLink(List<Links> links);
    }

}