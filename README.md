# Fairplay

**Play anything from your Windows PC on your HomePods.**

Windows has never been able to use a HomePod as a speaker — only the Apple Music app can cast to
one. Fairplay fixes that: it sits in the system tray and sends whatever your PC is playing
(Spotify, YouTube, games, calls, system sounds) to a HomePod over AirPlay.

- Works with a single HomePod, a **stereo pair** (true left/right), or just one speaker of a pair
- Low delay — down to about 80 ms, so video stays watchable
- Reconnects on its own if Wi-Fi blips, and steps aside if someone starts playing from their phone
- Optional: let your keyboard volume keys control the HomePod's volume
- No iTunes, no drivers, nothing to install

## Getting started

1. Run `Fairplay.exe`. An AirPlay icon appears in the system tray (on Windows 11 it may be hidden
   behind the `^` arrow — drag it onto the taskbar to keep it handy).
2. Click the icon and choose your speaker. Your PC's audio now plays there.
3. The first time, Windows asks whether Fairplay may use the network. Click **Allow** — the
   HomePod needs to talk back to your PC to keep the audio in time.

Next time you start Fairplay it reconnects to the same speaker automatically.

## The menu

| | |
| --- | --- |
| **Speakers** | Click a speaker to play on it; click it again to stop. A stereo pair appears once (e.g. *Bedroom (stereo pair)*), with each of its HomePods listed underneath (*Bed only*, *Desk only*) if you want just one. |
| **Speaker volume** | The HomePod's own volume. |
| **Latency** | How far behind your PC the HomePod plays. Lower is better for video; if you hear crackles or dropouts, nudge it up. |
| **Audio source** | Everything Windows plays (default), or just one playback device — see below. |
| **Windows volume controls the speaker** | Your volume keys and the Windows volume slider change the HomePod's volume. |
| **Reconnect to last speaker on start** / **Start with Windows** | What they say. |

Fairplay stays quiet: you get one notification when it starts, and after that only if something
goes wrong.

### Sending only some apps to the HomePod

Pick a playback device you don't otherwise listen to — for example a monitor's HDMI audio with
nothing plugged in, or a virtual cable like [VB-CABLE](https://vb-audio.com/Cable/) — under
**Audio source → Only this playback device**. Then, in Windows **Settings → System → Sound →
Volume mixer**, send the apps you want on the HomePod to that device. Fairplay can rename the
device after your speaker (e.g. *Bedroom (AirPlay)*) so it's easy to find.

## Requirements

- Windows 10 (2004 or later) or Windows 11, 64-bit.
- PC and HomePod on the same network.
- In the Home app: **Home Settings → Speakers & TV → Allow Speaker & TV Access** must be set to
  **Everyone** or **Anyone on the Same Network**.
- HomePods and most AirPlay 2 speakers work. Older AirPlay-1-only and password-protected speakers
  don't.

## Troubleshooting

- **"Couldn't connect" / no sound:** usually Windows Firewall. Fairplay detects this and offers to
  fix it (Windows will ask for permission).
- **Crackles or short dropouts:** raise the latency a step; it's usually one HomePod with a weak
  Wi-Fi signal.
- **Video out of sync:** Windows doesn't know about the speaker's delay, so lower the latency as
  far as it plays cleanly.
- **Something else:** *Show log…* in the menu, or `%APPDATA%\Fairplay\fairplay.log`.

## How it works, briefly

Fairplay records what Windows sends to your speakers, converts it to the format HomePods expect,
and streams it over AirPlay 2 — the same encrypted protocol iPhones and Macs use. The HomePod
syncs its clock to your PC so playback stays steady, and for a stereo pair both HomePods are sent
the stream with a shared clock, so each can play its own side in sync.

It's written from scratch in C# (.NET 8). See [AgentInfo.md](AgentInfo.md) for the technical
details, protocol notes and what was learned building it.

## Command line

`fairplay-cli.exe` drives the same engine without the tray app:

```
fairplay-cli discover            list speakers on the network
fairplay-cli tone Bedroom        play a short test chime (--pan left|right to check channels)
fairplay-cli stream Bedroom      stream your PC's audio until Ctrl+C
```

Run it with no arguments for the full list of commands and options.

## Building

Needs the .NET 8 SDK.

```
dotnet publish src/Fairplay.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
dotnet publish src/Fairplay.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Thanks to [pyatv](https://github.com/postlund/pyatv), [OwnTone](https://github.com/owntone/owntone-server)
and the [openairplay spec](https://openairplay.github.io/airplay-spec/), whose work made this
possible.
