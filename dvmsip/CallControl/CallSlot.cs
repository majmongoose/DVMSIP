// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using dvmsip.Sip;
using dvmsip.Vocoder;

namespace dvmsip.CallControl
{
    /// <summary>
    /// Groups all resources for one concurrent call slot.
    /// HostService owns a list of these; each slot can handle one phone patch at a time.
    /// </summary>
    internal sealed class CallSlot : IDisposable
    {
        public int Index { get; }
        public AudioBridge Bridge { get; }
        public RtpBridge RtpBridge { get; }

        /// <summary>Set by HostService once the mode-specific controller has been created.</summary>
        public ICallController Controller { get; set; } = null!;

        public CallSlot(int index, AudioBridge bridge, RtpBridge rtpBridge)
        {
            Index     = index;
            Bridge    = bridge;
            RtpBridge = rtpBridge;
        }

        public void Start()
        {
            Bridge.Start();
        }

        public void Dispose()
        {
            RtpBridge.Dispose();
            Bridge.Dispose();
        }
    }
}
