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
using fnecore.P25;
using Serilog;

namespace dvmsip.CallControl
{
    /// <summary>
    /// Per-slot call state machine for conventional phone patch mode.
    ///
    /// States:
    ///   IDLE      — monitoring FNE for voice activity, monitoring SIP for inbound INVITEs
    ///   ACTIVE    — call set up or in progress, full-duplex audio bridge running
    ///   RELEASING — tearing down both sides, draining audio pipelines, returning to IDLE
    ///
    /// Direction is tracked on the active PhonePatchCall object.
    /// </summary>
    internal sealed class CallStateMachine : ICallController
    {
        public enum CallState { Idle, Active, Releasing }

        private volatile CallState _state = CallState.Idle;
        private PhonePatchCall? _call;
        private readonly object _lock = new();

        private readonly DvmSipConfig _config;

        public CallSlot Slot { get; }
        public SipService SipService { private get; set; } = null!;
        public Fne.SipPeerSystem PeerSystem { private get; set; } = null!;

        // State accessors
        public bool IsIdle                  => _state == CallState.Idle;
        public bool IsActive                => _state == CallState.Active;
        public bool CanTransmit             => IsActive;
        public bool IsActiveFor(uint dstId) => _state == CallState.Active && _call?.DstId == dstId;

        public TransmitTarget Target =>
            new(_config.PhonePatch.SourceRid, _config.PhonePatch.Talkgroup, P25Defines.LC_GROUP, Individual: false);

        public CallStateMachine(CallSlot slot, DvmSipConfig config)
        {
            Slot    = slot;
            _config = config;
        }

        // ---- Events from FNE ----

        /// <summary>
        /// Called each time a P25 LDU arrives on the configured talkgroup.
        /// Triggers an outbound SIP call if we are idle (radio → SIP direction).
        /// </summary>
        public void OnP25AudioReceived(uint srcId, uint dstId, P25DUID duid)
        {
            lock (_lock)
            {
                if (_state != CallState.Idle)
                    return;

                if (string.IsNullOrEmpty(_config.PhonePatch.OutboundNumber))
                    return; // radio-originated calls disabled

                _call = new PhonePatchCall { SrcId = srcId, DstId = dstId, Outbound = true };
                Transition(CallState.Active, $"P25 voice from RID {srcId} on TG {dstId}");
            }

            Task.Run(async () =>
            {
                if (await SipService.PlaceCallAsync(Slot, _config.PhonePatch.OutboundNumber))
                    OnSipAnswered(outbound: true);
                else
                    OnSipFailed();
            });
        }

        // ---- Events from SIP ----

        public bool TryBeginInbound(string from, string toUser)
        {
            lock (_lock)
            {
                if (_state != CallState.Idle) return false;

                _call = new PhonePatchCall
                {
                    SrcId    = _config.PhonePatch.SourceRid,
                    DstId    = _config.PhonePatch.Talkgroup,
                    Outbound = false,
                };
                Transition(CallState.Active, $"Inbound SIP call from {from}");
                return true;
            }
        }

        public void OnInboundCall(InboundCall call)
        {
            Task.Run(async () =>
            {
                if (await call.AnswerAsync())
                    OnSipAnswered(outbound: false);
                else
                    OnSipFailed();
            });
        }

        public void OnSipHangup()
        {
            lock (_lock)
            {
                Log.Information("[Slot {Slot}] SIP hangup received", Slot.Index);
                Teardown("SIP hangup");
            }
        }

        private void OnSipAnswered(bool outbound)
        {
            lock (_lock)
            {
                if (_state != CallState.Active || _call == null) return;

                Log.Information("[Slot {Slot}] Call active — SRC {SrcId} TG {DstId} {Dir}",
                    Slot.Index, _call.SrcId, _call.DstId, outbound ? "radio→SIP" : "SIP→radio");
            }
        }

        private void OnSipFailed()
        {
            lock (_lock)
            {
                Log.Warning("[Slot {Slot}] SIP call failed — returning to IDLE", Slot.Index);
                Teardown("SIP call failed");
            }
        }

        // ---- Teardown ----

        private void Teardown(string reason)
        {
            if (_state != CallState.Active) return;
            Transition(CallState.Releasing, reason);

            var call = _call;
            _call = null;

            Task.Run(() =>
            {
                try
                {
                    SipService.HangupSlot(Slot);
                    PeerSystem.EndTransmit(Slot);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Slot {Slot}] Error during teardown", Slot.Index);
                }

                if (call != null)
                {
                    var duration = DateTime.UtcNow - call.StartTime;
                    Log.Information("[Slot {Slot}] Call ended — RID {SrcId} TG {DstId} duration {Dur:g}",
                        Slot.Index, call.SrcId, call.DstId, duration);
                }

                lock (_lock)
                {
                    Transition(CallState.Idle, "teardown complete");
                }
            });
        }

        // ---- Helpers ----

        private void Transition(CallState next, string reason)
        {
            Log.Information("[Slot {Slot}] Call state {Old} → {New}: {Reason}",
                Slot.Index, _state, next, reason);
            _state = next;
        }
    }
}
