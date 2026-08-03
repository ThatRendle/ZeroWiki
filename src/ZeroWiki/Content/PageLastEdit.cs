namespace ZeroWiki.Content;

/// <summary>
/// Who last touched a page and when, read from git history (D5) — never from a hand-maintained field.
/// </summary>
public sealed record PageLastEdit(string AuthorName, DateTimeOffset EditedAt);
