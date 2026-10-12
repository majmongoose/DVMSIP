// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using fnecore.EDAC;
using fnecore.EDAC.RS;
using fnecore.P25;

namespace dvmsip.Fne
{
    /// <summary>
    /// TDULC (terminator with link control) frame encoding: RS (24,12,13), Golay (24,12,8), then interleave.
    /// fnecore provides the FEC primitives but no TDULC framing.
    /// </summary>
    internal static class P25Tdulc
    {
        public const int FrameLength = 54;      // P25_TDULC_FRAME_LENGTH_BYTES
        private const int RsLength = 18;        // P25_TDULC_LENGTH_BYTES
        private const int FecLength = 36;       // P25_TDULC_FEC_LENGTH_BYTES

        /// <summary>Builds an LC_CALL_TERM TDULC frame terminating the call to <paramref name="dstId"/>.</summary>
        public static byte[] CallTermFrame(uint dstId)
        {
            var lc = new byte[RsLength];
            lc[0] = (byte)(0x40 | P25Defines.LC_CALL_TERM);     // Implicit Operation + LCO
            lc[6] = (byte)(dstId >> 16);                        // Target Address
            lc[7] = (byte)(dstId >> 8);
            lc[8] = (byte)dstId;

            byte[] rs = ReedSolomonAlgorithm.Encode(lc, ErrorCorrectionCodeType.ReedSolomon_241213);

            var fec = new byte[FecLength + 1];
            Golay24128.encode24128(ref fec, rs, RsLength);

            var frame = new byte[FrameLength];
            P25Interleaver.Encode(fec, ref frame, 114, 410);
            return frame;
        }
    }
}
