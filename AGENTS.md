# AGENTS.md

Rules for AI agents working on MusicSpin. Build, test and layout details are in
[docs/building.md](docs/building.md) and [docs/architecture.md](docs/architecture.md).

## Roles

- **Opus (T3 Code)** implements UI issues and opens the big UI PRs.
- **Grok Bot** reviews Opus's PRs and leaves comments only. It never pushes to
  someone else's branch. It also watches CI on `main` and reports problems to
  Gabriel. It only fixes them when he asks.
- **Gabriel** merges everything. Agents never merge.

## Claiming work

1. Before you start an issue, add your label (`agent:opus` or `agent:grok`)
   and leave a short comment saying you're on it.
2. Skip any issue that already has an `agent:*` label.

## Comment tags

Both agents post under Gabriel's GitHub account. Every comment an agent posts
(on issues, PRs, reviews and review replies) starts with its name: `Grok Bot: `
or `Opus: `. Comments without a tag are Gabriel's.

## Branches

- Grok Bot's branches start with `grok/`. Opus keeps its usual names.
- Never push to another agent's branch. Leave a review comment instead.

## Review loop

- Grok Bot reviews each PR when it opens and again when new commits land.
- Opus answers every review comment, either with a fix or with a reply
  explaining why not.
- Gabriel merges once the review threads are settled and CI is green.

## CI failures on `main`

- Report the cause to Gabriel: the failing job, the test and the likely reason.
- Open a fix PR only if nobody has claimed the failure and Gabriel approves.

## Commits

- Author: `Gabriel Vaduva <vgabriel12@outlook.com>`.
- No force-pushes. Fix things with new commits.
- Tests must cover unhappy paths, not just the happy path.
