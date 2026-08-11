---
name: mutation-testing
description: ZeroWiki's mutation-testing discipline — the caps that keep a run proportionate, the rules that make a result mean something, and the harness hazards that have bitten this repo. Load BEFORE running any mutant, and before briefing an agent to run one. Trigger: mutating a service to check a test dies, verifying a security or concurrency property, confirming a test can actually fail, or any block brief that includes mutation testing.
---

# Mutation testing — capped and scoped

Mutation testing is this project's evidence standard: a green suite is not proof a security property
holds, so break the property and check a test dies. It has earned its place — it has caught a live
concurrency defect, a `BootstrapConcurrencyTests` that only half-worked, an assertion that compared
only a URL's path, and a hasher recorder blind to the password. **It is also easy to run far past
the point of usefulness**, so it is bounded. **ZeroWiki is a wiki for a small trusted group, not a
system that warrants unbounded verification.**

1. **Cap confirmation runs at 3.** A mutant that dies 3/3 with a consistent, understood failure mode
   is confirmed. Exceed 3 **only** when results are genuinely flaky or nondeterministic and
   characterising that variance *is* the finding.
2. **Mutate security- and correctness-critical paths only** — auth, concurrency, and data integrity
   (in practice `BootstrapService`, `InvitationService`, `LoginService`, `GitTokenService`, the
   anonymous gate). **Not** general CRUD or wiki-page logic: ordinary unit tests with normal coverage
   are correct there.
3. **No polling loops with sleep plus background processes.** If a run must be backgrounded, use a
   bounded wait with a short timeout (~2 min) and report if it has not resolved.
4. **Stop and summarise when the mutant at hand is resolved.** Do not expand to other files without
   an explicit go-ahead. A genuine finding is **not** licence to keep digging in the same area — fix
   it and move on.

**Brief agents with these limits in the block brief itself.** Reining an agent in afterwards is what
made the rule necessary.

## Rules that make a mutation result mean something

- **Verify under the full `dotnet test`, never a filter.** A filtered run measures a condition the
  gate never runs in: `BootstrapConcurrencyTests` reported 3/3 filtered and 7/13 under the real
  parallel suite. A filtered figure is not wrong, it is *irrelevant* — never post one as the record.
- **Checksum the target before *and* after.** A no-op mutation is indistinguishable from a surviving
  mutant. A `\n`-vs-CRLF mismatch once silently modified nothing across three mutations.
- **Check your instrument before believing it.** Test any pattern you measure with against
  known-present markup first. Two agents once shared a blind spot — both anchor regexes required
  `href="…"` while Blazor renders `href=""` bare — so they corroborated each other while both were
  wrong. Two measurements agreeing is not corroboration when they share an instrument.
- **A surviving mutant may be correct.** Record it deliberately with the reason (an explicit
  `app.UseRouting()` whose removal changes nothing is kept because the ordering dependency is a
  security property). Never silently drop the result or edit the code to make it die.

## Hazard: an interrupted mutation run leaves a live mutant in `src/`

`BootstrapService.cs` was once found with `deferred: false` → `true` still applied after an agent was
stopped mid-run — the mutation that breaks "exactly one administrator", sitting in production code
with the working tree looking entirely ordinary.

- **Always `git diff -- src` before committing** anything that followed a mutation run. This is not
  ceremony.
- **Run `git status --short -- src` alongside it — the diff is blind to untracked files.** `git diff`
  reports only on files git already tracks; a file that has never been `git add`ed is not shown as
  unchanged, it is not shown *at all*. §7b mutated `GitEmailService.cs`, which was brand new and
  untracked for the whole block, so the mandated diff came back clean over a file it had never looked
  at. A new file is the *normal* case for a block that adds a service, which is exactly when mutation
  testing is most likely to run.
- **A `??` entry means "read it or checksum it", not "it's fine".** `git status` surfaces that an
  untracked file exists; it cannot show content, and for a new file git has no baseline to diff
  against, so **no git command can verify a mutant inside it**. Git gives you visibility here, not
  verification.
- **The content check is what actually protects you** — checksum the target before *and* after each
  mutation (already required above), revert via the harness, and have a second pair of eyes re-read or
  re-run the mutants. In §7b that discipline, not git, is what confirmed the file was clean.
- **Mutation harnesses must revert via `trap`/`finally`**, never a final step that an interruption
  can skip.
