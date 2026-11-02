using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using Tubifarry.Core.Telemetry;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Soulseek;
using Tubifarry.Indexers.Soulseek.Search.Core;

namespace Tubifarry.Tests;

public class SlskdSelectedEditionTests
{
    // Capture the real request generator's lazy pipeline input without starting
    // a network search or depending on the internal generator type.
    private sealed class CapturePipeline : ISlskdSearchChain
    {
        public SearchContext? Context { get; private set; }

        public LazyIndexerPageableRequestChain BuildChain(SearchContext context, SearchExecutor searchExecutor)
        {
            Context = context;
            return new LazyIndexerPageableRequestChain(0);
        }
    }

    private static SearchContext Generate(bool anyReleaseOk, TrackCountFilterType filter,
        int? selected, params int[] editions)
    {
        var pipeline = new CapturePipeline();
        var indexer = new SlskdIndexer(null!, null!, null!, pipeline, null!, null!,
            null!, null!, null!, null!, new NoopSentryHelper(), LogManager.GetLogger("test"))
        {
            Definition = new IndexerDefinition
            {
                Settings = new SlskdSettings { TrackCountFilter = (int)filter }
            }
        };
        var releases = editions.Select(count => new AlbumRelease
        {
            TrackCount = count,
            Monitored = count == selected,
            Tracks = new List<Track>()
        }).ToList();
        var album = new Album { AnyReleaseOk = anyReleaseOk, AlbumReleases = releases };
        var criteria = new AlbumSearchCriteria
        {
            Artist = new Artist
            {
                Name = "Artist",
                Metadata = new ArtistMetadata { Aliases = new List<string>() }
            },
            AlbumTitle = "Album",
            Albums = new List<Album> { album }
        };

        indexer.GetExtendedRequestGenerator().GetSearchRequests(criteria);
        return pipeline.Context!;
    }

    [TestCase(32, 12, 32)] // Deluxe bonuses must not trigger another regular CD.
    [TestCase(10, 10, 20)] // An explicitly selected programme can also be shorter.
    public void Exact_respects_explicitly_selected_edition(int selected, int regular, int deluxe)
    {
        var context = Generate(false, TrackCountFilterType.Exact, selected, regular, deluxe);
        Assert.That(context.TrackCount, Is.EqualTo(selected));
        Assert.That(context.TrackCounts, Is.EqualTo(new[] { selected }));
    }

    [Test]
    public void Exact_keeps_all_editions_when_any_release_is_allowed()
    {
        var context = Generate(true, TrackCountFilterType.Exact, 32, 12, 32);
        Assert.That(context.TrackCounts, Is.EqualTo(new[] { 12, 32 }));
    }

    [TestCase(TrackCountFilterType.Disabled)]
    [TestCase(TrackCountFilterType.Lower)]
    [TestCase(TrackCountFilterType.Unfitting)]
    public void Other_filters_keep_existing_edition_semantics(TrackCountFilterType filter)
    {
        var context = Generate(false, filter, 32, 12, 32);
        Assert.That(context.TrackCounts, Is.EqualTo(new[] { 12, 32 }));
    }

    [Test]
    public void Missing_selected_edition_keeps_all_valid_counts()
    {
        var context = Generate(false, TrackCountFilterType.Exact, null, 32, 0, 12, 12);
        Assert.That(context.TrackCounts, Is.EqualTo(new[] { 12, 32 }));
    }

    [Test]
    public void Invalid_selected_count_does_not_filter_everything_out()
    {
        var context = Generate(false, TrackCountFilterType.Exact, 0, 0, 12, 32);
        Assert.That(context.TrackCounts, Is.EqualTo(new[] { 12, 32 }));
    }
}
