using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KindredLogistics.Services
{
    internal class PlayerSettingsService
    {
        const int GLOBAL_PLAYER_ID = 0;

        static readonly string CONFIG_PATH = Path.Combine(BepInEx.Paths.ConfigPath, MyPluginInfo.PLUGIN_NAME);
        static readonly string PLAYER_SETTINGS_PATH = Path.Combine(CONFIG_PATH, "playerSettings.json");
        static readonly string BLACKLIST_SETTINGS_PATH = Path.Combine(CONFIG_PATH, "blacklistSettings.json");
        static readonly string RESERVE_SETTINGS_PATH = Path.Combine(CONFIG_PATH, "reserveSettings.json");

        static readonly JsonSerializerOptions prettyJsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true
        };

        public struct PlayerSettings
        {
            public PlayerSettings()
            {
                DontPullLast = true;
            }

            public bool SortStash { get; set; }
            public bool Pull { get; set; }
            public bool CraftPull { get; set; }
            public bool DontPullLast { get; set; }
            public bool AutoStashMissions { get; set; }
            public bool Conveyor { get; set; }
            public bool Salvage { get; set; }
            public bool UnitSpawner { get; set; }
            public bool Brazier { get; set; }
            public bool Named { get; set; }
            public bool SilentPull { get; set; }
            public bool SilentStash { get; set; }
            public bool Trash { get; set; }
            public bool StashBlacklist { get; set; }
        }

        PlayerSettings defaultSettings = new();

        Dictionary<ulong, PlayerSettings> playerSettings = [];

        // Blacklist: steamId → (guidHash → retainCount)
        Dictionary<ulong, Dictionary<int, int>> blacklistSettings = [];

        public PlayerSettingsService()
        {
            LoadSettings();
            LoadBlacklistSettings();
            LoadReserveSettings();

            if(!playerSettings.ContainsKey(GLOBAL_PLAYER_ID))
            {
                playerSettings[GLOBAL_PLAYER_ID] = new PlayerSettings()
                {
                    SortStash = true,
                    Pull = true,
                    CraftPull = true,
                    AutoStashMissions = true,
                    Conveyor = true,
                    Salvage = true,
                    UnitSpawner = false,
                    Brazier = false,
                    Named = false,
                    Trash = true
                };
                SaveSettings();
            }
        }

        void LoadSettings()
        {
            if(!File.Exists(PLAYER_SETTINGS_PATH))
            {
                SaveSettings();
                return;
            }

            var json = File.ReadAllText(PLAYER_SETTINGS_PATH);
            playerSettings = JsonSerializer.Deserialize<Dictionary<ulong, PlayerSettings>>(json);
        }

        void SaveSettings()
        {
            if (!Directory.Exists(CONFIG_PATH))
                Directory.CreateDirectory(CONFIG_PATH);
            var json = JsonSerializer.Serialize(playerSettings, prettyJsonOptions);
            File.WriteAllText(PLAYER_SETTINGS_PATH, json);
        }

        public bool IsSortStashEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.SortStash && playerSettings[GLOBAL_PLAYER_ID].SortStash;
        }

        public bool ToggleSortStash(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.SortStash = !settings.SortStash;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.SortStash;
        }
        
        public bool TogglePull()
        {
            if (!playerSettings.TryGetValue(GLOBAL_PLAYER_ID, out var settings))
                settings = new PlayerSettings();
            settings.Pull = !settings.Pull;
            playerSettings[GLOBAL_PLAYER_ID] = settings;
            SaveSettings();
            return settings.Pull;
        }

        public bool IsPullEnabled()
        {
            return !playerSettings[GLOBAL_PLAYER_ID].Pull;
        }

        public bool ToggleTrash()
        {
            if (!playerSettings.TryGetValue(GLOBAL_PLAYER_ID, out var settings))
                settings = new PlayerSettings();
            settings.Trash = !settings.Trash;
            playerSettings[GLOBAL_PLAYER_ID] = settings;
            SaveSettings();
            return settings.Trash;
        }

        public bool IsTrashEnabled()
        {
            return !playerSettings[GLOBAL_PLAYER_ID].Trash;
        }

        public bool IsCraftPullEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.CraftPull && playerSettings[GLOBAL_PLAYER_ID].CraftPull;
        }

        public bool ToggleCraftPull(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.CraftPull = !settings.CraftPull;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.CraftPull;
        }

        public bool IsDontPullLastEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.DontPullLast;
        }

        public bool ToggleDontPullLast(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.DontPullLast = !settings.DontPullLast;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.DontPullLast;
        }

        public bool IsAutoStashMissionsEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.AutoStashMissions && playerSettings[GLOBAL_PLAYER_ID].AutoStashMissions;
        }

        public bool ToggleAutoStashMissions(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.AutoStashMissions = !settings.AutoStashMissions;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.AutoStashMissions;
        }

        public bool IsConveyorEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.Conveyor && playerSettings[GLOBAL_PLAYER_ID].Conveyor;
        }

        public bool IsSalvageEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.Salvage && playerSettings[GLOBAL_PLAYER_ID].Salvage;
        }

        public bool ToggleSalvage(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.Salvage = !settings.Salvage;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.Salvage;
        }

        public bool IsUnitSpawnerEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.UnitSpawner && playerSettings[GLOBAL_PLAYER_ID].UnitSpawner;
        }

        public bool ToggleUnitSpawner(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.UnitSpawner = !settings.UnitSpawner;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.UnitSpawner;
        }
        
        public bool IsBrazierEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.Brazier && playerSettings[GLOBAL_PLAYER_ID].Brazier;
        }

        public bool IsSolarEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.Named;
        }

        public bool ToggleSolar(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.Named = !settings.Named;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.Named;
        }

        public bool ToggleBrazier(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.Brazier = !settings.Brazier;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.Brazier;
        }

        public bool ToggleSilentPull(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.SilentPull = !settings.SilentPull;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.SilentPull;
        }

        public bool IsSilentPullEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.SilentPull;
        }

        public bool ToggleSilentStash(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.SilentStash = !settings.SilentStash;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.SilentStash;
        }

        public bool IsSilentStashEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.SilentStash;
        }

        public bool ToggleConveyor(ulong playerId = GLOBAL_PLAYER_ID)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.Conveyor = !settings.Conveyor;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.Conveyor;
        }

        public PlayerSettings GetSettings(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                return new PlayerSettings();
            return settings;
        }

        public PlayerSettings GetGlobalSettings()
        {
            return playerSettings[GLOBAL_PLAYER_ID];
        }

        // Stash Blacklist methods

        public bool IsStashBlacklistEnabled(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = defaultSettings;
            return settings.StashBlacklist;
        }

        public bool ToggleStashBlacklist(ulong playerId)
        {
            if (!playerSettings.TryGetValue(playerId, out var settings))
                settings = new PlayerSettings();
            settings.StashBlacklist = !settings.StashBlacklist;
            playerSettings[playerId] = settings;
            SaveSettings();
            return settings.StashBlacklist;
        }

        void LoadBlacklistSettings()
        {
            if (!File.Exists(BLACKLIST_SETTINGS_PATH))
            {
                blacklistSettings = [];
                return;
            }
            var json = File.ReadAllText(BLACKLIST_SETTINGS_PATH);
            blacklistSettings = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<int, int>>>(json) ?? [];
        }

        void SaveBlacklistSettings()
        {
            if (!Directory.Exists(CONFIG_PATH))
                Directory.CreateDirectory(CONFIG_PATH);
            var json = JsonSerializer.Serialize(blacklistSettings, prettyJsonOptions);
            File.WriteAllText(BLACKLIST_SETTINGS_PATH, json);
        }

        public Dictionary<int, int> GetBlacklist(ulong playerId)
        {
            if (blacklistSettings.TryGetValue(playerId, out var bl))
                return bl;
            return new Dictionary<int, int>();
        }

        public void SetBlacklistEntry(ulong playerId, int guidHash, int count)
        {
            if (!blacklistSettings.TryGetValue(playerId, out var bl))
            {
                bl = new Dictionary<int, int>();
                blacklistSettings[playerId] = bl;
            }
            if (count <= 0)
                bl.Remove(guidHash);
            else
                bl[guidHash] = count;
            SaveBlacklistSettings();
        }

        public void ClearBlacklist(ulong playerId)
        {
            blacklistSettings.Remove(playerId);
            SaveBlacklistSettings();
        }

        // Keep stack multipliers (K0-K9 templates)
        // Key = 0-9, Value = multiplier of max stack size (e.g. 0.5 = half stack)
        static readonly float[] DEFAULT_RESERVE_MULTIPLIERS = [0.5f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f];
        Dictionary<int, float> reserveMultipliers;

        void LoadReserveSettings()
        {
            if (!File.Exists(RESERVE_SETTINGS_PATH))
            {
                reserveMultipliers = new();
                for (int i = 0; i < DEFAULT_RESERVE_MULTIPLIERS.Length; i++)
                    reserveMultipliers[i] = DEFAULT_RESERVE_MULTIPLIERS[i];
                SaveReserveSettings();
                return;
            }
            var json = File.ReadAllText(RESERVE_SETTINGS_PATH);
            reserveMultipliers = JsonSerializer.Deserialize<Dictionary<int, float>>(json);
        }

        void SaveReserveSettings()
        {
            if (!Directory.Exists(CONFIG_PATH))
                Directory.CreateDirectory(CONFIG_PATH);
            var json = JsonSerializer.Serialize(reserveMultipliers, prettyJsonOptions);
            File.WriteAllText(RESERVE_SETTINGS_PATH, json);
        }

        public float GetReserveMultiplier(int templateId)
        {
            if (reserveMultipliers.TryGetValue(templateId, out var mult))
                return mult;
            if (templateId >= 0 && templateId < DEFAULT_RESERVE_MULTIPLIERS.Length)
                return DEFAULT_RESERVE_MULTIPLIERS[templateId];
            return 1f;
        }

        public void SetReserveMultiplier(int templateId, float multiplier)
        {
            reserveMultipliers[templateId] = multiplier;
            SaveReserveSettings();
        }

        public Dictionary<int, float> GetAllReserveMultipliers()
        {
            return reserveMultipliers;
        }
    }
}
