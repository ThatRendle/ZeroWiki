## ADDED Requirements

### Requirement: Git repository is the source of truth

The system SHALL treat the mounted content volume as a non-bare git repository whose working tree is the authoritative store for all page content and authorship. The system SHALL NOT hold content or authorship in any store that cannot be rebuilt from the repository.

#### Scenario: Repository present on startup

- **WHEN** the app starts and the content volume contains a git repository with a `docs/` working tree
- **THEN** the system serves pages from the working tree without requiring any additional configuration

#### Scenario: Empty volume is initialized

- **WHEN** the app starts and the content volume contains no git repository
- **THEN** the system initializes a new non-bare git repository with a `docs/` directory and an initial commit

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
- **THEN** the system still renders the page body and records the metadata as empty rather than failing the page

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

- **WHEN** the app starts and finds uncommitted changes in the working tree (for example after a crash mid-save)
- **THEN** the system reconciles the tree to a clean state — either committing the orphaned changes as a recovered edit or discarding them per policy — before serving requests or accepting pushes
