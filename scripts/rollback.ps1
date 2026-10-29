<#
.SYNOPSIS
    Rolls back Receptbanken on Ubuntu Server 24.04 LTS to the previous Docker image
    (and optionally, only when explicitly requested, restores the data volume from backup).
    Requires no Docker installation on Windows.

.DESCRIPTION
    Safety principles:
    - Standard execution (without -RestoreData) restores ONLY the previous Docker image.
      The SQLite database, images, and DataProtection keys in 'receptbanken-data' are untouched.
    - If -RestoreData is specified, a clear warning and confirmation are required, after which
      an extra safety backup (pre-rollback-<timestamp>.tar.gz) of the current data state is
      created and verified with SHA-256 before the volume is restored.
#>
[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [string]$ServerHost,
    [string]$ServerUser,
    [int]$ServerPort = 0,
    [string]$DeployPath,
    [int]$AppPort = 0,
    [string]$VolumeName,
    [string]$TargetTag,
    [string]$BackupTimestamp,
    [switch]$RestoreData,
    [switch]$Force,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Fail-Rollback {
    param([string]$Message)
    Write-Host "`n[ERROR] Rollback aborted: $Message" -ForegroundColor Red
    exit 1
}

function Convert-ScriptToBase64Command {
    param([string]$Script)
    $normalized = $Script.Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $b64 = [Convert]::ToBase64String($bytes)
    return "printf '%s' '$b64' | base64 -d | bash 2>&1"
}

# 1. Read configuration from .deploy.env if present
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $PSScriptRoot "..\.deploy.env"
}
$config = @{}
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) { continue }
        $parts = $trimmed -split "=", 2
        if ($parts.Count -eq 2) {
            $config[$parts[0].Trim()] = $parts[1].Trim()
        }
    }
}

if (-not $ServerHost) { $ServerHost = $config["SERVER_HOST"] }
if (-not $ServerUser) { $ServerUser = $config["SERVER_USER"] }
if ($ServerPort -le 0) {
    $ServerPort = if ($config["SERVER_PORT"]) { [int]$config["SERVER_PORT"] } else { 22 }
}
if (-not $DeployPath) {
    $DeployPath = if ($config["DEPLOY_PATH"]) { $config["DEPLOY_PATH"] } else { "/opt/receptbanken" }
}
if ($AppPort -le 0) {
    $AppPort = if ($config["APP_PORT"]) { [int]$config["APP_PORT"] } else { 8080 }
}
if (-not $VolumeName) {
    $VolumeName = if ($config["VOLUME_NAME"]) { $config["VOLUME_NAME"] } else { "receptbanken-data" }
}

# Strict parameter validation
if ([string]::IsNullOrWhiteSpace($ServerHost) -or [string]::IsNullOrWhiteSpace($ServerUser)) {
    Fail-Rollback "ServerHost and ServerUser must be specified (in .deploy.env or via -ServerHost / -ServerUser)."
}
if ($ServerHost -notmatch '^[a-zA-Z0-9._-]+$') {
    Fail-Rollback "Invalid ServerHost '$ServerHost'. Only letters, digits, dots, hyphens, and underscores are allowed."
}
if ($ServerUser -notmatch '^[a-zA-Z0-9._-]+$') {
    Fail-Rollback "Invalid ServerUser '$ServerUser'. Only letters, digits, dots, hyphens, and underscores are allowed."
}
if ($ServerPort -lt 1 -or $ServerPort -gt 65535) {
    Fail-Rollback "Invalid ServerPort '$ServerPort' (must be 1-65535)."
}
if ($AppPort -lt 1 -or $AppPort -gt 65535) {
    Fail-Rollback "Invalid AppPort '$AppPort' (must be 1-65535)."
}
if ($VolumeName -notmatch '^[a-zA-Z0-9_.-]+$') {
    Fail-Rollback "Invalid VolumeName '$VolumeName'."
}
$DeployPath = $DeployPath.TrimEnd('/')
if ($DeployPath -notmatch '^/[a-zA-Z0-9/_.-]+$' -or $DeployPath.Contains("..") -or $DeployPath -eq "/app" -or $DeployPath.StartsWith("/app/data")) {
    Fail-Rollback "Invalid or unsafe DeployPath '$DeployPath'."
}
if ($TargetTag -and ($TargetTag -notmatch '^[a-zA-Z0-9_.-]+$')) {
    Fail-Rollback "Invalid TargetTag '$TargetTag'."
}
if ($BackupTimestamp -and ($BackupTimestamp -notmatch '^[a-zA-Z0-9_.-]+$')) {
    Fail-Rollback "Invalid BackupTimestamp '$BackupTimestamp'."
}

