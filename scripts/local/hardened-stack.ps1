<#
.SYNOPSIS
  Hardened local stack: OpenBao outside dev mode and SeaweedFS with S3 identities.

.DESCRIPTION
  Up      generates the storage identities (first run), starts the stack, bootstraps
          OpenBao (init, unseal, keys, policies, AppRole, root token revoked) and
          creates the artifact bucket with object lock using the storage admin identity.
  Verify  runs the OpenBao allow/deny matrix and the HardenedStackTests (storage
          identities, AppRole logins, the API cannot open an A1, the worker can).
  Run     runs the API or the worker against the hardened stack with its own
          AppRole and storage identity (-Process Api|Worker).
  Down    stops the stack and keeps its data. With -Reset it also deletes the data
          volumes and the generated credentials in -PrivateDir.

  Credentials are written to -PrivateDir (outside Git) and never printed. Synthetic
  data only: never put a real A1, CSC or secret in the hardened stack either.
#>
[CmdletBinding()]
param(
    [ValidateSet('Up', 'Verify', 'Run', 'Down')]
    [string]$Action = 'Up',
    [string]$PrivateDir = ([System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'rrf5-private')),
    [ValidateSet('Api', 'Worker')]
    [string]$Process = 'Api',
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$compose = [System.IO.Path]::Combine($repo, 'deploy', 'compose', 'docker-compose.hardened.yml')
$baoDir = Join-Path $PrivateDir 'openbao-local'
$s3Dir = Join-Path $PrivateDir 'seaweedfs-local'
$bucket = 'rrf5-regulatory'
$baoUrl = 'http://127.0.0.1:58201/'
$s3Url = 'http://127.0.0.1:58334/'

function Invoke-Compose {
    $env:RRF5_PRIVATE_DIR = $PrivateDir
    # Native tools write progress to stderr; only the exit code decides (PowerShell 5.1).
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker compose -f $compose @args 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed ($LASTEXITCODE)" }
}

function New-RandomHex([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

function Write-Private([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-EnvFile([string]$path) {
    $values = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*([^#=]+)=(.*)$') { $values[$Matches[1].Trim()] = $Matches[2].Trim() }
    }
    return $values
}

function Initialize-StorageIdentities {
    New-Item -ItemType Directory -Force $s3Dir | Out-Null
    $config = Join-Path $s3Dir 's3.json'
    if (Test-Path $config) { return }
    $identities = @()
    $definitions = @(
        @{ Name = 'admin';  Actions = @('Admin', 'Read', 'List', 'Write', 'Tagging') },
        @{ Name = 'worker'; Actions = @("Read:$bucket", "List:$bucket", "Write:$bucket") },
        # SeaweedFS 3.97 refuses GetObject to an identity without Write; the API never
        # writes, reads are pinned to the recorded version and hash-verified, and
        # COMPLIANCE versions cannot be deleted. A cloud bucket policy grants read only.
        @{ Name = 'api';    Actions = @("Read:$bucket", "Write:$bucket") }
    )
    foreach ($definition in $definitions) {
        $access = 'rrf5' + $definition.Name + (New-RandomHex 6)
        $secret = New-RandomHex 24
        $identities += @{ name = "rrf5-$($definition.Name)"; credentials = @(@{ accessKey = $access; secretKey = $secret }); actions = $definition.Actions }
        Write-Private (Join-Path $s3Dir "storage-$($definition.Name).env") ("ObjectStorage__AccessKey=$access`nObjectStorage__SecretKey=$secret`n")
    }
    Write-Private $config ((@{ identities = $identities } | ConvertTo-Json -Depth 6))
    Write-Host "storage identities generated in $s3Dir"
}

function Invoke-Dotnet([hashtable]$environment, [string[]]$arguments) {
    $saved = @{}
    foreach ($key in $environment.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $environment[$key]) }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet @arguments 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments -join ' ') failed ($LASTEXITCODE)" }
    }
    finally {
        $ErrorActionPreference = $previous
        foreach ($key in $environment.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}

switch ($Action) {
    'Up' {
        New-Item -ItemType Directory -Force $baoDir | Out-Null
        Initialize-StorageIdentities
        Write-Host '== Starting OpenBao (server mode) and SeaweedFS (identities)'
        Invoke-Compose up -d --wait openbao seaweedfs
        Write-Host '== OpenBao bootstrap'
        Invoke-Compose run --rm openbao-bootstrap
        Write-Host '== Artifact bucket (storage admin identity, once)'
        $admin = Read-EnvFile (Join-Path $s3Dir 'storage-admin.env')
        $environment = @{ 'ObjectStorage__ServiceUrl' = $s3Url; 'ObjectStorage__Bucket' = $bucket; 'DOTNET_ENVIRONMENT' = 'Development' } + $admin
        Invoke-Dotnet $environment @('run', '--project', ([System.IO.Path]::Combine($repo, 'src', 'Rrf5.Fiscal.Worker')), '--', 'ensure-bucket')
        Write-Host "Hardened stack is up: OpenBao $baoUrl, S3 $s3Url. Next: -Action Verify"
    }
    'Verify' {
        Write-Host '== OpenBao policy matrix'
        Invoke-Compose run --rm openbao-verify
        Write-Host '== HardenedStackTests'
        Invoke-Dotnet @{ 'RRF5_HARDENED_DIR' = $PrivateDir; 'RRF5_HARDENED_BAO_URL' = $baoUrl; 'RRF5_HARDENED_S3_URL' = $s3Url } `
            @('test', ([System.IO.Path]::Combine($repo, 'tests', 'Security', 'Rrf5.Fiscal.Tests.Security.csproj')), '--filter', 'FullyQualifiedName~HardenedStackTests', '--logger', 'console;verbosity=normal')
    }
    'Run' {
        $role = if ($Process -eq 'Api') { 'rrf5-api' } else { 'rrf5-worker' }
        $storage = if ($Process -eq 'Api') { 'storage-api.env' } else { 'storage-worker.env' }
        $environment = @{ 'OpenBao__Address' = $baoUrl; 'ObjectStorage__ServiceUrl' = $s3Url; 'ObjectStorage__Bucket' = $bucket; 'DOTNET_ENVIRONMENT' = 'Development' }
        $environment += Read-EnvFile (Join-Path $baoDir "$role.env")
        $environment += Read-EnvFile (Join-Path $s3Dir $storage)
        Write-Host "== $Process with AppRole $role and its storage identity (Ctrl+C stops it)"
        Invoke-Dotnet $environment @('run', '--project', ([System.IO.Path]::Combine($repo, 'src', "Rrf5.Fiscal.$Process")))
    }
    'Down' {
        if ($Reset) {
            Invoke-Compose down -v
            foreach ($dir in @($baoDir, $s3Dir)) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
            Write-Host 'hardened stack, its volumes and its generated credentials removed'
        }
        else {
            Invoke-Compose down
            Write-Host 'hardened stack stopped (data kept; it starts sealed and Up unseals it)'
        }
    }
}
