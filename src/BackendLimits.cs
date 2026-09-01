namespace WingetTuiSharp;

/// <summary>
/// Hard memory limits applied at backend trust boundaries: 1,000 search rows; 10,000 local
/// rows; 256 catalogs; 2,048 versions; 4,096 total metadata or verification items; 4 Ki UTF-16
/// code units for ordinary fields; and 64 Ki for descriptions and installation notes.
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

    internal int Take (int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative (requested);
        int granted = Math.Min (requested, _remaining);
        _remaining -= granted;
        return granted;
    }
}
