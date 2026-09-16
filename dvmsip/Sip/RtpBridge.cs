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
using SIPSorcery.Net;

namespace dvmsip.Sip
{
    /// <summary>
    /// Bridges between an RTP session (PCMU G.711 µ-law) and the AudioBridge channels.
    ///
    /// SIP → FNE: RTP PCMU packets decoded to PCM and written to AudioBridge.PcmInbound
    /// FNE → SIP: PCM frames from AudioBridge.PcmToSip encoded to PCMU and sent via RTP
    ///
    /// G.711 µ-law encode/decode implemented inline — no external library needed.
    /// Codec table is public domain (~256 bytes each way).
    /// </summary>
    internal sealed class RtpBridge : IDisposable
    {
        public const int PcmSamples   = 160;   // 20 ms at 8 kHz
        public const int PcmuBytes    = 160;   // 1 byte per sample for PCMU

        private readonly AudioBridge _bridge;
        private readonly int _rtpPort;
        private RTPSession? _rtpSession;
        private Thread? _sendThread;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        private uint _rtpTimestamp;

        public int RtpPort => _rtpPort;
        public bool IsActive => _rtpSession != null;

        public RtpBridge(AudioBridge bridge, int rtpPort)
        {
            _bridge  = bridge;
            _rtpPort = rtpPort;
        }

        /// <summary>Attach an active RTP session (called when a SIP call connects).</summary>
        public void Attach(RTPSession session)
        {
            _rtpSession = session;
            _rtpSession.OnRtpPacketReceived += OnRtpReceived;

            _sendThread = new Thread(SendLoop)
            {
                Name = $"dvmsip-rtp-{_rtpPort}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _sendThread.Start();

            Log.Debug("[RTP] Session attached on port {Port}", _rtpPort);
        }

        /// <summary>Detach the RTP session (called when a SIP call ends).</summary>
        public void Detach()
        {
            if (_rtpSession == null) return;
            _rtpSession.OnRtpPacketReceived -= OnRtpReceived;
            _rtpSession = null;
            _cts.Cancel();
            Log.Debug("[RTP] Session detached on port {Port}", _rtpPort);
        }

        // ---- RTP receive (SIP → FNE) ----

        private void OnRtpReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType,
            RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length < PcmuBytes) return;

            var pcm = new short[PcmSamples];
            for (int i = 0; i < PcmSamples; i++)
                pcm[i] = MuLawDecode(payload[i]);

            _bridge.PcmInbound.Writer.TryWrite(pcm);
        }

        // ---- RTP send loop (FNE → SIP) ----

        private void SendLoop()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_bridge.PcmToSip.Reader.TryRead(out var pcm) && _rtpSession != null)
                    {
                        var pcmu = new byte[PcmuBytes];
                        for (int i = 0; i < PcmSamples; i++)
                            pcmu[i] = MuLawEncode(pcm[i]);

                        _rtpSession.SendAudio(_rtpTimestamp, pcmu);
                        _rtpTimestamp += PcmSamples;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[RTP] Send loop error on port {Port}", _rtpPort);
            }
        }

        // ---- G.711 µ-law codec (public domain) ----

        private static short MuLawDecode(byte mulaw)
        {
            mulaw = (byte)~mulaw;
            int sign  = (mulaw & 0x80) != 0 ? -1 : 1;
            int exp   = (mulaw >> 4) & 0x07;
            int mant  = mulaw & 0x0F;
            int mag   = ((mant << 1) | 0x21) << exp;
            return (short)(sign * (mag - 33));
        }

        private static byte MuLawEncode(short pcm)
        {
            const int MuLawMax = 0x1FFF;
            const int MuLawBias = 0x21;

            int sign = (pcm < 0) ? 0x80 : 0;
            int s = Math.Abs(pcm);
            s = Math.Min(s, MuLawMax);
            s += MuLawBias;

            int exp = 7;
            for (; exp > 0; exp--)
            {
                if ((s & (1 << (exp + 3))) != 0) break;
            }
            int mant = (s >> (exp + 3)) & 0x0F;
            return (byte)(~(sign | (exp << 4) | mant) & 0xFF);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            _cts.Dispose();
        }
    }
}
