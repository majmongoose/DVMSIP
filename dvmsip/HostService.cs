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

            bool trunked = _config.PhonePatch.IsTrunked;
            if (!trunked && !string.Equals(_config.PhonePatch.Mode, "conventional", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Unknown patch mode '{Mode}', using conventional", _config.PhonePatch.Mode);
            }

            int slotCount = Math.Max(1, _config.PhonePatch.MaxCalls);

            // Allocate call slots — each slot has its own AudioBridge and RTP port pair
            int rtpPort = _config.Rtp.PortRangeStart;
            if (rtpPort + slotCount * 2 - 1 > _config.Rtp.PortRangeEnd)
                throw new InvalidOperationException(
                    $"rtp port range {_config.Rtp.PortRangeStart}-{_config.Rtp.PortRangeEnd} is too small for {slotCount} call slot(s)");

            for (int i = 0; i < slotCount; i++)
            {
                // trunked interconnect calls are half-duplex on the radio side, so SIP audio is keyed by VOX
                var bridge = trunked
                    ? new AudioBridge(_config.Network.Jitter, _config.PhonePatch.VoxThreshold, _config.PhonePatch.VoxHangTime)
                    : new AudioBridge(_config.Network.Jitter);
                var rtpBridge = new RtpBridge(bridge, rtpPort);
                rtpPort += 2; // RTP + RTCP
                _slots.Add(new CallSlot(i, bridge, rtpBridge));
            }

            // FNE peer system — receives/transmits P25 audio and signalling
            _fne = new SipPeerSystem(_config, _slots);

            // SIP service — registers UA, handles inbound/outbound calls
            _sip = new SipService(_config, _slots);

            if (trunked)
            {
                var manager = new InterconnectManager(_config, _slots) { PeerSystem = _fne };
                _fne.Router = manager;

                foreach (var slot in _slots)
                {
                    slot.Controller = new InterconnectCall(slot, _config, manager.IsUnitBusy)
                    {
                        SipService = _sip,
                        PeerSystem = _fne,
                    };
                }
            }
            else
            {
                _fne.Router = new ConventionalCallRouter(_config, _slots);

                foreach (var slot in _slots)
                {
                    slot.Controller = new CallStateMachine(slot, _config)
                    {
                        SipService = _sip,
                        PeerSystem = _fne,
                    };
                }
            }

            foreach (var slot in _slots)
                slot.Start(); // start AudioBridge codec + playout threads

            _fne.Start();
            _fne.StartTransmitLoops(cancellationToken);
            _sip.Start();

            if (trunked)
                Log.Information("DVMSIP started — {Count} call slot(s), trunked telephone interconnect", slotCount);
            else
                Log.Information("DVMSIP started — {Count} call slot(s), talkgroup {TG}", slotCount, _config.PhonePatch.Talkgroup);

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
