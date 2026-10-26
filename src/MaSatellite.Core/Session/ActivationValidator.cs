using MaSatellite.Core.Protocol;
using MaSatellite.Core.Security;

namespace MaSatellite.Core.Session;

public enum ActivationVerdict
{
    Admissible,

    /// <summary>Would be fine with unpaired access enabled: say goodbye <c>pairing_required</c>.</summary>
    PairingRequired,

    /// <summary>Not allowed for the matched PSK: say goodbye <c>unauthorized</c>.</summary>
    Unauthorized,

    /// <summary>Pairing method or format not offered/allowed: reply <c>pair/abort</c> <c>method_not_supported</c> and stay connected.</summary>
    MethodNotSupported,
}

/// <summary>The <c>server/activate</c> admissibility rules of the spec, as a pure function.</summary>
public static class ActivationValidator
{
    public static ActivationVerdict Validate(
        string pskCategory,
        bool unpairedAccessEnabled,
        IReadOnlyCollection<string> activities,
        IReadOnlyCollection<string> activeRoles,
        string? pairingMethod,
        string? pairingFormat,
        IReadOnlyCollection<string> offeredMethods)
    {
        var unpaired = pskCategory != PskCategory.LongTerm;
        var wantsPairing = activities.Contains(Activities.Pairing);

        var setAllowed = IsAllowedSet(pskCategory, unpairedAccessEnabled, activities);
        var capable = IsAllowedSet(pskCategory, unpairedAccessEnabled, [.. activities, .. activities.Contains(Activities.Playback) ? [] : new[] { Activities.Playback }]);
        var rolesOk = activeRoles.Count == 0 || capable;

        if (!setAllowed || !rolesOk)
        {
            // Would enabling unpaired access make it admissible? Then the client is the one refusing.
            if (unpaired && !unpairedAccessEnabled
                && IsAllowedSet(pskCategory, true, activities)
                && (activeRoles.Count == 0 || IsAllowedSet(pskCategory, true, [.. activities, .. activities.Contains(Activities.Playback) ? [] : new[] { Activities.Playback }])))
            {
                return ActivationVerdict.PairingRequired;
            }

            return ActivationVerdict.Unauthorized;
        }

        if (wantsPairing)
        {
            // pairing_psk if and only if the connection is keyed by the pairing PSK, and it must be a method we offer.
            var needsPsk = pskCategory == PskCategory.Pairing;
            if (string.IsNullOrEmpty(pairingMethod)
                || (pairingMethod == "pairing_psk") != needsPsk
                || !offeredMethods.Contains(pairingMethod)
                || !string.IsNullOrEmpty(pairingFormat))
            {
                return ActivationVerdict.MethodNotSupported;
            }
        }

        return ActivationVerdict.Admissible;
    }

    private static bool IsAllowedSet(string category, bool unpairedEnabled, IReadOnlyCollection<string> activities)
    {
        var set = activities.Distinct().ToHashSet();
        if (set.Any(a => a is not (Activities.Playback or Activities.Pairing)))
        {
            return false;
        }

        var playback = set.Contains(Activities.Playback);
        var pairing = set.Contains(Activities.Pairing);
        return category switch
        {
            PskCategory.LongTerm => !pairing,
            _ => !playback || unpairedEnabled,
        };
    }
}
