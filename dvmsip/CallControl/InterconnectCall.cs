// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Configuration;
using dvmsip.Fne;
using dvmsip.Sip;
using fnecore.P25;
using Serilog;
using SIPSorcery.SIP;

namespace dvmsip.CallControl
{
    /// <summary>
    /// Trunked mode: a P25 telephone interconnect call between one subscriber unit and a SIP party.
    ///
    ///   IDLE → DIALING   → CONNECTED → RELEASING → IDLE   (radio → phone, TELE INT PSTN REQ)
    ///   IDLE → ALERTING  → CONNECTED → RELEASING → IDLE   (phone → radio, TELE INT ANS REQ/RSP)
    ///
    /// The control channel holds the voice channel grant for the whole call; this side keeps it alive with
    /// repeated grant demands and ends it with an LC_CALL_TERM TDULC.
    /// </summary>
    internal sealed class InterconnectCall : ICallController
    {
        public enum CallState { Idle, Dialing, Alerting, Connected, Releasing }

        private static readonly TimeSpan GrantKeepAliveInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AnswerRequestInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RadioKeyedTimeout = TimeSpan.FromSeconds(1);

        private readonly object _lock = new();
        private readonly DvmSipConfig _config;
        private readonly Func<uint, bool> _unitBusy;

        private volatile CallState _state = CallState.Idle;
        private uint _rid;
        private bool _outbound;
        private InboundCall? _inbound;
        private DateTime _startTime;
        private DateTime _alertDeadline;
        private Timer? _timer;

        private bool _radioKeyed;
        private DateTime _lastRadioActivity;

        public CallSlot Slot { get; }
        public SipService SipService { private get; set; } = null!;
        public SipPeerSystem PeerSystem { private get; set; } = null!;

        public uint Rid => _rid;
        public CallState State => _state;
        public bool IsIdle => _state == CallState.Idle;

        public bool CanTransmit => _state == CallState.Connected && !RadioKeyed;

        public TransmitTarget Target =>
            new(_config.PhonePatch.SourceRid, _rid, P25Defines.LC_TEL_INT_VCH_USER, Individual: true);

        private bool RadioKeyed
        {
            get
            {
                lock (_lock)
                    return _radioKeyed && (DateTime.UtcNow - _lastRadioActivity) < RadioKeyedTimeout;
            }
        }

        public InterconnectCall(CallSlot slot, DvmSipConfig config, Func<uint, bool> unitBusy)
        {
            Slot     = slot;
            _config  = config;
            _unitBusy = unitBusy;
        }

        // ---- Radio → phone ----

        /// <summary>Claims this slot for a radio-originated call and dials <paramref name="number"/>.</summary>
        public bool TryBeginOutbound(uint rid, string number)
        {
            lock (_lock)
            {
                if (_state != CallState.Idle) return false;

                Begin(rid, outbound: true);
                Transition(CallState.Dialing, $"telephone interconnect request from RID {rid} to {number}");
            }

            Task.Run(async () =>
            {
                bool connected = await SipService.PlaceCallAsync(Slot, number);
                lock (_lock)
                {
                    if (_state != CallState.Dialing) return;

                    if (connected)
                        Connect();
                    else
                        Teardown("SIP call failed");
                }
            });

            return true;
        }

        // ---- Phone → radio ----

        public bool TryBeginInbound(string from, string toUser)
        {
            if (!_config.PhonePatch.DidMap.TryGetValue(toUser, out uint rid))
                return false;

            if (_unitBusy(rid))
                return false;

            lock (_lock)
            {
                if (_state != CallState.Idle) return false;

                Begin(rid, outbound: false);
                Transition(CallState.Alerting, $"inbound SIP call from {from} to RID {rid}");
                return true;
            }
        }

        public void OnInboundCall(InboundCall call)
        {
            lock (_lock)
            {
                if (_state != CallState.Alerting) return;

                _inbound = call;
                _alertDeadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.PhonePatch.AnswerTimeout));
                call.Ringing();

