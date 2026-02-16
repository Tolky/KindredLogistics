using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Stunlock.Core;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Entities;

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
        static readonly Regex reserveRegex = new(@"(?<![A-Za-z])K(\d)(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex capRegex = new(@"(?<![A-Za-z])O(\d)(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        readonly Dictionary<Entity, string> _nameCache = new(capacity: 200);
        readonly Dictionary<Entity, int> _priorityCache = new(capacity: 200);
        readonly Dictionary<Entity, int> _reserveCache = new(capacity: 200);
        readonly Dictionary<Entity, int> _capCache = new(capacity: 200);

        // Pooled collections for StashCharacterInventory to avoid per-call allocations
        readonly Dictionary<PrefabGUID, List<(Entity stash, Entity inventory)>> _stashMatches = new(capacity: 100);
        readonly List<List<(Entity stash, Entity inventory)>> _stashMatchListPool = new(64);
        readonly HashSet<PrefabGUID> _stashAlreadyAdded = new(32);
        readonly HashSet<PrefabGUID> _stashTransferredItems = new(32);
        readonly Dictionary<(Entity stash, PrefabGUID item), int> _stashAmountStashed = new(32);
        readonly Dictionary<PrefabGUID, int> _stashAmountUnstashed = new(16);
        int _stashMatchListPoolIndex;

        List<(Entity stash, Entity inventory)> RentMatchList()
        {
            if (_stashMatchListPoolIndex < _stashMatchListPool.Count)
            {
                var list = _stashMatchListPool[_stashMatchListPoolIndex++];
                list.Clear();
                return list;
            }
            var newList = new List<(Entity stash, Entity inventory)>(8);
            _stashMatchListPool.Add(newList);
            _stashMatchListPoolIndex++;
            return newList;
        }

        void ClearStashCollections()
        {
            _stashMatches.Clear();
            _stashMatchListPoolIndex = 0;
            _stashAlreadyAdded.Clear();
            _stashTransferredItems.Clear();
            _stashAmountStashed.Clear();
            _stashAmountUnstashed.Clear();
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
            public readonly HashSet<Entity> SalvageReceiverStashes = new(4);
            public readonly List<Entity> AllStashes = new(32);

            public void Clear()
            {
                NormalStashes.Clear();
                ReceiverStashes.Clear();
                SenderStashes.Clear();
                OverflowStashes.Clear();
                SalvageStashes.Clear();
                SpawnerStashes.Clear();
                BrazierStashes.Clear();
                TrashStashes.Clear();
                SalvageReceiverStashes.Clear();
                AllStashes.Clear();
            }
        }

        readonly Dictionary<int, TerritoryStashData> _territoryCache = new(capacity: 32);

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

        // Returns the keep template ID (0-9) for a stash, or -1 if no K tag.
        internal int GetReserveTemplateId(Entity stash)
        {
            if (_reserveCache.TryGetValue(stash, out var templateId))
                return templateId;

            var name = GetCachedName(stash);
            var match = reserveRegex.Match(name);
            templateId = match.Success ? int.Parse(match.Groups[1].Value) : -1;
            _reserveCache[stash] = templateId;
            return templateId;
        }

        // Returns the keep amount for a given item in a stash based on its K template.
        // Returns -1 if no K tag (no keep limit).
        internal int GetReserveAmount(Entity stash, int maxStackSize)
        {
            var templateId = GetReserveTemplateId(stash);
            if (templateId < 0) return -1;
            var multiplier = Core.PlayerSettings.GetReserveMultiplier(templateId);
            return (int)System.Math.Ceiling(multiplier * maxStackSize);
        }

        // Returns the cap template ID (0-9) for a stash, or -1 if no O tag.
        internal int GetCapTemplateId(Entity stash)
        {
            if (_capCache.TryGetValue(stash, out var templateId))
                return templateId;

            var name = GetCachedName(stash);
            var match = capRegex.Match(name);
            templateId = match.Success ? int.Parse(match.Groups[1].Value) : -1;
            _capCache[stash] = templateId;
            return templateId;
        }

        // Returns the cap amount for a given item in a stash based on its O template.
        // Returns -1 if no O tag (no cap). Uses Floor so partial last stacks are not counted.
        internal int GetCapAmount(Entity stash, int maxStackSize)
        {
            var templateId = GetCapTemplateId(stash);
            if (templateId < 0) return -1;
            var multiplier = Core.PlayerSettings.GetReserveMultiplier(templateId);
            return (int)System.Math.Floor(multiplier * maxStackSize);
        }

        // Clamps a proposed deposit amount to respect an O-cap on the destination stash.
        // Returns the allowed amount (0 = nothing allowed, -1 = no cap / unlimited).
        // For stackable items: if the allowed remainder would create a new incomplete stack,
        // trim to only fill existing partial stacks + complete new stacks.
        internal int ClampForCap(Entity stash, Entity inventoryEntity, PrefabGUID item, int maxStackSize, int proposedAmount)
        {
            var capAmount = GetCapAmount(stash, maxStackSize);
            if (capAmount < 0) return proposedAmount; // no cap

            // Count current amount of this item in the stash inventory
            var currentAmount = Core.ServerGameManager.GetInventoryItemCount(inventoryEntity, item);
            var capRemaining = capAmount - currentAmount;
            if (capRemaining <= 0) return 0;

            var allowed = System.Math.Min(proposedAmount, capRemaining);

            // Partial-stack rule: don't create incomplete new stacks.
            // Find how much partial space exists in existing stacks of this item.
            if (maxStackSize > 1 && allowed > 0)
            {
                var partialSpace = 0;
                var invBuffer = inventoryEntity.ReadBuffer<InventoryBuffer>();
                for (int s = 0; s < invBuffer.Length; s++)
                {
                    if (invBuffer[s].ItemType.GuidHash == item.GuidHash && invBuffer[s].Amount < maxStackSize)
                        partialSpace += maxStackSize - invBuffer[s].Amount;
                }

                // How much would go into new stacks after filling partials?
                var afterPartials = allowed - partialSpace;
                if (afterPartials > 0)
                {
                    // Only allow complete new stacks beyond partial fills
                    var completeNewStacks = afterPartials / maxStackSize;
                    allowed = partialSpace + completeNewStacks * maxStackSize;
                }
            }

            return System.Math.Max(allowed, 0);
        }

        internal void FlushNameCache()
        {
            _nameCache.Clear();
            _priorityCache.Clear();
            _reserveCache.Clear();
            _capCache.Clear();
            _territoryCache.Clear();
        }

        internal void InvalidateTerritory(int territoryId)
        {
            _territoryCache.Remove(territoryId);
        }

        internal void InvalidateAllTerritories()
        {
            _territoryCache.Clear();
        }

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
                bool isSkipped = name.EndsWith(SKIP_SUFFIX);

                data.AllStashes.Add(stash);

                bool isOverflow = name.Contains(OVERFLOW_SUFFIX);
                bool isSalvage = name.Contains(SALVAGE_SUFFIX);
                bool isSpawner = name.Contains("spawner");
                bool isBrazier = name.Contains("brazier");
                bool isTrash = name.Contains("trash");

                if (isOverflow) data.OverflowStashes.Add(stash);
                if (isSalvage)
                {
                    data.SalvageStashes.Add(stash);
                    if (receiverRegex.IsMatch(name))
                        data.SalvageReceiverStashes.Add(stash);
                }
                if (isSpawner) data.SpawnerStashes.Add(stash);
                if (isBrazier) data.BrazierStashes.Add(stash);
                if (isTrash) data.TrashStashes.Add(stash);

                if (!isSalvage && !isOverflow && !name.Contains(SPOILS_SUFFIX) && !isSkipped)
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

        readonly List<Entity> _stashesOnTerritoryResult = new(64);

        public List<Entity> GetStashesOnTerritory(int territoryIndex)
        {
            _stashesOnTerritoryResult.Clear();

            var castleHeart = Core.TerritoryService.GetCastleHeart(territoryIndex);
            if (castleHeart == Entity.Null) return _stashesOnTerritoryResult;

            var sharedInventoryManager = castleHeart.Read<SharedCastleInventoryConnection>().SharedInventoryManager.GetEntityOnServer();
            if (sharedInventoryManager == Entity.Null) return _stashesOnTerritoryResult;

            var sharedCastleInventory = Core.EntityManager.GetBuffer<SharedCastleInventories>(sharedInventoryManager);

            // First pass: non-K stashes (normal priority)
            for (int i = 0; i < sharedCastleInventory.Length; i++)
            {
                var stash = sharedCastleInventory[i].InventorySource;
                if (!Core.EntityManager.Exists(stash)) continue;
                var name = GetCachedName(stash);
                if (name.EndsWith(SKIP_SUFFIX)) continue;
                if (GetReserveTemplateId(stash) >= 0) continue;
                _stashesOnTerritoryResult.Add(stash);
            }

            // Second pass: K-tagged stashes last (deprioritized for pulls)
            for (int i = 0; i < sharedCastleInventory.Length; i++)
            {
                var stash = sharedCastleInventory[i].InventorySource;
                if (!Core.EntityManager.Exists(stash)) continue;
                var name = GetCachedName(stash);
                if (name.EndsWith(SKIP_SUFFIX)) continue;
                if (GetReserveTemplateId(stash) < 0) continue;
                _stashesOnTerritoryResult.Add(stash);
            }

            return _stashesOnTerritoryResult;
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

        public List<Entity> GetAllStashes(int territoryId)
        {
            return GetOrClassifyTerritory(territoryId).AllStashes;
        }

        public bool IsClassifiedAsReceiver(int territoryId, Entity stash)
        {
            return GetOrClassifyTerritory(territoryId).SalvageReceiverStashes.Contains(stash);
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
                ClearStashCollections();
                var matches = _stashMatches;
                var foundStash = false;
                var alreadyAdded = _stashAlreadyAdded;
                // Force fresh classification to include newly placed chests
                InvalidateTerritory(territoryIndex);
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
                                    itemMatches = RentMatchList();
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
                var transferredItems = _stashTransferredItems;
                var amountStashed = _stashAmountStashed;
                var amountUnstashed = _stashAmountUnstashed;
                var overflowStashes = GetAllOverflowStashes(territoryIndex);

                // Stash blacklist: build retain counters if feature is enabled
                Dictionary<int, int> retainRemaining = null;
                if (Core.PlayerSettings.IsStashBlacklistEnabled(user.PlatformId))
                {
                    var blacklist = Core.PlayerSettings.GetBlacklist(user.PlatformId);
                    if (blacklist.Count > 0)
                    {
                        retainRemaining = new Dictionary<int, int>(blacklist.Count);
                        foreach (var (guidHash, retainCount) in blacklist)
                            retainRemaining[guidHash] = retainCount;
                    }
                }

                for (int i = ACTION_BAR_SLOTS; i < inventoryBuffer.Length; i++)
                {
                    var itemEntry = inventoryBuffer[i];
                    var item = itemEntry.ItemType;

                    // Stash blacklist: retain items up to configured count
                    int retainInSlot = 0;
                    if (retainRemaining != null && item.GuidHash != 0 &&
                        retainRemaining.TryGetValue(item.GuidHash, out var remaining) && remaining > 0)
                    {
                        retainInSlot = Math.Min(remaining, itemEntry.Amount);
                        retainRemaining[item.GuidHash] = remaining - retainInSlot;
                        if (retainInSlot >= itemEntry.Amount)
                            continue; // retain entire slot
                        itemEntry.Amount -= retainInSlot; // only stash the excess
                    }

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
                                    // O cap: check if this stash has reached its cap for this item
                                    if (GetCapTemplateId(stashEntry.stash) >= 0 &&
                                        Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(item, out var capPrefab))
                                    {
                                        var maxStack = capPrefab.Read<ItemData>().MaxAmount;
                                        var capAmt = GetCapAmount(stashEntry.stash, maxStack);
                                        if (capAmt >= 0)
                                        {
                                            var currentCount = Core.ServerGameManager.GetInventoryItemCount(stashEntry.inventory, item);
                                            if (currentCount >= capAmt) continue;
                                        }
                                    }

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
                            if (overflowStashes.Count > 0 &&
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
                                    // O cap: clamp deposit amount, track excess for next stash
                                    var capExcess = 0;
                                    if (GetCapTemplateId(stashEntry.stash) >= 0 &&
                                        Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(item, out var capPrefab2))
                                    {
                                        var maxStack = capPrefab2.Read<ItemData>().MaxAmount;
                                        var clamped = ClampForCap(stashEntry.stash, stashEntry.inventory, item, maxStack, itemEntry.Amount);
                                        if (clamped <= 0) continue;
                                        capExcess = itemEntry.Amount - clamped;
                                        itemEntry.Amount = clamped;
                                    }

                                    var addItemResponse = InventoryUtilitiesServer.TryAddItem(addItemSettings, stashEntry.inventory, itemEntry);

                                    if (!addItemResponse.Success)
                                    {
                                        itemEntry.Amount += capExcess;
                                        continue;
                                    }

                                    transferredItems.Add(item);
                                    var transferredAmount = itemEntry.Amount - addItemResponse.RemainingAmount;
                                    if (amountStashed.TryGetValue((stashEntry.stash, item), out var amount))
                                        amountStashed[(stashEntry.stash, item)] = amount + transferredAmount;
                                    else
                                        amountStashed[(stashEntry.stash, item)] = transferredAmount;

                                    itemEntry.Amount = addItemResponse.RemainingAmount + capExcess;
                                    if (!addItemResponse.ItemsRemaining && capExcess <= 0)
                                    {
                                        if (retainInSlot > 0)
                                        {
                                            var retained = inventoryBuffer[i];
                                            retained.Amount = retainInSlot;
                                            inventoryBuffer[i] = retained;
                                        }
                                        else
                                        {
                                            InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        }
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
                            if (overflowStashes.Count > 0 &&
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
                                        if (retainInSlot > 0)
                                        {
                                            var retained = inventoryBuffer[i];
                                            retained.Amount = retainInSlot;
                                            inventoryBuffer[i] = retained;
                                        }
                                        else
                                        {
                                            InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
                                        }
                                        break;
                                    }
                                }
                                catch (Exception e)
                                {
                                    Core.LogException(e, "Overflow Item Storage");
                                }
                            }
                        }

                        if (itemEntry.Amount > 0 || retainInSlot > 0)
                        {
                            var unstashable = itemEntry.Amount;
                            itemEntry.Amount += retainInSlot;
                            inventoryBuffer[i] = itemEntry;

                            if (unstashable > 0)
                            {
                                if (amountUnstashed.TryGetValue(item, out var amount))
                                    amountUnstashed[item] = amount + unstashable;
                                else
                                    amountUnstashed[item] = unstashable;
                            }
                        }
                    }
                }

                lastStashed[charEntity] = Core.ServerTime;

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