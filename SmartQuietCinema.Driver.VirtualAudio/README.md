# Virtual Audio PCM Proof of Concept

This experiment tests whether the Windows Audio Engine provides the real shared render mix through standard WASAPI Loopback on a SmartQuietCinema render-only WaveRT endpoint. The driver and probe appear in the solution's `Experimental` folder.

## Source and topology

The native project adapts Microsoft's [SysVAD at revision d49bb7d72f3651952b2b6e4ad28d14fe2247eb69](https://github.com/microsoft/Windows-driver-samples/tree/d49bb7d72f3651952b2b6e4ad28d14fe2247eb69/audio/sysvad). The upstream Microsoft Public License is reproduced in [LICENSE.SysVAD.txt](LICENSE.SysVAD.txt). The preparation step archives tracked source from that revision and keeps upstream notices in the generated files.

- `SmartQuietCinema.VirtualAudio.vcxproj` builds the pinned PortCls/KMDF adapter, topology, WaveRT miniport and stream code. The retained WaveRT implementation allocates and maps the engine's cyclic buffer and advances position and notifications from the sample's software clock.
- `minipairs.h` adapts the speaker topology and two-pin Render3 layout. The one system render sink goes through the DAC topology node to the bridge. One kernel system pin instance is exposed for the Windows engine to mix shared clients. There are no capture, KS loopback, offload, sideband, or APO registrations.
- `Prepare-SysVad.ps1` stages source in the ignored `obj/SysVAD` directory. Its adaptations remove capture filter registration, reject capture stream creation, replace the upstream capture sine write with silence, and remove the registry setting that could enable render dumps. Sideband volume and mute use the same feature guards as the upstream miniport header. The render sink consumes the buffer without sending it to hardware.
- `SmartQuietCinemaVirtualAudio.inx` defines the package and explicit-render endpoint. Hardware ID is `ROOT\SmartQuietCinemaVirtualAudio`, service is `SmartQuietCinemaVirtualAudio`, and the device description is `SmartQuietCinema Virtual Audio (Experimental)`. It sets `PKEY_AudioDevice_NeverSetAsDefaultEndpoint` for both render roles. Confirm the actual default IDs before and after a laboratory installation and test.
- `SmartQuietCinema.Tools.VirtualAudioProbe` is an independent .NET 10 console project using `NAudio.Wasapi 3.3.0`. It opens one capture and two shared render clients using a caller-supplied MMDevice render ID. Before opening a stream, it requires an active render device and the exact experimental hardware ID in its PnP ancestry. A friendly name, a missing identity, and a physical endpoint are insufficient.

The miniport advertises stereo PCM16 with a 4-byte frame at 44,100 and 48,000 Hz, with 48,000 Hz as the proposed default. The x64 experimental INF keeps the upstream Windows 11 build 22621 floor. Windows 10, ARM64, multichannel, compressed audio, hardware offload, and a KS capture or loopback pin are outside this package. The probe requests stereo float32 at either source rate. It reads the recorder's actual mix format and accepts stereo PCM16, PCM24, PCM32, or float32, including matching extensible subtypes. The source rate printed by the probe is distinct from the capture rate. A 44.1 kHz source with a 48 kHz capture exercises shared conversion, not a 44.1 kHz endpoint mix.

Without a driver loopback pin, [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) that WASAPI can obtain a copy of the engine mix. Whether this topology delivers that PCM on the selected Windows build remains a laboratory result. A successful driver build, endpoint listing, or a moving output meter is insufficient evidence.

## Prepare and build

Use a Windows host with the .NET 10 SDK or a compatible later SDK, Git, Windows PowerShell 5.1, and `tar.exe`. The driver additionally requires Visual Studio C++ tools and a matching SDK and [WDK](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk) with the `WindowsKernelModeDriver10.0` MSBuild toolset and InfVerif. A partially cloned source checkout may require Git access to read deferred objects. The driver maps to x64 and is excluded from the default Debug and Release solution builds. Build it explicitly after staging SysVAD. Driver build steps do not deploy or sign the driver.

From the repository root:

```powershell
$revision = 'd49bb7d72f3651952b2b6e4ad28d14fe2247eb69'
$upstream = Join-Path $env:TEMP ('sqc-sysvad-' + [guid]::NewGuid().ToString('N'))
git clone --depth 1 --filter=blob:none --sparse --revision=$revision https://github.com/microsoft/Windows-driver-samples.git $upstream
git -C $upstream sparse-checkout set audio/sysvad
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\SmartQuietCinema.Driver.VirtualAudio\Prepare-SysVad.ps1 -SourceCheckout $upstream
# Run the native build from the matching WDK developer shell.
MSBuild.exe .\SmartQuietCinema.Driver.VirtualAudio\SmartQuietCinema.VirtualAudio.vcxproj /t:Build /p:Configuration=Debug /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.28000.0
dotnet build .\SmartQuietCinema.Tools.VirtualAudioProbe\SmartQuietCinema.Tools.VirtualAudioProbe.csproj
$probe = '.\SmartQuietCinema.Tools.VirtualAudioProbe\bin\Debug\net10.0-windows10.0.19041.0\SmartQuietCinema.Tools.VirtualAudioProbe.dll'
dotnet $probe self-test
dotnet $probe list
dotnet $probe defaults
```

