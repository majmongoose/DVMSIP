// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Sip;

namespace dvmsip.CallControl
{
    /// <summary>Addressing for P25 traffic a slot transmits toward the FNE.</summary>
    internal readonly record struct TransmitTarget(uint SrcId, uint DstId, byte Lco, bool Individual);

    /// <summary>
    /// Per-slot call control. Conventional mode patches a talkgroup (CallStateMachine); trunked mode
    /// runs a P25 telephone interconnect call for one subscriber unit (InterconnectCall).
    /// </summary>
    internal interface ICallController
    {
        bool IsIdle { get; }

        /// <summary>True while SIP → radio audio may be transmitted toward the FNE.</summary>
        bool CanTransmit { get; }

        /// <summary>Addressing for the transmitted P25 traffic.</summary>
        TransmitTarget Target { get; }

        /// <summary>Atomically claims this slot for an inbound SIP call. Returns false if it cannot take the call.</summary>
        bool TryBeginInbound(string from, string toUser);

        /// <summary>Called once an inbound call has been bound to this slot.</summary>
        void OnInboundCall(InboundCall call);

        /// <summary>Called when the SIP dialog ends (remote hangup or cancel).</summary>
        void OnSipHangup();
    }
}
