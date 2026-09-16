// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using System.Net;
using System.Threading.Channels;
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
        // IMBE byte offsets within a 216-byte P25 LDU FNE payload.
        // Verified from dvmconsole/FneSystemBase.P25.cs.
        private static readonly int[] LduImbeOffsets = { 10, 26, 55, 80, 105, 130, 155, 180, 204 };

        private const int ImbeFrameBytes = 11;

        private readonly DvmSipConfig _config;
        private readonly IReadOnlyList<CallSlot> _slots;

        public FnePeer Peer => (FnePeer)fne;

        // ---- Constructor ----

        public SipPeerSystem(DvmSipConfig config, IReadOnlyList<CallSlot> slots)
            : base(CreatePeer(config))
        {
            _config = config;
            _slots  = slots;

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

            // Find a slot that is active for this talkgroup, or the first idle slot
            var slot = FindSlotForIncoming(e.SrcId, e.DstId);
            if (slot == null) return;

            // Extract 9 IMBE frames from the LDU payload and forward to the audio bridge
            foreach (int offset in LduImbeOffsets)
            {
                if (offset + ImbeFrameBytes > e.Data.Length) break;

                var imbe = new byte[ImbeFrameBytes];
                Buffer.BlockCopy(e.Data, offset, imbe, 0, ImbeFrameBytes);
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
        /// Starts a background task per slot that drains ImbeToFne and assembles 9-frame LDU1 packets
        /// to transmit toward the FNE talkgroup (SIP → radio direction).
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
            var frames = new byte[9][];
            int frameCount = 0;

            try
            {
                await foreach (var imbe in slot.Bridge.ImbeToFne.Reader.ReadAllAsync(ct))
                {
                    frames[frameCount++] = imbe;
                    if (frameCount == 9)
                    {
                        TransmitLdu(_config.PhonePatch.SourceRid, _config.PhonePatch.Talkgroup, frames);
                        frames = new byte[9][];
                        frameCount = 0;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                Log.Error(ex, "[FNE] Transmit loop error for slot");
            }
        }

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

        // ---- Transmit P25 audio toward radios ----

        /// <summary>
        /// Sends 9 IMBE frames (one LDU1) toward the FNE talkgroup.
        /// Called by CallStateMachine when the SIP → radio path is active.
        /// </summary>
        public void TransmitLdu(uint srcId, uint dstId, byte[][] imbeFrames)
        {
            if (imbeFrames.Length != 9)
                throw new ArgumentException("Exactly 9 IMBE frames required per LDU", nameof(imbeFrames));

            var callData = new RemoteCallData
            {
                SrcId      = srcId,
                DstId      = dstId,
                LCO        = P25Defines.LC_GROUP,
                TxStreamID = Peer.pktSeq(),
            };

            // Build 216-byte LDU1 payload with IMBE frames at verified offsets
            var payload = new byte[200];
            CreateP25MessageHdr((byte)P25DUID.LDU1, callData, ref payload);

            for (int i = 0; i < 9; i++)
                Buffer.BlockCopy(imbeFrames[i], 0, payload, LduImbeOffsets[i], ImbeFrameBytes);

            payload[23] = (byte)(P25_MSG_HDR_SIZE + 9 * ImbeFrameBytes);

            Peer.SendMaster(
                FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
                payload, Peer.pktSeq(), callData.TxStreamID);
        }

        // ---- Helpers ----

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
