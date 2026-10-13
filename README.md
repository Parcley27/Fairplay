# Fairplay

Play **any Windows audio on a HomePod** — a single HomePod or an existing **stereo pair** —
over AirPlay 2. No iTunes, no Apple Music app, no virtual-cable driver required.

Fairplay is a from-scratch AirPlay 2 *sender* for Windows written in C#/.NET 8: HomeKit
transient pairing, the encrypted RTSP control channel, NTP clock sync and the encrypted
realtime RTP audio stream. It captures what Windows plays (WASAPI loopback) and streams it to
the speaker in real time.

```
 any app ──► Windows audio engine ──► WASAPI loopback ──► stereo/44.1 kHz resampler (clock-slaved)
                                                                  │
            HomePod / stereo pair ◄── encrypted RTP (ChaCha20-Poly1305) ◄── QPC-paced sender
                     ▲   │
     RTSP + HAP ─────┘   └──── NTP timing requests back to us (the speaker syncs to our clock)
```

## Using it

1. Run `publish\Fairplay.exe`. An AirPlay icon appears in the tray (on Windows 11 it may be
   hidden under the `^` overflow — drag it onto the taskbar to keep it visible).
2. Click the icon and pick your speaker. A stereo pair shows up as one entry (e.g.
   *Bedroom (stereo pair)*) that plays left/right on both HomePods, with each HomePod listed
   underneath (*Bed only*, *Desk only*) for playing on just one of them. Click the checked
   entry again to stop.
3. The first time, Windows asks whether Fairplay may use the network — allow it (both private
   and public). If that prompt was dismissed, Fairplay detects the block and offers to fix it.

Everything Windows plays now comes out of the HomePod. From the same menu:

| Option | What it does |
| --- | --- |
| **Speaker volume** slider | Sets the HomePod's own volume. |
| **Windows volume controls the speaker** | Volume keys / the Windows slider / mute drive the HomePod's volume, and the audio is sent at full level (Fairplay undoes Windows' software attenuation). Off by default so connecting never jumps the speaker to your PC's volume. |
| **Audio source** | *Everything Windows plays* follows the default output device. Or pick one specific playback device (see "Only some apps" below). |
| **Latency** slider | 50 ms – 1 s; applied (with a ~1 s reconnect) when you let go. Lower is better for video; raise it a notch if you hear crackles or dropouts (usually one HomePod with a weak Wi-Fi link). Below 80 ms is experimental. Default 120 ms. |
| **Reconnect to last speaker on start**, **Start with Windows** | What they say. |

If the connection drops, Fairplay reconnects by itself. If someone starts playing on the
HomePod from a phone, Fairplay steps aside instead of stealing it back (it checks the speaker's
"session active" flag before reconnecting) and tells you.

