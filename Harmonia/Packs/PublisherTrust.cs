using Harmonia.Packs.Hpk;

namespace Harmonia.Packs;

public enum PublisherTrustState
{
    // Signed by the key pinned for this pack id.
    Trusted,

    // Signed by a new key that the pinned key endorsed.
    Rotated,

    // Signed, and nothing is pinned for this pack id yet.
    FirstUse,

    // Signed by a key other than the pinned one, without an endorsement.
    KeyChanged,

    Unsigned,
}

// Trust is pinned per pack id to a signing key fingerprint (feed-v1.md,
// "Publisher trust"). Only Trusted and Rotated install without asking.
public static class PublisherTrust
{
    public static PublisherTrustState Evaluate(string? pinnedFingerprint, HpkSignature? signature)
    {
        if (signature is null)
            return PublisherTrustState.Unsigned;
        if (string.IsNullOrEmpty(pinnedFingerprint))
            return PublisherTrustState.FirstUse;
        if (string.Equals(signature.Fingerprint, pinnedFingerprint, StringComparison.Ordinal))
            return PublisherTrustState.Trusted;

        // The endorsement itself was verified when the pack was opened.
        if (string.Equals(signature.PreviousFingerprint, pinnedFingerprint, StringComparison.Ordinal))
            return PublisherTrustState.Rotated;

        return PublisherTrustState.KeyChanged;
    }

    public static bool InstallsWithoutConfirmation(PublisherTrustState state) =>
        state is PublisherTrustState.Trusted or PublisherTrustState.Rotated;
}
