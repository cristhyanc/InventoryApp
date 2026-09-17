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

Invoke-ExternalCommand -Label "Build backend ($Configuration)" -Command {
    & dotnet build $BackendSolution --configuration $Configuration --no-restore
}

Invoke-ExternalCommand -Label "Test backend ($Configuration)" -Command {
    & dotnet test $BackendSolution --configuration $Configuration --no-build --no-restore
}

Invoke-ExternalCommand -Label 'Install frontend dependencies' -Command {
    & npm --prefix $FrontendDirectory ci
}

Invoke-ExternalCommand -Label 'Build frontend' -Command {
    & npm --prefix $FrontendDirectory run build
}

Write-Host ""
Write-Host 'Validation succeeded.'

