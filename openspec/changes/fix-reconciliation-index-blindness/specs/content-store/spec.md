## ADDED Requirements

### Requirement: Working-tree verification does not depend on the git index

The system SHALL determine whether the working tree diverges from `HEAD` by comparing content through an
instrument that does not consult the git index, so that an index entry marked `--assume-unchanged` or
`--skip-worktree` cannot cause a divergent tree to be reported as clean. This applies wherever the
working-tree-clean invariant is established or asserted — startup reconciliation, the assertion that
follows it, and the health/self-check that reports the invariant.

Where the system finds a tracked path that differs from `HEAD` and whose index entry suppresses that
difference, it SHALL refuse to start, naming the path and the index state responsible. It SHALL NOT
report the tree as clean, and SHALL NOT silently clear an index bit the operator set.

A suppressed index entry over a path that does **not** differ from `HEAD` is not a fault and SHALL NOT
prevent startup.

#### Scenario: A suppressed index entry cannot hide a divergent file

- **WHEN** a tracked file in the working tree differs from `HEAD` and its index entry is marked
  `--assume-unchanged` or `--skip-worktree`, so that staging, the staged diff, and the porcelain status
  all report nothing
- **THEN** the system detects the divergence anyway, refuses to start, and names both the path and the
  index state that suppressed it — rather than reporting a clean tree and starting

#### Scenario: A suppressed index entry over an unchanged file is not a fault

- **WHEN** an index entry is marked `--assume-unchanged` or `--skip-worktree` for a path whose working
  tree content matches `HEAD`
- **THEN** the system starts normally, because the invariant it protects is not violated

#### Scenario: The health check cannot be answered by the index

- **WHEN** the working-tree-clean self-check runs over a tree containing a divergent file whose index
  entry suppresses the difference
- **THEN** the check reports the invariant as violated rather than healthy, on the same content-level
  comparison reconciliation uses

#### Scenario: Untracked content is still reconciled, not refused

- **WHEN** the working tree contains untracked content — the ordinary way a new ZeroWiki is populated —
  and no tracked path is being suppressed
- **THEN** the system reconciles it into a recovery commit exactly as before, because index suppression
  applies only to tracked paths and this change alters no part of that behaviour
