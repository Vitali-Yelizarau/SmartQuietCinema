#Requires -Version 5.1
<#
.SYNOPSIS
Copies a diagnostic probe ZIP from an existing VMware share, verifies its independent SHA-256, and extracts it to a new guest-local directory.
.PARAMETER SharePath
Existing VMware shared folder containing the diagnostic ZIP and its sidecar.
.PARAMETER Destination
New guest-local directory under an existing parent. Defaults to a new directory under C:\Test.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SharePath,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedProbeSha256,
    [string]$Destination = (Join-Path 'C:\Test' ('SmartQuietCinema-Pcm-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$name = 'SmartQuietCinema-VMware-PCM-Diagnostics-win-x64.zip'
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
$parent = [IO.Path]::GetDirectoryName($destinationPath)
if ($destinationPath.StartsWith('\\') -or (Test-Path -LiteralPath $destinationPath) -or ![IO.Directory]::Exists($parent)) {
    throw 'Supply an unused guest-local destination under an existing directory.'
}
$stage = Join-Path $parent ('.sqc-pcm-transfer-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try {
    Copy-Item -LiteralPath (Join-Path $SharePath $name), (Join-Path $SharePath "$name.sha256") -Destination $stage
    $zip = Join-Path $stage $name
    $sidecar = (Get-Content -LiteralPath "$zip.sha256" -Raw).Trim().Split(' ')[0]
    if ($sidecar -ne $ExpectedProbeSha256 -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $ExpectedProbeSha256) {
        throw 'Diagnostic ZIP SHA-256 mismatch.'
    }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $stage 'Extracted')
    [IO.Directory]::Move($stage, $destinationPath)
    Write-Output "Verified diagnostic ZIP: $(Join-Path $destinationPath $name)"
    Write-Output "Guest runner: $(Join-Path $destinationPath 'Extracted\Invoke-VMwareProbeUpdate.ps1')"
} finally {
    if ([IO.Directory]::Exists($stage) -and [IO.Path]::GetDirectoryName($stage) -eq $parent -and [IO.Path]::GetFileName($stage) -match '^\.sqc-pcm-transfer-[0-9a-f]{32}$') {
        [IO.Directory]::Delete($stage, $true)
    }
}
