// OmniRPGEngine.cs
// Core XP + Rage discipline system with UI shell, leaderboard, admin config editor, and BotReSpawn profile XP UI.
// NOTE: Drop into oxide/plugins (or carbon/plugins) as-is. Data/config files will be created on first load.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OmniRPGEngine", "Somescrub", "0.3.1")]
    [Description("Universal RPG framework providing XP, levels and discipline-based skill trees (Rage MVP).")]
    public class OmniRPGEngine : RustPlugin
    {
        #region Permissions / References

        private const string PERM_USE = "omnirpgengine.use";
        private const string PERM_ADMIN = "omnirpgengine.admin";

        [PluginReference] private Plugin PermissionsManager;
        [PluginReference("ImageLibrary")] private Plugin ImageLibrary;
        [PluginReference("BotReSpawn")] private Plugin BotReSpawn; // Used to detect BotReSpawn NPCs
        [PluginReference]
        private Plugin Economics;

        [PluginReference]
        private Plugin ServerRewards;

        // Track BotReSpawn NPC userIDs so they never pollute player stats / leaderboard
        private readonly HashSet<ulong> botReSpawnIds = new HashSet<ulong>();

        // Per-player Bot XP page index for pagination
        private readonly Dictionary<ulong, int> botXpPage = new Dictionary<ulong, int>();

        #endregion

        #region Config

        private PluginConfig config;

        private class PluginConfig
        {
            public string DataFileName = "OmniRPGEngine_Data";

            public XpSettings XP = new XpSettings();
            public RageSettings Rage = new RageSettings();
            public UiSettings UI = new UiSettings();

            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig();
            }
        }

        private class UiSettings
        {
            public string ProfileCommand = "orpgxp";
            public string RageCommand = "orpgrage";
            public string UiCommand = "orpgui";

            public float AnchorMinX = 0.2f;
            public float AnchorMinY = 0.1f;
            public float AnchorMaxX = 0.8f;
            public float AnchorMaxY = 0.9f;
        }

        private class XpSettings
        {
            public double BaseKillNpc = 25;
            public double BaseKillPlayer = 50;
            public double BaseGatherOre = 2;
            public double BaseGatherWood = 1;
            public double BaseGatherPlants = 1.5;

            // Per-source multipliers so server owners can tune them
            public double BotReSpawnMultiplier = 1.0;
            public double NpcSpawnMultiplier = 1.0;
            public double ZombieHordeMultiplier = 1.0;

            public double LevelCurveBase = 100;    // XP for level 1→2
            public double LevelCurveGrowth = 1.25; // Each level multiplies required XP by this

            // Legacy per BotReSpawn profile multipliers (for migration)
            public Dictionary<string, double> BotReSpawnProfileMultipliers = new Dictionary<string, double>();

            // New full settings (per profile)
            public Dictionary<string, BotProfileXpSettings> BotReSpawnProfiles = new Dictionary<string, BotProfileXpSettings>();
        }

        private class RageSettings
        {
            public bool Enabled = true;

            // How many discipline points per character level
            public double CorePointsPerLevel = 1.0;

            // Fury shared settings
            public float FuryDurationSeconds = 10f;
            public float FuryMaxBonusDamage = 0.3f; // 30% at max Fury
            public float FuryOnKillGain = 0.15f;    // 15% fury per qualifying kill

            // Weapon specialization nodes – values here are per level
            public Dictionary<string, RageNodeConfig> Nodes = new Dictionary<string, RageNodeConfig>
            {
                {
                    "core",
                    new RageNodeConfig
                    {
                        DisplayName = "Rage",
                        MaxLevel = 20,
                        DamageBonusPerLevel = 0.01f,
                        RecoilReductionPerLevel = 0.005f,
                        MoveSpeedPerLevel = 0.0025f
                    }
                },
                {
                    "rifle",
                    new RageNodeConfig
                    {
                        DisplayName = "Rifle Mastery",
                        MaxLevel = 10,
                        DamageBonusPerLevel = 0.02f,
                        CritChancePerLevel = 0.01f
                    }
                },
                {
                    "shotgun",
                    new RageNodeConfig
                    {
                        DisplayName = "Shotgun Savagery",
                        MaxLevel = 10,
                        DamageBonusPerLevel = 0.02f,
                        BleedChancePerLevel = 0.015f
                    }
                },
                {
                    "pistol",
                    new RageNodeConfig
                    {
                        DisplayName = "Pistol Precision",
                        MaxLevel = 10,
                        DamageBonusPerLevel = 0.015f,
                        CritDamagePerLevel = 0.015f
                    }
                }
            };
            
            // NEW: respec settings
            public RespecSettings Respec = new RespecSettings();
        }

        private class RageNodeConfig
        {
            public string DisplayName = "Unnamed";
            public int MaxLevel = 10;

            public float DamageBonusPerLevel = 0f;
            public float CritChancePerLevel = 0f;
            public float CritDamagePerLevel = 0f;
            public float BleedChancePerLevel = 0f;
            public float MoveSpeedPerLevel = 0f;
            public float RecoilReductionPerLevel = 0f;
        }

        // NEW: RespecSettings definition
        private class RespecSettings
        {
            [JsonProperty("Respec Enabled")]
            public bool Enabled = true;

            // economics / serverrewards / item / none
            [JsonProperty("Respec Mode")]
            public string Mode = "economics";

            [JsonProperty("Economics Cost")]
            public double EconomicsCost = 0;

            [JsonProperty("ServerRewards Cost (RP)")]
            public int ServerRewardsCost = 0;

            [JsonProperty("Item Shortname")]
            public string ItemShortname = "scrap";

            [JsonProperty("Item Amount")]
            public int ItemAmount = 0;
        }

        private class BotProfileXpSettings
        {
            public double Multiplier = 1.0;
            public double FlatXp = 0.0;
        }

        protected override void LoadDefaultConfig()
        {
            config = PluginConfig.DefaultConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null)
                {
                    throw new Exception("Config file is null");
                }

                if (config.XP.BotReSpawnProfiles == null)
                    config.XP.BotReSpawnProfiles = new Dictionary<string, BotProfileXpSettings>();

                // Migration from legacy BotReSpawnProfileMultipliers if needed
                if (config.XP.BotReSpawnProfileMultipliers != null &&
                    config.XP.BotReSpawnProfileMultipliers.Count > 0 &&
                    (config.XP.BotReSpawnProfiles == null || config.XP.BotReSpawnProfiles.Count == 0))
                {
                    foreach (var kvp in config.XP.BotReSpawnProfileMultipliers)
                    {
                        config.XP.BotReSpawnProfiles[kvp.Key] = new BotProfileXpSettings
                        {
                            Multiplier = kvp.Value,
                            FlatXp = 0.0
                        };
                    }
                }
            }
            catch
            {
                PrintWarning("Config file is malformed or missing, creating new one.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region Data

        private DynamicConfigFile dataFile;
        private Dictionary<ulong, PlayerData> players = new Dictionary<ulong, PlayerData>();

        private class PlayerData
        {
            public ulong UserId;
            public string LastKnownName;

            public double TotalXp;
            public int Level;
            public double CurrentXp;      // XP accumulated since last level
            public double XpToNextLevel;

            public int UnspentDisciplinePoints;

            public RageData Rage = new RageData();
            public long PlayerKills;
            public long NpcKills;
            public long Deaths;
            public double TotalPlayTimeSeconds;
            [NonSerialized] public float SessionStartTime;

            public PlayerData()
            {
            }

            public PlayerData(ulong id, string name)
            {
                UserId = id;
                LastKnownName = name;
                Level = 1;
                XpToNextLevel = 100;
            }
        }

        private class RageData
        {
            public int TreeLevel;
            public int UnspentPoints;
            public Dictionary<string, int> NodeLevels = new Dictionary<string, int>();

            // Fury
            public float FuryAmount; // 0–1
            public double FuryExpireTimestamp;

            // UI helpers
            public string LastUpgradedNodeId;
            public double LastUpgradeFlashTime;
            public string SelectedNodeId;

            // Progress: highest unlocked Rage tier (1 = Tier 1 only, 2 = Tier 2 unlocked, etc.)
            public int MaxUnlockedTier = 1;

            public RageData()
            {
                FuryAmount = 0f;
                FuryExpireTimestamp = 0;
                LastUpgradedNodeId = null;
                LastUpgradeFlashTime = 0;
                SelectedNodeId = null;
                MaxUnlockedTier = 1;
            }
        }

        private void LoadData()
        {
            dataFile = Interface.Oxide.DataFileSystem.GetFile(config.DataFileName);
            try
            {
                var stored = dataFile.ReadObject<Dictionary<ulong, PlayerData>>();
                if (stored != null)
                {
                    players = stored;
                }
            }
            catch
            {
                PrintWarning("Data file corrupted or missing, starting fresh.");
                players = new Dictionary<ulong, PlayerData>();
            }
        }

        private void SaveData()
        {
            dataFile.WriteObject(players);
        }

        private PlayerData GetOrCreatePlayerData(BasePlayer player)
        {
            if (!IsHumanBasePlayer(player))
                return null;

            var id = player.userID;
            PlayerData data;
            if (!players.TryGetValue(id, out data))
            {
                data = new PlayerData(id, player.displayName);
                data.XpToNextLevel = GetRequiredXpForLevel(1);
                players[id] = data;
            }
            else
            {
                data.LastKnownName = player.displayName;
            }

            return data;
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            LoadConfig();
            LoadData();

            permission.RegisterPermission(PERM_USE, this);
            permission.RegisterPermission(PERM_ADMIN, this);

            cmd.AddChatCommand(config.UI.ProfileCommand, this, CmdProfile);
            cmd.AddChatCommand(config.UI.RageCommand, this, CmdRage);
            cmd.AddChatCommand(config.UI.UiCommand, this, CmdOpenUi);
            cmd.AddChatCommand("orpg", this, CmdOpenUi);
            cmd.AddChatCommand("orpgadmin", this, CmdAdminUi);

            cmd.AddConsoleCommand("omnirpg.ui", this, "CCmdOpenUi");
            cmd.AddConsoleCommand("omnirpg.rage.upgrade", this, "CCmdRageUpgrade");
            cmd.AddConsoleCommand("omnirpg.rage.inspect", this, "CCmdRageInspect");
            cmd.AddConsoleCommand("omnirpg.rage.respec", this, "CCmdRageRespec");

            // Admin config editor commands
            cmd.AddConsoleCommand("omnirpg.admin.adjust", this, "CCmdAdminAdjust");
            cmd.AddConsoleCommand("omnirpg.admin.toggle", this, "CCmdAdminToggle");
            cmd.AddConsoleCommand("omnirpg.admin.save", this, "CCmdAdminSave");

            // Bot XP editor commands
            cmd.AddConsoleCommand("omnirpg.botxp.mult", this, "CCmdBotXpMult");
            cmd.AddConsoleCommand("omnirpg.botxp.flat", this, "CCmdBotXpFlat");
            cmd.AddConsoleCommand("omnirpg.botxp.page", this, "CCmdBotXpPage");
            
            // Register images after server starts
            // (RegisterImages itself is invoked from OnServerInitialized)
        }

        private void RegisterImages()
        {
            if (ImageLibrary == null)
            {
                Puts("[OmniRPG] ImageLibrary is not loaded, cannot register images.");
                return;
            }

            // Use raw GitHub URL for the Disciplines background
            string url = "https://raw.githubusercontent.com/jmg5215/OmniRPGEngine/refs/heads/main/assets/ui_images/Disciplines/disciplines_bg.png";

            ImageLibrary.Call("AddImage", url, IMAGE_DISCIPLINES_BG, 0UL);
            Puts($"[OmniRPG] Queued Disciplines background image: {url}");
        }

        private void OnServerInitialized()
        {
            RegisterImages();
        }

        private void OnServerSave()
        {
            foreach (var bp in BasePlayer.activePlayerList)
            {
                var data = GetOrCreatePlayerData(bp);
                if (data != null)
                {
                    data.TotalPlayTimeSeconds += Time.realtimeSinceStartup - data.SessionStartTime;
                    data.SessionStartTime = Time.realtimeSinceStartup;
                }
            }
            SaveData();
        }

        private void Unload()
        {
            SaveData();
            foreach (var pl in BasePlayer.activePlayerList)
            {
                DestroyProfileUi(pl);
            }
        }

        private void OnPlayerInit(BasePlayer player)
        {
            var data = GetOrCreatePlayerData(player);
            if (data != null)
            {
                data.SessionStartTime = Time.realtimeSinceStartup;
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            var data = GetOrCreatePlayerData(player);
            if (data != null)
            {
                data.TotalPlayTimeSeconds += Time.realtimeSinceStartup - data.SessionStartTime;
            }
            SaveData();
            DestroyProfileUi(player);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            var killer = info.InitiatorPlayer;
            if (killer == null || !killer.IsConnected) return;
            if (!permission.UserHasPermission(killer.UserIDString, PERM_USE))
                return;

            // Identify victim types
            var victimPlayer = entity as BasePlayer;
            var victimNpc = entity as BaseNpc;

            // Skip BotReSpawn NPCs entirely — they are handled by OnBotReSpawnNPCKilled
            if (victimPlayer != null && IsBotReSpawnNpc(victimPlayer))
                return;

            var killerData = GetOrCreatePlayerData(killer);
            if (killerData == null) return;

            double xp = 0;

            if (victimPlayer != null)
            {
                if (victimPlayer == killer)
                {
                    // Suicide or self-inflicted, no XP
                    xp = 0;
                }
                else
                {
                    // Real player kill
                    xp = config.XP.BaseKillPlayer;
                    killerData.PlayerKills++;

                    var victimData = GetOrCreatePlayerData(victimPlayer);
                    if (victimData != null)
                        victimData.Deaths++;
                }
            }
            else if (victimNpc != null)
            {
                // Non-player NPCs (animals, etc.)
                xp = config.XP.BaseKillNpc;
                killerData.NpcKills++;
            }

            if (xp <= 0) return;

            AwardXp(killer, xp, "Kill");
            OnRageKillEvent(killer, entity);
            SaveData();
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity as BasePlayer;
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;
            if (!IsHumanBasePlayer(player)) return;

            double xp = 0;

            if (dispenser.gatherType == ResourceDispenser.GatherType.Ore)
            {
                xp = config.XP.BaseGatherOre * item.amount;
            }
            else if (dispenser.gatherType == ResourceDispenser.GatherType.Tree)
            {
                xp = config.XP.BaseGatherWood * item.amount;
            }

            if (xp > 0)
                AwardXp(player, xp, "Gather");
        }

        private void OnCollectiblePickup(Item item, BasePlayer player)
        {
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;
            if (!IsHumanBasePlayer(player)) return;

            var def = item.info;
            if (def == null) return;

            double xp = 0;

            if (def.shortname.Contains("corn") || def.shortname.Contains("pumpkin") ||
                def.shortname.Contains("hemp") || def.shortname.Contains("mushroom"))
            {
                xp = config.XP.BaseGatherPlants * item.amount;
            }

            if (xp > 0)
                AwardXp(player, xp, "Plants");
        }

        #endregion

        #region BotReSpawn Integration

        // Called when a BotReSpawn NPC is spawned
        private void OnBotReSpawnNPCSpawned(ScientistNPC npc, string profilename, string group)
        {
            if (npc == null) return;

            if (!botReSpawnIds.Contains(npc.userID))
                botReSpawnIds.Add(npc.userID);

            // Ensure we have a config entry for this profile
            EnsureBotProfileSettings(profilename);
        }

        // Called when a BotReSpawn NPC is killed
        private void OnBotReSpawnNPCKilled(ScientistNPC npc, string profilename, string group, HitInfo info)
        {
            if (npc == null || info == null) return;

            botReSpawnIds.Add(npc.userID);

            var killer = info.InitiatorPlayer;
            if (killer == null || !killer.IsConnected) return;
            if (!permission.UserHasPermission(killer.UserIDString, PERM_USE)) return;
            if (!IsHumanBasePlayer(killer)) return;

            var killerData = GetOrCreatePlayerData(killer);
            if (killerData == null) return;

            killerData.NpcKills++;


            var settings = EnsureBotProfileSettings(profilename);

            // Base XP from global + profile multiplier
            var baseXp = config.XP.BaseKillNpc * config.XP.BotReSpawnMultiplier * settings.Multiplier;

            // Additive flat XP per your choice (B)
            var xp = baseXp + settings.FlatXp;

            AwardXp(killer, xp, $"BotReSpawn ({profilename})");
            OnRageKillEvent(killer, npc);
            SaveData();
        }

        #endregion

        #region External API (XPerience-Compatible)

        [HookMethod("GiveXPID")]
        public void GiveXPID(ulong playerId, double amount)
        {
            var player = BasePlayer.FindByID(playerId);
            if (player == null || amount == 0) return;
            AwardXp(player, amount, "API_ID");
        }

        [HookMethod("GiveXP")]
        public void GiveXP(BasePlayer player, double amount)
        {
            if (player == null || amount == 0) return;
            AwardXp(player, amount, "API");
        }

        [HookMethod("GiveXPBasic")]
        public void GiveXPBasic(BasePlayer player, double amount)
        {
            if (player == null || amount == 0) return;
            AwardXp(player, amount, "API_BASIC");
        }

        #endregion

        #region XP Core

        private double GetRequiredXpForLevel(int level)
        {
            return config.XP.LevelCurveBase * Math.Pow(config.XP.LevelCurveGrowth, level - 1);
        }

        private void AwardXp(BasePlayer player, double amount, string reason)
        {
            if (player == null || amount <= 0) return;
            if (!IsHumanBasePlayer(player)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            data.TotalXp += amount;
            data.CurrentXp += amount;

            while (data.CurrentXp >= data.XpToNextLevel)
            {
                data.CurrentXp -= data.XpToNextLevel;
                data.Level++;

                data.XpToNextLevel = GetRequiredXpForLevel(data.Level);

                var gainedPoints = config.Rage.CorePointsPerLevel;
                data.UnspentDisciplinePoints += (int)Math.Round(gainedPoints);

                data.Rage.UnspentPoints++;

                player.ChatMessage(
                    $"<color=#ffb74d>[OmniRPG]</color> You reached <color=#ffeb3b>level {data.Level}</color>! " +
                    $"You gained <color=#b3e5fc>+{gainedPoints:0}</color> discipline point(s) and <color=#f48fb1>+1</color> Rage point.");
            }

            player.SendConsoleCommand("gametip.showtoast", 1, $"<size=14><color=#ffb74d>+{amount:0} XP</color> ({reason})</size>");

            SaveData();
        }

        #endregion

        #region Utility

        private string FormatTime(double seconds)
        {
            if (seconds <= 0) return "0m";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{t.Minutes}m {t.Seconds}s";
        }

        // Basic SteamID64 sanity check – real players only
        private bool IsLikelyHumanSteamId(ulong id)
        {
            var idStr = id.ToString();
            if (idStr.Length != 17) return false;
            if (!idStr.StartsWith("7656")) return false;
            return true;
        }

        private bool IsBotReSpawnId(ulong id)
        {
            if (botReSpawnIds.Contains(id))
                return true;

            if (BotReSpawn != null)
            {
                try
                {
                    var result = BotReSpawn.Call("IsBotReSpawn", id);
                    if (result is bool b && b)
                        return true;
                }
                catch { }
            }

            return false;
        }

        private bool IsBotReSpawnNpc(BasePlayer player)
        {
            if (player == null) return false;

            // Ask BotReSpawn directly if this NPC is one of its bots
            if (BotReSpawn != null && player is NPCPlayer npc)
            {
                try
                {
                    var result = BotReSpawn.Call("IsBotReSpawn", npc);
                    if (result is bool b && b)
                        return true;
                }
                catch
                {
                    // ignore errors from BotReSpawn
                }
            }

            // Check our tracked BotReSpawn IDs
            if (botReSpawnIds.Contains(player.userID))
                return true;

            // Fallback: use the ID-based API
            return IsBotReSpawnId(player.userID);
        }

        private bool IsHumanBasePlayer(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsNpc) return false;
            if (IsBotReSpawnNpc(player)) return false;

            return IsLikelyHumanSteamId(player.userID);
        }

        private bool IsHumanPlayerData(PlayerData data)
        {
            if (data == null) return false;
            if (!IsLikelyHumanSteamId(data.UserId)) return false;
            if (IsBotReSpawnId(data.UserId)) return false;

            return true;
        }

        // Ensure a BotReSpawn profile has settings in config, return current settings
        private BotProfileXpSettings EnsureBotProfileSettings(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                profileName = "UnnamedProfile";

            if (config.XP.BotReSpawnProfiles == null)
                config.XP.BotReSpawnProfiles = new Dictionary<string, BotProfileXpSettings>();

            BotProfileXpSettings settings;
            if (!config.XP.BotReSpawnProfiles.TryGetValue(profileName, out settings))
            {
                settings = new BotProfileXpSettings { Multiplier = 1.0, FlatXp = 0.0 };
                config.XP.BotReSpawnProfiles[profileName] = settings;
                SaveConfig();
            }

            return settings;
        }

        private int GetBotXpPage(BasePlayer player)
        {
            int page;
            if (!botXpPage.TryGetValue(player.userID, out page))
                page = 0;
            return page;
        }

        private void SetBotXpPage(BasePlayer player, int page)
        {
            botXpPage[player.userID] = page;
        }

        #endregion

        #region Rage Discipline

        private RageNodeConfig GetRageNodeConfig(string nodeId)
        {
            RageNodeConfig def;
            if (!config.Rage.Nodes.TryGetValue(nodeId, out def))
                return null;
            return def;
        }

        private int GetRageNodeLevel(PlayerData data, string nodeId)
        {
            int lvl;
            if (data.Rage.NodeLevels.TryGetValue(nodeId, out lvl))
                return lvl;
            return 0;
        }

        private string GetRageNodeDescription(string nodeId)
        {
            switch (nodeId)
            {
                case "core":
                    return "Core Rage: minor bonuses to all damage, recoil and movement speed.";
                case "rifle":
                    return "Rifle Mastery: increases damage and headshot potential with rifles.";
                case "shotgun":
                    return "Shotgun Savagery: boosts close-range damage and bleed effects.";
                case "pistol":
                    return "Pistol Precision: improves pistol damage and critical strike potential.";
                default:
                    return string.Empty;
            }
        }

        // Uses ImageLibrary (if present) to fetch a sprite ID for Rage node icons.
        // Configure images in ImageLibrary with these keys:
        //   omnirpg_rage_core, omnirpg_rage_rifle, omnirpg_rage_shotgun, omnirpg_rage_pistol
        private string GetRageNodeIconSprite(string nodeId)
        {
            if (ImageLibrary == null) return string.Empty;

            string key = null;
            switch (nodeId)
            {
                case "core":
                    key = "omnirpg_rage_core";
                    break;
                case "rifle":
                    key = "omnirpg_rage_rifle";
                    break;
                case "shotgun":
                    key = "omnirpg_rage_shotgun";
                    break;
                case "pistol":
                    key = "omnirpg_rage_pistol";
                    break;
            }

            if (string.IsNullOrEmpty(key))
                return string.Empty;

            try
            {
                var result = ImageLibrary.Call("GetImage", key);
                if (result is string s && !string.IsNullOrEmpty(s))
                    return s;
            }
            catch
            {
                // ignore image errors
            }

            return string.Empty;
        }

        private float GetRageDamageBonus(PlayerData data, Item weapon)
        {
            if (!config.Rage.Enabled) return 0f;
            if (data == null) return 0f;

            float bonus = 0f;

            var coreCfg = GetRageNodeConfig("core");
            if (coreCfg != null)
            {
                var coreLvl = GetRageNodeLevel(data, "core");
                bonus += coreCfg.DamageBonusPerLevel * coreLvl;
            }

            if (weapon != null)
            {
                var shortname = weapon.info.shortname;

                if (shortname.Contains("rifle"))
                {
                    var cfg = GetRageNodeConfig("rifle");
                    if (cfg != null)
                    {
                        var lvl = GetRageNodeLevel(data, "rifle");
                        bonus += cfg.DamageBonusPerLevel * lvl;
                    }
                }
                else if (shortname.Contains("shotgun"))
                {
                    var cfg = GetRageNodeConfig("shotgun");
                    if (cfg != null)
                    {
                        var lvl = GetRageNodeLevel(data, "shotgun");
                        bonus += cfg.DamageBonusPerLevel * lvl;
                    }
                }
                else if (shortname.Contains("pistol") || shortname.Contains("revolver") ||
                         shortname.Contains("nailgun"))
                {
                    var cfg = GetRageNodeConfig("pistol");
                    if (cfg != null)
                    {
                        var lvl = GetRageNodeLevel(data, "pistol");
                        bonus += cfg.DamageBonusPerLevel * lvl;
                    }
                }
            }

            if (data.Rage.FuryAmount > 0f && Time.realtimeSinceStartup < data.Rage.FuryExpireTimestamp)
            {
                bonus += config.Rage.FuryMaxBonusDamage * Mathf.Clamp01(data.Rage.FuryAmount);
            }

            return bonus;
        }

        private void OnRageKillEvent(BasePlayer killer, BaseCombatEntity victim)
        {
            if (!config.Rage.Enabled) return;
            if (!IsHumanBasePlayer(killer)) return;

            var data = GetOrCreatePlayerData(killer);
            if (data == null) return;

            data.Rage.FuryAmount = Mathf.Clamp01(data.Rage.FuryAmount + config.Rage.FuryOnKillGain);
            data.Rage.FuryExpireTimestamp = Time.realtimeSinceStartup + config.Rage.FuryDurationSeconds;

            killer.SendConsoleCommand("gametip.showtoast", 1,
                $"<size=14><color=#e57373>Fury</color> {data.Rage.FuryAmount * 100f:0}%</size>");
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            var attacker = info.InitiatorPlayer;
            if (attacker == null) return;
            if (!permission.UserHasPermission(attacker.UserIDString, PERM_USE)) return;
            if (!IsHumanBasePlayer(attacker)) return;

            var data = GetOrCreatePlayerData(attacker);
            if (data == null) return;

            var weapon = info.Weapon?.GetItem();
            var bonus = GetRageDamageBonus(data, weapon);
            if (bonus <= 0f) return;

            info.damageTypes.ScaleAll(1f + bonus);
        }

        private bool TryChargeRespecCost(BasePlayer player)
        {
            if (player == null)
                return false;

            var r = config.Rage.Respec;
            if (!r.Enabled || string.IsNullOrEmpty(r.Mode))
            {
                // Respec is effectively free / disabled cost
                return true;
            }

            string mode = r.Mode.ToLowerInvariant();

            if (mode == "economics")
            {
                if (Economics == null)
                {
                    player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Respec cost is set to Economics, but the Economics plugin is not loaded.");
                    return false;
                }

                double balance = 0;
                var result = Economics.Call("Balance", player.UserIDString);
                if (result is double d) balance = d;
                else if (result is float f) balance = f;
                else if (result is int i) balance = i;

                if (balance < r.EconomicsCost)
                {
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> You need <color=#e57373>{r.EconomicsCost:0}</color> coins to respec.");
                    return false;
                }

                Economics.Call("Withdraw", player.UserIDString, r.EconomicsCost);
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Spent <color=#e57373>{r.EconomicsCost:0}</color> coins to respec.");
                return true;
            }

            if (mode == "serverrewards" || mode == "rustrewards" || mode == "rp")
            {
                if (ServerRewards == null)
                {
                    player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Respec cost is set to ServerRewards, but that plugin is not loaded.");
                    return false;
                }

                int cost = r.ServerRewardsCost;
                if (cost <= 0)
                    return true;

                // ServerRewards API: bool TakePoints(BasePlayer player, int amount)
                bool success = false;
                var result = ServerRewards.Call("TakePoints", player, cost);
                if (result is bool b) success = b;

                if (!success)
                {
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> You need <color=#e57373>{cost}</color> RP to respec.");
                    return false;
                }

                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Spent <color=#e57373>{cost}</color> RP to respec.");
                return true;
            }

            if (mode == "item")
            {
                if (string.IsNullOrEmpty(r.ItemShortname) || r.ItemAmount <= 0)
                    return true;

                var def = ItemManager.FindItemDefinition(r.ItemShortname);
                if (def == null)
                {
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Respec item shortname '<color=#e57373>{r.ItemShortname}</color>' is invalid.");
                    return false;
                }

                int have = player.inventory.GetAmount(def.itemid);
                if (have < r.ItemAmount)
                {
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> You need <color=#e57373>{r.ItemAmount}</color> x <color=#e57373>{def.displayName.english}</color> to respec.");
                    return false;
                }

                player.inventory.Take(null, def.itemid, r.ItemAmount);
                try
                {
                    // Ensure the player's inventory is updated on the server/client
                    // Oxide v2 PlayerInventory.ServerUpdate requires a float delta parameter
                    player.inventory?.ServerUpdate(0f);
                }
                catch
                {
                    // Some server builds may not expose ServerUpdate; ignore if unavailable
                }

                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Spent <color=#e57373>{r.ItemAmount}</color> x <color=#e57373>{def.displayName.english}</color> to respec.");
                return true;
            }

            // Unknown mode or "none" -> treat as free
            return true;
        }
        #endregion

        #region Chat + Console Commands

        private bool HasPerm(BasePlayer player, string perm, bool notify = true)
        {
            if (player == null) return false;

            if (!permission.UserHasPermission(player.UserIDString, perm))
            {
                if (notify)
                    player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> You don't have permission to use this command.");
                return false;
            }

            return true;
        }

        private void CmdProfile(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PERM_USE)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null)
            {
                player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> No profile data found. Reconnect if this persists.");
                return;
            }

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> Level <color=#ffeb3b>{data.Level}</color>  " +
                $"XP <color=#b3e5fc>{data.CurrentXp:0}/{data.XpToNextLevel:0}</color>  " +
                $"Total XP <color=#b3e5fc>{data.TotalXp:0}</color>\n" +
                $"Discipline Points: <color=#f48fb1>{data.UnspentDisciplinePoints}</color>  " +
                $"Rage Points: <color=#e57373>{data.Rage.UnspentPoints}</color>  " +
                $"Fury: <color=#e57373>{data.Rage.FuryAmount * 100f:0}%</color>\n" +
                $"Player Kills: <color=#ffcc80>{data.PlayerKills}</color> | " +
                $"NPC Kills: <color=#ffcc80>{data.NpcKills}</color> | " +
                $"Deaths: <color=#ffcc80>{data.Deaths}</color>");
        }

        private void CmdRage(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PERM_USE)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            if (args == null || args.Length == 0)
            {
                ShowRageSummary(player, data);
                return;
            }

            var sub = args[0].ToLower();

            if (sub == "add" && args.Length >= 3)
            {
                if (!HasPerm(player, PERM_ADMIN)) return;

                var nodeId = args[1].ToLower();
                int points;
                if (!int.TryParse(args[2], out points) || points <= 0)
                {
                    player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Usage: /orpgrage add <node> <points>");
                    return;
                }

                AllocateRagePoints(player, data, nodeId, points);
            }
            else if (sub == "respec")
            {
                if (!HasPerm(player, PERM_ADMIN)) return;

                ResetRageTree(player, data);
            }
            else
            {
                ShowRageSummary(player, data);
            }
        }

        private void AllocateRagePoints(BasePlayer player, PlayerData data, string nodeId, int points)
        {
            RageNodeConfig cfg;
            if (!config.Rage.Nodes.TryGetValue(nodeId, out cfg))
            {
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Unknown Rage node '{nodeId}'.");
                return;
            }

            int current = GetRageNodeLevel(data, nodeId);
            int maxAlloc = cfg.MaxLevel - current;
            if (maxAlloc <= 0)
            {
                player.ChatMessage(
                    $"<color=#ffb74d>[OmniRPG]</color> Node <color=#e57373>{cfg.DisplayName}</color> is already at max level.");
                return;
            }

            int spend = Math.Min(points, maxAlloc);
            if (spend > data.Rage.UnspentPoints)
            {
                player.ChatMessage(
                    $"<color=#ffb74d>[OmniRPG]</color> Not enough Rage points. You have <color=#e57373>{data.Rage.UnspentPoints}</color>.");
                return;
            }

            data.Rage.UnspentPoints -= spend;
            int newLevel = current + spend;
            data.Rage.NodeLevels[nodeId] = newLevel;

            // Record for tree flash highlight
            data.Rage.LastUpgradedNodeId = nodeId;
            data.Rage.LastUpgradeFlashTime = Time.realtimeSinceStartup;

            // Tier unlock: core node is the Tier 1 "Super Skill".
            // When it reaches max level, unlock Tier 2.
            if (nodeId.Equals("core", StringComparison.OrdinalIgnoreCase) &&
                newLevel >= cfg.MaxLevel &&
                data.Rage.MaxUnlockedTier < 2)
            {
                data.Rage.MaxUnlockedTier = 2;
                player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Your mastery of Rage unlocks <color=#e57373>Tier 2</color>!");
            }

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> Allocated <color=#e57373>{spend}</color> point(s) to " +
                $"<color=#e57373>{cfg.DisplayName}</color>. New level: <color=#e57373>{newLevel}/{cfg.MaxLevel}</color>. " +
                $"Remaining Rage points: <color=#e57373>{data.Rage.UnspentPoints}</color>");

            SaveData();
        }

        private void ResetRageTree(BasePlayer player, PlayerData data)
        {
            int spent = data.Rage.NodeLevels.Values.Sum();
            data.Rage.NodeLevels.Clear();
            data.Rage.UnspentPoints += spent;

            // Reset Rage tier progression and UI helpers
            data.Rage.MaxUnlockedTier = 1;
            data.Rage.LastUpgradedNodeId = null;
            data.Rage.LastUpgradeFlashTime = 0;
            data.Rage.SelectedNodeId = null;

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> Rage tree reset. Refunded <color=#e57373>{spent}</color> points.");

            SaveData();
        }

        private void ShowRageSummary(BasePlayer player, PlayerData data)
        {
            var parts = new List<string>
            {
                $"<color=#ffb74d>[OmniRPG]</color> <color=#e57373>Rage</color> tree:",
                $"Unspent Rage points: <color=#e57373>{data.Rage.UnspentPoints}</color>",
                $"Fury: <color=#e57373>{data.Rage.FuryAmount * 100f:0}%</color>"
            };

            foreach (var kvp in config.Rage.Nodes)
            {
                var nodeId = kvp.Key;
                var cfg = kvp.Value;
                var lvl = GetRageNodeLevel(data, nodeId);

                parts.Add(
                    $"{cfg.DisplayName}: <color=#e57373>{lvl}/{cfg.MaxLevel}</color>");
            }

            player.ChatMessage(string.Join("\n", parts));
        }

        // Console command used by Rage UI "Upgrade" buttons
        [ConsoleCommand("omnirpg.rage.inspect")]
        private void CCmdRageInspect(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_USE, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length == 0) return;

            var nodeId = args[0].ToLower();
            RageNodeConfig cfg;
            if (!config.Rage.Nodes.TryGetValue(nodeId, out cfg))
                return;

            data.Rage.SelectedNodeId = nodeId;
            SaveData();

            // Reopen Rage page so the right-hand context updates
            ShowMainUi(player, data, "rage");
        }

        [ConsoleCommand("omnirpg.rage.upgrade")]
        private void CCmdRageUpgrade(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_USE, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length == 0) return;

            var nodeId = args[0].ToLower();

            // Spend 1 point per click from UI
            AllocateRagePoints(player, data, nodeId, 1);

            // Small visual flash to acknowledge the upgrade
            ShowRageUpgradeFlash(player);

            // Refresh Rage UI page
            ShowMainUi(player, data, "rage");
        }

        [ConsoleCommand("omnirpg.rage.adjust")]
        private void CCmdRageAdjust(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_USE, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 2) return;

            var nodeId = args[0].ToLower();
            int delta;
            if (!int.TryParse(args[1], out delta) || delta == 0)
                return;

            if (delta < 0)
            {
                // No per-node refunds; use full respec instead
                player.ChatMessage(
                    "<color=#ffb74d>[OmniRPG]</color> You cannot reduce individual Rage nodes. " +
                    "Use <color=#e57373>/orpg rage respec</color> to fully reset the tree.");
                return;
            }

            // Only positive adjustments are allowed
            AllocateRagePoints(player, data, nodeId, delta);

            // Refresh Rage UI page
            ShowMainUi(player, data, "rage");
        }

        [ConsoleCommand("omnirpg.rage.respec")]
        private void CCmdRageRespec(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_USE, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            // Charge cost according to config (Economics / ServerRewards / Item / free)
            if (!TryChargeRespecCost(player))
                return;

            int spent = data.Rage.NodeLevels.Values.Sum();
            data.Rage.NodeLevels.Clear();
            data.Rage.UnspentPoints += spent;

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> Rage tree reset. Refunded <color=#e57373>{spent}</color> points.");

            SaveData();

            // Refresh Rage page so the player sees the reset
            ShowMainUi(player, data, "rage");
        }

        

        // Admin: adjust numeric config fields from Admin UI
        [ConsoleCommand("omnirpg.admin.adjust")]
        private void CCmdAdminAdjust(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 3) return;

            string category = args[0].ToLower();   // "xp" or "rage" or "respec"
            string field = args[1];                // e.g. "BaseKillNpc"
            string deltaStr = args[2];

            double delta;
            if (!double.TryParse(deltaStr, NumberStyles.Float, CultureInfo.InvariantCulture, out delta))
            {
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Invalid delta '{deltaStr}'.");
                return;
            }

            bool changed = false;

            switch (category)
            {
                case "xp":
                    changed = AdjustXpField(player, field, delta);
                    break;
                case "rage":
                    changed = AdjustRageField(player, field, delta);
                    break;
                case "respec":
                    changed = AdjustRespecField(player, field, delta);
                    break;
                default:
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Unknown category '{category}'.");
                    return;
            }

            if (changed)
            {
                SaveConfig();
                player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Config updated and saved.");
            }

            ShowMainUi(player, data, "admin");
        }

        // Admin: toggle boolean config values from Admin UI
        [ConsoleCommand("omnirpg.admin.toggle")]
        private void CCmdAdminToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 2) return;

            string category = args[0].ToLower();   // "rage"
            string field = args[1];

            bool changed = false;

            switch (category)
            {
                case "rage":
                    if (field.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Rage.Enabled = !config.Rage.Enabled;
                        player.ChatMessage(
                            $"<color=#ffb74d>[OmniRPG]</color> Rage Enabled: <color=#e57373>{config.Rage.Enabled}</color>");
                        changed = true;
                    }
                    else if (field.Equals("RespecEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Rage.Respec.Enabled = !config.Rage.Respec.Enabled;
                        player.ChatMessage(
                            $"<color=#ffb74d>[OmniRPG]</color> Rage Respec Enabled: <color=#e57373>{config.Rage.Respec.Enabled}</color>");
                        changed = true;
                    }
                    break;
            }

            if (changed)
            {
                SaveConfig();
                player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Config updated and saved.");
            }

            ShowMainUi(player, data, "admin");
        }

        // Admin: explicit Save button
        [ConsoleCommand("omnirpg.admin.save")]
        private void CCmdAdminSave(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            SaveConfig();
            player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Config saved to disk.");

            ShowMainUi(player, data, "admin");
        }

        // Admin: set string config values (e.g. Rage Respec Mode)
        [ConsoleCommand("omnirpg.admin.mode")]
        private void CCmdAdminMode(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 3) return;

            string category = args[0].ToLower();   // "rage"
            string field = args[1];
            string value = args[2];

            bool changed = false;

            if (category == "rage" && field.Equals("RespecMode", StringComparison.OrdinalIgnoreCase))
            {
                var r = config.Rage.Respec;
                string v = value.ToLowerInvariant();

                switch (v)
                {
                    case "none":
                    case "free":
                        r.Mode = "none";
                        r.Enabled = false;
                        break;

                    case "economics":
                        r.Mode = "economics";
                        r.Enabled = true;
                        break;

                    case "serverrewards":
                    case "rustrewards":
                    case "rp":
                        r.Mode = "serverrewards";
                        r.Enabled = true;
                        break;

                    case "item":
                        r.Mode = "item";
                        r.Enabled = true;
                        break;

                    default:
                        player.ChatMessage(
                            $"<color=#ffb74d>[OmniRPG]</color> Unknown respec mode '{value}'. Use economics / rp / item / free.");
                        return;
                }

                changed = true;
                player.ChatMessage(
                    $"<color=#ffb74d>[OmniRPG]</color> Rage Respec Mode: <color=#e57373>{r.Mode}</color> (Enabled: {r.Enabled})");
            }

            if (changed)
            {
                SaveConfig();
                player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Config updated and saved.");
            }

            ShowMainUi(player, data, "admin");
        }

        private bool AdjustXpField(BasePlayer player, string field, double delta)
        {
            switch (field)
            {
                case "BaseKillNpc":
                    config.XP.BaseKillNpc = Math.Max(0, config.XP.BaseKillNpc + delta);
                    break;
                case "BaseKillPlayer":
                    config.XP.BaseKillPlayer = Math.Max(0, config.XP.BaseKillPlayer + delta);
                    break;
                case "BaseGatherOre":
                    config.XP.BaseGatherOre = Math.Max(0, config.XP.BaseGatherOre + delta);
                    break;
                case "BaseGatherWood":
                    config.XP.BaseGatherWood = Math.Max(0, config.XP.BaseGatherWood + delta);
                    break;
                case "BaseGatherPlants":
                    config.XP.BaseGatherPlants = Math.Max(0, config.XP.BaseGatherPlants + delta);
                    break;
                case "BotReSpawnMultiplier":
                    config.XP.BotReSpawnMultiplier = Math.Max(0, config.XP.BotReSpawnMultiplier + delta);
                    break;
                case "LevelCurveBase":
                    config.XP.LevelCurveBase = Math.Max(1, config.XP.LevelCurveBase + delta);
                    break;
                case "LevelCurveGrowth":
                    config.XP.LevelCurveGrowth = Mathf.Clamp(
                        (float)(config.XP.LevelCurveGrowth + delta), 1.01f, 5f);
                    break;
                default:
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Unknown XP field '{field}'.");
                    return false;
            }

            return true;
        }

        private bool AdjustRageField(BasePlayer player, string field, double delta)
        {
            switch (field)
            {
                case "CorePointsPerLevel":
                    config.Rage.CorePointsPerLevel = Math.Max(0, config.Rage.CorePointsPerLevel + delta);
                    break;

                case "FuryDurationSeconds":
                    config.Rage.FuryDurationSeconds = Mathf.Clamp(
                        config.Rage.FuryDurationSeconds + (float)delta, 1f, 120f);
                    break;

                case "FuryMaxBonusDamage":
                    config.Rage.FuryMaxBonusDamage = Mathf.Clamp(
                        config.Rage.FuryMaxBonusDamage + (float)delta, 0f, 2f);
                    break;

                case "FuryOnKillGain":
                    config.Rage.FuryOnKillGain = Mathf.Clamp(
                        config.Rage.FuryOnKillGain + (float)delta, 0f, 1f);
                    break;

                // Respec costs (Rage.Respec.*)
                case "RespecEconomicsCost":
                    config.Rage.Respec.EconomicsCost = Math.Max(0, config.Rage.Respec.EconomicsCost + delta);
                    break;

                case "RespecServerRewardsCost":
                    config.Rage.Respec.ServerRewardsCost = Math.Max(0, config.Rage.Respec.ServerRewardsCost + (int)delta);
                    break;

                case "RespecItemAmount":
                    config.Rage.Respec.ItemAmount = Math.Max(0, config.Rage.Respec.ItemAmount + (int)delta);
                    break;

                default:
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Unknown Rage field '{field}'.");
                    return false;
            }

            return true;
        }

        private bool AdjustRespecField(BasePlayer player, string field, double delta)
        {
            var r = config.Rage.Respec;
            bool changed = false;

            switch (field)
            {
                case "EconomicsCost":
                    r.EconomicsCost = Math.Max(0, r.EconomicsCost + delta);
                    changed = true;
                    break;

                case "ServerRewardsCost":
                    r.ServerRewardsCost = Math.Max(0, r.ServerRewardsCost + (int)delta);
                    changed = true;
                    break;

                case "ItemAmount":
                    r.ItemAmount = Math.Max(0, r.ItemAmount + (int)delta);
                    changed = true;
                    break;
            }

            if (changed)
            {
                SaveConfig();
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Respec {field} adjusted by <color=#e57373>{delta}</color>. Now: Economics={r.EconomicsCost:0}, RP={r.ServerRewardsCost}, ItemAmount={r.ItemAmount}");
            }

            return changed;
        }


        // Adjust BotReSpawn profile multiplier
        [ConsoleCommand("omnirpg.botxp.mult")]
        private void CCmdBotXpMult(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 2) return;

            string profile = args[0];
            string deltaStr = args[1];

            double delta;
            if (!double.TryParse(deltaStr, NumberStyles.Float, CultureInfo.InvariantCulture, out delta))
            {
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Invalid delta '{deltaStr}'.");
                return;
            }

            var settings = EnsureBotProfileSettings(profile);
            settings.Multiplier = Math.Max(0, settings.Multiplier + delta);

            SaveConfig();

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> BotReSpawn profile <color=#e57373>{profile}</color> multiplier set to <color=#e57373>{settings.Multiplier:0.##}x</color>.");

            ShowMainUi(player, data, "botxp");
        }

        // Adjust BotReSpawn profile flat XP
        [ConsoleCommand("omnirpg.botxp.flat")]
        private void CCmdBotXpFlat(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            if (args == null || args.Length < 2) return;

            string profile = args[0];
            string deltaStr = args[1];

            double delta;
            if (!double.TryParse(deltaStr, NumberStyles.Float, CultureInfo.InvariantCulture, out delta))
            {
                player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Invalid delta '{deltaStr}'.");
                return;
            }

            var settings = EnsureBotProfileSettings(profile);
            settings.FlatXp = Math.Max(0, settings.FlatXp + delta);

            SaveConfig();

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> BotReSpawn profile <color=#e57373>{profile}</color> flat XP set to <color=#e57373>{settings.FlatXp:0}</color>.");

            ShowMainUi(player, data, "botxp");
        }

        // Bot XP: pagination
        [ConsoleCommand("omnirpg.botxp.page")]
        private void CCmdBotXpPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasPerm(player, PERM_ADMIN, false)) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var args = arg.Args;
            int delta = 0;
            if (args != null && args.Length > 0)
                int.TryParse(args[0], out delta);

            var profilesDict = config.XP.BotReSpawnProfiles ?? new Dictionary<string, BotProfileXpSettings>();
            int count = profilesDict.Count;
            const int pageSize = 10;
            int maxPage = count <= 0 ? 0 : (count - 1) / pageSize;

            int current = GetBotXpPage(player);
            int next = Mathf.Clamp(current + delta, 0, maxPage);
            SetBotXpPage(player, next);

            ShowMainUi(player, data, "botxp");
        }

        #endregion

        #region UI

        #region UI Main Shell & Navigation

        private const string UI_MAIN = "OmniRPG.UI.Main";
        private const string IMAGE_DISCIPLINES_BG = "omnirpg_disciplines_bg";

        [ConsoleCommand("omnirpg.ui")]
        private void CCmdOpenUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            var data = GetOrCreatePlayerData(player);
            if (data == null) return;
            if (!HasPerm(player, PERM_USE, false)) return;

            var args = arg.Args;
            var page = (args != null && args.Length > 0) ? args[0].ToLower() : "profile";
            if (page == "close")
            {
                DestroyProfileUi(player);
                return;
            }

            ShowMainUi(player, data, page);
        }

        private void CmdOpenUi(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PERM_USE)) return;
            var data = GetOrCreatePlayerData(player);
            if (data == null) return;

            var page = (args != null && args.Length > 0) ? args[0].ToLower() : "profile";
            if (page == "close")
            {
                DestroyProfileUi(player);
                return;
            }

            ShowMainUi(player, data, page);
        }

        private void CmdAdminUi(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PERM_ADMIN)) return;
            var data = GetOrCreatePlayerData(player);
            if (data == null) return;
            ShowMainUi(player, data, "admin");
        }

        private void ShowProfileUi(BasePlayer player, PlayerData data)
        {
            ShowMainUi(player, data, "profile");
        }

        private void DestroyProfileUi(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, UI_MAIN);
        }

        private void ShowMainUi(BasePlayer player, PlayerData data, string page)
        {
            DestroyProfileUi(player);

            var container = new CuiElementContainer();

            var panel = container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.05 0.05 0.05 0.97"
                },
                RectTransform =
                {
                    AnchorMin = "0.10 0.10",
                    AnchorMax = "0.90 0.90"
                },
                CursorEnabled = true
            }, "Overlay", UI_MAIN);

            // Left nav panel
            var navPanel = container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.98"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0.26 1"
                }
            }, panel, UI_MAIN + ".Nav");

            

            // Nav buttons
            float btnTop = 0.9f;
            float btnHeight = 0.07f;

            AddNavButton(container, navPanel, "Profile", "profile", page == "profile", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Disciplines", "disciplines",
                page == "disciplines" || page == "rage", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Leaderboard", "leaderboard", page == "leaderboard", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Settings", "settings", page == "settings", btnTop);
            btnTop -= btnHeight;

            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                AddNavButton(container, navPanel, "Admin", "adminmenu",
                    page == "adminmenu" || page == "admin" || page == "botxp", btnTop);
                btnTop -= btnHeight;
                AddNavButton(container, navPanel, "Save Plugin", "save", page == "save", btnTop);
                btnTop -= btnHeight;
            }

            AddNavButton(container, navPanel, "text", "text1", page == "text1", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "text", "text2", page == "text2", btnTop);
            btnTop -= btnHeight;

            AddNavButton(container, navPanel, "Close", "close", false, btnTop);

            // Content panel
            var contentPanel = container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.02 0.02 0.02 0.8"
                },
                RectTransform =
                {
                    AnchorMin = "0.26 0",
                    AnchorMax = "1 1"
                }
            }, panel, UI_MAIN + ".Content");

            switch (page)
            {
                case "profile":
                    BuildProfilePage(player, data, contentPanel, container);
                    break;

                case "disciplines":
                    BuildDisciplinesPage(player, data, contentPanel, container);
                    break;

                case "rage":
                    BuildRagePage(player, data, contentPanel, container);
                    break;

                case "leaderboard":
                    BuildLeaderboardPage(player, data, contentPanel, container);
                    break;

                case "botxp":
                    BuildBotXpPage(player, data, contentPanel, container);
                    break;

                case "stats": // legacy / placeholder
                    BuildStatsPage(player, data, contentPanel, container);
                    break;

                case "settings":
                    BuildSettingsPage(player, data, contentPanel, container);
                    break;

                case "adminmenu":
                    if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
                        BuildAdminMenuPage(player, data, contentPanel, container);
                    else
                        BuildAccessDeniedPage(player, contentPanel, container);
                    break;

                case "admin":
                    if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
                        BuildAdminPage(player, data, contentPanel, container);
                    else
                        BuildAccessDeniedPage(player, contentPanel, container);
                    break;

                case "save":
                    if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
                    {
                        SaveConfig();
                        player.ChatMessage("<color=#ffb74d>[OmniRPG]</color> Config saved.");
                    }
                    BuildProfilePage(player, data, contentPanel, container);
                    break;

                default:
                    BuildProfilePage(player, data, contentPanel, container);
                    break;
            }

            CuiHelper.AddUi(player, container);
        }

        private void ShowRageUpgradeFlash(BasePlayer player)
        {
            if (player == null) return;

            // Subtle orange flash over the main OmniRPG UI
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "1 0.45 0.15 0.20"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                },
                FadeOut = 0.15f
            }, UI_MAIN);

            CuiHelper.AddUi(player, container);
        }

        private void AddNavButton(CuiElementContainer container, string parent, string label, string pageKey, bool active, float top)
        {
            float bottom = top - 0.07f;
            if (bottom < 0.02f) bottom = 0.02f;

            container.Add(new CuiButton
            {
                Button =
                {
                    Color = active ? "0.3 0.5 0.3 0.95" : "0.15 0.15 0.15 0.95",
                    Command = $"omnirpg.ui {pageKey}"
                },
                Text =
                {
                    Text = label,
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = $"0.05 {bottom}",
                    AnchorMax = $"0.95 {top}"
                }
            }, parent);

            // (Removed shared 'Back to Disciplines' button)
        }

        #endregion

        #region UI Pages

        private void BuildDisciplinesPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            // Header
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Disciplines",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.6 0.97"
                }
            }, parent);

            var discPanel = parent + ".DisciplinesBackdrop";

            string bgPng = null;
            if (ImageLibrary != null)
            {
                var result = ImageLibrary.Call("GetImage", IMAGE_DISCIPLINES_BG);
                if (result is string s && !string.IsNullOrEmpty(s))
                    bgPng = s;
            }

            if (!string.IsNullOrEmpty(bgPng))
            {
                // Use the real background image
                container.Add(new CuiElement
                {
                    Name = discPanel,
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Png = bgPng,
                            Color = "1 1 1 1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.03 0.08",
                            AnchorMax = "0.97 0.86"
                        }
                    }
                });
            }
            else
            {
                // Fallback: parchment-style panel if image isn't ready
                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.82 0.74 0.60 0.95"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.08",
                        AnchorMax = "0.97 0.86"
                    }
                }, parent, discPanel);
            }

            // (Removed decorative top label to keep background art untouched)

            // Inner diagram area (where the nodes + lines live)
            var diagram = discPanel + ".Diagram";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0 0 0 0" // fully transparent
                },
                RectTransform =
                {
                    AnchorMin = "0.06 0.08",
                    AnchorMax = "0.94 0.80"
                }
            }, discPanel, diagram);

            // Normalized coordinates inside 'diagram' to approximate your image layout
            // Center X is 0.5; Y goes from bottom (0) to top (1)
            float fortitudeX = 0.50f, fortitudeY = 0.86f;
            float perceptionX = 0.22f, perceptionY = 0.66f;
            float willpowerX = 0.78f, willpowerY = 0.66f;
            float intelligenceX = 0.50f, intelligenceY = 0.54f;
            float rageX = 0.22f, rageY = 0.38f;
            float hardinessX = 0.78f, hardinessY = 0.38f;
            float dexterityX = 0.28f, dexterityY = 0.16f;
            float determinationX = 0.72f, determinationY = 0.16f;

            // (Removed line overlays so the artwork brushes remain untouched)

            float nodeSize = 0.16f;

            // Top: Fortitude
            AddDisciplineNode(container, diagram,
                "Fortitude", "fortitude",
                fortitudeX, fortitudeY, nodeSize,
                pageKey: null, enabled: false);

            // Mid left: Perception
            AddDisciplineNode(container, diagram,
                "Perception", "perception",
                perceptionX, perceptionY, nodeSize,
                pageKey: null, enabled: false);

            // Mid right: Willpower
            AddDisciplineNode(container, diagram,
                "Willpower", "willpower",
                willpowerX, willpowerY, nodeSize,
                pageKey: null, enabled: false);

            // Center: Intelligence
            AddDisciplineNode(container, diagram,
                "Intelligence", "intelligence",
                intelligenceX, intelligenceY, nodeSize,
                pageKey: null, enabled: false);

            // Lower left mid: Rage (ACTIVE -> opens Rage tree)
            AddDisciplineNode(container, diagram,
                "Rage", "rage",
                0.155f, 0.38f, nodeSize * 1.15f,
                pageKey: "rage", enabled: true);

            // Lower right mid: Hardiness
            AddDisciplineNode(container, diagram,
                "Hardiness", "hardiness",
                hardinessX, hardinessY, nodeSize,
                pageKey: null, enabled: false);

            // Bottom left: Dexterity
            AddDisciplineNode(container, diagram,
                "Dexterity", "dexterity",
                dexterityX, dexterityY, nodeSize,
                pageKey: null, enabled: false);

            // Bottom right: Determination
            AddDisciplineNode(container, diagram,
                "Determination", "determination",
                determinationX, determinationY, nodeSize,
                pageKey: null, enabled: false);
        }

        private void AddDisciplineLine(
            CuiElementContainer container,
            string parent,
            float x1, float y1,
            float x2, float y2)
        {
            // Simple straight connection bar between two points
            float minX = Mathf.Min(x1, x2);
            float maxX = Mathf.Max(x1, x2);
            float minY = Mathf.Min(y1, y2);
            float maxY = Mathf.Max(y1, y2);

            // Slight thickness
            float thickness = 0.01f;

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.20 0.12 0.07 1"
                },
                RectTransform =
                {
                    AnchorMin = $"{minX} {minY - thickness}",
                    AnchorMax = $"{maxX} {maxY + thickness}"
                }
            }, parent);
        }

        private void AddDisciplineNode(
            CuiElementContainer container,
            string parent,
            string label,
            string id,
            float centerX,
            float centerY,
            float size,
            string pageKey,
            bool enabled)
        {
            float half = size / 2f;

            string minX = (centerX - half).ToString(CultureInfo.InvariantCulture);
            string minY = (centerY - half).ToString(CultureInfo.InvariantCulture);
            string maxX = (centerX + half).ToString(CultureInfo.InvariantCulture);
            string maxY = (centerY + half).ToString(CultureInfo.InvariantCulture);

            // ONLY add a transparent clickable button if enabled
            if (!string.IsNullOrEmpty(pageKey) && enabled)
            {
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0 0 0 0",
                        Command = $"omnirpg.ui {pageKey}"
                    },
                    Text =
                    {
                        Text = "",
                        FontSize = 1,
                        Align = TextAnchor.MiddleCenter,
                        Color = "0 0 0 0"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"{minX} {minY}",
                        AnchorMax = $"{maxX} {maxY}"
                    }
                }, parent);
            }
        }

        // (Removed legacy AddDisciplineCard backup method)

        private void BuildProfilePage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"OmniRPG Profile - {data.LastKnownName}",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.6 0.97"
                }
            }, parent);

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.45",
                    AnchorMax = "0.5 0.84"
                }
            }, parent, parent + ".ProfileStats");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text =
                        $"Level: {data.Level}\n" +
                        $"XP: {data.CurrentXp:0}/{data.XpToNextLevel:0}\n" +
                        $"Total XP: {data.TotalXp:0}\n\n" +
                        $"Discipline Points: {data.UnspentDisciplinePoints}\n" +
                        $"Rage Points: {data.Rage.UnspentPoints}\n" +
                        $"Fury: {data.Rage.FuryAmount * 100f:0}%\n\n" +
                        $"Player Kills: {data.PlayerKills}\n" +
                        $"NPC Kills: {data.NpcKills}\n" +
                        $"Deaths: {data.Deaths}\n" +
                        $"Playtime: {FormatTime(data.TotalPlayTimeSeconds)}",
                    FontSize = 13,
                    Align = TextAnchor.UpperLeft,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.04 0.05",
                    AnchorMax = "0.96 0.95"
                }
            }, parent + ".ProfileStats");

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.52 0.45",
                    AnchorMax = "0.97 0.84"
                }
            }, parent, parent + ".ProfileRage");

            var rageLines = new List<string>();
            foreach (var kvp in config.Rage.Nodes)
            {
                var lvl = GetRageNodeLevel(data, kvp.Key);
                rageLines.Add($"{kvp.Value.DisplayName}: {lvl}/{kvp.Value.MaxLevel}");
            }

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Rage Tree\n" + string.Join("\n", rageLines),
                    FontSize = 13,
                    Align = TextAnchor.UpperLeft,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.04 0.05",
                    AnchorMax = "0.96 0.95"
                }
            }, parent + ".ProfileRage");

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.05",
                    AnchorMax = "0.97 0.4"
                }
            }, parent, parent + ".ProfileBuffs");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Active Buffs (Rage, future Disciplines, etc.)",
                    FontSize = 13,
                    Align = TextAnchor.UpperLeft,
                    Color = "0.9 0.9 0.9 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.04 0.7",
                    AnchorMax = "0.96 0.95"
                }
            }, parent + ".ProfileBuffs");
        }

        private void BuildLeaderboardPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Leaderboard",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.6 0.97"
                }
            }, parent);

            float headerYMin = 0.8f;
            float headerYMax = 0.85f;

            AddLeaderboardLabel(container, parent, "#", 0.03f, 0.10f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Name", 0.10f, 0.35f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Level", 0.35f, 0.45f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Total XP", 0.45f, 0.60f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Kills", 0.60f, 0.70f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Deaths", 0.70f, 0.80f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "K/D", 0.80f, 0.88f, headerYMin, headerYMax, true);
            AddLeaderboardLabel(container, parent, "Playtime", 0.88f, 0.98f, headerYMin, headerYMax, true);

            var top = players.Values
                .Where(p => IsHumanPlayerData(p))
                .OrderByDescending(p => p.TotalXp)
                .ThenByDescending(p => p.Level)
                .ThenBy(p => p.LastKnownName)
                .Take(15)
                .ToList();

            float rowHeight = 0.045f;
            for (int i = 0; i < top.Count; i++)
            {
                var p = top[i];
                float yMax = headerYMin - i * rowHeight;
                float yMin = yMax - rowHeight + 0.005f;

                if (yMin < 0.05f) break;

                double kd = p.Deaths > 0 ? (double)p.PlayerKills / p.Deaths : p.PlayerKills;

                AddLeaderboardLabel(container, parent, (i + 1).ToString(), 0.03f, 0.10f, yMin, yMax);
                AddLeaderboardLabel(container, parent, p.LastKnownName, 0.10f, 0.35f, yMin, yMax);
                AddLeaderboardLabel(container, parent, p.Level.ToString(), 0.35f, 0.45f, yMin, yMax);
                AddLeaderboardLabel(container, parent, $"{p.TotalXp:0}", 0.45f, 0.60f, yMin, yMax);
                AddLeaderboardLabel(container, parent, p.PlayerKills.ToString(), 0.60f, 0.70f, yMin, yMax);
                AddLeaderboardLabel(container, parent, p.Deaths.ToString(), 0.70f, 0.80f, yMin, yMax);
                AddLeaderboardLabel(container, parent, kd.ToString("0.00"), 0.80f, 0.88f, yMin, yMax);
                AddLeaderboardLabel(container, parent, FormatTime(p.TotalPlayTimeSeconds), 0.88f, 0.98f, yMin, yMax);
            }
        }

        private void AddLeaderboardLabel(CuiElementContainer container, string parent, string text, float xMin, float xMax, float yMin, float yMax, bool header = false)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = text,
                    FontSize = header ? 14 : 12,
                    Align = header ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft,
                    Color = header ? "1 0.9 0.6 1" : "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = $"{xMin} {yMin}",
                    AnchorMax = $"{xMax} {yMax}"
                }
            }, parent);
        }

        private void BuildRagePage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            // Header
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Rage Tree",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.6 0.97"
                }
            }, parent);

            // Summary strip at the top of the page
            var summaryPanel = parent + ".RageSummary";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.78",
                    AnchorMax = "0.97 0.86"
                }
            }, parent, summaryPanel);

            var summaryLines = new List<string>
            {
                $"Level: {data.Level}",
                $"Total XP: {data.TotalXp:0}",
                $"Rage Points: {data.Rage.UnspentPoints}",
                $"Fury: {data.Rage.FuryAmount * 100f:0}%"
            };

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = string.Join("   •   ", summaryLines),
                    FontSize = 13,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.04 0.08",
                    AnchorMax = "0.96 0.92"
                }
            }, summaryPanel);

            // Left: Rage tree canvas
            var treePanel = parent + ".RageTreeArea";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.05 0.05 0.05 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.10",
                    AnchorMax = "0.70 0.78"
                }
            }, parent, treePanel);

            // Tier selector strip
            var tierStrip = treePanel + ".TierStrip";
            container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.9" },
                RectTransform =
                {
                    AnchorMin = "0.03 0.82",
                    AnchorMax = "0.97 0.96"
                }
            }, treePanel, tierStrip);

            bool tier1Unlocked = true;
            bool tier2Unlocked = data.Rage.MaxUnlockedTier >= 2;
            bool tier3Unlocked = data.Rage.MaxUnlockedTier >= 3;

            AddTierTab(container, tierStrip, "Tier 1", "rage", 0.03f, 0.31f, tier1Unlocked);
            AddTierTab(container, tierStrip, "Tier 2", null,  0.35f, 0.63f, tier2Unlocked);
            AddTierTab(container, tierStrip, "Tier 3", null,  0.67f, 0.95f, tier3Unlocked);

            // Right: context + total buff summary
            var rightPanel = parent + ".RageRight";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.06 0.06 0.06 0.95"
                },
                RectTransform =
                {
                    AnchorMin = "0.71 0.10",
                    AnchorMax = "0.97 0.78"
                }
            }, parent, rightPanel);

            // Top half: selected node details
            var detailPanel = rightPanel + ".Details";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.10 0.10 0.10 0.95"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.50",
                    AnchorMax = "0.97 0.97"
                }
            }, rightPanel, detailPanel);

            // Bottom half: total combined Rage buffs
            var summaryBuffPanel = rightPanel + ".BuffSummary";
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.09 0.09 0.09 0.95"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.03",
                    AnchorMax = "0.97 0.48"
                }
            }, rightPanel, summaryBuffPanel);

            // Compute flash info for last-upgraded node
            string flashNode = data.Rage.LastUpgradedNodeId;
            bool flashActive = false;
            float flashWindow = 0.4f;
            if (!string.IsNullOrEmpty(flashNode) && data.Rage.LastUpgradeFlashTime > 0)
            {
                var elapsed = Time.realtimeSinceStartup - (float)data.Rage.LastUpgradeFlashTime;
                flashActive = elapsed >= 0 && elapsed <= flashWindow;
            }

            // Predefined positions for the Rage nodes (normalized coordinates in treePanel)
            var nodePositions = new Dictionary<string, Vector2>
            {
                { "core",    new Vector2(0.50f, 0.70f) },
                { "rifle",   new Vector2(0.25f, 0.30f) },
                { "shotgun", new Vector2(0.50f, 0.30f) },
                { "pistol",  new Vector2(0.75f, 0.30f) }
            };

            // Draw simple connection lines between core and each weapon node
            foreach (var kvp in nodePositions)
            {
                var nodeId = kvp.Key;
                if (nodeId == "core")
                    continue;

                var from = nodePositions["core"];
                var to = kvp.Value;

                float minX = Math.Min(from.x, to.x);
                float maxX = Math.Max(from.x, to.x);
                float minY = Math.Min(from.y, to.y);
                float maxY = Math.Max(from.y, to.y);

                float padding = 0.03f;
                minY += padding;
                maxY -= padding;

                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.4 0.35 0.2 0.7"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"{minX.ToString(CultureInfo.InvariantCulture)} {minY.ToString(CultureInfo.InvariantCulture)}",
                        AnchorMax = $"{maxX.ToString(CultureInfo.InvariantCulture)} {maxY.ToString(CultureInfo.InvariantCulture)}"
                    }
                }, treePanel);
            }

            // Draw nodes (more circular, no inline description)
            float nodeSize = 0.16f;
            foreach (var kvp in nodePositions)
            {
                var nodeId = kvp.Key;
                RageNodeConfig cfg;
                if (!config.Rage.Nodes.TryGetValue(nodeId, out cfg))
                    continue;

                Vector2 pos = kvp.Value;
                bool flashThis = flashActive && string.Equals(flashNode, nodeId, StringComparison.OrdinalIgnoreCase);
                AddRageNodeCircle(player, data, treePanel, container, nodeId, cfg, pos.x, pos.y, nodeSize, flashThis);
            }

            // Selected-node detail (top-right)
            string selectedId = data.Rage.SelectedNodeId;
            RageNodeConfig selectedCfg = null;
            int selectedLevel = 0;
            if (!string.IsNullOrEmpty(selectedId))
            {
                config.Rage.Nodes.TryGetValue(selectedId, out selectedCfg);
                selectedLevel = GetRageNodeLevel(data, selectedId);
            }

            string detailTitle = selectedCfg != null ? selectedCfg.DisplayName : "Select a Rage skill";
            string detailBody;
            if (selectedCfg == null)
            {
                detailBody = "Click the ? icon on any Rage node to view detailed info here.";
            }
            else
            {
                var lines = new List<string>
                {
                    $"Level: {selectedLevel}/{selectedCfg.MaxLevel}"
                };

                float totalDmg = selectedLevel * selectedCfg.DamageBonusPerLevel * 100f;
                float totalCrit = selectedLevel * selectedCfg.CritChancePerLevel * 100f;
                float totalCritDmg = selectedLevel * selectedCfg.CritDamagePerLevel * 100f;
                float totalBleed = selectedLevel * selectedCfg.BleedChancePerLevel * 100f;
                float totalMove = selectedLevel * selectedCfg.MoveSpeedPerLevel * 100f;
                float totalRecoil = selectedLevel * selectedCfg.RecoilReductionPerLevel * 100f;

                if (selectedCfg.DamageBonusPerLevel != 0)
                    lines.Add($"Damage: +{totalDmg:0.#}% total ({selectedCfg.DamageBonusPerLevel * 100f:0.#}%/level)");
                if (selectedCfg.CritChancePerLevel != 0)
                    lines.Add($"Crit Chance: +{totalCrit:0.#}% total ({selectedCfg.CritChancePerLevel * 100f:0.#}%/level)");
                if (selectedCfg.CritDamagePerLevel != 0)
                    lines.Add($"Crit Damage: +{totalCritDmg:0.#}% total ({selectedCfg.CritDamagePerLevel * 100f:0.#}%/level)");
                if (selectedCfg.BleedChancePerLevel != 0)
                    lines.Add($"Bleed Chance: +{totalBleed:0.#}% total ({selectedCfg.BleedChancePerLevel * 100f:0.#}%/level)");
                if (selectedCfg.MoveSpeedPerLevel != 0)
                    lines.Add($"Move Speed: +{totalMove:0.#}% total ({selectedCfg.MoveSpeedPerLevel * 100f:0.#}%/level)");
                if (selectedCfg.RecoilReductionPerLevel != 0)
                    lines.Add($"Recoil: -{totalRecoil:0.#}% total ({selectedCfg.RecoilReductionPerLevel * 100f:0.#}%/level)");

                if (lines.Count == 1)
                    lines.Add("No numeric bonuses configured for this node yet.");

                detailBody = string.Join("\n", lines);
            }

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = detailTitle,
                    FontSize = 15,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.7 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.70",
                    AnchorMax = "0.95 0.96"
                }
            }, detailPanel);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = detailBody,
                    FontSize = 12,
                    Align = TextAnchor.UpperLeft,
                    Color = "0.9 0.9 0.9 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.10",
                    AnchorMax = "0.95 0.80"
                }
            }, detailPanel);

            // Total combined buff summary (bottom-right)
            float sumDmg = 0f, sumCrit = 0f, sumCritDmg = 0f, sumBleed = 0f, sumMove = 0f, sumRecoil = 0f;
            foreach (var kvp in config.Rage.Nodes)
            {
                var nid = kvp.Key;
                var cfg = kvp.Value;
                int lvl = GetRageNodeLevel(data, nid);
                if (lvl <= 0) continue;

                sumDmg += lvl * cfg.DamageBonusPerLevel * 100f;
                sumCrit += lvl * cfg.CritChancePerLevel * 100f;
                sumCritDmg += lvl * cfg.CritDamagePerLevel * 100f;
                sumBleed += lvl * cfg.BleedChancePerLevel * 100f;
                sumMove += lvl * cfg.MoveSpeedPerLevel * 100f;
                sumRecoil += lvl * cfg.RecoilReductionPerLevel * 100f;
            }

            var buffLines = new List<string>();
            if (sumDmg != 0) buffLines.Add($"Damage: +{sumDmg:0.#}%");
            if (sumCrit != 0) buffLines.Add($"Crit Chance: +{sumCrit:0.#}%");
            if (sumCritDmg != 0) buffLines.Add($"Crit Damage: +{sumCritDmg:0.#}%");
            if (sumBleed != 0) buffLines.Add($"Bleed Chance: +{sumBleed:0.#}%");
            if (sumMove != 0) buffLines.Add($"Move Speed: +{sumMove:0.#}%");
            if (sumRecoil != 0) buffLines.Add($"Recoil: -{sumRecoil:0.#}%");
            if (buffLines.Count == 0) buffLines.Add("No Rage bonuses allocated yet.");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Total Rage Bonuses",
                    FontSize = 14,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.7 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.74",
                    AnchorMax = "0.95 0.96"
                }
            }, summaryBuffPanel);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = string.Join("\n", buffLines),
                    FontSize = 12,
                    Align = TextAnchor.UpperLeft,
                    Color = "0.9 0.9 0.9 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.10",
                    AnchorMax = "0.95 0.80"
                }
            }, summaryBuffPanel);

            // Quick help text at bottom of main page
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Click nodes to spend Rage points. Click the ? icon on a node to view detailed info on the right.",
                    FontSize = 12,
                    Align = TextAnchor.UpperLeft,
                    Color = "0.85 0.85 0.85 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.02",
                    AnchorMax = "0.80 0.09"
                }
            }, parent);
            // --- Rage Reset (Respec) button ---
            {
                // Optional: show current cost text based on config
                string costText;
                var r = config.Rage.Respec;
                var mode = (r.Mode ?? string.Empty).ToLowerInvariant();

                if (!r.Enabled || string.IsNullOrEmpty(mode) || mode == "none")
                {
                    costText = "Reset Tree (Free)";
                }
                else if (mode == "economics")
                {
                    costText = $"Reset Tree ({r.EconomicsCost:0} coins)";
                }
                else if (mode == "serverrewards" || mode == "rustrewards" || mode == "rp")
                {
                    costText = $"Reset Tree ({r.ServerRewardsCost} RP)";
                }
                else if (mode == "item")
                {
                    costText = $"Reset Tree ({r.ItemAmount} x {r.ItemShortname})";
                }
                else
                {
                    costText = "Reset Tree";
                }

                // Bottom-center panel
                var resetPanel = container.Add(new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.85" },
                    RectTransform =
                    {
                        AnchorMin = "0.35 0.02",
                        AnchorMax = "0.65 0.08"
                    },
                    CursorEnabled = true
                }, parent, "omnirpg.rage.reset.panel");

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Command = "omnirpg.rage.respec",
                        Color = "0.8 0.3 0.3 1.0"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.02 0.1",
                        AnchorMax = "0.98 0.9"
                    },
                    Text =
                    {
                        Text = costText,
                        FontSize = 14,
                        Align = TextAnchor.MiddleCenter
                    }
                }, resetPanel);
            }
            // Admin-only: full Rage respec button
            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.4 0.2 0.2 0.95",
                        Command = "omnirpg.rage.respec"
                    },
                    Text =
                    {
                        Text = "Reset Rage Tree",
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.82 0.02",
                        AnchorMax = "0.97 0.09"
                    }
                }, parent);
            }
        }

        private void AddRageNodeCircle(
            BasePlayer player,
            PlayerData data,
            string parent,
            CuiElementContainer container,
            string nodeId,
            RageNodeConfig cfg,
            float centerX,
            float centerY,
            float size,
            bool flashHighlight)
        {
            int level = GetRageNodeLevel(data, nodeId);
            bool canIncrease = data.Rage.UnspentPoints > 0 && level < cfg.MaxLevel;

            string nodePanel = parent + ".RageNode." + nodeId;
            string bgColor = flashHighlight ? "0.45 0.32 0.12 0.95" : "0.16 0.16 0.16 0.95";
            string ringColor = canIncrease ? "0.95 0.78 0.36 1" : "0.40 0.40 0.40 1";

            float half = size / 2f;
            string minX = (centerX - half).ToString(CultureInfo.InvariantCulture);
            string minY = (centerY - half).ToString(CultureInfo.InvariantCulture);
            string maxX = (centerX + half).ToString(CultureInfo.InvariantCulture);
            string maxY = (centerY + half).ToString(CultureInfo.InvariantCulture);

            // Base "circle"
            container.Add(new CuiPanel
            {
                Image = { Color = bgColor },
                RectTransform =
                {
                    AnchorMin = $"{minX} {minY}",
                    AnchorMax = $"{maxX} {maxY}"
                }
            }, parent, nodePanel);

            // Outer ring
            container.Add(new CuiPanel
            {
                Image = { Color = ringColor },
                RectTransform =
                {
                    AnchorMin = "0.06 0.06",
                    AnchorMax = "0.94 0.94"
                }
            }, nodePanel, nodePanel + ".Ring");

            // Icon / title area (top ~40%)
            container.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.05 0.95" },
                RectTransform =
                {
                    AnchorMin = "0.14 0.52",
                    AnchorMax = "0.86 0.90"
                }
            }, nodePanel, nodePanel + ".IconBg");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = cfg.DisplayName,
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 0.9 0.7 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.16 0.54",
                    AnchorMax = "0.84 0.88"
                }
            }, nodePanel);

            // "?" inspect button in upper-right
            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.25 0.35 0.45 0.95",
                    Command = $"omnirpg.rage.inspect {nodeId}"
                },
                Text =
                {
                    Text = "?",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.76 0.74",
                    AnchorMax = "0.92 0.92"
                }
            }, nodePanel);

            // Level label
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"Lv {level}/{cfg.MaxLevel}",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.95 0.95 0.95 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.14 0.40",
                    AnchorMax = "0.86 0.52"
                }
            }, nodePanel);

            // Compact centered progress bar
            float progress = cfg.MaxLevel > 0 ? Mathf.Clamp01(level / (float)cfg.MaxLevel) : 0f;

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 1" },
                RectTransform =
                {
                    AnchorMin = "0.20 0.32",
                    AnchorMax = "0.80 0.38"
                }
            }, nodePanel, nodePanel + ".ProgressBg");

            container.Add(new CuiPanel
            {
                Image = { Color = canIncrease ? "0.9 0.76 0.35 1" : "0.55 0.55 0.55 1" },
                RectTransform =
                {
                    AnchorMin = "0.20 0.32",
                    AnchorMax = $"{(0.20f + 0.60f * progress).ToString(CultureInfo.InvariantCulture)} 0.38"
                }
            }, nodePanel, nodePanel + ".ProgressFill");

            // Up (▲▲▲) button under the bar
            string upCmd = canIncrease ? $"omnirpg.rage.adjust {nodeId} 1" : "";
            string upColor = canIncrease ? "0.3 0.5 0.3 0.95" : "0.2 0.2 0.2 0.7";

            container.Add(new CuiButton
            {
                Button =
                {
                    Color = upColor,
                    Command = upCmd
                },
                Text =
                {
                    Text = "▲▲▲",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.85 0.02",
                    AnchorMax = "0.95 0.08"
                }
            }, nodePanel);
        }


        private void AddTierTab(
            CuiElementContainer container,
            string parent,
            string label,
            string pageKey,
            float xMin,
            float xMax,
            bool enabled)
        {
            string color = enabled ? "0.25 0.4 0.25 0.95" : "0.15 0.15 0.15 0.95";
            string textColor = enabled ? "1 1 1 1" : "0.7 0.7 0.7 1";

            container.Add(new CuiButton
            {
                Button =
                {
                    Color = color,
                    Command = enabled && !string.IsNullOrEmpty(pageKey)
                        ? $"omnirpg.ui {pageKey}"
                        : ""
                },
                Text =
                {
                    Text = enabled ? label : label + " (Locked)",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = textColor
                },
                RectTransform =
                {
                    AnchorMin = $"{xMin} 0.10",
                    AnchorMax = $"{xMax} 0.90"
                }
            }, parent);
        }

