# Hyper-V validation of the experimental virtual audio driver

## Validation status

| Status | Evidence or requirement |
| --- | --- |
| Confirmed on the build host | Debug x64 build, INF validation, and unsigned catalog generation for the actual package. SysVAD v6 is prepared from the pinned revision. User-mode mocks cover adapter ownership and shutdown. |
| Prepared for the guest | Package signing and trust checks, checkpoint and dump collection, and Stages A through E below. |
| Not yet validated | Loading under the chosen guest's Code Integrity policy, endpoint creation, PCM delivered through the Windows Audio Engine, real PnP teardown, and Driver Verifier. |
| Known limitations | The driver exposes an experimental render-only endpoint. The probe measures recognizable stereo content and transport integrity. Bit-exact output is outside that probe's checks. |
| Installation blockers | A package accepted by the guest, verified signatures and hashes, a recoverable isolated guest, and explicit authorization for that guest's installation. An unsigned package is for offline review. |

Keep installation and security-policy changes in the designated lab. Use the host only to build the package, manage the exact test VM, or analyze copied evidence.

## Build and verify the package

Use a local SysVAD checkout at the revision in the [driver README](README.md#prepare-and-build). Open Windows PowerShell 5.1 with WDK-enabled MSBuild on PATH, or add `-MSBuildPath` with the full WDK-enabled executable path. Use an existing parent outside the repository for the output.

```powershell
$packageScript = '.\SmartQuietCinema.Driver.VirtualAudio\New-HyperVDriverPackage.ps1'
$unsigned = Join-Path $env:TEMP ('sqc-unsigned-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript -SourceCheckout $upstream -OutputDirectory $unsigned
```

The script checks the SDK and WDK files, prepares a new SysVAD v6 tree, builds Debug x64 with SDK `10.0.28000.0`, and checks the stamped INF. It copies only that build's INF and SYS into an empty staging directory, generates its CAT with Inf2Cat, checks the exact file set, and records SHA-256 in `manifest.json`. In unsigned mode this validates the inputs to fresh catalog generation. Signature and catalog-member verification by SignTool run in signed mode. The `Driver` directory contains only the INF, SYS, and CAT. Keep `Symbols` with the matching PDB for WinDbg and retain the manifest separately from the installation directory. Each run publishes to a new directory. StampInf assigns the build's `DriverVer`, which the manifest records. The same source revision can produce different file hashes on separate runs. On failure, the script reports the location of its work directory if it was created. Treat that directory as build evidence.

| Package property | Expected value |
| --- | --- |
| Root hardware ID | `ROOT\SmartQuietCinemaVirtualAudio` |
| Service and binary | `SmartQuietCinemaVirtualAudio` and `SmartQuietCinemaVirtualAudio.sys` |
| Minimum target | Windows 11 x64 build 22621 |
| Package dependencies | Windows inbox `ks.inf` and `wdmaudio.inf`, KMDF 1.15, PortCls |

### Optional signing with an existing certificate

Use an explicitly selected laboratory Code Signing certificate with a private key in `CurrentUser\My` or `LocalMachine\My`. The signing environment must already trust its certificate chain for SignTool Authenticode verification. Use a separately authorized signing environment when the host lacks that trust. Keep private keys and PFX files on the signer. Transfer only the public certificate or chain required by the approved lab policy. The script creates no certificates, installs no trust, and requests no timestamp service.

```powershell
$thumbprint = '<40 hexadecimal characters from the selected signing certificate>'
$signed = Join-Path $env:TEMP ('sqc-signed-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript -SourceCheckout $upstream -OutputDirectory $signed -CertificateThumbprint $thumbprint
```

Add `-CertificateStore LocalMachine` to select the computer's personal store. The tool embeds the SHA-256 signature in the SYS, generates a fresh CAT from the final INF and signed SYS, signs the CAT with the same certificate, then verifies the embedded and catalog signatures and both catalog members using SignTool `verify /pa`. It rejects missing or unsuitable certificates and SignTool trust or verification failures. The manifest records the signer thumbprint and test-signing status. Authenticode verification checks the trust and integrity of the package on that machine. Windows kernel policy makes a separate load decision in the guest. Keep all three installed package files byte-for-byte as verified. [Driver package test signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing-driver-packages) · [SignTool](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/signtool)

## Prepare the designated VM

All settings and PnP actions below are manual steps for a later authorized lab session. Use a disposable Hyper-V Generation 2 VM on a serviced Windows 11 x64 build at or above 22621. Record its Hyper-V VM ID and guest computer name. Provide a guest console through basic VMConnect, a vTPM, ample disk space for symbols and dumps, and no host audio redirection. Isolate the VM from production networks and data. Use an isolated lab network only for explicitly selected debugger or HLK traffic.

On the Hyper-V host, use the recorded VM ID to confirm the target and read its firmware configuration. Make guest firmware changes only for that VM during the separately approved guest setup.

```powershell
$vmId = [guid](Read-Host 'Recorded VM ID of the isolated SmartQuietCinema guest')
$vm = Get-VM -Id $vmId -ErrorAction Stop
$vm | Select-Object Name, Id, Generation, State
Get-VMFirmware -VM $vm | Select-Object SecureBoot, SecureBootTemplate
```

Take a clean powered-off checkpoint before policy changes. Keep a backup or export separate from checkpoints, and preserve the guest's BitLocker recovery key if encryption is enabled. Set **Kernel memory dump** in the guest's **System Properties → Advanced → Startup and Recovery**. Keep a system-managed pagefile on the guest boot volume, enough free disk space and the default `%SystemRoot%\MEMORY.DMP` destination. Clear **Automatically restart** if the console will be monitored. Verify the settings without intentionally crashing the VM. [Hyper-V checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints) · [Windows crash dump settings](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/configure-system-failure-and-recovery-options)

From an elevated **guest** PowerShell, record the build, architecture, Secure Boot state, BCD entry, VBS and Memory Integrity, and audio service readiness.

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, OSArchitecture, BuildNumber
Confirm-SecureBootUEFI
bcdedit.exe /enum '{current}'
Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard |
    Select-Object VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning
Get-Service AudioEndpointBuilder, Audiosrv
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' |
    Select-Object CrashDumpEnabled, DumpFile, AutoReboot
```

Value `2` in `SecurityServicesRunning` indicates Memory Integrity is running. Verify both audio services are running before endpoint tests. Record any service or policy failure and resolve it separately inside the guest before proceeding. [VBS and Memory Integrity state](https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity)

### Guest test signing

- Microsoft preproduction signing uses a separately provisioned guest and validation process with Secure Boot enabled. Stage A below describes the self-signed test package produced by this repository's tool. Any external signing process requires a manifest for its final files.
- For a self-signed test package, use a checkpoint and verify the guest recovery key first. When approved, shut down the exact VM. In Hyper-V Manager open **that VM's** **Settings → Security**, verify its recorded VM ID in Hyper-V, and clear **Enable Secure Boot** for that VM. Boot it and verify `Confirm-SecureBootUEFI` returns `False`. Leave host Secure Boot and the guest's Memory Integrity policy alone.
- In the guest, verify the public self-signed certificate's thumbprint against the package manifest and a value obtained through the authorized signing handoff. Import that same public certificate into the guest's Local Computer **Trusted Root Certification Authorities** and **Trusted Publishers**. For a lab CA chain, trust the approved CA root and use the selected publisher certificate for Trusted Publishers. Keep the signing private key outside the guest.

The following commands apply only to the already approved **guest** with the selected public **self-signed** certificate. Run each change manually, then recheck the certificate stores and current boot entry after reboot.

```powershell
$publicCert = Read-Host 'Full path of the verified public test certificate (.cer)'
certutil.exe -dump $publicCert
# Continue only after comparing the public thumbprint with the independent signing record.
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
bcdedit.exe /set '{current}' TESTSIGNING ON
```

Restart the guest and confirm the current boot entry shows `testsigning Yes`. If the boot option is protected or the guest rejects the embedded signature with Memory Integrity active, stop and record the guest policy and Code Integrity events. Take a second checkpoint after guest policy and dump setup, before installing the package. [Loading test-signed code](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option) · [Test certificate installation](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/installing-a-test-certificate-on-a-test-computer) · [Preproduction signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/preproduction-driver-signing-and-install)

## Stage A — Driver load

In the designated guest, record the current audio defaults using the probe's `defaults` command from Stage B. Use the staged package, its manifest, and the approved signing record. Obtain WDK DevGen and SDK SignTool for x64 through the lab's approved tools. Point `$package` to the copied package root. Check its manifest thumbprint and integrity, then verify each SignTool exit code is zero before proceeding.

```powershell
$package = Read-Host 'Full path of the verified lab package root'
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
$driver = Join-Path $package 'Driver'
$inf = Join-Path $driver 'SmartQuietCinemaVirtualAudio.inf'
$sys = Join-Path $driver 'SmartQuietCinemaVirtualAudio.sys'
$cat = Join-Path $driver 'SmartQuietCinemaVirtualAudio.cat'
if ($manifest.signingMode -ne 'TestSignedAuthenticodeVerified') { throw 'This procedure requires a verified test-signed package.' }
foreach ($name in 'SmartQuietCinemaVirtualAudio.inf','SmartQuietCinemaVirtualAudio.sys','SmartQuietCinemaVirtualAudio.cat') {
    $expected = $manifest.driverSha256.$name
    if ((Get-FileHash -LiteralPath (Join-Path $driver $name) -Algorithm SHA256).Hash -ne $expected) { throw "Hash mismatch for $name." }
}
signtool.exe verify /pa /v $sys
signtool.exe verify /pa /v $cat
signtool.exe verify /pa /v /c $cat $inf
signtool.exe verify /pa /v /c $cat $sys
Get-AuthenticodeSignature -LiteralPath $sys,$cat |
    Select-Object Path, Status, @{Name='Thumbprint';Expression={$_.SignerCertificate.Thumbprint}}
```

Compare the SYS and CAT signer thumbprints with the independent signing record using `Get-AuthenticodeSignature`. Verify the certificate is valid and trusted in that guest. Use the read-only query to check for existing devices before creating a new one.

```powershell
$hwid = 'ROOT\SmartQuietCinemaVirtualAudio'
pnputil.exe /enum-devices /deviceid $hwid
pnputil.exe /enum-drivers
```

If an existing or disconnected instance matches, record it and resolve that prior attempt before proceeding. Also check for an older published SmartQuietCinema package. Creating the devnode could immediately bind a matching driver already in the store. Use the scoped cleanup procedure for an older package before creating a fresh device. PnPUtil stages a driver and binds matching devices already enumerated by PnP. Use DevGen to create this persistent ROOT devnode. Use the actual instance ID printed by DevGen and check its hardware IDs before installing.

```powershell
devgen.exe /add /bus ROOT /hardwareid $hwid
if ($LASTEXITCODE -ne 0) { throw 'DevGen did not create the test devnode.' }
$instanceId = Read-Host 'Exact instance ID reported by DevGen'
$deviceHardwareIds = (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
if ($hwid -notin $deviceHardwareIds) { throw 'The selected devnode is not the experimental adapter.' }
pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) { throw 'PnPUtil reported a package or binding failure.' }
```

Record the exact published `oem*.inf` from PnPUtil. Confirm that Windows bound that package and the experimental service to the same instance. Check the problem code and service state. Preserve new Code Integrity, Kernel-PnP, and Service Control Manager errors from the run, and inspect `%SystemRoot%\INF\setupapi.dev.log` for the exact instance. Successful installation requires the expected binding and started devnode with no problem code.

```powershell
pnputil.exe /enum-devices /instanceid $instanceId /drivers
Get-PnpDevice -InstanceId $instanceId
Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_ProblemCode','DEVPKEY_Device_Service'
sc.exe query SmartQuietCinemaVirtualAudio
```

If creation succeeds and binding fails, keep the instance ID for precise cleanup. Stop the trial if there is an unexpected default-endpoint change, wrong binding, problem code, or security-policy error. [DevGen ROOT device syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devgen-command-syntax) · [PnPUtil command syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax) · [SetupAPI device logs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/setupapi-device-installation-log-entries)

## Stage B — Virtual endpoint

Build the current `SmartQuietCinema.Tools.VirtualAudioProbe` for Windows, and use its deployment with the matching .NET 10 runtime in the guest. Run these commands before and after installation when taking the defaults and endpoint inventory.

```powershell
$probe = Read-Host 'Full path to SmartQuietCinema.Tools.VirtualAudioProbe.dll'
dotnet $probe self-test
dotnet $probe defaults
dotnet $probe list
```

Verify the defaults match the pre-install snapshot verbatim and select exactly one entry marked `SmartQuietCinema experiment`. Save its opaque MMDevice render ID. Inspect the endpoint's supported/default formats in the Windows Sound control panel. Source advertises stereo PCM16 at 44,100 and 48,000 Hz. Windows shared mode may negotiate another WASAPI representation. Establish that client initialization and stream start work with the first targeted `probe` run below. Keep its actual negotiated format in the run record even when marker validation fails.

## Stage C — Real PCM

Stop other playback to the experimental endpoint. In the guest, run at both source rates, allowing each run to close cleanly. Use the endpoint ID recorded in Stage B.

```powershell
$endpointId = Read-Host 'Exact experimental render endpoint ID from the probe list'
dotnet $probe probe $endpointId 48000
dotnet $probe probe $endpointId 44100
dotnet $probe defaults
```

The probe opens two independent shared render clients. Source A emits 688/1104 Hz (left/right), and source B emits 1424/1936 Hz. A separate WASAPI Loopback client must retrieve both with correct channel placement in at least four consecutive 125 ms windows, without discontinuities after startup, packet-position gaps, timestamp errors, malformed samples, or the stock 3000 Hz SysVAD marker. Save both exit codes, all marker and transport counts, source and capture rates, channels, encoding, bit depth, and any subtype. Check the longest consecutive run of marker windows and transport counters as the sequence evidence. Phase, resampling and gain may differ in the shared engine.

Repeat at each endpoint mix rate if the guest offers both in the experimental endpoint's **Default Format**. Changing that format is a separate manual guest action. Verify the capture rate from the output. A 44.1 kHz source run with 48 kHz capture only exercises conversion. For broader application coverage, repeat with an independent playback app explicitly routed to the virtual endpoint and verify that app's known PCM through an approved loopback capture. Physical leakage requires a separately monitored physical output. Endpoint listing and successful PCM delivery are separate acceptance checks. [WASAPI Loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) · [Probe marker validation](README.md#marker-validation)

## Stage D — PnP lifecycle

Proceed after Stages A–C pass and the pre-install checkpoint and matching PDB are available. Perform one targeted operation at a time. Recheck binding, PnP status, defaults and reproducible PCM after every restart or reinstall.

1. Close all clients and restart the exact devnode with `pnputil.exe /restart-device $instanceId`. Record observable Stop and Start events. A reset of the endpoint alone does not establish which PnP requests ran.
2. Repeat with an explicitly selected WASAPI client held open beyond the operation. The current probe is bounded to a few seconds, so use a lab client that keeps its handle open until the operator closes it. Confirm invalidation or clean shutdown, then close that handle, reopen the endpoint and repeat Stage C. Repeat during both active playback and pause when available.
3. Test ordinary remove using the scoped cleanup procedure below, then recreate the ROOT devnode and repeat Stages A–C. Use the newly reported instance ID.
4. For **Surprise Removal**, use the Windows Hardware Lab Kit **DF - PNP Surprise Remove Device Test (Reliability)** with the exact experimental devnode as its only target. It uses the EDT filter to send `IRP_MN_SURPRISE_REMOVAL` followed by `IRP_MN_REMOVE_DEVICE`. Require separate approval for the guest filter/test, the checkpoint and exact target identity. Run with a held-open client when the test setup can preserve that condition. Confirm the IRP sequence from the test log or guest kernel debugger. `DevGen /remove`, `PnPUtil /remove-device` and VM power-off alone do not establish Surprise Removal coverage. Leave it pending if an exact-target test is unavailable.

With an explicitly configured guest kernel-debugging session and the matching PDB, break at `SmartQuietCinemaVirtualAudio!PnpHandler`. At x64 function entry `!irp @rdx` shows the IRP passed as the second argument. Record the current PnP major and minor function, including `0x04` for STOP and `0x17` for SURPRISE_REMOVAL. Inspect the `StartDevice` callback to confirm startup, then repeat timing-sensitive tests without debugger pauses. [PortCls rebalance](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implement-pnp-rebalance-for-portcls-audio-drivers) · [HLK Surprise Remove test](https://learn.microsoft.com/en-us/windows-hardware/test/hlk/testref/decf00f0-0eae-4768-b06b-85e9c0aaf7da) · [WinDbg !irp](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/-irp)

### Scoped cleanup

Close the clients. Confirm that the exact instance you recorded has the experiment's hardware ID. In the guest, remove only that instance.

```powershell
$deviceHardwareIds = (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
if ($hwid -notin $deviceHardwareIds) { throw 'Refusing cleanup for a different adapter.' }
devgen.exe /remove $instanceId
if ($LASTEXITCODE -ne 0) { throw 'DevGen did not remove the recorded devnode.' }
pnputil.exe /enum-devices /deviceid $hwid
```

For a device created with another approved enumerator, use `pnputil.exe /remove-device $instanceId` with the same exact hardware-ID check. Verify the experimental endpoint disappears and the playback defaults match the original snapshot. To remove its package, first verify in `pnputil.exe /enum-drivers` that the recorded `oem*.inf` maps to `SmartQuietCinemaVirtualAudio.inf` and the SmartQuietCinema provider. Check the installed driver fields in `pnputil.exe /enum-devices /drivers`, including disconnected devices. Only when there are no remaining bindings, run `pnputil.exe /delete-driver $publishedInf` with the exact manually recorded name. Leave any package that cannot be identified or removed safely. Avoid wildcard, forced, bulk, or subtree operations. [PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)

## Stage E — Driver Verifier

After the basic guest tests and a separate explicit approval, create another guest checkpoint. Record existing Verifier settings. Use only the driver's exact SYS filename. Flags `0x0002003B` select Special Pool, Force IRQL Checking, Pool Tracking, I/O Verification, Deadlock Detection and DDI Compliance Checking. Confirm the selection and the only verified driver with `/querysettings` before restart.

```powershell
verifier.exe /querysettings
verifier.exe /flags 0x0002003B /driver SmartQuietCinemaVirtualAudio.sys
verifier.exe /bootmode oneboot
verifier.exe /querysettings
```

Restart the guest, inspect `verifier.exe /query`, and repeat the approved lifecycle and PCM cases. On a fault, follow the evidence procedure below before resuming. To end an active Verifier run explicitly, use `verifier.exe /reset` **inside the guest**, reboot and confirm `/querysettings`. For a boot loop use the one-boot policy, guest recovery/Safe Mode, or the known-good checkpoint. [Verifier command reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/verifier) · [DDI Compliance Checking](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/ddi-compliance-checking)

## Evidence and recovery

Preserve the package manifest, exact SYS/PDB pair, test timestamps, ID and binding, probe output, Verifier settings, OS and CI settings, and debugger or HLK evidence. Copy these and any dumps to approved lab storage outside the guest disk before restoring a checkpoint. Export the System and Code Integrity logs, and retain SetupAPI logs and, when available, Kernel-PnP configuration events. The following commands write only to a new evidence directory selected inside the guest.

```powershell
$evidence = Join-Path $env:TEMP ('sqc-evidence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence
wevtutil.exe epl System (Join-Path $evidence 'System.evtx')
wevtutil.exe epl 'Microsoft-Windows-CodeIntegrity/Operational' (Join-Path $evidence 'CodeIntegrity.evtx')
Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'INF\setupapi.dev.log') -Destination $evidence
Get-ChildItem -LiteralPath $env:SystemRoot -Filter MEMORY.DMP
```

After a BSOD, record the stop code, wait for the dump to complete when possible, copy the dump or recover it from a clone of the stopped guest disk, then restore the clean checkpoint. Kernel dumps can contain sensitive guest data. Analyze a copy in WinDbg with the exact private PDB and Microsoft symbols. Save the output of `!analyze -v`, `lmvm SmartQuietCinemaVirtualAudio`, and, during a Verifier test, `!verifier`.

```text
.symfix
.sympath+ <full path to the matching Symbols directory>
.reload /f
!analyze -v
lmvm SmartQuietCinemaVirtualAudio
!verifier
```

Record passed, failed and pending stages separately before any further rollout. Guest-only test signing, certificate trust and any firmware changes can be rolled back by restoring the clean pre-policy checkpoint. Confirm the recorded VM ID before restoring it. [Code Integrity events](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/code-integrity-event-log-messages) · [Analyze a kernel dump in WinDbg](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/analyzing-a-kernel-mode-dump-file-with-windbg)