Notifications are kept to a minimum: one when Fairplay starts (saying where it's playing), and
otherwise only when something fails — a speaker can't be reached, or Fairplay gives up
reconnecting. Short drops that heal themselves are silent; the tray icon turns amber meanwhile.

### Only some apps on the HomePod

Point Fairplay at a playback device you don't otherwise listen to — e.g. a monitor's HDMI audio
with no speakers attached, or a virtual cable such as
[VB-CABLE](https://vb-audio.com/Cable/) — via **Audio source → Only this playback device**.
Then in **Settings → System → Sound → Volume mixer**, set the *Output device* of the apps you
want on the HomePod to that device. That device effectively *is* the HomePod: choose it per app,
or make it the default ("Send all apps to this device") to send everything. The same submenu
can rename the device after your speaker (e.g. *Bedroom (AirPlay)*) so it reads naturally in
Windows' output pickers, and restore the original name later.

### Command line

`publish\fairplay-cli.exe` exposes the same engine:

```
fairplay-cli discover                    list speakers (stereo pairs grouped)
fairplay-cli tone Bedroom                short test chime
fairplay-cli stream Bedroom              stream system audio until Ctrl+C
fairplay-cli run Bedroom --sync-volume   tray-app behaviour (reconnects, volume sync)
fairplay-cli devices                     playback devices you can capture
fairplay-cli firewall [--fix]            check / repair the firewall rule
fairplay-cli rename "CABLE Input" "Bedroom (AirPlay)"   rename a playback device
fairplay-cli selftest                    offline crypto / plist / resampler checks
```
Add `--latency <ms>`, `--volume <dB -30..0>`, `--device <name>`, `--seconds <n>`, `--verbose`.

## Requirements and limits

- Windows 10 2004+ / Windows 11, x64. The published executables are self-contained.
- The speaker's AirPlay access must allow this PC: in the Home app, *Home Settings → Speakers &
  TV → Allow Speaker & TV Access* = **Everyone** or **Anyone on the Same Network** (that's what
  enables HomeKit "transient" pairing). "Only People Sharing This Home" can't work for any
  non-Apple sender.
- Works with AirPlay 2 receivers that support transient pairing (HomePod, HomePod mini,
  HomePod 2nd gen, Apple TV, most AirPlay 2 speakers). AirPlay-1-only and password-protected
  receivers aren't supported.
- One target at a time (one speaker or one stereo pair). Multi-room is not implemented.
- Windows doesn't know about the speaker's latency, so video lip-sync is off by the chosen
  latency (+~30 ms).
- A single HomePod of a pair gets a mono mix (a paired HomePod on its own plays only its
  channel). For a HomePod mini, which has one driver, that loses nothing.

## How it works (and what we learned)

Validated against two HomePod minis on **HomePod OS 27.0** (`AudioAccessory5,1`, AirTunes
980.77.2) configured as a stereo pair.

**Discovery.** Plain mDNS (`_airplay._tcp`). A stereo pair advertises two devices with the same
`tsid` (tight-sync id) and `gpn` (group name, e.g. "Bedroom"); one has `igl=1` (AirPlay group
leader) and the other has status bit `0x2000` (tight-sync leader).

**Stereo pairs.** The group leader does *not* relay a third-party realtime stream to its
partner: streaming to the leader alone plays only that speaker — and only its own channel.
Fairplay therefore opens a session with **both** HomePods and sends each the same stereo stream
on one shared RTP timeline and sync anchor; each renders its side. While both sessions are up,
the speakers re-advertise themselves as separate group leaders with `gid=<tsid>+0` (left) and
`gid=<tsid>+1` (right). Each one syncs independently to our NTP clock, which keeps the two sides
together. Volume set on either applies to both; Fairplay sets both anyway.

**Pairing.** `POST /pair-setup` with TLV8 `{method 0, state 1, flags 0x10 (transient)}`, then
SRP-6a (RFC 5054 3072-bit group, SHA-512, user `Pair-Setup`, PIN `3939`):
`k = H(N|PAD(g))`, `u = H(PAD(A)|PAD(B))`, `x = H(s|H(I:P))`, `K = H(S)`,
`M1 = H(H(N)⊕H(g)|H(I)|s|A|B|K)`. Transient pairing stops after M4; `K` seeds every key via
HKDF-SHA512 (`Control-Salt` / `Control-Write|Read-Encryption-Key`, `Events-Salt` /
`Events-Read|Write-Encryption-Key` — the event channel's directions are swapped).

**Control channel.** After pairing, RTSP on TCP 7000 is wrapped in HAP framing:
`[len u16le][ChaCha20-Poly1305(block ≤1024, aad=len)][tag]`, nonce = 4 zero bytes ‖ u64le counter
per direction. Sequence: `SETUP` (bplist: device info, `timingProtocol: NTP`, our timing port) →
connect the event channel (receiver POSTs `/command updateInfo`, we answer 200) → `SETUP` stream
(`type 96` realtime, `ct 1` LPCM, `audioFormat 0x800` = 44.1 kHz/16-bit/stereo, `spf 352`,
`shk` = 32-byte stream key) → `RECORD` → `POST /feedback` every 2 s → `TEARDOWN`.
Volume: `SET_PARAMETER volume: <dB>` (−30…0, −144 mute; mapped linearly from 0–100 %).

**Timing.** The HomePod sends NTP-style requests (pt 0x52) to our timing port and won't answer
the stream `SETUP` until it has synced — so a firewall that drops inbound UDP makes it hang.
Sync packets (pt 0x54, every second) say "RTP time *T − latency* plays at NTP time *now*".
Our NTP clock is QPC-based and never jumps.

**Audio.** RTP header (pt 96) ‖ ChaCha20-Poly1305(PCM, key `shk`, aad = header[4..12]) ‖ tag ‖
8-byte nonce. PCM is **big-endian** s16. Lost packets are re-requested (pt 0x55) and resent from
a backlog (pt 0x56). The receiver reports `arrivalToRenderLatencyMs: 70`.

**Capture.** WASAPI loopback on the chosen endpoint (plus a silent keep-alive stream so the
engine never stops delivering), downmix to stereo, then a Kaiser-windowed-sinc polyphase
resampler (≈90 dB SNR) whose ratio is trimmed by a FIFO-level controller — that slaves the
sound card's clock to the sender clock without ever dropping or repeating samples (measured
drift on this PC's HDMI device: ~75–150 ppm).

**Useful receiver status bits** (`statusFlags` in `/info` / `flags` in TXT):
`0x20000` = a sender session is active (used to detect another device taking over),
`0x100000` = receiver is rendering audio — it stays set for valid streams and drops within
~0.6 s if every packet fails authentication, which made it possible to verify decryption
end to end without a microphone.

### Verification done

- Offline self-tests: binary plist, TLV8 fragmentation, SRP against an independent server
  implementation, HAP framing, resampler quality.
- Live: pairing/SETUP/RECORD/feedback/TEARDOWN against the stereo pair; the leader's rendering
  flag stays set for valid audio and clears for deliberately corrupted packets.
- Loopback path: a known test signal played through Windows came out of the sender bit-clean —
  correct frequencies, L/R order preserved, 0 underruns/overflows, no phase discontinuities over
  2,372 consecutive 5 ms blocks.
- Volume sync: Windows 52 % → −14.4, 30 % → −21.0, mute → −144, observed on *both* HomePods.
- Stereo pair: both HomePods accept parallel sessions, each runs its own clock sync and
  requests retransmits of our packets (so each is decrypting and sequencing the stream), and
  they re-advertise as left (`+0`) / right (`+1`) while streaming. Capture → pair at 80 ms
  latency with a 20 ms capture buffer: 0 underruns, 0 phase discontinuities.
- Takeover: a second sender kicks Fairplay off; Fairplay waits, then reconnects if the other
  sender stops within ~12 s, otherwise steps aside and notifies.

## Building

```
dotnet build Fairplay.sln
dotnet publish src/Fairplay.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
dotnet publish src/Fairplay.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Layout: `src/Fairplay.Core` (discovery, crypto, plist, AirPlay protocol, capture, controller),
`src/Fairplay.App` (tray UI), `src/Fairplay.Cli` (command line + self-tests).

Prior art that informed this: [pyatv](https://github.com/postlund/pyatv) (RAOP/AirPlay 2
client), [OwnTone](https://github.com/owntone/owntone-server), the
[openairplay spec](https://openairplay.github.io/airplay-spec/), and the recent wave of
Windows senders (AirFlash, AirSend, playfruit).
