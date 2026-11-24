#Requires -Version 5.1
<#
.SYNOPSIS
Builds and validates a fresh SmartQuietCinema Virtual Audio package for a Hyper-V lab.
.PARAMETER SourceCheckout
Local Git checkout of the SysVAD revision required by Prepare-SysVad.ps1.
.PARAMETER OutputDirectory
New directory for the driver files, matching debugger symbols, and SHA-256 manifest. Its parent must exist.
.PARAMETER MSBuildPath
Optional path to the WDK-enabled MSBuild executable. Defaults to MSBuild.exe on PATH.
.PARAMETER KitRoot
Optional Windows Kits 10 root. Uses the installed KitsRoot10 registry value by default.
.PARAMETER CertificateThumbprint
Thumbprint of an existing code-signing certificate with a private key. Omit it for an unsigned package.
.PARAMETER CertificateStore
Personal store containing the selected certificate. CurrentUser is the default.
.PARAMETER DeferTrustToGuest
Stages signed files for guest verification. The host does not attest trust or catalog membership in this mode.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceCheckout,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$MSBuildPath,
    [string]$KitRoot,
    [ValidatePattern('\A[0-9A-Fa-f]{40}\z')][string]$CertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')][string]$CertificateStore = 'CurrentUser',
    [switch]$DeferTrustToGuest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RequiredFile {
    <#
    .SYNOPSIS
    Returns a nonempty file needed by the build or package and identifies the missing artifact on failure.
    #>
    param([string]$Path)

    $file = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $file -or $file.PSIsContainer -or $file.Length -eq 0) { throw "Missing or empty required file: $Path" }
    return $file
}

function Invoke-CheckedTool {
    <#
    .SYNOPSIS
    Displays native tool output and stops on any exit code other than success, including SignTool warnings.
    #>
    param([string]$Path, [string[]]$Arguments)

    & $Path @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Path)) failed with exit code $LASTEXITCODE." }
}

function Get-SigningCertificate {
    <#
    .SYNOPSIS
    Selects an existing code-signing identity so packaging can fail before building if its key is unavailable.
    #>
    param([string]$Thumbprint, [string]$Store)

    $identity = Get-Item -LiteralPath "Cert:\$Store\My\$Thumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $identity) { throw "Code-signing certificate $Thumbprint was not found in Cert:\$Store\My." }
    if (!$identity.HasPrivateKey) { throw "Certificate $Thumbprint has no private key in Cert:\$Store\My." }
    $now = Get-Date
    if ($now -lt $identity.NotBefore -or $now -gt $identity.NotAfter) { throw "Certificate $Thumbprint is outside its validity period." }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    if ($codeSigningOid -notin @($identity.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })) {
        throw "Certificate $Thumbprint does not carry the Code Signing enhanced key usage."
    }
    return $identity
}

function Get-NamedFileHashes {
    <#
    .SYNOPSIS
    Captures hashes of exact input files so the manifest identifies the bytes used and detects changes during packaging.
    #>
    param([string]$Directory, [string[]]$Names)

    $hashes = [ordered]@{}
    foreach ($name in $Names) {
        $file = Get-RequiredFile (Join-Path $Directory $name)
        $hashes[$name] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
    }
    return $hashes
}

