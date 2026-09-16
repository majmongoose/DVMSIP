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
    /// Manages the SIP User Agent: registration, inbound call acceptance, outbound call dialling.
    /// Uses SIPSorcery. Offers only PCMU (PT 0) and telephone-event/101 in SDP.
    /// </summary>
    internal sealed class SipService : IDisposable
    {
        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;

        private SIPTransport? _transport;
        private SIPUserAgent? _ua;
        private SIPRegistrationUserAgent? _registrar;
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

            _transport = new SIPTransport();
            _transport.AddSIPChannel(new SIPUDPChannel(
                new IPEndPoint(localAddr, _config.Sip.LocalPort)));

            _ua = new SIPUserAgent(_transport, null);
            _ua.OnIncomingCall += OnIncomingCall;
            _ua.OnCallHungup   += OnCallHungup;
            _ua.OnDtmfTone     += (tone, duration) =>
                Log.Information("[SIP] DTMF tone {Tone} received ({Duration}ms)", tone, duration);

            // Register with the SIP server.
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
            _registrar?.Stop();
            _transport?.Shutdown();
        }

        // ---- Inbound calls ----

        private void OnIncomingCall(SIPUserAgent ua, SIPRequest req)
        {
            Log.Information("[SIP] Incoming call from {From} to {To}",
                req.Header.From?.FromURI, req.Header.To?.ToURI);

            // AcceptCall transitions the server UA to ringing state and lets us decide.
            var uas = ua.AcceptCall(req);

            var slot = FindIdleSlot();
            if (slot == null)
            {
                Log.Warning("[SIP] No idle call slots — rejecting inbound call");
                uas.Reject(SIPResponseStatusCodesEnum.BusyHere, null);
                return;
            }

            var rtpSession = CreateRtpSession(slot.RtpBridge.RtpPort);
            slot.RtpBridge.Attach(rtpSession);

            Task.Run(async () =>
            {
                bool answered = await ua.Answer(uas, rtpSession, null);
                if (answered)
                {
                    Log.Information("[SIP] Inbound call answered");
                    slot.StateMachine.OnSipAnswered(ua, rtpSession, outbound: false);
                }
                else
                {
                    slot.RtpBridge.Detach();
                    Log.Warning("[SIP] Inbound call answer failed");
                }
            });
        }

        private void OnCallHungup(SIPDialogue dialogue)
        {
            Log.Information("[SIP] Remote hangup");
            foreach (var slot in _slots)
                slot.StateMachine.OnSipHangup();
        }

        // ---- Outbound calls ----

        public async Task PlaceCallAsync(CallSlot slot, string dialledNumber)
        {
            if (_ua == null) throw new InvalidOperationException("SipService not started");

            string uri = $"sip:{dialledNumber}@{_config.Sip.Realm}";
            Log.Information("[SIP] Placing outbound call to {Uri}", uri);

            var rtpSession = CreateRtpSession(slot.RtpBridge.RtpPort);
            slot.RtpBridge.Attach(rtpSession);

            bool connected = await _ua.Call(uri, _config.Sip.User, _config.Sip.Password, rtpSession, 60);
            if (connected)
            {
                Log.Information("[SIP] Outbound call connected to {Uri}", uri);
                slot.StateMachine.OnSipAnswered(_ua, rtpSession, outbound: true);
            }
            else
            {
                slot.RtpBridge.Detach();
                slot.StateMachine.OnSipFailed();
                Log.Warning("[SIP] Outbound call failed to {Uri}", uri);
            }
        }

        public void HangupSlot(CallSlot slot)
        {
            _ua?.Hangup();
            slot.RtpBridge.Detach();
        }

        // ---- RTP session factory ----

        private static RTPSession CreateRtpSession(int localPort)
        {
            var session = new RTPSession(false, false, false);
            var track = new MediaStreamTrack(new[] { SDPWellKnownMediaFormatsEnum.PCMU });
            session.addTrack(track);
            return session;
        }

        // ---- Helpers ----

        private CallSlot? FindIdleSlot()
        {
            foreach (var slot in _slots)
                if (slot.StateMachine.IsIdle) return slot;
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
