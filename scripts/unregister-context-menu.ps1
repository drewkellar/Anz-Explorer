[CmdletBinding()]
param()

$menuKey = "HKCU:\Software\Classes\Directory\shell\AnzExplorer"
if (Test-Path -LiteralPath $menuKey) {
    Remove-Item -LiteralPath $menuKey -Recurse
}
Write-Output "Removed the per-user 'Open in Anz Explorer' folder menu."