$SshTarget = "$ServerUser@$ServerHost"
$SshArgs = @("-p", "$ServerPort", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10")

function Invoke-RemoteBash {
    param([string]$Script, [switch]$AllowFailure)
    # Encode script as Base64 so Windows PowerShell 5.1 native argument quoting never strips quotes or splits pipes
    $remoteCmd = Convert-ScriptToBase64Command -Script $Script

    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $rawOutput = & ssh @SshArgs $SshTarget $remoteCmd 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldEap
    }

    $lines = @()
    if ($null -ne $rawOutput) {
        foreach ($item in @($rawOutput)) {
            if ($null -ne $item) {
                $lines += $item.ToString()
            }
        }
    }
    $joined = ($lines -join "`n").Trim()

    if (-not $AllowFailure -and $code -ne 0) {
        Fail-Rollback "Remote command on $SshTarget failed (exit code $code): $joined"
    }
    return @{
        ExitCode = $code
        Output   = $joined
    }
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Receptbanken - Rollback / Recovery" -ForegroundColor Cyan
Write-Host " Target Host : $SshTarget (port $ServerPort)"
Write-Host " Deploy Path : $DeployPath"
Write-Host " Data Volume : $VolumeName"
Write-Host " Scope       : $(if ($RestoreData) { 'IMAGE + DATA VOLUME (-RestoreData)' } else { 'DOCKER IMAGE ONLY (data volume untouched)' })"
if ($DryRun) {
    Write-Host " Run Mode    : DRY-RUN (no changes on server)" -ForegroundColor Yellow
}
Write-Host "============================================================" -ForegroundColor Cyan

# 2. If -RestoreData is requested, require explicit warning and confirmation
if ($RestoreData -and -not $DryRun -and -not $Force) {
    Write-Host ""
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    Write-Host " WARNING: You have specified -RestoreData!" -ForegroundColor Red
    Write-Host " This will replace the contents of volume '$VolumeName'" -ForegroundColor Yellow
    Write-Host " (SQLite database, images, and keys) with an earlier backup." -ForegroundColor Yellow
    Write-Host " An extra safety backup of the current state will be taken first." -ForegroundColor Yellow
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    $confirmation = Read-Host "Type 'RESTORE' (in uppercase) to confirm"
    if ($confirmation -cne "RESTORE") {
        Fail-Rollback "Rollback aborted because confirmation did not match 'RESTORE'."
    }
}

if ($DryRun) {
    Write-Step "[DRY-RUN] Summary of planned rollback steps:"
    $plannedTag = if ($TargetTag) { $TargetTag } else { "<from $DeployPath/.previous_version>" }
    Write-Host " 1. Connect via SSH to $SshTarget and identify previous image tag ($plannedTag)"
    Write-Host " 2. Verify that Docker image 'receptbanken:$plannedTag' and volume '$VolumeName' exist on server"
    if ($RestoreData) {
        $plannedBackup = if ($BackupTimestamp) { "volume-$BackupTimestamp.tar.gz" } else { "<latest verified volume-*.tar.gz>" }
        Write-Host " 3. [RestoreData] Check free disk space and stop container cleanly"
        Write-Host " 4. [RestoreData] Create and verify extra safety backup of current volume: pre-rollback-<timestamp>.tar.gz"
        Write-Host " 5. [RestoreData] Verify target backup ($plannedBackup) including SHA-256 and restore into volume '$VolumeName'"
    }
    else {
        Write-Host " 3. Leave data volume '$VolumeName' completely untouched (no database restore)"
    }
    Write-Host " 6. Update $DeployPath/.env with IMAGE_TAG=$plannedTag and run 'docker compose -p receptbanken up -d --no-build'"
    Write-Host " 7. Verify Docker healthcheck and /health"
    Write-Success "Rollback dry-run completed without errors. No local Docker required."
    exit 0
}

# 3. Inspect server state, target image tag, and backups
Write-Step "Checking available versions and volume status on $SshTarget..."
$inspectScript = @"
set -e
if ! docker volume inspect "$VolumeName" >/dev/null 2>&1; then
    echo "ERR_NO_VOLUME"
    exit 10
fi

PREV_VER=""
if [ -f "$DeployPath/.previous_version" ]; then
    PREV_VER=`$(cat "$DeployPath/.previous_version" | tr -d ' \r\n')
fi

CURR_VER=""
if [ -f "$DeployPath/.current_version" ]; then
    CURR_VER=`$(cat "$DeployPath/.current_version" | tr -d ' \r\n')
fi

LATEST_BACKUP=""
if [ -d "$DeployPath/backups" ]; then
    LATEST_BACKUP=`$(ls -1t "$DeployPath"/backups/volume-*.tar.gz 2>/dev/null | head -n 1 || true)
fi

echo "INFO|PREV_VER=`$PREV_VER|CURR_VER=`$CURR_VER|LATEST_BACKUP=`$LATEST_BACKUP"
"@

$infoResult = Invoke-RemoteBash -Script $inspectScript
$infoLine = ($infoResult.Output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -like "INFO|*" } | Select-Object -Last 1)
if (-not $infoLine) {
    Fail-Rollback "Could not read version status from server: $($infoResult.Output)"
}

