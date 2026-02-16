using KindredLogistics;
using KindredLogistics.Commands.Converters;
using KindredLogistics.Services;
using Stunlock.Core;
using Steamworks;
using VampireCommandFramework;

namespace Logistics.Commands
{
    [CommandGroup(name: "logistics", "l")]
    public static class LogisticsCommands
    {
        [Command(name: "sortstash", shortHand: "ss", usage: ".l ss", description: "Toggles autostashing on double clicking sort button for player.")]
        public static void TogglePlayerAutoStash(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var autoStash = Core.PlayerSettings.ToggleSortStash(SteamID);
            ctx.Reply($"SortStash is {(autoStash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }
       
        [Command(name: "craftpull", shortHand: "cr", usage: ".l cr", description: "Toggles right-clicking on recipes for missing ingredients.")]
        public static void TogglePlayerAutoPull(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var autoPull = Core.PlayerSettings.ToggleCraftPull(SteamID);
            ctx.Reply($"CraftPull is {(autoPull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "dontpulllast", shortHand: "dpl", usage: ".l dpl", description: "Toggles the ability to not pull the last item from a container for Logistics commands.")]
        public static void ToggleDontPullLast(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var dontPullLast = Core.PlayerSettings.ToggleDontPullLast(SteamID);
            // Re-evaluate the player's territory so the new retain value takes effect immediately
            var character = ctx.Event.SenderCharacterEntity;
            var territoryId = Core.TerritoryService.GetTerritoryId(character);
            if (territoryId >= 0)
                ConveyorService.MarkTerritoryPending(territoryId);
            ctx.Reply($"DontPullLast is {(dontPullLast ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "autostashmissions", shortHand: "asm", usage: ".l asm", description: "Toggles autostashing for servant missions.")]
        public static void ToggleServantAutoStash(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var autoStashMissions = Core.PlayerSettings.ToggleAutoStashMissions(SteamID);
            ctx.Reply($"AutoStash for missions is {(autoStashMissions ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "conveyor", shortHand: "co", usage: ".l co", description: "Toggles the ability of sender/receiver's to move items around.")]
        public static void ToggleConveyor(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var conveyor = Core.PlayerSettings.ToggleConveyor(SteamID);
            ctx.Reply($"Conveyor is {(conveyor ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "salvage", shortHand: "sal", usage: ".l sal", description: "Toggles the ability to salvage items from a chest named 'salvage'.")]
        public static void ToggleSalvage(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var salvage = Core.PlayerSettings.ToggleSalvage(SteamID);
            ctx.Reply($"Salvage is {(salvage ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "unitspawner", shortHand: "us", usage: ".l sp", description: "Toggles the ability to fill unit stations from a chest named 'spawner'.")]
        public static void ToggleUnitSpawner(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var spawner = Core.PlayerSettings.ToggleUnitSpawner(SteamID);
            ctx.Reply($"Spawner is {(spawner ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "brazier", shortHand: "bz", usage: ".l bz", description: "Toggles the ability to fill braziers from a chest named 'brazier'.")]
        public static void ToggleBrazier(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var brazier = Core.PlayerSettings.ToggleBrazier(SteamID);
            ctx.Reply($"Brazier is {(brazier ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "silentpull", shortHand: "sp", description: "Toggles the ability to not send messages when pulling about where they came from.")]
        public static void ToggleSilentCraftPull(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var silentCraftPull = Core.PlayerSettings.ToggleSilentPull(SteamID);
            ctx.Reply($"SilentPull is {(silentCraftPull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "silentstash", shortHand: "ssh", description: "Toggles the ability to not send messages when stashing items about where they go.")]
        public static void ToggleSilentStash(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var silentStash = Core.PlayerSettings.ToggleSilentStash(SteamID);
            ctx.Reply($"SilentStash is {(silentStash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "stashblacklist", shortHand: "sbl", usage: ".l sbl", description: "Toggles per-player stash blacklist. When enabled, blacklisted items are retained in inventory during .stash.")]
        public static void ToggleStashBlacklist(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var stashBlacklist = Core.PlayerSettings.ToggleStashBlacklist(SteamID);
            ctx.Reply($"StashBlacklist is {(stashBlacklist ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "blacklist", shortHand: "bl", usage: ".l bl [item] [count]", description: "Set or list stash blacklist entries. Use '.l bl' to list, '.l bl <item> <count>' to set (0 to remove).")]
        public static void Blacklist(ChatCommandContext ctx, FoundItem item = default, int count = -1)
        {
            var steamId = ctx.Event.User.PlatformId;

            // No args: list all blacklisted items
            if (item.prefab.GuidHash == 0)
            {
                var bl = Core.PlayerSettings.GetBlacklist(steamId);
                if (bl.Count == 0)
                {
                    ctx.Reply("Stash blacklist is empty.");
                    return;
                }
                var msg = "Stash Blacklist:";
                foreach (var (guidHash, retainCount) in bl)
                {
                    var prefab = new PrefabGUID(guidHash);
                    msg += $"\n  <color=green>{prefab.PrefabName()}</color>: keep <color=white>{retainCount}</color>";
                }
                ctx.Reply(msg);
                return;
            }

            // Item provided but no count: show current entry
            if (count == -1)
            {
                var bl = Core.PlayerSettings.GetBlacklist(steamId);
                if (bl.TryGetValue(item.prefab.GuidHash, out var current))
                    ctx.Reply($"<color=green>{item.prefab.PrefabName()}</color>: keep <color=white>{current}</color>");
                else
                    ctx.Reply($"<color=green>{item.prefab.PrefabName()}</color> is not blacklisted.");
                return;
            }

            // Set entry
            Core.PlayerSettings.SetBlacklistEntry(steamId, item.prefab.GuidHash, count);
            if (count <= 0)
                ctx.Reply($"Removed <color=green>{item.prefab.PrefabName()}</color> from stash blacklist.");
            else
                ctx.Reply($"Stash blacklist: keep <color=white>{count}</color>x <color=green>{item.prefab.PrefabName()}</color> in inventory.");
        }

        [Command(name: "blacklistclear", shortHand: "blclear", usage: ".l blclear", description: "Clears your entire stash blacklist.")]
        public static void BlacklistClear(ChatCommandContext ctx)
        {
            var steamId = ctx.Event.User.PlatformId;
            Core.PlayerSettings.ClearBlacklist(steamId);
            ctx.Reply("Stash blacklist cleared.");
        }

        [Command(name: "keepstack", shortHand: "ks", usage: ".l ks [id] [multiplier]", description: "Sets the multiplier for a K/O template (0-9). K = keep floor, O = deposit cap. Without args, shows all values.")]
        public static void KeepStack(ChatCommandContext ctx, int templateId = -1, float multiplier = -1f)
        {
            if (templateId == -1)
            {
                // Show all keep multipliers
                var multipliers = Core.PlayerSettings.GetAllReserveMultipliers();
                var msg = "K/O Stack Multipliers (K=keep floor, O=deposit cap):";
                for (int i = 0; i <= 9; i++)
                {
                    var mult = Core.PlayerSettings.GetReserveMultiplier(i);
                    msg += $"\n  K{i}/O{i}: <color=white>{mult}x</color> stack";
                }
                ctx.Reply(msg);
                return;
            }

            if (templateId < 0 || templateId > 9)
            {
                ctx.Reply("Template ID must be between <color=white>0</color> and <color=white>9</color>.");
                return;
            }

            if (multiplier < 0)
            {
                var current = Core.PlayerSettings.GetReserveMultiplier(templateId);
                ctx.Reply($"K{templateId}/O{templateId}: <color=white>{current}x</color> stack");
                return;
            }

            Core.PlayerSettings.SetReserveMultiplier(templateId, multiplier);
            ctx.Reply($"K{templateId}/O{templateId} set to <color=green>{multiplier}x</color> stack.");
        }

        [Command(name: "autobase", shortHand: "ab", usage: ".l ab", description: "Toggles autobase mode. When enabled, conveyors S/R disabled, machines pull ingredients by W priority.")]
        public static void ToggleAutoBase(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var autoBase = Core.PlayerSettings.ToggleAutoBase(SteamID);
            // Mark all territories pending for immediate re-evaluation
            for (int i = TerritoryService.MIN_TERRITORY_ID; i <= TerritoryService.MAX_TERRITORY_ID; i++)
                ConveyorService.MarkTerritoryPending(i);
            ctx.Reply($"AutoBase is {(autoBase ? "<color=green>enabled</color> (Conveyor S/R disabled, salvage auto-enabled)" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "settings", shortHand: "s", usage: ".l s", description: "Displays current settings.")]
        public static void DisplaySettings(ChatCommandContext ctx)
        {
            var SteamID = ctx.Event.User.PlatformId;

            var settings = Core.PlayerSettings.GetSettings(SteamID);
            var globalSettings = Core.PlayerSettings.GetGlobalSettings();
            ctx.Reply("KindredLogistics Settings:\n" +
                      $"SortStash: {(globalSettings.SortStash ? (settings.SortStash ? "<color=green>On</color>" : "<color=red>Off</color>") : ("<color=red>Server Off</color>"))}\n" +
                      $"Pull (Global) : {(globalSettings.Pull ? "<color=green>Server On</color>" : "<color=red>Server Off</color>")}\n" +
                      $"CraftPull: {(globalSettings.CraftPull ? (settings.CraftPull ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}\n" +
                      $"DontPullLast: {(settings.DontPullLast ? "<color=green>On</color>" : "<color=red>Off</color>")}\n" +
                      $"AutoStashMissions: {(globalSettings.AutoStashMissions ? (settings.AutoStashMissions ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}");
            ctx.Reply(
                      $"AutoBase: {(globalSettings.AutoBase ? (settings.AutoBase ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}\n" +
                      $"Conveyor: {(globalSettings.Conveyor ? (settings.Conveyor ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}\n" +
                      $"Salvage: {(globalSettings.Salvage ? (settings.Salvage ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}\n" +
                      $"UnitSpawner: {(globalSettings.UnitSpawner ? (settings.UnitSpawner ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}\n" +
                      $"Brazier: {(globalSettings.Brazier ? (settings.Brazier ? "<color=green>On</color>" : "<color=red>Off</color>") : "<color=red>Server Off</color>")}" + $" | Named: {(globalSettings.Named ? "<color=green>Server On</color>" : "<color=red>Server Off</color>")}\n" +
                      $"Silent (Pull: {(settings.SilentPull ? "<color=green>On</color>" : "<color=red>Off</color>")}" + $" | Stash: {(settings.SilentStash ? "<color=green>On</color>" : "<color=red>Off</color>")})\n" +
                      $"StashBL: {(settings.StashBlacklist ? "<color=green>On</color>" : "<color=red>Off</color>")}");
        }

    }

    [CommandGroup(name: "logisticsglobal", "lg")]
    public static class LogisticsGlobal
    {

        [Command(name: "sortstash", shortHand: "ss", usage: ".lg ss", description: "Toggles autostashing on double clicking sort button for player.", adminOnly: true)]
        public static void TogglePlayerAutoStash(ChatCommandContext ctx)
        {
            var autoStash = Core.PlayerSettings.ToggleSortStash();
            ctx.Reply($"Global SortStash is {(autoStash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "pull", shortHand: "p", usage: ".lg p", description: "Toggles the ability to pull items from containers.", adminOnly: true)]
        public static void TogglePlayerPull(ChatCommandContext ctx)
        {
            var pull = Core.PlayerSettings.TogglePull();
            ctx.Reply($"Global Pull is {(pull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "craftpull", shortHand: "cr", usage: ".lg cr", description: "Toggles right-clicking on recipes for missing ingredients.", adminOnly: true)]
        public static void TogglePlayerAutoPull(ChatCommandContext ctx)
        {
            var autoPull = Core.PlayerSettings.ToggleCraftPull();
            ctx.Reply($"CraftPull is {(autoPull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "autostashmissions", shortHand: "asm", usage: ".lg asm", description: "Toggles autostashing for servant missions.", adminOnly: true)]
        public static void ToggleServantAutoStash(ChatCommandContext ctx)
        {
            var autoStashMissions = Core.PlayerSettings.ToggleAutoStashMissions();
            ctx.Reply($"Global AutoStash for missions is {(autoStashMissions ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "conveyor", shortHand: "co", usage: ".lg co", description: "Toggles the ability of sender/receiver's to move items around.", adminOnly: true)]
        public static void ToggleConveyor(ChatCommandContext ctx)
        {
            var conveyor = Core.PlayerSettings.ToggleConveyor();
            ctx.Reply($"Global Conveyor is {(conveyor ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "salvage", shortHand: "sal", usage: ".lg sal", description: "Toggles the ability to salvage items from a chest named 'salvage'.", adminOnly: true)]
        public static void ToggleSalvage(ChatCommandContext ctx)
        {
            var salvage = Core.PlayerSettings.ToggleSalvage();
            ctx.Reply($"Global Salvage is {(salvage ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "unitspawner", shortHand: "us", usage: ".lg sp", description: "Toggles the ability to fill unit stations from a chest named 'spawner'.", adminOnly: true)]
        public static void ToggleUnitSpawner(ChatCommandContext ctx)
        {
            var spawner = Core.PlayerSettings.ToggleUnitSpawner();
            ctx.Reply($"Global Spawner is {(spawner ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "brazier", shortHand: "bz", usage: ".lg bz", description: "Toggles the ability to fill braziers from a chest named 'brazier'.", adminOnly: true)]
        public static void ToggleBrazier(ChatCommandContext ctx)
        {
            var brazier = Core.PlayerSettings.ToggleBrazier();
            ctx.Reply($"Global Brazier is {(brazier ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "autobase", shortHand: "ab", usage: ".lg ab", description: "Toggles global autobase mode.", adminOnly: true)]
        public static void ToggleAutoBase(ChatCommandContext ctx)
        {
            var autoBase = Core.PlayerSettings.ToggleAutoBase();
            // Mark all territories pending for immediate re-evaluation
            for (int i = TerritoryService.MIN_TERRITORY_ID; i <= TerritoryService.MAX_TERRITORY_ID; i++)
                ConveyorService.MarkTerritoryPending(i);
            ctx.Reply($"Global AutoBase is {(autoBase ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "named", shortHand:"nam", usage: ".lg nam", description: "Toggles the ability allow night/proximity controlled braziers.", adminOnly: true)]
        public static void ToggleSolar(ChatCommandContext ctx)
        {
            var solar = Core.PlayerSettings.ToggleSolar();
            ctx.Reply($"Global Named is {(solar ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "trash", usage: ".lg trash", description:"Toggles the ability to allowed trashes to delete contents.", adminOnly: true )]
        public static void ToggleTrash(ChatCommandContext ctx)
        {
            var trash = Core.PlayerSettings.ToggleTrash();
            ctx.Reply($"Global Trash is {(trash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}.");
        }

        [Command(name: "settings", shortHand: "s", usage: ".lg s", description: "Displays current settings.", adminOnly: true)]
        public static void DisplaySettings(ChatCommandContext ctx)
        {
            var settings = Core.PlayerSettings.GetGlobalSettings();
            ctx.Reply("KindredLogistics Global settings:\n" +
                      $"SortStash: {(settings.SortStash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Pull: {(settings.Pull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"CraftPull: {(settings.CraftPull ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"AutoStashMissions: {(settings.AutoStashMissions ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"AutoBase: {(settings.AutoBase ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Conveyor: {(settings.Conveyor ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Salvage: {(settings.Salvage ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"UnitSpawner: {(settings.UnitSpawner ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Brazier: {(settings.Brazier ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Named: {(settings.Named ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}\n" +
                      $"Trash: {(settings.Trash ? "<color=green>enabled</color>" : "<color=red>disabled</color>")}"

                      );
        }
    }

    public static class AdditionalCommands
    {
        [Command(name: "stash", description: "Stashes all items in your inventory.")]
        public static void StashInventory(ChatCommandContext ctx)
        {
            Core.Stash.StashCharacterInventory(ctx.Event.SenderCharacterEntity);
        }

        [Command(name: "pull", description: "Pulls specified item from containers.")]
        public static void PullItem(ChatCommandContext ctx, FoundItem item, int quantity = 1)
        {
            PullService.PullItem(ctx.Event.SenderCharacterEntity, item.prefab, quantity);
        }

        [Command(name: "finditem", shortHand: "fi", description: "Finds the specified item in containers")]
        public static void FindItem(ChatCommandContext ctx, FoundItem item)
        {
            Core.Stash.ReportWhereItemIsLocated(ctx.Event.SenderCharacterEntity, item.prefab);
        }

        [Command(name: "findchest", shortHand: "fc", description: "Finds the specified chest by name")]
        public static void FindChest(ChatCommandContext ctx, string name)
        {
            Core.Stash.ReportWhereChestIsLocated(ctx.Event.SenderCharacterEntity, name);
        }

        [Command(name: "emptytrash", description: "Empties all items in your trash containers.", adminOnly: true)]
        public static void EmptyTrash(ChatCommandContext ctx)
        {
            Core.Trash.EmptyTrash(ctx.Event.SenderCharacterEntity);
        }

        [Command(name: "adminstash", description: "Spawns in items to stash to the current territory.", adminOnly: true)]
        public static void AdminStash(ChatCommandContext ctx, FoundItem item, int quantity = 1)
        {
            Core.Stash.AdminStash(ctx.Event.SenderCharacterEntity, item.prefab, quantity);
        }
    }
}
