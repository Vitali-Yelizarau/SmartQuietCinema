# Hyper-V validation of the experimental virtual audio driver

## Validation status

| Status | Evidence or requirement |
| --- | --- |
| Confirmed on the build host | Debug x64 build, INF validation, and unsigned catalog generation for the actual package. SysVAD v6 is prepared from the pinned revision. User-mode mocks cover adapter ownership and shutdown. |
| Prepared for the guest | Package signing and trust checks, checkpoint and dump collection, and the five guest tests below. |
| Not yet validated | Loading under the chosen guest's Code Integrity policy, endpoint creation, PCM delivered through the Windows Audio Engine, real PnP teardown, and Driver Verifier. |
| Known limitations | The driver exposes an experimental render-only endpoint. The probe measures recognizable stereo content and transport integrity. Bit-exact output is outside that probe's checks. |
| Installation blockers | A package accepted by the guest, verified signatures and hashes, a recoverable isolated guest, and explicit authorization for that guest's installation. An unsigned package is for offline review. |

Keep installation and security-policy changes in the designated lab. Use the host only to build the package, manage the exact test VM, or analyze copied evidence.

## Build the offline laboratory bundle

From the repository root, use Windows PowerShell 5.1 and WDK-enabled MSBuild. Obtain the local pinned SysVAD checkout as described in the [driver README](README.md#prepare-and-build). The host needs the .NET SDK, the Windows MAUI workload for the optional app, and access to NuGet runtime packs through a cache or feed. The guest is offline.

```powershell
$bundleScript = '.\SmartQuietCinema.Driver.VirtualAudio\New-HyperVTestBundle.ps1'
$output = Join-Path $env:TEMP ('sqc-lab-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bundleScript -SourceCheckout $upstream -OutputDirectory $output -UnsignedReview
```

Pass `-MSBuildPath` for an explicit WDK-enabled executable or `-KitRoot` for a custom Windows Kits root. Choose a new output directory under an existing, short parent path to stay within Windows PowerShell 5.1 and WDK path limits. The script delegates the fresh SysVAD v6 Debug x64 build, InfVerif, catalog generation and optional signing to `New-HyperVDriverPackage.ps1`. It verifies the native manifest, runs the published probe, reopens the ZIP and checks each member's relative path, length and SHA-256. StampInf assigns a build-specific `DriverVer`, so separate builds can have different hashes.

| ZIP location | Contents |
| --- | --- |
| `Driver/` | Exact INF, SYS and CAT for `ROOT\SmartQuietCinemaVirtualAudio`. |
| `Certificate/` | Selected public X.509 CER in signed mode. |
| `Tools/VirtualAudioProbe/` | Self-contained x64 probe and both production PCM sources. |
| `Application/` | Optional unpackaged MAUI and its app-local dependencies. |
| `AudioTestBench/` | Optional self-contained Offline Audio Test Bench. |
| `Scripts/` | Payload verification, CLI smoke test and read-only guest evidence collection. |
| `Documentation/` | This runbook, driver README, analysis notes and SysVAD license. |
| `manifest.json` | Git provenance, component status, native manifest, and hashes of all payload files. |

Keep the ZIP SHA-256 sidecar, `build.log`, `driver-manifest.json` and the `Symbols/` PDB on the host. The native manifest records SYS and PDB hashes. WinDbg matches the PDB by identifier and age, and the SYS records the PDB filename. The archive has no user A/B samples, debug tools, or private keys.

**Stage A — Driver Laboratory.** The unsigned review bundle is `NotSigned` and `NotInstallable`. The manifest also records whether the checkout was clean. A signed clean build reports `RequiresGuestPrerequisites` until the guest's tools, trust, boot policy and recovery setup have been checked.

**Stage B — Application Laboratory.** A failed app or Test Bench publication leaves Stage A available. The log records the reason and the manifest identifies omitted components. Use `-SkipApplication` when the build host lacks the MAUI Windows workload. A packaged MSIX needs registration, signature trust and any external dependencies staged offline. The bundle uses the app's existing unpackaged configuration with `WindowsAppSDKSelfContained=true`, app-local .NET for `win-x64`, and the Visual Studio x64 CRT from REDIST. If discovery fails, supply the licensed `x64\Microsoft.VC*.CRT` directory via `-VCRuntimeDirectory`. Launch on a clean guest remains **Not yet validated**. The app does not demonstrate a Virtual Audio → Loopback → DSP → Physical Output route. [MAUI Windows publishing](https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-unpackaged-cli?view=net-maui-10.0) · [Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) · [Visual C++ REDIST](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)

### Build with an existing test certificate

Use a clean Git checkout, an existing Code Signing identity in `CurrentUser\My` or `LocalMachine\My` trusted by the signer, and the matching public CER. Keep the private key and PFX on the signer.

```powershell
$thumbprint = Read-Host 'Approved signer thumbprint (40 hexadecimal characters)'
$publicCerPath = Read-Host 'Existing corresponding public CER path'
$signed = Join-Path $env:TEMP ('sqc-signed-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bundleScript -SourceCheckout $upstream -OutputDirectory $signed -CertificateThumbprint $thumbprint -PublicCertificatePath $publicCerPath
```

Add `-CertificateStore LocalMachine` if needed. The driver packager signs SYS, generates a new CAT from the final INF and SYS, signs CAT and verifies both signatures and catalog membership with SignTool. Host Authenticode verification leaves guest trust and kernel Code Integrity to the guest. [Test-signing driver packages](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing-driver-packages)

### External guest tools

Microsoft prohibits redistribution of DevGen. Use the official WDK and SDK under the lab's license. For example, download an approved EWDK ISO on a connected machine and make the official image available to the isolated guest. Verify that the selected kit supplies x64 `DevGen.exe` and `SignTool.exe` and record their full paths. Keep these tools in the kit or its mounted media. Windows supplies PnPUtil. The probe, Test Bench and included MAUI publish carry their own .NET runtime. [DevGen terms and usage](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devgen-command-syntax) · [Official WDK and EWDK media](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk)

### Offline transfer and integrity checks

Use Hyper-V Generation 2 with Windows 11 Home x64 build 22621 or later and **no network adapter**. Use basic VMConnect. If approved for that guest, enable the Hyper-V Guest Service Interface and check `vmicguestinterface` in Windows. Transfer the ZIP and checksum over the integration channel. Keep an independent copy of the expected checksum.

```powershell
$vmId = [guid](Read-Host 'Recorded isolated VM ID')
$vm = Get-VM -Id $vmId -ErrorAction Stop
if ($vm.Generation -ne 2 -or @(Get-VMNetworkAdapter -VM $vm).Count) { throw 'Check the offline Generation 2 guest.' }
Get-VMIntegrationService -VM $vm
$zip = Join-Path $output 'SmartQuietCinema-HyperV-Test.zip'
Copy-VMFile -VM $vm -SourcePath $zip -DestinationPath 'C:\Lab\SmartQuietCinema-HyperV-Test.zip' -FileSource Host -CreateFullPath
Copy-VMFile -VM $vm -SourcePath "$zip.sha256" -DestinationPath 'C:\Lab\SmartQuietCinema-HyperV-Test.zip.sha256' -FileSource Host -CreateFullPath
```

In the guest, compare SHA-256 before extracting to a new directory. Compare the sidecar value with the independent handoff record as well.

```powershell
$zip = 'C:\Lab\SmartQuietCinema-HyperV-Test.zip'
$expected = (Get-Content -LiteralPath "$zip.sha256" -Raw).Trim().Split(' ')[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'ZIP hash mismatch.' }
$package = 'C:\Lab\SmartQuietCinema'
if (Test-Path -LiteralPath $package) { throw 'Choose an unused extraction path.' }
Expand-Archive -LiteralPath $zip -DestinationPath $package
$verify = Join-Path $package 'Scripts\Test-HyperVTestBundle.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -PayloadOnly
if ($LASTEXITCODE -ne 0) { throw 'The offline payload check failed.' }
```

The CLI smoke test exercises the production PCM generators and analyzer without opening live streams. [Copy-VMFile](https://learn.microsoft.com/en-us/powershell/module/hyper-v/copy-vmfile) · [Guest Service Interface](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/manage/manage-hyper-v-integration-services)

## Prepare the designated VM

All settings and PnP actions below are manual steps for a later authorized lab session. Use a disposable Hyper-V Generation 2 VM on a serviced Windows 11 Home x64 build at or above 22621. Record its Hyper-V VM ID and guest computer name. Provide a guest console through basic VMConnect, a vTPM, ample disk space for symbols and dumps, and no host audio redirection. Isolate the VM from production networks and data. Any debugger or HLK network requires another authorized lab pass. Keep the primary guest without a network adapter.

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

- Microsoft preproduction signing uses a separately provisioned guest and validation process with Secure Boot enabled. The driver laboratory steps below describe the self-signed test package produced by this repository's tool. Any external signing process requires a manifest for its final files.
- For a self-signed test package, use a checkpoint and verify the guest recovery key first. When approved, shut down the exact VM. In Hyper-V Manager open **that VM's** **Settings → Security**, verify its recorded VM ID in Hyper-V, and clear **Enable Secure Boot** for that VM. Boot it and verify `Confirm-SecureBootUEFI` returns `False`. Leave host Secure Boot and the guest's Memory Integrity policy alone.
- In the guest, verify the public self-signed certificate's thumbprint against the package manifest and a value obtained through the authorized signing handoff. Import that same public certificate into the guest's Local Computer **Trusted Root Certification Authorities** and **Trusted Publishers**. For a lab CA chain, trust the approved CA root and use the selected publisher certificate for Trusted Publishers. Keep the signing private key outside the guest.

The following commands apply only to the already approved **guest** with the selected public **self-signed** certificate. Run each change manually, then recheck the certificate stores and current boot entry after reboot.

```powershell
$publicCert = Join-Path $package 'Certificate\SmartQuietCinema-Lab.cer'
certutil.exe -dump $publicCert
# Continue only after comparing the public thumbprint with the independent signing record.
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
bcdedit.exe /set '{current}' TESTSIGNING ON
```

Restart the guest and confirm the current boot entry shows `testsigning Yes`. If the boot option is protected or the guest rejects the embedded signature with Memory Integrity active, stop and record the guest policy and Code Integrity events. Take a second checkpoint after guest policy and dump setup, before installing the package. [Loading test-signed code](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option) · [Test certificate installation](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/installing-a-test-certificate-on-a-test-computer) · [Preproduction signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/preproduction-driver-signing-and-install)

## Test 1 — Driver load

In the designated guest, record playback defaults using the bundled probe. Compare the public certificate thumbprint with the independent handoff record. After the approved guest trust procedure, run the prerequisites check before creating the devnode.

```powershell
$driver = Join-Path $package 'Driver'
$inf = Join-Path $driver 'SmartQuietCinemaVirtualAudio.inf'
$probe = Join-Path $package 'Tools\VirtualAudioProbe\SmartQuietCinema.Tools.VirtualAudioProbe.exe'
& $probe defaults
$devgen = Read-Host 'Full path to x64 DevGen.exe in the approved guest kit'
$signtool = Read-Host 'Full path to x64 SignTool.exe in the approved guest kit'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -RequireDriverLab -DevGenPath $devgen -SignToolPath $signtool
if ($LASTEXITCODE -ne 0) { throw 'Stage A guest checks failed.' }
```

Confirm test signing, guest recovery and authorization separately. Check for existing devices and prior package versions before creating a devnode.

```powershell
$hwid = 'ROOT\SmartQuietCinemaVirtualAudio'
pnputil.exe /enum-devices /deviceid $hwid
pnputil.exe /enum-drivers
```

If an existing or disconnected instance matches, record it and resolve that prior attempt before proceeding. Also check for an older published SmartQuietCinema package. Creating the devnode could immediately bind a matching driver already in the store. Use the scoped cleanup procedure for an older package before creating a fresh device. PnPUtil stages a driver and binds matching devices already enumerated by PnP. Use DevGen to create this persistent ROOT devnode. Use the actual instance ID printed by DevGen and check its hardware IDs before installing.

```powershell
& $devgen /add /bus ROOT /hardwareid $hwid
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

## Test 2 — Virtual endpoint

Use the self-contained probe from the verified extraction. Run these commands before and after installation when taking the defaults and endpoint inventory.

```powershell
& $probe self-test
& $probe defaults
& $probe list
```

Verify the defaults match the pre-install snapshot verbatim and select exactly one entry marked `SmartQuietCinema experiment`. Save its opaque MMDevice render ID. Inspect the endpoint's supported/default formats in the Windows Sound control panel. Source advertises stereo PCM16 at 44,100 and 48,000 Hz. Windows shared mode may negotiate another WASAPI representation. Establish that client initialization and stream start work with the first targeted `probe` run below. Keep its actual negotiated format in the run record even when marker validation fails.

## Test 3 — Real PCM

Stop other playback to the experimental endpoint. In the guest, run at both source rates, allowing each run to close cleanly. Use the endpoint ID recorded in Test 2.

```powershell
$endpointId = Read-Host 'Exact experimental render endpoint ID from the probe list'
& $probe probe $endpointId 48000
& $probe probe $endpointId 44100
& $probe defaults
```

The probe opens two independent shared render clients. Source A emits 688/1104 Hz (left/right), and source B emits 1424/1936 Hz. A separate WASAPI Loopback client must retrieve both with correct channel placement in at least four consecutive 125 ms windows, without discontinuities after startup, packet-position gaps, timestamp errors, malformed samples, or the stock 3000 Hz SysVAD marker. Save both exit codes, all marker and transport counts, source and capture rates, channels, encoding, bit depth, and any subtype. Check the longest consecutive run of marker windows and transport counters as the sequence evidence. Phase, resampling and gain may differ in the shared engine.

Repeat at each endpoint mix rate if the guest offers both in the experimental endpoint's **Default Format**. Changing that format is a separate manual guest action. Verify the capture rate from the output. A 44.1 kHz source run with 48 kHz capture only exercises conversion. For broader application coverage, repeat with an independent playback app explicitly routed to the virtual endpoint and verify that app's known PCM through an approved loopback capture. Physical leakage requires a separately monitored physical output. Endpoint listing and successful PCM delivery are separate acceptance checks. [WASAPI Loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) · [Probe marker validation](README.md#marker-validation)

## Test 4 — PnP lifecycle

Proceed after Tests 1–3 pass and the pre-install checkpoint and matching PDB are available. Perform one targeted operation at a time. Recheck binding, PnP status, defaults and reproducible PCM after every restart or reinstall.

1. Close all clients and restart the exact devnode with `pnputil.exe /restart-device $instanceId`. Record observable Stop and Start events. A reset of the endpoint alone does not establish which PnP requests ran.
2. Repeat with an explicitly selected WASAPI client held open beyond the operation. The current probe is bounded to a few seconds, so use a lab client that keeps its handle open until the operator closes it. Confirm invalidation or clean shutdown, then close that handle, reopen the endpoint and repeat Test 3. Repeat during both active playback and pause when available.
3. Test ordinary remove using the scoped cleanup procedure below, then recreate the ROOT devnode and repeat Tests 1–3. Use the newly reported instance ID.
4. For **Surprise Removal**, use the Windows Hardware Lab Kit **DF - PNP Surprise Remove Device Test (Reliability)** with the exact experimental devnode as its only target. It uses the EDT filter to send `IRP_MN_SURPRISE_REMOVAL` followed by `IRP_MN_REMOVE_DEVICE`. Require separate approval for the guest filter/test, the checkpoint and exact target identity. Run with a held-open client when the test setup can preserve that condition. Confirm the IRP sequence from the test log or guest kernel debugger. `DevGen /remove`, `PnPUtil /remove-device` and VM power-off alone do not establish Surprise Removal coverage. Leave it pending if an exact-target test is unavailable.

With an explicitly configured guest kernel-debugging session and the matching PDB, break at `SmartQuietCinemaVirtualAudio!PnpHandler`. At x64 function entry `!irp @rdx` shows the IRP passed as the second argument. Record the current PnP major and minor function, including `0x04` for STOP and `0x17` for SURPRISE_REMOVAL. Inspect the `StartDevice` callback to confirm startup, then repeat timing-sensitive tests without debugger pauses. [PortCls rebalance](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implement-pnp-rebalance-for-portcls-audio-drivers) · [HLK Surprise Remove test](https://learn.microsoft.com/en-us/windows-hardware/test/hlk/testref/decf00f0-0eae-4768-b06b-85e9c0aaf7da) · [WinDbg !irp](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/-irp)

### Scoped cleanup

Close the clients. Confirm that the exact instance you recorded has the experiment's hardware ID. In the guest, remove only that instance.

```powershell
$deviceHardwareIds = (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
if ($hwid -notin $deviceHardwareIds) { throw 'Refusing cleanup for a different adapter.' }
& $devgen /remove $instanceId
if ($LASTEXITCODE -ne 0) { throw 'DevGen did not remove the recorded devnode.' }
pnputil.exe /enum-devices /deviceid $hwid
```

For a device created with another approved enumerator, use `pnputil.exe /remove-device $instanceId` with the same exact hardware-ID check. Verify the experimental endpoint disappears and the playback defaults match the original snapshot. To remove its package, first verify in `pnputil.exe /enum-drivers` that the recorded `oem*.inf` maps to `SmartQuietCinemaVirtualAudio.inf` and the SmartQuietCinema provider. Check the installed driver fields in `pnputil.exe /enum-devices /drivers`, including disconnected devices. Only when there are no remaining bindings, run `pnputil.exe /delete-driver $publishedInf` with the exact manually recorded name. Leave any package that cannot be identified or removed safely. Avoid wildcard, forced, bulk, or subtree operations. [PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)

## Test 5 — Driver Verifier

After the basic guest tests and a separate explicit approval, create another guest checkpoint. Record existing Verifier settings. Use only the driver's exact SYS filename. Flags `0x0002003B` select Special Pool, Force IRQL Checking, Pool Tracking, I/O Verification, Deadlock Detection and DDI Compliance Checking. Confirm the selection and the only verified driver with `/querysettings` before restart.

```powershell
verifier.exe /querysettings
verifier.exe /flags 0x0002003B /driver SmartQuietCinemaVirtualAudio.sys
verifier.exe /bootmode oneboot
verifier.exe /querysettings
```

Restart the guest, inspect `verifier.exe /query`, and repeat the approved lifecycle and PCM cases. On a fault, follow the evidence procedure below before resuming. To end an active Verifier run explicitly, use `verifier.exe /reset` **inside the guest**, reboot and confirm `/querysettings`. For a boot loop use the one-boot policy, guest recovery/Safe Mode, or the known-good checkpoint. [Verifier command reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/verifier) · [DDI Compliance Checking](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/ddi-compliance-checking)

## Optional application and DSP checks

Read Stage B status in `manifest.json`. If present, launch `Application\SmartQuietCinema.Maui.exe` from the full extracted folder as a normal guest user. Record OS build, start-up errors and audio device enumeration. The app carries local .NET, Windows App SDK and CRT files. Its ordinary WASAPI/DSP checks are independent of the experimental system audio route.

Use a supported original or synthetic IEEE Float WAV and an unused output path with the Test Bench.

```powershell
$bench = Join-Path $package 'AudioTestBench\SmartQuietCinema.Tools.AudioTestBench.exe'
$original = Read-Host 'Full path of original or synthetic IEEE Float WAV'
$processed = Read-Host 'Unused output WAV path'
& $bench $original $processed --interval 'sample:0:0.5'
```

## Evidence and recovery

Use the bundled diagnostics after each guest test. Select a new evidence directory outside the extraction.

```powershell
$evidence = Join-Path $env:TEMP ('sqc-evidence-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -EvidenceDirectory $evidence
```

Use `-ProbeResultFile` and `-PnpResultFile` to include selected results. The script records guest and audio service configuration, the exact hardware-ID query, Driver Store identities, SetupAPI, System, Code Integrity and available audio channels. Copy these records through an approved offline evidence channel before restoring the VM.

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
