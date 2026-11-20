<#
.SYNOPSIS
    Complete local validation for InventoryApp (Windows PowerShell / PowerShell 7).

.DESCRIPTION
    Pipeline:
      1. Run the InventoryApp Agent Evals corpus and runner tests
      2. Check documentation links and anchors (scripts/check-doc-links.mjs)
      3. Validate agent workflow, runner placement and deployment workflow contracts
         (the same offline contract checks CI runs in .github/workflows/validate.yml)
      4. Restore backend
      5. Verify C# formatting          (dotnet format --verify-no-changes)
      6. Build backend                 (analyzers + warnings-as-errors, see Directory.Build.props)
      7. Test backend with coverage    (Inventory.UnitTests and Inventory.IntegrationTests; the latter includes the architecture tests)
      8. Check vulnerable NuGet packages
      9. Install frontend dependencies (npm ci)
     10. Angular ESLint                (npm run lint)
     11. Frontend unit/component tests (npm run test)
     12. Angular production build      (npm run build)
     13. npm audit report

    scripts/validate.sh performs the same checks on macOS, Linux, Git Bash and CI.
    Keep the two in step.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryRoot = Split-Path -Parent $ScriptDirectory
$BackendSolution = Join-Path $RepositoryRoot 'backend/InventoryApi/InventoryApi.slnx'
# `dotnet format --exclude` matches paths relative to the working directory, not absolute ones,
# so the formatting step runs from the repository root with this relative path.
$MigrationsRelativePath = 'backend/Inventory.Infrastructure/Migrations'
$FrontendDirectory = Join-Path $RepositoryRoot 'frontend/inventory-app'
$FrontendLockFile = Join-Path $FrontendDirectory 'package-lock.json'

function Assert-Command {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][scriptblock]$Command
    )

    Write-Host ""
    Write-Host "==> $Label"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][scriptblock]$Command
    )

    Write-Host ""
    Write-Host "==> $Label"
    & $Command
}

# `dotnet package list --vulnerable` prints its findings but always exits 0, so the result has
# to be read out of its output. Being unable to run the query at all (normally no network
# access to nuget.org) is reported but does not fail validation; an actual vulnerable package
# does.
function Test-VulnerableNuGetPackages {
    $output = & dotnet package list --project $BackendSolution --vulnerable --include-transitive
    $queryExitCode = $LASTEXITCODE

    $output | ForEach-Object { Write-Host $_ }

    if ($queryExitCode -ne 0) {
        Write-Host ""
        Write-Warning "The NuGet vulnerability query did not complete (this check needs access to nuget.org). Skipping it; rerun validation when online."
        return
    }

    if ($output -match 'has the following vulnerable packages') {
        Write-Host ""
        throw 'Vulnerable NuGet packages reported above.'
    }
}

# npm audit is reported, not enforced. See README.md ("Validate a change"): every outstanding high/critical
# advisory is in the Angular 19 build toolchain and only clears with a major Angular upgrade.
function Show-NpmAudit {
    & npm --prefix $FrontendDirectory audit
    Write-Host ""
    Write-Host 'NOTE: npm audit is informational and does not fail validation. See README.md ("Validate a change").'
}

Assert-Command -Name 'dotnet'
Assert-Command -Name 'npm'
Assert-Command -Name 'node'

if (-not (Test-Path -LiteralPath $BackendSolution -PathType Leaf)) {
    throw "Backend solution not found: $BackendSolution"
}

if (-not (Test-Path -LiteralPath $FrontendLockFile -PathType Leaf)) {
    throw "Frontend lock file not found: $FrontendLockFile"
}

Invoke-ExternalCommand -Label 'Test agent eval runner' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/run-agent-evals.test.mjs')
}

Invoke-ExternalCommand -Label 'Run agent eval corpus' -Command {
    & node (Join-Path $RepositoryRoot 'scripts/run-agent-evals.mjs')
}

# Unit tests only: the model-decision evals themselves call a model and run on demand.
Invoke-ExternalCommand -Label 'Test model-decision eval runner' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/run-agent-model-evals.test.mjs')
}

# Unit tests only: the review-prompt evals themselves call a model and run on demand.
Invoke-ExternalCommand -Label 'Test review-prompt eval runner' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/run-agent-review-evals.test.mjs')
}

Invoke-ExternalCommand -Label 'Test Project status sync' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/sync-project-status.test.mjs')
}

# Fixture-driven only: the JB Metro lookup's tests never call a supplier's site.
Invoke-ExternalCommand -Label 'Test JB Metro price lookup' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/price-collector/jbmetro-lookup.test.mjs')
}

