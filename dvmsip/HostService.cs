// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using dvmsip.CallControl;
using dvmsip.Configuration;
using dvmsip.Fne;
using dvmsip.Sip;
using dvmsip.Vocoder;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace dvmsip
{
    /// <summary>
    /// Top-level IHostedService that owns and wires all major subsystems.
    /// </summary>
    internal sealed class HostService : IHostedService
    {
        private readonly DvmSipConfig _config;

        private SipPeerSystem? _fne;
        private SipService? _sip;
        private readonly List<CallSlot> _slots = new();

        public HostService(DvmSipConfig config)
        {
            _config = config;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Log.Information("Starting DVMSIP host service");

            // Validate mode
            if (_config.PhonePatch.Mode != "conventional")
            {
                Log.Warning("Patch mode '{Mode}' is not yet supported; only 'conventional' is implemented", _config.PhonePatch.Mode);
            }

            int slotCount = Math.Max(1, _config.PhonePatch.MaxCalls);

            // Allocate call slots — each slot has its own AudioBridge and CallStateMachine
            int rtpPort = _config.Rtp.PortRangeStart;
            for (int i = 0; i < slotCount; i++)
            {
                var bridge = new AudioBridge(_config.Network.Jitter);
                var rtpBridge = new RtpBridge(bridge, rtpPort);
                rtpPort += 2; // RTP + RTCP
                var sm = new CallStateMachine(i, _config, rtpBridge, bridge);
                _slots.Add(new CallSlot(bridge, rtpBridge, sm));
            }

            // FNE peer system — receives/transmits P25 audio
            _fne = new SipPeerSystem(_config, _slots);

            // SIP service — registers UA, handles inbound/outbound calls
            _sip = new SipService(_config, _slots);

            // Wire back-references so state machines can call out
            foreach (var slot in _slots)
            {
                slot.StateMachine.SipService = _sip;
                slot.StateMachine.PeerSystem = _fne;
                slot.StateMachine.Slot       = slot;
                slot.Start(); // start AudioBridge codec + playout threads
            }

            _fne.Start();
            _fne.StartTransmitLoops(cancellationToken);
            _sip.Start();

            Log.Information("DVMSIP started — {Count} call slot(s), talkgroup {TG}",
                slotCount, _config.PhonePatch.Talkgroup);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("Stopping DVMSIP host service");

            _sip?.Stop();
            _fne?.Stop();

            foreach (var slot in _slots)
                slot.Dispose();

            return Task.CompletedTask;
        }
    }
}
