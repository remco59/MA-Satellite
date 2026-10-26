using MaSatellite.Core.Session;

namespace MaSatellite.Core.Tests;

public class ArbitrationPolicyTests
{
    private static ServerAdmission A(string id, AdmissionRank rank) => new(id, id, rank);

    [Theory]
    [InlineData(AdmissionRank.None, AdmissionRank.Playback, true)]
    [InlineData(AdmissionRank.Playback, AdmissionRank.None, false)]
    [InlineData(AdmissionRank.Playback, AdmissionRank.Playback, true)]
    [InlineData(AdmissionRank.None, AdmissionRank.Pairing, true)]
    [InlineData(AdmissionRank.Pairing, AdmissionRank.Playback, false)]
    [InlineData(AdmissionRank.Pairing, AdmissionRank.Pairing, false)]
    [InlineData(AdmissionRank.Pairing, AdmissionRank.None, false)]
    [InlineData(AdmissionRank.Playback, AdmissionRank.Pairing, false)]
    public void Higher_or_equal_playback_wins_and_a_pairing_attempt_is_never_displaced(AdmissionRank existing, AdmissionRank incoming, bool replace)
    {
        Assert.Equal(replace, ArbitrationPolicy.ShouldReplace(A("old", existing), A("new", incoming), null));
    }

    [Fact]
    public void Equal_idle_connections_prefer_the_last_played_server()
    {
        Assert.True(ArbitrationPolicy.ShouldReplace(A("old", AdmissionRank.None), A("new", AdmissionRank.None), "new"));
        Assert.False(ArbitrationPolicy.ShouldReplace(A("old", AdmissionRank.None), A("new", AdmissionRank.None), "old"));
        Assert.False(ArbitrationPolicy.ShouldReplace(A("old", AdmissionRank.None), A("new", AdmissionRank.None), null));
        Assert.False(ArbitrationPolicy.ShouldReplace(A("old", AdmissionRank.None), A("new", AdmissionRank.None), "other"));
    }

    [Fact]
    public void Legacy_reasons_map_to_ranks()
    {
        Assert.Equal(AdmissionRank.Playback, ArbitrationPolicy.FromLegacyReason("playback"));
        Assert.Equal(AdmissionRank.None, ArbitrationPolicy.FromLegacyReason("discovery"));
        Assert.Equal(AdmissionRank.None, ArbitrationPolicy.FromLegacyReason(null));
    }
}
