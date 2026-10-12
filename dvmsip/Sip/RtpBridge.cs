// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Net;
using dvmsip.Vocoder;
using Serilog;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace dvmsip.Sip
{
    /// <summary>
    /// Bridges between an RTP session (PCMU G.711 µ-law) and the AudioBridge channels.
    ///
    /// SIP → FNE: RTP PCMU packets decoded to PCM and written to AudioBridge.PcmInbound
    /// FNE → SIP: PCM frames from AudioBridge.PcmToSip encoded to PCMU and sent via RTP
    /// </summary>
    internal sealed class RtpBridge : IDisposable
    {
        public const int PcmSamples = 160;   // 20 ms at 8 kHz, 1 byte per sample for PCMU

        private readonly AudioBridge _bridge;
        private readonly int _rtpPort;
        private readonly object _lock = new();
        private RTPSession? _rtpSession;
        private Thread? _sendThread;
        private CancellationTokenSource? _callCts;
        private bool _disposed;

        public int RtpPort => _rtpPort;

        public RtpBridge(AudioBridge bridge, int rtpPort)
        {
            _bridge  = bridge;
            _rtpPort = rtpPort;
        }

        /// <summary>Attach an RTP session for a new call. Any previous session is detached first.</summary>
        public void Attach(RTPSession session)
        {
            lock (_lock)
            {
                DetachLocked();

                _rtpSession = session;
                _rtpSession.OnRtpPacketReceived += OnRtpReceived;
                _callCts = new CancellationTokenSource();
                _bridge.BeginCall();

                var ct = _callCts.Token;
                _sendThread = new Thread(() => SendLoop(session, ct))
                {
                    Name = $"dvmsip-rtp-{_rtpPort}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                _sendThread.Start();
            }

            Log.Debug("[RTP] Session attached on port {Port}", _rtpPort);
        }

        /// <summary>Detach and close the current RTP session (idempotent).</summary>
        public void Detach()
        {
            lock (_lock)
            {
                if (_rtpSession == null) return;
                DetachLocked();
            }
            Log.Debug("[RTP] Session detached on port {Port}", _rtpPort);
        }

        private void DetachLocked()
        {
            if (_rtpSession == null) return;

            _callCts?.Cancel();
            _rtpSession.OnRtpPacketReceived -= OnRtpReceived;
            _rtpSession.Close("call ended");

            if (_sendThread != null && _sendThread != Thread.CurrentThread)
                _sendThread.Join(TimeSpan.FromSeconds(1));

            _bridge.EndCall();

            _callCts?.Dispose();
            _callCts    = null;
            _sendThread = null;
            _rtpSession = null;
        }

        // ---- RTP receive (SIP → FNE) ----

        private void OnRtpReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            if (rtpPacket.Header.PayloadType != (int)SDPWellKnownMediaFormatsEnum.PCMU) return;

            var payload = rtpPacket.Payload;
            if (payload == null) return;

            // Re-frame to 20 ms blocks so senders using other ptime values are not truncated
            for (int offset = 0; offset + PcmSamples <= payload.Length; offset += PcmSamples)
            {
                var pcm = new short[PcmSamples];
                for (int i = 0; i < PcmSamples; i++)
                    pcm[i] = MuLawDecoder.MuLawToLinearSample(payload[offset + i]);

                _bridge.PcmInbound.Writer.TryWrite(pcm);
            }
        }

        // ---- RTP send loop (FNE → SIP) ----

        private void SendLoop(RTPSession session, CancellationToken ct)
        {
            var reader = _bridge.PcmToSip.Reader;
            try
            {
                while (reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out var pcm))
                    {
                        var pcmu = new byte[PcmSamples];
                        for (int i = 0; i < PcmSamples; i++)
                            pcmu[i] = MuLawEncoder.LinearToMuLawSample(pcm[i]);

                        // First argument is the duration in RTP units added to the previous timestamp
                        session.SendAudio(PcmSamples, pcmu);
                    }
                }
            }
            catch (OperationCanceledException) { /* call ended */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[RTP] Send loop error on port {Port}", _rtpPort);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
