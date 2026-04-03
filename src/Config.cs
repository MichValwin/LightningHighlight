using System;
using System.Globalization;
using ConfigLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace LightningHighlight {
    internal class ModConfig {
        public static readonly string defSafeColor = "#00FF0020";
        public static readonly string defDangerColor = "#FF000020";
        public static readonly int defIntSafeColor = 536936192;
        public static readonly int defIntDangerColor = 536871167;

        public ModConfig(ICoreClientAPI api, Mod mod) {
            ModId = mod.Info.ModID;

            HotkeyCode = $"toggle{ModId}";
            ThreadName = $"{ModId}Worker";
            TaskCode = $"{ModId}Task";
            configFile = "lightninghighlight-config.json";

            data = readConfig(api);
            save(api);
        }
        public string ModId { get; private set; }
        public string HotkeyCode { get; private set; }
        public string ThreadName { get; private set; }
        public string TaskCode { get; private set; }

        public int HighlighSlot { get; } = 5229;

        public int ChunkRadius {
            get => data.ChunkRadius;
            set => data.ChunkRadius = value;
        }

        public int parsedSafeColor { get; private set; }

        public int parsedLightningHitColor { get; private set; }

        public string HotkeyDescriptionString => Lang.Get($"{ModId}:hotkeyDescription");

        private readonly string configFile;
        private readonly ConfigData data;


        class ConfigData {
            public int ChunkRadius;
            public string SafeColor;
            public string LightningDangerColor;
        }

        public void save(ICoreClientAPI api) {
            api.StoreModConfig(data, configFile);
        }

        private ConfigData readConfig(ICoreClientAPI api) {
            ConfigData data = null;
            try {
                data = api.LoadModConfig<ConfigData>(configFile);
            } catch (Exception e) {
                api.Logger.Error(e);
            }

            data ??= new ConfigData { ChunkRadius = 2 };
            data.SafeColor ??= defSafeColor;
            data.LightningDangerColor ??= defDangerColor;
            setColors(data, api);

            if (api.ModLoader.IsModEnabled("configlib")) {
                try {
                    subscribeToConfigChange(api);
                } catch (Exception ex) {
                    api.Logger.Error("Error while subscribing to configlib events", ex);
                }
            }

            return data;
        }


        private void subscribeToConfigChange(ICoreAPI api) {
            ConfigLibModSystem system = api.ModLoader.GetModSystem<ConfigLibModSystem>();

            system.SettingChanged += (domain, config, setting) => {
                if (domain != ModId) return;

                setting.AssignSettingValue(data);
                setColors(data, api);
            };

            system.ConfigsLoaded += () => {
                system.GetConfig(ModId)?.AssignSettingsValues(data);
            };
        }

        private void setColors(ConfigData data, ICoreAPI api) {
            if (tryParseColor(data.SafeColor, out int parsedSafe)) {
                parsedSafeColor = parsedSafe;
            } else {
                api.Logger.Error("Error parsing lightning safe color. Setting default safe color");
                parsedSafeColor = defIntSafeColor;
                data.SafeColor = defSafeColor;
            }

            if (tryParseColor(data.LightningDangerColor, out int parsedDanger)) {
                parsedLightningHitColor = parsedDanger;
            } else {
                api.Logger.Error("Error parsing lightning danger color. Setting default danger color");
                parsedLightningHitColor = defIntDangerColor;
                data.LightningDangerColor = defDangerColor;
            }

        }

        private static bool tryParseColor(string color, out int result) {
            result = 0;
            if (color == null || color.Length < 7) return false;
            if (!int.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, null, out int r)) return false;
            if (!int.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, null, out int g)) return false;
            if (!int.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, null, out int b)) return false;
            int a = (color.Length < 8) ? 255 : int.TryParse(color.AsSpan(7, 2), NumberStyles.HexNumber, null, out int alpha) ? alpha : 255;
            result = ColorUtil.ReverseColorBytes(ColorUtil.ToRgba(a, r, g, b));
            return true;
        }
    }
}
