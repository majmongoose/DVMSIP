// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2026 Charles Bricker, K3NOP
 *
 */

using dvmsip.Configuration;

namespace dvmsip.Tests
{
    public class ConfigTests
    {
        [Fact]
        public void ExampleConfig_Loads()
        {
            var config = DvmSipConfig.LoadFromFile(FindExampleConfig());

            Assert.False(config.PhonePatch.IsTrunked);
            Assert.Equal("100", config.PhonePatch.PresetNumbers[1]);
            Assert.Equal("5551234", config.PhonePatch.PresetNumbers[2]);
            Assert.Equal(1234u, config.PhonePatch.DidMap["1000"]);
            Assert.Equal(30, config.PhonePatch.AnswerTimeout);
            Assert.Equal(500, config.PhonePatch.VoxThreshold);
            Assert.Equal(600, config.PhonePatch.VoxHangTime);
        }

        private static string FindExampleConfig()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "configs", "dvmsip-config.example.yml");
                if (File.Exists(path))
                    return path;
            }

            throw new FileNotFoundException("configs/dvmsip-config.example.yml not found above the test directory");
        }
    }
}
