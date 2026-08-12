---
name: retro
description: End-of-session retrospective for this repo. Reviews what actually happened in the session and proposes concrete, evidence-backed improvements to AI tooling (Claude Code config, hooks, permissions), skills, and documentation (CLAUDE.md, DESIGN.md, README, doc comments). Use at the end of a working session, or when the user asks for a retro, a post-mortem, or "what did we learn".
---

# Retrospective

Turn one session's friction into durable changes to how work happens here — the tooling, the skills, and
the docs. **Not** a summary of what was built, and **not** a code review of the diff (that's `/code-review`).

## The bar

One rule governs everything below: **every proposal must cite something that actually happened in this
session**, and must name a specific file to change. If you cannot point at the moment that motivated it,
it does not go in the list.

Concretely — reject a proposal if it:

- restates advice that already exists in `CLAUDE.md` / `DESIGN.md` (the fix for a rule that was written
  down and broken anyway is a **check**, not louder prose — see the ladder below);
- is generic craft advice ("add more tests", "improve error handling", "communicate assumptions");
- speculates about problems the session didn't hit;
- would add prose to `CLAUDE.md` that duplicates an existing bullet. That file is already very large;
  **prefer correcting or deleting an existing passage over appending a new one.**

**Finding nothing that clears the bar is a valid, useful result.** Say so plainly and stop. A retro that
always produces five items trains the reader to skip it.

## Step 1 — gather the evidence

Do this before forming any opinion.

1. `git status --short` and `git diff --stat` — what actually changed.
2. Re-read the session with these questions in mind:
   - **Where did the user correct me?** Especially the same thing twice, or a claim of mine they had to
     challenge (that's the strongest signal available — it means my output was confidently wrong).
   - **What did I get wrong that a rule already covered?** A documented invariant that was violated anyway
     is the highest-value finding in this whole exercise.
   - **What cost tool calls or rework?** Failed commands, missing binaries, wrong paths, assumptions that
     had to be unwound, research I did twice.
   - **What could not be verified here, and what would have made it verifiable?** This repo can't launch
     the GUI, and some binaries are sandbox-blocked; dev affordances that close that gap (an env-var demo
     mode, a pure extracted function, a fake) are real, reusable wins.
   - **What did a review or a later step catch that I had already called done?**
3. **Hunt stale documentation directly** — do not rely on memory. For each symbol, flag, or behaviour the
   diff changed, grep the docs for it:
   `Grep` the changed names across `CLAUDE.md`, `DESIGN.md`, `README.md`, `THIRD-PARTY-NOTICES.md`, and
   XML doc comments near the change. A behaviour change that leaves a doc asserting the old behaviour is a
   defect, not a tidiness issue — it will mislead the next reader, who may be you.

## Step 2 — the fix ladder

This repo pins its invariants in a specific order of preference. Propose the **cheapest durable** rung
that would actually have caught the problem, and say which rung you chose:

1. **A test** — best. Pure logic extracted and asserted (`MasonryPacker`, `UpdatePolicy`,
   `BreadcrumbTrimmer` are the pattern). Survives refactors and rewrites; nobody has to read it.
   **"There's no test seam" is not a reason to drop a rung** — creating one is usually the fix. If the rule
   only holds at a layer that can't be tested (a XAML binding, a visual-tree behaviour), move the invariant
   to a view model or a pure function and assert it there; the view then just binds the tested property.
   `BusyGateTests` over `IsPromptBusy`/`ShowUpdateBusy` is the worked example.
2. **An automated check** — a hook, a grep-based guard, an MSBuild target, a CI step. Right when the rule
   is mechanical but has no natural test seam (e.g. "never bind a `BusyBar`'s visibility to an ancestor").
3. **A permission / settings change** — `.claude/settings.json` allowlist entries for commands that
   prompted repeatedly, env vars, hooks.
4. **A skill** — when a multi-step procedure was reconstructed from scratch this session and will recur.
5. **A doc change** — last resort for genuinely non-mechanical judgement. Correct or delete in place
   where possible.

A rule that was already written down and still got broken has, by definition, failed at rung 5. Don't
propose rung 5 again for it.

## Step 3 — categorise

Sort what survives into the three buckets the user asked for. Some will be empty; leave them empty.

- **AI tooling** — `.claude/settings.json` (permissions, env, hooks), MCP servers, what to put in
  `CLAUDE.md`'s environment notes, anything that would have saved tool calls or a failed command.
- **Skills** — new skills worth adding, or edits to existing ones under `.claude/skills/`. A skill earns
  its place only if the procedure is repeatable and was genuinely reconstructed this session.
- **Documentation** — `CLAUDE.md`, `DESIGN.md`, `README.md`, `THIRD-PARTY-NOTICES.md`, doc comments.
  Stale-doc corrections outrank additions. Note deletions explicitly; they're wins too.

## Step 4 — report

Lead with a two-line honest read of the session — including what went badly. Then, per bucket, a short
numbered list. For each item:

- **What** — the proposed change, in one line.
- **Why** — the specific moment that motivated it (quote the correction, name the file, cite the failed
  command). No evidence, no item.
- **Where** — the exact file to change, and the rung from Step 2.
- **Cost** — rough size, so the user can triage.

Order by expected value, not by bucket. Keep the whole thing scannable — if it's longer than a screen or
two, the bar was set too low.

## Step 5 — offer, don't apply

Stop after the report and ask which items to action. These are changes to the user's tooling and their
repo's docs; the retro proposes, the user disposes. Apply only what they pick.

Do not commit anything: this repo's convention is that commits are the user's explicit call, after
runtime verification.

## Anti-patterns

- **Praising the session.** Skip it. The useful half is the friction.
- **Rewriting history charitably.** If a claim of mine was wrong and the user caught it, name it as that.
- **Bloating `CLAUDE.md`.** Every addition competes for attention with everything already in it.
- **Proposing process.** "Review more carefully before handing over" is not a change; a check is.
- **Filing the same finding under two buckets** to make the list look fuller.
