using System;
using System.Collections;
using System.Globalization;
using System.IO;
using LitJson;

namespace VisualDodge
{
    // The settings in config.json in the mod's folder. A missing file or value keeps the default.
    internal static class Config
    {
        public static bool Enable = true;
        // How many seconds before an enemy's attack warning reaches the player (the moment the perfect-dodge window
        // opens) the glow comes: the player's reaction time. 0 = when the window opens.
        public static float ReactionSeconds = 0.2f;
        // Length of the glow; 0 = 0.25 s.
        public static float FlashSeconds = 0.25f;
        // The game's display clip that makes the character glow: a path in Wukong's clip folder
        // (/Game/00Main/VFX/Characters/sunwukong/DBC/) or a full /Game/ path.
        public static string Clip = "Talent/DBC_wukong_buff_precisedodge";

        public static void Load()
        {
            string path = Path.Combine(ModLog.Folder, "config.json");
            if (!File.Exists(path))
            {
                ModLog.Info("config.json not found: default settings.");
                return;
            }
            try
            {
                JsonData json = JsonMapper.ToObject(File.ReadAllText(path));
                Enable = GetBool(json, "Enable", Enable);
                ReactionSeconds = Clamp(GetFloat(json, "ReactionSeconds", ReactionSeconds), 0f, 2f);
                FlashSeconds = Clamp(GetFloat(json, "FlashSeconds", FlashSeconds), 0f, 2f);
                Clip = GetString(json, "Clip", Clip);
                ModLog.Info($"config.json read: glow {ReactionSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s before the attack, " +
                            $"{FlashSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s long, clip {Clip}{(Enable ? "" : ", turned off")}.");
            }
            catch (Exception e)
            {
                ModLog.Error($"config.json: {e.Message} (default settings for the rest)");
            }
        }

        private static bool Has(JsonData json, string key)
        {
            return json.IsObject && ((IDictionary)json).Contains(key);
        }

        private static bool GetBool(JsonData json, string key, bool fallback)
        {
            return Has(json, key) && json[key].IsBoolean ? (bool)json[key] : fallback;
        }

        private static string GetString(JsonData json, string key, string fallback)
        {
            return Has(json, key) && json[key].IsString && ((string)json[key]).Trim().Length > 0 ? ((string)json[key]).Trim() : fallback;
        }

        private static float GetFloat(JsonData json, string key, float fallback)
        {
            return Has(json, key) ? ToFloat(json[key], fallback) : fallback;
        }

        private static float ToFloat(JsonData value, float fallback)
        {
            if (value.IsDouble)
            {
                return (float)(double)value;
            }
            if (value.IsInt)
            {
                return (int)value;
            }
            return fallback;
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
