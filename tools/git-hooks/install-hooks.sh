#!/bin/sh
# Installs Hoard's commit-msg hook (tools/git-hooks/commit-msg) into this clone.
#
# Run by the SessionStart hook in .claude/settings.json, so it executes on every machine and on every
# session — which is exactly why it has to be careful about what it overwrites. Three rules, each of
# them a way this could otherwise damage someone's setup:
#
#   1. It only writes INSIDE this repository. `git rev-parse --git-path hooks` honours
#      core.hooksPath, and that is commonly pointed at a shared machine-wide hook directory
#      (commitlint, secret scanning). Installing there would apply Hoard's hook to every repo on the
#      machine and could replace the hook that directory exists to provide.
#   2. It never overwrites a commit-msg hook it didn't write — identified by the `hoard-managed-hook`
#      marker line in the canonical copy. Its own stale copies it updates (otherwise every edit to the
#      canonical hook would leave every clone pinned to the old one); anything without the marker
#      belongs to somebody else, so it is left alone and reported.
#   3. It resolves its own source from the repo root, so it behaves the same started from any
#      subdirectory.
#
# It always exits 0: a session must never fail to start over a hook install. When it declines it says
# so on stderr, because silently doing nothing while CLAUDE.md advertises deterministic enforcement is
# the worse failure.

root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
[ -n "$root" ] || exit 0

src="$root/tools/git-hooks/commit-msg"
[ -f "$src" ] || exit 0

hooks=$(git rev-parse --git-path hooks 2>/dev/null) || exit 0
# --git-path yields a path relative to the CWD unless core.hooksPath is absolute.
case "$hooks" in
    /*) ;;
    *)  hooks="$(pwd)/$hooks" ;;
esac

# Rule 1: refuse to write outside the repository (a shared/global core.hooksPath).
case "$hooks" in
    "$root"/*) ;;
    *)
        echo "Hoard: core.hooksPath points outside this repo ($hooks) — leaving it alone." >&2
        echo "       To enforce commit-message attribution stripping here, install it yourself:" >&2
        echo "       cp '$src' '$hooks/commit-msg' && chmod +x '$hooks/commit-msg'" >&2
        exit 0
        ;;
esac

dst="$hooks/commit-msg"

if [ -e "$dst" ]; then
    # Already ours and current: nothing to do. The common case, every session after the first.
    cmp -s "$src" "$dst" && exit 0

    # Rule 2: ours to update, or somebody else's to leave alone.
    # The second pattern recognises copies we shipped BEFORE the marker existed — every version of
    # this hook has referred to its canonical path — so clones installed by the old naive `cp` are
    # updated rather than being stranded on it forever as "somebody else's".
    if ! grep -qE 'hoard-managed-hook|tools/git-hooks/commit-msg' "$dst" 2>/dev/null; then
        echo "Hoard: a different commit-msg hook is already installed at $dst — leaving it alone." >&2
        echo "       Merge in tools/git-hooks/commit-msg by hand if you want both." >&2
        exit 0
    fi
fi

mkdir -p "$hooks" 2>/dev/null || exit 0
cp "$src" "$dst" 2>/dev/null && chmod +x "$dst" 2>/dev/null
exit 0
