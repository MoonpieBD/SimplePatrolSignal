using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SimplePatrolSignal", "Moonpie", "1.0.0")]
    [Description("Call a Patrol Helicopter to your location using a special supply signal.")]
    public class SimplePatrolSignal : RustPlugin
    {
        [PluginReference("NoEscape")]
        private Plugin NoEscape;

        private const string HELI_PREFAB =
            "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
        private const string PermissionUse = "simplepatrolsignal.use";
        private const string PermissionVIP = "simplepatrolsignal.vip";
        private const string PermissionAdmin = "simplepatrolsignal.admin";


        private StoredData storedData;

        private PatrolHelicopter patrol;
        private ConfigurationManager config;
        private bool isActive = false;
        private bool originalUseDangerZones;
        private bool originalMonumentCrash;
        private Vector3 patrolZone;
        private Timer reconsiderTimer;
        private List<LootContainer> processedContainers = new List<LootContainer>();

        #region Initialization

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionVIP, this);
            permission.RegisterPermission(PermissionAdmin, this);

            LoadDefaultMessages();
        }

        private void OnServerInitialized()
        {
            LoadConfigValues();
            LoadData();
            CleanupExpiredCooldowns();
        }

        private void Unload()
        {
            SaveData();
            DestroyPatrol();
        }

        #endregion

        #region Localization

        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["NotAllowed"] = "You are not allowed to use this command.",
                    ["HeliSignalActive"] = "Another Patrol Heli Signal is already active.",
                    ["PatrolCalled"] = "A patrol helicopter is on its way to your location!",
                    ["DestroyingPatrol"] = "The patrol helicopter is leaving the area.",
                    ["ReceivedHeliSignal"] = "You have received a Patrol Heli Signal.",
                    ["CooldownActive"] =
                        "You must wait {0} minutes before using another Patrol Heli Signal.",
                    ["VIPCooldownActive"] =
                        "[VIP] You must wait {0} minutes before using another Patrol Heli Signal.",
                    ["CooldownReset"] = "Your cooldown has been reset.",
                    ["CooldownResetTarget"] = "You've reset the cooldown for {0}.",
                    ["NoActiveHeli"] = "There is no active patrol helicopter.",
                    ["HeliDespawned"] = "Patrol helicopter has been despawned.",
                    ["InvalidPlayer"] = "Player not found.",
                    ["RaidBlocked"] = "You cannot call a patrol helicopter during a raid block.",
                    ["NoEscapeBlocked"] =
                        "You cannot call a patrol helicopter during combat block.",
                },
                this
            );
        }

        private string GetMessage(string key, string userId = null) =>
            lang.GetMessage(key, this, userId);

        #endregion

        #region Data Management

        private class StoredData
        {
            public Dictionary<ulong, double> Cooldowns = new Dictionary<ulong, double>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        private void OnServerSave()
        {
            SaveData();
        }

        private void LoadData()
        {
            try
            {
                storedData =
                    Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
                double currentTime = CurrentTime();
                storedData.Cooldowns = storedData
                    .Cooldowns.Where(kvp =>
                        (currentTime - kvp.Value) < config.Signal.CooldownSeconds
                    )
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            catch
            {
                storedData = new StoredData();
            }
        }

        #endregion

        #region Commands

        [ChatCommand("helisignal")]
        private void CmdHeliSignalChat(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                GiveHeliSignal(player);
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage(GetMessage("NotAllowed", player.UserIDString));
                return;
            }

            switch (args[0].ToLower())
            {
                case "reset":
                    if (args.Length > 1)
                    {
                        var target = BasePlayer.Find(args[1]);
                        if (target == null)
                        {
                            player.ChatMessage(GetMessage("InvalidPlayer", player.UserIDString));
                            return;
                        }
                        ResetCooldown(target);
                        player.ChatMessage(
                            string.Format(
                                GetMessage("CooldownResetTarget", player.UserIDString),
                                target.displayName
                            )
                        );
                    }
                    else
                    {
                        ResetCooldown(player);
                        player.ChatMessage(GetMessage("CooldownReset", player.UserIDString));
                    }
                    break;

                case "despawn":
                    if (!isActive)
                    {
                        player.ChatMessage(GetMessage("NoActiveHeli", player.UserIDString));
                        return;
                    }
                    DestroyPatrol();
                    player.ChatMessage(GetMessage("HeliDespawned", player.UserIDString));
                    break;
            }
        }

        private void ResetCooldown(BasePlayer player)
        {
            if (storedData?.Cooldowns == null)
            {
                PrintError("storedData or storedData.Cooldowns is null, cannot reset cooldown!");
                return;
            }

            storedData.Cooldowns.Remove(player.userID);
            SaveData();
        }

        private void CleanupExpiredCooldowns()
        {
            double currentTime = CurrentTime();
            var expiredCooldowns = storedData
                .Cooldowns.Where(kvp => (currentTime - kvp.Value) >= config.Signal.CooldownSeconds)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var userId in expiredCooldowns)
            {
                storedData.Cooldowns.Remove(userId);
            }

            if (expiredCooldowns.Count > 0)
                SaveData();
        }

        [ConsoleCommand("helisignal")]
        private void CmdHeliSignalConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
            {
                GiveHeliSignal(player);
            }
        }

        private void GiveHeliSignal(BasePlayer player)
        {
            if (!HasAdminPermission(player))
            {
                player.ChatMessage(GetMessage("NotAllowed", player.UserIDString));
                return;
            }

            var supplySignal = CreateHeliSignalItem();
            if (supplySignal != null)
            {
                player.GiveItem(supplySignal);
                player.ChatMessage(GetMessage("ReceivedHeliSignal", player.UserIDString));
            }
        }

        private bool HasPermission(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, PermissionUse);
        }

        private bool HasAdminPermission(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }
        #endregion

        #region Cooldown Management
        private bool IsOnCooldown(BasePlayer player)
        {
            if (!storedData.Cooldowns.TryGetValue(player.userID, out double lastUsage))
                return false;

            double timeSinceLastUse = CurrentTime() - lastUsage;
            float cooldownTime = GetPlayerCooldownTime(player);
            return timeSinceLastUse < cooldownTime;
        }

        private double GetRemainingCooldown(BasePlayer player)
        {
            if (!storedData.Cooldowns.TryGetValue(player.userID, out double lastUsage))
                return 0;

            double timeSinceLastUse = CurrentTime() - lastUsage;
            float cooldownTime = GetPlayerCooldownTime(player);
            double remainingTime = cooldownTime - timeSinceLastUse;
            return remainingTime > 0 ? remainingTime : 0;
        }

        private float GetPlayerCooldownTime(BasePlayer player)
        {
            return HasVIPPermission(player)
                ? config.Signal.VIPCooldownSeconds
                : config.Signal.CooldownSeconds;
        }

        private void SetCooldown(BasePlayer player)
        {
            storedData.Cooldowns[player.userID] = CurrentTime();
            SaveData();
        }

        private double CurrentTime() =>
            System.DateTime.UtcNow.Subtract(System.DateTime.UnixEpoch).TotalSeconds;

        private bool HasVIPPermission(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, PermissionVIP);
        }

        #endregion


        #region Event Hooks

        private bool IsRaidBlocked(BasePlayer player)
        {
            if (NoEscape == null || !NoEscape.IsLoaded)
            {
                PrintWarning(
                    "It seems that the NoEscape Plugin is not loaded and you are trying to use it"
                );
                return false;
            }

            return (bool)(NoEscape.Call("IsRaidBlocked", player) ?? false);
        }

        private bool IsNoEscapeActive(BasePlayer player)
        {
            if (NoEscape == null || !NoEscape.IsLoaded)
            {
                PrintWarning(
                    "It seems that the NoEscape Plugin is not loaded and you are trying to use it"
                );
                return false;
            }

            return (bool)(NoEscape.Call("IsCombatBlocked", player) ?? false);
        }

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity)
        {
            DebugLog($"Explosive thrown by player {player.displayName}.");
            if (entity is SupplySignal signal && signal.skinID == config.Signal.SkinId)
            {
                DebugLog("Supply signal detected.");

                if (!HasPermission(player))
                {
                    DebugLog(
                        $"Permission denied: Player {player.displayName} does not have permission to use the signal."
                    );
                    player.ChatMessage(GetMessage("NotAllowed", player.UserIDString));
                    ReturnSignal(player, signal);
                    return;
                }
                else
                {
                    DebugLog("Player have permissions");
                }

                if (config.BlockDuringRaid && IsRaidBlocked(player))
                {
                    DebugLog(
                        $"Raid block active: Player {player.displayName} is currently raid blocked."
                    );
                    player.ChatMessage(GetMessage("RaidBlocked", player.UserIDString));
                    ReturnSignal(player, signal);
                    return;
                }
                else
                {
                    DebugLog("Player doesn't seems to be raid blocked");
                }

                if (config.BlockDuringNoEscape && IsNoEscapeActive(player))
                {
                    DebugLog(
                        $"Combat block active: Player {player.displayName} is currently in combat block."
                    );
                    player.ChatMessage(GetMessage("NoEscapeBlocked", player.UserIDString));
                    ReturnSignal(player, signal);
                    return;
                }
                else
                {
                    DebugLog("Player doesn't seems to be in combat block");
                }

                if (IsOnCooldown(player))
                {
                    double remainingSeconds = Math.Ceiling(GetRemainingCooldown(player) / 60.0);
                    string messageKey = HasVIPPermission(player)
                        ? "VIPCooldownActive"
                        : "CooldownActive";
                    DebugLog(
                        $"Cooldown active: Player {player.displayName} must wait {remainingSeconds} minutes before using another signal."
                    );
                    player.ChatMessage(
                        string.Format(GetMessage(messageKey, player.UserIDString), remainingSeconds)
                    );
                    ReturnSignal(player, signal);
                    return;
                }
                else
                {
                    DebugLog("Player doesn't seems to be on cooldown");
                }

                signal.CancelInvoke(signal.Explode);
                signal.Invoke(signal.KillMessage, 30f);

                DebugLog("Trying to call the patrol helicopter");

                timer.Once(
                    config.Signal.Warmup,
                    () =>
                    {
                        CallPatrolHelicopter(player);
                        SetCooldown(player);
                        signal.Kill();
                    }
                );
            }
        }

        private void ReturnSignal(BasePlayer player, SupplySignal signal)
        {
            var returnedSignal = CreateHeliSignalItem();
            if (returnedSignal != null)
            {
                player.GiveItem(returnedSignal);
            }
            signal.Kill();
        }

        private object CanLootEntity(BasePlayer player, LootContainer container)
        {
            if (container == null || !config.LootSettings.Enabled)
                return null;

            if (processedContainers.Contains(container))
                return null;

            processedContainers.Add(container);

            string containerName = container.ShortPrefabName;
            float dropChance;

            if (config.LootSettings.Containers.TryGetValue(containerName, out dropChance))
            {
                if (RollChance(dropChance))
                {
                    var item = CreateHeliSignalItem();
                    if (item != null)
                    {
                        container.inventory.capacity++;
                        container.inventorySlots++;
                        item.MoveToContainer(container.inventory);
                    }
                }
            }

            return null;
        }

        private void OnEntityKill(LootContainer container)
        {
            if (container != null)
            {
                processedContainers.Remove(container);
            }
        }

        #endregion

        #region Patrol Helicopter Logic

        private void CallPatrolHelicopter(BasePlayer player)
        {
            if (isActive)
            {
                DebugLog("Heli signal already active");
                player.ChatMessage(GetMessage("HeliSignalActive", player.UserIDString));
                GiveHeliSignal(player);
                return;
            }
            DebugLog("Heli signal not active");

            isActive = true;
            patrolZone = player.transform.position;
            SpawnPatrolHelicopter();

            player.ChatMessage(GetMessage("PatrolCalled", player.UserIDString));

            // Broadcast to all players who called the heli
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected) continue;

                string message = $"<color=#FFD700>Patrol Helicopter Alert:</color> <color=#00FF00>{player.displayName}</color> has just called a patrol helicopter!";
                p.ChatMessage(message);
            }

        }

        private void SpawnPatrolHelicopter()
        {
            DebugLog("Attempting to spawn Patrol Helicopter.");
            originalUseDangerZones = PatrolHelicopterAI.use_danger_zones;
            originalMonumentCrash = PatrolHelicopterAI.monument_crash;

            Vector3 spawnPosition = patrolZone + (Vector3.forward * 500f);
            spawnPosition.y = 60f;

            patrol =
                GameManager.server.CreateEntity(HELI_PREFAB, spawnPosition) as PatrolHelicopter;

            if (patrol == null)
            {
                PrintError("Failed to create Patrol Helicopter entity.");
                isActive = false;
                return;
            }

            patrol.enableSaving = false;
            patrol.Spawn();
            DebugLog("Patrol Helicopter spawned successfully.");

            // Set helicopter health etc.
            patrol.InitializeHealth(config.Patrol.Health, config.Patrol.Health);
            if (patrol.weakspots != null)
            {
                if (patrol.weakspots.Length > 0)
                {
                    patrol.weakspots[0].maxHealth = config.Patrol.MainRotorHealth;
                    patrol.weakspots[0].health = config.Patrol.MainRotorHealth;
                }
                if (patrol.weakspots.Length > 1)
                {
                    patrol.weakspots[1].maxHealth = config.Patrol.TailRotorHealth;
                    patrol.weakspots[1].health = config.Patrol.TailRotorHealth;
                }
            }

            patrol.myAI.timeBetweenRockets = config.Patrol.TimeBeforeRocket;
            patrol.maxCratesToSpawn = config.Patrol.CrateAmount;
            PatrolHelicopterAI.use_danger_zones = false;
            PatrolHelicopterAI.monument_crash = false;
            patrol.myAI.hasInterestZone = true;
            patrol.myAI.interestZoneOrigin = patrolZone;
            patrol.myAI.ExitCurrentState();

            reconsiderTimer = timer.Repeat(10f, 0, ReconsiderPosition);
            timer.Once(config.Patrol.Duration, DestroyPatrol);
        }

        private float lastReconsiderTime = 0f;
        private float reconsiderCooldown = 15f; // seconds between repositioning
        private float minDistanceToPlayer = 150f; // minimum distance heli wants to keep
        private float maxDistanceFromOriginalZone = 500f; // max allowed distance from patrolZone

        private void ReconsiderPosition()
        {
            if (patrol == null || patrol.IsDestroyed)
            {
                DestroyPatrol();
                return;
            }

            // Only reconsider after cooldown
            if (UnityEngine.Time.time - lastReconsiderTime < reconsiderCooldown)
                return;

            // If heli currently has a target, don't reposition
            if (patrol.myAI.leftGun.HasTarget() || patrol.myAI.rightGun.HasTarget())
                return;

            // Find closest valid player
            BasePlayer closestPlayer = BasePlayer.activePlayerList
                .Where(p => p != null && !p.IsSleeping() && !p.IsDead())
                .OrderBy(p => Vector3.Distance(patrol.transform.position, p.transform.position))
                .FirstOrDefault();

            if (closestPlayer != null)
            {
                Vector3 currentZone = patrol.myAI.interestZoneOrigin;
                Vector3 playerPos = closestPlayer.transform.position;

                float distToPlayer = Vector3.Distance(currentZone, playerPos);

                if (distToPlayer > minDistanceToPlayer)
                {
                    // Lerp 30% towards player position
                    Vector3 targetPos = Vector3.Lerp(currentZone, playerPos, 0.3f);

                    // Clamp targetPos to be within 500m of original patrolZone
                    Vector3 directionFromOriginal = targetPos - patrolZone;
                    if (directionFromOriginal.magnitude > maxDistanceFromOriginalZone)
                    {
                        targetPos = patrolZone + directionFromOriginal.normalized * maxDistanceFromOriginalZone;
                    }

                    patrol.myAI.interestZoneOrigin = targetPos;
                    patrol.myAI.State_Move_Enter(targetPos);

                    DebugLog($"ReconsiderPosition: Moving interest zone towards {closestPlayer.displayName} at {targetPos}");
                    lastReconsiderTime = UnityEngine.Time.time;
                }
                else
                {
                    DebugLog("Player too close, keeping interest zone.");
                    lastReconsiderTime = UnityEngine.Time.time;
                }
            }
            else
            {
                // No player found - fallback to original zone
                patrol.myAI.interestZoneOrigin = patrolZone;
                patrol.myAI.State_Move_Enter(patrolZone);
                lastReconsiderTime = UnityEngine.Time.time;
            }
        }


        private void DestroyPatrol()
        {
            DebugLog("Destroying Patrol Helicopter.");
            Puts(GetMessage("DestroyingPatrol"));

            if (reconsiderTimer != null && !reconsiderTimer.Destroyed)
            {
                reconsiderTimer.Destroy();
            }

            if (patrol != null && !patrol.IsDestroyed)
            {
                patrol.myAI.Retire();
                patrol.Kill();
                patrol = null;
            }

            PatrolHelicopterAI.use_danger_zones = originalUseDangerZones;
            PatrolHelicopterAI.monument_crash = originalMonumentCrash;
            isActive = false;
        }

        #endregion

        #region Helper Methods

        private Item CreateHeliSignalItem()
        {
            var item = ItemManager.CreateByName("supply.signal", 1, config.Signal.SkinId);
            if (item != null)
            {
                item.name = config.Signal.DisplayName;
            }
            return item;
        }

        private bool RollChance(float chance)
        {
            return UnityEngine.Random.Range(0f, 100f) <= chance;
        }

        private void DebugLog(string message)
        {
            if (config.DebugMode)
            {
                Puts($"[DEBUG] {message}");
            }
        }

        #endregion

        #region Configuration

        private class ConfigurationManager
        {
            [JsonProperty("Version")]
            public VersionConfig version { get; set; } = new VersionConfig();

            public class VersionConfig
            {
                [JsonProperty("Major")]
                public int major { get; set; } = 1;

                [JsonProperty("Minor")]
                public int minor { get; set; } = 0;

                [JsonProperty("Patch")]
                public int patch { get; set; } = 0;
            }

            [JsonProperty("Supply Signal Settings")]
            public SupplySignalSettings Signal { get; set; } = new SupplySignalSettings();

            [JsonProperty("Patrol Helicopter Settings")]
            public PatrolSettings Patrol { get; set; } = new PatrolSettings();

            [JsonProperty("Loot Settings")]
            public LootSettings LootSettings { get; set; } = new LootSettings();

            [JsonProperty("Block During Raid")]
            public bool BlockDuringRaid { get; set; } = true;

            [JsonProperty("Block During Combat block")]
            public bool BlockDuringNoEscape { get; set; } = true;

            [JsonProperty("Debug Mode")]
            public bool DebugMode { get; set; } = false;
        }

        private class SupplySignalSettings
        {
            [JsonProperty("Skin ID")]
            public ulong SkinId { get; set; } = 3332447426;

            [JsonProperty("Display Name")]
            public string DisplayName { get; set; } = "Patrol Heli Signal";

            [JsonProperty("Warmup Time Before Patrol Arrival (seconds)")]
            public float Warmup { get; set; } = 5f;

            [JsonProperty("Default Cooldown Time (seconds)")]
            public float CooldownSeconds { get; set; } = 3600f;

            [JsonProperty("VIP Cooldown Time (seconds)")]
            public float VIPCooldownSeconds { get; set; } = 1800f;
        }

        private class PatrolSettings
        {
            [JsonProperty("Patrol Duration (seconds)")]
            public float Duration { get; set; } = 1800f;

            [JsonProperty("Helicopter Health")]
            public float Health { get; set; } = 10000f;

            [JsonProperty("Main Rotor Health")]
            public float MainRotorHealth { get; set; } = 900f;

            [JsonProperty("Tail Rotor Health")]
            public float TailRotorHealth { get; set; } = 500f;

            [JsonProperty("Number of Crates to Spawn")]
            public int CrateAmount { get; set; } = 6;

            [JsonProperty("Time Before Firing Rockets (seconds)")]
            public float TimeBeforeRocket { get; set; } = 0.25f;
        }

        private class LootSettings
        {
            [JsonProperty("Enable Loot Drops")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Loot Containers and Drop Chances")]
            public Dictionary<string, float> Containers { get; set; } =
                new Dictionary<string, float>
                {
                    { "crate_normal", 5f },
                    { "crate_normal_2", 5f },
                    { "crate_elite", 10f },
                    { "heli_crate", 15f },
                    { "bradley_crate", 15f },
                };
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigurationManager();
            SaveConfig();
            Puts("Default configuration file created.");
        }

        private void LoadConfigValues()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigurationManager>();

            VersionNumber serverVersion = new VersionNumber(
                config.version.major,
                config.version.minor,
                config.version.patch
            );

            if (serverVersion < Version)
            {
                ConciliateConfiguration(serverVersion);
            }
        }

        private void ConciliateConfiguration(VersionNumber serverVersion)
        {
            config.Signal.CooldownSeconds = 3600f;
            config.Signal.VIPCooldownSeconds = 1800f;
            config.BlockDuringRaid = true;
            config.BlockDuringNoEscape = true;
            PrintWarning("Merging new configuration keys into existing config...");

            config.version.major = Version.Major;
            config.version.minor = Version.Minor;
            config.version.patch = Version.Patch;
            SaveConfig();
        }

        private void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion
    }
}
 