                PeerSystem.SendAnswerRequest(_rid, call.CallerNumber);
                _timer = new Timer(_ => OnAlertTimer(), null, AnswerRequestInterval, AnswerRequestInterval);
            }
        }

        /// <summary>TELE INT ANS RSP from the unit.</summary>
        public void OnAnswerResponse(byte response)
        {
            lock (_lock)
            {
                if (_state != CallState.Alerting || _inbound == null) return;

                switch (response)
                {
                    case P25Tsbk.AnswerProceed:
                        Log.Information("[Slot {Slot}] RID {Rid} answered", Slot.Index, _rid);
                        StopTimer();

                        var call = _inbound;
                        Task.Run(async () =>
                        {
                            bool answered = await call.AnswerAsync();
                            lock (_lock)
                            {
                                if (_state != CallState.Alerting) return;

                                if (answered)
                                    Connect();
                                else
                                    Teardown("SIP answer failed");
                            }
                        });
                        break;

                    case P25Tsbk.AnswerDeny:
                        Log.Information("[Slot {Slot}] RID {Rid} refused the call", Slot.Index, _rid);
                        _inbound.Reject(SIPResponseStatusCodesEnum.BusyHere);
                        _inbound = null;
                        Teardown("unit refused");
                        break;

                    case P25Tsbk.AnswerWait:
                        Log.Information("[Slot {Slot}] RID {Rid} asked the caller to wait", Slot.Index, _rid);
                        break;

                    default:
                        Log.Warning("[Slot {Slot}] RID {Rid} unknown answer response ${Response:X2}", Slot.Index, _rid, response);
                        break;
                }
            }
        }

        private void OnAlertTimer()
        {
            lock (_lock)
            {
                if (_state != CallState.Alerting || _inbound == null) return;

                if (DateTime.UtcNow >= _alertDeadline)
                {
                    Log.Information("[Slot {Slot}] RID {Rid} did not answer", Slot.Index, _rid);
                    _inbound.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable);
                    _inbound = null;
                    Teardown("answer timeout");
                    return;
                }

                PeerSystem.SendAnswerRequest(_rid, _inbound.CallerNumber);
            }
        }

        // ---- Radio activity ----

        /// <summary>A voice frame from the unit; the unit has the floor (half-duplex).</summary>
        public void OnRadioVoice()
        {
            lock (_lock)
            {
                _radioKeyed = true;
                _lastRadioActivity = DateTime.UtcNow;
            }
        }

        public void OnRadioTerminator()
        {
            lock (_lock)
                _radioKeyed = false;
        }

        /// <summary>The FNE rejected our transmission, most likely because the unit keyed up.</summary>
        public void OnTrafficRejected()
        {
            OnRadioVoice();
            PeerSystem.EndTransmit(Slot);
        }

        public void OnRadioCancel()
        {
            lock (_lock)
            {
                Log.Information("[Slot {Slot}] RID {Rid} cancelled the call", Slot.Index, _rid);
                Teardown("unit cancel");
            }
        }

        public void OnSipHangup()
        {
            lock (_lock)
            {
                if (_state == CallState.Idle || _state == CallState.Releasing) return;

                Log.Information("[Slot {Slot}] SIP hangup for RID {Rid}", Slot.Index, _rid);
                _inbound = null; // a ringing caller that hangs up has already cancelled its INVITE
                Teardown("SIP hangup");
            }
        }

        // ---- State helpers (call with _lock held) ----

        private void Begin(uint rid, bool outbound)
        {
            _rid = rid;
            _outbound = outbound;
            _inbound = null;
            _radioKeyed = false;
            _startTime = DateTime.UtcNow;
        }

        private void Connect()
        {
            Transition(CallState.Connected, _outbound ? "SIP party answered" : "unit answered");

            PeerSystem.SendGrantDemand(_rid);
            _timer = new Timer(_ => KeepAlive(), null, GrantKeepAliveInterval, GrantKeepAliveInterval);
        }

        private void KeepAlive()
        {
            lock (_lock)
            {
                if (_state == CallState.Connected)
                    PeerSystem.SendGrantDemand(_rid);
            }
        }

        private void Teardown(string reason)
        {
            if (_state == CallState.Idle || _state == CallState.Releasing) return;

            var previous = _state;
            uint rid = _rid;
            var unanswered = (previous == CallState.Alerting) ? _inbound : null;
            Transition(CallState.Releasing, reason);
            StopTimer();

            Task.Run(() =>
            {
                try
                {
                    unanswered?.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable);
                    SipService.HangupSlot(Slot);
                    PeerSystem.EndTransmit(Slot);

                    if (previous == CallState.Connected)
                        PeerSystem.SendCallTerm(rid);       // releases the voice channel grant
                    else if (previous == CallState.Dialing)
                        PeerSystem.SendGrantDenial(rid);    // tells the unit the call could not be placed
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Slot {Slot}] Error during teardown", Slot.Index);
                }

                Log.Information("[Slot {Slot}] Telephone interconnect call ended — RID {Rid} {Dir} duration {Dur:g}",
                    Slot.Index, rid, _outbound ? "radio→SIP" : "SIP→radio", DateTime.UtcNow - _startTime);

                lock (_lock)
                {
                    _inbound = null;
                    _rid = 0;
                    Transition(CallState.Idle, "teardown complete");
                }
            });
        }

        private void StopTimer()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void Transition(CallState next, string reason)
        {
            Log.Information("[Slot {Slot}] Interconnect state {Old} → {New}: {Reason}", Slot.Index, _state, next, reason);
            _state = next;
        }
    }
}
