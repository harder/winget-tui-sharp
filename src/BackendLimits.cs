namespace WingetTuiSharp;

/// <summary>
/// Hard memory limits applied at backend trust boundaries: 1,000 search rows; 10,000 local
/// rows; 256 catalogs; 2,048 versions; 4,096 total metadata or verification items; 4 Ki UTF-16
/// code units for ordinary display fields and exact operational identities; and 64 Ki for
/// descriptions and installation notes. Oversized identities are rejected, never truncated.
/// </summary>
internal static class BackendLimits
{
    internal const int SearchMatches = AppState.SearchResultLimit;
    internal const int LocalMatches = 10_000;
    internal const int Catalogs = 256;
    internal const int Versions = 2_048;
    internal const int MetadataItems = 4_096;
    internal const int VerificationItems = 4_096;
    internal const int VerificationInstallers = 256;
    internal const int IdentityCharacters = 4 * 1_024;
    internal const int SimpleTextCharacters = 4 * 1_024;
    internal const int RichTextCharacters = 64 * 1_024;

    internal static List<T> Materialize<T> (IReadOnlyList<T> projected, int maximum)
    {
        ArgumentNullException.ThrowIfNull (projected);
        ArgumentOutOfRangeException.ThrowIfNegative (maximum);

        int count = Math.Min (projected.Count, maximum);
        List<T> copy = new (count);

        for (int i = 0; i < count; i++)
        {
            copy.Add (projected [i]);
        }

        return copy;
    }

    internal static string? SimpleText (string? value) => Truncate (value, SimpleTextCharacters);
    internal static string? RichText (string? value) => Truncate (value, RichTextCharacters);

    /// <summary>
    /// Accept an operational identity only when it can be retained exactly. Unlike display text,
    /// package ids, versions, and source names must never be shortened into a different lookup key.
    /// </summary>
    internal static string? ExactIdentity (string? value)
        => value is null || value.Length > IdentityCharacters || string.IsNullOrWhiteSpace (value) ? null : value;

    /// <summary>Validate an optional identity, distinguishing an absent value from an oversized one.</summary>
    internal static bool TryExactIdentity (string? value, out string? exact)
    {
        if (value is { Length: > IdentityCharacters })
        {
            exact = null;
            return false;
        }

        exact = ExactIdentity (value);
        return true;
    }

    internal static string? Truncate (string? value, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative (maximumCharacters);

        if (value is null || value.Length <= maximumCharacters)
        {
            return value;
        }

        int length = maximumCharacters;

        // Never split a UTF-16 surrogate pair at the retained boundary.
        if (length > 0 && char.IsHighSurrogate (value [length - 1]) && char.IsLowSurrogate (value [length]))
        {
            length--;
        }

        return value [..length];
    }
}

/// <summary>Shares one total item allowance across related or nested external collections.</summary>
internal sealed class CollectionBudget
{
    private int _remaining;

    internal CollectionBudget (int maximumItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegative (maximumItems);
        _remaining = maximumItems;
    }

    internal int Remaining => _remaining;

    internal int Take (int requested) => TakeBounded (requested).Count;

    internal CollectionTake TakeBounded (int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative (requested);
        int granted = Math.Min (requested, _remaining);
        _remaining -= granted;
        return new (granted, granted == requested);
    }
}

internal readonly record struct CollectionTake (int Count, bool Complete);

internal sealed record VerificationCandidate (IReadOnlyList<VerifyCheck> Checks, bool Complete);

internal readonly record struct VerificationDecision (VerifyOutcome Outcome, IReadOnlyList<VerifyCheck> Checks);

/// <summary>
/// Chooses a verification result without treating partial projected data as definitive. A fully
/// observed passing installer proves the package healthy under WinGet's any-installer semantics.
/// Otherwise any omitted/unreadable data may change the best-installer result and yields Error.
/// </summary>
internal static class VerificationEvaluator
{
    internal static VerificationDecision Decide (
        IReadOnlyList<VerificationCandidate> candidates,
        bool externalIncomplete)
    {
        VerificationCandidate? completePass = candidates.FirstOrDefault (
            candidate => candidate.Complete
                         && candidate.Checks.Count > 0
                         && candidate.Checks.All (check => check.Ok));

        if (completePass is not null)
        {
            return new (VerifyOutcome.Ok, completePass.Checks);
        }

        if (externalIncomplete || candidates.Any (candidate => !candidate.Complete))
        {
            return new (VerifyOutcome.Error, []);
        }

        VerificationCandidate? best = candidates
            .Where (candidate => candidate.Checks.Count > 0)
            .OrderBy (candidate => candidate.Checks.Count (check => !check.Ok))
            .FirstOrDefault ();

        return best is null
                   ? new (VerifyOutcome.NotApplicable, [])
                   : new (VerifyOutcome.Issues, best.Checks);
    }
}
