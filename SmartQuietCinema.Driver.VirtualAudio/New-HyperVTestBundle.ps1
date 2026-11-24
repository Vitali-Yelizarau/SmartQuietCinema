#Requires -Version 5.1
<#
.SYNOPSIS
Builds a verified SmartQuietCinema ZIP for offline virtual machine testing.
.PARAMETER SourceCheckout
Local checkout of the SysVAD revision used by the driver packager.
.PARAMETER OutputDirectory
A new directory under an existing parent for the ZIP, checksum, build log, and host-only symbols.
.PARAMETER UnsignedReview
Creates an inspection archive whose driver is marked NotSigned and NotInstallable.
.PARAMETER CertificateThumbprint
Existing Code Signing identity selected for the driver packager.
.PARAMETER PublicCertificatePath
The corresponding public CER file. PKCS#12 and private keys are rejected.
.PARAMETER VMware
Uses the VMware Workstation Pro target and archive name.
.PARAMETER DeferTrustToGuest
Publishes a signed VMware transfer ZIP that requires guest trust and catalog verification before use.
.PARAMETER VCRuntimeDirectory
Optional Visual Studio x64 Microsoft.VC*.CRT REDIST directory for unpackaged MAUI.
#>
[CmdletBinding(DefaultParameterSetName = 'Review')]
param(
    [Parameter(Mandatory)][string]$SourceCheckout,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Review')][switch]$UnsignedReview,
    [Parameter(Mandatory, ParameterSetName = 'Signed')][ValidatePattern('\A[0-9A-Fa-f]{40}\z')][string]$CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Signed')][string]$PublicCertificatePath,
    [ValidateSet('CurrentUser', 'LocalMachine')][string]$CertificateStore = 'CurrentUser',
    [string]$MSBuildPath,
    [string]$KitRoot,
    [string]$VCRuntimeDirectory,
    [switch]$SkipApplication,
    [switch]$VMware,
    [Parameter(ParameterSetName = 'Signed')][switch]$DeferTrustToGuest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-BundleCommand {
    <# .SYNOPSIS
    Runs one build or smoke-test step and turns an unexpected native exit code into a packaging failure.
    #>
    param([string]$Program, [string[]]$Arguments, [int]$ExpectedExit = 0)

    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "$([IO.Path]::GetFileName($Program)) exited with $LASTEXITCODE (expected $ExpectedExit)." }
}

function Assert-FileSet {
    <# .SYNOPSIS
    Requires a nonempty local copy of each file needed to run an offline component.
    #>
    param([string]$Directory, [string[]]$Names)

    foreach ($name in $Names) {
        $file = Get-Item -LiteralPath (Join-Path $Directory $name) -ErrorAction SilentlyContinue
        if (!$file -or $file.PSIsContainer -or !$file.Length) { throw "Missing required file $name in $Directory." }
    }
}