Stop on any failed command. Move an incomplete or older generated staging directory aside before preparing SysVAD again. The script applies `SysVad-Preflight.patch` after the existing transformations and writes `Prepared-v5.txt` only after every adaptation succeeds. An older stamp is insufficient for this build. Keep the pinned upstream checkout for reproducibility. Run [InfVerif](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/infverif) on the generated INF.

The adapter omits the SysVAD demonstration that temporarily registers the current PnP thread and immediately removes it. The render stream uses a timer for WaveRT notifications and does not own a streaming thread or interrupt. Endpoint registration and ETW discovery are retained. The WDK annotates `ResourceSet` as non-null for both resource manager methods. Microsoft documents `NULL` as the required reserved value. See [AddStreamResource](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nf-portcls-iportclsstreamresourcemanager-addstreamresource) and [AddStreamResource2](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nf-portcls-iportclsstreamresourcemanager2-addstreamresource2).

### Visual Studio integration and the WDK payload

Visual Studio can discover `WindowsKernelModeDriver10.0` from its WDK extension and still lack the kit files. The extension version identifies the IDE integration. Check the actual WDK content root and build folder in the MSBuild diagnostic log. For the selected SDK build, the kit must supply `Include/<version>/km/portcls.h`, `Include/<version>/km/wdm.h`, `Lib/<version>/km/x64/portcls.lib`, the matching `build/<version>` files, and InfVerif.

The verified environment uses Visual Studio 2026, SDK build 28000, and full WDK `10.0.28000.3231`. In this kit, `InfVerif.exe` is under `Tools\10.0.28000.0\x64` and `Inf2Cat.exe` is under `bin\10.0.28000.0\x86`. The project targets Windows 11 build 22621 at compile time and in the INF. Inf2Cat uses `10_NI_X64`, `10_GE_X64`, and `10_25H2_X64` for Windows 11 22H2, 24H2, and 25H2, respectively. See [Windows Driver Kit](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk) and [Inf2Cat](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/inf2cat).

The Debug x64 build writes the SYS, stamped INF, and unsigned CAT together under `bin/x64/Debug/SmartQuietCinema.VirtualAudio`. Treat the binary, INF validation, catalog generation, and signature as separate checks. Verify the package signature on the laboratory system before deployment. See [driver signing options](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings).

## Static analysis

See [Static analysis findings](STATIC-ANALYSIS.md) for the MSVC/WDK ruleset, warning registry, startup resource-list contract, and WaveRT ownership and notification constraints.

## Marker validation

Two independent render clients emit continuous deterministic, low-level signals. Source A uses 688 Hz on left and 1104 Hz on right. Source B uses 1424 Hz on left and 1936 Hz on right. The probe starts event-driven loopback first and captures both sources during a bounded overlap. The callback copies packets into preallocated memory and records frame counts, silence flags, discontinuities after startup, timestamp errors, and position gaps.

The analyzer projects Hann-windowed 125 ms chunks onto the marker frequencies at the actual capture rate. Four consecutive windows must contain both sources in the correct stereo channels. Projections allow gain and phase changes and require the expected frequency to exceed opposite-channel leakage. A fixed unrelated tone cannot satisfy all four projections. Significant energy at the pinned SysVAD default loopback tone of 3000 Hz also fails, even alongside the markers. The command reports no frames and captured silence separately, rejects malformed or truncated capture and nonfinite samples, and fails on observed transport errors. This is a controlled test for recognizable PCM and channel placement, with no bit-exact comparison. Keep other audio off the test endpoint while probing.

## Hyper-V preflight

The INF model decoration `NTamd64.10.0...22621` selects Windows 11 x64 build 22621 or later. Use a currently serviced x64 Windows 11 guest that meets this floor. The packaged INF declares `ks.inf` and `wdmaudio.inf` dependencies supplied by Windows and stamps KMDF 1.15. The built SYS imports PortCls, the NT kernel, HAL, and the Windows KMDF loader. Keep the stamped INF, matching SYS and CAT together. Generating the CAT and passing InfVerif checks package structure. The current SYS and CAT are unsigned and the package is **not ready to install**.

A later guest session requires a package whose signature satisfies the guest's Code Integrity policy. A self-signed test package on ordinary x64 Windows requires the separate test-signing setup and certificate trust. Memory Integrity requires a digital signature on the image even in test mode. With Secure Boot retained, Microsoft preproduction signing requires a provisioned guest and the appropriate preproduction signature. Changes to Secure Boot, BCD, test signing, or certificate trust are outside this preflight and require separate approval for the guest. [Windows test signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option) · [Secure Boot preproduction signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/preproduction-driver-signing-and-install)

