## ADDED Requirements

### Requirement: Git repository is the source of truth

The system SHALL treat the mounted content volume as a non-bare git repository whose working tree is the authoritative store for all page content and authorship. The system SHALL NOT hold content or authorship in any store that cannot be rebuilt from the repository.

#### Scenario: Repository present on startup

- **WHEN** the app starts and the content volume contains a git repository with a `docs/` working tree
- **THEN** the system serves pages from the working tree without requiring any additional configuration

#### Scenario: Empty volume is initialized

- **WHEN** the app starts and the content volume contains no git repository
- **THEN** the system initializes a new non-bare git repository with a `docs/` directory and an initial commit

#### Scenario: Repository with history but no working tree is refused

- **WHEN** the app starts and the content volume contains a non-bare git repository with commit history (whether adopted from elsewhere or created by a previous start) but no `docs/` directory
- **THEN** the system refuses to start, naming the missing `docs/` directory, rather than creating one, committing into the repository, or recording the missing directory as a recovered change

#### Scenario: Authorship comes from git

- **WHEN** the system reports who authored or last edited a page
- **THEN** the value is derived from git history (`log`/`blame`) and no hand-maintained author field is consulted

### Requirement: Markdown pages with YAML frontmatter

The system SHALL read Markdown files from the working tree and parse optional YAML frontmatter for page metadata such as tags. Malformed frontmatter SHALL NOT prevent the page body from rendering.

#### Scenario: Page with frontmatter is rendered

- **WHEN** a Markdown file begins with a YAML frontmatter block containing `tags`
- **THEN** the system renders the Markdown body and exposes the parsed tags as page metadata

#### Scenario: Page without frontmatter is rendered

- **WHEN** a Markdown file has no frontmatter block
- **THEN** the system renders the Markdown body and treats the metadata as empty

#### Scenario: Malformed frontmatter degrades gracefully

- **WHEN** a Markdown file has a frontmatter block that fails to parse as YAML
- **THEN** the system still renders the page body and records the metadata as empty rather than failing the page, and never records partially-parsed metadata

#### Scenario: Hostile frontmatter cannot take the process down

- **WHEN** a Markdown file's frontmatter block exceeds the permitted size or nesting depth, or expands exponentially through YAML aliases
- **THEN** the system rejects the block before parsing it, renders the page body with empty metadata, and continues serving every other page

### Requirement: Pages are addressable by a route derived from their path

The system SHALL derive each page's route from its working-tree path relative to `docs/` with the `.md` extension removed, under a `/wiki/` prefix, encoding a space as `_` and a literal `_` as `__`. Where two files would claim the same route, the system SHALL refuse to serve that route rather than choose between them, leaving every other route unaffected.

#### Scenario: Nested page is addressable by its path

- **WHEN** the working tree contains `docs/Project Notes/Kick Off.md`
- **THEN** the page is served at `/wiki/Project_Notes/Kick_Off`

#### Scenario: Literal underscore round-trips

- **WHEN** the working tree contains a page whose filename contains a literal underscore
- **THEN** the route encodes it as `__`, and resolving that route back to a path yields the original filename rather than one containing a space

#### Scenario: Ambiguous route is refused, not guessed

- **WHEN** two files in the working tree encode to the same route (for example `a_ b.md` and `a _b.md`, which both encode to `a___b`)
- **THEN** the system serves neither page at that route, names every file claiming it, and continues to serve every other page normally

### Requirement: Raw HTML in page content is not rendered

The system SHALL render Markdown with raw HTML disabled, so that inline and block HTML in page content appears as text rather than as markup, and SHALL NOT rely on sanitizing HTML instead.

#### Scenario: Embedded script is not executed

- **WHEN** a page's Markdown contains a raw HTML element such as `<script>` or an element carrying an event-handler attribute
- **THEN** the rendered page displays that markup as visible text and the browser executes none of it

### Requirement: Derived index rebuildable from the repository

The system SHALL maintain a lightweight index of pages (paths, titles, tags, and last-edit metadata) that is derived entirely from the repository, and SHALL be able to rebuild that index from the repository alone.

#### Scenario: Index rebuilt from repository

- **WHEN** the index is deleted or absent at startup
- **THEN** the system rebuilds the complete index by scanning the repository working tree and history

#### Scenario: Index updated after a content change

- **WHEN** a file in the working tree is added, modified, or removed by any writer
- **THEN** the system updates the index entries for the affected files

### Requirement: Working-tree-clean invariant

The system SHALL keep the working tree equal to `HEAD` (no uncommitted changes) at all times except for the brief, lock-protected window of an in-progress save, so that incoming pushes are always accepted.

#### Scenario: Tree is clean between saves

- **WHEN** no save is in progress
- **THEN** the working tree contains no uncommitted changes

#### Scenario: Dirty tree reconciled at startup

- **WHEN** the app starts and finds uncommitted changes in the working tree (for example after a crash mid-save, or content copied onto the volume before first start)
- **THEN** the system always commits the changes — tracked and untracked alike — as a single recovery commit authored `System <system@zerowiki.org>`, leaving the tree clean, before serving requests or accepting pushes

#### Scenario: Nested git repository refused rather than committed as a gitlink

- **WHEN** startup reconciliation finds a nested git repository under the working tree (for example an existing Obsidian vault or a cloned notes folder copied in with its own `.git`)
- **THEN** the system refuses to start, naming the offending path, rather than committing it as a gitlink — a reference to a commit in an object database the system never touches, from which the actual content could not be recovered
