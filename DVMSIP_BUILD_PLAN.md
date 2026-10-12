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
  outboundNumber: "100" # number dialled when a radio keys up (empty = radio-originated calls disabled)
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

**P25 LDU network framing** (verified against dvmhost `BaseNetwork` and fnecore `Constants.cs`):

An FNE P25 LDU message is a 24-byte header followed by nine DFSI voice records, **not** a raw 216-byte LDU. Total length is 193 bytes for LDU1 and 181 for LDU2; byte 180 is the frame type, written by `CreateP25MessageHdr`. Records are tagged 0x62–0x6A (LDU1) and 0x6B–0x73 (LDU2).

| Record | Offset after header | Length | IMBE offset in record | Absolute IMBE offset | Extra fields (LDU1 / LDU2) |
|--------|-----|----|----|-----|---|
| Voice 1  | 0   | 22 | 10 | 34  | — |
| Voice 2  | 22  | 14 | 1  | 47  | — |
| Voice 3  | 36  | 17 | 5  | 65  | LCO, MFId, service options / MI[0..2] |
| Voice 4  | 53  | 17 | 5  | 82  | dstId (3 bytes) / MI[3..5] |
| Voice 5  | 70  | 17 | 5  | 99  | srcId (3 bytes) / MI[6..8] |
| Voice 6  | 87  | 17 | 5  | 116 | RS / algId, keyId |
| Voice 7  | 104 | 17 | 5  | 133 | RS / RS |
| Voice 8  | 121 | 17 | 5  | 150 | RS / RS |
| Voice 9  | 138 | 16 | 4  | 166 | LSD1, LSD2 |

`payload[23]` = 24 + 154 = 178. The offsets 10, 26, 55, 80, 105, 130, 155, 180, 204 that appear in dvmconsole/dvmhost index a *reconstructed* 225-byte LDU buffer (records copied to 0, 25, 50, 75, …, 200). They are **not** offsets into the network message.

A transmission uses one stream ID, alternates LDU1/LDU2, and ends with a TDU.

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

## Phase 3 — Standards-based Telephone Interconnect (trunked FDMA)

> **Prerequisite:** a PR to `DVMProject/dvmhost` that changes **both the host (`src/host`, `src/common`) and the FNE (`src/fne`)**. Both live in the dvmhost repository. Phase 3 cannot be tested end-to-end until that PR is merged. The current state described below was verified against dvmhost `r05a06_dev` (commit `536997ac`).

### Background — what P25 actually defines

P25 Telephone Interconnect (TIA-102.BADA; signalling in TIA-102.AABC) is an **individual call between one subscriber unit and one telephone party**. It is not a group patch. Phases 1–2 implement a console-style *group* patch (`LC_GROUP` on a talkgroup), which sits outside the P25 interconnect service. Phase 3 implements the real service.

