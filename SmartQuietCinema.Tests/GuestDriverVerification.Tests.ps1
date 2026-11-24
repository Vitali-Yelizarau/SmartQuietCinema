#Requires -Version 5.1
<#
.SYNOPSIS
Exercises the production guest readiness gates using a synthetic transfer ZIP and Authenticode and SignTool test doubles.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$validator = Join-Path $repo 'SmartQuietCinema.Driver.VirtualAudio\Test-HyperVTestBundle.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($validator, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw 'Guest validator did not parse.' }
$functions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in 'Assert-DriverLabManifest', 'Assert-GuestDriverSignatures', 'Assert-GuestTransferArchive' }, $false))
if ($functions.Count -ne 3) { throw 'The expected production readiness gates were not found.' }
foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }

$thumb = 'A' * 40
$wrongThumb = 'B' * 40
$revision = '1' * 40
$sys = 'test.sys'
$inf = 'test.inf'
$cat = 'test.cat'
$allCommands = @("verify /pa /v $sys", "verify /pa /v $cat", "verify /pa /v /c $cat $inf", "verify /pa /v /c $cat $sys")

function Get-AuthenticodeSignature {
    <# .SYNOPSIS
    Returns the per-file Authenticode test result so guest trust and signer failures stay independent.
    #>
    param([string]$LiteralPath)
    if ($LiteralPath -eq $script:untrustedFile) { return @{ Status = 'UnknownError'; SignerCertificate = @{ Thumbprint = $thumb } } }
    if ($LiteralPath -eq $script:absentSignerFile) { return @{ Status = 'Valid'; SignerCertificate = $null } }
    if ($LiteralPath -eq $script:wrongSignerFile) { return @{ Status = 'Valid'; SignerCertificate = @{ Thumbprint = $wrongThumb } } }
    return @{ Status = 'Valid'; SignerCertificate = @{ Thumbprint = $thumb } }
}

function Invoke-TestSignTool {
    <# .SYNOPSIS
    Captures the native argument form and injects a nonzero exit at one verification call.
    #>
    $script:calls.Add(($args -join ' '))
    $global:LASTEXITCODE = if ($script:calls.Count -eq $script:failAt) { $script:failureCode } else { 0 }
}

function New-TestManifest {
    <# .SYNOPSIS
    Creates an independent bundle and driver-manifest pair for testing contradictory states and provenance.
    #>
    return @{
        revision = $revision
        workingTreeClean = $true
        target = 'Windows 11 x64 (build 22621 or later), VMware Workstation Pro'
        driver = @{ smartQuietCinemaRevision = $revision; certificateThumbprint = $thumb; signingMode = 'SignedPendingGuestVerification' }
        stageA = @{ signature = 'SignedPendingGuestVerification'; installability = 'NotInstallableUntilGuestVerification'; publicCertificate = 'Certificate/SmartQuietCinema-Lab.cer' }
    }
}

function Assert-Failure {
    <# .SYNOPSIS
    Fails the test if a deliberately invalid bundle reaches a successful readiness result.
    #>
    param([scriptblock]$Check, [string]$Expected)
    $failure = $null
    try { & $Check } catch { $failure = $_.Exception.Message }
    if (!$failure -or $failure -notlike "*$Expected*") { throw "Expected failure '$Expected', got '$failure'." }
}

