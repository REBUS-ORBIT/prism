# Visualiser v0.2.0 — PR merge order

The Visualiser feature ships across **12 PRs** stacked across two
roughly-parallel lineages (orchestrator + server/agent). Merge in the
following strict order to avoid base-branch drift; rebasing each PR
onto `main` after the prior one merges keeps the diff coherent.

This is the actionable runbook for whoever does the merging.

---

## Foundation (base: `main`)

| # | PR                                                                                   | Branch                       | Notes                                                       |
| - | ------------------------------------------------------------------------------------ | ---------------------------- | ----------------------------------------------------------- |
| 1 | [#1 Phase A — role plumbing](https://github.com/REBUS-ORBIT/prism/pull/1)            | `feat/visualiser-phase-a`    | AgentProtocol enum, DB migration 0003, dispatcher branch.   |
| 2 | [#4 CI cleanup](https://github.com/REBUS-ORBIT/prism/pull/4)                         | `chore/ci-cleanup`           | Independent of #1; rebases cleanly either order.            |

---

## Orchestrator stack (base: `main` after #1 + #4)

| # | PR                                                                                         | Branch                                | Stack base                                       |
| - | ------------------------------------------------------------------------------------------ | ------------------------------------- | ------------------------------------------------ |
| 3 | [#2 Phase B — orchestrator scaffold](https://github.com/REBUS-ORBIT/prism/pull/2)          | `feat/visualiser-phase-b`             | `main` (after #1)                                |
| 4 | [#3 Phase C — receive pipeline](https://github.com/REBUS-ORBIT/prism/pull/3)               | `feat/visualiser-phase-c`             | `feat/visualiser-phase-b` (rebase onto `main` after #2 merges) |
| 5 | Phase E — Python import + UE editor scaffold *(no PR yet)*                                 | `feat/visualiser-phase-e`             | `feat/visualiser-phase-c`. Open at: <https://github.com/REBUS-ORBIT/prism/pull/new/feat/visualiser-phase-e> |
| 6 | [#5 Phase F — Pixel Streaming](https://github.com/REBUS-ORBIT/prism/pull/5)                | `feat/visualiser-phase-f`             | `feat/visualiser-phase-e`                        |
| 7 | [#7 Phase J orchestrator — MVR/GDTF](https://github.com/REBUS-ORBIT/prism/pull/7)          | `feat/visualiser-phase-j-orchestrator`| `feat/visualiser-phase-f`                        |

---

## Server + Agent stack (base: `main` after the orchestrator stack)

| # | PR                                                                                       | Branch                            | Stack base                                       |
| - | ---------------------------------------------------------------------------------------- | --------------------------------- | ------------------------------------------------ |
|  8 | Phase G — server API + WS signalling proxy *(no PR yet)*                                | `feat/visualiser-phase-g`         | `main` (after orchestrator stack). Open at: <https://github.com/REBUS-ORBIT/prism/pull/new/feat/visualiser-phase-g> |
|  9 | Phase H — coturn on VM 211 *(no PR yet)*                                                | `feat/visualiser-phase-h`         | `feat/visualiser-phase-g`. Open at: <https://github.com/REBUS-ORBIT/prism/pull/new/feat/visualiser-phase-h> |
| 10 | [#6 Phase I — PS player + agent bridge](https://github.com/REBUS-ORBIT/prism/pull/6)    | `feat/visualiser-phase-i`         | `feat/visualiser-phase-h`                        |
| 11 | [#8 Phase J server — attachments](https://github.com/REBUS-ORBIT/prism/pull/8)          | `feat/visualiser-phase-j-server`  | `feat/visualiser-phase-i`                        |

---

## Final (base: `main` after every prior PR)

| # | PR                                                                       | Branch                       | Notes                                                                   |
| - | ------------------------------------------------------------------------ | ---------------------------- | ----------------------------------------------------------------------- |
| 12 | Phase K — release + docs + hardening (**this PR**)                      | `feat/visualiser-phase-k`    | Branched from `main` — additive-only; merges last to avoid base drift. |

After #12 lands, cut the `v0.2.0` tag at HEAD of `main`. See
[`RELEASE_STRATEGY.md`](RELEASE_STRATEGY.md) for the milestone-tag
procedure.

---

## Rebasing checklist

After each merge, **before** opening the next PR's diff for review:

```bash
git fetch origin
git checkout feat/visualiser-phase-<next>
git rebase origin/main         # or `git rebase origin/feat/visualiser-phase-<prev>` for stacked PRs
# Resolve conflicts (most likely in shared/contracts/, server/src/docs/openapi.ts,
# CHANGELOG.md, and web/src/admin/main.ts).
git push --force-with-lease
```

### Conflicts to expect

| File                                       | Why                                                                                         | Resolution                                                                                  |
| ------------------------------------------ | ------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `shared/contracts/agent-protocol.{cs,ts,json}` | Multiple phases append MessageType literals + data interfaces.                          | Take the union of both sides; re-run `npm run codegen:contracts` to refresh the generated JSON. |
| `server/src/docs/openapi.ts`                | Phase G adds visualiser schemas + paths; Phase K extends them; Phase J adds attachments. | Take Phase K's superset for visualiser sections; merge any non-K additions on top.          |
| `server/src/main.ts`                        | Multiple phases register new Fastify plugins.                                              | Take the union of `app.register(...)` lines.                                                |
| `web/src/admin/main.ts` + `App.vue`         | Multiple phases add new SPA routes + sidebar entries.                                      | Take the union of routes/entries.                                                           |
| `CHANGELOG.md`                              | Every PR adds a `## v0.1.NN` heading.                                                      | Order chronologically by version (highest at the top, under `## Unreleased`).               |

---

## Pre-merge verification

For each PR, before clicking Merge:

1.  CI is green (`agent-msi.yml`, `server-image.yml`, `visualiser-msi.yml`,
    `web-build.yml`).
2.  No `[skip ci]` commits in the diff.
3.  The PR's `CHANGELOG.md` entry matches its csproj / package.json
    version bump.
4.  Branch is rebased onto its declared base (the GitHub UI shows
    "This branch has no conflicts with the base branch" — if it shows
    conflicts, rebase locally first).

---

## Operator runbook for the v0.2.0 milestone

Once all 12 PRs are merged on `main`:

```bash
git checkout main
git pull origin main
git tag v0.2.0 -m "Visualiser GA"
git push origin v0.2.0
```

This fires both `agent.yml` (publishes the agent MSI / GitHub release)
and `server.yml` (builds + publishes `ghcr.io/rebus-orbit/prism-server:v0.2.0`).

For the orchestrator (which versions independently with the
`visualiser-` prefix):

```bash
git tag visualiser-v0.2.0 -m "Visualiser orchestrator GA"
git push origin visualiser-v0.2.0
```

See [`RELEASE_STRATEGY.md`](RELEASE_STRATEGY.md) for the full tag
strategy and the orchestrator version-continuity decision (the v0.5.0
orchestrator build can either be tagged `visualiser-v0.2.0` as a fresh
milestone or `visualiser-v0.5.0` to preserve semver continuity —
document either way in the release notes).
