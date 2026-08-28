[CmdletBinding()]
param(
    [string] $ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\AnzExplorer.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AnzExplorer.exe")
)

$ErrorActionPreference = "Stop"
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$menuKey = "HKCU:\Software\Classes\Directory\shell\AnzExplorer"
$commandKey = Join-Path $menuKey "command"
New-Item -Path $commandKey -Force | Out-Null
Set-Item -LiteralPath $menuKey -Value "Open in Anz Explorer"
Set-ItemProperty -LiteralPath $menuKey -Name "Icon" -Value $resolvedExecutable
Set-Item -LiteralPath $commandKey -Value ('"{0}" "%V"' -f $resolvedExecutable)
Write-Output "Registered the per-user 'Open in Anz Explorer' folder menu."