$work = $null
$published = $false
try {
    if ($DeferTrustToGuest -and !$CertificateThumbprint) { throw '-DeferTrustToGuest requires a signing certificate.' }
    $kitVersion = '10.0.28000.0'
    $target = 'SmartQuietCinemaVirtualAudio'
    $hardwareId = 'ROOT\SmartQuietCinemaVirtualAudio'
    $configuration = 'Debug'
    $platform = 'x64'
    $infName = "$target.inf"
    $sysName = "$target.sys"
    $catName = "$target.cat"
    $pdbName = "$target.pdb"
    $driverFiles = @($infName, $sysName, $catName)
    $payloadFiles = @($infName, $sysName)
    $prepareFiles = @('Prepare-SysVad.ps1', 'SysVad-Preflight.patch', 'SysVad-AdapterOwnership.patch')
    $projectName = 'SmartQuietCinema.VirtualAudio.vcxproj'

    $checkout = Get-Item -LiteralPath $SourceCheckout -ErrorAction SilentlyContinue
    if ($null -eq $checkout -or !$checkout.PSIsContainer -or $checkout.PSProvider.Name -ne 'FileSystem') {
        throw "The pinned SysVAD checkout directory does not exist: $SourceCheckout"
    }
    $output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw "The output path already exists: $output" }
    $parent = [IO.Path]::GetDirectoryName($output)
    if (![IO.Directory]::Exists($parent)) { throw "The output parent directory does not exist: $parent" }

    $certificate = $null
    if ($CertificateThumbprint) {
        $certificate = Get-SigningCertificate $CertificateThumbprint $CertificateStore
    }
    if (!$KitRoot) { $KitRoot = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10).KitsRoot10 }
    if (!$MSBuildPath) {
        $msbuildCommand = Get-Command MSBuild.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$msbuildCommand) { throw 'MSBuild.exe was not found. Use the WDK Developer PowerShell or pass -MSBuildPath.' }
        $MSBuildPath = $msbuildCommand.Path
    }
    $msbuild = Get-RequiredFile $MSBuildPath
    $infVerif = Get-RequiredFile (Join-Path $KitRoot "Tools\$kitVersion\x64\InfVerif.exe")
    $inf2Cat = Get-RequiredFile (Join-Path $KitRoot "bin\$kitVersion\x86\Inf2Cat.exe")
    $signTool = Get-RequiredFile (Join-Path $KitRoot "bin\$kitVersion\x64\signtool.exe")
    [void](Get-RequiredFile (Join-Path $KitRoot "Include\$kitVersion\km\portcls.h"))
    [void](Get-RequiredFile (Join-Path $KitRoot "Include\$kitVersion\km\wdm.h"))
    [void](Get-RequiredFile (Join-Path $KitRoot "Lib\$kitVersion\km\x64\portcls.lib"))
    $git = Get-Command git.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    [void](Get-Command tar.exe -CommandType Application -ErrorAction Stop)
    $revision = & $git.Path -C $PSScriptRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the SmartQuietCinema revision.' }

    $project = (Get-RequiredFile (Join-Path $PSScriptRoot $projectName)).FullName
    [xml]$projectProperties = Get-Content -LiteralPath $project -Raw
    $catalogTargetsNode = $projectProperties.SelectSingleNode("//*[local-name()='Inf2CatWindowsVersionList']")
    if ($null -eq $catalogTargetsNode -or [string]::IsNullOrWhiteSpace($catalogTargetsNode.InnerText)) {
        throw 'The driver project does not specify Inf2Cat Windows targets.'
    }
    $catalogTargets = $catalogTargetsNode.InnerText
    $sourceNames = $prepareFiles + @($projectName, "$target.inx", 'minipairs.h', 'New-HyperVDriverPackage.ps1')
    $sourceHashes = Get-NamedFileHashes $PSScriptRoot $sourceNames

    $work = Join-Path $parent ('.sqc-package-' + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $work)
    $freshSource = Join-Path $work 'source'
    [void][IO.Directory]::CreateDirectory($freshSource)
    foreach ($name in $prepareFiles) { [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $freshSource $name)) }
    & (Join-Path $freshSource 'Prepare-SysVad.ps1') -SourceCheckout $checkout.FullName | Out-Host
    $sysvad = Join-Path $freshSource 'obj\SysVAD\audio\sysvad'
    $sysvadRevision = [IO.File]::ReadAllText((Get-RequiredFile (Join-Path $sysvad 'Prepared-v6.txt')).FullName)
    $bin = Join-Path $work 'bin'
    $intermediates = Join-Path $work 'obj'
    $buildLog = Join-Path $work 'msbuild.log'
    Invoke-CheckedTool $msbuild.FullName @($project, '/t:Build', "/p:Configuration=$configuration", "/p:Platform=$platform",
        "/p:WindowsTargetPlatformVersion=$kitVersion", "/p:SysVadDirectory=$sysvad", "/p:OutDir=$bin/", "/p:IntDir=$intermediates/",
        '/p:SignMode=Off', '/nologo', '/v:minimal', "/flp:LogFile=$buildLog;Verbosity=Normal")

    $builtPackage = Join-Path $bin 'SmartQuietCinema.VirtualAudio'
    $publish = Join-Path $work 'publish'
    $driver = Join-Path $publish 'Driver'
    $symbols = Join-Path $publish 'Symbols'
    [void][IO.Directory]::CreateDirectory($driver)
    [void][IO.Directory]::CreateDirectory($symbols)
    $null = Get-RequiredFile (Join-Path $builtPackage $catName)
    foreach ($name in $payloadFiles) {
        $file = Get-RequiredFile (Join-Path $builtPackage $name)
        [IO.File]::Copy($file.FullName, (Join-Path $driver $name))
    }
    [IO.File]::Copy((Get-RequiredFile (Join-Path $bin $pdbName)).FullName, (Join-Path $symbols $pdbName))

    $inf = Join-Path $driver $infName
    $sys = Join-Path $driver $sysName
    $cat = Join-Path $driver $catName
    $infText = [IO.File]::ReadAllText($inf)
    $catalogPattern = '(?im)^CatalogFile\s*=\s*' + [regex]::Escape($catName) + '[ \t\r]*$'
    $modelPattern = '(?im)^\[Models\.NTamd64\.10\.0\.\.\.22621\][ \t\r]*\n[^;\r\n]+,\s*' + [regex]::Escape($hardwareId) + '[ \t\r]*$'
    $driverVersion = [regex]::Match($infText, '(?im)^DriverVer[ \t]*=[ \t]*([^\r\n;]+)')
    if ($infText -notmatch $catalogPattern -or $infText -notmatch $modelPattern -or !$driverVersion.Success) {
        throw 'The built INF does not declare the expected catalog, Windows 11 x64 root device, or DriverVer.'
    }
    Invoke-CheckedTool $infVerif.FullName @('/u', '/v', $inf)

    $signatureMode = 'Unsigned'
    if ($certificate) {
        $signArguments = @('sign', '/v', '/fd', 'SHA256', '/s', 'My', '/sha1', $certificate.Thumbprint)
        if ($CertificateStore -eq 'LocalMachine') { $signArguments += '/sm' }
        Invoke-CheckedTool $signTool.FullName ($signArguments + $sys)
    }

    # Generate a fresh catalog from the final INF and the exact SYS that will be published.
    $payloadHashes = Get-NamedFileHashes $driver $payloadFiles
    $catalogArguments = @("/driver:$driver", "/os:$catalogTargets")
    $catalogTimeNode = $projectProperties.SelectSingleNode("//*[local-name()='Inf2CatUseLocalTime']")
    if ($catalogTimeNode -and $catalogTimeNode.InnerText -eq 'true') { $catalogArguments += '/uselocaltime' }
    Invoke-CheckedTool $inf2Cat.FullName $catalogArguments
    $null = Get-RequiredFile $cat

    if ($certificate) {
        Invoke-CheckedTool $signTool.FullName ($signArguments + $cat)
        foreach ($file in @($sys, $cat)) {
            if (!$DeferTrustToGuest) { Invoke-CheckedTool $signTool.FullName @('verify', '/pa', '/v', $file) }
            $signature = Get-AuthenticodeSignature -LiteralPath $file
            if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
                throw "The signed file does not use the selected certificate: $file"
            }
            if ($DeferTrustToGuest -and $signature.Status -notin 'Valid', 'NotTrusted', 'UnknownError') {
                throw "Unexpected Authenticode status $($signature.Status) for signed file: $file"
            }
        }
        if ($DeferTrustToGuest) {
            # A missing trust chain can surface as UnknownError. The guest must recheck both signatures and both catalog members.
            $signatureMode = 'SignedPendingGuestVerification'
        } else {
            foreach ($file in @($inf, $sys)) { Invoke-CheckedTool $signTool.FullName @('verify', '/pa', '/v', '/c', $cat, $file) }
            $signatureMode = 'TestSignedAuthenticodeVerified'
        }
    }

    $finalHashes = Get-NamedFileHashes $driver $driverFiles
    foreach ($name in $payloadFiles) {
        if ($payloadHashes[$name] -ne $finalHashes[$name]) { throw "The catalog input changed after hashing: $name" }
    }
    $packageEntries = @(Get-ChildItem -LiteralPath $driver -Force)
    if ($packageEntries.Count -ne $driverFiles.Count -or @($packageEntries | Where-Object { $_.PSIsContainer -or $_.Name -notin $driverFiles }).Count -ne 0) {
        throw 'The Driver directory contains unexpected files. Only the INF, SYS, and CAT may be published.'
    }
    $endSourceHashes = Get-NamedFileHashes $PSScriptRoot $sourceNames
    foreach ($name in $sourceNames) {
        if ($sourceHashes[$name] -ne $endSourceHashes[$name]) { throw "The build input changed during packaging: $name" }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTime]::UtcNow.ToString('o')
        smartQuietCinemaRevision = $revision
        sysVadRevision = $sysvadRevision
        sysVadPreparation = 'v6'
        build = "$configuration|$platform"
        sdk = $kitVersion
        msbuildVersion = $msbuild.VersionInfo.FileVersion
        infVerifVersion = $infVerif.VersionInfo.FileVersion
        inf2CatVersion = $inf2Cat.VersionInfo.FileVersion
        signToolVersion = $signTool.VersionInfo.FileVersion
        hardwareId = $hardwareId
        minimumWindowsBuild = 22621
        driverVer = $driverVersion.Groups[1].Value.Trim()
        catalogTargets = $catalogTargets.Split(',')
        signingMode = $signatureMode
        certificateThumbprint = if ($certificate) { $certificate.Thumbprint } else { $null }
        sourceSha256 = $sourceHashes
        driverSha256 = $finalHashes
        symbolsSha256 = Get-NamedFileHashes $symbols @($pdbName)
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish 'manifest.json') -Encoding UTF8
    [IO.File]::Copy($buildLog, (Join-Path $publish 'msbuild.log'))
    [IO.Directory]::Move($publish, $output)
    $published = $true
    Write-Output "Package: $output"
    Write-Output "Signing: $signatureMode"
    foreach ($name in $driverFiles) { Write-Output "$name SHA-256 $($finalHashes[$name])" }
} catch {
    [Console]::Error.WriteLine("Package preparation failed. $($_.Exception.Message)")
    if ($work -and [IO.Directory]::Exists($work)) { [Console]::Error.WriteLine("Build diagnostics retained at $work") }
    exit 1
} finally {
    if ($published) {
        # Delete only the unique workspace created beside this output during this run.
        try { [IO.Directory]::Delete($work, $true) }
        catch { Write-Warning "Could not remove the build workspace at $work. $($_.Exception.Message)" }
    }
}
