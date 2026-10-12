param(
    [string]$ContainerName = "subtru-integration-postgres",
    [switch]$ClearProcessEnvVar,
    [switch]$ClearUserEnvVar
)

$ErrorActionPreference = "Stop"
$OwnershipLabel = "com.subtru.integration-postgres"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Yellow
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
        throw "Refusing to remove container '$Name' because it was not created by the SubTru integration setup script. Remove it manually if that is intentional."
    }
}

Write-Step "Checking Docker availability"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI not found in PATH. Install Docker Desktop first."
}

$existingContainerId = Get-ContainerId -Name $ContainerName
if ([string]::IsNullOrWhiteSpace($existingContainerId)) {
    Write-Step "Container '$ContainerName' does not exist"
}
else {
    Assert-SubTruIntegrationContainer -Name $ContainerName

    $runningContainerId = Get-ContainerId -Name $ContainerName -RunningOnly
    if (-not [string]::IsNullOrWhiteSpace($runningContainerId)) {
        Write-Step "Stopping container '$ContainerName'"
        docker stop $ContainerName | Out-Null
    }

    Write-Step "Removing container '$ContainerName'"
    docker rm --volumes $ContainerName | Out-Null
}

if ($ClearProcessEnvVar) {
    Write-Step "Clearing process env var SUBTRU_TEST_DB_CONNECTION"
    Remove-Item Env:SUBTRU_TEST_DB_CONNECTION -ErrorAction SilentlyContinue
}

if ($ClearUserEnvVar) {
    Write-Step "Clearing user env var SUBTRU_TEST_DB_CONNECTION"
    [Environment]::SetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION", $null, "User")
}

Write-Step "Done"
Write-Host "Integration Postgres teardown complete." -ForegroundColor Green
