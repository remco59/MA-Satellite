namespace MaSatellite.Host.NowPlaying;

/// <summary>How much weight a piece of information deserves; the About tab shows this next to every item.</summary>
public enum InfoTrust
{
    /// <summary>Tags in the user's own library or provider catalogue.</summary>
    Library,

    /// <summary>Structured, curated metadata (MusicBrainz) matched to this exact recording or artist.</summary>
    Verified,

    /// <summary>Free text written by a community or a provider (for example an artist biography). Not fact-checked here.</summary>
    Community,
}

public sealed record AboutFact(string Label, string Value, InfoTrust Trust, string Source);

public sealed record ArtistAbout(
    string Name,
    string? Biography,
    string? BiographySource,
    IReadOnlyList<AboutFact> Facts,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MbLink> Links,
    string? MusicAssistantUri);

public sealed record AboutInfo(
    IReadOnlyList<AboutFact> TrackFacts,
    IReadOnlyList<ArtistAbout> Artists,
    IReadOnlyList<string> Tags,
    string? ReleaseArtworkUrl,
    string? MatchNote,
    IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => TrackFacts.Count == 0 && Artists.Count == 0 && Tags.Count == 0;
}

public enum RelatedStatus
{
    Ok,
    Empty,

    /// <summary>The server or its providers do not offer this lookup.</summary>
    Unavailable,
}

public sealed record RelatedSection(string Title, string Source, RelatedStatus Status, IReadOnlyList<MaSatellite.MaApi.Models.MaItem> Items, string? Message = null);

public sealed record RelatedContent(IReadOnlyList<RelatedSection> Sections);
