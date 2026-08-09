# Contributing to Hoard

Thanks for looking. Hoard is a spare-time project, so the honest expectation-setting first: issues and
small, focused pull requests are very welcome; large redesigns are likely to sit unmerged unless we've
talked about them in an issue beforehand.

## Getting set up

```bash
dotnet build Hoard.slnx                   # the solution is .slnx, not .sln
dotnet test  Hoard.slnx                   # 300-odd tests, all should pass
dotnet run --project src/Hoard.Desktop    # run from a terminal so logs stream
```

The build downloads gallery-dl (~24 MB) on first run. You need the **.NET 10 SDK** and nothing else.

**The running app locks its own DLLs**, so a full build fails with `MSB3027` while Hoard is open. Build a
test project while iterating, or close the app first.

## Before you write code

Read [`CLAUDE.md`](CLAUDE.md). It's long, but it's the architecture *and the reasoning* — most of it
exists because something was tried, broke in a non-obvious way, and the note is there so it doesn't get
re-broken. In particular:

- `Hoard.Core` must stay platform-neutral — no subprocess, no P/Invoke, no Avalonia. That boundary is
  what keeps a future mobile client possible.
- The archive is append-only. Never mutate or delete ops.
- Schema changes are additive, via `PRAGMA user_version` — not EF migrations.
- UI work: read [`DESIGN.md`](DESIGN.md) first, and never hardcode a colour, radius or spacing. Bind a
  token from `Theme/Tokens.axaml`, or add one.

Hoard archives **Pinterest**, deliberately. It isn't a general media archiver and PRs adding other
sources won't be merged — see the note at the top of `CLAUDE.md`.

## Commits

Hoard releases with [release-please](https://github.com/googleapis/release-please), so commit messages
are load-bearing: they decide the version bump and they become the changelog.

Use [Conventional Commits](https://www.conventionalcommits.org/) — `feat:`, `fix:`, `perf:`, `docs:`,
`refactor:`, `chore:`, `test:`, with an optional scope. **The subject of a `feat:`, `fix:` or `perf:`
commit is copied verbatim into the changelog**, so write it as a user-visible statement, not an
implementation note:

```
good:  fix(board): folder covers refresh after a pin moves out
bad:   fix: refactor cover loading + fix refresh bug + tidy VM
```

Implementation detail belongs in the commit body. `docs:`, `chore:`, `refactor:` and `test:` never reach
the changelog, so they can be as terse as you like.

Please also spell things in **British/Australian English** in identifiers, comments and UI strings, to
match what's already there.

## Licensing of your contribution

Hoard is licensed under [GPL-3.0](LICENSE), and contributions are accepted under the same licence.

**You keep the copyright in your contribution.** By submitting one, you confirm that:

1. You wrote it, or you otherwise have the right to submit it under GPL-3.0 (it isn't copied from
   incompatible code, and if your employer has rights to your work, you have their permission); and
2. You grant the project maintainer (**@adanub**) a perpetual, worldwide, non-exclusive, royalty-free,
   irrevocable licence to use, reproduce, modify, distribute and sublicense your contribution, **including
   the right to release it under different licence terms** in future versions of Hoard.

Point 2 exists so the project isn't permanently frozen on one licence. Without it, changing licence later
would need written agreement from every past contributor, which in practice means it can never happen.
It does **not** let anyone take away what's already published: everything released under GPL-3.0 stays
available under GPL-3.0, forever.

If you aren't comfortable with that grant, open an issue instead — a well-described bug report is genuinely
valuable and carries none of this.
