// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Net;
using dvmsip.CallControl;
using dvmsip.Configuration;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace dvmsip.Sip
{
    /// <summary>
    /// Manages SIP registration, inbound call acceptance and outbound dialling.
    /// Each call slot owns its own SIPUserAgent (a SIPSorcery UA carries one dialog at a time);
    /// a separate listener UA receives new INVITEs and hands them to the slot whose controller claims them.
    /// Offers only PCMU (PT 0) in SDP.
    /// </summary>
    internal sealed class SipService : IDisposable
    {
        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;
        private readonly Dictionary<CallSlot, SIPUserAgent> _slotUas = new();

        private SIPTransport? _transport;
        private SIPUserAgent? _listener;
        private SIPRegistrationUserAgent? _registrar;
        private IPAddress? _rtpBindAddress;
        private bool _disposed;

        public SipService(DvmSipConfig config, IReadOnlyList<CallSlot> slots)
        {
            _config = config;
            _slots  = slots;
        }

        public void Start()
        {
            var localAddr = IPAddress.TryParse(_config.Sip.LocalAddress, out var addr)
                ? addr : IPAddress.Any;
            _rtpBindAddress = localAddr.Equals(IPAddress.Any) ? null : localAddr;

            _transport = new SIPTransport();
            _transport.AddSIPChannel(new SIPUDPChannel(
                new IPEndPoint(localAddr, _config.Sip.LocalPort)));

            _listener = new SIPUserAgent(_transport, null);
            _listener.OnIncomingCall += OnIncomingCall;

            foreach (var slot in _slots)
            {
                var ua = new SIPUserAgent(_transport, null);
                var capturedSlot = slot;
                ua.OnCallHungup += _ => capturedSlot.Controller.OnSipHangup();
                ua.OnDtmfTone   += (tone, duration) =>
                    Log.Information("[SIP] DTMF tone {Tone} received ({Duration}ms)", tone, duration);
                _slotUas[slot] = ua;
            }

            // Constructor: transport, user, password, server, expiry,
            //   maxAttemptTimeout, failureRetryInterval, maxAttempts, exitOnFailure, usernameInContact
            _registrar = new SIPRegistrationUserAgent(
                _transport,
                _config.Sip.User,
                _config.Sip.Password,
                _config.Sip.Registrar,
                60,    // expiry (seconds)
                30,    // maxRegistrationAttemptTimeout
                30,    // registerFailureRetryInterval
                3,     // maxRegisterAttempts
                true,  // exitOnUnequivocalFailure
                false  // sendUsernameInContactHeader
            );
            _registrar.RegistrationSuccessful += (_, _) =>
                Log.Information("[SIP] Registration successful with {Registrar}", _config.Sip.Registrar);
            _registrar.RegistrationFailed += (_, reason, _) =>
                Log.Warning("[SIP] Registration failed: {Reason}", reason);
            _registrar.Start();

            Log.Information("[SIP] Transport listening on {Addr}:{Port}", localAddr, _config.Sip.LocalPort);
        }

        public void Stop()
        {
            foreach (var ua in _slotUas.Values)
                if (ua.IsCallActive) ua.Hangup();

            _registrar?.Stop();
            _transport?.Shutdown();
        }

        // ---- Inbound calls ----

        private void OnIncomingCall(SIPUserAgent listener, SIPRequest req)
        {
            string from = req.Header.From?.FromURI?.ToString() ?? "unknown";
            string callerNumber = req.Header.From?.FromURI?.User ?? "";
            string toUser = req.URI?.User ?? "";
            Log.Information("[SIP] Incoming call from {From} to {To}", from, toUser);

            CallSlot? slot = null;
            foreach (var s in _slots)
            {
                if (s.Controller.TryBeginInbound(from, toUser)) { slot = s; break; }
            }

            if (slot == null)
            {
                Log.Warning("[SIP] No call slot can take the inbound call to {To} — rejecting", toUser);
                listener.AcceptCall(req).Reject(SIPResponseStatusCodesEnum.BusyHere, null);
                return;
            }

            var ua  = _slotUas[slot];
            var uas = ua.AcceptCall(req);
            uas.CallCancelled += (_, _) => slot.Controller.OnSipHangup();

            slot.Controller.OnInboundCall(new InboundCall(this, slot, ua, uas, from, callerNumber, toUser));
        }

        /// <summary>Answers a bound inbound call and attaches its media to the slot.</summary>
        public async Task<bool> AnswerAsync(InboundCall call)
        {
            var rtpSession = CreateRtpSession(call.Slot.RtpBridge.RtpPort);
            call.Slot.RtpBridge.Attach(rtpSession);

            try
            {
                if (await call.Ua.Answer(call.Uas, rtpSession, null))
                {
                    Log.Information("[SIP] Inbound call from {From} answered", call.From);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SIP] Error answering inbound call");
            }

            Log.Warning("[SIP] Inbound call answer failed");
            return false;
        }

        // ---- Outbound calls ----

        /// <summary>Dials <paramref name="dialledNumber"/> from the slot. Returns true once the call is answered.</summary>
        public async Task<bool> PlaceCallAsync(CallSlot slot, string dialledNumber)
        {
            var ua  = _slotUas[slot];
            string uri = $"sip:{dialledNumber}@{_config.Sip.Realm}";
            Log.Information("[SIP] Placing outbound call to {Uri}", uri);

            var rtpSession = CreateRtpSession(slot.RtpBridge.RtpPort);
            slot.RtpBridge.Attach(rtpSession);

            try
            {
                if (await ua.Call(uri, _config.Sip.User, _config.Sip.Password, rtpSession, 60))
                {
                    Log.Information("[SIP] Outbound call connected to {Uri}", uri);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SIP] Error placing outbound call to {Uri}", uri);
            }

            Log.Warning("[SIP] Outbound call failed to {Uri}", uri);
            return false;
        }

        /// <summary>Hangs up the slot's SIP dialog (if still up) and closes its RTP session.</summary>
        public void HangupSlot(CallSlot slot)
        {
            var ua = _slotUas[slot];
            if (ua.IsCallActive) ua.Hangup();
            slot.RtpBridge.Detach();
        }

        // ---- RTP session factory ----

        private RTPSession CreateRtpSession(int localPort)
        {
            // RTP on localPort, RTCP on localPort + 1 (slots are allocated two ports apart)
            var session = new RTPSession(false, false, false, _rtpBindAddress, localPort);
            session.addTrack(new MediaStreamTrack(new[] { SDPWellKnownMediaFormatsEnum.PCMU }));
            return session;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
