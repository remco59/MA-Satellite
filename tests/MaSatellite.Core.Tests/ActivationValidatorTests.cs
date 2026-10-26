using MaSatellite.Core.Security;
using MaSatellite.Core.Session;

namespace MaSatellite.Core.Tests;

public class ActivationValidatorTests
{
    private static readonly string[] Pairing = ["pairing"];
    private static readonly string[] Playback = ["playback"];
    private static readonly string[] Both = ["playback", "pairing"];
    private static readonly string[] None = [];
    private static readonly string[] Player = ["player@v1"];
    private static readonly string[] Offered = ["pairing_psk"];

    private static ActivationVerdict V(string cat, bool unpaired, string[] activities, string[] roles, string? method = null, string? format = null) =>
        ActivationValidator.Validate(cat, unpaired, activities, roles, method, format, Offered);

    [Fact]
    public void Long_term_psk_allows_empty_or_playback_only()
    {
        Assert.Equal(ActivationVerdict.Admissible, V("lt", false, None, None));
        Assert.Equal(ActivationVerdict.Admissible, V("lt", false, Playback, Player));
        Assert.Equal(ActivationVerdict.Unauthorized, V("lt", true, Pairing, None, "pairing_psk"));
        Assert.Equal(ActivationVerdict.Unauthorized, V("lt", true, Both, Player));
    }

    [Theory]
    [InlineData("sn")]
    [InlineData("pr")]
    public void Unpaired_sessions_may_stay_idle_or_pair_without_unpaired_access(string category)
    {
        Assert.Equal(ActivationVerdict.Admissible, V(category, false, None, None));
        var method = category == "pr" ? "pairing_psk" : "static_pairing_code";
        var verdict = V(category, false, Pairing, None, method);
        Assert.Equal(category == "pr" ? ActivationVerdict.Admissible : ActivationVerdict.MethodNotSupported, verdict);
    }

    [Theory]
    [InlineData("sn")]
    [InlineData("pr")]
    public void Unpaired_playback_needs_unpaired_access(string category)
    {
        Assert.Equal(ActivationVerdict.Admissible, V(category, true, Playback, Player));
        Assert.Equal(ActivationVerdict.PairingRequired, V(category, false, Playback, Player));
        Assert.Equal(ActivationVerdict.PairingRequired, V(category, false, Playback, None));
    }

    [Fact]
    public void Roles_without_playback_capability_are_unauthorized_even_when_the_set_is_fine()
    {
        // idle + roles on a sentinel connection is only playback-capable with unpaired access
        Assert.Equal(ActivationVerdict.PairingRequired, V("sn", false, None, Player));
        Assert.Equal(ActivationVerdict.Admissible, V("sn", true, None, Player));
        // a pairing-only activity cannot carry roles on a long-term connection (it is not an allowed set at all)
        Assert.Equal(ActivationVerdict.Unauthorized, V("lt", false, Pairing, Player, "pairing_psk"));
    }

    [Fact]
    public void Pairing_psk_method_must_match_the_matched_psk()
    {
        Assert.Equal(ActivationVerdict.Admissible, V("pr", true, Pairing, None, "pairing_psk"));
        Assert.Equal(ActivationVerdict.MethodNotSupported, V("sn", true, Pairing, None, "pairing_psk")); // sentinel cannot do pairing_psk
        Assert.Equal(ActivationVerdict.MethodNotSupported, V("pr", true, Pairing, None, "dynamic_pairing_code"));
        Assert.Equal(ActivationVerdict.MethodNotSupported, V("pr", true, Pairing, None, null));
        Assert.Equal(ActivationVerdict.MethodNotSupported, V("pr", true, Pairing, None, "pairing_psk", "digits")); // no format for pairing_psk
        Assert.Equal(ActivationVerdict.Admissible, V("pr", false, Pairing, None, "pairing_psk"));
        Assert.Equal(ActivationVerdict.Admissible, V("pr", true, Both, Player, "pairing_psk"));
    }

    [Fact]
    public void Unknown_activities_are_unauthorized()
    {
        Assert.Equal(ActivationVerdict.Unauthorized, V("sn", true, ["recording"], None));
        Assert.Equal(PskCategory.Sentinel, "sn");
    }
}
