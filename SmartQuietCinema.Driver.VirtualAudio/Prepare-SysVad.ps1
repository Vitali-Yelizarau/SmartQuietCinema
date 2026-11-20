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

function Update-PinnedSource {
    <# .SYNOPSIS
    Applies one exact adaptation to the pinned source and fails if its patch point has drifted.
    #>
    param([string]$Source, [string]$Before, [string]$After)

    $lineBreak = if ($Source.Contains("`r`n")) { "`r`n" } else { "`n" }
    $pattern = $Before -replace '\r?\n', $lineBreak
    if ([regex]::Matches($Source, [regex]::Escape($pattern)).Count -ne 1) {
        throw "Expected one pinned SysVAD patch point: $($Before.Trim())"
    }
    return $Source.Replace($pattern, ($After -replace '\r?\n', $lineBreak))
}

function Invoke-StagedSysVadPatch {
    <# .SYNOPSIS
    Restores the pinned source's line endings and applies one adaptation only inside the generated tree.
    #>
    param([string]$Output, [string]$Patch, [Text.Encoding]$Encoding)

    $expandedPatch = Join-Path $Output 'sysvad-expanded.patch'
    $patchText = [Text.StringBuilder]::new()
    foreach ($line in [IO.File]::ReadLines($Patch)) {
        if ($line -match '^[ +\-](?![+\-]{2} )' -or $line -match '^\{ws:[st]+\}$') {
            $restored = [regex]::Replace($line, '\{ws:([st]+)\}$', [Text.RegularExpressions.MatchEvaluator]{
                param($token)
                return $token.Groups[1].Value.Replace('s', ' ').Replace('t', "`t")
            })
            [void]$patchText.Append($restored).Append("`r`n")
        } else {
            [void]$patchText.Append($line).Append("`n")
        }
    }
    [IO.File]::WriteAllText($expandedPatch, $patchText.ToString(), $Encoding)
    $oldCeiling = [Environment]::GetEnvironmentVariable('GIT_CEILING_DIRECTORIES')
    try {
        $env:GIT_CEILING_DIRECTORIES = [IO.Path]::GetDirectoryName($Output)
        & git -C $Output -c core.whitespace=cr-at-eol apply --whitespace=error -- $expandedPatch
        if ($LASTEXITCODE -ne 0) { throw "Could not apply the SysVAD adaptation: $Patch" }
    } finally {
        [Environment]::SetEnvironmentVariable('GIT_CEILING_DIRECTORIES', $oldCeiling)
        [IO.File]::Delete($expandedPatch)
    }
}

$revision = 'd49bb7d72f3651952b2b6e4ad28d14fe2247eb69'
$head = & git -C $SourceCheckout rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $revision) {
    throw "Expected the Microsoft Windows-driver-samples checkout at $revision."
}

$output = Join-Path $PSScriptRoot 'obj\SysVAD'
$staged = Join-Path $output 'audio\sysvad'
$stamp = Join-Path $staged 'Prepared-v6.txt'
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
    $adapter = [regex]::Replace($adapter, $captureCall, '')

    # Remove the sample's temporary registration of the PnP start thread. WaveRT uses a timer for buffer notifications.
    $resourceDeclarations = '(?m)^    PPORTCLSStreamResourceManager2?[ \t]+pPortClsResMgr2?[ \t]+=[ \t]+NULL;[ \t]*\r?\n'
    $resourceDemo = '(?ms)^\r?\n        //\r?\n        // Test: add and remove current thread as streaming audio resource\.[ \t]*\r?\n.*?(?=^    \}\r?$)'
    if ([regex]::Matches($adapter, $resourceDeclarations).Count -ne 2 -or [regex]::Matches($adapter, $resourceDemo).Count -ne 1) { throw 'The upstream stream-resource demonstration did not match.' }
    $adapter = [regex]::Replace($adapter, $resourceDeclarations, '')
    $adapter = [regex]::Replace($adapter, $resourceDemo, '')

    # Keep optional ETW discovery separate from the endpoint installation result now returned to StartDevice.
    $etwQuery = '(?m)^(        )ntStatus( = unknownWave->QueryInterface \(IID_IPortClsEtwHelper, \(PVOID \*\)&pPortClsEtwHelper\);\r?\n        if \(NT_SUCCESS\()ntStatus(?=\)\))'
    if ([regex]::Matches($adapter, $etwQuery).Count -ne 1) { throw 'The upstream ETW helper query did not match.' }
    $adapter = [regex]::Replace($adapter, $etwQuery, '${1}const NTSTATUS etwStatus${2}etwStatus')
    [IO.File]::WriteAllText($adapterPath, $adapter, $encoding)

    $commonPath = Join-Path $staged 'common.cpp'
    $common = [IO.File]::ReadAllText($commonPath)

    # The adapter instance slot normally belongs to the destructor. Release it here when allocation creates no object.
    $adapterAllocationLog = '        DPF(D_ERROR, ("NewAdapterCommon failed, 0x%x", ntStatus));'
    $common = Update-PinnedSource $common $adapterAllocationLog ("        InterlockedDecrement(&CAdapterCommon::m_AdapterInstances);`n" + $adapterAllocationLog)

    # Check the newly allocated destination before the counted registry-name copy.
    $valueNameAllocation = @'
        pwstrKeyValueName = (PWSTR)ExAllocatePool2(POOL_FLAG_NON_PAGED, kvFullInfo->NameLength + sizeof(WCHAR)*2, MINADAPTER_POOLTAG);
        IF_TRUE_ACTION_JUMP(kvFullInfo == NULL, ntStatus = STATUS_INSUFFICIENT_RESOURCES, loop_exit);
