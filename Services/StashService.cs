using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Stunlock.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace KindredLogistics.Services
{
    internal class StashService
    {
        const int ACTION_BAR_SLOTS = 8;
        const string SKIP_SUFFIX = "''";
        const float FIND_SPOTLIGHT_DURATION = 15f;

        const string OVERFLOW_SUFFIX = "overflow";
        const string SALVAGE_SUFFIX = "salvage";
        public const string SPOILS_SUFFIX = "spoils";

        static readonly ComponentType[] StashQuery =
            [
                ComponentType.ReadOnly(Il2CppType.Of<InventoryOwner>()),
                ComponentType.ReadOnly(Il2CppType.Of<CastleHeartConnection>()),
                ComponentType.ReadOnly(Il2CppType.Of<AttachedBuffer>()),
                ComponentType.ReadOnly(Il2CppType.Of<NameableInteractable>()),
            ];

        public static readonly PrefabGUID ExternalInventoryPrefab = new(1183666186);
        static readonly PrefabGUID findContainerSpotlightPrefab = new(-2014639169);

        public delegate bool StashFilter(Entity station);

        readonly Regex receiverRegex;
        readonly Regex senderRegex;

        public Regex ReceiverRegex => receiverRegex;

        readonly Dictionary<Entity, (double expirationTime, List<Entity> targetStashes)> activeSpotlights = [];
        
        const float STASH_COOLDOWN = 1f;
        readonly Dictionary<Entity, double> lastStashed = [];
        const int DEFAULT_STASH_PRIORITY = 5;
        static readonly Regex priorityRegex = new(@"(?<![A-Za-z])P(\d)(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        readonly Dictionary<Entity, string> _nameCache = new(capacity: 200);
        readonly Dictionary<Entity, int> _priorityCache = new(capacity: 200);

        internal string GetCachedName(Entity entity)
        {
            if (!_nameCache.TryGetValue(entity, out var name))
            {
                name = entity.Read<NameableInteractable>().Name.ToString().ToLower();
                _nameCache[entity] = name;
            }
            return name;
        }

        internal int GetStashPriority(Entity stash)
        {
            if (_priorityCache.TryGetValue(stash, out var priority))
                return priority;

            var name = GetCachedName(stash);
            var match = priorityRegex.Match(name);
            priority = match.Success ? int.Parse(match.Groups[1].Value) : DEFAULT_STASH_PRIORITY;
            _priorityCache[stash] = priority;
            return priority;
        }

        internal void FlushNameCache()
        {
            _nameCache.Clear();
            _priorityCache.Clear();
            _territoryCache.Clear();
        }

        internal class TerritoryStashData
        {
            public readonly List<Entity> NormalStashes = new(32);
            public readonly List<(int group, Entity stash)> ReceiverStashes = new(16);
            public readonly List<(int group, Entity stash)> SenderStashes = new(16);
            public readonly List<Entity> OverflowStashes = new(4);
            public readonly List<Entity> SalvageStashes = new(4);
            public readonly List<Entity> SpawnerStashes = new(4);
            public readonly List<Entity> BrazierStashes = new(4);
            public readonly List<Entity> TrashStashes = new(4);
        }

        readonly Dictionary<int, TerritoryStashData> _territoryCache = new(capacity: 32);

        TerritoryStashData GetOrClassifyTerritory(int territoryId)
        {
            if (_territoryCache.TryGetValue(territoryId, out var cached))
                return cached;

            var data = new TerritoryStashData();

            var castleHeart = Core.TerritoryService.GetCastleHeart(territoryId);
            if (castleHeart == Entity.Null)
            {
                _territoryCache[territoryId] = data;
                return data;
            }

            var sharedInventoryManager = castleHeart.Read<SharedCastleInventoryConnection>().SharedInventoryManager.GetEntityOnServer();
            if (sharedInventoryManager == Entity.Null)
            {
                _territoryCache[territoryId] = data;
                return data;
            }

            var sharedCastleInventory = Core.EntityManager.GetBuffer<SharedCastleInventories>(sharedInventoryManager);
            for (var i = 0; i < sharedCastleInventory.Length; i++)
            {
                var sharedInventory = sharedCastleInventory[i];
                var stash = sharedInventory.InventorySource;

                if (!Core.EntityManager.Exists(stash)) continue;

                var name = GetCachedName(stash);
                if (name.EndsWith(SKIP_SUFFIX)) continue;

                bool isOverflow = name.Contains(OVERFLOW_SUFFIX);
                bool isSalvage = name.Contains(SALVAGE_SUFFIX);
                bool isSpawner = name.Contains("spawner");
                bool isBrazier = name.Contains("brazier");
                bool isTrash = name.Contains("trash");

                if (isOverflow) data.OverflowStashes.Add(stash);
                if (isSalvage) data.SalvageStashes.Add(stash);
                if (isSpawner) data.SpawnerStashes.Add(stash);
                if (isBrazier) data.BrazierStashes.Add(stash);
                if (isTrash) data.TrashStashes.Add(stash);

                if (!isSalvage && !isOverflow && !name.Contains(SPOILS_SUFFIX))
                    data.NormalStashes.Add(stash);

                var stashTerritoryId = Core.TerritoryService.GetTerritoryId(stash);
                if (stashTerritoryId == territoryId)
                {
                    foreach (Match match in receiverRegex.Matches(name))
                    {
                        var group = int.Parse(match.Groups[1].Value);
                        if (!isOverflow)
                            data.ReceiverStashes.Add((group, stash));
                    }
                    foreach (Match match in senderRegex.Matches(name))
                    {
                        var group = int.Parse(match.Groups[1].Value);
                        data.SenderStashes.Add((group, stash));
                    }
                }
            }

            _territoryCache[territoryId] = data;
            return data;
        }

        public StashService()
        {
            var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            foreach (var entry in StashQuery)
                entityQueryBuilder.AddAll(entry);
            entityQueryBuilder.Dispose();
            receiverRegex = new Regex(Const.RECEIVER_REGEX, RegexOptions.Compiled);
            senderRegex = new Regex(Const.SENDER_REGEX, RegexOptions.Compiled);
        }

        public IEnumerable<Entity> GetAllAlliedStashesOnTerritory(Entity character)
        {
            var territoryIndex = Core.TerritoryService.GetTerritoryId(character);
            if (territoryIndex == -1) yield break;
            foreach(var stash in GetStashesOnTerritory(territoryIndex))
                yield return stash;
        }

        public IEnumerable<Entity> GetStashesOnTerritory(int territoryIndex)
        {
            var castleHeart = Core.TerritoryService.GetCastleHeart(territoryIndex);
            if (castleHeart == Entity.Null) yield break;

            var sharedInventoryManager = castleHeart.Read<SharedCastleInventoryConnection>().SharedInventoryManager.GetEntityOnServer();
            if (sharedInventoryManager == Entity.Null) yield break;

            var sharedCastleInventory = Core.EntityManager.GetBuffer<SharedCastleInventories>(sharedInventoryManager);
            foreach (var sharedInventory in sharedCastleInventory)
            {
                var name = sharedInventory.InventorySource.Read<NameableInteractable>().Name.ToString();
                if (name.EndsWith(SKIP_SUFFIX)) continue;

                yield return sharedInventory.InventorySource;
            }
        }

        public List<(int group, Entity stash)> GetAllReceivingStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).ReceiverStashes;
        }

        public List<(int group, Entity stash)> GetAllSendingStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).SenderStashes;
        }

        public List<Entity> GetAllSalvageStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).SalvageStashes;
        }

        public List<Entity> GetAllSpawnerStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).SpawnerStashes;
        }

        public List<Entity> GetAllBrazierStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).BrazierStashes;
        }

        public List<Entity> GetAllOverflowStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).OverflowStashes;
        }

        public List<Entity> GetAllTrashStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).TrashStashes;
        }

        public void StashCharacterInventory(Entity charEntity)
        {
            try
            {
                var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
                var user = userEntity.Read<User>();


                if (lastStashed.TryGetValue(charEntity, out var lastStashTime) && Core.ServerTime - lastStashTime < STASH_COOLDOWN)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "You must wait before stashing again!");
                    return;
                }

                var downed = new PrefabGUID(-1992158531);
                if (BuffUtility.TryGetBuff(Core.EntityManager, charEntity, downed, out var buff))
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash while downed!");
                    return;
                }

                var health = charEntity.Read<Health>();
                if (health.IsDead)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash when dead!");
                    return;
                }

                var territoryIndex = Core.TerritoryService.GetTerritoryId(charEntity);
                if (territoryIndex == -1)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash outside territories!");
                    return;
                }

                var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryIndex);
                if (castleHeartEntity == Entity.Null)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "There is no heart on this territory!");
                    return;
                }

                if (!Core.ServerGameManager.IsAllies(castleHeartEntity, charEntity))
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "You aren't allies with the heart on this territory!");
                    return;
                }

                var castleHeart = castleHeartEntity.Read<CastleHeart>();
                if (castleHeart.ActiveEvent >= CastleHeartEvent.Attacked)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Unable to stash while castle is {castleHeart.ActiveEvent.ToString()}");
                    return;
                }

                if (BuffUtility.TryGetBuff(Core.Server.EntityManager, charEntity, Const.Buff_InCombat_PvPVampire, out Entity buffEntity))
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Unable to stash while in PvP combat.");
                    return;
                }

                var serverGameManager = Core.ServerGameManager;
                var matches = new Dictionary<PrefabGUID, List<(Entity stash, Entity inventory)>>(capacity: 100);
                var foundStash = false;
                var alreadyAdded = new HashSet<PrefabGUID>();
                var normalStashes = GetOrClassifyTerritory(territoryIndex).NormalStashes;
                foreach (var stash in normalStashes)
                {
                    try
                    {
                        if (stash.Has<CastleWorkstation>() &&
                            stash.Read<CastleWorkstation>().MatchingFloorType != CastleFloorTypes.Treasury)
                        {
                            continue;
                        }
                        if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                            continue;

                        foundStash = true;

                        foreach (var attachedBuffer in buffer)
                        {
                            var attachedEntity = attachedBuffer.Entity;
                            if (!attachedEntity.Has<PrefabGUID>()) continue;
                            if (!attachedEntity.Read<PrefabGUID>().Equals(ExternalInventoryPrefab)) continue;

                            alreadyAdded.Clear();
                            var checkInventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                            foreach (var inventoryEntry in checkInventoryBuffer)
                            {
                                var item = inventoryEntry.ItemType;
                                if (item.GuidHash == 0) continue;
                                if (alreadyAdded.Contains(item)) continue;
                                alreadyAdded.Add(item);
                                if (!matches.TryGetValue(item, out var itemMatches))
                                {
                                    itemMatches = [];
                                    matches[item] = itemMatches;
                                }
                                itemMatches.Add((stash, attachedEntity));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Core.LogException(e, "Stash Retrieval");
                    }
                }

                if (!foundStash)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash as no available stashes found in your current territory!");
                    return;
                }

                foreach (var itemMatches in matches.Values)
                {
                    itemMatches.Sort((a, b) => GetStashPriority(a.stash).CompareTo(GetStashPriority(b.stash)));
                }

                // get player inventory and find allied owned stashes in same territory with item matches
                if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, charEntity, out Entity inventory))
                    return;

                if (!serverGameManager.TryGetBuffer<InventoryBuffer>(inventory, out var inventoryBuffer))
                    return;

                var addItemSettings = Utilities.GetAddItemSettings();
                HashSet<PrefabGUID> transferredItems = [];
                Dictionary<(Entity stash, PrefabGUID item), int> amountStashed = [];
                Dictionary<PrefabGUID, int> amountUnstashed = [];
                var overflowStashes = GetAllOverflowStashes(territoryIndex);
                for (int i = ACTION_BAR_SLOTS; i < inventoryBuffer.Length; i++)
                {
                    var itemEntry = inventoryBuffer[i];
                    var item = itemEntry.ItemType;
                    

                    var hasItemEntity = !itemEntry.ItemEntity.GetEntityOnServer().Equals(Entity.Null);

                    if (hasItemEntity)
                    {
                        var success = false;
                        if (matches.TryGetValue(item, out var stashEntries))
                        {
                            foreach (var stashEntry in stashEntries)
                            {
                                try
                                {
                                    var stashInventoryBuffer = stashEntry.inventory.ReadBuffer<InventoryBuffer>();

                                    for (int j = 0; j < stashInventoryBuffer.Length; j++)
                                    {
                                        if (!stashInventoryBuffer[j].ItemType.Equals(PrefabGUID.Empty)) continue;

                                        transferredItems.Add(item);
                                        stashInventoryBuffer[j] = itemEntry;

                                        var itemEntity = itemEntry.ItemEntity.GetEntityOnServer();
                                        if (itemEntity.Has<InventoryItem>())
                                        {
                                            var inventoryItem = itemEntity.Read<InventoryItem>();
                                            inventoryItem.ContainerEntity = stashEntry.stash;
                                            itemEntity.Write(inventoryItem);
                                        }

                                        if (amountStashed.TryGetValue((stashEntry.stash, item), out var amount))
                                            amountStashed[(stashEntry.stash, item)] = amount + 1;
                                        else
                                            amountStashed[(stashEntry.stash, item)] = 1;

                                        InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        success = true;
                                        break;
                                    }

                                    if (success) break;
                                }
                                catch (Exception e)
                                {
                                    Core.LogException(e, "Item Entity Storage");
                                }
                            }
                        }

                        if (!success)
                        {
                            ItemData itemData = default;
                            if (overflowStashes.Any() &&
                                Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(itemEntry.ItemType, out var prefab))
                            {
                                itemData = prefab.Read<ItemData>();
                            }

                            var isSoulshard = itemData.ItemCategory == ItemCategory.Soulshard;

                            foreach (var overflowStash in overflowStashes)
                            {
                                try
                                {
                                    if (!serverGameManager.TryGetBuffer<InventoryInstanceElement>(overflowStash, out var iieBuffer)) continue;

                                    Entity overflowInventory = Entity.Null;
                                    foreach (var iie in iieBuffer)
                                    {
                                        if (iie.RestrictedType != PrefabGUID.Empty && iie.RestrictedType != itemData.ItemTypeGUID ||
                                            iie.RestrictedCategory != 0 && (iie.RestrictedCategory & (long)itemData.ItemCategory) == 0 ||
                                            isSoulshard && iie.RestrictedCategory == 0)
                                            continue;
                                        overflowInventory = iie.ExternalInventoryEntity.GetEntityOnServer();
                                    }

                                    if (overflowInventory == Entity.Null) continue;

                                    var overflowInventoryBuffer = overflowInventory.ReadBuffer<InventoryBuffer>();
                                    for (int j = 0; j < overflowInventoryBuffer.Length; j++)
                                    {
                                        if (!overflowInventoryBuffer[j].ItemType.Equals(PrefabGUID.Empty)) continue;
                                        
                                        transferredItems.Add(item);
                                        overflowInventoryBuffer[j] = itemEntry;
                                        
                                        var itemEntity = itemEntry.ItemEntity.GetEntityOnServer();
                                        if (itemEntity.Has<InventoryItem>())
                                        {
                                            var inventoryItem = itemEntity.Read<InventoryItem>();
                                            inventoryItem.ContainerEntity = overflowStash;
                                            itemEntity.Write(inventoryItem);
                                        }

                                        if (amountStashed.TryGetValue((overflowStash, item), out var amount))
                                            amountStashed[(overflowStash, item)] = amount + 1;
                                        else
                                            amountStashed[(overflowStash, item)] = 1;

                                        InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        success = true;
                                        break;
                                    }
                                    if (success) break;
                                }
                                catch (Exception e)
                                {
                                    Core.LogException(e, "Overflow Item Entity Storage");
                                }
                            }
                        }

                        if (!success)
                        {
                            if (amountUnstashed.TryGetValue(item, out var amount))
                                amountUnstashed[item] = amount + 1;
                            else
                                amountUnstashed[item] = 1;
                        }
                    }
                    else
                    {
                        if (matches.TryGetValue(item, out var stashEntries))
                        {
                            foreach (var stashEntry in stashEntries)
                            {
                                try
                                {
                                    var addItemResponse = InventoryUtilitiesServer.TryAddItem(addItemSettings, stashEntry.inventory, itemEntry);

                                    if (!addItemResponse.Success) continue;

                                    transferredItems.Add(item);
                                    var transferredAmount = itemEntry.Amount - addItemResponse.RemainingAmount;
                                    if (amountStashed.TryGetValue((stashEntry.stash, item), out var amount))
                                        amountStashed[(stashEntry.stash, item)] = amount + transferredAmount;
                                    else
                                        amountStashed[(stashEntry.stash, item)] = transferredAmount;

                                    itemEntry.Amount = addItemResponse.RemainingAmount;
                                    if (!addItemResponse.ItemsRemaining)
                                    {
                                        InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        break;
                                    }
                                }
                                catch (Exception e)
                                {
                                    Core.LogException(e, "Item Storage");
                                }
                            }
                        }

                        if (itemEntry.Amount > 0)
                        {
                            ItemData itemData = default;
                            if (overflowStashes.Any() &&
                                Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(itemEntry.ItemType, out var prefab))
                            {
                                itemData = prefab.Read<ItemData>();
                            }

                            var isSoulshard = itemData.ItemCategory == ItemCategory.Soulshard;

                            foreach (var overflowStash in overflowStashes)
                            {
                                try
                                {
                                    if (!serverGameManager.TryGetBuffer<InventoryInstanceElement>(overflowStash, out var iieBuffer)) continue;

                                    Entity overflowInventory = Entity.Null;
                                    foreach (var iie in iieBuffer)
                                    {
                                        if (iie.RestrictedType != PrefabGUID.Empty && iie.RestrictedType != itemData.ItemTypeGUID ||
                                            iie.RestrictedCategory != 0 && (iie.RestrictedCategory & (long)itemData.ItemCategory) == 0 ||
                                            isSoulshard && iie.RestrictedCategory == 0)
                                            continue;
                                        overflowInventory = iie.ExternalInventoryEntity.GetEntityOnServer();
                                    }

                                    if (overflowInventory == Entity.Null) continue;

                                    var addItemResponse = InventoryUtilitiesServer.TryAddItem(addItemSettings, overflowInventory, itemEntry);
                                    if (!addItemResponse.Success) continue;

                                    transferredItems.Add(item);
                                    var transferredAmount = itemEntry.Amount - addItemResponse.RemainingAmount;
                                    if (amountStashed.TryGetValue((overflowStash, item), out var amount))
                                        amountStashed[(overflowStash, item)] = amount + transferredAmount;
                                    else
                                        amountStashed[(overflowStash, item)] = transferredAmount;
                                    itemEntry.Amount = addItemResponse.RemainingAmount;
                                    if (!addItemResponse.ItemsRemaining)
                                    {
                                        InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        break;
                                    }
                                }
                                catch (Exception e)
                                {
                                    Core.LogException(e, "Overflow Item Storage");
                                }
                            }
                        }

                        if (itemEntry.Amount > 0)
                        {
                            inventoryBuffer[i] = itemEntry;

                            if (amountUnstashed.TryGetValue(item, out var amount))
                                amountUnstashed[item] = amount + itemEntry.Amount;
                            else
                                amountUnstashed[item] = itemEntry.Amount;
                        }
                    }
                }

                if (amountStashed.Count > 0)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Stashed items from your inventory to the current territory!");
                }
                else
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "No items were able to stash from your inventory!");
                }

                if (!Core.PlayerSettings.IsSilentStashEnabled(user.PlatformId))
                {
                    foreach (var ((stash, item), amount) in amountStashed)
                    {
                        Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                               $"Stashed <color=white>{amount}</color>x <color=green>{item.PrefabName()}</color> to <color=#FFC0CB>{stash.EntityName()}</color>");
                    }

                    foreach (var stashedItemType in transferredItems)
                    {
                        if (amountUnstashed.TryGetValue(stashedItemType, out var amount))
                            Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                                               $"Unable to stash <color=white>{amount}</color>x <color=green>{stashedItemType.PrefabName()}</color> due to insufficient space in stashes!");
                    }
                }
            }
            catch (Exception e)
            {
                Core.LogException(e, "Stash Character Inventory");
            }
        }

        public void AdminStash(Entity charEntity, PrefabGUID itemType, int amountToGive)
        {
            try
            {
                var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
                var user = userEntity.Read<User>();

                var territoryIndex = Core.TerritoryService.GetTerritoryId(charEntity);
                if (territoryIndex == -1)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash outside territories!");
                    return;
                }

                var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryIndex);
                if (castleHeartEntity == Entity.Null)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "There is no heart on this territory!");
                    return;
                }

                var serverGameManager = Core.ServerGameManager;
                var matches = new HashSet<Entity>(capacity: 100);
                var foundStash = false;
                foreach (var stash in GetStashesOnTerritory(territoryIndex))
                {
                    try
                    {
                        if (stash.Has<CastleWorkstation>() &&
                            stash.Read<CastleWorkstation>().MatchingFloorType != CastleFloorTypes.Treasury)
                        {
                            continue;
                        }
                        if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                            continue;

                        var name = stash.Read<NameableInteractable>().Name.ToString().ToLower();

                        if (name.Contains(SALVAGE_SUFFIX) || name.Contains(OVERFLOW_SUFFIX))
                            continue;

                        foundStash = true;

                        foreach (var attachedBuffer in buffer)
                        {
                            var attachedEntity = attachedBuffer.Entity;
                            if (!attachedEntity.Has<PrefabGUID>()) continue;
                            if (!attachedEntity.Read<PrefabGUID>().Equals(ExternalInventoryPrefab)) continue;

                            var checkInventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                            foreach (var inventoryEntry in checkInventoryBuffer)
                            {
                                var item = inventoryEntry.ItemType;
                                if (item != itemType) continue;
                                matches.Add(stash);
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Core.LogException(e, "Stash Retrieval");
                    }
                }

                if (!foundStash)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to stash as no available stashes found in your current territory!");
                    return;
                }

                // get player inventory and find allied owned stashes in same territory with item matches
                if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, charEntity, out Entity inventory))
                    return;

                if (!serverGameManager.TryGetBuffer<InventoryBuffer>(inventory, out var inventoryBuffer))
                    return;

                var addItemSettings = Utilities.GetAddItemSettings();
                HashSet<PrefabGUID> transferredItems = [];
                Dictionary<Entity, int> amountStashed = [];
                Dictionary<PrefabGUID, int> amountUnstashed = [];
                var overflowStashes = GetAllOverflowStashes(territoryIndex);
                            
                foreach (var stash in matches)
                {
                    try
                    {
                        var inventoryResponse = serverGameManager.TryAddInventoryItem(stash, itemType, amountToGive);

                        if (!inventoryResponse.Success) continue;
                        var transferredAmount = amountToGive - inventoryResponse.RemainingAmount;
                        amountToGive = inventoryResponse.RemainingAmount;

                        if (amountStashed.TryGetValue(stash, out var amount))
                            amountStashed[stash] = amount + transferredAmount;
                        else
                            amountStashed[stash] = transferredAmount;

                        if (amountToGive <= 0) break;
                    }
                    catch (Exception e)
                    {
                        Core.LogException(e, "Item Storage");
                    }
                }

                if (amountToGive > 0)
                {
                    foreach (var overflowStash in overflowStashes)
                    {
                        try
                        {
                            if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buffer))
                                continue;

                            Entity overflowInventory = Entity.Null;
                            foreach (var attachedBuffer in buffer)
                            {
                                var attachedEntity = attachedBuffer.Entity;
                                if (!attachedEntity.Has<PrefabGUID>()) continue;
                                if (!attachedEntity.Read<PrefabGUID>().Equals(ExternalInventoryPrefab)) continue;
                                overflowInventory = attachedEntity;
                                break;
                            }

                            if (overflowInventory == Entity.Null) continue;


                            var inventoryResponse = serverGameManager.TryAddInventoryItem(overflowStash, itemType, amountToGive);

                            if (!inventoryResponse.Success) continue;
                            var transferredAmount = amountToGive - inventoryResponse.RemainingAmount;
                            amountToGive = inventoryResponse.RemainingAmount;

                            if (amountStashed.TryGetValue(overflowStash, out var amount))
                                amountStashed[overflowStash] = amount + transferredAmount;
                            else
                                amountStashed[overflowStash] = transferredAmount;

                            if (amountToGive <= 0) break;
                        }
                        catch (Exception e)
                        {
                            Core.LogException(e, "Overflow Item Storage");
                        }
                    }
                }

                if (amountStashed.Count > 0)
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Admin stashed items to the current territory!");
                }
                else
                {
                    Utilities.SendSystemMessageToClient(Core.EntityManager, user, "No items were able to admin stash!");
                }

                if (!Core.PlayerSettings.IsSilentStashEnabled(user.PlatformId))
                {
                    foreach (var (stash, amount) in amountStashed)
                    {
                        Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                               $"Admin Stashed <color=white>{amount}</color>x <color=green>{itemType.PrefabName()}</color> to <color=#FFC0CB>{stash.EntityName()}</color>");
                    }

                    if (amountToGive > 0)
                        Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                                            $"Unable to admin stash <color=white>{amountToGive}</color>x <color=green>{itemType.PrefabName()}</color> due to insufficient space in stashes!");
                }
            }
            catch (Exception e)
            {
                Core.LogException(e, "AdminStash");
            }
        }

        public void ReportWhereItemIsLocated(Entity charEntity, PrefabGUID item)
        {
            var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
            var user = userEntity.Read<User>();

            ClearSpotlights(userEntity);

            var territoryIndex = Core.TerritoryService.GetTerritoryId(charEntity);
            if (territoryIndex == -1)
            {
                Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to search for items outside territories!");
                return;
            }

            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Find Item Report\n--------------------------------");
            var serverGameManager = Core.ServerGameManager;
            var foundStash = false;
            var totalFound = 0;
            var itemName = item.PrefabName();
            foreach (var stash in GetAllAlliedStashesOnTerritory(charEntity))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                    continue;

                foundStash = true;

                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(ExternalInventoryPrefab)) continue;

                    var amountFound = serverGameManager.GetInventoryItemCount(attachedEntity, item);
                    if (amountFound > 0)
                    {
                        totalFound += amountFound;
                        Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                                       $"<color=white>{amountFound}</color>x <color=green>{item.PrefabName()}</color> found in <color=#FFC0CB>{stash.EntityName()}</color>");
                        AddSpotlight(stash, userEntity);
                    }
                }
            }

            if (!foundStash)
            {
                Utilities.SendSystemMessageToClient(Core.EntityManager, user, "No available stashes found in your current territory!");
                return;
            }

            Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Total <color=green>{itemName}</color> found: <color=white>{totalFound}</color>");
        }

        public void ReportWhereChestIsLocated(Entity charEntity, string chestName)
        {
            var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
            var user = userEntity.Read<User>();
            ClearSpotlights(userEntity);
            var territoryIndex = Core.TerritoryService.GetTerritoryId(charEntity);
            if (territoryIndex == -1)
            {
                Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to search for chests outside territories!");
                return;
            }
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Find Chest Report\n--------------------------------");
            var serverGameManager = Core.ServerGameManager;
            var foundStash = false;
            var totalFound = 0;
            var searchName = chestName.ToLower();
            foreach (var stash in GetAllAlliedStashesOnTerritory(charEntity))
            {
                var stashName = stash.Read<NameableInteractable>().Name.ToString();
                var stashNameLower = stashName.ToLower();
                if (!stashNameLower.Contains(searchName)) continue;
                foundStash = true;
                totalFound++;

                // Highlight the searched text within the stash name
                var highlightedName = stashName.Replace(chestName, $"<color=yellow><b>{chestName}</b></color>", StringComparison.OrdinalIgnoreCase);

                Utilities.SendSystemMessageToClient(Core.EntityManager, user,
                                       $"Found chest: <color=#FFC0CB>{highlightedName}</color>");
                AddSpotlight(stash, userEntity);
            }
            if (!foundStash)
            {
                Utilities.SendSystemMessageToClient(Core.EntityManager, user, "No matching stashes found in your current territory!");
                return;
            }
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Total chests matching <color=green>{chestName}</color>: <color=white>{totalFound}</color>");
        }

        void ClearSpotlights(Entity userEntity)
        {
            if (!activeSpotlights.TryGetValue(userEntity, out var spotlight))
                return;
            activeSpotlights.Remove(userEntity);

            if (spotlight.expirationTime < Core.ServerTime)
                return;

            foreach (var stash in spotlight.targetStashes)
            {
                Buffs.RemoveBuff(stash, findContainerSpotlightPrefab);
            }
        }

        void AddSpotlight(Entity stash, Entity userEntity)
        {
            if (!activeSpotlights.TryGetValue(userEntity, out var spotlight))
            {
                spotlight.expirationTime = Core.ServerTime + FIND_SPOTLIGHT_DURATION;
                spotlight.targetStashes = [];
                activeSpotlights.Add(userEntity, spotlight);
            }
            spotlight.targetStashes.Add(stash);

            Buffs.RemoveAndAddBuff(userEntity, stash, findContainerSpotlightPrefab, FIND_SPOTLIGHT_DURATION, UpdateSpotlight);

            void UpdateSpotlight(Entity buffEntity)
            {
                var character = userEntity.Read<User>().LocalCharacter;
                buffEntity.Write<SpellTarget>(new()
                {
                    Target = character
                });
                buffEntity.Write<EntityOwner>(new()
                {
                    Owner = character.GetEntityOnServer()
                });
                buffEntity.Write<EntityCreator>(new()
                {
                    Creator = character.GetEntityOnServer()
                });
            }
        }
    }
}