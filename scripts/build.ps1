[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot ".dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

& $dotnet restore (Join-Path $repositoryRoot "AnzExplorer.sln") -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $repositoryRoot "AnzExplorer.sln") -c $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet test (Join-Path $repositoryRoot "tests\AnzExplorer.Core.Tests\AnzExplorer.Core.Tests.csproj") -c $Configuration -p:Platform=x64 --no-build --no-restore
exit $LASTEXITCODE

