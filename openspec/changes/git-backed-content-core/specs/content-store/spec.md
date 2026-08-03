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

The system SHALL derive each page's route from its working-tree path relative to `docs/` with the `.md` extension removed, under a `/wiki/` prefix, encoding a space as `_` and a literal `_` as `__`. The system SHALL serve a route only when exactly one file claims it **and** decoding that route reproduces that file's own path; a route failing either condition SHALL be refused rather than resolved, naming every file implicated and leaving every other route unaffected. The system SHALL refuse a malformed or hostile route rather than failing the request with an unhandled error.

#### Scenario: Nested page is addressable by its path

- **WHEN** the working tree contains `docs/Project Notes/Kick Off.md`
- **THEN** the page is served at `/wiki/Project_Notes/Kick_Off`

#### Scenario: Literal underscore round-trips

- **WHEN** the working tree contains a page whose filename contains a literal underscore
- **THEN** the route encodes it as `__`, and resolving that route back to a path yields the original filename rather than one containing a space

#### Scenario: Ambiguous route is refused, not guessed

- **WHEN** two files in the working tree encode to the same route (for example `a_ b.md` and `a _b.md`, which both encode to `a___b`)
- **THEN** the system serves neither page at that route, names every file claiming it, and continues to serve every other page normally

#### Scenario: Sole claimant whose route does not identify it is refused

- **WHEN** the working tree contains a single file whose route decodes to a different path than the file's own (for example `Chapter  1.md`, with two spaces, whose route `Chapter__1` decodes to `Chapter_1.md`), and no other file claims that route
- **THEN** the system refuses that route rather than serving the file, so that no later save can resolve the route to a different file than the one being read

#### Scenario: Only a page's own canonical route serves it

- **WHEN** a request supplies a route that is not the exact route the requested file produces — for example `/wiki/Chapter%20%201` (two spaces) where only `Chapter_1.md` exists, both encoding to `Chapter__1`
- **THEN** the system does not serve that file, so that a request can never reach a page by a route that would resolve elsewhere on a later save

#### Scenario: Hostile route is refused, not fatal

- **WHEN** a request supplies a route containing a percent-encoded NUL byte, a control character, or another sequence that no enumerated file could have produced
- **THEN** the system refuses the route and continues serving, rather than raising an unhandled error

### Requirement: Page content cannot execute script

The system SHALL render Markdown with raw HTML disabled, so that inline and block HTML in page content appears as text rather than as markup, and SHALL NOT rely on sanitizing HTML instead. The system SHALL additionally permit a link or image destination only when it is relative or carries an explicitly allowed scheme, refusing every other destination, and SHALL serve a Content-Security-Policy that blocks inline script.

#### Scenario: Embedded script is not executed

- **WHEN** a page's Markdown contains a raw HTML element such as `<script>` or an element carrying an event-handler attribute
- **THEN** the rendered page displays that markup as visible text and the browser executes none of it

#### Scenario: Script-bearing link destination is refused

- **WHEN** a page's Markdown contains a link or image whose destination carries a scheme outside the allowed set — for example `javascript:`, `data:`, or `vbscript:` — including forms that reach that scheme only after entity decoding, case changes, or embedded whitespace
- **THEN** the rendered page does not emit that destination as a live link or image source

#### Scenario: Ordinary destinations still work

- **WHEN** a page's Markdown contains a relative link to another page, a bare fragment, or an `http`, `https`, or `mailto` destination
- **THEN** the rendered page emits it unchanged as a working link

#### Scenario: Response carries a script-blocking policy

- **WHEN** any page is served
- **THEN** the response carries a Content-Security-Policy header whose `script-src` does not permit inline script

### Requirement: Derived index rebuildable from the repository

The system SHALL maintain a lightweight index of pages (paths, titles, tags, and last-edit metadata) that is derived entirely from the repository, and SHALL be able to rebuild that index from the repository alone. The index SHALL record the commit it was built from, and the system SHALL serve page metadata from the index only while that commit is the repository's current commit — refreshing it otherwise, including when the commit was advanced by a writer that never notified the application.

#### Scenario: Index rebuilt from repository

- **WHEN** the index is deleted or absent at startup
- **THEN** the system rebuilds the complete index by scanning the repository working tree and history

#### Scenario: Index updated after a content change

- **WHEN** a file in the working tree is added, modified, or removed by any writer
- **THEN** the system updates the index entries for the affected files

#### Scenario: Content changed by an unannounced writer is still reflected

- **WHEN** the repository's commit is advanced while the app is running by a writer that does not notify it — an incoming push updating the working tree, or a commit made directly on the volume — and a page is then requested
- **THEN** the system serves that page's current content and metadata rather than metadata describing the superseded commit, without requiring a restart

#### Scenario: Refused routes survive indexing

- **WHEN** the index is built or refreshed over a working tree containing a route no file identifies uniquely
- **THEN** that route is still refused, naming every file implicated, rather than being served or reported as a route no page claims

#### Scenario: Index holds no content

- **WHEN** a page's body is rendered
- **THEN** the body is read from the working tree for that request rather than from the index, so no index entry can cause a superseded body to be served

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
