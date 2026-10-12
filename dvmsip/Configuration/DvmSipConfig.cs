// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace dvmsip.Configuration
{
    public class LogConfig
    {
        [YamlMember(Alias = "displayLevel")]
        public int DisplayLevel { get; set; } = 1;

        [YamlMember(Alias = "fileLevel")]
        public int FileLevel { get; set; } = 1;

        [YamlMember(Alias = "filePath")]
        public string FilePath { get; set; } = "./dvmsip.log";
    }

    public class SystemConfig
    {
        [YamlMember(Alias = "identity")]
        public string Identity { get; set; } = "DVMSIP-1";
    }

    public class NetworkConfig
    {
        [YamlMember(Alias = "address")]
        public string Address { get; set; } = "127.0.0.1";

        [YamlMember(Alias = "port")]
        public int Port { get; set; } = 62031;

        /// <summary>Local UDP port hint (0 = any). FnePeer does not expose local port binding; kept for DVM config parity.</summary>
        [YamlMember(Alias = "local")]
        public int Local { get; set; } = 0;

        [YamlMember(Alias = "peerId")]
        public uint PeerId { get; set; } = 9000001;

        [YamlMember(Alias = "password")]
        public string Password { get; set; } = "s3cr3t";

        [YamlMember(Alias = "jitter")]
        public int Jitter { get; set; } = 360;
    }

    public class SipConfig
    {
        [YamlMember(Alias = "registrar")]
        public string Registrar { get; set; } = "sip:pbx.local";

        [YamlMember(Alias = "user")]
        public string User { get; set; } = "phonepatch";

        [YamlMember(Alias = "password")]
        public string Password { get; set; } = "";

        [YamlMember(Alias = "realm")]
        public string Realm { get; set; } = "pbx.local";

        [YamlMember(Alias = "proxy")]
        public string Proxy { get; set; } = "";

        [YamlMember(Alias = "localPort")]
        public int LocalPort { get; set; } = 5060;

        [YamlMember(Alias = "localAddress")]
        public string LocalAddress { get; set; } = "0.0.0.0";
    }

    public class RtpConfig
    {
        [YamlMember(Alias = "portRangeStart")]
        public int PortRangeStart { get; set; } = 16384;

        [YamlMember(Alias = "portRangeEnd")]
        public int PortRangeEnd { get; set; } = 16512;

        [YamlMember(Alias = "dtmfRelay")]
        public bool DtmfRelay { get; set; } = true;
    }

    public class PhonePatchConfig
    {
        [YamlMember(Alias = "mode")]
        public string Mode { get; set; } = "conventional";

        [YamlMember(Alias = "talkgroup")]
        public uint Talkgroup { get; set; } = 1;

        [YamlMember(Alias = "sourceRID")]
        public uint SourceRid { get; set; } = 9000001;

        [YamlMember(Alias = "maxCalls")]
        public int MaxCalls { get; set; } = 1;

        [YamlMember(Alias = "inboundDID")]
        public string InboundDid { get; set; } = "1000";

        [YamlMember(Alias = "outboundNumber")]
        public string OutboundNumber { get; set; } = "";

        /// <summary>Trunked: telephone numbers for P25 implicit dialing, keyed by PSTN address (0 - 255).</summary>
        [YamlMember(Alias = "presetNumbers")]
        public Dictionary<int, string> PresetNumbers { get; set; } = new();

        /// <summary>Trunked: inbound SIP DID (request URI user) to the radio ID it rings.</summary>
        [YamlMember(Alias = "didMap")]
        public Dictionary<string, uint> DidMap { get; set; } = new();

        /// <summary>Trunked: seconds to wait for a radio to answer an inbound call.</summary>
        [YamlMember(Alias = "answerTimeout")]
        public int AnswerTimeout { get; set; } = 30;

        /// <summary>Voice activity threshold (RMS of 16-bit PCM) for keying phone audio onto the radio side.</summary>
        [YamlMember(Alias = "voxThreshold")]
        public int VoxThreshold { get; set; } = 500;

        /// <summary>Milliseconds of silence before a phone-audio transmission is ended.</summary>
        [YamlMember(Alias = "voxHangTime")]
        public int VoxHangTime { get; set; } = 600;

        [YamlIgnore]
        public bool IsTrunked => string.Equals(Mode, "trunked", StringComparison.OrdinalIgnoreCase);
    }

    public class DvmSipConfig
    {
        [YamlMember(Alias = "log")]
        public LogConfig Log { get; set; } = new();

        [YamlMember(Alias = "system")]
        public SystemConfig System { get; set; } = new();

        [YamlMember(Alias = "network")]
        public NetworkConfig Network { get; set; } = new();

        [YamlMember(Alias = "sip")]
        public SipConfig Sip { get; set; } = new();

        [YamlMember(Alias = "rtp")]
        public RtpConfig Rtp { get; set; } = new();

        [YamlMember(Alias = "phonePatch")]
        public PhonePatchConfig PhonePatch { get; set; } = new();

        public static DvmSipConfig LoadFromFile(string path)
        {
            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<DvmSipConfig>(yaml);
        }
    }
}
