// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Net;
using dvmsip.CallControl;
using dvmsip.Configuration;
using fnecore;
using fnecore.DMR;
using fnecore.NXDN;
using fnecore.P25;
using Serilog;

namespace dvmsip.Fne
{
    /// <summary>
    /// FNE peer that receives P25 voice from the network and injects audio back.
    /// Extends fnecore.FneSystemBase following the pattern in DVMProject/dvmconsole/PeerSystem.cs.
    /// </summary>
    internal sealed class SipPeerSystem : FneSystemBase
    {
        // FNE P25 LDU messages carry a 24-byte header followed by nine DFSI voice records
        // (22, 14, 17 x 6, 16 bytes). These are the record offsets relative to the end of the header,
        // and the IMBE codeword offset within each record (matches dvmhost/dvmconsole DFSI framing).
        private static readonly int[] DfsiRecordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };
        private static readonly int[] ImbeOffsetInRecord = { 10, 1, 5, 5, 5, 5, 5, 5, 4 };
        private const int DfsiLength = 154;

        private const int ImbeFrameBytes = 11;
        private const int FramesPerLdu = 9;

        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;
        private readonly Dictionary<CallSlot, TxState> _tx = new();

        public FnePeer Peer => (FnePeer)fne;

        private sealed class TxState
        {
            public readonly byte[][] Frames = new byte[FramesPerLdu][];
            public int FrameCount;
            public uint StreamId;
            public ushort Seq;
            public bool NextIsLdu2;
        }

        // ---- Constructor ----

        public SipPeerSystem(DvmSipConfig config, IReadOnlyList<CallSlot> slots)
            : base(CreatePeer(config))
        {
            _config = config;
            _slots  = slots;

            foreach (var slot in slots)
                _tx[slot] = new TxState();

            // PeerDisconnected is an Action<uint> delegate on FnePeer (not an abstract override)
            Peer.PeerDisconnected = OnPeerDisconnected;
        }

        private static FnePeer CreatePeer(DvmSipConfig config)
        {
            IPEndPoint endpoint;
            try
            {
                endpoint = new IPEndPoint(IPAddress.Parse(config.Network.Address), config.Network.Port);
            }
            catch (FormatException)
            {
                var addrs = Dns.GetHostAddresses(config.Network.Address);
                endpoint  = new IPEndPoint(addrs[0], config.Network.Port);
            }

            var peer = new FnePeer(config.System.Identity, config.Network.PeerId, endpoint);
            peer.Passphrase = config.Network.Password;
            peer.Information = new PeerInformation
            {
                Details = new PeerDetails
                {
                    ConventionalPeer = true,
                    Identity         = config.System.Identity,
                    Software         = $"DVMSIP_R{Program.Version}",
                }
            };
            peer.PingTime = 5;

            // Route fnecore log output through Serilog
            peer.Logger = (level, msg) =>
            {
                switch (level)
                {
                    case LogLevel.DEBUG:   Log.Debug(msg);   break;
                    case LogLevel.WARNING: Log.Warning(msg); break;
                    case LogLevel.ERROR:   Log.Error(msg);   break;
                    case LogLevel.FATAL:   Log.Fatal(msg);   break;
                    default:               Log.Information(msg); break;
                }
            };

            return peer;
        }

        // ---- FneSystemBase abstract overrides ----

        protected override bool P25DataValidate(uint peerId, uint srcId, uint dstId,
            CallType callType, P25DUID duid, FrameType frameType, uint streamId, byte[] message)
            => true;

        protected override void P25DataPreprocess(object sender, P25DataReceivedEvent e) { }

