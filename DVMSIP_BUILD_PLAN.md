# DVMSIP Build Plan

A SIP telephone interconnect gateway for the DVMProject ecosystem, enabling P25 radios to make and receive telephone calls via a SIP PBX or SIP trunk.

---

## Repository Setup

Create a new repository: `DVMProject/dvmsip`

### Solution structure

```
dvmsip/
├── dvmsip.sln
├── dvmsip/                         # Main application project
│   ├── dvmsip.csproj
│   ├── Program.cs
│   ├── HostService.cs              # IHostedService entry point
│   ├── Configuration/
│   │   └── DvmSipConfig.cs         # YAML config model
│   ├── Fne/
│   │   └── SipPeerSystem.cs        # fnecore peer subclass
│   ├── Sip/
│   │   ├── SipService.cs           # SIPSorcery UA management
│   │   └── RtpBridge.cs            # RTP session + G.711 codec
│   ├── Vocoder/
│   │   ├── ImbeVocoder.cs          # P/Invoke wrapper for libdvmvocoder
│   │   └── AudioBridge.cs          # IMBE ↔ PCM conversion thread
│   └── CallControl/
│       ├── CallStateMachine.cs     # IDLE/DIALING/RINGING/CONNECTED/RELEASING
│       └── PhonePatchCall.cs       # Per-call state container
├── configs/
│   └── dvmsip-config.example.yml
├── tools/
│   ├── start-dvmsip.sh
│   └── dvmsip-watchdog.sh
└── CMakeLists_vocoder.txt          # Optional: build dvmvocoder as part of this repo
```

### Project dependencies (NuGet)

```xml
<PackageReference Include="SIPSorcery" Version="*" />
<PackageReference Include="YamlDotNet" Version="*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="*" />
<PackageReference Include="Microsoft.Extensions.Logging" Version="*" />
<PackageReference Include="Serilog.Extensions.Hosting" Version="*" />
```

### Git submodules

```bash
git submodule add https://github.com/DVMProject/fnecore   extern/fnecore
git submodule add https://github.com/DVMProject/dvmvocoder extern/dvmvocoder
```

Add `extern/fnecore/fnecore.csproj` as a project reference in `dvmsip.csproj`.

---

## Configuration

File: `configs/dvmsip-config.yml`

```yaml
log:
  displayLevel: 1        # 0=none 1=info 2=debug 3=trace
  fileLevel: 1
  filePath: ./dvmsip.log

system:
  identity: "DVMSIP-1"  # FNE peer identity string

network:                 # FNE peer connection
  address: "192.168.1.10"
  port: 62031
  local: 0              # local UDP port (0 = any)
  peerId: 9000001       # unique peer ID — must not clash with any dvmhost peer
  password: "s3cr3t"
  jitter: 360           # voice jitter buffer in ms

sip:
  registrar: "sip:pbx.local"
  user: "phonepatch"
  password: "sip-password"
  realm: "pbx.local"
  proxy: ""             # outbound proxy URI, empty to disable
  localPort: 5060
  localAddress: "0.0.0.0"

rtp:
  portRangeStart: 16384
  portRangeEnd: 16512
  dtmfRelay: true       # RFC 4733 telephone-event

phonePatch:
  mode: "conventional"  # "conventional" | "trunked" (trunked requires dvmhost PR)
  talkgroup: 1          # talkgroup to bridge (conventional mode)
  sourceRID: 9000001    # RID presented for SIP-originated transmissions
  maxCalls: 1           # max simultaneous phone patch calls
  inboundDID: "1000"   # SIP extension that rings for incoming PSTN calls
```

Parse with `YamlDotNet`. Map each top-level section to a dedicated C# config class. Load at startup in `Program.cs` before building the host.

---

## Phase 1 — Conventional full-duplex patch

**Goal:** A radio talking on a configured talkgroup is bridged to a SIP call. Incoming SIP calls are transmitted on that talkgroup. Full-duplex audio. No dvmhost changes needed.

### Task 1 — Project scaffolding

