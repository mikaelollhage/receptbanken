<#
.SYNOPSIS
    Deploys Receptbanken from Windows to Ubuntu Server 24.04 LTS.
    Windows runs .NET build, tests, and source packaging; Ubuntu builds the Docker image
    and updates the container via Docker Compose. No Docker installation is required on Windows.

.DESCRIPTION
    Workflow:
    1. Reads and validates configuration from .deploy.env or parameters.
    2. Runs 'dotnet build -c Release' and 'dotnet test -c Release' locally on Windows.
    3. Creates and verifies a clean distribution archive (.tar.gz) with only required build files.
    4. Checks server environment, disk space, container name, and data volume ('receptbanken-data') via SSH.
       Strictly separates first installation (-FirstInstall) from updates.
    5. Transfers the distribution archive to /opt/receptbanken/builds/<version>/ via SCP.
    6. Builds the Docker image on Ubuntu (while any existing container continues running).
    7. On update: stops the old container cleanly, creates a raw volume backup,
       generates and verifies SHA-256, and verifies that receptbanken.db is inside the archive.
    8. Starts the new version with 'docker compose up -d --no-build', verifies /health, and cleans up build files.
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
    [int]$BackupRetentionCount = 0,
    [int]$ImageRetentionCount = 0,
    [string]$VapidSubject = "",
    [string]$ArchiveOutputPath = "",
    [switch]$FirstInstall,
    [switch]$SkipTests,
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

function Remove-TempArtifacts {
    param([string]$StagePath = "", [string]$ArchiveFile = "", [bool]$KeepArchive = $false)
    if ($StagePath -and (Test-Path -LiteralPath $StagePath)) {
        Remove-Item -LiteralPath $StagePath -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $KeepArchive -and $ArchiveFile -and (Test-Path -LiteralPath $ArchiveFile)) {
        Remove-Item -LiteralPath $ArchiveFile -Force -ErrorAction SilentlyContinue
    }
}

function Fail-Deploy {
    param(
        [string]$Message,
        [string]$StagePath = "",
        [string]$ArchiveFile = "",
        [bool]$KeepArchive = $false
    )
    Write-Host "`n[ERROR] Deployment aborted: $Message" -ForegroundColor Red
    Remove-TempArtifacts -StagePath $StagePath -ArchiveFile $ArchiveFile -KeepArchive $KeepArchive
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
if ($BackupRetentionCount -le 0) {
    $BackupRetentionCount = if ($config["BACKUP_RETENTION_COUNT"]) { [int]$config["BACKUP_RETENTION_COUNT"] } else { 10 }
}
if ($ImageRetentionCount -le 0) {
    $ImageRetentionCount = if ($config["IMAGE_RETENTION_COUNT"]) { [int]$config["IMAGE_RETENTION_COUNT"] } else { 5 }
}
if ([string]::IsNullOrWhiteSpace($VapidSubject)) {
    $VapidSubject = if ($config["VAPID_SUBJECT"]) { $config["VAPID_SUBJECT"].Trim() } else { "" }
}
else {
    $VapidSubject = $VapidSubject.Trim()
}

# Strict parameter validation to prevent unsafe remote shell commands or invalid paths
if ([string]::IsNullOrWhiteSpace($ServerHost) -or [string]::IsNullOrWhiteSpace($ServerUser)) {
    Fail-Deploy "ServerHost and ServerUser must be specified (either in .deploy.env or via -ServerHost / -ServerUser)."
}
if ($ServerHost -notmatch '^[a-zA-Z0-9._-]+$') {
    Fail-Deploy "Invalid ServerHost '$ServerHost'. Only letters, digits, dots, hyphens, and underscores are allowed."
}
if ($ServerUser -notmatch '^[a-zA-Z0-9._-]+$') {
    Fail-Deploy "Invalid ServerUser '$ServerUser'. Only letters, digits, dots, hyphens, and underscores are allowed."
}
if ($ServerPort -lt 1 -or $ServerPort -gt 65535) {
    Fail-Deploy "Invalid ServerPort '$ServerPort' (must be 1-65535)."
}
if ($AppPort -lt 1 -or $AppPort -gt 65535) {
    Fail-Deploy "Invalid AppPort '$AppPort' (must be 1-65535)."
}
if ($VolumeName -notmatch '^[a-zA-Z0-9_.-]+$') {
    Fail-Deploy "Invalid VolumeName '$VolumeName'."
}
$DeployPath = $DeployPath.TrimEnd('/')
if ($DeployPath -notmatch '^/[a-zA-Z0-9/_.-]+$' -or $DeployPath.Contains("..") -or $DeployPath -eq "/app" -or $DeployPath.StartsWith("/app/data")) {
    Fail-Deploy "Invalid or unsafe DeployPath '$DeployPath'. Must be an absolute Linux path (e.g. /opt/receptbanken) and must not point to /app/data."
}
if (-not [string]::IsNullOrWhiteSpace($VapidSubject)) {
    if ($VapidSubject -notmatch '^(mailto:|https://)[a-zA-Z0-9.:/@_+-]+$') {
        Fail-Deploy "Invalid VAPID_SUBJECT '$VapidSubject'. Must start with 'mailto:' or 'https://' and contain only valid URI/email characters."
    }
}
else {
    Write-Warn "VAPID_SUBJECT is not configured in .deploy.env; falling back to default 'mailto:noreply@receptbanken.local'."
}

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$ComposeFileLocal = Join-Path $RepoRoot "docker-compose.yml"
$DockerfileLocal = Join-Path $RepoRoot "Dockerfile"
if (-not (Test-Path -LiteralPath $ComposeFileLocal)) {
    Fail-Deploy "Could not find docker-compose.yml in '$RepoRoot'."
}
if (-not (Test-Path -LiteralPath $DockerfileLocal)) {
    Fail-Deploy "Could not find Dockerfile in '$RepoRoot'."
}

$VersionTag = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
if ($VersionTag -notmatch '^[0-9]{8}-[0-9]{6}$') {
    Fail-Deploy "Generated version tag '$VersionTag' has an invalid format."
}

$ImageRef = "receptbanken:$VersionTag"
$StageDir = Join-Path ([System.IO.Path]::GetTempPath()) "receptbanken-stage-$VersionTag"
$KeepCustomArchive = (-not [string]::IsNullOrWhiteSpace($ArchiveOutputPath))
$ArchivePath = if ($KeepCustomArchive) {
    [System.IO.Path]::GetFullPath($ArchiveOutputPath)
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) "receptbanken-src-$VersionTag.tar.gz"
}

