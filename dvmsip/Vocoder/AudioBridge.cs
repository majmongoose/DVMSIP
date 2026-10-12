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
    /// FNE → SIP:  IMBE frames arrive on ImbeInbound → decode → playout queue → PcmToSip every 20 ms
    /// SIP → FNE:  PCM frames arrive on PcmInbound   → encode → IMBE written to ImbeToFne
    ///
    /// Decode and encode each run on a dedicated AboveNormal thread that blocks on its input
    /// channel; they use separate native decoder/encoder contexts. A playout thread releases one
    /// PCM frame every 20 ms while a call is active, smoothing FNE's 180 ms LDU bursts.
    /// </summary>
    internal sealed class AudioBridge : IDisposable
    {
        // ---- Public channels ----

        /// <summary>FNE → bridge: write byte[11] IMBE frames here (from SipPeerSystem).</summary>
        public Channel<byte[]> ImbeInbound { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        // PcmToSip and ImbeToFne are also drained by ClearBuffers on call boundaries, so not SingleReader.

        /// <summary>bridge → SIP: read short[160] PCM frames here (from RtpBridge).</summary>
        public Channel<short[]> PcmToSip { get; } =
            Channel.CreateBounded<short[]>(new BoundedChannelOptions(64)
                { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>SIP → bridge: write short[160] PCM frames here (from RtpBridge).</summary>
        public Channel<short[]> PcmInbound { get; } =
            Channel.CreateBounded<short[]>(new BoundedChannelOptions(64)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>bridge → FNE: read byte[11] IMBE frames here (from SipPeerSystem).</summary>
        public Channel<byte[]> ImbeToFne { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        // ---- Jitter playout buffer ----

        private readonly Queue<short[]> _playoutQueue = new();
        private readonly int _maxPlayoutFrames;
        private volatile bool _callActive;

        // ---- Threads ----

        private readonly Thread _decodeThread;
        private readonly Thread _encodeThread;
        private readonly Thread _playoutThread;
        private readonly CancellationTokenSource _cts = new();
        private ImbeVocoder? _vocoder;
        private bool _started;
        private bool _disposed;

        public AudioBridge(int jitterMs = 360)
        {
            // Each IMBE frame = 20 ms; cap buffer at jitterMs / 20, minimum 3 frames
            _maxPlayoutFrames = Math.Max(3, jitterMs / 20);

            _decodeThread  = NewThread("dvmsip-decode", DecodeLoop);
            _encodeThread  = NewThread("dvmsip-encode", EncodeLoop);
            _playoutThread = NewThread("dvmsip-playout", PlayoutLoop);
        }

        private static Thread NewThread(string name, ThreadStart loop) => new(loop)
        {
            Name = name,
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

        public void Start()
        {
            _vocoder = new ImbeVocoder();
            _decodeThread.Start();
            _encodeThread.Start();
            _playoutThread.Start();
            _started = true;
        }

        /// <summary>Clears stale audio and starts emitting playout frames for a new call.</summary>
        public void BeginCall()
        {
            ClearBuffers();
            _callActive = true;
        }

        /// <summary>Stops playout and discards buffered audio for the ended call.</summary>
        public void EndCall()
        {
            _callActive = false;
            ClearBuffers();
        }

        private void ClearBuffers()
        {
            lock (_playoutQueue) _playoutQueue.Clear();
            while (PcmToSip.Reader.TryRead(out _)) { }
            while (ImbeToFne.Reader.TryRead(out _)) { }
        }

        // ---- Codec loops: each blocks on its input channel ----

        private void DecodeLoop()
        {
            var reader = ImbeInbound.Reader;
            RunLoop("decode", () =>
            {
                while (reader.TryRead(out var imbeFrame))
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
            }, reader.WaitToReadAsync);
        }

        private void EncodeLoop()
        {
            var reader = PcmInbound.Reader;
            RunLoop("encode", () =>
            {
                while (reader.TryRead(out var pcm))
                {
                    var imbeFrame = new byte[ImbeVocoder.ImbeFrameBytes];
                    _vocoder!.Encode(pcm, imbeFrame);
                    ImbeToFne.Writer.TryWrite(imbeFrame);
                }
            }, reader.WaitToReadAsync);
        }

        private void RunLoop(string name, Action drain, Func<CancellationToken, ValueTask<bool>> waitToRead)
        {
            var ct = _cts.Token;
            try
            {
                while (waitToRead(ct).AsTask().GetAwaiter().GetResult())
                    drain();
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[AudioBridge] {Name} thread error", name);
            }
        }

        // ---- Playout: releases one PCM frame every 20 ms toward SIP while a call is active ----

        private void PlayoutLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var ct = _cts.Token;
            try
            {
                while (timer.WaitForNextTickAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    if (!_callActive) continue;

                    short[]? frame;
                    lock (_playoutQueue)
                        _playoutQueue.TryDequeue(out frame);

                    // Emit silence during gaps to keep the RTP stream flowing
                    PcmToSip.Writer.TryWrite(frame ?? new short[ImbeVocoder.PcmSamples]);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();

            // Native contexts must outlive any in-flight Decode/Encode call
            if (_started)
            {
                _decodeThread.Join();
                _encodeThread.Join();
                _playoutThread.Join();
            }

            _vocoder?.Dispose();
            _cts.Dispose();
        }
    }
}