$infoMap = @{}
foreach ($kv in ($infoLine.Substring(5) -split '\|')) {
    $pair = $kv -split '=', 2
    if ($pair.Count -eq 2) { $infoMap[$pair[0].Trim()] = $pair[1].Trim() }
}

if (-not $TargetTag) {
    $TargetTag = $infoMap["PREV_VER"]
}
if ([string]::IsNullOrWhiteSpace($TargetTag)) {
    Fail-Rollback "No previous version found in $DeployPath/.previous_version. Specify desired version tag with -TargetTag <tag>."
}
if ($TargetTag -notmatch '^[a-zA-Z0-9_.-]+$') {
    Fail-Rollback "Invalid TargetTag '$TargetTag'."
}

$TargetImage = "receptbanken:$TargetTag"
Write-Step "Verifying that Docker image '$TargetImage' is available on server..."
$verifyImgScript = @"
set -e
if ! docker image inspect "$TargetImage" >/dev/null 2>&1; then
    echo "ERR_IMAGE_NOT_FOUND"
    exit 15
fi
echo "IMAGE_OK"
"@
$imgResult = Invoke-RemoteBash -Script $verifyImgScript -AllowFailure
if ($imgResult.ExitCode -ne 0 -or $imgResult.Output -notmatch "IMAGE_OK") {
    Fail-Rollback "Docker image '$TargetImage' was not found on server. Check available images with 'docker images receptbanken' on the server."
}
Write-Success "Docker image '$TargetImage' is available on server."

# 4. Optional Data Volume Restore (ONLY if -RestoreData was explicitly passed)
if ($RestoreData) {
    $backupFile = ""
    if ($BackupTimestamp) {
        $backupFile = "$DeployPath/backups/volume-$BackupTimestamp.tar.gz"
    }
    else {
        $backupFile = $infoMap["LATEST_BACKUP"]
    }

    if ([string]::IsNullOrWhiteSpace($backupFile)) {
        Fail-Rollback "No volume backup (volume-*.tar.gz) found in $DeployPath/backups."
    }

    $safetyTimestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    Write-Step "Creating safety backup of current data state (pre-rollback-$safetyTimestamp.tar.gz) before volume restore..."

    $restoreDataScript = @"
set -e
BACKUP_DIR="$DeployPath/backups"
TARGET_ARCHIVE="$backupFile"

# 1. Verify target backup archive exists, is non-empty, passes sha256 if present, and contains receptbanken.db
if [ ! -s "`$TARGET_ARCHIVE" ]; then
    echo "ERR_TARGET_BACKUP_MISSING:`$TARGET_ARCHIVE"
    exit 20
fi

if [ -f "`$TARGET_ARCHIVE.sha256" ]; then
    (cd "`$BACKUP_DIR" && sha256sum -c "`$(basename "`$TARGET_ARCHIVE.sha256")" >/dev/null 2>&1) || {
        echo "ERR_CHECKSUM_MISMATCH"
        exit 21
    }
fi

tar -tzf "`$TARGET_ARCHIVE" | grep -E '(^\./|^)receptbanken\.db$' >/dev/null 2>&1 || {
    echo "ERR_TARGET_BACKUP_NO_DB"
    exit 22
}

# 2. Check free disk space (>= 1 GB for safety backup)
FREE_KB=`$(df -Pk "$DeployPath" 2>/dev/null | awk 'NR==2 {print `$4}')
if [ -z "`$FREE_KB" ] || [ "`$FREE_KB" -lt 1048576 ]; then
    echo "ERR_LOW_DISK_SPACE:`$FREE_KB"
    exit 23
}

# 3. Stop running container cleanly so SQLite WAL flushes
if docker container inspect receptbanken >/dev/null 2>&1; then
    docker stop -t 15 receptbanken >/dev/null 2>&1 || true
fi

