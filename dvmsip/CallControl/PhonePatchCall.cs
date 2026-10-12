// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

namespace dvmsip.CallControl
{
    /// <summary>
    /// Per-call state container. Holds all live call metadata for one active phone patch.
    /// </summary>
    internal sealed class PhonePatchCall
    {
        public uint SrcId    { get; set; }
        public uint DstId    { get; set; }
        public bool Outbound { get; set; }

        public DateTime StartTime { get; } = DateTime.UtcNow;
    }
}
