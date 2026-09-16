// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using dvmsip.Configuration;
using dvmsip.Sip;
using dvmsip.Vocoder;
using fnecore.P25;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace dvmsip.CallControl
{
    /// <summary>
    /// Per-slot call state machine for conventional phone patch mode.
    ///
    /// States:
    ///   IDLE      — monitoring FNE for voice activity, monitoring SIP for inbound INVITEs
    ///   ACTIVE    — full-duplex audio bridge running (either direction)
    ///   RELEASING — tearing down both sides, draining audio pipelines, returning to IDLE
    ///
    /// Direction is tracked on the active PhonePatchCall object.
    /// </summary>
    internal sealed class CallStateMachine : IDisposable
    {
        public enum CallState { Idle, Active, Releasing }

        private volatile CallState _state = CallState.Idle;
        private PhonePatchCall? _call;
        private readonly object _lock = new();

        private readonly int _slotIndex;
        private readonly DvmSipConfig _config;
        private readonly RtpBridge _rtp;
        private readonly AudioBridge _bridge;

        // Set by HostService after all subsystems are wired
        public SipService? SipService        { private get; set; }
        public Fne.SipPeerSystem? PeerSystem { private get; set; }
        public CallSlot? Slot                { private get; set; }

        // State accessors
        public bool IsIdle                => _state == CallState.Idle;
        public bool IsActive              => _state == CallState.Active;
        public bool IsActiveFor(uint dstId) => _state == CallState.Active && _call?.DstId == dstId;

        public CallStateMachine(int slotIndex, DvmSipConfig config, RtpBridge rtp, AudioBridge bridge)
        {
            _slotIndex = slotIndex;
            _config    = config;
            _rtp       = rtp;
            _bridge    = bridge;
        }

        // ---- Events from FNE ----

        /// <summary>
        /// Called by SipPeerSystem each time a P25 LDU arrives on the configured talkgroup.
        /// Triggers an outbound SIP call if we are idle (radio → SIP direction).
        /// </summary>
        public void OnP25AudioReceived(uint srcId, uint dstId, P25DUID duid)
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CallState.Idle:
                        // Radio started talking — place an outbound SIP call
                        _call = new PhonePatchCall { SrcId = srcId, DstId = dstId, Outbound = true };
                        Transition(CallState.Active, $"P25 voice from RID {srcId} on TG {dstId}");
                        Task.Run(() => SipService?.PlaceCallAsync(GetSlot()!, _config.PhonePatch.InboundDid));
                        break;

                    case CallState.Active:
                        // Already bridging — just update the DUID
                        if (_call != null) _call.LastDuid = duid;
                        break;

                    case CallState.Releasing:
                        // Ignore frames during teardown
                        break;
                }
            }
        }

        // ---- Events from SIP ----

        /// <summary>Called when a SIP call is answered (either direction).</summary>
        public void OnSipAnswered(SIPUserAgent ua, RTPSession rtpSession, bool outbound)
        {
            lock (_lock)
            {
                if (_state == CallState.Releasing) return;

                if (_call == null)
                {
                    // Inbound SIP call — radio → SIP was not yet initiated
                    _call = new PhonePatchCall
                    {
                        SrcId    = _config.PhonePatch.SourceRid,
                        DstId    = _config.PhonePatch.Talkgroup,
                        Outbound = false,
                    };
                    Transition(CallState.Active, "Inbound SIP call answered");
                }

                _call.SipUa      = ua;
                _call.RtpSession = rtpSession;

                Log.Information("[Slot {Slot}] Call active — SRC {SrcId} TG {DstId} {Dir}",
                    _slotIndex, _call.SrcId, _call.DstId,
                    outbound ? "radio→SIP" : "SIP→radio");
            }
        }

        public void OnSipFailed()
        {
            lock (_lock)
            {
                Log.Warning("[Slot {Slot}] SIP call failed — returning to IDLE", _slotIndex);
                Teardown("SIP call failed");
            }
        }

        public void OnSipHangup()
        {
            lock (_lock)
            {
                if (_state == CallState.Idle) return;
                Log.Information("[Slot {Slot}] SIP hangup received", _slotIndex);
                Teardown("SIP hangup");
            }
        }

        // ---- Teardown ----

        private void Teardown(string reason)
        {
            if (_state == CallState.Idle) return;
            Transition(CallState.Releasing, reason);

            var call = _call;
            _call = null;

            Task.Run(() =>
            {
                if (call != null)
                {
                    var duration = DateTime.UtcNow - call.StartTime;
                    Log.Information("[Slot {Slot}] Call ended — RID {SrcId} TG {DstId} duration {Dur:g}",
                        _slotIndex, call.SrcId, call.DstId, duration);
                }

                // Brief drain window then return to idle
                Thread.Sleep(100);

                lock (_lock)
                {
                    Transition(CallState.Idle, "teardown complete");
                }
            });
        }

        // ---- Transmit loop: FNE → SIP driven by IMBE-to-PCM pipeline in AudioBridge ----
        // AudioBridge.PcmToSip → RtpBridge.SendLoop handles this automatically.

        // ---- Helpers ----

        private void Transition(CallState next, string reason)
        {
            Log.Information("[Slot {Slot}] Call state {Old} → {New}: {Reason}",
                _slotIndex, _state, next, reason);
            _state = next;
        }

        private CallSlot? GetSlot() => Slot;

        public void Dispose() { /* nothing owned here */ }
    }
}
