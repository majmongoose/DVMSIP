// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Configuration;
using dvmsip.Fne;
using fnecore.P25;
using Serilog;

namespace dvmsip.CallControl
{
    /// <summary>
    /// Trunked mode: routes P25 telephone interconnect signalling and voice from the FNE to the
    /// InterconnectCall serving each subscriber unit.
    /// </summary>
    internal sealed class InterconnectManager : IP25CallRouter
    {
        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;
        private readonly object _routeLock = new();

        public SipPeerSystem PeerSystem { private get; set; } = null!;

        public InterconnectManager(DvmSipConfig config, IReadOnlyList<CallSlot> slots)
        {
            _config = config;
            _slots  = slots;
        }

        /// <summary>True if a call for <paramref name="rid"/> is already in progress.</summary>
        public bool IsUnitBusy(uint rid) => FindCall(rid) != null;

        // ---- IP25CallRouter ----

        public CallSlot? OnVoice(uint srcId, uint dstId, byte lco, bool standardMfId, P25DUID duid)
        {
            if (lco != P25Defines.LC_TEL_INT_VCH_USER || !standardMfId)
                return null;

            var call = FindCall(dstId);
            if (call == null || call.State != InterconnectCall.CallState.Connected)
                return null;

            call.OnRadioVoice();
            return call.Slot;
        }

        public void OnTerminator(uint srcId, uint dstId, byte lco)
        {
            FindCall(dstId)?.OnRadioTerminator();
        }

        public void OnTsbk(byte[] tsbk)
        {
            // Motorola SUs may send standard interconnect TSBKs with their MFId
            byte mfId = P25Tsbk.MfId(tsbk);
            if (mfId != P25Defines.P25_MFG_STANDARD && mfId != DvmNetDefines.MfgMot)
                return;

            uint rid = P25Tsbk.UnitAddress(tsbk);
            switch (P25Tsbk.Opcode(tsbk))
            {
                case P25Defines.TSBK_ISP_TELE_INT_PSTN_REQ:
                    OnPstnRequest(rid, P25Tsbk.PstnAddress(tsbk));
                    break;

                case P25Defines.TSBK_IOSP_TELE_INT_ANS:
                    FindCall(rid)?.OnAnswerResponse(P25Tsbk.AnswerResponse(tsbk));
                    break;

                case P25Defines.TSBK_ISP_CAN_SRV_REQ:
                    byte service = P25Tsbk.CancelledService(tsbk);
                    if (service == P25Defines.TSBK_IOSP_TELE_INT_DIAL || service == P25Defines.TSBK_ISP_TELE_INT_PSTN_REQ ||
                        service == P25Defines.TSBK_IOSP_TELE_INT_ANS)
                        FindCall(rid)?.OnRadioCancel();
                    break;
            }
        }

        public void OnTrafficRejected(uint dstId)
        {
            FindCall(dstId)?.OnTrafficRejected();
        }

        // ---- Radio → phone ----

        private void OnPstnRequest(uint rid, byte pstnAddress)
        {
            if (!_config.PhonePatch.PresetNumbers.TryGetValue(pstnAddress, out string? number) || string.IsNullOrEmpty(number))
            {
                Log.Warning("[Interconnect] RID {Rid} requested unknown PSTN address {Address}", rid, pstnAddress);
                PeerSystem.SendGrantDenial(rid);
                return;
            }

            lock (_routeLock)
            {
                // units repeat requests until they are granted; ignore requests for a call already in progress
                if (IsUnitBusy(rid))
                    return;

                foreach (var slot in _slots)
                {
                    if (slot.Controller is InterconnectCall call && call.TryBeginOutbound(rid, number))
                        return;
                }
            }

            Log.Warning("[Interconnect] No call slot available for RID {Rid}", rid);
            PeerSystem.SendGrantDenial(rid);
        }

        private InterconnectCall? FindCall(uint rid)
        {
            if (rid == 0U) return null;

            foreach (var slot in _slots)
            {
                if (slot.Controller is InterconnectCall call && !call.IsIdle && call.Rid == rid)
                    return call;
            }

            return null;
        }
    }
}
