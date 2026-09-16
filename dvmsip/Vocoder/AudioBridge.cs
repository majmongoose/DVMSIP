// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Threading.Channels;
using Serilog;

namespace dvmsip.Vocoder
{
    /// <summary>
    /// Owns the IMBE vocoder and bridges audio between the FNE (P25 IMBE) and SIP (PCM) paths.
    ///
    /// FNE → SIP:  IMBE frames arrive on ImbeInbound → decode → PCM written to PcmToSip
    /// SIP → FNE:  PCM frames arrive on PcmInbound   → encode → IMBE written to ImbeToFne
    ///
    /// Runs a single dedicated background thread at AboveNormal priority to guarantee
    /// consistent 20 ms frame timing without OS task-scheduler jitter.
    ///
    /// Phase 2: a playout jitter buffer holds up to jitterMs worth of decoded PCM before
    /// releasing one frame every 20 ms via PeriodicTimer, smoothing FNE's 180 ms LDU bursts.
    /// </summary>
    internal sealed class AudioBridge : IDisposable
    {
        // ---- Public channels ----

        /// <summary>FNE → bridge: write byte[11] IMBE frames here (from SipPeerSystem).</summary>
        public Channel<byte[]> ImbeInbound { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>bridge → SIP: read short[160] PCM frames here (from RtpBridge).</summary>
        public Channel<short[]> PcmToSip { get; } =
            Channel.CreateBounded<short[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>SIP → bridge: write short[160] PCM frames here (from RtpBridge).</summary>
        public Channel<short[]> PcmInbound { get; } =
            Channel.CreateBounded<short[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>bridge → FNE: read byte[11] IMBE frames here (from SipPeerSystem).</summary>
        public Channel<byte[]> ImbeToFne { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        // ---- Jitter playout buffer (Phase 2) ----

        private readonly int _jitterMs;
        private readonly Queue<short[]> _playoutQueue = new();
        private readonly int _maxPlayoutFrames;
        private PeriodicTimer? _playoutTimer;
        private Thread? _playoutThread;

        // ---- Codec thread ----

        private readonly Thread _codecThread;
        private readonly CancellationTokenSource _cts = new();
        private ImbeVocoder? _vocoder;
        private bool _disposed;

        public AudioBridge(int jitterMs = 360)
        {
            _jitterMs = jitterMs;
            // Each IMBE frame = 20 ms; cap buffer at jitterMs / 20, minimum 3 frames
            _maxPlayoutFrames = Math.Max(3, jitterMs / 20);

            _codecThread = new Thread(CodecLoop)
            {
                Name = "dvmsip-codec",
                IsBackground = false,
                Priority = ThreadPriority.AboveNormal,
            };
        }

        public void Start()
        {
            _vocoder = new ImbeVocoder();
            _codecThread.Start();
            StartPlayoutTimer();
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        // ---- Codec loop: runs on dedicated thread ----

        private void CodecLoop()
        {
            Log.Debug("[AudioBridge] Codec thread started");
            var ct = _cts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Drain IMBE inbound (FNE → SIP)
                    while (ImbeInbound.Reader.TryRead(out var imbeFrame))
                    {
                        var pcm = new short[ImbeVocoder.PcmSamples];
                        _vocoder!.Decode(imbeFrame, pcm);

                        lock (_playoutQueue)
                        {
                            if (_playoutQueue.Count >= _maxPlayoutFrames)
                                _playoutQueue.Dequeue(); // drop oldest on overflow
                            _playoutQueue.Enqueue(pcm);
                        }
                    }

                    // Drain PCM inbound (SIP → FNE)
                    while (PcmInbound.Reader.TryRead(out var pcm))
                    {
                        var imbeFrame = new byte[ImbeVocoder.ImbeFrameBytes];
                        _vocoder!.Encode(pcm, imbeFrame);
                        ImbeToFne.Writer.TryWrite(imbeFrame);
                    }

                    Thread.Sleep(1); // yield; playout timer drives actual 20 ms cadence
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[AudioBridge] Codec thread error");
            }

            Log.Debug("[AudioBridge] Codec thread stopped");
        }

        // ---- Playout timer: releases one PCM frame every 20 ms toward SIP ----

        private void StartPlayoutTimer()
        {
            _playoutTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            _playoutThread = new Thread(PlayoutLoop)
            {
                Name = "dvmsip-playout",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _playoutThread.Start();
        }

        private void PlayoutLoop()
        {
            var ct = _cts.Token;
            try
            {
                while (_playoutTimer!.WaitForNextTickAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    short[]? frame = null;
                    lock (_playoutQueue)
                    {
                        if (_playoutQueue.Count > 0)
                            frame = _playoutQueue.Dequeue();
                    }

                    if (frame != null)
                        PcmToSip.Writer.TryWrite(frame);
                    else
                        // Emit silence to keep RTP stream flowing during gaps
                        PcmToSip.Writer.TryWrite(new short[ImbeVocoder.PcmSamples]);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _playoutTimer?.Dispose();
            _vocoder?.Dispose();
        }
    }
}