'@
    $common = Update-PinnedSource $common $valueNameAllocation ($valueNameAllocation.Replace('kvFullInfo == NULL', 'pwstrKeyValueName == NULL'))
    $keyNameAllocation = @'
        pwstrKeyName = (PWSTR)ExAllocatePool2(POOL_FLAG_NON_PAGED, kBasicInfo->NameLength + sizeof(WCHAR), MINADAPTER_POOLTAG);
        IF_TRUE_ACTION_JUMP(kBasicInfo == NULL, ntStatus = STATUS_INSUFFICIENT_RESOURCES, loop_exit);
'@
    $common = Update-PinnedSource $common $keyNameAllocation ($keyNameAllocation.Replace('kBasicInfo == NULL', 'pwstrKeyName == NULL'))

    # Enumeration can fail before the next iteration acquires any resources.
    foreach ($name in 'pwstrKeyValueName', 'pwstrKeyName') {
        $freeName = "            ExFreePoolWithTag($name, MINADAPTER_POOLTAG);"
        $common = Update-PinnedSource $common $freeName ($freeName + "`n            $name = NULL;")
    }

    # Transfer handle ownership only on success and close each acquired handle once in loop_exit.
    $openKey = @'
        ntStatus = ZwOpenKey(&hCurrentSourceKey, KEY_READ, &hCurrentSourceKeyAttributes);
        IF_FAILED_ACTION_JUMP(ntStatus, ZwClose(hCurrentSourceKey), loop_exit);
'@
    $common = Update-PinnedSource $common $openKey ($openKey.Replace('ZwClose(hCurrentSourceKey)', 'hCurrentSourceKey = NULL'))
    $createKey = @'
        ntStatus = ZwCreateKey(&hNewDestinationKey, KEY_WRITE, &hNewDestinationKeyAttributes, 0, NULL, REG_OPTION_NON_VOLATILE, &ulDisposition);
        IF_FAILED_ACTION_JUMP(ntStatus, ZwClose(hCurrentSourceKey), loop_exit);
'@
    $common = Update-PinnedSource $common $createKey ($createKey.Replace('ZwClose(hCurrentSourceKey)', 'hNewDestinationKey = NULL'))
    foreach ($handle in 'hCurrentSourceKey', 'hNewDestinationKey') {
        $closeKey = "            ZwClose($handle);"
        $common = Update-PinnedSource $common $closeKey ($closeKey + "`n            $handle = NULL;")
    }

    # A template link is usable only after successful interface registration.
    $templateLink = '        &TemplateSymbolicLinkName);'
    $common = Update-PinnedSource $common $templateLink ($templateLink + "`n    IF_FAILED_JUMP(ntStatus, Exit);")
    [IO.File]::WriteAllText($commonPath, $common, $encoding)

    $enginePath = Join-Path $staged 'EndpointsCommon\MiniportAudioEngineNode.cpp'
    $engine = [IO.File]::ReadAllText($enginePath)
    $sideband = '(?m)^    if \(IsSidebandDevice\(\) && m_pSidebandDevice->Is(?:Volume|Mute)Supported\(m_DeviceType\)\)\r?\n    \{\r?\n[\s\S]*?^    \}(?:\r?\n    else(?=\r?$))?'
    # The pinned revision checks sideband support four times each for volume and mute (channel count, steps, get, and set).
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
    $miniport = Update-PinnedSource $miniport $entry "$entry`n`n$renderOnly"
    [IO.File]::WriteAllText($miniportPath, $miniport, $encoding)

    $streamPath = Join-Path $staged 'EndpointsCommon\minwavertstream.cpp'
    $stream = [IO.File]::ReadAllText($streamPath)
    $sine = 'm_ToneGenerator.GenerateSine(m_pDmaBuffer + bufferOffset, runWrite);'
    if ([regex]::Matches($stream, [regex]::Escape($sine)).Count -ne 1) { throw 'The upstream capture generator did not match.' }
    [IO.File]::WriteAllText($streamPath, $stream.Replace($sine, 'RtlZeroMemory(m_pDmaBuffer + bufferOffset, runWrite);'), $encoding)

    Invoke-StagedSysVadPatch $output (Join-Path $PSScriptRoot 'SysVad-Preflight.patch') $encoding
    Invoke-StagedSysVadPatch $output (Join-Path $PSScriptRoot 'SysVad-AdapterOwnership.patch') $encoding

    [IO.File]::WriteAllText($stamp, $revision, $encoding)
    Write-Output "Prepared SysVAD $revision at $staged"
} finally {
    [IO.File]::Delete($archive)
}