        protected override void P25DataReceived(object sender, P25DataReceivedEvent e)
        {
            // Only handle voice LDUs for the configured talkgroup
            if (e.DstId != _config.PhonePatch.Talkgroup) return;
            if (e.DUID != P25DUID.LDU1 && e.DUID != P25DUID.LDU2) return;

            byte firstTag = e.DUID == P25DUID.LDU1 ? P25DFSI.P25_DFSI_LDU1_VOICE1 : P25DFSI.P25_DFSI_LDU2_VOICE10;
            if (!HasValidDfsiRecords(e.Data, firstTag))
            {
                Log.Debug("[FNE] Dropping malformed {Duid} from RID {SrcId}", e.DUID, e.SrcId);
                return;
            }

            // Find a slot that is active for this talkgroup, or the first idle slot
            var slot = FindSlotForIncoming(e.SrcId, e.DstId);
            if (slot == null) return;

            for (int i = 0; i < FramesPerLdu; i++)
            {
                var imbe = new byte[ImbeFrameBytes];
                Buffer.BlockCopy(e.Data, ImbeOffset(i), imbe, 0, ImbeFrameBytes);
                slot.Bridge.ImbeInbound.Writer.TryWrite(imbe);
            }

            slot.StateMachine.OnP25AudioReceived(e.SrcId, e.DstId, e.DUID);
        }

        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            Log.Information("[FNE] PEER {PeerId} connected to master", e.PeerId);
        }

        private void OnPeerDisconnected(uint peerId)
        {
            Log.Warning("[FNE] PEER {PeerId} disconnected from master", peerId);
        }

        // ---- SIP → FNE transmit loops (one per slot) ----

        /// <summary>
        /// Starts a background task per slot that drains ImbeToFne and transmits alternating
        /// LDU1/LDU2 toward the FNE talkgroup (SIP → radio direction).
        /// </summary>
        public void StartTransmitLoops(CancellationToken ct)
        {
            foreach (var slot in _slots)
            {
                var capturedSlot = slot;
                Task.Run(() => TransmitLoopAsync(capturedSlot, ct), ct);
            }
        }

        private async Task TransmitLoopAsync(CallSlot slot, CancellationToken ct)
        {
            var tx = _tx[slot];
            try
            {
                await foreach (var imbe in slot.Bridge.ImbeToFne.Reader.ReadAllAsync(ct))
                {
                    // Drop codec tail frames that arrive after the call has been torn down,
                    // otherwise they would open a new stream that is never terminated.
                    if (!slot.StateMachine.IsActive) continue;

                    lock (tx)
                    {
                        tx.Frames[tx.FrameCount++] = imbe;
                        if (tx.FrameCount < FramesPerLdu) continue;
                        tx.FrameCount = 0;
                        SendLdu(tx);
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[FNE] Transmit loop error");
            }
        }

        /// <summary>Terminates the slot's SIP → radio stream with a TDU, if one is open.</summary>
        public void EndTransmit(CallSlot slot)
        {
            var tx = _tx[slot];
            lock (tx)
            {
                tx.FrameCount = 0;
                if (tx.StreamId == 0) return;

                SendP25TDU(CreateCallData(tx.StreamId));
                Log.Debug("[FNE] Sent TDU for stream {StreamId}", tx.StreamId);
                tx.StreamId = 0;
            }
        }

        private void SendLdu(TxState tx)
        {
            if (tx.StreamId == 0)
            {
                tx.StreamId   = (uint)Random.Shared.Next(1, int.MaxValue);
                tx.Seq        = 0;
                tx.NextIsLdu2 = false;
            }

            var callData = CreateCallData(tx.StreamId);
            bool ldu2    = tx.NextIsLdu2;
            var  duid    = ldu2 ? P25DUID.LDU2 : P25DUID.LDU1;

            var payload = new byte[ldu2 ? Constants.P25LDU2PacketLength : Constants.P25LDU1PacketLength];
            CreateP25MessageHdr((byte)duid, callData, ref payload);

            byte firstTag = ldu2 ? P25DFSI.P25_DFSI_LDU2_VOICE10 : P25DFSI.P25_DFSI_LDU1_VOICE1;
            for (int i = 0; i < FramesPerLdu; i++)
            {
                int rec = P25_MSG_HDR_SIZE + DfsiRecordOffsets[i];
                payload[rec] = (byte)(firstTag + i);

                if (ldu2)
                    WriteLdu2RecordFields(payload, rec, i, callData);
                else
                    WriteLdu1RecordFields(payload, rec, i, callData);

                Buffer.BlockCopy(tx.Frames[i], 0, payload, ImbeOffset(i), ImbeFrameBytes);
            }

            payload[23] = (byte)(P25_MSG_HDR_SIZE + DfsiLength);

            ushort seq = tx.Seq;
            tx.Seq = (ushort)((tx.Seq + 1) % Constants.RtpCallEndSeq);
            tx.NextIsLdu2 = !ldu2;

            Peer.SendMaster(
                FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
                payload, seq, callData.TxStreamID);
        }

        private static void WriteLdu1RecordFields(byte[] data, int rec, int index, RemoteCallData callData)
        {
            switch (index)
            {
                case 2: // VOICE3 + LC: LCO, MFId, service options
                    data[rec + 1] = callData.LCO;
                    data[rec + 2] = callData.MFId;
                    data[rec + 3] = callData.ServiceOptions;
                    break;
                case 3: // VOICE4 + LC: destination address
                    FneUtils.Write3Bytes(callData.DstId, ref data, rec + 1);
                    break;
                case 4: // VOICE5 + LC: source address
                    FneUtils.Write3Bytes(callData.SrcId, ref data, rec + 1);
                    break;
                case 8: // VOICE9 + LSD
                    data[rec + 1] = callData.LSD1;
                    data[rec + 2] = callData.LSD2;
                    break;
            }
        }

        private static void WriteLdu2RecordFields(byte[] data, int rec, int index, RemoteCallData callData)
        {
            switch (index)
            {
                // VOICE12..14 carry the 9-byte MI, which is all zero for clear voice
                case 5: // VOICE15 + ESS: algorithm ID and key ID
                    data[rec + 1] = callData.AlgorithmId;
                    data[rec + 2] = (byte)(callData.KeyId >> 8);
                    data[rec + 3] = (byte)callData.KeyId;
                    break;
                case 8: // VOICE18 + LSD
                    data[rec + 1] = callData.LSD1;
                    data[rec + 2] = callData.LSD2;
                    break;
            }
        }

        private RemoteCallData CreateCallData(uint streamId) => new()
        {
            SrcId      = _config.PhonePatch.SourceRid,
            DstId      = _config.PhonePatch.Talkgroup,
            LCO        = P25Defines.LC_GROUP,
            TxStreamID = streamId,
        };

        // ---- DMR / NXDN stubs (required by FneSystemBase) ----

        protected override bool DMRDataValidate(uint peerId, uint srcId, uint dstId,
            byte slot, CallType callType, FrameType frameType, DMRDataType dataType,
            uint streamId, byte[] message) => false;

        protected override void DMRDataReceived(object sender, DMRDataReceivedEvent e) { }

        protected override bool NXDNDataValidate(uint peerId, uint srcId, uint dstId,
            CallType callType, NXDNMessageType messageType, FrameType frameType,
            uint streamId, byte[] message) => false;

        protected override void NXDNDataReceived(object sender, NXDNDataReceivedEvent e) { }

        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId,
            byte slot, CallType callType, FrameType frameType, DMRDataType dataType,
            uint streamId) => false;

        protected override void KeyResponse(object sender, KeyResponseEvent e) { }

        // ---- Helpers ----

        private static int ImbeOffset(int index) =>
            P25_MSG_HDR_SIZE + DfsiRecordOffsets[index] + ImbeOffsetInRecord[index];

        private static bool HasValidDfsiRecords(byte[] data, byte firstTag)
        {
            if (data.Length < P25_MSG_HDR_SIZE + DfsiLength) return false;
            for (int i = 0; i < FramesPerLdu; i++)
                if (data[P25_MSG_HDR_SIZE + DfsiRecordOffsets[i]] != firstTag + i) return false;
            return true;
        }

        private CallSlot? FindSlotForIncoming(uint srcId, uint dstId)
        {
            // Prefer a slot already active for this stream
            foreach (var slot in _slots)
                if (slot.StateMachine.IsActiveFor(dstId)) return slot;

            // Otherwise take first idle slot
            foreach (var slot in _slots)
                if (slot.StateMachine.IsIdle) return slot;

            return null;
        }
    }
}
