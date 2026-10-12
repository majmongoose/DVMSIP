// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Configuration;
using fnecore.P25;

namespace dvmsip.CallControl
{
    /// <summary>Conventional mode: voice on the configured talkgroup is patched to a call slot.</summary>
    internal sealed class ConventionalCallRouter : IP25CallRouter
    {
        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;

        public ConventionalCallRouter(DvmSipConfig config, IReadOnlyList<CallSlot> slots)
        {
            _config = config;
            _slots  = slots;
        }

        public CallSlot? OnVoice(uint srcId, uint dstId, byte lco, bool standardMfId, P25DUID duid)
        {
            if (dstId != _config.PhonePatch.Talkgroup) return null;

            // prefer the slot already active for this talkgroup, otherwise take the first idle slot
            CallStateMachine? sm = null;
            foreach (var slot in _slots)
            {
                if (slot.Controller is CallStateMachine c && c.IsActiveFor(dstId)) { sm = c; break; }
            }

            if (sm == null)
            {
                foreach (var slot in _slots)
                {
                    if (slot.Controller is CallStateMachine c && c.IsIdle) { sm = c; break; }
                }
            }

            if (sm == null) return null;

            sm.OnP25AudioReceived(srcId, dstId, duid);
            return sm.Slot;
        }

        public void OnTerminator(uint srcId, uint dstId, byte lco) { }

        public void OnTsbk(byte[] tsbk) { }

        public void OnTrafficRejected(uint dstId) { }
    }
}
