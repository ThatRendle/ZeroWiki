using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ZeroWiki.Data.Converters;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as a fixed-width ISO-8601 string normalised to UTC
/// (<c>2026-07-25T13:00:00.0000000Z</c>).
/// </summary>
/// <remarks>
/// <para>
/// Fixed width plus an always-<c>Z</c> suffix plus SQLite's default BINARY collation means
/// lexicographic order <em>is</em> chronological order, which is what restores server-side
/// <c>ORDER BY</c> and <c>&gt;</c>/<c>&lt;</c> — SQLite supports neither on EF's default
/// <see cref="DateTimeOffset"/> mapping.
/// </para>
/// <para>
/// Normalising to UTC on write is the point, not a side effect: EF's built-in
/// <c>DateTimeOffsetToBinaryConverter</c> packs the offset into the stored value instead, so
/// two representations of the same instant compare unequal and an expiry filter silently
/// admits already-expired rows. Do not swap this for it.
/// </para>
/// </remarks>
public sealed class Iso8601UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    /// <summary>
    /// Seven fixed fractional digits and a literal <c>Z</c> — never the round-trip
    /// (<c>"o"</c>) form, whose variable offset would break the fixed width that ordering
    /// depends on. The literals are quoted so they can never be read as format specifiers.
    /// </summary>
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Every formatted value is exactly this long, by construction.</summary>
    public const int FormattedLength = 28;

    public Iso8601UtcDateTimeOffsetConverter()
        : base(
            value => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture),
            value => DateTimeOffset.ParseExact(
                value,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
    {
    }
}