# Ensure ArchivePath is never inside RepoRoot/src or StageDir so it cannot include itself
$normalizedStage = [System.IO.Path]::GetFullPath($StageDir).TrimEnd('\') + "\"
$normalizedArchive = [System.IO.Path]::GetFullPath($ArchivePath)
if ($normalizedArchive.StartsWith($normalizedStage, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail-Deploy "Archive path '$ArchivePath' must not reside inside the temporary staging directory."
}

$RemoteBuildDir = "$DeployPath/builds/$VersionTag"
$RemoteArchivePath = "$RemoteBuildDir/receptbanken-src-$VersionTag.tar.gz"
$SshTarget = "$ServerUser@$ServerHost"
$SshArgs = @("-p", "$ServerPort", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10")
$ScpArgs = @("-P", "$ServerPort", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10")

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
        Fail-Deploy "Remote command on $SshTarget failed (exit code $code): $joined" -StagePath $StageDir -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    return @{
        ExitCode = $code
        Output   = $joined
    }
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Receptbanken - Deployment ($VersionTag)" -ForegroundColor Cyan
Write-Host " Build Mode  : Windows (.NET build/test + archive) -> Ubuntu (docker build)"
Write-Host " Target Host : $SshTarget (port $ServerPort)"
Write-Host " Deploy Path : $DeployPath"
Write-Host " Build Path  : $RemoteBuildDir"
Write-Host " Data Volume : $VolumeName"
Write-Host " Install Mode: $(if ($FirstInstall) { 'FIRST INSTALLATION (-FirstInstall)' } else { 'UPDATE EXISTING INSTALLATION' })"
if ($DryRun) {
    Write-Host " Run Mode    : DRY-RUN (no remote connection or changes on Ubuntu)" -ForegroundColor Yellow
}
Write-Host "============================================================" -ForegroundColor Cyan

# Check local disk space in TEMP before building/packaging
Write-Step "Checking local disk space and tools on Windows..."
$tempRoot = [System.IO.Path]::GetPathRoot($StageDir)
$driveLetter = $tempRoot.TrimEnd('\').TrimEnd(':')
$psDrive = Get-PSDrive -Name $driveLetter -ErrorAction SilentlyContinue
$minLocalFreeBytes = 200MB
if ($psDrive -and ($null -ne $psDrive.Free) -and ($psDrive.Free -gt 0) -and ($psDrive.Free -lt $minLocalFreeBytes)) {
    $freeMb = [math]::Round($psDrive.Free / 1MB, 0)
    Fail-Deploy "Insufficient local disk space on drive ${driveLetter}: ($freeMb MB free, at least 200 MB required)."
}

$tarCmd = Get-Command "tar.exe" -ErrorAction SilentlyContinue
if (-not $tarCmd) {
    Fail-Deploy "Could not find 'tar.exe' on Windows. Ensure the built-in Windows tar utility is available."
}
Write-Success "Local tools (dotnet, tar, ssh, scp) and disk space verified. No local Docker required."

# Step 1 - Local verification (dotnet build & dotnet test)
if (-not $SkipTests) {
    Write-Step "Step 1: Running local Release build and tests on Windows..."
    if ($DryRun) {
        Write-Host "[DRY-RUN] Would run: dotnet build `"$RepoRoot\Receptbanken.sln`" -c Release --nologo"
        Write-Host "[DRY-RUN] Would run: dotnet test `"$RepoRoot\tests\Receptbanken.Tests\Receptbanken.Tests.csproj`" -c Release --no-build --nologo"
    }
    else {
        Push-Location -LiteralPath $RepoRoot
        $oldEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & dotnet build "Receptbanken.sln" -c Release --nologo
            if ($LASTEXITCODE -ne 0) {
                $ErrorActionPreference = $oldEap
                Fail-Deploy "'dotnet build -c Release' failed. Deployment aborted."
            }

            & dotnet test "tests/Receptbanken.Tests/Receptbanken.Tests.csproj" -c Release --no-build --nologo
            if ($LASTEXITCODE -ne 0) {
                $ErrorActionPreference = $oldEap
                Fail-Deploy "'dotnet test -c Release' failed. Deployment aborted."
            }
        }
        finally {
            $ErrorActionPreference = $oldEap
            Pop-Location
        }
        Write-Success "Local Release build and all automated tests passed."
    }
}
else {
    Write-Warn "Local build and tests skipped (-SkipTests specified)."
}

# Step 2 - Create and verify a clean distribution archive (.tar.gz)
Write-Step "Step 2: Creating and verifying distribution archive for Docker build..."
try {
    if (Test-Path -LiteralPath $StageDir) {
        Remove-Item -LiteralPath $StageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

    # Copy root build files required by Dockerfile
    $rootFiles = @(
        "Dockerfile",
        ".dockerignore",
        "Directory.Build.props",
        "Receptbanken.sln",
        "NuGet.Config",
        "nuget.config",
        "global.json"
    )
    foreach ($rf in $rootFiles) {
        $srcFile = Join-Path $RepoRoot $rf
        if (Test-Path -LiteralPath $srcFile -PathType Leaf) {
            Copy-Item -LiteralPath $srcFile -Destination (Join-Path $StageDir $rf) -Force
        }
    }

    # Copy source files under src/ while strictly excluding build outputs, user data, DBs, secrets, and archives
    $srcRoot = Join-Path $RepoRoot "src"
    if (-not (Test-Path -LiteralPath $srcRoot -PathType Container)) {
        Fail-Deploy "Source directory '$srcRoot' was not found." -StagePath $StageDir -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }

    # Only exclude VCS, IDE, and compiler output directories by segment name (never generic names like 'data' or 'backup' that match source namespaces)
    $excludedDirNames = @(
        ".git", ".vs", ".vscode", "bin", "obj", "TestResults"
    )
    $excludedExtensions = @(
        ".db", ".db-shm", ".db-wal", ".sqlite", ".sqlite3",
        ".env", ".pem", ".key", ".pfx", ".p12", ".cer", ".crt",
        ".zip", ".tar", ".gz", ".tgz", ".7z", ".bak", ".user", ".suo"
    )

    $allSrcFiles = Get-ChildItem -LiteralPath $srcRoot -Recurse -File -Force
    foreach ($file in $allSrcFiles) {
        if ($file.FullName.Equals($normalizedArchive, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $relativeFromRepo = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $NormalizedRel = $relativeFromRepo.Replace('\', '/')
        $segments = $NormalizedRel -split '/'

        $skipDir = $false
        for ($i = 0; $i -lt ($segments.Count - 1); $i++) {
            $seg = $segments[$i]
            foreach ($exDir in $excludedDirNames) {
                if ($seg.Equals($exDir, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $skipDir = $true
                    break
                }
            }
            if ($skipDir) { break }
        }
        if ($skipDir) { continue }

        # Exclude runtime user-data folders if present locally under Receptbanken.Web/data or images/recipes
        if ($NormalizedRel -match '^src/Receptbanken\.Web/data/' -or $NormalizedRel -match '(^|/)images/recipes/') {
            continue
        }

        $fileName = $file.Name
        if ($fileName.StartsWith(".env", [System.StringComparison]::OrdinalIgnoreCase) -or
            $fileName.StartsWith(".deploy.env", [System.StringComparison]::OrdinalIgnoreCase) -or
            $fileName.StartsWith("id_rsa", [System.StringComparison]::OrdinalIgnoreCase) -or
            $fileName.StartsWith("id_ed25519", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $ext = $file.Extension
        $skipExt = $false
        foreach ($exExt in $excludedExtensions) {
            if ($ext.Equals($exExt, [System.StringComparison]::OrdinalIgnoreCase)) {
                $skipExt = $true
                break
            }
        }
        if ($skipExt) { continue }

        $destFile = Join-Path $StageDir $relativeFromRepo
        $destParent = Split-Path -Path $destFile -Parent
        if (-not (Test-Path -LiteralPath $destParent)) {
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destFile -Force
    }

    $archiveParent = Split-Path -Path $ArchivePath -Parent
    if ($archiveParent -and -not (Test-Path -LiteralPath $archiveParent)) {
        New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & tar.exe -czf $ArchivePath -C $StageDir .
        $tarCreateCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldEap
    }

    if ($tarCreateCode -ne 0 -or -not (Test-Path -LiteralPath $ArchivePath)) {
        Fail-Deploy "Failed to create distribution archive with tar.exe." -StagePath $StageDir -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
}
finally {
    if (Test-Path -LiteralPath $StageDir) {
        Remove-Item -LiteralPath $StageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Verify archive contents
$archiveSize = (Get-Item -LiteralPath $ArchivePath).Length
if ($archiveSize -lt 1024) {
    Fail-Deploy "Distribution archive '$ArchivePath' is unexpectedly small ($archiveSize bytes)." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
}

$oldEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $tarEntriesRaw = & tar.exe -tzf $ArchivePath 2>&1
    $tarListCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldEap
}

if ($tarListCode -ne 0) {
    Fail-Deploy "Could not read table of contents from distribution archive '$ArchivePath'." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
}

$tarEntries = @($tarEntriesRaw | ForEach-Object { ($_.ToString().Trim() -replace '^\./', '').TrimEnd('/') } | Where-Object { $_.Length -gt 0 })

$requiredEntries = @(
    "Dockerfile",
    ".dockerignore",
    "Directory.Build.props",
    "Receptbanken.sln",
    "src/Receptbanken.Core/Receptbanken.Core.csproj",
    "src/Receptbanken.Infrastructure/Receptbanken.Infrastructure.csproj",
    "src/Receptbanken.Infrastructure/Data/AppDbContext.cs",
    "src/Receptbanken.Infrastructure/Data/DbInitializer.cs",
    "src/Receptbanken.Infrastructure/Data/DesignTimeDbContextFactory.cs",
    "src/Receptbanken.Infrastructure/Backup/ZipBackupService.cs",
    "src/Receptbanken.Infrastructure/Storage/LocalImageStorageService.cs",
    "src/Receptbanken.Infrastructure/Migrations/AppDbContextModelSnapshot.cs",
    "src/Receptbanken.Web/Receptbanken.Web.csproj",
    "src/Receptbanken.Web/Program.cs",
    "src/Receptbanken.Web/Components/App.razor"
)

foreach ($req in $requiredEntries) {
    if ($tarEntries -notcontains $req) {
        Fail-Deploy "Distribution archive is missing required build file '$req'." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
}

# Verify that every single .cs, .razor, and .csproj source file under src/ (outside bin/obj) is included in the archive
$expectedCodeFiles = Get-ChildItem -LiteralPath (Join-Path $RepoRoot "src") -Recurse -File |
    Where-Object {
        $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/').Replace('\', '/')
        ($rel -notmatch '(^|/)(bin|obj|\.vs|\.git)/') -and ($_.Extension -in @(".cs", ".razor", ".csproj"))
    }
foreach ($codeFile in $expectedCodeFiles) {
    $relCode = $codeFile.FullName.Substring($RepoRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($tarEntries -notcontains $relCode) {
        Fail-Deploy "Source file '$relCode' is missing from the distribution archive!" -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
}

$forbiddenPatterns = @(
    '(^|/)bin/',
    '(^|/)obj/',
    '(^|/)\.git/',
    '(^|/)\.vs/',
    '(^|/)\.vscode/',
    '(^|/)TestResults/',
    '(^|/)\.deploy\.env',
    '(^|/)\.env',
    '\.(db|db-wal|db-shm|sqlite|sqlite3|pem|key|pfx|zip|tar|gz)$'
)

foreach ($entry in $tarEntries) {
    foreach ($pattern in $forbiddenPatterns) {
        if ($entry -match $pattern) {
            Fail-Deploy "Security check blocked archive: forbidden file '$entry' was found in the distribution archive." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
        }
    }
}

$archiveKb = [math]::Round($archiveSize / 1KB, 1)
Write-Success "Distribution archive created and verified ($archiveKb KB, $($tarEntries.Count) entries, no sensitive files)."

if ($DryRun) {
    Write-Step "[DRY-RUN] Summary of planned remote steps on Ubuntu ($SshTarget):"
    Write-Host " 1. Verify Docker, Docker Compose, and free disk space (at least 2048 MB in $DeployPath and /var/lib)"
    Write-Host " 2. Verify installation mode ($(if ($FirstInstall) { 'First installation (-FirstInstall)' } else { 'Update existing installation' })) and data volume '$VolumeName'"
    Write-Host " 3. Create temporary build directory '$RemoteBuildDir' (never inside /app/data or '$VolumeName')"
    Write-Host " 4. Transfer distribution archive ($archiveKb KB) and docker-compose.yml via SCP to $SshTarget"
    Write-Host " 5. Extract archive in '$RemoteBuildDir' and run 'docker build --platform linux/amd64 -t $ImageRef .' while existing container keeps running"
    if (-not $FirstInstall) {
        Write-Host " 6. Stop existing container cleanly, create raw volume backup '$DeployPath/backups/volume-$VersionTag.tar.gz', verify SHA-256 and confirm receptbanken.db is in archive"
    }
    Write-Host " 7. Update $DeployPath/.env with IMAGE_TAG=$VersionTag and run 'docker compose -p receptbanken -f $DeployPath/docker-compose.yml up -d --no-build'"
    Write-Host " 8. Verify Docker healthcheck and GET /health"
    Write-Host " 9. Remove '$RemoteBuildDir', retain latest $BackupRetentionCount backups and latest $ImageRetentionCount Docker images"
    Remove-TempArtifacts -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    Write-Success "Dry-run completed without errors."
    exit 0
}

# Step 3A & Step 5 (Pre-flight) - Check server environment, disk space, and existing installation via SSH
Write-Step "Step 3: Connecting to $SshTarget to verify server environment, disk space, and existing installation..."
$preflightScript = @"
set -e
command -v docker >/dev/null 2>&1 || { echo "ERR_NO_DOCKER"; exit 10; }
docker compose version >/dev/null 2>&1 || { echo "ERR_NO_COMPOSE"; exit 11; }
command -v tar >/dev/null 2>&1 || { echo "ERR_NO_TAR"; exit 13; }
command -v base64 >/dev/null 2>&1 || { echo "ERR_NO_BASE64"; exit 13; }
mkdir -p "$DeployPath/backups" "$DeployPath/builds"

# Check free disk space in KB on DeployPath and /var/lib (require >= 2097152 KB = 2 GB)
DEPLOY_FREE_KB=`$(df -Pk "$DeployPath" 2>/dev/null | awk 'NR==2 {print `$4}')
DOCKER_FREE_KB=`$(df -Pk /var/lib/docker 2>/dev/null | awk 'NR==2 {print `$4}')
if [ -z "`$DOCKER_FREE_KB" ]; then
    DOCKER_FREE_KB=`$(df -Pk /var/lib 2>/dev/null | awk 'NR==2 {print `$4}')
fi
if [ -z "`$DOCKER_FREE_KB" ]; then
    DOCKER_FREE_KB="`$DEPLOY_FREE_KB"
fi
if [ -z "`$DEPLOY_FREE_KB" ] || [ "`$DEPLOY_FREE_KB" -lt 2097152 ] || [ "`$DOCKER_FREE_KB" -lt 2097152 ]; then
    echo "ERR_DISK_SPACE:DeployKB=`$DEPLOY_FREE_KB,DockerKB=`$DOCKER_FREE_KB"
    exit 12
fi

VOL_EXISTS="false"
if docker volume inspect "$VolumeName" >/dev/null 2>&1; then
    VOL_EXISTS="true"
fi

CONTAINER_EXISTS="false"
CONTAINER_RUNNING="false"
MOUNTED_VOL=""
CURRENT_IMAGE=""
if docker container inspect receptbanken >/dev/null 2>&1; then
    CONTAINER_EXISTS="true"
    CONTAINER_RUNNING=`$(docker container inspect -f '{{.State.Running}}' receptbanken 2>/dev/null || echo "false")
    MOUNTED_VOL=`$(docker container inspect -f '{{range .Mounts}}{{if eq .Destination "/app/data"}}{{.Name}}{{end}}{{end}}' receptbanken 2>/dev/null || echo "")
    CURRENT_IMAGE=`$(docker container inspect -f '{{.Config.Image}}' receptbanken 2>/dev/null || echo "")
fi

# Ensure this version tag does not already exist on the server
if docker image inspect "$ImageRef" >/dev/null 2>&1; then
    echo "ERR_TAG_ALREADY_EXISTS:$ImageRef"
    exit 14
fi

echo "STATE|VOL_EXISTS=`$VOL_EXISTS|CONTAINER_EXISTS=`$CONTAINER_EXISTS|CONTAINER_RUNNING=`$CONTAINER_RUNNING|MOUNTED_VOL=`$MOUNTED_VOL|CURRENT_IMAGE=`$CURRENT_IMAGE"
"@

$preResult = Invoke-RemoteBash -Script $preflightScript -AllowFailure
if ($preResult.ExitCode -ne 0) {
    if ($preResult.Output -match "ERR_NO_DOCKER") {
        Fail-Deploy "Docker is not installed or not accessible for user $ServerUser on $ServerHost." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    elseif ($preResult.Output -match "ERR_NO_COMPOSE") {
        Fail-Deploy "Docker Compose ('docker compose') is missing on $ServerHost." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    elseif ($preResult.Output -match "ERR_DISK_SPACE") {
        Fail-Deploy "Insufficient free disk space on server (at least 2 GB required): $($preResult.Output)" -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    elseif ($preResult.Output -match "ERR_TAG_ALREADY_EXISTS") {
        Fail-Deploy "Image tag '$ImageRef' already exists on the server. Wait one second and try again." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    else {
        Fail-Deploy "Could not verify server environment on ${SshTarget}: $($preResult.Output)" -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
}

$stateLine = ($preResult.Output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -like "STATE|*" } | Select-Object -Last 1)
if (-not $stateLine) {
    Fail-Deploy "Unexpected response from server pre-flight check: $($preResult.Output)" -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
}

$stateMap = @{}
foreach ($kv in ($stateLine.Substring(6) -split '\|')) {
    $pair = $kv -split '=', 2
    if ($pair.Count -eq 2) { $stateMap[$pair[0].Trim()] = $pair[1].Trim() }
}

$volExists = ($stateMap["VOL_EXISTS"] -eq "true")
$containerExists = ($stateMap["CONTAINER_EXISTS"] -eq "true")
$containerRunning = ($stateMap["CONTAINER_RUNNING"] -eq "true")
$mountedVol = $stateMap["MOUNTED_VOL"]
$currentImage = $stateMap["CURRENT_IMAGE"]

# Enforce strict separation between FirstInstall and Update BEFORE uploading/building
if ($FirstInstall) {
    if ($volExists -or $containerExists) {
        Fail-Deploy "Flag -FirstInstall was specified, but volume '$VolumeName' ($volExists) or container 'receptbanken' ($containerExists) already exists on the server! Aborting to protect existing data. Run without -FirstInstall to update." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    Write-Success "Verified that no existing installation or volume is present. Proceeding with first installation."
}
else {
    if (-not $volExists -and -not $containerExists) {
        Fail-Deploy "Neither container 'receptbanken' nor data volume '$VolumeName' was found on the server. If this is a brand new installation, run with -FirstInstall. Aborting so a new empty volume is never created by mistake." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    if (-not $volExists) {
        Fail-Deploy "Container exists, but expected data volume '$VolumeName' is missing on the server! Aborting immediately." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    if ($containerExists -and $mountedVol -ne $VolumeName) {
        Fail-Deploy "Existing container 'receptbanken' uses volume '$mountedVol' instead of expected '$VolumeName'! Aborting to prevent volume switch." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
    Write-Success "Verified existing installation and confirmed data volume '$VolumeName' is in use."
}

# Step 3B - Create isolated build directory and transfer distribution archive via SCP
Write-Step "Transferring distribution archive ($archiveKb KB) and docker-compose.yml via SCP to $SshTarget..."
$null = Invoke-RemoteBash -Script "set -e`nrm -rf `"$RemoteBuildDir`"`nmkdir -p `"$RemoteBuildDir`""

$oldEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & scp @ScpArgs "$ComposeFileLocal" "${SshTarget}:${DeployPath}/docker-compose.yml"
    $scpComposeCode = $LASTEXITCODE
    if ($scpComposeCode -ne 0) {
        $ErrorActionPreference = $oldEap
        Fail-Deploy "SCP transfer of docker-compose.yml failed (exit code $scpComposeCode)." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }

    & scp @ScpArgs "$ArchivePath" "${SshTarget}:${RemoteArchivePath}"
    $scpArchiveCode = $LASTEXITCODE
    if ($scpArchiveCode -ne 0) {
        $ErrorActionPreference = $oldEap
        Fail-Deploy "SCP transfer of distribution archive failed (exit code $scpArchiveCode)." -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
    }
}
finally {
    $ErrorActionPreference = $oldEap
    Remove-TempArtifacts -ArchiveFile $ArchivePath -KeepArchive $KeepCustomArchive
}
Write-Success "Distribution archive transferred to '$RemoteBuildDir'."

# Step 4 - Build Docker image on Ubuntu while existing container continues running
Write-Step "Step 4: Building Docker image ($ImageRef) on Ubuntu (existing container continues running)..."
$remoteBuildScript = @"
set -e
cd "$RemoteBuildDir"
tar -xzf "$RemoteArchivePath"
rm -f "$RemoteArchivePath"

if [ ! -f "Dockerfile" ] || [ ! -d "src" ]; then
    echo "ERR_INVALID_BUILD_CONTEXT"
    cd "$DeployPath" && rm -rf "$RemoteBuildDir"
    exit 15
fi

docker build --platform linux/amd64 -t "$ImageRef" .
docker image inspect "$ImageRef" >/dev/null 2>&1 || {
    echo "ERR_IMAGE_NOT_BUILT"
    cd "$DeployPath" && rm -rf "$RemoteBuildDir"
    exit 16
}
echo "BUILD_OK"
"@

$buildResult = Invoke-RemoteBash -Script $remoteBuildScript -AllowFailure
if ($buildResult.ExitCode -ne 0 -or $buildResult.Output -notmatch "BUILD_OK") {
    $null = Invoke-RemoteBash -Script "rm -rf `"$RemoteBuildDir`"" -AllowFailure
    Fail-Deploy "Docker build on Ubuntu failed. The existing application was NOT stopped or modified.`nDetails:`n$($buildResult.Output)"
}
Write-Success "Docker image '$ImageRef' built successfully on Ubuntu."

# Step 5 & Step 6 - Verify existing database and create verified backup before update
if (-not $FirstInstall) {
    Write-Step "Step 5 & 6: Verifying existing database and creating verified volume backup before container replacement..."

    $backupScript = @"
set -e
BACKUP_DIR="$DeployPath/backups"
mkdir -p "`$BACKUP_DIR"

# 1. Verify newly built image exists and check free disk space (>= 2 GB) before stopping container
docker image inspect "$ImageRef" >/dev/null 2>&1 || { echo "ERR_NEW_IMAGE_MISSING"; exit 20; }

DEPLOY_FREE_KB=`$(df -Pk "$DeployPath" 2>/dev/null | awk 'NR==2 {print `$4}')
if [ -z "`$DEPLOY_FREE_KB" ] || [ "`$DEPLOY_FREE_KB" -lt 2097152 ]; then
    echo "ERR_LOW_SPACE_BEFORE_BACKUP:`$DEPLOY_FREE_KB"
    exit 21
fi

# Verify existing SQLite database in volume is present and non-empty before stopping container
docker run --rm --entrypoint /bin/sh -u 0 -v "${VolumeName}:/source:ro" "$ImageRef" -c 'test -s /source/receptbanken.db' || {
    echo "ERR_DB_MISSING_OR_EMPTY_IN_VOLUME"
    exit 22
}

# Optional portable ZIP export while old container is still running
if docker container inspect -f '{{.State.Running}}' receptbanken 2>/dev/null | grep -q "true"; then
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --max-time 15 "http://127.0.0.1:$AppPort/api/backup/export" -o "`$BACKUP_DIR/app-$VersionTag.zip" 2>/dev/null || rm -f "`$BACKUP_DIR/app-$VersionTag.zip"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -T 15 -O "`$BACKUP_DIR/app-$VersionTag.zip" "http://127.0.0.1:$AppPort/api/backup/export" 2>/dev/null || rm -f "`$BACKUP_DIR/app-$VersionTag.zip"
    fi
fi

# 2. Stop old container cleanly so SQLite WAL checkpoints completely to receptbanken.db
WAS_RUNNING="false"
if docker container inspect -f '{{.State.Running}}' receptbanken 2>/dev/null | grep -q "true"; then
    WAS_RUNNING="true"
    docker stop -t 15 receptbanken >/dev/null
fi

restart_old_and_fail() {
    ERR_MSG="`$1"
    CODE="`$2"
    echo "`$ERR_MSG"
    if [ "`$WAS_RUNNING" = "true" ]; then
        docker start receptbanken >/dev/null 2>&1 || true
    fi
    exit "`$CODE"
}

# 3. Create consistent raw backup of receptbanken-data
RAW_ARCHIVE="`$BACKUP_DIR/volume-$VersionTag.tar.gz"
docker run --rm --entrypoint /bin/sh -u 0 \
    -v "${VolumeName}:/source:ro" \
    -v "`$BACKUP_DIR:/backup" \
    "$ImageRef" \
    -c "cd /source && tar -czf /backup/volume-$VersionTag.tar.gz ." || restart_old_and_fail "ERR_TAR_CREATE_FAILED" 23

if [ ! -s "`$RAW_ARCHIVE" ]; then
    restart_old_and_fail "ERR_BACKUP_EMPTY" 24
fi

# 4. Create SHA-256 checksum and verify it immediately
(cd "`$BACKUP_DIR" && sha256sum "volume-$VersionTag.tar.gz" > "volume-$VersionTag.tar.gz.sha256") || restart_old_and_fail "ERR_SHA256_CREATE_FAILED" 25
(cd "`$BACKUP_DIR" && sha256sum -c "volume-$VersionTag.tar.gz.sha256" >/dev/null 2>&1) || restart_old_and_fail "ERR_SHA256_VERIFY_FAILED" 26

# 5. Verify archive can be read and contains receptbanken.db
tar -tzf "`$RAW_ARCHIVE" >/dev/null 2>&1 || restart_old_and_fail "ERR_ARCHIVE_UNREADABLE" 27
tar -tzf "`$RAW_ARCHIVE" | grep -E '(^\./|^)receptbanken\.db$' >/dev/null 2>&1 || restart_old_and_fail "ERR_BACKUP_MISSING_DB" 28

ARCHIVE_BYTES=`$(wc -c < "`$RAW_ARCHIVE" | tr -d ' ')
echo "BACKUP_OK|BYTES=`$ARCHIVE_BYTES"
"@

    $backupResult = Invoke-RemoteBash -Script $backupScript -AllowFailure
    if ($backupResult.ExitCode -ne 0 -or $backupResult.Output -notmatch "BACKUP_OK\|") {
        $null = Invoke-RemoteBash -Script "rm -rf `"$RemoteBuildDir`"" -AllowFailure
        Fail-Deploy "Backup of volume '$VolumeName' failed or could not be verified ($($backupResult.Output)). The old container has been restarted and the active image version was not changed."
    }
    Write-Success "Raw volume backup (volume-$VersionTag.tar.gz) and SHA-256 checksum created and verified ($($backupResult.Output))."
}

# Step 7 - Start with Docker Compose and verify healthcheck
Write-Step "Step 7: Updating IMAGE_TAG and starting container with Docker Compose..."
$startRemoteScript = @"
set -e
cd "$DeployPath"

# Save previous image reference only now when backup is approved and we are switching active version
if [ -n "$currentImage" ]; then
    echo "$currentImage" > "$DeployPath/.previous_image"
    PREV_TAG=`$(echo "$currentImage" | cut -d: -f2)
    echo "`$PREV_TAG" > "$DeployPath/.previous_version"
fi

# Ensure the named volume exists before compose up (only creates on -FirstInstall)
if ! docker volume inspect "$VolumeName" >/dev/null 2>&1; then
    docker volume create "$VolumeName" >/dev/null
fi

docker tag "$ImageRef" "receptbanken:latest"

cat > "$DeployPath/.env" <<EOF
IMAGE_TAG=$VersionTag
APP_PORT=$AppPort
VAPID_SUBJECT=$VapidSubject
EOF

echo "$VersionTag" > "$DeployPath/.current_version"

# Scope strictly to project 'receptbanken' without touching any other containers on the host
docker compose -p receptbanken -f "$DeployPath/docker-compose.yml" --env-file "$DeployPath/.env" up -d --no-build
"@

$null = Invoke-RemoteBash -Script $startRemoteScript
Write-Success "Container started with image '$ImageRef' and data volume '$VolumeName'."

Write-Step "Verifying Docker healthcheck and /health..."
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

    if [ "`$STATUS" = "unhealthy" ] || [ "`$STATUS" = "missing" ]; then
        echo "HEALTH_FAILED:`$STATUS"
        docker logs --tail 50 receptbanken 2>&1 || true
        exit 30
    fi

    # Also verify via built-in CLI probe (/health) once container is running
    if [ "`$RUNNING" = "true" ] && docker exec receptbanken dotnet Receptbanken.Web.dll --healthcheck >/dev/null 2>&1; then
        echo "HEALTHY_OK"
        exit 0
    fi

    sleep 2
    ATTEMPT=`$((ATTEMPT + 1))
done

echo "HEALTH_TIMEOUT"
docker logs --tail 50 receptbanken 2>&1 || true
exit 31
"@

$healthResult = Invoke-RemoteBash -Script $healthScript -AllowFailure
if ($healthResult.ExitCode -ne 0 -or $healthResult.Output -notmatch "HEALTHY_OK") {
    $null = Invoke-RemoteBash -Script "rm -rf `"$RemoteBuildDir`"" -AllowFailure
    Write-Host "`n[ERROR] The new version ($ImageRef) failed the health check!" -ForegroundColor Red
    Write-Host $healthResult.Output -ForegroundColor Yellow
    Write-Host "`nNOTE: No automatic database restore has been performed." -ForegroundColor Yellow
    Write-Host "To roll back to the previous Docker image, run:" -ForegroundColor Cyan
    Write-Host "  .\scripts\rollback.ps1" -ForegroundColor White
    exit 1
}
Write-Success "Health check (/health and SQLite connection) passed!"

# Step 8 - Clean up temporary build files and apply retention policy
Write-Step "Step 8: Cleaning up temporary build files and applying retention policy..."
$cleanupScript = @"
set -e
# Remove temporary build directory (never touches $VolumeName or other containers)
rm -rf "$RemoteBuildDir"
find "$DeployPath/builds" -mindepth 1 -maxdepth 1 -type d -exec rm -rf {} + 2>/dev/null || true

# Retain latest $BackupRetentionCount verified volume-*.tar.gz backup sets
cd "$DeployPath/backups"
VOL_BACKUPS=`$(ls -1t volume-*.tar.gz 2>/dev/null || true)
COUNT=0
for FILE in `$VOL_BACKUPS; do
    COUNT=`$((COUNT + 1))
    if [ `$COUNT -gt $BackupRetentionCount ]; then
        TS=`$(echo "`$FILE" | sed -e 's/^volume-//' -e 's/\.tar\.gz$//')
        rm -f "volume-`$TS.tar.gz" "volume-`$TS.tar.gz.sha256" "app-`$TS.zip"
    fi
done

# Retain latest $ImageRetentionCount receptbanken:<timestamp> images, never deleting active or previous
PREV_VER=`$(cat "$DeployPath/.previous_version" 2>/dev/null || echo "")
IMG_TAGS=`$(docker images receptbanken --format '{{.Tag}}' | grep -E '^[0-9]{8}-[0-9]{6}$' | sort -r || true)
IMG_COUNT=0
for TAG in `$IMG_TAGS; do
    if [ "`$TAG" = "$VersionTag" ] || [ "`$TAG" = "`$PREV_VER" ]; then
        continue
    fi
    IMG_COUNT=`$((IMG_COUNT + 1))
    if [ `$IMG_COUNT -ge $ImageRetentionCount ]; then
        docker rmi "receptbanken:`$TAG" >/dev/null 2>&1 || true
    fi
done
"@

$null = Invoke-RemoteBash -Script $cleanupScript -AllowFailure
Write-Success "Build directory cleanup and retention policy completed."

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " Deployment completed successfully!" -ForegroundColor Green
Write-Host " Version     : $ImageRef"
Write-Host " Data Volume : $VolumeName"
Write-Host " URL         : http://${ServerHost}:${AppPort}"
Write-Host "============================================================" -ForegroundColor Green
exit 0