$passed = 0
$oldExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$scratch = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('sqc-guest-gate-' + [guid]::NewGuid().ToString('N')))).FullName
$archivePath = Join-Path $scratch 'transfer.zip'
$manifestPath = Join-Path $scratch 'manifest.json'
try {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    [IO.File]::WriteAllText($manifestPath, '{"schemaVersion":1}')
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $manifestPath, 'manifest.json') } finally { $zip.Dispose() }
    $digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    Assert-GuestTransferArchive $archivePath $digest $manifestPath
    Assert-Failure { Assert-GuestTransferArchive $archivePath '' $manifestPath } 'independent SHA-256'
    Assert-Failure { Assert-GuestTransferArchive $archivePath ('0' * 64) $manifestPath } 'expected SHA-256'
    [IO.File]::AppendAllText($manifestPath, [Environment]::NewLine)
    Assert-Failure { Assert-GuestTransferArchive $archivePath $digest $manifestPath } 'extracted manifest does not match'
    $passed += 4

    $valid = New-TestManifest
    Assert-DriverLabManifest $valid $thumb
    $legacy = New-TestManifest
    $legacy.driver.signingMode = 'TestSignedAuthenticodeVerified'
    $legacy.stageA.signature = 'TestSignedAuthenticodeVerifiedOnHost'
    $legacy.stageA.installability = 'RequiresGuestPrerequisites'
    Assert-DriverLabManifest $legacy ''
    $passed += 2

    Assert-Failure { Assert-DriverLabManifest $valid '' } 'independently recorded signer'
    Assert-Failure { Assert-DriverLabManifest $valid $wrongThumb } 'expected signer identity'
    $passed += 2
    foreach ($case in 'installability', 'nativeMode', 'revision', 'cleanTree', 'certificate', 'target', 'outerMode') {
        $invalid = New-TestManifest
        $expected = 'Inconsistent guest-verification manifest status'
        switch ($case) {
            'installability' { $invalid.stageA.installability = 'RequiresGuestPrerequisites' }
            'nativeMode' { $invalid.driver.signingMode = 'TestSignedAuthenticodeVerified' }
            'revision' { $invalid.driver.smartQuietCinemaRevision = '2' * 40; $expected = 'clean revision' }
            'cleanTree' { $invalid.workingTreeClean = 'false'; $expected = 'clean revision' }
            'certificate' { $invalid.stageA.publicCertificate = 'Certificate/other.cer'; $expected = 'expected signer identity' }
            'target' { $invalid.target = 'Hyper-V Generation 2' }
            'outerMode' { $invalid.stageA.signature = 'TestSignedAuthenticodeVerifiedOnHost'; $expected = 'supported signed bundle' }
        }
        Assert-Failure { Assert-DriverLabManifest $invalid $thumb } $expected
        $passed++
    }

    $script:calls = New-Object 'System.Collections.Generic.List[string]'
    $script:untrustedFile = $script:absentSignerFile = $script:wrongSignerFile = ''
    $script:failAt = 0
    $script:failureCode = 1
    Assert-GuestDriverSignatures 'Invoke-TestSignTool' $inf $sys $cat $thumb
    if (($calls -join '|') -cne ($allCommands -join '|')) { throw 'Guest readiness did not require all four SignTool calls in order.' }
    $passed++

    foreach ($badFile in $sys, $cat) {
        foreach ($fault in 'untrustedFile', 'absentSignerFile', 'wrongSignerFile') {
            $calls.Clear()
            Set-Variable -Name $fault -Scope Script -Value $badFile
            Assert-Failure { Assert-GuestDriverSignatures 'Invoke-TestSignTool' $inf $sys $cat $thumb } "Guest trust or certificate mismatch: $badFile"
            $expectedCount = if ($badFile -eq $sys) { 0 } else { 1 }
            if ($calls.Count -ne $expectedCount) { throw 'An untrusted or unexpected signer reached SignTool.' }
            Set-Variable -Name $fault -Scope Script -Value ''
            $passed++
        }
    }
    foreach ($exitCode in 1, 2) {
        $script:failureCode = $exitCode
        for ($index = 1; $index -le $allCommands.Count; $index++) {
            $calls.Clear()
            $script:failAt = $index
            $expected = if ($index -le 2) { 'Guest SignTool verification failed' } else { 'Guest catalog verification failed' }
            Assert-Failure { Assert-GuestDriverSignatures 'Invoke-TestSignTool' $inf $sys $cat $thumb } $expected
            if ($calls.Count -ne $index -or ($calls -join '|') -cne ($allCommands[0..($index-1)] -join '|')) { throw 'A failed check did not stop the readiness gate.' }
            $passed++
        }
    }
    Write-Output "Guest driver verification gates: $passed passed, 0 failed, 0 skipped."
} finally {
    if ($null -ne $oldExitCode) { $global:LASTEXITCODE = $oldExitCode }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    [IO.File]::Delete($manifestPath)
    [IO.File]::Delete($archivePath)
    [IO.Directory]::Delete($scratch)
}
