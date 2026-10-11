#!/usr/bin/env bash
# Deletes an issue's implementation storage branches once nothing depends on them:
#   agent/issue-<n>-checkpoint-<run>-<attempt> and agent/recovery-<n>-<run>-<attempt>.
#
# Usage: agent-branch-cleanup.sh <remote-url> <issue-number> <mode> [head-sha]
#   contained  delete only branches whose commit is an ancestor of <head-sha>, i.e. work that is
#              fully contained in a published pull request head and can be recovered from it.
#   all        delete every storage branch of the issue (used once the issue's pull request merged).
#
# Each deletion is re-checked against the SHA listed immediately before it and never force-pushes.
# Cleanup is best-effort housekeeping: a branch that cannot be inspected or deleted is reported as a
# warning and left in place, and the script still exits 0 so it never blocks the agent lifecycle.
set -euo pipefail

remote="${1:-}"
issue="${2:-}"
mode="${3:-}"
head_sha="${4:-}"

[ -n "$remote" ] || { echo "::error::Branch cleanup requires a remote URL."; exit 2; }
[[ "$issue" =~ ^[1-9][0-9]*$ ]] || { echo "::error::Branch cleanup requires a valid issue number."; exit 2; }
case "$mode" in
  contained) [[ "$head_sha" =~ ^[0-9a-f]{40}$ ]] || { echo "::error::Contained cleanup requires a 40-character head SHA."; exit 2; } ;;
  all) ;;
  *) echo "::error::Branch cleanup mode must be 'contained' or 'all'."; exit 2 ;;
esac

git_remote() {
  if [ -n "${GH_TOKEN:-}" ]; then
    local auth
    auth="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w0)"
    echo "::add-mask::$auth"
    git -c credential.helper= -c core.hooksPath=/dev/null -c http.extraheader="AUTHORIZATION: basic $auth" "$@"
  else
    git -c credential.helper= -c core.hooksPath=/dev/null "$@"
  fi
}

refs="$(git_remote ls-remote --heads "$remote" \
  "refs/heads/agent/issue-$issue-checkpoint-*" "refs/heads/agent/recovery-$issue-*")" \
  || { echo "::warning::Could not list storage branches for issue #$issue; nothing was deleted."; exit 0; }

if [ "$mode" = "contained" ] && [ -n "$refs" ] && ! git cat-file -e "$head_sha^{commit}" 2>/dev/null; then
  # A closed pull request's head stays fetchable by SHA through refs/pull/<n>/head.
  git_remote fetch --quiet --no-tags "$remote" "$head_sha" >/dev/null 2>&1 || true
fi

deleted=0
kept=0
while read -r sha ref; do
  [ -n "${ref:-}" ] || continue
  branch="${ref#refs/heads/}"
  # ls-remote patterns are globs; keep only exact storage names for this issue.
  [[ "$branch" =~ ^agent/(issue-$issue-checkpoint|recovery-$issue)-[1-9][0-9]*-[1-9][0-9]*$ ]] || continue

  if [ "$mode" = "contained" ]; then
    if ! git cat-file -e "$sha^{commit}" 2>/dev/null; then
      git_remote fetch --quiet --no-tags "$remote" "$ref" >/dev/null 2>&1 || true
    fi
    if ! git cat-file -e "$sha^{commit}" 2>/dev/null || ! git merge-base --is-ancestor "$sha" "$head_sha" 2>/dev/null; then
      echo "Keeping $branch ($sha): not contained in $head_sha."
      kept=$((kept + 1))
      continue
    fi
  fi

  current="$(git_remote ls-remote "$remote" "$ref" || true)"
  if [ "${current%%$'\t'*}" != "$sha" ]; then
    echo "::warning::Keeping $branch: it moved while cleanup was running."
    kept=$((kept + 1))
    continue
  fi
  if git_remote push --quiet "$remote" --delete "$ref"; then
    echo "Deleted $branch ($sha)."
    deleted=$((deleted + 1))
  else
    echo "::warning::Could not delete $branch; it was left in place."
    kept=$((kept + 1))
  fi
done <<< "$refs"

echo "Storage branch cleanup for issue #$issue ($mode): deleted $deleted, kept $kept."
