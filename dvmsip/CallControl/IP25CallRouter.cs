// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using fnecore.P25;

namespace dvmsip.CallControl
{
    /// <summary>Routes P25 traffic received from the FNE to the call slot it belongs to.</summary>
    internal interface IP25CallRouter
    {
        /// <summary>A radio voice frame (LDU1/LDU2). Returns the slot whose bridge takes the audio, or null to drop it.</summary>
        CallSlot? OnVoice(uint srcId, uint dstId, byte lco, bool standardMfId, P25DUID duid);

        /// <summary>The end of a radio transmission (TDU/TDULC).</summary>
        void OnTerminator(uint srcId, uint dstId, byte lco);

        /// <summary>A single block TSBK received from the network.</summary>
        void OnTsbk(byte[] tsbk);

        /// <summary>The FNE rejected traffic this gateway is transmitting to <paramref name="dstId"/>.</summary>
        void OnTrafficRejected(uint dstId);
    }
}
