namespace ZeroWiki.Content;

/// <summary>
/// D17: the base revision a save declares it started from — the blob a specific file had at
/// <c>HEAD</c> when the save's caller loaded it (<see cref="ForBlob"/>), or the explicit "this page did
/// not exist at <c>HEAD</c> yet" sentinel (<see cref="AbsentAtHead"/>).
/// </summary>
/// <remarks>
/// Carried as an explicit sentinel rather than an empty string, so "I started from nothing" and "I did
/// not declare a base at all" are distinct inputs (D17) — the second is not a value this type can hold;
/// a caller that has no base to declare simply does not call
/// <see cref="PageSaveService.SaveAsync(RouteValue, string, PageBaseRevision, ZeroWiki.Identity.AuthenticatedAccount, CancellationToken)"/>
/// with one, rather than this type growing a third state to represent it.
/// </remarks>
public readonly record struct PageBaseRevision
{
    private PageBaseRevision(string? blobSha) => BlobSha = blobSha;

    /// <summary>The declared blob sha, or <see langword="null"/> for <see cref="AbsentAtHead"/>.</summary>
    public string? BlobSha { get; }

    /// <summary>The page did not exist at <c>HEAD</c> when the save's caller loaded it.</summary>
    public static readonly PageBaseRevision AbsentAtHead = new((string?)null);

    /// <summary>The blob sha the save's caller loaded the page's content from.</summary>
    public static PageBaseRevision ForBlob(string blobSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobSha);
        return new PageBaseRevision(blobSha);
    }
}
