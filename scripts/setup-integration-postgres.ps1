param(
    [string]$ContainerName = "subtru-integration-postgres",
    [string]$PostgresUser = "postgres",
    [string]$PostgresPassword = "postgres",
    [string]$Database = "subtru_test",
    [int]$HostPort = 5432,
    [switch]$SetUserEnvVar
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$MigrationDir = Join-Path $RepoRoot "db\migrations"
$OwnershipLabel = "com.subtru.integration-postgres"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Get-ContainerId {
    param([string]$Name, [switch]$RunningOnly)

    $args = @("ps")
    if (-not $RunningOnly) {
        $args += "-a"
    }

    $args += @("-q", "-f", "name=^${Name}$")

    $output = docker @args | Select-Object -First 1
    if ($null -eq $output) {
        return $null
    }

    $value = $output.ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return $value
}

function Assert-SubTruIntegrationContainer {
    param([string]$Name)

    $labelsJson = docker inspect --format "{{json .Config.Labels}}" $Name 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect Docker container '$Name'."
    }

    $labels = $labelsJson | ConvertFrom-Json
    $ownershipProperty = if ($null -eq $labels) {
        $null
    }
    else {
        $labels.PSObject.Properties[$OwnershipLabel]
    }
    $ownership = if ($null -eq $ownershipProperty) {
        $null
    }
    else {
        $ownershipProperty.Value
    }

    if ($ownership -ne "true") {
        throw "Refusing to use container '$Name' because it was not created by this script. Choose another -ContainerName or remove the existing container manually."
    }
}

Write-Step "Checking Docker availability"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI not found in PATH. Install Docker Desktop first."
}

$existingContainerId = Get-ContainerId -Name $ContainerName

if ([string]::IsNullOrWhiteSpace($existingContainerId)) {
    Write-Step "Starting new Postgres container '$ContainerName' on localhost:$HostPort"
    docker run --name $ContainerName `
      --label "$OwnershipLabel=true" `
      -e "POSTGRES_USER=$PostgresUser" `
      -e "POSTGRES_PASSWORD=$PostgresPassword" `
      -e "POSTGRES_DB=$Database" `
      -p "${HostPort}:5432" `
      -d postgres:16 | Out-Null
}
else {
    Assert-SubTruIntegrationContainer -Name $ContainerName

    $runningContainerId = Get-ContainerId -Name $ContainerName -RunningOnly
    if ([string]::IsNullOrWhiteSpace($runningContainerId)) {
        Write-Step "Starting existing Postgres container '$ContainerName'"
        docker start $ContainerName | Out-Null
    }
    else {
        Write-Step "Container '$ContainerName' already running"
    }
}

Write-Step "Waiting for Postgres readiness"
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $status = docker exec $ContainerName pg_isready -U $PostgresUser -d $Database 2>$null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
    }
    catch {
    }

    Start-Sleep -Seconds 1
}

if (-not $ready) {
    throw "Postgres did not become ready in time. Check container logs: docker logs $ContainerName"
}

if ($Database -in @("postgres", "template0", "template1")) {
    throw "Refusing to reset reserved database '$Database'. Choose a test database name."
}

if ($Database -notmatch "^[A-Za-z0-9_]+$") {
    throw "Database name '$Database' must contain only letters, numbers, and underscores."
}

Write-Step "Resetting disposable integration database '$Database'"
$resetSql = @"
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = '$Database'
  AND pid <> pg_backend_pid();

DROP DATABASE IF EXISTS "$Database";
CREATE DATABASE "$Database";
"@

$resetSql | docker exec -i $ContainerName psql -U $PostgresUser -d postgres -v ON_ERROR_STOP=1 | Out-Null

$migrationFiles = @(Get-ChildItem -Path $MigrationDir -Filter "*.sql" |
    Sort-Object Name)

if ($migrationFiles.Count -eq 0) {
    throw "No migration files found in $MigrationDir"
}

Write-Step "Applying $($migrationFiles.Count) migrations"

foreach ($file in $migrationFiles) {
    $relativePath = "db/migrations/$($file.Name)"
    Write-Host "Applying $relativePath"
    Get-Content $file.FullName -Raw | docker exec -i $ContainerName psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 | Out-Null
}

$connectionString = "Host=localhost;Port=$HostPort;Database=$Database;Username=$PostgresUser;Password=$PostgresPassword;Pooling=true;Timeout=15;Command Timeout=30"
$env:SUBTRU_TEST_DB_CONNECTION = $connectionString

Write-Step "Set process env var: SUBTRU_TEST_DB_CONNECTION"
Write-Host "Configured for localhost:$HostPort/$Database as '$PostgresUser' (password hidden)."

if ($SetUserEnvVar) {
    [Environment]::SetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION", $connectionString, "User")
    Write-Step "Set user env var (restart VS Code to pick up changes)"
}

Write-Step "Done"
Write-Host "Run integration test:" -ForegroundColor Green
Write-Host 'dotnet test .\tests\SubTru.Tests\SubTru.Tests.csproj --filter "FullyQualifiedName~PostgresProjectAuthServiceIntegrationTests"' -ForegroundColor Green