**Driver runtime and PnP lifetime: Not yet validated.** The startup resource-list contract is resolved in source. Before admitting an active-stream removal or first unqualified driver trial, resolve the open adapter-lifetime and notification-quiescence question described in [Static analysis findings](STATIC-ANALYSIS.md#wavert-ownership-and-notification-ordering). Build and INF checks do not establish PortCls runtime behavior.

For a later approved installation, retain a clean pre-install Hyper-V checkpoint and confirm access to the guest console. Record the exact package hashes and the assigned device instance ID. Check the prototype's Device Manager status or `pnputil /enum-devices /instanceid "<experimental-instance-id>" /drivers` and preserve `%SystemRoot%\INF\setupapi.dev.log`, the `Microsoft-Windows-CodeIntegrity/Operational` log, relevant System PnP events and any crash dump. SetupAPI distinguishes package and binding failures from failures to start a bound device. On a crash, failed boot, or unsafe cleanup, save diagnostics outside the guest disk if possible and restore the known-clean checkpoint. A checkpoint does not replace a backup. [SetupAPI device logs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/setupapi-device-installation-log-entries) · [Code Integrity logs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/code-integrity-event-log-messages) · [Hyper-V checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints)

## Authorized laboratory procedure

Only perform the installation and cleanup steps on a designated laboratory system after separate approval. The development phase requires no installation, default-device change, or security setting change. Use a signed package appropriate for the lab system and its code-integrity policy. Do not disable Secure Boot or make a test-signing change as part of this procedure.

For Hyper-V, use a Generation 2 VM with Windows 11 x64 build 22621 or newer, vTPM, and Secure Boot enabled. A practical starting allocation is 2 vCPUs, 8 GiB RAM, and a 64 GiB virtual disk. Take a checkpoint before installation and use a local guest session without redirected audio. Supply a package whose signature the guest already trusts. A Microsoft preproduction signature requires a separately provisioned guest. See [Hyper-V VM generations](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/plan/should-i-create-a-generation-1-or-2-virtual-machine-in-hyper-v) and [preproduction driver signing with Secure Boot](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/preproduction-driver-signing-and-install). A VM without a real playback path can verify virtual PCM delivery. Verify absence of audible leakage separately on a laboratory system with a monitored physical output.

1. Build x64 Debug with the matching WDK. Locate the generated INF, SYS, and CAT. Run InfVerif on the generated INF and verify the package signature on the lab system before creating a device.
2. As the user who will run the probe, save `dotnet $probe defaults` and `dotnet $probe list` to the lab log. Start physical monitoring with a separate electrical recorder or an independent microphone. Keep the hardware defaults in place.
3. In an elevated **lab-only** shell, create one root devnode with the WDK [DevGen](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devgen-command-syntax) and record the actual instance ID from its output. Stage and associate the signed INF using [PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax).

   ```powershell
   devgen /add /bus ROOT /hardwareid "ROOT\SmartQuietCinemaVirtualAudio"
   pnputil /add-driver "<signed-package-directory>\SmartQuietCinemaVirtualAudio.inf" /install
   ```

4. Verify the matching hardware ID, provider, published `oem*.inf`, and device status. Check that `defaults` still matches the baseline verbatim and that `list` reports exactly one SmartQuietCinema endpoint. Use that endpoint ID. If PnP ancestry cannot be resolved, preserve the fail-closed behavior and investigate the identity mapping before rendering.
5. Run the probe twice at each source rate, allowing each process to exit before starting the next. Save formats, marker counts, frame counts, transport counters, and exit codes. Check the physical monitor for marker frequencies during the runs. The probe verifies capture from its two explicit Windows clients. For independent application coverage, also play a known marker through another shared-mode application explicitly assigned to the virtual endpoint and observe the virtual endpoint using an authorized lab capture. Preserve the system playback default throughout.

   ```powershell
   $virtualId = '<ID reported by list for the experimental device>'
   dotnet $probe probe $virtualId 48000
   dotnet $probe probe $virtualId 44100
   dotnet $probe defaults
   ```

6. To verify both **endpoint mix** rates, repeat with the experimental endpoint's own Default Format set to each advertised rate under lab authorization. Check the reported capture rate rather than inferring it from the command argument. Compare pre/post defaults, confirm both marker pairs and stereo placement, and confirm the physical monitor stayed free of the markers. Record any tone, missing markers, invalidation, or discontinuity as a failed run. Reopening the same ID should work without leaving a stale stream.
7. Close probe and test streams. Verify the exact instance ID belongs to the experimental hardware ID, then run `devgen /remove "<experimental-instance-id>"`. Verify the published package matches this experiment and has no other bindings before running `pnputil /delete-driver "<experimental-oem-inf>"`. Do not use forced or bulk removal. Verify that the experimental endpoint is gone and that `defaults` matches the pre-install snapshot. If a role differs, stop the trial and have the lab owner restore the recorded endpoint for that role. Report any original endpoint that is no longer available.

If the driver is missing, `probe` exits with code 1 before stream creation. A missing ID or invalid rate returns 2. `self-test`, `list`, and `defaults` open no audio streams. Hardware PCM transfer, actual conversion and format negotiation, endpoint identity on this driver, and unchanged physical output require the authorized laboratory run.
