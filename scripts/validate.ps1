<#
.SYNOPSIS
    Complete local validation for InventoryApp (Windows PowerShell / PowerShell 7).

.DESCRIPTION
    Pipeline:
      1. Restore backend
      2. Verify C# formatting          (dotnet format --verify-no-changes)
      3. Build backend                 (analyzers + warnings-as-errors, see Directory.Build.props)
      4. Test backend with coverage    (includes the architecture tests)
      5. Check vulnerable NuGet packages
      6. Install frontend dependencies (npm ci)
      7. Angular ESLint                (npm run lint)
      8. Angular production build      (npm run build)
      9. npm audit report

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
$MigrationsRelativePath = 'backend/InventoryApi/Migrations'
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

if (-not (Test-Path -LiteralPath $BackendSolution -PathType Leaf)) {
    throw "Backend solution not found: $BackendSolution"
}

if (-not (Test-Path -LiteralPath $FrontendLockFile -PathType Leaf)) {
    throw "Frontend lock file not found: $FrontendLockFile"
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

Invoke-ExternalCommand -Label "Test backend with coverage ($Configuration)" -Command {
    & dotnet test $BackendSolution --configuration $Configuration --no-build --no-restore `
        --collect:"XPlat Code Coverage"
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

Invoke-ExternalCommand -Label 'Build frontend' -Command {
    & npm --prefix $FrontendDirectory run build
}

Invoke-Step -Label 'Audit frontend dependencies (report only)' -Command {
    Show-NpmAudit
}

Write-Host ""
Write-Host 'Validation succeeded.'