- [ ] Create `dvmsip.sln` and `dvmsip/dvmsip.csproj` targeting `.NET 8`
- [ ] Add submodules (`fnecore`, `dvmvocoder`)
- [ ] Add NuGet packages (`SIPSorcery`, `YamlDotNet`, `Microsoft.Extensions.Hosting`, `Serilog`)
- [ ] Implement `Program.cs`: load config YAML → build `IHost` → run
- [ ] Implement `DvmSipConfig.cs`: nested config classes matching the YAML schema above
- [ ] Add `configs/dvmsip-config.example.yml`
- [ ] Add `tools/start-dvmsip.sh` and `tools/dvmsip-watchdog.sh` (model from dvmhost's tools/)

### Task 2 — dvmvocoder P/Invoke wrapper

Build `libdvmvocoder.so` from the submodule first, then write the C# interop layer.

**Build the native library:**

```bash
cd extern/dvmvocoder
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
make -j$(nproc)
# copy libvocoder.so / libvocoder.dll / libvocoder.dylib next to the dvmsip executable
```

> **Deployment:** `libvocoder` is loaded via standard OS library search (no config path). Place the compiled `libvocoder.dll` / `libvocoder.so` / `libvocoder.dylib` in the same directory as `dvmsip`. There is no `vocoder.libraryPath` config field.

**Implement `Vocoder/ImbeVocoder.cs`:**

```csharp
// Verified P/Invoke signatures from extern/dvmvocoder/vocoder/MBEDecoder.h and MBEEncoder.h
// Library cmake target: libvocoder  →  libvocoder.dll / libvocoder.so / libvocoder.dylib
internal static class NativeVocoder
{
    private const string Lib = "libvocoder";

    // Decoder — mode 1 = DECODE_88BIT_IMBE (P25)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MBEDecoder_Create(int mode);

    // decode: codeword (11 bytes IMBE) → samples (160 × int16); returns error count
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int    MBEDecoder_Decode(IntPtr pDecoder,
        [In]  byte[]  codeword,
        [Out] short[] samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void   MBEDecoder_Delete(IntPtr pDecoder);

    // Encoder — mode 1 = ENCODE_88BIT_IMBE (P25)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MBEEncoder_Create(int mode);

    // encode: samples (160 × int16) → codeword (11 bytes IMBE)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void   MBEEncoder_Encode(IntPtr pEncoder,
        [In]  short[] samples,
        [Out] byte[]  codeword);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void   MBEEncoder_Delete(IntPtr pEncoder);
}

// Mode constants matching MBE_ENCODER_MODE / MBE_DECODER_MODE in the C++ headers
internal enum MbeMode { DmrAmbe = 0, Imbe88Bit = 1 }
```

> **IMBE frame size:** 11 bytes (88 bits). Each frame represents 20 ms of audio (160 PCM samples at 8 kHz).

**Implement `Vocoder/AudioBridge.cs`:**

- Owns one decoder context (`MBEDecoder_Create((int)MbeMode.Imbe88Bit)`) and one encoder context (`MBEEncoder_Create((int)MbeMode.Imbe88Bit)`)
- Exposes two `System.Threading.Channels.Channel<T>`:
  - `ImbeInbound`: receives `byte[11]` IMBE frames from FNE; decodes to `short[160]` PCM; writes to `PcmToSip` channel
  - `PcmInbound`: receives `short[160]` PCM from RTP; encodes to `byte[11]` IMBE; writes to `ImbeToFne` channel
- Runs on a dedicated thread (`Thread` with `IsBackground = false`) — not a Task, to guarantee real-time priority
- Set thread priority to `ThreadPriority.AboveNormal`
- Frame timing: each IMBE frame = 20ms; do not block the codec thread on I/O

### Task 3 — FNE peer connection

**Implement `Fne/SipPeerSystem.cs`:**

Subclass `fnecore.FneSystemBase` directly (confirmed from fnecore source). Instantiate and configure `FnePeer` first, then pass it to the base constructor — matching the pattern in `DVMProject/dvmconsole/PeerSystem.cs`.

```csharp
public class SipPeerSystem : fnecore.FneSystemBase
{
    public FnePeer Peer => (FnePeer)fne;

    public SipPeerSystem(DvmSipConfig config) : base(CreatePeer(config)) { }

    private static FnePeer CreatePeer(DvmSipConfig config)
    {
        var peer = new FnePeer(
            config.System.Identity,
            config.Network.PeerId,
            config.Network.Address,
            config.Network.Port);

        peer.Passphrase = config.Network.Password;
        peer.Information = new PeerInformation
        {
            Details = new PeerDetails
            {
                ConventionalPeer = true,
                Identity = config.System.Identity,
                Software = $"DVMSIP_R{Program.Version}"
            }
        };
        peer.PingTime = 5;

        // Wire logger into Serilog
        peer.Logger = (level, msg) => { /* map fnecore LogLevel → Serilog */ };

        // PeerDisconnected is an Action<uint> delegate (not an abstract override)
        peer.PeerDisconnected = (peerId) => { /* handle disconnect */ };

        return peer;
    }

    // Must override — accept all P25 traffic (talkgroup filtering done in handler)
    protected override bool P25DataValidate(...) => true;
    protected override void P25DataPreprocess(object sender, P25DataReceivedEvent e) { }

    protected override void P25DataReceived(object sender, P25DataReceivedEvent e)
    {
        // Filter to configured talkgroup: if e.DstId != config.PhonePatch.Talkgroup return
        // Only process LDU1 / LDU2 DUIDs (P25DUID.LDU1, P25DUID.LDU2)
        // Extract 9 IMBE frames from the raw FNE payload using the offsets below
        // Forward each byte[11] frame to AudioBridge.ImbeInbound channel
    }

    // Called by CallStateMachine to transmit audio toward radios
    // Builds a full P25 LDU1+LDU2 packet from 18 consecutive IMBE frames and sends via fne.SendMaster
    public void TransmitP25Audio(uint srcId, uint dstId, byte[][] imbeFrames) { }
}
```

**P25 LDU IMBE frame extraction offsets** (verified from `dvmconsole/FneSystemBase.P25.cs`):

| Frame | LDU1 byte offset | LDU2 byte offset |
|-------|-----------------|-----------------|
| Voice 1  | 10  | 10  |
| Voice 2  | 26  | 26  |
| Voice 3  | 55  | 55  |
| Voice 4  | 80  | 80  |
| Voice 5  | 105 | 105 |
| Voice 6  | 130 | 130 |
| Voice 7  | 155 | 155 |
| Voice 8  | 180 | 180 |
| Voice 9  | 204 | 204 |

Each `Buffer.BlockCopy(payload, offset, imbe, 0, 11)` extracts one 11-byte IMBE frame.

Key points:
- Use `fnecore.FnePeer` for the actual UDP connection and handshake — do not reimplement login/auth
- `PeerConnected` is an abstract override (fires via `FneSystemBase`); `PeerDisconnected` is an `Action<uint>` delegate set directly on `FnePeer`
- Filter talkgroup in `P25DataReceived` (fnecore has no server-side talkgroup filter API for peers)
- Log peer state changes at info level via the Serilog-wired `peer.Logger`

### Task 4 — SIP UA and RTP bridge

**Implement `Sip/SipService.cs`:**

```csharp
// Using SIPSorcery
var sipTransport = new SIPTransport();
sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(localAddr, config.Sip.LocalPort)));

var registrar = new SIPRegistrationUserAgent(sipTransport,
    config.Sip.User, config.Sip.Password,
    config.Sip.Registrar, config.Sip.Realm);
registrar.Start();
```

Responsibilities of `SipService`:
- Register the SIP UA on startup; re-register on expiry
- Listen for incoming `SIPUserAgent.IncomingCallReceived` — hand off to `CallStateMachine`
- Expose `PlaceCallAsync(string dialledNumber)` for outbound calls
- Expose `HangupAsync(string callId)` for teardown

**Implement `Sip/RtpBridge.cs`:**

```csharp
// G.711 µ-law encode/decode — implement inline; no external library needed
// µ-law table is public domain and ~256 bytes

public class RtpBridge
{
    // Called per RTP packet received from SIP side (20ms = 160 bytes PCMU)
    public void OnRtpReceived(byte[] pcmuPayload)
    {
        short[] pcm = MuLawDecode(pcmuPayload);   // 160 samples
        _audioBridge.PcmInbound.Writer.TryWrite(pcm);
    }

    // Called per PCM frame from AudioBridge (to send toward SIP)
    public void SendPcmToSip(short[] pcm)
    {
        byte[] pcmu = MuLawEncode(pcm);
        _rtpSession.SendAudioFrame(/* timestamp */, pcmu);
    }
}
```

- Use SIPSorcery's `RTPSession` for RTP send/receive
- Offer only PCMU (PT 0) and telephone-event/101 in SDP — keep it simple for Phase 1
- Wire `RtpSession.OnRtpPacketReceived` → `RtpBridge.OnRtpReceived`
- Wire `AudioBridge.PcmToSip` consumer → `RtpBridge.SendPcmToSip`

### Task 5 — Call state machine

**Implement `CallControl/CallStateMachine.cs`:**

States: `IDLE → ACTIVE → RELEASING → IDLE`

For conventional mode, the state machine is simple:

- **IDLE**: monitoring FNE for voice activity on the configured talkgroup, and monitoring SIP for incoming calls
- **ACTIVE** (radio → SIP): FNE voice stream detected → place outbound SIP call → bridge audio → SIP call ends or radio PTT drops → RELEASING
- **ACTIVE** (SIP → radio): incoming SIP INVITE → answer → transmit audio on FNE talkgroup → SIP hangs up → RELEASING
- **RELEASING**: tear down both sides; wait for audio pipelines to drain; return to IDLE

Implement as a simple `enum` state variable with a dedicated state-transition method — no external state machine library needed at this stage.

```csharp
public enum CallState { Idle, Active, Releasing }

private volatile CallState _state = CallState.Idle;

private void Transition(CallState next, string reason)
{
    Log.Information("Call state {Old} → {New}: {Reason}", _state, next, reason);
    _state = next;
}
```

### Task 6 — End-to-end test (conventional)

Manual test procedure:
1. Start a dvmfne instance and a dvmhost instance configured for a conventional P25 channel on talkgroup 1
2. Start DVMSIP pointing at that FNE; confirm peer registration succeeds in the log
3. Start an Asterisk or FreeSWITCH instance; confirm SIP registration succeeds
4. **Radio → SIP:** Key up a P25 radio on TG 1; confirm an outbound SIP call is placed; answer on a SIP phone; confirm two-way audio
5. **SIP → radio:** Dial the DVMSIP inbound DID from a SIP phone; confirm audio is transmitted on TG 1; confirm P25 radio receives audio
6. Verify DTMF digits are relayed (RFC 4733 telephone-event frames visible in Wireshark)
7. Hang up from both sides; confirm clean teardown with no audio leakage

---

## Phase 2 — Hardening and polish

### Task 7 — Jitter buffer

The FNE delivers P25 LDUs in 180ms bursts (9 × 20ms IMBE frames per LDU). SIP RTP expects 20ms packets. Implement a simple playout queue in `AudioBridge`:

- Hold up to 3 LDUs (540ms) in a circular buffer
- Release one 20ms frame every 20ms on a timer tick (`System.Threading.PeriodicTimer`)
- Discard frames if buffer grows beyond 3 LDUs (network burst absorption limit)
- Target playout latency configurable via `network.jitter` in config (default 360ms)

### Task 8 — Multi-call support

- Allow `phonePatch.maxCalls` simultaneous conventional patches (one per configured talkgroup entry)
- Instantiate one `CallStateMachine` per talkgroup
- Instantiate one vocoder context per concurrent call (one `AudioBridge` per call slot)
- Pool `RtpBridge` instances with port allocation from `rtp.portRangeStart`–`rtp.portRangeEnd`

### Task 9 — DTMF relay

SIPSorcery surfaces RFC 4733 DTMF events via `RTPSession.OnDtmfTone`. Log received digits. For Phase 2, relay them as P25 supplementary data if dvmhost adds support; for now, log only.

### Task 10 — Systemd integration

```ini
# /etc/systemd/system/dvmsip.service
[Unit]
Description=DVM SIP Phone Patch
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/dvmsip -c /etc/dvmsip/dvmsip-config.yml
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Add `install` CMake/dotnet publish targets. Add to `tools/start-dvmsip.sh`.

### Task 11 — Logging and metrics

- Use `Serilog` with rolling file sink; log level controlled by `log.fileLevel` in config
- Log all call events (setup, answer, teardown, duration) at info level with RID and talkgroup
- Optional: expose call count / active call metrics via a simple HTTP endpoint (`Microsoft.AspNetCore.Diagnostics`) consistent with dvmfne's InfluxDB pattern

---

## Phase 3 — Trunked FDMA Phase 1 support

> **Prerequisite:** A PR to `DVMProject/dvmhost` must be merged before Phase 3 can be tested end-to-end.

### dvmhost PR — what to add

The PR touches only the existing P25 trunking control infrastructure. No new files needed — all changes are additive to existing files.

**`src/host/p25/Control.cpp` / `Control.h`**

Add handling for three TSBK opcodes in the control channel receive path:

- `TSBK_IOSP_TEL_INT_VCH_REQ` (0x15) — radio-originated outgoing call request
  - Extract dialed digits (BCD, up to 10 digits) and source RID from the TSBK payload
  - Signal the FNE (or a registered telephone interconnect handler) with the dial request
  - Allocate a voice channel and respond with TSBK 0x1A

- `TSBK_IOSP_TEL_INT_VCH_GRANT` (0x1A) — voice channel grant for telephone interconnect
  - Sent by the site to the requesting radio
  - Carries the assigned channel number and the source/destination RID

- `TSBK_OSP_TEL_INT_ANS_RQST` (0x1B) — incoming call alert to radio
  - Broadcast on the control channel when an inbound SIP call arrives
  - Carries the destination RID of the radio being called

**`src/common/p25/lc/TDULC.cpp` (or equivalent LC class directory)**

Add `LC_TEL_INT_VCH_USER` link control word. This is the LC word embedded in LDU1/LDU2 headers when the voice channel carries a telephone interconnect call. Radios use this to display the correct call type indication.

**`src/host/p25/packet/Voice.cpp`**

When sourcing a voice stream on behalf of a telephone interconnect call (injected by DVMSIP via the FNE), emit `LC_TEL_INT_VCH_USER` in the LDU headers rather than the standard `LC_GROUP` or `LC_PRIVATE` type.

**Testing the dvmhost PR:**

- Unit test: craft a raw TSBK 0x15 frame and verify the handler extracts digits and RID correctly
- Integration test: use a P25 radio to dial a number; observe TSBK 0x1A grant on the control channel in a P25 decoder (e.g. OP25 or Unitrunker)

### DVMSIP Phase 3 tasks

### Task 12 — Trunked call state machine

Extend `CallStateMachine` with trunked states:

```
IDLE → DIALING → RINGING → CONNECTED → RELEASING → IDLE
```

- **DIALING** (radio → SIP): TSBK 0x15 received via FNE → DVMSIP places SIP INVITE → wait for SIP response
- **RINGING**: SIP 180 Ringing received → wait for radio to answer (TSBK 0x1A grant acknowledged by radio)
- **CONNECTED**: SIP 200 OK + radio on channel → full-duplex bridge active
- **RELEASING**: either side hangs up → send SIP BYE + TSBK call termination → drain audio → IDLE

- **DIALING** (SIP → radio): SIP INVITE received → DVMSIP sends TSBK 0x1B (Answer Request) via FNE → wait for radio acknowledgement
- **RINGING**: waiting for radio to key up
- **CONNECTED**: radio keys up on granted channel → full-duplex bridge

### Task 13 — Digit relay from radio to SIP

TSBK 0x15 carries the dialed digits. Extract BCD digits in the TSBK handler and pass them to `SipService.PlaceCallAsync(digits)`. Map the configured dial plan if needed (e.g. prefix a 9 for outside lines).

### Task 14 — Caller ID

For SIP → radio calls, the SIP `From` header contains the caller's number. Relay it via P25 supplementary data if dvmhost adds support. For Phase 3, log it and include it in the TSBK 0x1B payload where the standard permits.

---

## Build and install

```bash
# 1. Build libvocoder native library (platform-specific output)
cd extern/dvmvocoder
mkdir -p build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
make -j$(nproc)
# Result: libvocoder.so (Linux) / libvocoder.dylib (macOS)
# Copy it to the publish output directory alongside dvmsip

# 2. Build and publish dvmsip
cd ../../..
dotnet build dvmsip.sln
dotnet publish dvmsip/dvmsip.csproj -c Release -o /usr/local/bin/dvmsip-publish

# 3. Place native library next to executable (required for P/Invoke resolution)
cp extern/dvmvocoder/build/libvocoder.so /usr/local/bin/dvmsip-publish/

# 4. Install
sudo cp -r /usr/local/bin/dvmsip-publish/. /usr/local/bin/dvmsip/
sudo mkdir -p /etc/dvmsip
sudo cp configs/dvmsip-config.example.yml /etc/dvmsip/dvmsip-config.yml
# edit /etc/dvmsip/dvmsip-config.yml for your site

# 5. Systemd
sudo cp tools/dvmsip.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now dvmsip
```

---

## Key references

| Resource | Location |
|---|---|
| fnecore (FNE peer library) | `extern/fnecore/` — `FnePeer.cs`, `FneSystemBase.cs`, `RtpHeader.cs` |
| dvmvocoder (IMBE library) | `extern/dvmvocoder/vocoder/` — check headers for exact P/Invoke signatures |
| dvmconsole (vocoder P/Invoke reference) | `github.com/DVMProject/dvmconsole` — canonical example of dvmvocoder + fnecore wired together in C# |
| SIPSorcery docs | `github.com/sipsorcery-org/sipsorcery` |
| TIA-102.AABC | P25 TSBK opcode definitions — opcodes 0x15, 0x1A, 0x1B |
| TIA-102.BADA | P25 Telephone Interconnect Interface overview |
| dvmhost P25 trunking | `src/host/p25/Control.cpp` in dvmhost — existing TSBK handler pattern to follow for the PR |

---

## Notes for Claude Code

- **Vocoder P/Invoke signatures are verified.** Library name is `libvocoder` (not `dvmvocoder`). Exact C interop exports are `MBEDecoder_Create/Decode/Delete` and `MBEEncoder_Create/Encode/Delete` with `CallingConvention.Cdecl`. Mode constant for P25 IMBE = `1` (`DECODE_88BIT_IMBE` / `ENCODE_88BIT_IMBE`). IMBE frame = 11 bytes, 160 PCM samples per frame, 20 ms.
- **No vocoder libraryPath config.** `libvocoder.dll`/`.so`/`.dylib` is placed next to the executable. Standard OS library search applies. Do not add a `vocoder` config section.
- **fnecore base class is `fnecore.FneSystemBase`.** `FnePeer` is created first and passed to the base constructor. Wire `peer.Logger` to Serilog. `PeerDisconnected` is an `Action<uint>` delegate on `FnePeer`; `PeerConnected` is an abstract event override on `FneSystemBase`.
- **P25 LDU IMBE offsets are verified.** Frames are at byte offsets 10, 26, 55, 80, 105, 130, 155, 180, 204 in the 216-byte LDU payload.
- **DVM config parity.** Config YAML structure and field names mirror dvmhost/dvmfne style (snake_case, same section names). The `network.local` field is kept for parity but FnePeer does not expose local port binding.
- **Cross-platform target.** .NET 8, targeting win-x64 / linux-x64 / osx-arm64. Native `libvocoder` must be compiled per-platform. No Windows-only or Linux-only APIs in managed code.
- **Conventional mode first.** Phases 1 and 2 require no changes outside this repository. Phase 3 is out of scope until the dvmhost PR is merged.
- **No TDMA/AMBE+2.** DVMProject supports P25 conventional and FDMA Phase 1 only.
- **License:** fnecore is AGPLv3. dvmsip.csproj must declare `<PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>` and include a LICENSE file.
