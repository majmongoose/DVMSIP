// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using fnecore;
using fnecore.EDAC;
using fnecore.P25;

namespace dvmsip.Fne
{
    /// <summary>
    /// Single block TSBK helpers for P25 telephone interconnect signalling (TIA-102.AABC).
    /// Works on the raw 12-byte TSBK (opcode, MFId, 8 payload bytes, CRC).
    /// </summary>
    internal static class P25Tsbk
    {
        public const byte AnswerProceed = 0x20;
        public const byte AnswerDeny = 0x21;
        public const byte AnswerWait = 0x22;

        public const int MaxAnswerDigits = 10;

        private const int TsbkLength = P25Defines.P25_TSBK_LENGTH_BYTES;
        private const int MessageHeaderLength = 24;

        /// <summary>
        /// Decodes the TSBK carried by an FNE TSDU message (24-byte header + TSDU frame).
        /// Returns null if the frame fails Trellis or CRC decoding.
        /// </summary>
        public static byte[]? Decode(byte[] message)
        {
            if (message.Length < MessageHeaderLength + P25Defines.P25_TSDU_FRAME_LENGTH_BYTES)
                return null;

            var frame = new byte[P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            Buffer.BlockCopy(message, MessageHeaderLength, frame, 0, frame.Length);

            var raw = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
            P25Interleaver.Decode(frame, ref raw, 114, 318);

            var tsbk = new byte[TsbkLength];
            if (!new Trellis().Decode12(raw, ref tsbk))
                return null;

            return CRC.CheckCCITT162(tsbk, TsbkLength) ? tsbk : null;
        }

        public static byte Opcode(byte[] tsbk) => (byte)(tsbk[0] & 0x3F);

        public static byte MfId(byte[] tsbk) => tsbk[1];

        /// <summary>Trailing 24-bit address shared by the interconnect TSBKs (the subscriber unit).</summary>
        public static uint UnitAddress(byte[] tsbk) => FneUtils.Bytes3ToUInt32(tsbk, 7);

        /// <summary>TELE INT PSTN REQ (0x09): index into the preset number table.</summary>
        public static byte PstnAddress(byte[] tsbk) => tsbk[6];

        /// <summary>TELE INT ANS RSP (0x0A ISP): answer response code.</summary>
        public static byte AnswerResponse(byte[] tsbk) => tsbk[3];

        /// <summary>CAN SRV REQ (0x23): opcode of the service being cancelled.</summary>
        public static byte CancelledService(byte[] tsbk) => (byte)(tsbk[2] & 0x3F);

        /// <summary>
        /// Builds a TELE INT ANS REQ (0x0A OSP) alerting <paramref name="target"/> of an inbound call.
        /// Up to 10 digits (0-9, '*', '#') are presented; longer numbers keep their trailing digits.
        /// </summary>
        public static byte[] AnswerRequest(uint target, string digits)
        {
            var codes = new List<byte>();
            foreach (char c in digits)
            {
                if (c >= '0' && c <= '9') codes.Add((byte)(c - '0'));
                else if (c == '*') codes.Add(0x0A);
                else if (c == '#') codes.Add(0x0B);
            }

            if (codes.Count > MaxAnswerDigits)
                codes.RemoveRange(0, codes.Count - MaxAnswerDigits);

            // unused digit positions are null filler ($F)
            while (codes.Count < MaxAnswerDigits)
                codes.Add(0x0F);

            var tsbk = new byte[TsbkLength];
            tsbk[0] = (byte)(0x80 | P25Defines.TSBK_IOSP_TELE_INT_ANS);    // Last Block + Opcode
            tsbk[1] = P25Defines.P25_MFG_STANDARD;
            for (int i = 0; i < MaxAnswerDigits; i += 2)
                tsbk[2 + (i / 2)] = (byte)((codes[i] << 4) | codes[i + 1]);  // Digits 1 - 10
            FneUtils.Write3Bytes(target, ref tsbk, 7);                         // Target Address

            CRC.AddCCITT162(ref tsbk, TsbkLength);
            return tsbk;
        }
    }
}
