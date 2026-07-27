param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
}

Set-Location -LiteralPath $Root

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host 'Restarting as administrator...'

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Configuration', $Configuration
    )

    if ($NoBuild) {
        $arguments += '-NoBuild'
    }

    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WorkingDirectory $Root -Verb RunAs
    exit
}

$dotnet = Join-Path $Root '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = Join-Path $Root '.tools\dotnet\dotnet.exe'
}

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Portable dotnet was not found under .tools\dotnet10 or .tools\dotnet.'
}

$project = Join-Path $Root 'GitHubTurbo\GitHubTurbo.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw 'GitHubTurbo\GitHubTurbo.csproj was not found.'
}

$dotnetRoot = Split-Path -Parent $dotnet
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"

Write-Host "Using portable dotnet: $dotnet"
Write-Host "Project: $project"

if (-not $NoBuild) {
    Write-Host "Building $Configuration..."
    & $dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

Write-Host 'Starting GitHub Turbo...'
& $dotnet run --project $project -c $Configuration --no-build
exit $LASTEXITCODE