function Publish-OfflineProject {
    <# .SYNOPSIS
    Publishes one x64 application and checks that the app-local .NET runtime is actually present.
    #>
    param([string]$Name, [string]$Destination, [string[]]$Options, [string[]]$Dependencies)

    $project = Join-Path $repo "$Name\$Name.csproj"
    # A fresh artifacts tree prevents reuse of assemblies carrying CodeView paths from a developer build.
    Invoke-BundleCommand 'dotnet.exe' (@('publish', $project, '-c', 'Release', '--self-contained', 'true', '-o', $Destination,
        '--artifacts-path', (Join-Path $work 'managed'), '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PathMap=$repo=/_/", '--nologo') + $Options)
    Assert-FileSet $Destination (@("$Name.exe", "$Name.dll", "$Name.runtimeconfig.json", "$Name.deps.json", 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll') + $Dependencies)
    $configuration = Get-Content -LiteralPath (Join-Path $Destination "$Name.runtimeconfig.json") -Raw | ConvertFrom-Json
    if ('Microsoft.NETCore.App' -notin @($configuration.runtimeOptions.includedFrameworks.name)) {
        throw "$Name does not declare its included .NET runtime."
    }
}

function Find-VCRuntimeDirectory {
    <# .SYNOPSIS
    Finds an installed Visual Studio x64 redistributable CRT folder for app-local deployment.
    #>
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (![IO.File]::Exists($vswhere)) { return }
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$vsRoot) { return }
    $redist = Join-Path $vsRoot 'VC\Redist\MSVC'
    if (![IO.Directory]::Exists($redist)) { return }
    Get-ChildItem -LiteralPath $redist -Directory | Where-Object Name -Match '^\d+(\.\d+)+$' |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object {
            $arch = Join-Path $_.FullName 'x64'
            if ([IO.Directory]::Exists($arch)) { Get-ChildItem -LiteralPath $arch -Directory -Filter 'Microsoft.VC*.CRT' }
        } | Select-Object -First 1 -ExpandProperty FullName
}

function Add-ApplicationRuntime {
    <# .SYNOPSIS
    Supplies the installed x64 app-local Visual C++ runtime alongside the Windows App SDK files.
    #>
    param([string]$Source, [string]$Destination)

    if (!$Source) { $Source = Find-VCRuntimeDirectory }
    if (!$Source) { throw 'The Visual Studio x64 CRT REDIST folder is unavailable. Supply -VCRuntimeDirectory.' }
    $crt = Get-Item -LiteralPath $Source
    if ($crt.Name -notmatch '^Microsoft\.VC\d+\.CRT$' -or $crt.Parent.Name -ne 'x64') {
        throw 'Use the official Visual Studio x64 Microsoft.VC*.CRT REDIST directory.'
    }
    Assert-FileSet $crt.FullName @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
    foreach ($dll in Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll') {
        $destinationPath = Join-Path $Destination $dll.Name
        if (![IO.File]::Exists($destinationPath)) { [IO.File]::Copy($dll.FullName, $destinationPath) }
    }
}

function Get-PayloadFileRecords {
    <# .SYNOPSIS
    Inventories regular files with safe relative names and rejects key material and debug symbols.
    #>
    param([string]$Directory)

    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName) {
        $path = $file.FullName.Substring($Directory.Length + 1).Replace('\', '/')
        if ($path -notmatch '\A[^/:\\]+(?:/[^/:\\]+)*\z' -or $path -match '(?:^|/)\.{1,2}(?:/|$)' -or
            $file.Extension -in '.pfx', '.p12', '.pvk', '.pem', '.key', '.snk', '.pdb' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected or unsafe ZIP payload file: $path"
        }
        [ordered]@{ path = $path; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
}

function Confirm-PackageZip {
    <# .SYNOPSIS
    Reopens the finished ZIP and verifies every entry name, length, and streamed digest against the staged files.
    #>
    param([string]$ArchivePath, [object[]]$Expected)

    $remaining = @{}
    foreach ($file in $Expected) { $remaining.Add($file.path, $file) }
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (!$remaining.ContainsKey($name) -or $remaining[$name].path -cne $name -or $remaining[$name].bytes -ne $entry.Length) {
                throw "Unexpected ZIP entry: $name"
            }
            $stream = $entry.Open()
            try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose() }
            if ($digest -ne $remaining[$name].sha256) { throw "ZIP entry hash mismatch: $name" }
            $remaining.Remove($name)
        }
        if ($remaining.Count) { throw 'The ZIP is missing staged files.' }
    } finally {
        $sha.Dispose()
        $zip.Dispose()
    }
}

$work = $null
$publicCertificate = $null
$transcribing = $false
$published = $false
try {
    if ($DeferTrustToGuest -and !$VMware) { throw '-DeferTrustToGuest requires -VMware.' }
    $repo = Split-Path $PSScriptRoot -Parent
    $output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw "The output path already exists: $output" }
    $parent = [IO.Path]::GetDirectoryName($output)
    if (![IO.Directory]::Exists($parent)) { throw "The output parent directory does not exist: $parent" }
    $head = & git.exe -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40,64}$') { throw 'Could not read the repository HEAD.' }
    $dirty = (& git.exe -C $repo status --porcelain=v1 --untracked-files=normal) -join [char]10
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the repository state.' }
    if ($dirty -and $CertificateThumbprint) { throw 'Build a signed bundle from a clean Git checkout.' }

    if ($CertificateThumbprint) {
        $certificateFile = Get-Item -LiteralPath $PublicCertificatePath -ErrorAction SilentlyContinue
        if (!$certificateFile -or $certificateFile.Extension -ne '.cer') { throw 'Supply the matching public CER certificate.' }
        if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($certificateFile.FullName) -ne 'Cert') {
            throw 'Only an X.509 public certificate is permitted.'
        }
        $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFile.FullName)
        if ($publicCertificate.HasPrivateKey -or $publicCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw 'The public certificate does not match the selected signing thumbprint.'
        }
    } elseif (!$UnsignedReview) { throw 'Select -UnsignedReview or supply an existing signing identity.' }

    $work = Join-Path $parent ('.sqc-lab-' + [guid]::NewGuid().ToString('N').Substring(0, 16))
    [void](New-Item -ItemType Directory -Path $work)
    $publish = Join-Path $work 'output'
    $payload = Join-Path $work 'payload'
    foreach ($directory in $publish, $payload) { [void][IO.Directory]::CreateDirectory($directory) }
    Start-Transcript -LiteralPath (Join-Path $publish 'build.log') | Out-Null
    $transcribing = $true
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $driverOutput = Join-Path $work 'native'
    $driverArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'New-HyperVDriverPackage.ps1'),
        '-SourceCheckout', $SourceCheckout, '-OutputDirectory', $driverOutput)
    if ($MSBuildPath) { $driverArgs += @('-MSBuildPath', $MSBuildPath) }
    if ($KitRoot) { $driverArgs += @('-KitRoot', $KitRoot) }
    if ($CertificateThumbprint) { $driverArgs += @('-CertificateThumbprint', $CertificateThumbprint, '-CertificateStore', $CertificateStore) }
    if ($DeferTrustToGuest) { $driverArgs += '-DeferTrustToGuest' }
    Write-Output '[Driver] Building and checking the current package.'
    Invoke-BundleCommand $ps51 $driverArgs
    $driverManifestPath = Join-Path $driverOutput 'manifest.json'
    $driverManifest = Get-Content -LiteralPath $driverManifestPath -Raw | ConvertFrom-Json
    if ($driverManifest.smartQuietCinemaRevision -ne $head) { throw 'The driver packager built a different Git revision.' }
    $expectedMode = if ($DeferTrustToGuest) { 'SignedPendingGuestVerification' } elseif ($CertificateThumbprint) { 'TestSignedAuthenticodeVerified' } else { 'Unsigned' }
    if ($driverManifest.signingMode -ne $expectedMode -or $driverManifest.certificateThumbprint -ne $(if ($publicCertificate) { $publicCertificate.Thumbprint })) {
        throw 'The driver packager returned an unexpected signing result.'
    }

    $driver = Join-Path $payload 'Driver'
    [void][IO.Directory]::CreateDirectory($driver)
    $driverFiles = @('SmartQuietCinemaVirtualAudio.inf', 'SmartQuietCinemaVirtualAudio.sys', 'SmartQuietCinemaVirtualAudio.cat')
    $nativeDriver = Join-Path $driverOutput 'Driver'
    Assert-FileSet $nativeDriver $driverFiles
    if (@(Get-ChildItem -LiteralPath $nativeDriver -Force).Count -ne $driverFiles.Count) { throw 'Unexpected native driver package contents.' }
    foreach ($name in $driverFiles) {
        $file = Join-Path $nativeDriver $name
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $driverManifest.driverSha256.$name) { throw "Driver manifest mismatch: $name" }
        [IO.File]::Copy($file, (Join-Path $driver $name))
    }
    [IO.Directory]::Move((Join-Path $driverOutput 'Symbols'), (Join-Path $publish 'Symbols'))
    [IO.File]::Copy($driverManifestPath, (Join-Path $publish 'driver-manifest.json'))
    [IO.File]::Copy((Join-Path $driverOutput 'msbuild.log'), (Join-Path $publish 'msbuild.log'))
    if ($publicCertificate) {
        $certDirectory = Join-Path $payload 'Certificate'
        [void][IO.Directory]::CreateDirectory($certDirectory)
        [IO.File]::WriteAllBytes((Join-Path $certDirectory 'SmartQuietCinema-Lab.cer'), $publicCertificate.RawData)
    }

    Write-Output '[Probe] Publishing the x64 WASAPI probe and both production PCM sources.'
    $probe = Join-Path $payload 'Tools\VirtualAudioProbe'
    $probeName = 'SmartQuietCinema.Tools.VirtualAudioProbe'
    Publish-OfflineProject $probeName $probe @('-r', 'win-x64') @('NAudio.Core.dll', 'NAudio.Wasapi.dll')

    $benchStatus = 'Unavailable'
    try {
        Write-Output '[Audio Test Bench] Publishing the optional offline DSP tool.'
        $bench = Join-Path $work 'bench'
        $benchName = 'SmartQuietCinema.Tools.AudioTestBench'
        Publish-OfflineProject $benchName $bench @('-r', 'win-x64') @('NAudio.Core.dll', 'SmartQuietCinema.Core.dll')
        Invoke-BundleCommand (Join-Path $bench "$benchName.exe") @() 2
        [IO.Directory]::Move($bench, (Join-Path $payload 'AudioTestBench'))
        $benchStatus = 'Published'
    } catch { Write-Warning "Audio Test Bench omitted. $($_.Exception.Message)" }

    $appStatus = 'Skipped'
    if (!$SkipApplication) {
        $appStatus = 'Unavailable'
        try {
            Write-Output '[Application] Publishing the optional unpackaged Windows app.'
            $app = Join-Path $work 'app'
            Publish-OfflineProject 'SmartQuietCinema.Maui' $app @('-f', 'net10.0-windows10.0.19041.0', '-p:RuntimeIdentifierOverride=win-x64',
                '-p:WindowsPackageType=None', '-p:WindowsAppSDKSelfContained=true') @('Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll')
            Add-ApplicationRuntime $VCRuntimeDirectory $app
            [IO.Directory]::Move($app, (Join-Path $payload 'Application'))
            $appStatus = 'PublishedGuestLaunchUnverified'
        } catch { Write-Warning "MAUI omitted. $($_.Exception.Message)" }
    }
    $scripts = Join-Path $payload 'Scripts'
    $docs = Join-Path $payload 'Documentation'
    foreach ($directory in $scripts, $docs) { [void][IO.Directory]::CreateDirectory($directory) }
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Test-HyperVTestBundle.ps1'), (Join-Path $scripts 'Test-HyperVTestBundle.ps1'))
    foreach ($document in 'HYPER-V-VALIDATION.md', 'README.md', 'STATIC-ANALYSIS.md', 'LICENSE.SysVAD.txt') {
        [IO.File]::Copy((Join-Path $PSScriptRoot $document), (Join-Path $docs $document))
    }
    $currentHead = & git.exe -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $currentHead -ne $head) { throw 'The Git revision changed during packaging.' }
    $currentDirty = (& git.exe -C $repo status --porcelain=v1 --untracked-files=normal) -join [char]10
    if ($LASTEXITCODE -ne 0 -or $currentDirty -cne $dirty) { throw 'The Git working tree state changed during packaging.' }

    $manifest = [ordered]@{
        schemaVersion = 1
        revision = $head
        workingTreeClean = !$dirty
        createdUtc = [DateTime]::UtcNow.ToString('o')
        target = if ($VMware) { 'Windows 11 x64 (build 22621 or later), VMware Workstation Pro' } else { 'Windows 11 Home x64 (build 22621 or later), Hyper-V Generation 2' }
        stageA = [ordered]@{
            signature = if ($DeferTrustToGuest) { 'SignedPendingGuestVerification' } elseif ($publicCertificate) { 'TestSignedAuthenticodeVerifiedOnHost' } else { 'NotSigned' }
            installability = if ($DeferTrustToGuest) { 'NotInstallableUntilGuestVerification' } elseif ($publicCertificate) { 'RequiresGuestPrerequisites' } else { 'NotInstallable' }
            probe = 'Tools/VirtualAudioProbe/SmartQuietCinema.Tools.VirtualAudioProbe.exe'
            signalGenerator = 'VirtualAudioProbe probe <endpoint-id> [44100|48000]'
            devGen = 'RequiredSeparately'
            signTool = 'RequiredSeparatelyForGuestSignatureVerification'
            publicCertificate = if ($publicCertificate) { 'Certificate/SmartQuietCinema-Lab.cer' } else { $null }
        }
        stageB = [ordered]@{ application = $appStatus; audioTestBench = $benchStatus; guestValidation = 'NotYetValidated' }
        driver = $driverManifest
        files = @(Get-PayloadFileRecords $payload)
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding UTF8
    Invoke-BundleCommand $ps51 @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scripts 'Test-HyperVTestBundle.ps1'), '-PayloadOnly')

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipName = if ($DeferTrustToGuest) { 'SmartQuietCinema-VMware-Test-GuestVerification.zip' }
    elseif ($VMware -and $publicCertificate) { 'SmartQuietCinema-VMware-Test-Signed.zip' }
    elseif ($VMware) { 'SmartQuietCinema-VMware-Test-Unsigned.zip' }
    else { 'SmartQuietCinema-HyperV-Test.zip' }
    $zipPath = Join-Path $publish $zipName
    $manifestFile = Get-Item -LiteralPath (Join-Path $payload 'manifest.json')
    $archiveRecords = $manifest.files + @(@{ path = 'manifest.json'; bytes = $manifestFile.Length; sha256 = (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash })
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $archiveRecords) {
            $source = Join-Path $payload $file.path.Replace('/', '\')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, $file.path, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    Confirm-PackageZip $zipPath $archiveRecords
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$zipHash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding Ascii
    Write-Output "Verified archive SHA-256 $zipHash"
    Stop-Transcript | Out-Null
    $transcribing = $false
    [IO.Directory]::Move($publish, $output)
    $published = $true
    Write-Output "Archive: $(Join-Path $output $zipName)"
    Write-Output "Stage A: $($manifest.stageA.signature), $($manifest.stageA.installability). Stage B: application $appStatus, Audio Test Bench $benchStatus."
} catch {
    Write-Host "Bundle preparation failed. $($_.Exception.Message)"
    if ($work -and [IO.Directory]::Exists($work)) { Write-Host "Build diagnostics retained at $work" }
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($publicCertificate) { $publicCertificate.Dispose() }
    if ($published -and [IO.Path]::GetDirectoryName($work) -eq $parent -and [IO.Path]::GetFileName($work) -match '^\.sqc-lab-[a-f0-9]{16}$') {
        try { [IO.Directory]::Delete($work, $true) }
        catch { Write-Warning "Could not remove the bundle workspace at $work. $($_.Exception.Message)" }
    }
}
