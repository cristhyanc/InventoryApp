#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
backend_solution="${repo_root}/backend/InventoryApi/InventoryApi.slnx"
frontend_dir="${repo_root}/frontend/inventory-app"
configuration="${CONFIGURATION:-Release}"

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Required command not found: $1" >&2
        exit 127
    fi
}

run_step() {
    local label="$1"
    shift
    echo
    echo "==> ${label}"
    "$@"
}

require_command dotnet
require_command npm

if [[ ! -f "${backend_solution}" ]]; then
    echo "Backend solution not found: ${backend_solution}" >&2
    exit 2
fi

if [[ ! -f "${frontend_dir}/package-lock.json" ]]; then
    echo "Frontend lock file not found: ${frontend_dir}/package-lock.json" >&2
    exit 2
fi

run_step "Restore backend" \
    dotnet restore "${backend_solution}"

run_step "Build backend (${configuration})" \
    dotnet build "${backend_solution}" --configuration "${configuration}" --no-restore

run_step "Test backend (${configuration})" \
    dotnet test "${backend_solution}" --configuration "${configuration}" --no-build --no-restore

run_step "Install frontend dependencies" \
    npm --prefix "${frontend_dir}" ci

run_step "Build frontend" \
    npm --prefix "${frontend_dir}" run build

echo
echo "Validation succeeded."

