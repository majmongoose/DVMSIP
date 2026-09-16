// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Runtime.InteropServices;

namespace dvmsip.Vocoder
{
    /// <summary>
    /// Mode constants matching MBE_ENCODER_MODE / MBE_DECODER_MODE in dvmvocoder headers.
    /// </summary>
    internal enum MbeMode
    {
        DmrAmbe   = 0,
        Imbe88Bit = 1,
    }

    /// <summary>
    /// P/Invoke wrapper for libvocoder (dvmvocoder).
    /// Place libvocoder.dll / libvocoder.so / libvocoder.dylib next to the executable.
    /// Verified signatures from extern/dvmvocoder/vocoder/MBEDecoder.h and MBEEncoder.h.
    /// </summary>
    internal static class NativeVocoder
    {
        private const string Lib = "libvocoder";

        // ---- Decoder ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEDecoder_Create(int mode);

        /// <summary>Decode 11-byte IMBE codeword to 160 PCM int16 samples. Returns error count.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MBEDecoder_Decode(
            IntPtr pDecoder,
            [In]  byte[]  codeword,
            [Out] short[] samples);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEDecoder_Delete(IntPtr pDecoder);

        // ---- Encoder ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEEncoder_Create(int mode);

        /// <summary>Encode 160 PCM int16 samples to 11-byte IMBE codeword.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_Encode(
            IntPtr pEncoder,
            [In]  short[] samples,
            [Out] byte[]  codeword);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_Delete(IntPtr pEncoder);
    }

    /// <summary>
    /// Managed wrapper that owns native encoder and decoder contexts for P25 IMBE.
    /// Thread-safe for single-threaded use per instance (AudioBridge owns one instance).
    /// </summary>
    internal sealed class ImbeVocoder : IDisposable
    {
        public const int ImbeFrameBytes = 11;
        public const int PcmSamples     = 160;

        private IntPtr _decoder;
        private IntPtr _encoder;
        private bool _disposed;

        public ImbeVocoder()
        {
            _decoder = NativeVocoder.MBEDecoder_Create((int)MbeMode.Imbe88Bit);
            _encoder = NativeVocoder.MBEEncoder_Create((int)MbeMode.Imbe88Bit);

            if (_decoder == IntPtr.Zero || _encoder == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Failed to create libvocoder contexts. Ensure libvocoder is present next to the executable.");
        }

        /// <summary>Decode one 11-byte IMBE frame to 160 PCM samples. Returns vocoder error count.</summary>
        public int Decode(byte[] imbeFrame, short[] pcmOut)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeVocoder.MBEDecoder_Decode(_decoder, imbeFrame, pcmOut);
        }

        /// <summary>Encode 160 PCM samples to one 11-byte IMBE frame.</summary>
        public void Encode(short[] pcmIn, byte[] imbeOut)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeVocoder.MBEEncoder_Encode(_encoder, pcmIn, imbeOut);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_decoder != IntPtr.Zero) { NativeVocoder.MBEDecoder_Delete(_decoder); _decoder = IntPtr.Zero; }
            if (_encoder != IntPtr.Zero) { NativeVocoder.MBEEncoder_Delete(_encoder); _encoder = IntPtr.Zero; }
        }
    }
}
