# AgentInfo — context for agents working on Fairplay

Fairplay is a from-scratch **AirPlay 2 sender for Windows** (C#/.NET 8). It captures everything
Windows plays (WASAPI loopback) and streams it to HomePods — a single HomePod, both halves of a
stereo pair, or one half of a pair on its own. Front ends: a system-tray app (`Fairplay.exe`) and
a CLI (`fairplay-cli.exe`) that share one core library.

Status: working end to end and in daily use by the owner against two HomePod minis
(HomePod OS 27) configured as a stereo pair. Read this file before changing protocol code — most
of what's here was learned by experiment and isn't obvious from the code.

---

## 1. Repo map

```
Fairplay.sln
src/Fairplay.Core/            class library (net8.0-windows), all the real work
  Discovery/Mdns.cs           minimal mDNS querier (shares UDP 5353, falls back to legacy unicast)
  Discovery/AirPlayDevice.cs  AirPlayDevice (TXT parsing, feature/status bits), AirPlayTarget, grouping
  Crypto/Srp.cs               SRP-6a client (3072-bit, SHA-512) for HomeKit pair-setup
  Crypto/HapCrypto.cs         HKDF key derivation + HAP framed ChaCha20-Poly1305 stream cipher
  Crypto/Tlv8.cs              TLV8 encode/decode with fragmentation
  Plist/BinaryPlist.cs        bplist00 reader/writer (+ Dump for logs)
  AirPlay/RtspConnection.cs   RTSP/HTTP over TCP 7000, transparent HAP encryption after pairing
  AirPlay/AirPlaySession.cs   one receiver: pairing, SETUP x2, RECORD, volume, /feedback, TEARDOWN
  AirPlay/EventChannel.cs     reverse channel the receiver POSTs /command to (we answer 200)
  AirPlay/TimingServer.cs     answers NTP-style timing requests (shared by all receivers)
  AirPlay/RtpAudioChannel.cs  per-receiver UDP: encrypted audio, sync packets, retransmits
  AirPlay/AirPlayStreamer.cs  one target (1..2 receivers): setup, QPC-paced send loop, mono downmix
  Audio/LoopbackSource.cs     WASAPI loopback capture → stereo → resampler → FIFO (clock-slaved)
  Audio/Resampler.cs          Kaiser-windowed-sinc polyphase resampler with live ratio trim
  Audio/IAudioSource.cs       source interface + ToneSource (test chime, --pan support)
  Audio/WindowsAudio.cs       IPolicyConfig COM: set default device, rename endpoint, set volume
  Control/StreamController.cs app brain: discovery loop, start/stop, reconnect, takeover, volume sync
  Control/Settings.cs         %APPDATA%\Fairplay\settings.json
  Util/NtpClock.cs            QPC-based monotonic NTP clock (never jumps)
  Util/Firewall.cs            read/repair Windows Firewall rules via HNetCfg.FwPolicy2 / netsh
  Util/Log.cs                 tiny logger; sinks subscribe to Log.Message
src/Fairplay.App/             WinForms tray app (assembly "Fairplay")
  TrayApp.cs                  the whole UI: one ContextMenuStrip with sliders hosted in it
  Program.cs                  single-instance mutex, --exit, --show-menu, log file
  TrayIcons.cs                tray glyph drawn at runtime (idle/streaming/busy)
  LogForm.cs, Assets/         log window; .ico/.png
src/Fairplay.Cli/             console app (assembly "fairplay" in Debug, "fairplay-cli" in Release)
  Program.cs                  discover/devices/info/tone/stream/run/firewall/rename/setvol
  SelfTest.cs                 offline tests (plist, TLV8, SRP vs independent server, HAP, resampler)
publish/                      (gitignored) self-contained single-file builds
```

Dependencies: only `NAudio.Wasapi` 2.2.1 (WASAPI/COM wrappers). Everything else — mDNS, plist,
SRP, TLV8, RTSP, resampler — is in-repo. Crypto primitives come from the .NET BCL.

## 2. Build, run, test

- **SDK location**: the .NET 8 SDK on the owner's machine is a per-user install at
  `%LOCALAPPDATA%\Microsoft\dotnet-sdk\dotnet.exe` — it is **not on PATH**. Use the full path
  (and set `DOTNET_CLI_TELEMETRY_OPTOUT=1`).
- **Runtime gotcha**: the machine has only `Microsoft.NETCore.App`, not the Windows Desktop
  runtime. The Debug WinForms exe therefore fails ("You must install .NET", a *modal dialog* that
  blocks your shell command) unless you set `DOTNET_ROOT` to the SDK dir above. The CLI (no
  WinForms) runs fine without it. Published builds are self-contained and need nothing.
- Build: `dotnet build Fairplay.sln -c Debug` (with the SDK path above — the `dotnet` on PATH is
  runtime-only and has no SDK).
- Publish (what the owner runs):
  ```
  dotnet publish src/Fairplay.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
  dotnet publish src/Fairplay.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
  ```
  Sizes: `Fairplay.exe` ≈ 72 MB, `fairplay-cli.exe` ≈ 35 MB (bundled runtime; trimming is unsafe
  with WinForms + COM interop).
- Self-tests: `fairplay-cli selftest` (must all pass).
- **Single instance**: the tray app holds mutex `Local\Fairplay.Tray`. A second launch exits
  silently — including your Debug build if the owner's published copy is running. Stop it
  gracefully with `publish\Fairplay.exe --exit` (signals event `Local\Fairplay.Exit`; the app sends
  TEARDOWN). Don't `taskkill` unless stuck — the speaker then keeps a dead session for a while.
  If the owner's copy was running, restart it when done (it auto-connects to the last speaker).
- `Fairplay.exe --show-menu` pops the tray menu open after 13 s so it can be screenshotted.
- Logs: `%APPDATA%\Fairplay\fairplay.log` (`--verbose` for protocol-level debug incl. plist dumps).
- File locks: a running app/CLI locks `bin\` DLLs and `publish\*.exe`; stop it before building.
- Settings: `%APPDATA%\Fairplay\settings.json` (LastTargetId, SourceDeviceId, LatencyMs [default 120],
  SyncVolume, AutoConnect, RenamedDevices). CLI `run` uses `Persist = false` so it never overwrites
  the tray app's settings — keep it that way. If tests change the owner's settings (e.g. latency),
  restore them.

## 3. The protocol as implemented (verified on HomePod OS 27, AirTunes 980.77.2)

**Discovery.** mDNS PTR query for `_airplay._tcp.local`; TXT keys used: `deviceid`, `model`,
`features` (64-bit as `lo,hi`), `flags` (status), `gid`, `gpn` (group name), `igl` (is group
leader), `pgid`, `tsid` (tight-sync id), `pk`, `osvers`. Feature bit 48 = transient pairing.
Python's `zeroconf` and our own querier both work on this network (Public profile Wi-Fi).

**Pairing (HomeKit transient).** `POST /pair-setup` sent as `HTTP/1.1` with `X-Apple-HKP: 4`:
- M1 TLV `{0:0x00 method, 6:0x01 state, 0x13:0x10 flags=transient}` → M2 `{salt, B}`
- SRP-6a: user `Pair-Setup`, PIN `3939`, RFC 5054 3072-bit group, g = 5, SHA-512.
  `k=H(N|PAD(g))`, `u=H(PAD(A)|PAD(B))`, `x=H(s|H("Pair-Setup:3939"))`, `K=H(S)` (S minimal bytes),
  `M1=H(H(N)⊕H(g) | H(I) | s | A | B | K)` with A/B minimal big-endian. Normalize `(B − k·v) mod N`
  to positive before ModPow (C# BigInteger keeps the sign).
- M3 `{state 3, A, M1}` → M4 `{proof M2}`; verify `M2 = H(A|M1|K)`. Retry with a fresh key up to 3×
  (guards against rare leading-zero encoding mismatches). No M5/M6.
- Works only if the Home app setting *Allow Speaker & TV Access* is "Everyone" or "Same network".
  pyatv also sends `/pair-pin-start` first; not needed.

**Keys.** HKDF-SHA512, 32-byte outputs, from `K`:
control `Control-Salt` + `Control-Write-Encryption-Key` (ours to send) / `Control-Read-…` (receive);
event channel `Events-Salt` with directions **swapped** (we write with `Events-Read-Encryption-Key`).

**HAP framing** (control + event TCP): blocks ≤1024 B: `[len u16 LE][ChaCha20-Poly1305(block, aad=len)][tag 16]`,
nonce = 4 zero bytes ‖ u64 LE counter, separate counter per direction.

**RTSP sequence** (all encrypted after pairing; headers CSeq, User-Agent `AirPlay/690.7.1`,
DACP-ID, Active-Remote, Client-Instance; URI `rtsp://<localIP>/<sessionId>`):
1. `GET /info` (before pairing, unauthenticated) — plist incl. `statusFlags`, `initialVolume`,
   `supportedFormats.audioStream = 0x1440800` → realtime stream supports only **44.1 kHz**:
   PCM 16/2 (`0x800`), ALAC 16/2, AAC-LC, AAC-ELD.
2. `SETUP` #1 bplist: deviceID/macAddress (stable fake MAC), sessionUUID, `timingPort`,
   `timingProtocol: "NTP"`, isMultiSelectAirPlay true, groupContainsGroupLeader false,
   model `Windows1,1`, name = machine name, osName/osVersion, senderSupportsRelay false,
   sourceVersion `690.7.1`, statsCollectionEnabled false. Reply: `eventPort`, `skipRecord: true`.
3. Connect the event channel (TCP to eventPort, HAP-encrypted). The receiver POSTs
   `/command` (`type: updateInfo`); answer `RTSP/1.0 200 OK` + CSeq + `Audio-Latency: 0`.
   **When another sender takes over, the receiver closes this channel first** — that's our
   fastest "lost" signal.
4. `SETUP` #2 `streams:[{type 96, ct 1 (LPCM), audioFormat 0x800, spf 352, sr 44100,
   controlPort, shk (random 32-byte stream key), latencyMin min(11025,L), latencyMax max(88200,L),
   isMedia, audioMode default, supportsDynamicStreamID false, streamConnectionID}]`.
   Reply `dataPort`, `controlPort`, `arrivalToRenderLatencyMs: 70`.
   **The receiver does not answer this SETUP until it has synced to our clock** — so if its timing
   requests can't reach us (firewall), it hangs. We time out after 6 s and, if zero timing
   requests arrived, throw `ReceiverCannotReachUsException`.
5. `RECORD` with `RTP-Info: seq=…;rtptime=…`, `Range: npt=0-`.
6. `POST /feedback` every 2 s (keep-alive; IO error or 3 failures ⇒ lost).
7. `SET_PARAMETER` `text/parameters` `volume: <dB>` (−30…0, −144 mute; UI maps 0–100 % linearly).
8. `TEARDOWN` on stop.

**Timing.** Receiver sends 32-byte requests (pt `0x52`) to our timing port; reply pt `0xD3`,
origin = their transmit timestamp, receive/transmit = our `NtpClock`. Each receiver syncs
independently (~0.7 requests/s). One `TimingServer` bound to `0.0.0.0` serves all receivers.

**Sync packets** (control port, every 1 s, first with `0x90` else `0x80`): `D4 0007 | rtpNow−L |
NTP(now) | rtpNow` — "the frame with RTP time rtpNow−L plays at NTP now". `L` = latency in frames.
All receivers get identical anchors from one clock read — this is what keeps a pair in sync.

**Audio packets** (to dataPort): `80 60|E0(marker on first) | seq u16 | ts u32 | ssrc u32` +
ChaCha20-Poly1305(payload = 352 frames **big-endian** s16 stereo = 1408 B, key `shk`,
aad = header[4..12], nonce = 4 zero ‖ u64 LE counter) + tag 16 + the 8-byte counter (LE) appended.
1444 bytes per packet, one every 7.98 ms.

**Retransmits.** Receiver sends `80 D5 seq first count` to our control port; we answer from a
1024-packet backlog with `80 D6 <orig seq>` + the original packet.

## 4. Stereo pairs (the non-obvious part)

- A pair advertises two devices with the same `tsid` and `gpn`. One has `igl=1` (AirPlay group
  leader — "Bed" here); the other has status bit `0x2000` TightSyncIsGroupLeader ("Desk").
- **The group leader does NOT relay a third-party realtime stream to its partner.** Streaming to
  `igl=1` alone plays only that speaker (and, it appears, only its own channel). Initial design
  assumed relaying; the owner reported "only Bed plays".
- Fix (matches OwnTone users' reports, owntone issue #1413): open a full session with **both**
  members, send each the same stereo stream on one RTP timeline / sync anchor. Each renders its
  side. While both sessions are up they re-advertise as separate leaders: `gid=<tsid>+0` (left,
  Desk) and `gid=<tsid>+1` (right, Bed).
- One member alone (`TargetKind.PairMember`, "Bed only") gets a **mono downmix** (L=R=(L+R)/2) so
  nothing is lost if it only renders its side. HomePod mini has one driver, so no loss.
- Volume set on either member propagates to both; we set both anyway.
- Evidence the follower really participates without hearing it: Desk sends its own timing and
  retransmit requests, which requires decrypting/sequencing our packets. To check left/right by
  ear: `fairplay-cli tone <pair> --pan left` (should sound only on Desk).
- Targets: `AirPlayBrowser.GroupTargets` emits pair + each member; ordering via
  `AirPlayTarget.Sort` (pair, then its members). Target ids: pair `group:<tsid>`, others
  `device:<deviceid>`.

## 5. Receiver status bits (`statusFlags` in /info, `flags` in TXT)

| Bit | Meaning (observed) |
| --- | --- |
| `0x20000` | a sender session is active — **except** on the tight-sync leader, where it's always set |
| `0x100000` | receiver is rendering audio (leader). Stays set for valid streams; clears within ~0.6 s if every packet fails auth. Does NOT detect late packets (stays set even at 30 ms latency). Not set on the tight-sync member. |
| `0x2000` | TightSyncIsGroupLeader (one member of a pair) |
| `0x800` | cleared on Desk while the pair is in a session (meaning unknown) |

Takeover detection (`StreamController.IsBusyWithAnotherSenderAsync`): busy = `0x20000` set and
`0x2000` not set. Before that fix, Desk's permanent bit made every reconnect give up.

## 6. Audio pipeline

- `LoopbackSource`: WASAPI shared-mode loopback on the default render endpoint (follows default
  changes via `IMMNotificationClient`) or a chosen endpoint. Polls every 3 ms (engine period 10 ms).
- A **silent keep-alive `WasapiOut`** on the same endpoint keeps loopback packets flowing when
  nothing plays (otherwise loopback goes quiet and the FIFO drains).
- Downmix >2 channels (ITU-ish, LFE dropped), float/int16/24/32 handled.
- Loopback is captured **after Windows' software volume** (the owner's HDMI endpoint applies volume
  in software; measured −9.9 dB). "Windows volume controls the speaker" mode applies make-up gain
  `10^(−levelDb/20)` (only when the endpoint has no hardware volume) and maps the slider to speaker
  volume. Off by default so connecting never jumps the speaker to the PC's volume.
- `Resampler`: 96-tap Kaiser (β=9) sinc, 512 phases, linear phase interpolation, ≈90 dB SNR
  (limited by Kaiser ripple, not phases). 48 kHz → 44.1 kHz.
- Clock slaving: the sender reads 352 frames per packet on the QPC schedule; the FIFO level
  (EMA-smoothed) drives `Resampler.Trim = clamp(0.05 × error_s, ±0.003)`. Measured drift
  HDMI-vs-QPC ≈ 75–150 ppm. No drops/repeats.
- FIFO target 20 ms, grows +5 ms per underrun up to 60 ms. Buffering state outputs silence until
  target is reached; overflow >150 ms over target jumps back.
- Send loop: dedicated thread, `timeBeginPeriod(1)`, MMCSS "Pro Audio", `Thread.Sleep(1)`.
  (A spin/yield near deadlines cost ~10 % of a core for nothing — the receiver buffers the whole
  latency window.) Steady state ≈ 2 % of one core.
- Total latency ≈ setting + ~20 ms FIFO + ~10 ms engine period. The owner uses 80 ms; slider
  goes 50 ms – 1 s; <80 ms labelled experimental (receiver's arrival-to-render is 70 ms).

## 7. App behaviour (StreamController + TrayApp)

- States: Idle → Connecting → Streaming; on loss → Reconnecting (backoff 1,2,3,4,8,15,30,30,30 s,
  re-discovering each time). If the target looks busy (another sender) for >12 s, give up and
  notify; a brief interruption reconnects silently. A reconnect queued behind a user Stop is
  dropped (`reconnectToken`).
- Any receiver of a pair lost ⇒ whole target lost ⇒ reconnect both.
- Notifications policy (owner's request): one on app start ("playing on X" / "pick a speaker" /
  a failure), afterwards **only failures**. Every toast is logged as `notification: …`.
- Latency slider is debounced 0.7 s and restarts the stream (live latency change via sync packets
  was not attempted — unknown how HomePods react).
- Firewall: on `ReceiverCannotReachUsException` the app re-checks rules (the Windows prompt may
  have just been answered), else offers `Firewall.Repair` (UAC `netsh`: delete rules for the exe,
  add allow UDP+TCP inbound, `remoteip=localsubnet`). Windows creates **temporary Block rules while
  its own prompt is pending** — a "Blocked" reading can flip to Allowed once the user clicks.
- "Audio source → Only this playback device" + "Show this device as '<Speaker> (AirPlay)'" renames
  an endpoint via `IPolicyConfig.SetPropertyValue(PKEY_Device_DeviceDesc)` — works without admin;
  original names kept in settings for restore.

## 8. Decisions and why

- **C#/.NET 8**: runtime already present; SDK installs per-user without admin (Rust/C++ would need
  VS Build Tools); BCL has ChaCha20-Poly1305/HKDF/SHA-512/BigInteger; first-class COM for WASAPI,
  IPolicyConfig, firewall; WinForms for the tray. Cost: 72 MB self-contained exe.
- **NTP timing, not PTP**: HomePods accept NTP for realtime streams; PTP would need ports 319/320
  and a PTP stack.
- **Realtime stream (type 96) with LPCM**, not buffered (103) or ALAC: simplest that works; ALAC
  needs a real encoder for any bandwidth saving (uncompressed ALAC frames save nothing).
- **No virtual audio driver shipped/installed.** A true Windows output device needs a signed
  kernel driver (impossible to sign here). VB-CABLE works as an opt-in source; we deliberately did
  not install it autonomously (kernel driver, possible reboot, UAC appeared to auto-approve).
- Own mDNS/plist instead of libraries: small, no dependency risk, full control over 5353 sharing.

## 9. Verifying without ears (the PC has no working microphone)

- `fairplay-cli tone <speaker> --level -80` = inaudible chime: use it for protocol, latency and
  takeover tests so the bedroom speakers stay quiet. Restore volume with `--volume <dB>` if changed.
- Poll `GET /info` (unauthenticated, port 7000) for `statusFlags` on both members while testing
  (`fairplay-cli info <speaker>` dumps it; a 20-line Python socket poller is handy).
- `tone --corrupt` breaks every auth tag: the leader's `0x100000` drops within ~0.6 s — proves the
  good path is decrypted.
- Per-receiver stats (`tone`/`stream` print them): timing + retransmit requests per speaker show
  each one is actively consuming the stream.
- `stream --dump x.wav` writes exactly what is sent. Check with per-5 ms-block sine fits and phase
  continuity — a single global sine fit is too strict (the clock trim wobbles ±tens of ppm).
- Play known signals with Python `sounddevice` — it drops ~270 ms at stream start, so prepend 1 s
  of silence.
- pyatv (`pip install pyatv`) works as a "second sender" for takeover tests. Python 3.14 breaks
  `atvremote` (`get_event_loop`); drive the API with `asyncio.run` instead.
- Tray UI: `--show-menu` + screenshot (PowerShell `Graphics.CopyFromScreen`). WinForms `TrackBar`
  exposes no UIA RangeValue pattern — click its track with `SetCursorPos`/`mouse_event` instead.
- Packet loss reference: Bed's Wi-Fi link shows ~4 % loss (alone or in the pair), Desk ~0 %;
  all recovered by retransmits.

## 10. Pitfalls hit along the way

- `Action<ReadOnlySpan<T>>` isn't legal in C# 12 → `Resampler` uses a `FrameSink` delegate.
- `TrackBar` crashes the app on start if given `Color.Transparent`; use
  `ProfessionalColors.ToolStripDropDownBackground` to match the menu.
- Async work started in the `ApplicationContext` constructor runs before the message loop; post it
  via the sync context so balloons/MessageBoxes stay on the UI thread.
- Editing C# through shell heredocs + `sed`/Python mangles `\n`, `\f`, `\u…` escapes — prefer the
  Edit tool for strings containing backslashes.
- The default git branch is `master` (there is no `main`). The owner asked for one commit per
  change.

## 11. Ideas / not done

- Multi-room (several unrelated speakers) — the streamer already fans out to N receivers with one
  timeline; mainly UI + target model work.
- ALAC encoding to halve bandwidth (would help Bed's lossy link at very low latency).
- Live latency change without restarting (send new sync anchors) — untested.
- Per-app capture via process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`).
- Label pair members left/right (only observable via `gid …+0/+1` while streaming).
- AirPlay-1-only and password-protected receivers aren't supported.
