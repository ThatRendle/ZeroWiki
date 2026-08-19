namespace ZeroWiki.Content;

/// <summary>
/// Who last touched a page and when, read from git history (D5) — never from a hand-maintained field.
/// </summary>
/// <param name="AuthorName">
/// The raw git author name (<c>%an</c>) exactly as git reports it — the identity a synthetic browser
/// save carries (<see cref="AccountGitAuthorFactory.CreateAuthor"/>) or a pusher self-asserted (D5).
/// The index stores this raw form, never a resolved account; §8.3's mapping is applied at display
/// time (<c>WikiPage.razor</c>), not baked into this long-lived snapshot — see
/// <see cref="GitIdentityResolver"/>'s own remarks for why.
/// </param>
/// <param name="EditedAt">The commit's author date (<c>%aI</c>).</param>
/// <param name="AuthorEmail">
/// The raw git author email (<c>%ae</c>) — <see langword="null"/> only when git's own output could
/// not be parsed (see the callers' remarks). What §8.3's resolver is given to map back to an account.
/// </param>
public sealed record PageLastEdit(string AuthorName, DateTimeOffset EditedAt, string? AuthorEmail = null);
