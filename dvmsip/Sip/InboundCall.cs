// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.CallControl;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace dvmsip.Sip
{
    /// <summary>
    /// An inbound SIP call bound to a call slot. The slot's controller decides when to ring, answer or reject.
    /// </summary>
    internal sealed class InboundCall
    {
        private readonly SipService _sip;

        public CallSlot Slot { get; }
        public SIPUserAgent Ua { get; }
        public SIPServerUserAgent Uas { get; }

        /// <summary>Caller URI (From header).</summary>
        public string From { get; }
        /// <summary>Caller user part (usually the calling number).</summary>
        public string CallerNumber { get; }
        /// <summary>Called user part of the request URI (the DID).</summary>
        public string ToUser { get; }

        public InboundCall(SipService sip, CallSlot slot, SIPUserAgent ua, SIPServerUserAgent uas,
            string from, string callerNumber, string toUser)
        {
            _sip = sip;
            Slot = slot;
            Ua = ua;
            Uas = uas;
            From = from;
            CallerNumber = callerNumber;
            ToUser = toUser;
        }

        /// <summary>Sends 180 Ringing to the caller.</summary>
        public void Ringing() => Uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);

        /// <summary>Answers the call and attaches media to the slot. Returns false if the answer failed.</summary>
        public Task<bool> AnswerAsync() => _sip.AnswerAsync(this);

        /// <summary>Rejects the call with the given final response.</summary>
        public void Reject(SIPResponseStatusCodesEnum code) => Uas.Reject(code, null);
    }
}
