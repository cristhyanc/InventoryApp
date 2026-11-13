#!/usr/bin/env bash
# Trusted Git publisher for the agent workflows. Workflows never run this file from the agent-edited
# checkout: each step that holds a GitHub App token first fetches it from a trusted commit (the
# workflow's own commit, or the trusted develop base) into $RUNNER_TEMP and runs that copy.
#
# Usage:
#   git-push-with-app-retry.sh <remote-url> <source>:<destination-ref>   push (retries on 403)
#   git-push-with-app-retry.sh --dry-run <remote-url> <source>:<destination-ref>
#   git-push-with-app-retry.sh --ls-remote <remote-url> <ref>
#
# Every credentialed Git command runs in a fresh bare repository that ignores system and global Git
# configuration, never in the agent's repository: its .git/config, hooks and attributes are agent-
# writable. The commit to publish is fetched from the agent's repository (the current directory, or
# AGENT_PUBLISH_SOURCE) through the upload-pack protocol, which re-hashes every object it receives,
# and only then pushed. When AGENT_PUBLISH_BASE_SHA is set, the published commit must descend from
# that base and no commit after it may touch .github, .agent-* or inventory.db.
set -euo pipefail

mode=push
case "${1:-}" in
  --dry-run) mode=dry-run; shift ;;
  --ls-remote) mode=ls-remote; shift ;;
esac

remote="${1:-}"
refspec="${2:-}"

[ -n "$remote" ] || { echo "::error::Git push retry helper requires a remote URL."; exit 2; }
[ -n "$refspec" ] || { echo "::error::Git push retry helper requires a refspec."; exit 2; }
[ -n "${GH_TOKEN:-}" ] || { echo "::error::GH_TOKEN is required for GitHub App push."; exit 2; }

auth="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w0)"
# ls-remote output is captured by the caller, so its mask command goes to stderr (the runner reads both).
if [ "$mode" = "ls-remote" ]; then echo "::add-mask::$auth" >&2; else echo "::add-mask::$auth"; fi

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/agent-publish.XXXXXX")"
trap 'rm -rf "$work"' EXIT

clean_git() {
  env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_OBJECT_DIRECTORY -u GIT_ALTERNATE_OBJECT_DIRECTORIES \
    -u GIT_CONFIG -u GIT_CONFIG_PARAMETERS -u GIT_CONFIG_COUNT -u GIT_EXEC_PATH -u GIT_TEMPLATE_DIR \
    GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_TERMINAL_PROMPT=0 \
    git --git-dir="$work/publish.git" -c credential.helper= -c core.hooksPath=/dev/null -c core.fsmonitor=false "$@"
}

clean_git init --quiet --bare --template= "$work/publish.git" >/dev/null

if [ "$mode" = "ls-remote" ]; then
  clean_git -c http.extraheader="AUTHORIZATION: basic $auth" ls-remote "$remote" "$refspec"
  exit 0
fi

source_spec="${refspec%%:*}"
destination="${refspec#*:}"
[ "$source_spec" != "$refspec" ] && [ -n "$source_spec" ] || { echo "::error::Refspec must be <source>:<destination>."; exit 2; }
[[ "$destination" =~ ^refs/heads/[A-Za-z0-9._/-]+$ ]] || { echo "::error::Destination must be a branch ref (refs/heads/...)."; exit 2; }

if [[ "$source_spec" =~ ^[0-9a-f]{40}$ ]]; then
  source_sha="$source_spec"
else
  # Compatibility with callers that pass HEAD: resolve it once, then publish that exact commit.
  source_sha="$(git -C "${AGENT_PUBLISH_SOURCE:-$PWD}" rev-parse --verify "$source_spec^{commit}")"
  [[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || { echo "::error::Could not resolve $source_spec."; exit 1; }
fi

source_repo="$(cd "${AGENT_PUBLISH_SOURCE:-$PWD}" && pwd)"
clean_git fetch --quiet --no-tags "file://$source_repo" "$source_sha" \
  || { echo "::error::Could not copy $source_sha out of the agent repository."; exit 1; }
[ "$(clean_git rev-parse --verify FETCH_HEAD)" = "$source_sha" ] || { echo "::error::Fetched commit does not match $source_sha."; exit 1; }
clean_git update-ref "refs/heads/publish" "$source_sha"

if [ -n "${AGENT_PUBLISH_BASE_SHA:-}" ]; then
  base="$AGENT_PUBLISH_BASE_SHA"
  [[ "$base" =~ ^[0-9a-f]{40}$ ]] || { echo "::error::AGENT_PUBLISH_BASE_SHA must be a 40-character SHA."; exit 2; }
  clean_git cat-file -e "$base^{commit}" 2>/dev/null || { echo "::error::Published commit does not contain trusted base $base."; exit 1; }
  clean_git merge-base --is-ancestor "$base" "$source_sha" || { echo "::error::Published commit does not descend from trusted base $base."; exit 1; }
  while read -r commit; do
    protected_bytes="$(clean_git diff-tree --root -r -m --no-commit-id --name-only -z "$commit" -- .github '.agent-*' inventory.db | wc -c)"
    [ "$protected_bytes" -eq 0 ] || { echo "::error::Commit $commit touches .github, .agent-* or inventory.db; refusing to publish."; exit 1; }
  done < <(clean_git rev-list "$base..$source_sha")
fi

push_args=(push)
[ "$mode" = "dry-run" ] && push_args+=(--dry-run)

delays=(0 2 5 10)
attempt=0

for delay in "${delays[@]}"; do
  attempt=$((attempt + 1))

  if [ "$delay" -gt 0 ]; then
    echo "::warning::Retrying GitHub App Git push after ${delay}s (attempt ${attempt}/${#delays[@]})."
    sleep "$delay"
  fi

  set +e
  output="$(clean_git -c http.extraheader="AUTHORIZATION: basic $auth" \
    "${push_args[@]}" "$remote" "$source_sha:$destination" 2>&1)"
  status=$?
  set -e

  printf '%s\n' "$output"

  if [ "$status" -eq 0 ]; then
    exit 0
  fi

  if [ "$mode" = "dry-run" ] || ! grep -Eqi '403|Permission to .* denied' <<<"$output"; then
    exit "$status"
  fi

  if [ "$attempt" -ge "${#delays[@]}" ]; then
    exit "$status"
  fi
done

exit 1
