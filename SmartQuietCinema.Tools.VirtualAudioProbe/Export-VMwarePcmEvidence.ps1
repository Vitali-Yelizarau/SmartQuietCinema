#Requires -Version 5.1
<# .SYNOPSIS
Packages the immediate PCM evidence directories from a guest-local test run and records the archive SHA-256.
#>
[CmdletBinding()]
param([string]$RunRoot = 'C:\Test\SmartQuietCinema-PCM-Native')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Get-Item -LiteralPath $RunRoot).FullName
$evidence = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '\APcmEvidence-[0-9a-f]{32}\z' })
if (!$evidence.Count) { throw "No PCM evidence directories found under $root." }
if (@($evidence | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Evidence directories must be local directories.' }
$name = 'PcmNativeEvidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.zip'
$zip = Join-Path $root $name
Compress-Archive -LiteralPath $evidence.FullName -DestinationPath $zip -CompressionLevel Fastest
$sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$sha256  $name" | Set-Content -LiteralPath "$zip.sha256" -Encoding Ascii
Write-Output "Evidence ZIP: $zip"
Write-Output "Evidence SHA-256: $sha256"
