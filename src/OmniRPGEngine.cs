// OmniRPGEngine.cs
// MVP core XP + Rage discipline skeleton with simple UI scaffolding.
// NOTE: Drop into oxide/plugins as-is. Config and data files will be created on first load.

using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OmniRPGEngine", "GuildGPT", "0.2.6")]
    [Description("Universal RPG framework providing XP, levels and discipline-based skill trees (Rage MVP).")]
    public class OmniRPGEngine : RustPlugin
    {
        #region Permissions / References

        private const string PERM_USE = "omnirpgengine.use";
        private const string PERM_ADMIN = "omnirpgengine.admin";

        [PluginReference] private Plugin PermissionsManager;
        [PluginReference] private Plugin ImageLibrary;

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

            public double LevelCurveBase = 100;   // XP for level 1→2
            public double LevelCurveGrowth = 1.25; // Each level multiplies required XP by this
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

            public RageData()
            {
                FuryAmount = 0f;
                FuryExpireTimestamp = 0;
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
            if (player == null) return null;
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

            double xp = 0;

            if (entity is BaseNpc)
            {
                xp = config.XP.BaseKillNpc;
            }
            else if (entity is BasePlayer && entity != killer)
            {
                xp = config.XP.BaseKillPlayer;
            }

            if (xp <= 0) return;

            // Stats tracking
            var killerData = GetOrCreatePlayerData(killer);
            if (entity is BaseNpc)
            {
                if (killerData != null) killerData.NpcKills++;
            }
            else if (entity is BasePlayer && entity != killer)
            {
                if (killerData != null) killerData.PlayerKills++;
                var victimPlayer = entity as BasePlayer;
                var victimData = GetOrCreatePlayerData(victimPlayer);
                if (victimData != null) victimData.Deaths++;
            }

            AwardXp(killer, xp, "Kill");
            OnRageKillEvent(killer, entity);
            SaveData();
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity as BasePlayer;
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

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

        
        private string FormatTime(double seconds)
        {
            if (seconds <= 0) return "0m";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{t.Minutes}m {t.Seconds}s";
        }

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

            var data = GetOrCreatePlayerData(attacker);
            if (data == null) return;

            var weapon = info.Weapon?.GetItem();
            var bonus = GetRageDamageBonus(data, weapon);
            if (bonus <= 0f) return;

            info.damageTypes.ScaleAll(1f + bonus);
        }

        #endregion

        #region Chat Commands

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
                $"Fury: <color=#e57373>{data.Rage.FuryAmount * 100f:0}%</color>");
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
        #endregion

        #region UI

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
            AddNavButton(container, navPanel, "Stats", "stats", page == "stats", btnTop);
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
                case "stats":
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

        private void BuildStatsPage(BasePlayer player, PlayerData data, string parent, CuiElementContainer container)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Stats / Skill Tree (coming soon)",
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
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Admin Settings / Config Editor (coming soon)",
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
    }
}
