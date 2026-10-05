#!/usr/bin/env bash
#
# Scheduled database backup WebJob (issue #333).
#
# App Service runs this script on the schedule in the settings.job next to it. It contains no
# backup logic of its own: it invokes the published application's supported
# `backup-database --upload` command (issues #331 and #332), which takes a verified SQLite
# snapshot, uploads it to the private backup container, removes the local copy, and exits non-zero
# on any failure. This script's whole job is to find that application, run the command where the
# API itself runs, log the run, and propagate the command's exit code so a failed backup is a
# failed WebJob run.
#
# It deliberately names no database path and no connection string. The database backed up is
# whatever `ConnectionStrings:DefaultConnection` resolves to for the API - an absolute path if one
# is configured, or the relative `Data Source=inventory.db` default, which is why the command is
# invoked from the application's own directory below rather than from the directory App Service
# copies this script into.
#
# It also configures nothing about the upload: the container, the storage account and the identity
# all come from the application settings the API already reads (`BackupStorage__BlobServiceUri`,
# `BackupStorage__ContainerName`, and the App Service's managed identity through
# DefaultAzureCredential). There is no secret here, and none may ever be added.
#
# See docs/architecture.md § Scheduling the backup with an App Service WebJob (issue #333) for the
# deployment prerequisites a human must confirm.

set -Eeuo pipefail

job_name="database-backup"
entry_assembly="InventoryApi.dll"
command_arguments=(backup-database --upload)

# Logged with a UTC timestamp because the schedule, the uploaded object names and the alert
# queries are all UTC. Failures use the same "FAILED (<reason>)" shape the backup command's own
# output uses, so the failed-run alert of issue #334 catches a job-level failure too.
log() {
    printf '%s %s: %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$job_name" "$1"
}

# The published application's directory. App Service copies a triggered WebJob's files to a
# temporary directory before running them, so the application is found rather than assumed to be
# the working directory: WEBROOT_PATH when the platform provides it, then the standard Linux App
# Service site root under the persistent HOME mount, and finally this script's own location four
# levels up (App_Data/jobs/triggered/<job name>/) for a host that runs it in place.
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
in_place_root="$(cd -- "${script_directory}/../../../.." 2>/dev/null && pwd || true)"

application_directory=""
for candidate in "${WEBROOT_PATH:-}" "${HOME:-}/site/wwwroot" "${in_place_root}"; do
    if [[ -n "${candidate}" ]] && [[ -f "${candidate}/${entry_assembly}" ]]; then
        application_directory="${candidate}"
        break
    fi
done

if [[ -z "${application_directory}" ]]; then
    log "FAILED (ApplicationNotFound): ${entry_assembly} was not found in WEBROOT_PATH, \$HOME/site/wwwroot or ${in_place_root}. No backup was taken."
    exit 1
fi

# The .NET host. It is on PATH on a Linux App Service running a .NET application; DOTNET_ROOT is
# the fallback for a host that provides the runtime without putting it on PATH.
dotnet_host="$(command -v dotnet || true)"
if [[ -z "${dotnet_host}" ]] && [[ -n "${DOTNET_ROOT:-}" ]] && [[ -x "${DOTNET_ROOT}/dotnet" ]]; then
    dotnet_host="${DOTNET_ROOT}/dotnet"
fi

if [[ -z "${dotnet_host}" ]]; then
    log "FAILED (DotnetHostNotFound): no 'dotnet' host is available on PATH or under DOTNET_ROOT. No backup was taken."
    exit 1
fi

cd -- "${application_directory}"

log "START: ${entry_assembly} ${command_arguments[*]} in ${application_directory}. The database, container and identity come from the application's own configuration."

started_at="$(date -u '+%s')"
exit_code=0
"${dotnet_host}" "${entry_assembly}" "${command_arguments[@]}" || exit_code=$?
duration=$(( $(date -u '+%s') - started_at ))

# The command's own output above carries the artifact metadata an operator needs - the uploaded
# daily/monthly object names, the snapshot's SHA-256, its byte count and the integrity result -
# and never a connection string, a credential or any row of business data. It is inherited by this
# job's log rather than captured and reparsed here, so there is exactly one place that decides what
# a backup run reports.
if [[ "${exit_code}" -eq 0 ]]; then
    log "COMPLETED: the snapshot was verified and uploaded; duration ${duration}s. Object names and checksum are in the command output above."
else
    log "FAILED (NonZeroExit): ${entry_assembly} ${command_arguments[*]} exited ${exit_code}; duration ${duration}s. The reason is in the command output above."
fi

exit "${exit_code}"
