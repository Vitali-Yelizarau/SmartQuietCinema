#Requires -Version 5.1
<#
.SYNOPSIS
Stages the pinned SysVAD revision and its render-only adaptations for a local WDK build.
.PARAMETER SourceCheckout
A local Git checkout of the official Windows-driver-samples repository at the documented revision.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceCheckout)

$ErrorActionPreference = 'Stop'
$revision = 'd49bb7d72f3651952b2b6e4ad28d14fe2247eb69'
$head = & git -C $SourceCheckout rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $revision) {
    throw "Expected the Microsoft Windows-driver-samples checkout at $revision."
}

$output = Join-Path $PSScriptRoot 'obj\SysVAD'
$staged = Join-Path $output 'audio\sysvad'
$stamp = Join-Path $staged 'Prepared-v2.txt'
if ([IO.File]::Exists($stamp) -and [IO.File]::ReadAllText($stamp) -ceq $revision) {
    Write-Output "Prepared SysVAD source already exists at $staged"
    return
}
if (Test-Path -LiteralPath $output) { throw "An outdated or incomplete staging directory exists: $output. Move it aside and run again." }

[void][IO.Directory]::CreateDirectory($output)
$archive = Join-Path $output 'source.tar'
try {
    & git -C $SourceCheckout archive --format=tar "--output=$archive" $revision audio/sysvad
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the pinned SysVAD tree.' }
    & tar.exe -xf $archive -C $output
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned SysVAD tree.' }

    $encoding = [Text.UTF8Encoding]::new($false)
    $adapterPath = Join-Path $staged 'adapter.cpp'
    $adapter = [IO.File]::ReadAllText($adapterPath)
    $fileSetting = '(?m)^.*L"DoNotCreateDataFiles".*\r?\n'
    if ([regex]::Matches($adapter, $fileSetting).Count -ne 1) { throw 'The upstream render-dump setting did not match.' }
    # Keep the upstream disabled default and remove the registry setting that could enable render PCM dumps.
    $adapter = [regex]::Replace($adapter, $fileSetting, '')

    # StartDevice registers only render filters. Remove the unused capture registration code as well as its call.
    $captureFunctions = '(?ms)^#pragma code_seg\("PAGE"\)\r?\nNTSTATUS[^\S\r\n]*\r?\nInstall(?:Endpoint|All)CaptureFilters\(.*?^\}\r?\n\r?\n'
    $captureCall = '(?m)^    //\r?\n    // Install wave\+topology filters for capture devices\r?\n    //\r?\n    ntStatus = InstallAllCaptureFilters\(DeviceObject, Irp, pAdapterCommon\);\r?\n    IF_FAILED_JUMP\(ntStatus, Exit\);\r?\n\r?\n'
    if ([regex]::Matches($adapter, $captureFunctions).Count -ne 2 -or [regex]::Matches($adapter, $captureCall).Count -ne 1) { throw 'The upstream capture registration did not match.' }
    $adapter = [regex]::Replace($adapter, $captureFunctions, '')
    [IO.File]::WriteAllText($adapterPath, [regex]::Replace($adapter, $captureCall, ''), $encoding)

    $enginePath = Join-Path $staged 'EndpointsCommon\MiniportAudioEngineNode.cpp'
    $engine = [IO.File]::ReadAllText($enginePath)
    $sideband = '(?m)^    if \(IsSidebandDevice\(\) && m_pSidebandDevice->Is(?:Volume|Mute)Supported\(m_DeviceType\)\)\r?\n    \{\r?\n[\s\S]*?^    \}(?:\r?\n    else(?=\r?$))?'
    if ([regex]::Matches($engine, $sideband).Count -ne 8) { throw 'The upstream sideband volume and mute handlers did not match.' }
    $lineBreak = if ($engine.Contains("`r`n")) { "`r`n" } else { "`n" }
    # Match minwavert.h's sideband guards and leave software volume and mute available for the virtual endpoint.
    $engine = [regex]::Replace($engine, $sideband, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return '#if defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)' + $lineBreak + $match.Value + $lineBreak + '#endif'
    })
    [IO.File]::WriteAllText($enginePath, $engine, $encoding)

    $miniportPath = Join-Path $staged 'EndpointsCommon\minwavert.cpp'
    $miniport = [IO.File]::ReadAllText($miniportPath)
    $entry = '    DPF_ENTER(("[CMiniportWaveRT::NewStream]"));'
    if ([regex]::Matches($miniport, [regex]::Escape($entry)).Count -ne 1) { throw 'The upstream stream entry did not match.' }
    $renderOnly = @'
    // This adapter exposes only the system render sink. The audio engine owns loopback.
    if (Capture)
    {
        *OutStream = nullptr;
        return STATUS_NOT_SUPPORTED;
    }
'@
    [IO.File]::WriteAllText($miniportPath, $miniport.Replace($entry, "$entry`n`n$renderOnly"), $encoding)

    $streamPath = Join-Path $staged 'EndpointsCommon\minwavertstream.cpp'
    $stream = [IO.File]::ReadAllText($streamPath)
    $sine = 'm_ToneGenerator.GenerateSine(m_pDmaBuffer + bufferOffset, runWrite);'
    if ([regex]::Matches($stream, [regex]::Escape($sine)).Count -ne 1) { throw 'The upstream capture generator did not match.' }
    [IO.File]::WriteAllText($streamPath, $stream.Replace($sine, 'RtlZeroMemory(m_pDmaBuffer + bufferOffset, runWrite);'), $encoding)

    [IO.File]::WriteAllText($stamp, $revision, $encoding)
    Write-Output "Prepared SysVAD $revision at $staged"
} finally {
    [IO.File]::Delete($archive)
}
