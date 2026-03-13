using System;
using System.Globalization;
using ConfigLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace LightningHighlight {
    internal class ModConfig {
        public ModConfig(ICoreClientAPI api, Mod mod) {
            ModId = mod.Info.ModID;

            HotkeyCode = $"toggle{ModId}";
            ThreadName = $"{ModId}Worker";
            TaskCode = $"{ModId}Task";
            configFile = "lightninghighlight-config.json";

            data = ReadConfig(api);
            Save(api);
        }
        public string ModId { get; private set; }
        public string HotkeyCode { get; private set; }
        public string ThreadName { get; private set; }
        public string TaskCode { get; private set; }

        public int HighlighSlot { get; } = 5229;

        public int Radius {
            get => data.Radius;
            set => data.Radius = value;
        }

        public int parsedSafeColor { get; private set; }

        public int parsedLightningHitColor { get; private set; }


        public string HotkeyDescriptionString => Lang.Get($"{ModId}:hotkeyDescription");
        public string EnabledString(bool enabled) => Lang.Get(enabled ? $"{ModId}:disable" : $"{ModId}:enable");

        private readonly string configFile;
        private readonly ConfigData data;


        public void SetSafeColor(int color) {
            parsedSafeColor = color;
            data.SafeColor = serializeColor(color);
        }

        public void SetSafeColor(string color) {
            parsedSafeColor = parseColor(color);
            data.SafeColor = color;
        }



        public void SetSpawnableColor(int color) {
            parsedLightningHitColor = color;
            data.LightningHitColor = serializeColor(color);
        }

        public void SetSpawnableColor(string color) {
            parsedLightningHitColor = parseColor(color);
            data.LightningHitColor = color;
        }


        class ConfigData {
            public int Radius;
            public string SafeColor;
            public string LightningHitColor;
        }

        public void Save(ICoreClientAPI api) {
            api.StoreModConfig(data, configFile);
        }

        private ConfigData ReadConfig(ICoreClientAPI api) {
            ConfigData data = null;
            try {
                data = api.LoadModConfig<ConfigData>(configFile);
            }
            catch (Exception e) {
                api.Logger.Error(e);
            }

            data ??= new ConfigData { Radius = 80 };
            data.SafeColor ??= "#00FF0020";
            data.LightningHitColor ??= "#FF000020";

            parsedSafeColor = parseColor(data.SafeColor);
            parsedLightningHitColor = parseColor(data.LightningHitColor);

            if (api.ModLoader.IsModEnabled("configlib")) {
                SubscribeToConfigChange(api);
            }

            return data;
        }


        private void SubscribeToConfigChange(ICoreAPI api) {
            ConfigLibModSystem system = api.ModLoader.GetModSystem<ConfigLibModSystem>();

            system.SettingChanged += (domain, config, setting) => {
                if (domain != ModId) return;

                setting.AssignSettingValue(data);

                parsedSafeColor = parseColor(data.SafeColor);
                parsedLightningHitColor = parseColor(data.LightningHitColor);
            };

            system.ConfigsLoaded += () => {
                system.GetConfig(ModId)?.AssignSettingsValues(data);
            };
        }

        private static int parseColor(string color) {
            int r = int.Parse(color.Substring(1, 2), NumberStyles.HexNumber);
            int g = int.Parse(color.Substring(3, 2), NumberStyles.HexNumber);
            int b = int.Parse(color.Substring(5, 2), NumberStyles.HexNumber);
            int a = (color.Length < 8) ? 255 : int.Parse(color.Substring(7, 2), NumberStyles.HexNumber);

            return ColorUtil.ReverseColorBytes(ColorUtil.ToRgba(a, r, g, b));
        }

        private static string serializeColor(int color) => ColorUtil.Int2HexRgba(ColorUtil.ReverseColorBytes(color));

    }
}
