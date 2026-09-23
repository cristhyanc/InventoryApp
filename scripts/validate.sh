#!/usr/bin/env bash
#
# Complete local validation for InventoryApp (macOS, Linux, Git Bash, CI).
#
# Pipeline:
#   1. Restore backend
#   2. Verify C# formatting          (dotnet format --verify-no-changes)
#   3. Build backend                 (analyzers + warnings-as-errors, see Directory.Build.props)
#   4. Test backend with coverage    (includes the architecture tests)
#   5. Check vulnerable NuGet packages
#   6. Install frontend dependencies (npm ci)
#   7. Angular ESLint                (npm run lint)
#   8. Angular production build      (npm run build)
#   9. npm audit report
#
# scripts/validate.ps1 performs the same checks on Windows PowerShell. Keep the two in step.

set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
backend_solution="${repo_root}/backend/InventoryApi/InventoryApi.slnx"
# `dotnet format --exclude` matches paths relative to the working directory, not absolute ones,
# so the formatting step runs from the repository root with this relative path.
migrations_dir="backend/InventoryApi/Migrations"
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

# `dotnet package list --vulnerable` prints its findings but always exits 0, so the result has
# to be read out of its output. Being unable to run the query at all (normally no network
# access to nuget.org) is reported but does not fail validation; an actual vulnerable package
# does.
check_vulnerable_packages() {
    local output
    if ! output="$(dotnet package list --project "${backend_solution}" --vulnerable --include-transitive 2>&1)"; then
        echo "${output}"
        echo
        echo "WARNING: the NuGet vulnerability query did not complete (this check needs access" >&2
        echo "         to nuget.org). Skipping it; rerun validation when online." >&2
        return 0
    fi

    echo "${output}"

    if grep -q "has the following vulnerable packages" <<<"${output}"; then
        echo
        echo "Vulnerable NuGet packages reported above." >&2
        return 1
    fi

    return 0
}

# npm audit is reported, not enforced. See README.md ("Validate a change"): every outstanding high/critical
# advisory is in the Angular 19 build toolchain and only clears with a major Angular upgrade.
report_npm_audit() {
    npm --prefix "${frontend_dir}" audit || true
    echo
    echo 'NOTE: npm audit is informational and does not fail validation. See README.md (Validate a change).'
}

run_step "Restore backend" \
    dotnet restore "${backend_solution}"

# Generated EF Core migrations are excluded: they are not hand-maintained, and AGENTS.md
# forbids rewriting an applied migration.
verify_csharp_formatting() {
    (
        cd "${repo_root}"
        dotnet format "${backend_solution}" --verify-no-changes --no-restore --exclude "${migrations_dir}"
    )
}

run_step "Verify C# formatting" \
    verify_csharp_formatting

run_step "Build backend (${configuration})" \
    dotnet build "${backend_solution}" --configuration "${configuration}" --no-restore

run_step "Test backend with coverage (${configuration})" \
    dotnet test "${backend_solution}" --configuration "${configuration}" --no-build --no-restore \
    --collect:"XPlat Code Coverage"

run_step "Check vulnerable NuGet packages" \
    check_vulnerable_packages

run_step "Install frontend dependencies" \
    npm --prefix "${frontend_dir}" ci

run_step "Lint frontend" \
    npm --prefix "${frontend_dir}" run lint

run_step "Build frontend" \
    npm --prefix "${frontend_dir}" run build

run_step "Audit frontend dependencies (report only)" \
    report_npm_audit

echo
echo "Validation succeeded."
