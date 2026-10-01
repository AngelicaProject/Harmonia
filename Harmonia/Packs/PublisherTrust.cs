using Harmonia.Packs.Hpk;

namespace Harmonia.Packs;

public enum PublisherTrustState
{
    // Signed by the key the translation it updates trusts.
    Trusted,

    // Signed by a new key that the trusted key endorsed.
    Rotated,

    // Signed, and it updates no translation yet.
    FirstUse,

    // From a translation's feed, signed by a key other than the trusted one,
    // without an endorsement.
    KeyChanged,

    Unsigned,
}

// Each installed translation trusts one signing key fingerprint (feed-v1.md,
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