**Signalling** (opcodes verified in fnecore `P25Defines.cs` and dvmhost `common/p25/P25Defines.h`; the earlier draft's 0x15/0x1A/0x1B were wrong):

| Opcode | Dir | Name | Purpose |
|---|---|---|---|
| 0x08 | ISP | `TELE_INT_DIAL_REQ` | Radio requests a call, **explicit dialing** — carries the dialed digits |
| 0x09 | ISP | `TELE_INT_PSTN_REQ` | Radio requests a call, **implicit dialing** — carries an index into a preset number table held by the infrastructure |
| 0x08 | OSP | `TELE_INT_DIAL_GRANT` | Voice channel grant for an interconnect call (includes a call timer) |
| 0x0A | OSP | `TELE_INT_ANS_REQ` | Alerts (rings) a radio for an inbound phone call |
| 0x0A | ISP | `TELE_INT_ANS_RSP` | Radio's answer: proceed / deny / wait |
| 0x23 | ISP | `CAN_SRV_REQ` | Radio cancels / ends the call |
| 0x27 / 0x33 | OSP | `DENY_RSP` / `QUE_RSP` | Request refused / queued |

**Link control on the voice channel:**
- `LC_TEL_INT_VCH_USER` (0x06) — carries a **single unit address** (the radio) plus the call timer.
- `LC_TEL_INT_ANS_RQST` (0x07) — answer request for a unit that is already on a voice channel.
- `LC_CALL_TERM` (0x0F) — call termination.

> **Pitfall:** LCO 0x06 under `MFG_MOT` means *Motorola in-band GPS data*. Interconnect frames must use the standard MFId (0x00).

> Verify exact TSBK field layouts (digit packing, call timer units, answer-response codes) against TIA-102.AABC when implementing. The table above fixes opcodes and directions only.

**Call flows:**
- **Radio → phone:**
  1. The SU sends 0x08 (digits) or 0x09 (preset index).
  2. The system authorises the SU and places the call. It may deny or queue the request.
  3. The system grants a voice channel (0x08 OSP).
  4. Ringback and call-progress tones reach the radio as voice audio.
- **Phone → radio:**
  1. The caller dials a DID.
  2. The DID is mapped to a radio RID.
  3. The system sends 0x0A OSP and the radio rings.
  4. The radio answers with 0x0A ISP.
  5. The system grants a voice channel.
- **During the call:** the radio remains half-duplex PTT. The infrastructure sends phone audio to the radio only while the radio is not transmitting. The voice channel is held for the whole call (message trunking).
- **Ending the call:** any of `CAN_SRV_REQ`, a phone-side hangup, or call timer expiry ends the call. The system then sends a TDULC carrying `LC_CALL_TERM` and releases the channel.

### Network addressing convention (decided)

`LC_TEL_INT_VCH_USER` carries **one** address, but the 24-byte network header has both `srcId` and `dstId`. On dvmhost dev, grants, voice-channel permits and FNE call tracking are all keyed on `dstId`. Interconnect calls therefore use this convention:

| Leg | Header LCO | `srcId` | `dstId` |
|---|---|---|---|
| radio → phone (sourced by the site) | 0x06 | SU RID | **SU RID** |
| phone → radio (sourced by DVMSIP) | 0x06 | gateway `sourceRID` | **SU RID** |
| grant demand TDU from DVMSIP | 0x06 | gateway `sourceRID` | SU RID |

- The call is always identified by the SU's RID.
- The FNE determines direction from the **source peer**: is it a gateway peer or not. It does not use the IDs for this.
- This replaces the earlier "dst = gateway ID" draft. With this rule the existing unit-to-unit code paths can mostly be reused.

### dvmhost PR — file-level change list

Verified against `r05a06_dev` (commit `536997ac`). Line numbers are approximate guides for that commit.

#### 1. Common (`src/common`)

**TSBK classes and factory** (`common/p25/lc/tsbk/`, `TSBKFactory.cpp` ~L167):
- The standard decode switch has **no** interconnect cases. 0x08, 0x09 and **0x0A** all return `nullptr` ("unknown TSBK LCO").
- Consequence: the `IOSP_TELE_INT_ANS` handler in `ControlSignaling.cpp` (~L313) is unreachable today.
- **Done on `feature/p25-tele-int`:**
  - `OSP_TELE_INT_VCH_GRANT` (0x08 OSP: channel, call timer in 100 ms units, SU address)
  - `ISP_TELE_INT_PSTN_REQ` (0x09 ISP: preset index, SU address)
  - `IOSP_TELE_INT_ANS` (0x0A: ISP answer response / OSP answer request with 10 BCD digits, where 0xF escapes and pads)
  - the three classes registered in `TSBKFactory`, with layout tests in `tests/p25/TSBK_TeleInt_Tests.cpp`
  - Field layouts were cross-checked against SDRTrunk's decoders.
- **Explicit dialing (0x08 ISP) is an unconfirmed multi-block (UMBT) message, not a single TSBK.** dvmhost cannot receive it yet:
  - `host/p25/packet/Data.cpp` (~L121) drops non-AMBT PDUs on dedicated control channels.
  - `TSBKFactory::createAMBT` rejects non-AMBT formats.
  - Supporting it is a separate step. Implicit dialing (0x09) works without it.
- The Motorola remap list (~L118) already includes 0x0A. Whether Motorola SUs also send 0x08/0x09 with the MOT MFId needs a capture to confirm.

**Grant request wire format** (`common/network/BaseNetwork.cpp` `writeGrantReq` ~L105):
- Byte 19 carries the slot number plus `0x80` = U2U.
- Add an interconnect flag (e.g. `0x40`), or replace the `unitToUnit` bool with a service-type value.
- Update the parser in `fne/network/packet/GrantRequest.cpp` (~L58).

**Gateway peer flag** (`common/lookups/PeerListLookup.h` ~L174): add a `telephoneInterconnect` property next to `canIssueInhibit`, with parsing in the peer list file loader.

**Optional:** a per-RID interconnect permission in the RID ACL.

#### 2. Host (`src/host/p25`)

**`ControlSignaling::process`** (~L216):
- **New 0x08 / 0x09 ISP handling:** `IS_SUPPORT_CONTROL_CHECK`, `VALID_SRCID`, `VERIFY_SRCID_REG`. ACK or queue the SU, then forward the TSBK to the network. Do **not** grant yet; the grant follows the gateway's grant demand.
- **0x0A ISP:** replace the `DENY_SYS_UNSUPPORTED_SVC` at ~L313–322 with forwarding to the network. Mirror the U2U deny/queue handling for `ANS_DENY` / `ANS_WAIT` (~L293–310).
- **`ISP_CAN_SRV_REQ`** (~L468): if the SU holds an interconnect grant, release it and forward the cancel.

**`ControlSignaling::processNetwork`** (~L838):
- 0x0A OSP from a gateway: transmit to air only if the target SU is registered at this site.
- Interconnect ISPs (0x08, 0x09, 0x0A ISP) arriving from the network: `return true`, so they are never written to air.

**`ControlSignaling::writeRF_TSDU_Grant`** (~L2150) takes `bool grp`. Change it to a three-way grant type (group / U2U / interconnect). The interconnect path:
- keys the grant on the SU RID;
- skips talkgroup checks;
- requires `isUnitReg(SU)` (as U2U does, ~L2255);
- reuses the U2U `RPC_PERMIT_P25_TG` voice-channel permit (~L2448–2489);
- transmits the 0x08 OSP grant instead of `IOSP_UU_VCH` (~L2523);
- uses the interconnect service opcode in its denials.

**Network grant demand** (`Control.cpp` ~L1642–1700):
- `validateTGId(dstId)` at ~L1670 rejects a RID whenever the TG ACL is enforced.
- When the header LCO is 0x06, validate `dstId` as a RID and grant as interconnect.
- The same TG check exists in the explicit-TDU release path (~L1716).

**Voice channel, RF-sourced** (`packet/Voice.cpp` ~L250–320):
- `LC::decodeLC` for 0x06 (`common/p25/lc/LC.cpp` ~L1112) sets only `srcId` and `callTimer`, so `dstId == 0`.
- The SU is then rejected twice: by the target-RID check (~L305) and by the `m_permittedDstId` check (~L162, ~L619).
- **Done in step 5**, in the common LC classes so every decode site picks it up:
  - Decoding 0x06 sets `dstId` to the single address (the SU) and `m_group = false`.
  - Encoding 0x06 puts `dstId` (falling back to `srcId`) in the address field.
  - Two pre-existing encoder bugs were fixed:
    - `LC::encodeLC` was missing the reserved octet, so service options landed in octet 3.
    - The `LC_TEL_INT_VCH_USER` TDULC wrote the unit-to-unit layout.
  - Layouts were verified against SDRTrunk. Test: `TDULC_Tests` "TelInt_Encode_Decode_Layout".
- **Voice channel permit:** the control channel's permit RPC carries `"telInt": true`. The voice channel then keeps that permit through TG hang expiry, so pauses between bursts don't drop the call. It is cleared only by the control channel's unpermit.
- **RF header (HDU):** an interconnect HDU with no talkgroup is attributed to the unit permitted on the channel.

**Voice channel, network-sourced** (`Voice.cpp` `writeNet_LDU1` ~L1795–1850):
- Non-group handling already validates `dstId` as a RID (~L1885).
- `LC::encodeLC` for 0x06 (~L1236) puts **`m_srcId`** in the single address field, and the DFSI network records carry no call timer. As written, the air LC would show the gateway's `sourceRID` with a call timer of 0.
- Fix: put `dstId` (the SU) in the air LC address and set the call timer from the grant.
- Force the standard MFId: under `MFG_MOT`, 0x06 means in-band GPS (`P25Defines.h` ~L752).

**Later:**
- embed `LC_TEL_INT_ANS_RQST` for units already on a voice channel;
- send TDULC `LC_CALL_TERM` on cancel, hangup, or call-timer expiry.

#### 3. FNE (`src/fne/network/callhandler/TagP25Data.cpp`)

- **`validate()`:** treat LCO 0x06 like `PRIVATE`, checking the RID ACL on `dstId` instead of talkgroup rules (today it is dropped with ICC `REJECT_TRAFFIC`).
- **`isPeerPermitted()`:** permit 0x06 traffic only to gateway peers when the source is a site, and only to `findPeerUnitReg(dstId)` when the source is a gateway.
- **Call start and the repeat loop** (~L580, ~L656–760): extend the `lco == LCO::PRIVATE` branches to cover 0x06 with the direction rule above.
  - Apply the restriction **always**, not only when `m_restrictPVCallToRegOnly` is set; otherwise phone audio reaches every peer.
- **TSDU routing:** today `tsbk` is `nullptr` for interconnect opcodes, so they pass `validate()` and are **repeated to every peer**, dialed digits included. Once the factory decodes them:
  - send radio-originated interconnect ISPs (0x08, 0x09, 0x0A) only to gateway peers;
  - send a gateway's 0x0A OSP only to the peer where the target SU is registered.
- **`processGrantReq()`** (~L846): add an interconnect branch. The current code rejects any `dstId` that is not a valid, active talkgroup.
- Phase 2 TDMA (`TagP25P2Data.cpp`) already passes `TEL_INT_VCH_USER` through for late entry. Interconnect over TDMA is out of scope; leave it unchanged.

#### Implementation order

1. TSBK classes and factory.
2. Control-channel request handling and forwarding.
3. FNE routing and gateway peer flag.
4. Grants.
5. Voice-channel address and call timer fixes.
6. DVMSIP (tasks below).

Steps 1–3 can be tested with synthetic gateway traffic before any RF testing.

**Testing the dvmhost PR:**
- Unit tests: craft raw 0x08, 0x09 and 0x0A TSBKs and verify decode/encode round-trips (digits, preset index, answer code).
- FNE: confirm an LCO 0x06 stream from the gateway reaches only the registering site, and that a 0x08 ISP reaches only the gateway peer.
- RF: dial from a P25 radio and observe the 0x08 OSP grant and LCO 0x06 on the voice channel in a P25 decoder (e.g. OP25).

### DVMSIP Phase 3 tasks

> **Status: implemented, untested against a live system.** Set `phonePatch.mode: trunked` to enable.
>
> **Code:**
> - `CallControl/InterconnectCall.cs`: per-slot controller (states, keepalive, alerting, teardown).
> - `CallControl/InterconnectManager.cs`: TSBK and voice routing by RID.
> - `Fne/P25Tsbk.cs`, `Fne/P25Tdulc.cs`: wire formats.
> - `Vocoder/AudioBridge.cs`: VOX.
> - Conventional mode uses the same `ICallController` / `IP25CallRouter` seams (`CallStateMachine`, `ConventionalCallRouter`).
>
> **Tests:** `tests/dvmsip.Tests`. The TDULC's RS(24,12,13) output (fnecore `ReedSolomonAlgorithm`) is checked against a reference built from dvmhost's own matrix.
>
> **Implementation notes:**
> - **Call end:** uses an LC_CALL_TERM **TDULC**. The DVM `OSP_DVM_LC_CALL_TERM` TSBK cannot be used: its target field is 16 bits, and RIDs are 24 bits.
> - **fnecore usage:**
>   - `TSBKBase.Decode` copies the payload from byte 1 (the MFId) instead of byte 2, so DVMSIP parses raw TSBKs itself.
>   - fnecore has no TDULC framing. DVMSIP builds the TDULC from fnecore's FEC primitives: `ReedSolomonAlgorithm` for RS(24,12,13), plus its Golay, interleaver, Trellis and CRC classes.
> - **Radio → phone:** the grant is demanded once the SIP party answers, so there is no ringback on the radio yet (no SIP early media).
> - **Floor control:**
>   - The transmit gate closes while the unit is keyed (LDUs seen within the last second, until its terminator).
>   - An FNE in-call-control `REJECT_TRAFFIC` also closes it.
> - **Not yet supported:** explicit dialing (multi-block ISP).

#### Task 12 — Per-unit interconnect state machine

Calls are keyed by **RID**, not talkgroup:

```
IDLE → DIALING → RINGING → CONNECTED → RELEASING → IDLE
```

- **Radio → phone:**
  - **DIALING:** 0x08 or 0x09 ISP arrives via the FNE. Resolve the number and send a SIP INVITE.
  - **RINGING:** SIP 18x arrives. Ringback audio is passed to the radio once a channel is granted.
  - **CONNECTED:** SIP 200 OK arrives. Request the grant and bridge audio.
- **Phone → radio:**
  - **DIALING:** SIP INVITE arrives. Map the DID to a RID (new config map) and send 0x0A OSP via the FNE. Answer the INVITE with SIP 180.
  - **RINGING:** wait for 0x0A ISP:
    - proceed → SIP 200 and grant, then CONNECTED
    - deny → SIP 486 (Busy Here)
    - wait → stay in RINGING
  - No answer before a configurable timeout → SIP 408 (Request Timeout) or 480 (Temporarily Unavailable).
- **RELEASING:** triggered by SIP BYE, `CAN_SRV_REQ`, or call timer expiry. Send `LC_CALL_TERM` and a SIP BYE, drain audio, return to IDLE.

#### Task 13 — Dialing

- **Explicit dialing:** convert the digits from 0x08 ISP into the SIP request URI, applying a configurable dial plan (prefixes, allowed patterns).
- **Implicit dialing:** look up the 0x09 preset index in a new `phonePatch.presetNumbers` config table.
- Deny unknown indices and disallowed numbers back to the SU.

#### Task 14 — Caller ID

Log the SIP `From` number. Include it in 0x0A OSP only if the TIA-102.AABC field layout provides for it; otherwise log only.

#### Task 15 — Half-duplex turnaround

Required for standards behaviour, and also recommended for the Phase 1–2 conventional patch:
- Gate SIP → radio audio with voice activity detection (VOX) on the PCM stream.
- Transmit only while the phone party is speaking **and** no radio is keyed.
- The FNE enforces this: both legs share `dstId` (the SU RID), so its call-collision logic rejects a gateway stream that starts while the radio's stream is still active. Wait for the radio's terminator before keying.
- End each burst with a TDU.
- Radio → SIP audio flows whenever the radio is keyed.

#### Task 16 — Wire format

Follow the network addressing convention above:
- **Receive trunking messages:** `P25DataReceived` must accept TSDUs (it currently drops everything except LDUs) and parse 0x08 / 0x09 / 0x0A from the raw TSBK. fnecore has no classes for these.
- **Ring a radio:** send 0x0A OSP with `FneSystemBase.SendP25TSBK`.
- **Request a channel:** send a TDU with grant demand set (control byte `0x80`), header LCO 0x06, `srcId` = `sourceRID`, `dstId` = SU RID.
- **Keep the grant alive:** the control channel holds interconnect grants for the whole call (message trunking) and does not release them on per-burst TDUs. Re-send the grant-demand TDU every few seconds during the call; the control channel's grant timer is 15 s (`GRANT_TIMER_TIMEOUT`).
- **Deny a request:** send the grant-demand TDU with grant denial also set (control byte `0xC0`). The control channel sends the SU a deny (`DENY_TGT_UNIT_REFUSED`, service 0x08). Use this when the SIP call fails or the number is not allowed.
- **End the call:** send a TDULC carrying `LC_CALL_TERM` with header LCO 0x06. That TDULC is what releases the grant on the control channel.
- **Limits of the current host implementation:**
  - Explicit-channel (multi-block grant) sites refuse interconnect grants.
  - The grant's call timer is 0 (no timer).
- **Voice streams:**
  - LCO 0x06 with the standard MFId; `srcId` = `sourceRID`, `dstId` = SU RID
  - one stream ID per burst
  - alternating LDU1/LDU2
  - a terminating TDU per burst, and a TDULC `LC_CALL_TERM` when the call ends

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
| TIA-102.AABC | P25 TSBK definitions — telephone interconnect opcodes 0x08 (dial req / grant), 0x09 (PSTN req), 0x0A (answer req / rsp) |
| TIA-102.BADA | P25 Telephone Interconnect requirements and definitions |
| dvmhost control channel | `src/host/p25/packet/ControlSignaling.cpp` — TSBK handlers; the U2U (`IOSP_UU_VCH` / `IOSP_UU_ANS`) flow is the closest pattern to follow |
| dvmhost TSBK classes | `src/common/p25/lc/tsbk/` + `TSBKFactory.cpp` |
| dvmfne P25 routing | `src/fne/network/callhandler/TagP25Data.cpp` — `isPeerPermitted()`, `validate()`, private-call routing, `processGrantReq()` |

---

## Notes for Claude Code

- **Vocoder P/Invoke signatures are verified.** Library name is `libvocoder` (not `dvmvocoder`). Exact C interop exports are `MBEDecoder_Create/Decode/Delete` and `MBEEncoder_Create/Encode/Delete` with `CallingConvention.Cdecl`. Mode constant for P25 IMBE = `1` (`DECODE_88BIT_IMBE` / `ENCODE_88BIT_IMBE`). IMBE frame = 11 bytes, 160 PCM samples per frame, 20 ms.
- **No vocoder libraryPath config.** `libvocoder.dll`/`.so`/`.dylib` is placed next to the executable. Standard OS library search applies. Do not add a `vocoder` config section.
- **fnecore base class is `fnecore.FneSystemBase`.** `FnePeer` is created first and passed to the base constructor. Wire `peer.Logger` to Serilog. `PeerDisconnected` is an `Action<uint>` delegate on `FnePeer`; `PeerConnected` is an abstract event override on `FneSystemBase`.
- **P25 LDU network framing is DFSI, not a raw LDU.** FNE messages are a 24-byte header + nine DFSI voice records (see the Task 3 table). Absolute IMBE offsets in the network message are 34, 47, 65, 82, 99, 116, 133, 150, 166. The often-quoted 10, 26, 55, … 204 index a reconstructed 225-byte buffer, not the message.
- **DVM config parity.** Config YAML structure and field names mirror dvmhost/dvmfne style (camelCase keys, same section names). The `network.local` field is kept for parity but FnePeer does not expose local port binding.
- **Cross-platform target.** .NET 8, targeting win-x64 / linux-x64 / osx-arm64. Native `libvocoder` must be compiled per-platform. No Windows-only or Linux-only APIs in managed code.
- **Conventional mode first.** Phases 1 and 2 require no code changes outside this repository. They do need FNE *configuration*: the talkgroup present and active in talkgroup rules, DVMSIP's peer ID permitted, and `sourceRID` enabled if the FNE rejects unknown RIDs.
- **Phase 3 needs host *and* FNE changes** (both in the dvmhost repo). Out of scope here until that PR is merged.
- **FDMA only for DVMSIP.** dvmhost `r05a06_dev` adds P25 Phase 2 TDMA (`host/p25/phase2`, FNE `TagP25P2Data`), but DVMSIP bridges Phase 1 FDMA LDUs / IMBE only. Interconnect over TDMA (AMBE+2) is out of scope.
- **License:** fnecore is AGPLv3. dvmsip.csproj must declare `<PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>` and include a LICENSE file.
