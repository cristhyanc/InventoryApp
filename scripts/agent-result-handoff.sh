#!/usr/bin/env bash
# Restores an agent job's result inside a separate, trusted publish job.
#
# The Claude agent runs repository code it can edit, so nothing that runs after it in the same job
# can be trusted (it can change PATH, environment files, binaries or leave processes behind). The
# agent job therefore holds no GitHub App credential: it uploads its result as a plain-data
# artifact (a git bundle plus a few text files) and a later job, on a fresh runner, restores and
# re-verifies that data before it mints the App token. Workflows fetch this script from their own
# commit, never from a checkout.
#
# Usage (from a checkout of the trusted starting commit, with GH_TOKEN able to read Actions):
#   agent-result-handoff.sh restore <artifact-name>
#
# Writes to $GITHUB_OUTPUT: restored, branch, head_sha, worktree, has_bundle, metadata_dir.
# The run files (run-status, pr-title, pr-body.md) stay in metadata_dir, a private directory under
# $RUNNER_TEMP outside the checkout; nothing from the artifact is written into the working tree.
# Before anything is checked out, the new commits must descend from the trusted starting commit and
# must not touch .github, .agent-* or inventory.db, so a crafted tree (for example a symlink to
# .git/config) is never materialised. Every value in the artifact is untrusted: callers still verify
# the branch name and the run files before publishing.
set -euo pipefail

fail() { echo "::error::$1"; exit 1; }

mode="${1:-}"
name="${2:-}"
[[ "$mode" == "restore" ]] || fail "usage: agent-result-handoff.sh restore <artifact-name>"
[[ "$name" =~ ^[A-Za-z0-9._-]+$ ]] || fail "Invalid artifact name."
[[ "${GITHUB_OUTPUT:-}" != "" ]] || fail "GITHUB_OUTPUT is required."

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/agent-handoff.XXXXXX")"
result="$work/result"
mkdir "$result"

if [[ -n "${AGENT_RESULT_ZIP:-}" ]]; then
  # Tests pass the archive directly.
  cp -- "$AGENT_RESULT_ZIP" "$work/result.zip"
else
  [[ "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "GITHUB_REPOSITORY is required."
  [[ "${GITHUB_RUN_ID:-}" =~ ^[1-9][0-9]*$ ]] || fail "GITHUB_RUN_ID is required."
  ids="$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/artifacts?name=$name&per_page=100" \
    --jq '[.artifacts[] | select(.expired == false) | .id] | map(tostring) | join(" ")')"
  read -r -a id_list <<<"$ids"
  [[ "${#id_list[@]}" -eq 1 ]] || fail "Expected exactly one agent result artifact named $name in this run, found ${#id_list[@]}."
  [[ "${id_list[0]}" =~ ^[0-9]+$ ]] || fail "Unexpected artifact id."
  gh api "repos/$GITHUB_REPOSITORY/actions/artifacts/${id_list[0]}/zip" > "$work/result.zip"
fi

# Only plain files with known names are accepted: no directories, links or devices.
python3 -I - "$work/result.zip" "$result" <<'PY'
import stat, sys, zipfile
allowed = {"branch", "head_sha", "worktree", "result.bundle", "run-status", "pr-title", "pr-body.md"}
limits = {"result.bundle": 512 * 1024 * 1024, "pr-body.md": 1024 * 1024}
archive, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(archive) as zf:
    names = set()
    for info in zf.infolist():
        mode = info.external_attr >> 16
        if info.filename not in allowed or info.is_dir() or stat.S_ISLNK(mode) or info.filename in names:
            sys.exit(f"::error::Unexpected entry in the agent result: {info.filename!r}")
        if info.file_size > limits.get(info.filename, 64 * 1024):
            sys.exit(f"::error::Agent result entry {info.filename} is too large")
        names.add(info.filename)
        with zf.open(info) as source, open(f"{target}/{info.filename}", "xb") as dest:
            dest.write(source.read())
for required in ("branch", "head_sha", "worktree"):
    if required not in names:
        sys.exit(f"::error::The agent result has no {required} entry")
PY

branch="$(head -n 1 "$result/branch")"
head_sha="$(head -n 1 "$result/head_sha")"
worktree="$(head -n 1 "$result/worktree")"
[[ "$(wc -l < "$result/branch")" -le 1 && "$(wc -l < "$result/head_sha")" -le 1 ]] || fail "Malformed agent result."
[[ "$head_sha" =~ ^[0-9a-f]{40}$ ]] || fail "The agent result names no valid head SHA."
[[ -z "$branch" || ( "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ && "$branch" != *..* && "$branch" != *.lock ) ]] || fail "The agent result names an invalid branch."
[[ "$worktree" == "clean" || "$worktree" == "dirty" ]] || fail "The agent result has an invalid working-tree state."

safe_git() { git -c core.hooksPath=/dev/null -c core.fsmonitor=false "$@"; }

base_sha="$(safe_git rev-parse --verify 'HEAD^{commit}')"
has_bundle=false
if [[ -f "$result/result.bundle" ]]; then
  [[ -n "$branch" ]] || fail "A result bundle needs a branch."
  has_bundle=true
  safe_git bundle verify --quiet "$result/result.bundle" >/dev/null 2>&1 || fail "The result bundle does not apply to the trusted starting commit."
  # Fetched into a private ref, not a branch, so a refused result leaves the checkout untouched.
  safe_git fetch --quiet --no-tags "$result/result.bundle" "refs/heads/$branch:refs/agent-result/head" \
    || fail "The result bundle does not contain branch $branch."
  [[ "$(safe_git rev-parse --verify "refs/agent-result/head^{commit}")" == "$head_sha" ]] || fail "The result bundle does not match the reported head."
  safe_git merge-base --is-ancestor "$base_sha" "$head_sha" || fail "The agent result does not descend from the trusted starting commit."
  while IFS= read -r commit; do
    protected_bytes="$(safe_git diff-tree --root -r -m --no-commit-id --name-only -z "$commit" -- .github '.agent-*' inventory.db | wc -c)"
    [[ "$protected_bytes" -eq 0 ]] || fail "Agent commit $commit touches .github, .agent-* or inventory.db; refusing to check it out."
  done < <(safe_git rev-list "$base_sha..$head_sha")
  safe_git update-ref -d refs/agent-result/head
  safe_git checkout --quiet -B "$branch" "$head_sha" --
else
  [[ "$(safe_git rev-parse HEAD)" == "$head_sha" ]] || fail "The agent reported a new head but uploaded no bundle."
  if [[ -n "$branch" ]]; then
    safe_git checkout --quiet -B "$branch" --
  fi
fi

{
  echo "restored=true"
  echo "branch=$branch"
  echo "head_sha=$head_sha"
  echo "worktree=$worktree"
  echo "has_bundle=$has_bundle"
  echo "metadata_dir=$result"
} >> "$GITHUB_OUTPUT"
echo "Restored agent result: branch '${branch:-<none>}' at $head_sha (bundle: $has_bundle, working tree: $worktree)."
