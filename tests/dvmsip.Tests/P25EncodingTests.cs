// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Fne;
using fnecore;
using fnecore.EDAC;
using fnecore.P25;

namespace dvmsip.Tests
{
    public class P25EncodingTests
    {
        // Produced by dvmhost's RS634717::encode241213 algorithm and octal ENCODE_MATRIX compiled with gcc,
        // for an LC_CALL_TERM (implicit) to target 0x123456.
        private const string CallTermRsReference = "4F00000000001234565F261BAA9EE0E44603";

        [Fact]
        public void Rs241213_MatchesDvmhostReference()
        {
            var rs = new byte[18];
            rs[0] = 0x4F;
            rs[6] = 0x12;
            rs[7] = 0x34;
            rs[8] = 0x56;

            Rs241213.Encode(rs);

            Assert.Equal(CallTermRsReference, Convert.ToHexString(rs));
        }

        [Fact]
        public void CallTermFrame_DecodesToReferenceCodeword()
        {
            byte[] frame = P25Tdulc.CallTermFrame(0x123456);
            Assert.Equal(P25Tdulc.FrameLength, frame.Length);

            var fec = new byte[37];
            P25Interleaver.Decode(frame, ref fec, 114, 410);

            var rs = new byte[18];
            Golay24128.decode24128(ref rs, fec, 18);

            Assert.Equal(CallTermRsReference, Convert.ToHexString(rs));
        }

        [Fact]
        public void AnswerRequest_Layout()
        {
            byte[] tsbk = P25Tsbk.AnswerRequest(0x000064, "5551234");

            Assert.Equal(0x80 | P25Defines.TSBK_IOSP_TELE_INT_ANS, tsbk[0]);
            Assert.Equal(P25Defines.P25_MFG_STANDARD, tsbk[1]);
            Assert.Equal(new byte[] { 0x55, 0x51, 0x23, 0x4F, 0xFF }, tsbk[2..7]);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x64 }, tsbk[7..10]);
            Assert.True(CRC.CheckCCITT162(tsbk, (uint)tsbk.Length));
        }

        [Fact]
        public void AnswerRequest_KeepsTrailingTenDigits()
        {
            byte[] tsbk = P25Tsbk.AnswerRequest(0x000001, "+1 (555) 123-4567");

            // "15551234567" has 11 digits; the leading "1" is dropped
            Assert.Equal(new byte[] { 0x55, 0x51, 0x23, 0x45, 0x67 }, tsbk[2..7]);
        }

        [Fact]
        public void Decode_RecoversTsbkFromTsduMessage()
        {
            // TELE INT PSTN REQ from unit 0x00ABCD for preset 7
            var tsbk = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
            tsbk[0] = 0x80 | P25Defines.TSBK_ISP_TELE_INT_PSTN_REQ;
            tsbk[6] = 0x07;
            FneUtils.Write3Bytes(0x00ABCD, ref tsbk, 7);
            CRC.AddCCITT162(ref tsbk, (uint)tsbk.Length);

            var raw = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
            new Trellis().Encode12(tsbk, ref raw);

            var message = new byte[24 + P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            var frame = new byte[P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            P25Interleaver.Encode(raw, ref frame, 114, 318);
            Buffer.BlockCopy(frame, 0, message, 24, frame.Length);

            byte[]? decoded = P25Tsbk.Decode(message);

            Assert.NotNull(decoded);
            Assert.Equal(tsbk, decoded);
            Assert.Equal(P25Defines.TSBK_ISP_TELE_INT_PSTN_REQ, P25Tsbk.Opcode(decoded!));
            Assert.Equal(0x07, P25Tsbk.PstnAddress(decoded!));
            Assert.Equal(0x00ABCDu, P25Tsbk.UnitAddress(decoded!));
        }
    }
}
