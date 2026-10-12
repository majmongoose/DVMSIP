// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

namespace dvmsip.Fne
{
    /// <summary>DVM network values not defined by fnecore (see dvmhost common/network/BaseNetwork.h and P25Defines.h).</summary>
    internal static class DvmNetDefines
    {
        public const byte MfgStandardAlt = 0x01;
        public const byte MfgMot = 0x90;

        // FNE P25 message header control byte (byte 14)
        public const byte NetCtrlGrantDemand = 0x80;
        public const byte NetCtrlGrantDenial = 0x40;
        public const byte NetCtrlU2U = 0x01;
    }
}
