using MaSatellite.Host.NowPlaying;

namespace MaSatellite.Host.Tests.NowPlaying;

public class TrackMatcherTests
{
    private static readonly TrackKey Intoxicated = new("Intoxicated (Radio Edit)", ["Martin Solveig", "Good Times Ahead"], 158);

    [Fact]
    public void Same_recording_with_different_version_text_is_accepted()
    {
        var score = TrackMatcher.Score(Intoxicated, "Intoxicated", ["Martin Solveig"], 160);
        Assert.True(TrackMatcher.IsConfident(score), $"score {score}");
    }

    [Fact]
    public void Same_title_by_a_different_artist_is_rejected()
    {
        Assert.Equal(0, TrackMatcher.Score(Intoxicated, "Intoxicated", ["Some Other Band"], 158));
    }

    [Fact]
    public void Same_title_and_artist_but_very_different_length_is_rejected()
    {
        Assert.Equal(0, TrackMatcher.Score(Intoxicated, "Intoxicated", ["Martin Solveig"], 412));
    }

    [Fact]
    public void Unknown_duration_alone_does_not_block_a_strong_title_and_artist_match()
    {
        var score = TrackMatcher.Score(new TrackKey("Idioteque", ["Radiohead"], null), "Idioteque", ["Radiohead"], 309);
        Assert.True(TrackMatcher.IsConfident(score));
    }

    [Fact]
    public void Normalisation_ignores_case_accents_punctuation_and_featuring()
    {
        Assert.Equal(TrackMatcher.Normalize("Beyoncé – Halo (feat. X)"), TrackMatcher.Normalize("beyonce halo"));
        Assert.Equal(1, TrackMatcher.Similarity("Don't Stop", "dont stop"));
    }

    [Fact]
    public void Clearly_different_titles_do_not_match()
    {
        Assert.Equal(0, TrackMatcher.Score(new TrackKey("Halo", ["Beyoncé"], 261), "Hello", ["Beyoncé"], 261));
    }
}
