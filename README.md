# DVMSIP — DVM SIP Telephone Interconnect

DVMSIP connects a [Digital Voice Modem](https://github.com/DVMProject) P25 network to a SIP PBX or SIP trunk, so radios can make and receive telephone calls. It joins the network as an FNE peer (via [fnecore](https://github.com/DVMProject/fnecore)), converts P25 IMBE voice to and from G.711 µ-law with [dvmvocoder](https://github.com/DVMProject/dvmvocoder), and handles SIP and RTP with [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery).

It runs on Windows, Linux and macOS (.NET 8).

> **Status:** early development. Conventional mode needs no dvmhost changes. Trunked mode depends on dvmhost P25 telephone interconnect support that is not yet in upstream dvmhost. Neither mode has been tested against a live system yet.

## Modes

| | `conventional` | `trunked` |
|---|---|---|
| What it is | A console-style **talkgroup patch** | Standards-based **P25 Telephone Interconnect** (TIA-102): one radio ↔ one phone party |
| Radio → phone | Any radio keying up on the talkgroup makes DVMSIP dial `outboundNumber` | A radio's interconnect request (preset number) dials the matching `presetNumbers` entry |
| Phone → radio | An incoming SIP call is answered and patched onto the talkgroup | The dialled DID (`didMap`) rings one radio, and the call is answered when the radio accepts |
| Audio to radio | Continuous while the call is up | Keyed by voice activity (VOX), in bursts, half-duplex |
| Ends when | The SIP side hangs up | Either side hangs up or the radio cancels |
| dvmhost changes | None | Required (see [Trunked mode setup](#trunked-mode)) |

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (runtime only, if you deploy a published build)
- CMake and a C++ compiler, to build the native `libvocoder` library
- A DVM FNE (`dvmfne`) the gateway can peer with
- A SIP registrar or PBX (Asterisk, FreeSWITCH, …) with an extension for DVMSIP

## Getting the source

The FNE peer library and the vocoder are Git submodules:

```bash
git clone --recurse-submodules https://github.com/majmongoose/DVMSIP.git
```

If you already cloned without submodules:

```bash
git submodule update --init --recursive
```

## Building

### 1. The vocoder library

DVMSIP loads `libvocoder` (`libvocoder.so`, `libvocoder.dylib` or `libvocoder.dll`) through the normal OS library search, so it must sit **next to the `dvmsip` executable**. It has to be built for each platform:

```bash
cmake -S extern/dvmvocoder -B extern/dvmvocoder/build -DCMAKE_BUILD_TYPE=Release
cmake --build extern/dvmvocoder/build --config Release
```

On Windows with Visual Studio the DLL ends up in `extern/dvmvocoder/build/Release/`.

### 2. DVMSIP

```bash
dotnet build dvmsip.sln -c Release
```

To produce a deployable build, publish it and copy the vocoder library alongside:

```bash
dotnet publish dvmsip/dvmsip.csproj -c Release -o publish
cp extern/dvmvocoder/build/libvocoder.so publish/
```

### 3. Tests

```bash
dotnet test tests/dvmsip.Tests/dvmsip.Tests.csproj
```

The tests cover the P25 wire formats DVMSIP builds itself, including a check of its Reed-Solomon encoder against dvmhost's, and confirm the example configuration loads. They do not need the vocoder library.

## Configuration

Copy `configs/dvmsip-config.example.yml` to `dvmsip-config.yml` and edit it. Keys are camelCase, as in dvmhost and dvmfne.

| Section | Purpose |
|---|---|
| `log` | Console and file log levels (0 none, 1 info, 2 debug, 3 trace) and log file path |
| `system` | `identity`: the name shown for this peer on the FNE |
| `network` | FNE address, port, `peerId`, `password`, and the voice `jitter` buffer in ms |
| `sip` | Registrar URI, user, password and realm, plus the local SIP address and port |
| `rtp` | RTP port range. Each call slot uses two ports (RTP + RTCP), so the range must fit `maxCalls × 2` |
| `phonePatch` | Mode and call settings (below) |

`phonePatch` settings:

| Key | Mode | Meaning |
|---|---|---|
| `mode` | both | `conventional` or `trunked` |
| `sourceRID` | both | Radio ID DVMSIP transmits as |
| `maxCalls` | both | Simultaneous calls (one call slot each) |
| `talkgroup` | conventional | Talkgroup that is patched |
| `outboundNumber` | conventional | Number dialled when a radio keys up on the talkgroup; empty disables radio-originated calls |
| `presetNumbers` | trunked | Map of PSTN address (0–255) → number, for radios' preset ("implicit") dialing |
| `didMap` | trunked | Map of inbound SIP DID (request URI user) → radio ID to ring |
| `answerTimeout` | trunked | Seconds to wait for a radio to answer an inbound call |
| `voxThreshold` | trunked | RMS level of phone audio that keys a transmission toward the radio |
| `voxHangTime` | trunked | Milliseconds of phone silence before that transmission ends |

`inboundDID`, `sip.proxy`, `rtp.dtmfRelay` and `network.local` are accepted but not used yet. Received DTMF digits are always logged.

## DVM network setup

### Conventional mode

On the FNE:
- Add DVMSIP's `peerId` to the peer list, if you use one.
- Make sure `talkgroup` exists and is active in the talkgroup rules.
- If the FNE rejects unknown radio IDs, add `sourceRID` to the radio ID list.

### Trunked mode

This needs dvmhost **with P25 telephone interconnect support** on both the sites and the FNE. That support is currently on the `r05a06_dev` branch of [majmongoose/dvmhost](https://github.com/majmongoose/dvmhost) and is not yet upstream. Then:

1. **Control channel:** set `telephoneInterconnect: true` under `protocols.p25` in each site's dvmhost config. Without it, interconnect requests are denied as unsupported.
2. **FNE peer list:** flag DVMSIP's peer as a telephone interconnect gateway. That is the 11th column (`1`) in the peer list file, the `telephoneInterconnect` field in the FNE REST API, or the *Interconnect* box in `peered`. The FNE only sends interconnect requests and calls to gateway peers.
3. **Radios:** the radio IDs in `didMap` must be registered on the system, and `sourceRID` must be a valid radio ID.

Explicit dialing (typing a number on the radio) is not supported yet; radios dial through preset numbers.

## Running

```bash
dvmsip -c /path/to/dvmsip-config.yml
```

With no `-c` / `--config`, DVMSIP reads `dvmsip-config.yml` from the working directory.

The `tools/` directory has helpers for Linux:
- `start-dvmsip.sh` runs DVMSIP in the foreground.
- `dvmsip-watchdog.sh` restarts DVMSIP if it exits with an error.
- `dvmsip.service` is a systemd unit. It expects the executable at `/usr/local/bin/dvmsip` and the config at `/etc/dvmsip/dvmsip-config.yml`; adjust as needed.

```bash
sudo cp tools/dvmsip.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now dvmsip
```

## Known limitations

- Conventional mode transmits phone audio for the whole call, so radios on the talkgroup can't easily talk back on a repeater.
- Trunked mode:
  - A radio calling a phone hears no ringback; its voice channel is granted when the far end answers.
  - Explicit dialing is not supported.
  - Sites that use explicit channel grants (separate transmit and receive channel numbers) refuse interconnect grants.
- Only G.711 µ-law (PCMU) is offered in SDP.

## Design documentation

`DVMSIP_BUILD_PLAN.md` holds the design: the FNE wire formats, the Phase 3 interconnect design and the dvmhost change list.

## License

DVMSIP is licensed under the **GNU AGPL v3.0 or later** (see `LICENSE`), as required by fnecore (AGPL-3.0). dvmvocoder is GPL-2.0.
