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

            // Visual feedback for the last-upgraded Rage node in the tree UI
            public string LastUpgradedNodeId;
            public double LastUpgradeFlashTime;

            public RageData()
            {
                FuryAmount = 0f;
                FuryExpireTimestamp = 0;
                LastUpgradedNodeId = null;
                LastUpgradeFlashTime = 0;
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

            // Admin config editor commands
            cmd.AddConsoleCommand("omnirpg.admin.adjust", this, "CCmdAdminAdjust");
            cmd.AddConsoleCommand("omnirpg.admin.toggle", this, "CCmdAdminToggle");
            cmd.AddConsoleCommand("omnirpg.admin.save", this, "CCmdAdminSave");

            // Bot XP editor commands
            cmd.AddConsoleCommand("omnirpg.botxp.mult", this, "CCmdBotXpMult");
            cmd.AddConsoleCommand("omnirpg.botxp.flat", this, "CCmdBotXpFlat");
            cmd.AddConsoleCommand("omnirpg.botxp.page", this, "CCmdBotXpPage");
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
                if (victimPlayer.IsNpc)
                {
                    // Vanilla NPCs implemented as NPCPlayers (e.g. scientists, tunnel dwellers, etc.)
                    xp = config.XP.BaseKillNpc;
                    killerData.NpcKills++;
                }
                else if (victimPlayer != killer)
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

                int spent = data.Rage.NodeLevels.Values.Sum();
                data.Rage.NodeLevels.Clear();
                data.Rage.UnspentPoints += spent;

                player.ChatMessage(
                    $"<color=#ffb74d>[OmniRPG]</color> Rage tree reset. Refunded <color=#e57373>{spent}</color> points.");
                SaveData();
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
            data.Rage.NodeLevels[nodeId] = current + spend;

            // Record this node as the last-upgraded one for the tree UI flash highlight
            data.Rage.LastUpgradedNodeId = nodeId;
            data.Rage.LastUpgradeFlashTime = Time.realtimeSinceStartup;

            player.ChatMessage(
                $"<color=#ffb74d>[OmniRPG]</color> Allocated <color=#e57373>{spend}</color> point(s) to " +
                $"<color=#e57373>{cfg.DisplayName}</color>. New level: <color=#e57373>{current + spend}/{cfg.MaxLevel}</color>. " +
                $"Remaining Rage points: <color=#e57373>{data.Rage.UnspentPoints}</color>");

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

            string category = args[0].ToLower();   // "xp" or "rage"
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
                default:
                    player.ChatMessage($"<color=#ffb74d>[OmniRPG]</color> Unknown Rage field '{field}'.");
                    return false;
            }

            return true;
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
                    AnchorMin = $"{config.UI.AnchorMinX} {config.UI.AnchorMinY}",
                    AnchorMax = $"{config.UI.AnchorMaxX} {config.UI.AnchorMaxY}"
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
                    AnchorMax = "0.22 1"
                }
            }, panel, UI_MAIN + ".Nav");

            

            // Nav buttons
            float btnTop = 0.9f;
            float btnHeight = 0.07f;

            AddNavButton(container, navPanel, "Profile", "profile", page == "profile", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Rage", "rage", page == "rage", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Leaderboard", "leaderboard", page == "leaderboard", btnTop);
            btnTop -= btnHeight;
            AddNavButton(container, navPanel, "Settings", "settings", page == "settings", btnTop);
            btnTop -= btnHeight;

            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN))
            {
                AddNavButton(container, navPanel, "Admin", "admin", page == "admin", btnTop);
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
                    AnchorMin = "0.22 0",
                    AnchorMax = "1 1"
                }
            }, panel, UI_MAIN + ".Content");

            switch (page)
            {
                case "profile":
                    BuildProfilePage(player, data, contentPanel, container);
                    break;
                case "leaderboard":
                    BuildLeaderboardPage(player, data, contentPanel, container);
                    break;
                case "rage":
                    BuildRagePage(player, data, contentPanel, container);
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
        }

        #endregion

        #region UI Pages

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

        // Rage tree UI page
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

            // Main Rage tree canvas
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
                    AnchorMax = "0.97 0.78"
                }
            }, parent, treePanel);

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
            // Layout: core at top center, weapon nodes along the bottom row.
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

                // Slightly inset to avoid overlapping node circles too much
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

            // Draw each node as a circular-ish panel with icon, name, ring, and upgrade button
            float nodeSize = 0.18f;
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

            // Quick help text
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Click nodes to spend Rage points. Recently upgraded nodes will briefly glow.",
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
            const int pageSize = 10;
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
                    AnchorMin = "0.40 0.05",
                    AnchorMax = "0.60 0.12"
                }
            }, parent);

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
                    AnchorMin = "0.30 0.04",
                    AnchorMax = "0.38 0.13"
                }
            }, parent);

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
                    AnchorMin = "0.62 0.04",
                    AnchorMax = "0.70 0.13"
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

            float rageRowTop = 0.66f;

            void AddRageRow(string label, string field, double value, double stepSmall, double stepBig)
            {
                float yMax = rageRowTop;
                float yMin = yMax - rowHeight;
                rageRowTop -= rowHeight;

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

            // Button to open BotReSpawn XP settings page
            container.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.3 0.4 0.6 0.95",
                    Command = "omnirpg.ui botxp"
                },
                Text =
                {
                    Text = "BotReSpawn XP Settings",
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.62 0.03",
                    AnchorMax = "0.95 0.11"
                }
            }, parent);

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