// Bot XP page (per-profile multiplier + flat XP)
        private void BuildBotXpPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "BotReSpawn Profile XP",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.7 0.97"
                }
            }, parent);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Adjust per-profile XP using Multiplier and Flat XP. Profiles are discovered dynamically as BotReSpawn NPCs spawn or die.",
                    FontSize = 12,
                    Align = TextAnchor.MiddleLeft,
                    Color = "0.9 0.9 0.9 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.78",
                    AnchorMax = "0.97 0.85"
                }
            }, parent);

            var dict = config.XP.BotReSpawnProfiles ?? new Dictionary<string, BotProfileXpSettings>();
            if (dict.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = "No BotReSpawn profiles detected yet.\nOnce BotReSpawn NPCs have spawned or died, profiles will appear here.",
                        FontSize = 14,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.2 0.4",
                        AnchorMax = "0.8 0.6"
                    }
                }, parent);
                return;
            }

            var profiles = dict.Keys.OrderBy(k => k).ToList();
            const int pageSize = 9;
            int pageIndex = GetBotXpPage(player);
            int maxPage = (profiles.Count - 1) / pageSize;
            if (pageIndex < 0) pageIndex = 0;
            if (pageIndex > maxPage) pageIndex = maxPage;
            SetBotXpPage(player, pageIndex);

            float listTop = 0.74f;
            float rowHeight = 0.07f;

            int startIndex = pageIndex * pageSize;
            int endIndex = Math.Min(startIndex + pageSize, profiles.Count);

            for (int i = startIndex; i < endIndex; i++)
            {
                string profile = profiles[i];
                var settings = dict[profile];
                double mult = settings.Multiplier;
                double flat = settings.FlatXp;

                float yMax = listTop - (i - startIndex) * rowHeight;
                float yMin = yMax - rowHeight + 0.005f;

                var rowName = parent + ".BotXpRow." + profile;

                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.07 0.07 0.07 0.9"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"0.03 {yMin}",
                        AnchorMax = $"0.97 {yMax}"
                    }
                }, parent, rowName);

                // Profile name
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = profile,
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.5",
                        AnchorMax = "0.35 0.95"
                    }
                }, rowName);

                // Flat XP label
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"Flat XP: {flat:0}",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.9 0.9 0.9 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.05",
                        AnchorMax = "0.35 0.45"
                    }
                }, rowName);

                // Multiplier label
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"Mult: {mult:0.##}x",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.9 0.9 0.9 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.36 0.5",
                        AnchorMax = "0.60 0.95"
                    }
                }, rowName);

                // Button steps
                double flatSmall = 10;
                double flatBig = 50;
                double multSmall = 0.1;
                double multBig = 0.5;

                // Flat XP buttons
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.4 0.2 0.2 0.95",
                        Command = $"omnirpg.botxp.flat {profile} {(-flatBig).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{flatBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.36 0.05",
                        AnchorMax = "0.44 0.45"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.35 0.25 0.25 0.95",
                        Command = $"omnirpg.botxp.flat {profile} {(-flatSmall).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{flatSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.45 0.05",
                        AnchorMax = "0.53 0.45"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.35 0.25 0.95",
                        Command = $"omnirpg.botxp.flat {profile} {flatSmall.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{flatSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.54 0.05",
                        AnchorMax = "0.62 0.45"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.4 0.25 0.95",
                        Command = $"omnirpg.botxp.flat {profile} {flatBig.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{flatBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.63 0.05",
                        AnchorMax = "0.71 0.45"
                    }
                }, rowName);

                // Multiplier buttons
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.4 0.2 0.2 0.95",
                        Command = $"omnirpg.botxp.mult {profile} {(-multBig).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{multBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.72 0.50",
                        AnchorMax = "0.80 0.95"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.35 0.25 0.25 0.95",
                        Command = $"omnirpg.botxp.mult {profile} {(-multSmall).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{multSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.81 0.50",
                        AnchorMax = "0.89 0.95"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.35 0.25 0.95",
                        Command = $"omnirpg.botxp.mult {profile} {multSmall.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{multSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.72 0.05",
                        AnchorMax = "0.80 0.45"
                    }
                }, rowName);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.4 0.25 0.95",
                        Command = $"omnirpg.botxp.mult {profile} {multBig.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{multBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.81 0.05",
                        AnchorMax = "0.89 0.45"
                    }
                }, rowName);
            }

            // Pagination controls
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"Page {pageIndex + 1}/{maxPage + 1}",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.40 0.02",
                    AnchorMax = "0.60 0.08"
                }
            }, parent);

            // Prev on the bottom-left
            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.3 0.3 0.3 0.95",
                    Command = "omnirpg.botxp.page -1"
                },
                Text =
                {
                    Text = "Prev",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.10 0.02",
                    AnchorMax = "0.22 0.08"
                }
            }, parent);

            // Next on the bottom-right
            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.3 0.3 0.3 0.95",
                    Command = "omnirpg.botxp.page 1"
                },
                Text =
                {
                    Text = "Next",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.78 0.02",
                    AnchorMax = "0.90 0.08"
                }
            }, parent);
        }

        private void BuildStatsPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Stats / Skill Tree (placeholder)",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.2 0.45",
                    AnchorMax = "0.8 0.55"
                }
            }, parent);
        }

        private void BuildSettingsPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Player Settings (coming soon)",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.2 0.45",
                    AnchorMax = "0.8 0.55"
                }
            }, parent);
        }

        private void BuildAdminMenuPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            // Header
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Admin Config Editor",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.9 0.97"
                }
            }, parent);

            // Subtitle
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Choose a configuration section:",
                    FontSize = 13,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.78",
                    AnchorMax = "0.9 0.86"
                }
            }, parent);

            // Helper for buttons
            void AddAdminMenuButton(string label, string command, float minX, float minY, float maxX, float maxY)
            {
                var name = parent + ".AdminMenu." + label.Replace(" ", "");
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.15 0.15 0.15 0.95",
                        Command = command
                    },
                    Text =
                    {
                        Text = label,
                        FontSize = 14,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 0.9 0.6 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"{minX} {minY}",
                        AnchorMax = $"{maxX} {maxY}"
                    }
                }, parent, name);
            }

            // Row 1: XP & Rage / Bot XP
            AddAdminMenuButton(
                "XP & Rage Settings",
                "omnirpg.ui admin",
                0.05f, 0.52f,
                0.48f, 0.72f);

            AddAdminMenuButton(
                "BotReSpawn XP Settings",
                "omnirpg.ui botxp",
                0.52f, 0.52f,
                0.95f, 0.72f);

            // Row 2: Save + placeholder for future stuff
            AddAdminMenuButton(
                "Save Config",
                "omnirpg.admin.save",
                0.05f, 0.28f,
                0.48f, 0.48f);

            AddAdminMenuButton(
                "Future Section",
                "echo",
                0.52f, 0.28f,
                0.95f, 0.48f);
        }

        private void BuildAdminPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            // Header
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Admin Config Editor",
                    FontSize = 18,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.86",
                    AnchorMax = "0.6 0.97"
                }
            }, parent);

            // XP Settings panel (left)
            var xpPanel = container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.15",
                    AnchorMax = "0.48 0.84"
                }
            }, parent, parent + ".AdminXP");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "XP Settings",
                    FontSize = 15,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.88",
                    AnchorMax = "0.6 0.98"
                }
            }, xpPanel);

            float rowTop = 0.82f;
            float rowHeight = 0.10f;
            float rageRowHeight = 0.06f;

            void AddXpRow(string label, string field, double value, double stepSmall, double stepBig)
            {
                float yMax = rowTop;
                float yMin = yMax - rowHeight;
                rowTop -= rowHeight;

                var rowName = xpPanel + ".Row." + field;

                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.06 0.06 0.06 0.9"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"0.03 {yMin}",
                        AnchorMax = $"0.97 {yMax}"
                    }
                }, xpPanel, rowName);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"{label}: {value:0.###}",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.1",
                        AnchorMax = "0.7 0.9"
                    }
                }, rowName);

                // -big
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.4 0.2 0.2 0.95",
                        Command = $"omnirpg.admin.adjust xp {field} {(-stepBig).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{stepBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.72 0.15",
                        AnchorMax = "0.80 0.85"
                    }
                }, rowName);

                // -small
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.35 0.25 0.25 0.95",
                        Command = $"omnirpg.admin.adjust xp {field} {(-stepSmall).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{stepSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.81 0.15",
                        AnchorMax = "0.87 0.85"
                    }
                }, rowName);

                // +small
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.35 0.25 0.95",
                        Command = $"omnirpg.admin.adjust xp {field} {stepSmall.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{stepSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.88 0.15",
                        AnchorMax = "0.94 0.85"
                    }
                }, rowName);

                // +big
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.4 0.25 0.95",
                        Command = $"omnirpg.admin.adjust xp {field} {stepBig.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{stepBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.95 0.15",
                        AnchorMax = "1.01 0.85"
                    }
                }, rowName);
            }

            AddXpRow("NPC Kill XP", "BaseKillNpc", config.XP.BaseKillNpc, 5, 25);
            AddXpRow("Player Kill XP", "BaseKillPlayer", config.XP.BaseKillPlayer, 10, 50);
            AddXpRow("Gather Ore XP/Unit", "BaseGatherOre", config.XP.BaseGatherOre, 0.5, 2);
            AddXpRow("Gather Wood XP/Unit", "BaseGatherWood", config.XP.BaseGatherWood, 0.5, 2);
            AddXpRow("Gather Plants XP/Unit", "BaseGatherPlants", config.XP.BaseGatherPlants, 0.5, 2);
            AddXpRow("BotReSpawn XP Mult", "BotReSpawnMultiplier", config.XP.BotReSpawnMultiplier, 0.1, 0.5);
            AddXpRow("LevelCurve Base", "LevelCurveBase", config.XP.LevelCurveBase, 10, 50);
            AddXpRow("LevelCurve Growth", "LevelCurveGrowth", config.XP.LevelCurveGrowth, 0.05, 0.2);

            // Rage Settings panel (right)
            var ragePanel = container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.08 0.08 0.08 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.52 0.15",
                    AnchorMax = "0.97 0.84"
                }
            }, parent, parent + ".AdminRage");

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Rage Settings",
                    FontSize = 15,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.9 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.88",
                    AnchorMax = "0.6 0.98"
                }
            }, ragePanel);

            // Enabled toggle row
            var rowEnabled = ragePanel + ".Row.Enabled";

            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.06 0.06 0.06 0.9"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.72",
                    AnchorMax = "0.97 0.82"
                }
            }, ragePanel, rowEnabled);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"Rage Enabled: {config.Rage.Enabled}",
                    FontSize = 12,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.1",
                    AnchorMax = "0.7 0.9"
                }
            }, rowEnabled);

            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.3 0.3 0.5 0.95",
                    Command = "omnirpg.admin.toggle rage Enabled"
                },
                Text =
                {
                    Text = "Toggle",
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.75 0.15",
                    AnchorMax = "0.95 0.85"
                }
            }, rowEnabled);

            float rageRowTop = 0.72f;

            void AddRageRow(string label, string field, double value, double stepSmall, double stepBig)
            {
                float yMax = rageRowTop;
                float yMin = yMax - rageRowHeight;
                rageRowTop -= rageRowHeight;

                var rowName = ragePanel + ".Row." + field;

                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.06 0.06 0.06 0.9"
                    },
                    RectTransform =
                    {
                        AnchorMin = $"0.03 {yMin}",
                        AnchorMax = $"0.97 {yMax}"
                    }
                }, ragePanel, rowName);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"{label}: {value:0.###}",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.1",
                        AnchorMax = "0.7 0.9"
                    }
                }, rowName);

                // -big
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.4 0.2 0.2 0.95",
                        Command = $"omnirpg.admin.adjust rage {field} {(-stepBig).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{stepBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.72 0.15",
                        AnchorMax = "0.80 0.85"
                    }
                }, rowName);

                // -small
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.35 0.25 0.25 0.95",
                        Command = $"omnirpg.admin.adjust rage {field} {(-stepSmall).ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"-{stepSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.81 0.15",
                        AnchorMax = "0.87 0.85"
                    }
                }, rowName);

                // +small
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.35 0.25 0.95",
                        Command = $"omnirpg.admin.adjust rage {field} {stepSmall.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{stepSmall}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.88 0.15",
                        AnchorMax = "0.94 0.85"
                    }
                }, rowName);

                // +big
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.4 0.25 0.95",
                        Command = $"omnirpg.admin.adjust rage {field} {stepBig.ToString(CultureInfo.InvariantCulture)}"
                    },
                    Text =
                    {
                        Text = $"+{stepBig}",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.95 0.15",
                        AnchorMax = "1.01 0.85"
                    }
                }, rowName);
            }

            AddRageRow("Core Points per Level", "CorePointsPerLevel", config.Rage.CorePointsPerLevel, 0.1, 1);
            AddRageRow("Fury Duration (sec)", "FuryDurationSeconds", config.Rage.FuryDurationSeconds, 1, 5);
            AddRageRow("Fury Max Damage Bonus", "FuryMaxBonusDamage", config.Rage.FuryMaxBonusDamage, 0.05, 0.2);
            AddRageRow("Fury Gain per Kill", "FuryOnKillGain", config.Rage.FuryOnKillGain, 0.01, 0.05);

            // Respec cost rows
            // --- Rage Respec Settings (UI-configurable) ---
            {
                var r = config.Rage.Respec;

                // Summary row for respec status
                var respecRow = ragePanel + ".Row.RespecSummary";
                container.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = "0.06 0.06 0.06 0.9"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.20",
                        AnchorMax = "0.97 0.30"
                    }
                }, ragePanel, respecRow);

                string modeLabel = string.IsNullOrEmpty(r.Mode) ? "none" : r.Mode;

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"Respec Enabled: {r.Enabled} | Mode: {modeLabel}",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.03 0.15",
                        AnchorMax = "0.70 0.85"
                    }
                }, respecRow);

                // Toggle Enabled
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.3 0.3 0.5 0.95",
                        Command = "omnirpg.admin.toggle rage RespecEnabled"
                    },
                    Text =
                    {
                        Text = "Toggle",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.72 0.15",
                        AnchorMax = "0.82 0.85"
                    }
                }, respecRow);

                // Mode buttons: Free / Econ / RP / Item (below Rage panel)
                // This row sits between the Rage panel (bottom at Y=0.15) and the Save button (0.03–0.11)
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.25 0.25 0.95",
                        Command = "omnirpg.admin.mode rage RespecMode free"
                    },
                    Text =
                    {
                        Text = "Free",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.52 0.12",
                        AnchorMax = "0.60 0.15"
                    }
                }, parent);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.25 0.35 0.45 0.95",
                        Command = "omnirpg.admin.mode rage RespecMode economics"
                    },
                    Text =
                    {
                        Text = "Economics",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.61 0.12",
                        AnchorMax = "0.73 0.15"
                    }
                }, parent);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.30 0.35 0.30 0.95",
                        Command = "omnirpg.admin.mode rage RespecMode rp"
                    },
                    Text =
                    {
                        Text = "RP (ServerRewards)",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.74 0.12",
                        AnchorMax = "0.90 0.15"
                    }
                }, parent);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0.35 0.30 0.25 0.95",
                        Command = "omnirpg.admin.mode rage RespecMode item"
                    },
                    Text =
                    {
                        Text = "Item",
                        FontSize = 11,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.91 0.12",
                        AnchorMax = "0.98 0.15"
                    }
                }, parent);
            }

            // Fine-tune Respec cost values using existing Rage row layout
            AddRageRow("Respec Econ Cost", "RespecEconomicsCost", config.Rage.Respec.EconomicsCost, 100, 1000);
            AddRageRow("Respec RP Cost", "RespecServerRewardsCost", config.Rage.Respec.ServerRewardsCost, 5, 25);
            AddRageRow("Respec Item Amount", "RespecItemAmount", config.Rage.Respec.ItemAmount, 1, 10);

            // (BotReSpawn XP Settings button removed)

            // Bottom Save button
            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.3 0.5 0.3 0.95",
                    Command = "omnirpg.admin.save"
                },
                Text =
                {
                    Text = "Save Config",
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.40 0.03",
                    AnchorMax = "0.60 0.11"
                }
            }, parent);
        }

        private void BuildAccessDeniedPage(BasePlayer player, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "You do not have permission to view this page.",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 0.6 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.2 0.45",
                    AnchorMax = "0.8 0.55"
                }
            }, parent);
        }

        #endregion

        #endregion
    }
}
