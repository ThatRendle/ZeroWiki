#!/usr/bin/env bash
#
# mutate.sh — apply one mutant, run the full unfiltered suite, always revert.
#
#   .claude/skills/mutation-testing/mutate.sh <file> <search> <replace> ["label"]
#
# Read SKILL.md before using this. The script enforces the two rules that no
# amount of prose has managed to: the revert happens in a `trap` that an
# interruption cannot skip, and the target is verified by CONTENT checksum
# rather than by a git command. Everything else in SKILL.md — the cap of three
# confirmation runs, mutating only auth/concurrency/data-integrity paths, not
# expanding to other files without a go-ahead — is still yours to obey; a
# script cannot hold those for you.
#
# Exit codes:
#   0  mutant KILLED   (the suite failed, as it should)
#   1  mutant SURVIVED (the suite passed with the property broken — a finding)
#   2  harness fault   (no-op mutation, failed revert, bad arguments)
#
# A harness fault is never a result. Do not report 2 as either outcome.

set -uo pipefail

fail() { printf '\n[harness] %s\n' "$*" >&2; exit 2; }

[ $# -ge 3 ] || fail "usage: mutate.sh <file> <search> <replace> [\"label\"]"

TARGET=$1
SEARCH=$2
REPLACE=$3
LABEL=${4:-"$SEARCH -> $REPLACE"}

[ -f "$TARGET" ] || fail "target does not exist: $TARGET"

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || fail "not inside a git repository"
cd "$REPO_ROOT" || fail "cannot cd to repo root"

sum() { shasum -a 256 "$1" | cut -d' ' -f1; }

BACKUP=$(mktemp -t mutate-backup) || fail "cannot create backup file"
cp "$TARGET" "$BACKUP" || fail "cannot back up $TARGET"
BEFORE=$(sum "$TARGET")

# The revert runs on ANY exit — success, failure, Ctrl-C, SIGTERM. This is the
# whole reason the script exists: a live mutant has been left in src/ before by
# a run that was stopped between "apply" and "revert".
revert() {
  local status=$?
  if ! cp "$BACKUP" "$TARGET"; then
    printf '\n[harness] REVERT FAILED — %s may still contain the mutant.\n' "$TARGET" >&2
    printf '[harness] backup is at %s — restore it by hand NOW.\n' "$BACKUP" >&2
    rm -f "$BACKUP.applied"
    exit 2
  fi
  local after
  after=$(sum "$TARGET")
  if [ "$after" != "$BEFORE" ]; then
    printf '\n[harness] REVERT VERIFICATION FAILED for %s\n' "$TARGET" >&2
    printf '[harness]   before %s\n[harness]   after  %s\n' "$BEFORE" "$after" >&2
    printf '[harness] backup is at %s — do not commit until this is resolved.\n' "$BACKUP" >&2
    exit 2
  fi
  rm -f "$BACKUP"
  printf '[harness] reverted %s, checksum matches (%s)\n' "$TARGET" "${BEFORE:0:12}" >&2
  exit $status
}
trap revert EXIT INT TERM

printf '[harness] target   %s\n' "$TARGET" >&2
printf '[harness] mutant   %s\n' "$LABEL" >&2
printf '[harness] checksum %s (before)\n' "${BEFORE:0:12}" >&2

# Literal, not regex: a mutation is a text substitution, and a search string
# containing regex metacharacters must not silently match something else.
python3 - "$TARGET" "$SEARCH" "$REPLACE" <<'PY' || fail "substitution failed"
import sys, pathlib
path, search, replace = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
text = path.read_text()
count = text.count(search)
if count == 0:
    sys.stderr.write("[harness] search string not found — nothing to mutate\n")
    sys.exit(1)
if count > 1:
    sys.stderr.write(f"[harness] search string matches {count} times; make it unique\n")
    sys.exit(1)
path.write_text(text.replace(search, replace))
PY

AFTER=$(sum "$TARGET")
# A no-op mutation is indistinguishable from a surviving mutant — a CRLF/LF
# mismatch once silently modified nothing across three separate mutations and
# all three "survived".
[ "$AFTER" != "$BEFORE" ] || fail "no-op mutation: file content is unchanged after substitution"
printf '[harness] checksum %s (after, differs — mutation is live)\n' "${AFTER:0:12}" >&2

# Full unfiltered suite. A filtered run measures a condition the gate never runs
# in: one property reported 3/3 filtered and 7/13 under the real parallel suite.
# No pipe — a pipeline's exit status is the last command's, so piping to `tail`
# reports a failed run as a success.
LOG=$(mktemp -t mutate-testlog)
printf '[harness] running the full unfiltered suite (no filter, no pipe)...\n' >&2
dotnet test > "$LOG" 2>&1
TEST_STATUS=$?

grep -E 'Passed!|Failed!|error|Test Run' "$LOG" | tail -5 >&2
printf '[harness] full log: %s\n' "$LOG" >&2

if [ $TEST_STATUS -ne 0 ]; then
  printf '\n[harness] KILLED — the suite failed with the mutant applied.\n' >&2
  exit 0
fi

printf '\n[harness] SURVIVED — the suite PASSED with the property broken.\n' >&2
printf '[harness] That is a finding, not a pass. A surviving mutant may still be\n' >&2
printf '[harness] correct (record it deliberately, with the reason) — but never\n' >&2
printf '[harness] silently drop it, and never edit the code to make it die.\n' >&2
exit 1