# Stub-driven only: the collector scripts' tests answer themselves over a loopback HTTP stub and
# never call Restockroo, Apify or a supplier.
Invoke-ExternalCommand -Label 'Test price collector scripts' -Command {
    & node --test `
        (Join-Path $RepositoryRoot 'scripts/price-collector/get-work.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/submit-results.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/apify-guard.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/output-schema.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/pagination.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/workflow.test.mjs') `
        (Join-Path $RepositoryRoot 'scripts/price-collector/workflow-contract.test.mjs')
}

Invoke-ExternalCommand -Label 'Test documentation link checker' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/check-doc-links.test.mjs')
}

Invoke-ExternalCommand -Label 'Check documentation links and anchors' -Command {
    & node (Join-Path $RepositoryRoot 'scripts/check-doc-links.mjs')
}

# The same offline contract checks .github/workflows/validate.yml runs in its "Validate agent
# workflow contracts", "Validate runner placement contract" and "Validate deployment workflow
# contracts" steps, so a broken contract fails here instead of only in CI. The
# documentation-impact gate on the pull request body stays CI-only: it needs the pull request
# body, which is not available locally.
Invoke-ExternalCommand -Label 'Validate agent workflow contracts' -Command {
    & node (Join-Path $RepositoryRoot 'scripts/validate-agent-workflows.mjs')
}

Invoke-ExternalCommand -Label 'Test agent workflow contract validator' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/validate-agent-workflows.test.mjs')
}

Invoke-ExternalCommand -Label 'Test documentation-impact validator' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/validate-documentation-impact.test.mjs')
}

Invoke-ExternalCommand -Label 'Test Sonar new-issues validator' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/sonar-new-issues.test.mjs')
}

Invoke-ExternalCommand -Label 'Validate runner placement contract' -Command {
    & node (Join-Path $RepositoryRoot 'scripts/validate-runner-placement.mjs')
}

Invoke-ExternalCommand -Label 'Test runner placement contract validator' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/validate-runner-placement.test.mjs')
}

Invoke-ExternalCommand -Label 'Validate deployment workflow contracts' -Command {
    & node (Join-Path $RepositoryRoot 'scripts/validate-deployment-workflows.mjs')
}

Invoke-ExternalCommand -Label 'Test deployment workflow contract validator' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/validate-deployment-workflows.test.mjs')
}

Invoke-ExternalCommand -Label 'Test deployment migration preflight' -Command {
    & node --test (Join-Path $RepositoryRoot 'scripts/deployment-migration-preflight.test.mjs')
}

Invoke-ExternalCommand -Label 'Restore backend' -Command {
    & dotnet restore $BackendSolution
}

# Generated EF Core migrations are excluded: they are not hand-maintained, and AGENTS.md
# forbids rewriting an applied migration.
Invoke-ExternalCommand -Label 'Verify C# formatting' -Command {
    Push-Location -LiteralPath $RepositoryRoot
    try {
        & dotnet format $BackendSolution --verify-no-changes --no-restore --exclude $MigrationsRelativePath
    }
    finally {
        Pop-Location
    }
}

Invoke-ExternalCommand -Label "Build backend ($Configuration)" -Command {
    & dotnet build $BackendSolution --configuration $Configuration --no-restore
}

# A test that runs past the hang timeout is a hang, not a slow test (see scripts/validate.sh): the
# run stops with a full dump of the test host and a sequence file under the project's TestResults.
Invoke-ExternalCommand -Label "Test backend with coverage ($Configuration)" -Command {
    & dotnet test $BackendSolution --configuration $Configuration --no-build --no-restore `
        --collect:"XPlat Code Coverage" `
        --blame-hang --blame-hang-timeout 90s --blame-hang-dump-type full
}

Invoke-Step -Label 'Check vulnerable NuGet packages' -Command {
    Test-VulnerableNuGetPackages
}

Invoke-ExternalCommand -Label 'Install frontend dependencies' -Command {
    & npm --prefix $FrontendDirectory ci
}

Invoke-ExternalCommand -Label 'Lint frontend' -Command {
    & npm --prefix $FrontendDirectory run lint
}

Invoke-ExternalCommand -Label 'Test frontend' -Command {
    & npm --prefix $FrontendDirectory run test
}

Invoke-ExternalCommand -Label 'Build frontend' -Command {
    & npm --prefix $FrontendDirectory run build
}

Invoke-Step -Label 'Audit frontend dependencies (report only)' -Command {
    Show-NpmAudit
}

Write-Host ""
Write-Host 'Validation succeeded.'