# 4. Take mandatory pre-rollback backup of current volume state and verify SHA-256
SAFETY_ARCHIVE="`$BACKUP_DIR/pre-rollback-$safetyTimestamp.tar.gz"
docker run --rm --entrypoint /bin/sh -u 0 \
    -v "${VolumeName}:/source:ro" \
    -v "`$BACKUP_DIR:/backup" \
    "$TargetImage" \
    -c "cd /source && tar -czf /backup/pre-rollback-$safetyTimestamp.tar.gz ."

if [ ! -s "`$SAFETY_ARCHIVE" ] || ! tar -tzf "`$SAFETY_ARCHIVE" >/dev/null 2>&1; then
    echo "ERR_SAFETY_BACKUP_FAILED"
    exit 24
fi
(cd "`$BACKUP_DIR" && sha256sum "pre-rollback-$safetyTimestamp.tar.gz" > "pre-rollback-$safetyTimestamp.tar.gz.sha256")
(cd "`$BACKUP_DIR" && sha256sum -c "pre-rollback-$safetyTimestamp.tar.gz.sha256" >/dev/null 2>&1) || {
    echo "ERR_SAFETY_CHECKSUM_FAILED"
    exit 25
}

# 5. Restore target backup into volume and ensure app:app (1654:1654) ownership
docker run --rm --entrypoint /bin/sh -u 0 \
    -v "${VolumeName}:/target" \
    -v "`$BACKUP_DIR:/backup:ro" \
    "$TargetImage" \
    -c "rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true; cd /target && tar -xzf /backup/`$(basename "`$TARGET_ARCHIVE") && chown -R app:app /target"

echo "DATA_RESTORE_OK"
"@

    $restoreResult = Invoke-RemoteBash -Script $restoreDataScript -AllowFailure
    if ($restoreResult.ExitCode -ne 0 -or $restoreResult.Output -notmatch "DATA_RESTORE_OK") {
        Fail-Rollback "Data volume restore failed ($($restoreResult.Output))."
    }
    Write-Success "Extra safety backup (pre-rollback-$safetyTimestamp.tar.gz) created and data volume restored from $backupFile."
}
else {
    Write-Success "Data volume '$VolumeName' left untouched (restoring Docker image only)."
}

# 5. Switch container to TargetTag and start with docker compose
Write-Step "Restarting container with Docker image '$TargetImage'..."
$switchScript = @"
set -e
cd "$DeployPath"

docker tag "$TargetImage" "receptbanken:latest"

cat > "$DeployPath/.env" <<EOF
IMAGE_TAG=$TargetTag
APP_PORT=$AppPort
EOF

echo "$TargetTag" > "$DeployPath/.current_version"

docker compose -p receptbanken -f "$DeployPath/docker-compose.yml" --env-file "$DeployPath/.env" up -d --no-build
"@

$null = Invoke-RemoteBash -Script $switchScript
Write-Success "Container started with '$TargetImage'."

# 6. Verify health check (/health)
Write-Step "Verifying application health check (/health)..."
$healthScript = @"
set -e
MAX_ATTEMPTS=25
ATTEMPT=1
while [ `$ATTEMPT -le `$MAX_ATTEMPTS ]; do
    RUNNING=`$(docker container inspect -f '{{.State.Running}}' receptbanken 2>/dev/null || echo "false")
    STATUS=`$(docker container inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' receptbanken 2>/dev/null || echo "missing")
    if [ "`$RUNNING" = "true" ] && [ "`$STATUS" = "healthy" ]; then
        echo "HEALTHY_OK"
        exit 0
    fi
    if [ "`$RUNNING" = "true" ] && docker exec receptbanken dotnet Receptbanken.Web.dll --healthcheck >/dev/null 2>&1; then
        echo "HEALTHY_OK"
        exit 0
    fi
    sleep 2
    ATTEMPT=`$((ATTEMPT + 1))
done
docker logs --tail 50 receptbanken 2>&1 || true
exit 31
"@

$healthResult = Invoke-RemoteBash -Script $healthScript -AllowFailure
if ($healthResult.ExitCode -ne 0 -or $healthResult.Output -notmatch "HEALTHY_OK") {
    Fail-Rollback "Container started with '$TargetImage', but failed the health check:`n$($healthResult.Output)"
}
Write-Success "Health check (/health) passed!"

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " Rollback completed successfully!" -ForegroundColor Green
Write-Host " Active Version : $TargetImage"
Write-Host " URL            : http://${ServerHost}:${AppPort}"
Write-Host "============================================================" -ForegroundColor Green
exit 0
