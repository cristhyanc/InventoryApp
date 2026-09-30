#!/usr/bin/env bash
set -euo pipefail

remote="${1:-}"
refspec="${2:-}"

[ -n "$remote" ] || { echo "::error::Git push retry helper requires a remote URL."; exit 2; }
[ -n "$refspec" ] || { echo "::error::Git push retry helper requires a refspec."; exit 2; }
[ -n "${GH_TOKEN:-}" ] || { echo "::error::GH_TOKEN is required for GitHub App push."; exit 2; }

auth="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w0)"
echo "::add-mask::$auth"

delays=(0 2 5 10)
attempt=0

for delay in "${delays[@]}"; do
  attempt=$((attempt + 1))

  if [ "$delay" -gt 0 ]; then
    echo "::warning::Retrying GitHub App Git push after ${delay}s (attempt ${attempt}/${#delays[@]})."
    sleep "$delay"
  fi

  set +e
  output="$(git -c credential.helper= -c core.hooksPath=/dev/null \
    -c http.extraheader="AUTHORIZATION: basic $auth" \
    push "$remote" "$refspec" 2>&1)"
  status=$?
  set -e

  printf '%s\n' "$output"

  if [ "$status" -eq 0 ]; then
    exit 0
  fi

  if ! grep -Eqi '403|Permission to .* denied' <<<"$output"; then
    exit "$status"
  fi

  if [ "$attempt" -ge "${#delays[@]}" ]; then
    exit "$status"
  fi
done

exit 1
