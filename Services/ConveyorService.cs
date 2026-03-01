using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using ProjectM.Shared;
using Stunlock.Core;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace KindredLogistics.Services
{
    // Singleton enumerator that does nothing — avoids heap allocation for disabled callbacks
    sealed class EmptyEnumerator : IEnumerator
    {
        public static readonly EmptyEnumerator Instance = new();
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }

    internal class ConveyorService
    {
        readonly Dictionary<PrefabGUID, int> amountToDistribute = [];
        readonly Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest, int recipeAmount)>> _receivingNeeds = new(32);
        readonly HashSet<PrefabGUID> _alreadyAdded = new(32);
        readonly Dictionary<PrefabGUID, int> _availableInStashes = new(64);
        readonly Dictionary<int, int> _recipeStationCount = new(16);
        readonly Dictionary<int, int> _recipeStationsRemaining = new(16);
        readonly Dictionary<PrefabGUID, int> _ingredientHas = new(8);
        readonly List<(int group, Entity station, Entity inputInv, float matchFloor, int recipeStart, int recipeCount, int invStart, int invCount)> _collectedStations = new(16);
        readonly List<int> _activeRecipeHashes = new(64);
        readonly List<(PrefabGUID itemType, int amount)> _inventorySlots = new(512);
        // Permanent cache: recipe requirements never change at runtime
        readonly Dictionary<int, (PrefabGUID guid, int amount)[]> _cachedRecipeReqs = new(64);
        readonly Dictionary<PrefabGUID, List<(Entity receiver, int amount)>> _unitSpawnerNeeds = new(8);
        readonly Dictionary<PrefabGUID, List<(Entity receiver, int amount)>> _brazierNeeds = new(8);
        static readonly List<Entity> _emptyOverflowList = new();
        // Station metadata cache: survives across runs, invalidated per-territory by dirty tracking
        readonly Dictionary<int, List<(int group, Entity station, Entity inputInv)>> _stationMetaCache = new();
        static readonly HashSet<int> _pendingTerritories = new();
        // Reverse map: inventory sub-entity → territoryId (populated during ProcessConveyors runs)
        static readonly Dictionary<Entity, int> _inventoryToTerritory = new();

        /// <summary>Called by InventoryChangedPatches to mark a territory for immediate processing.</summary>
        internal static void MarkTerritoryPending(int territoryId)
        {
            _pendingTerritories.Add(territoryId);
        }

        /// <summary>Consume pending flag without processing (used when all features are disabled for a territory).</summary>
        internal static void ConsumePending(int territoryId)
        {
            _pendingTerritories.Remove(territoryId);
        }

        /// <summary>Check if a territory is pending processing (used by BrazierService).</summary>
        internal static bool IsTerritoryPending(int territoryId)
        {
            return _pendingTerritories.Contains(territoryId);
        }

        /// <summary>Lookup territory for an inventory sub-entity (External_Inventory, Refinementstation_Inventory).</summary>
        internal static int LookupInventoryTerritory(Entity inventoryEntity)
        {
            return _inventoryToTerritory.TryGetValue(inventoryEntity, out var tid) ? tid : -1;
        }

        /// <summary>Full scan of all territories to populate the reverse map (inventory sub-entity → territoryId).</summary>
        internal static void RefreshReverseMap()
        {
            _inventoryToTerritory.Clear();
            var sgm = Core.ServerGameManager;
            for (int t = TerritoryService.MIN_TERRITORY_ID; t <= TerritoryService.MAX_TERRITORY_ID; t++)
            {
                foreach (var (_, station) in Core.RefinementStations.GetAllReceivingStations(t))
                {
                    if (!Core.EntityManager.Exists(station)) continue;
                    var rs = station.Read<Refinementstation>();
                    var inp = rs.InputInventoryEntity.GetEntityOnServer();
                    if (inp != Entity.Null) _inventoryToTerritory[inp] = t;
                    var outp = rs.OutputInventoryEntity.GetEntityOnServer();
                    if (outp != Entity.Null) _inventoryToTerritory[outp] = t;
                }
                foreach (var (_, station) in Core.RefinementStations.GetAllSendingStations(t))
                {
                    if (!Core.EntityManager.Exists(station)) continue;
                    var rs = station.Read<Refinementstation>();
                    var outp = rs.OutputInventoryEntity.GetEntityOnServer();
                    if (outp != Entity.Null) _inventoryToTerritory[outp] = t;
                }
                void RegisterStashInventories(Entity stash)
                {
                    if (!sgm.TryGetBuffer<AttachedBuffer>(stash, out var abuf)) return;
                    foreach (var att in abuf)
                        if (att.Entity.Has<PrefabGUID>() && att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab))
                            _inventoryToTerritory[att.Entity] = t;
                }
                foreach (var stash in Core.Stash.GetAllOverflowStashes(t)) RegisterStashInventories(stash);
                foreach (var (_, stash) in Core.Stash.GetAllSendingStashes(t)) RegisterStashInventories(stash);
                foreach (var (_, stash) in Core.Stash.GetAllReceivingStashes(t)) RegisterStashInventories(stash);
            }
        }

        /// <summary>Called by InventoryChangedPatches — populates reverse map on first call if still empty.</summary>
        internal static void EnsureReverseMapPopulated()
        {
            if (_inventoryToTerritory.Count > 0) return;
            RefreshReverseMap();
            Core.Log.LogInfo($"[InvChanged] Reverse map populated: {_inventoryToTerritory.Count} inventory entities");
        }

        public ConveyorService()
        {
            // Mark all territories for initial processing on startup
            for (int t = TerritoryService.MIN_TERRITORY_ID; t <= TerritoryService.MAX_TERRITORY_ID; t++)
                _pendingTerritories.Add(t);

            // Second wave after a delay — catches territories whose stations weren't loaded yet
            Core.StartCoroutine(DelayedStartupPending());

            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessConveyors);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessSalvagers);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessUnitSpawners);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessBraziers);
        }

        static IEnumerator DelayedStartupPending()
        {
            for (int i = 0; i < 150; i++)
                yield return null;

            for (int t = TerritoryService.MIN_TERRITORY_ID; t <= TerritoryService.MAX_TERRITORY_ID; t++)
                _pendingTerritories.Add(t);
        }

        IEnumerator ProcessConveyors(int territoryId, Entity castleHeartEntity)
        {
            if (!_pendingTerritories.Remove(territoryId))
                return EmptyEnumerator.Instance;
            if (!Core.PlayerSettings.IsConveyorEnabled(0)) return EmptyEnumerator.Instance;
            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return EmptyEnumerator.Instance;
            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsConveyorEnabled(platformID)) return EmptyEnumerator.Instance;
            return ProcessConveyorsImpl(territoryId, castleHeartEntity);
        }

        IEnumerator ProcessConveyorsImpl(int territoryId, Entity castleHeartEntity)
        {
            var userOwner = castleHeartEntity.Read<UserOwner>();
            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;

            var serverGameManager = Core.ServerGameManager;

            // Rebuild station metadata cache only for THIS territory if it was marked dirty
            if (RefinementStationsService.ConsumeTerritoryDirty(territoryId))
                _stationMetaCache.Remove(territoryId);
            if (!_stationMetaCache.TryGetValue(territoryId, out var stationMeta))
            {
                stationMeta = new(16);
                foreach (var gs in Core.RefinementStations.GetAllReceivingStations(territoryId))
                {
                    var st = gs.station;
                    var refStation = st.Read<Refinementstation>();
                    var inputInv = refStation.InputInventoryEntity.GetEntityOnServer();
                    stationMeta.Add((gs.group, st, inputInv));
                }
                _stationMetaCache[territoryId] = stationMeta;
            }

            // Register station input inventories in reverse map for event-driven lookup
            foreach (var (_, _, inputInv) in stationMeta)
                _inventoryToTerritory[inputInv] = territoryId;
            // Also register output inventories + sending station outputs
            foreach (var (_, sendingStation) in Core.RefinementStations.GetAllSendingStations(territoryId))
            {
                if (Core.EntityManager.Exists(sendingStation))
                {
                    var outInv = sendingStation.Read<Refinementstation>().OutputInventoryEntity.GetEntityOnServer();
                    if (outInv != Entity.Null) _inventoryToTerritory[outInv] = territoryId;
                }
            }
            // Register stash inventory sub-entities
            foreach (var overflowStash in Core.Stash.GetAllOverflowStashes(territoryId))
            {
                if (serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var abuf))
                    foreach (var att in abuf)
                        if (att.Entity.Has<PrefabGUID>() && att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab))
                            _inventoryToTerritory[att.Entity] = territoryId;
            }
            foreach (var (_, sendingStash) in Core.Stash.GetAllSendingStashes(territoryId))
            {
                if (serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var abuf))
                    foreach (var att in abuf)
                        if (att.Entity.Has<PrefabGUID>() && att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab))
                            _inventoryToTerritory[att.Entity] = territoryId;
            }
            foreach (var (_, receivingStash) in Core.Stash.GetAllReceivingStashes(territoryId))
            {
                if (serverGameManager.TryGetBuffer<AttachedBuffer>(receivingStash, out var abuf))
                    foreach (var att in abuf)
                        if (att.Entity.Has<PrefabGUID>() && att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab))
                            _inventoryToTerritory[att.Entity] = territoryId;
            }

            // Pre-scan: only read dynamic data (RecipesBuffer + InventoryBuffer) per station
            _recipeStationCount.Clear();
            _collectedStations.Clear();
            _activeRecipeHashes.Clear();
            _inventorySlots.Clear();
            for (int smi = stationMeta.Count - 1; smi >= 0; smi--)
            {
                var (group, st, inputInv) = stationMeta[smi];
                if (!Core.EntityManager.Exists(st))
                {
                    stationMeta.RemoveAt(smi);
                    continue;
                }
                var matchFloor = st.Read<CastleWorkstation>().WorkstationLevel.HasFlag(WorkstationLevel.MatchingFloor) ? 0.75f : 1f;

                var recipeStart = _activeRecipeHashes.Count;
                var recipesBuffer = st.ReadBuffer<RefinementstationRecipesBuffer>();
                foreach (var r in recipesBuffer)
                {
                    if (!r.Unlocked || r.Disabled) continue;
                    var key = r.RecipeGuid.GuidHash;
                    _activeRecipeHashes.Add(key);
                    // Cache recipe requirements permanently (they never change at runtime)
                    if (!_cachedRecipeReqs.TryGetValue(key, out var cachedReqs))
                    {
                        if (!Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(r.RecipeGuid, out var recipeEntity))
                            continue;
                        var reqs = recipeEntity.ReadBuffer<RecipeRequirementBuffer>();
                        cachedReqs = new (PrefabGUID, int)[reqs.Length];
                        for (int ri = 0; ri < reqs.Length; ri++)
                            cachedReqs[ri] = (reqs[ri].Guid, reqs[ri].Amount);
                        _cachedRecipeReqs[key] = cachedReqs;
                    }
                    if (cachedReqs.Length >= 2)
                    {
                        _recipeStationCount.TryGetValue(key, out var c);
                        _recipeStationCount[key] = c + 1;
                    }
                }

                // Cache inventory contents (avoids IL2CPP InventoryBuffer reads in mainLoop)
                var invStart = _inventorySlots.Count;
                var invBuf = inputInv.ReadBuffer<InventoryBuffer>();
                foreach (var slot in invBuf)
                    if (slot.ItemType.GuidHash != 0 && slot.Amount > 0)
                        _inventorySlots.Add((slot.ItemType, slot.Amount));
                var invCount = _inventorySlots.Count - invStart;

                _collectedStations.Add((group, st, inputInv, matchFloor, recipeStart, _activeRecipeHashes.Count - recipeStart, invStart, invCount));
            }

            var hasMultiIngredient = _recipeStationCount.Count > 0;

            // Only scan stash inventories if there are multi-ingredient recipes on this territory
            _availableInStashes.Clear();
            _recipeStationsRemaining.Clear();
            if (hasMultiIngredient)
            {
                foreach (var overflowStash in Core.Stash.GetAllOverflowStashes(territoryId))
                {
                    if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buf)) continue;
                    foreach (var att in buf)
                    {
                        if (!att.Entity.Has<PrefabGUID>()) continue;
                        if (!att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                        foreach (var slot in att.Entity.ReadBuffer<InventoryBuffer>())
                        {
                            if (slot.ItemType.GuidHash != 0 && slot.Amount > 0)
                            {
                                _availableInStashes.TryGetValue(slot.ItemType, out var cur);
                                _availableInStashes[slot.ItemType] = cur + slot.Amount;
                            }
                        }
                    }
                }
                foreach (var (_, sendingStash) in Core.Stash.GetAllSendingStashes(territoryId))
                {
                    if (!serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var buf)) continue;
                    foreach (var att in buf)
                    {
                        if (!att.Entity.Has<PrefabGUID>()) continue;
                        if (!att.Entity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                        foreach (var slot in att.Entity.ReadBuffer<InventoryBuffer>())
                        {
                            if (slot.ItemType.GuidHash != 0 && slot.Amount > 0)
                            {
                                _availableInStashes.TryGetValue(slot.ItemType, out var cur);
                                _availableInStashes[slot.ItemType] = cur + slot.Amount;
                            }
                        }
                    }
                }

                // Initialize per-recipe station counters for fair distribution
                foreach (var (hash, count) in _recipeStationCount)
                    _recipeStationsRemaining[hash] = count;
            }

            // Determine what is needed for each station (ZERO ECS reads — all from cached data)
            _receivingNeeds.Clear();
            for (int si = 0; si < _collectedStations.Count; si++)
            {
                var (group, station, inputInventoryEntity, matchFloorReduction, recipeStart, recipeCount, invStart, invCount) = _collectedStations[si];

                // Build ingredient map ONCE per station from cached inventory (pure managed)
                _ingredientHas.Clear();
                for (int ii = invStart; ii < invStart + invCount; ii++)
                {
                    var (itemType, amount) = _inventorySlots[ii];
                    _ingredientHas.TryGetValue(itemType, out var cur);
                    _ingredientHas[itemType] = cur + amount;
                }

                for (int rci = 0; rci < recipeCount; rci++)
                {
                    var recipeHash = _activeRecipeHashes[recipeStart + rci];
                    if (!_cachedRecipeReqs.TryGetValue(recipeHash, out var requirements)) continue;
                    var reqCount = requirements.Length;

                    // For multi-ingredient recipes, compute balanced allocation using
                    // per-ingredient remaining pool + machine contents
                    int maxCrafts = 50; // default buffer for single-ingredient recipes
                    if (reqCount >= 2)
                    {
                        _recipeStationsRemaining.TryGetValue(recipeHash, out var stationsRemaining);
                        if (stationsRemaining < 1) stationsRemaining = 1;

                        // Uncapped maxCrafts: machine contents + remaining stash ingredients
                        maxCrafts = int.MaxValue;
                        for (int ri = 0; ri < reqCount; ri++)
                        {
                            var perCraft = Mathf.RoundToInt(requirements[ri].amount * matchFloorReduction);
                            if (perCraft <= 0) continue;
                            _ingredientHas.TryGetValue(requirements[ri].guid, out var has);
                            _availableInStashes.TryGetValue(requirements[ri].guid, out var remaining);
                            var crafts = (has + remaining) / perCraft;
                            if (crafts < maxCrafts) maxCrafts = crafts;
                        }
                        if (maxCrafts == int.MaxValue) maxCrafts = 0;

                        // Fair cap: each station gets at most ceil(uncapped / stationsRemaining)
                        var fairCap = (maxCrafts + stationsRemaining - 1) / stationsRemaining;
                        maxCrafts = fairCap;
                        if (maxCrafts > 50) maxCrafts = 50;

                        // Deduct from per-ingredient remaining pool
                        for (int ri = 0; ri < reqCount; ri++)
                        {
                            var perCraft = Mathf.RoundToInt(requirements[ri].amount * matchFloorReduction);
                            if (perCraft <= 0) continue;
                            _ingredientHas.TryGetValue(requirements[ri].guid, out var has);
                            var pull = System.Math.Max(0, maxCrafts * perCraft - has);
                            if (pull > 0)
                            {
                                _availableInStashes.TryGetValue(requirements[ri].guid, out var remaining);
                                _availableInStashes[requirements[ri].guid] = System.Math.Max(0, remaining - pull);
                            }
                        }

                        _recipeStationsRemaining[recipeHash] = stationsRemaining - 1;

                        // If no crafts possible, skip this recipe
                        if (maxCrafts <= 0) continue;
                    }

                    for (int ri = 0; ri < reqCount; ri++)
                    {
                        var singleCraftAmount = Mathf.RoundToInt(requirements[ri].amount * matchFloorReduction);

                        // All recipes use _ingredientHas (built once per station above)
                        _ingredientHas.TryGetValue(requirements[ri].guid, out var has);

                        var amountWanted = maxCrafts * singleCraftAmount - has;

                        if (amountWanted <= 0) continue;

                        if (!_receivingNeeds.TryGetValue((group, requirements[ri].guid), out var needs))
                        {
                            needs = [];
                            _receivingNeeds[(group, requirements[ri].guid)] = needs;
                        }

                        // Don't add duplicate entries for the same station - take MAX demand
                        // (multiple recipes at the same station may share ingredients)
                        bool alreadyRegistered = false;
                        for (int ni = 0; ni < needs.Count; ni++)
                        {
                            if (needs[ni].receiver == inputInventoryEntity)
                            {
                                if (amountWanted > needs[ni].amount)
                                    needs[ni] = (inputInventoryEntity, amountWanted, false, singleCraftAmount);
                                alreadyRegistered = true;
                                break;
                            }
                        }
                        if (!alreadyRegistered)
                        {
                            needs.Add((inputInventoryEntity, amountWanted, false, singleCraftAmount));
                        }
                    }
                }
            }

            // Determine what is desired by each receiving stash
            _alreadyAdded.Clear();
            foreach (var (group, stash) in Core.Stash.GetAllReceivingStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                    continue;

                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    _alreadyAdded.Clear();
                    var inventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();

                    foreach (var item in inventoryBuffer)
                    {
                        if (item.ItemType.GuidHash == 0) continue;

                        if (_alreadyAdded.Contains(item.ItemType)) continue;
                        _alreadyAdded.Add(item.ItemType);

                        if (!_receivingNeeds.TryGetValue((group, item.ItemType), out var needs))
                        {
                            needs = [];
                            _receivingNeeds[(group, item.ItemType)] = needs;
                        }

                        needs.Add((attachedEntity, -1, true, 0));
                    }
                }
            }

            if (_receivingNeeds.Count == 0) yield break;

            Dictionary<PrefabGUID, List<List<(Entity receiver, int amount, bool chest, int recipeAmount)>>> ungroupedItemLookup = null;
            // First distribute from overflow stashes
            var overflowStashes = Core.Stash.GetAllOverflowStashes(territoryId);
            foreach (var overflowStash in overflowStashes)
            {
                if (!Core.EntityManager.Exists(overflowStash)) continue;
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                    DistributeInventoryFromOverflow(_receivingNeeds, serverGameManager, attachedEntity, ref ungroupedItemLookup);
                }
                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

            // Now distribute from all the sender stations to the stations in need
            foreach (var (group, sendingStation) in Core.RefinementStations.GetAllSendingStations(territoryId))
            {
                if (!Core.EntityManager.Exists(sendingStation)) continue;

                var refinementStation = sendingStation.Read<Refinementstation>();
                var outputInventoryEntity = refinementStation.OutputInventoryEntity.GetEntityOnServer();
                if (outputInventoryEntity.Equals(Entity.Null)) continue;
                DistributeInventory(_receivingNeeds, serverGameManager, group, outputInventoryEntity, overflowStashes);

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

            // Next distribute from all the send stashes
            var dplRetain = Core.PlayerSettings.IsDontPullLastEnabled(platformID) ? 1 : 0;
            foreach (var (group, sendingStash) in Core.Stash.GetAllSendingStashes(territoryId))
            {
                if (!Core.EntityManager.Exists(sendingStash)) continue;
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var buffer))
                    continue;

                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    DistributeInventory(_receivingNeeds, serverGameManager, group, attachedEntity, _emptyOverflowList, retain: dplRetain, chest: true);
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

        }

        readonly HashSet<Entity> salvagerFull = [];
        readonly HashSet<(Entity entity, PrefabGUID item)> salvagerFullOfItem = [];
        readonly List<(Entity entity, Salvagestation station, int index)> _salvagers = new(16);
        readonly Dictionary<PrefabGUID, (bool itemEntity, int amount)> _itemAmountsToTransfer = new(32);

        IEnumerator ProcessSalvagers(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsSalvageEnabled(0)) return EmptyEnumerator.Instance;
            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return EmptyEnumerator.Instance;
            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsSalvageEnabled(platformID)) return EmptyEnumerator.Instance;
            return ProcessSalvagersImpl(territoryId, castleHeartEntity);
        }

        IEnumerator ProcessSalvagersImpl(int territoryId, Entity castleHeartEntity)
        {
            _salvagers.Clear();
            var idx = 0;
            foreach (var s in Core.SalvageService.GetAllSalvageStations(territoryId))
                _salvagers.Add((s, s.Read<Salvagestation>(), idx++));

            // Empty all salvage outputs first
            var itemStashes = Utilities.GetItemStashesOnTerritory(territoryId);
            var overflows = Core.Stash.GetAllOverflowStashes(territoryId);
            foreach (var salvager in _salvagers)
            {
                if (!Core.EntityManager.Exists(salvager.entity)) continue;

                var salvageStation = salvager.station;
                var outputInventoryEntity = salvageStation.OutputInventoryEntity.GetEntityOnServer();
                if (outputInventoryEntity == Entity.Null) continue;

                var inventoryBuffer = Core.EntityManager.GetBuffer<InventoryBuffer>(outputInventoryEntity).ToNativeArray(Allocator.Temp);
                try
                {
                    if (InventoryUtilities.IsInventoryEmpty(inventoryBuffer)) continue;

                    Utilities.StashInventoryEntity(outputInventoryEntity, itemStashes, overflows);
                }
                finally
                {
                    inventoryBuffer.Dispose();
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

            // Now fill all the salvagers
            salvagerFull.Clear();
            salvagerFullOfItem.Clear();
            foreach (var salvageSupplier in Core.Stash.GetAllSalvageStashes(territoryId))
            {
                if (!Core.ServerGameManager.TryGetBuffer<AttachedBuffer>(salvageSupplier, out var buffer))
                    continue;

                var isReceiverStash = Core.Stash.IsClassifiedAsReceiver(territoryId, salvageSupplier);
                foreach (var attachedBuffer in buffer)
                {
                    var salvageSupplierInventory = attachedBuffer.Entity;
                    if (!salvageSupplierInventory.Has<PrefabGUID>()) continue;
                    if (!salvageSupplierInventory.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    _itemAmountsToTransfer.Clear();
                    var inventoryBuffer = salvageSupplierInventory.ReadBuffer<InventoryBuffer>();
                    foreach (var item in inventoryBuffer)
                    {
                        Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(item.ItemType, out var prefabEntity);
                        if (!prefabEntity.Has<Salvageable>()) continue;

                        var amount = 0;
                        if (_itemAmountsToTransfer.TryGetValue(item.ItemType, out var entry))
                        {
                            amount = entry.amount;
                        }
                        else if (isReceiverStash)
                        {
                            amount = -1;
                        }
                        amount += item.Amount;
                        _itemAmountsToTransfer[item.ItemType] = (!item.ItemEntity.Equals(NetworkedEntity.Empty), amount);
                    }

                    foreach(var (itemType, entry) in _itemAmountsToTransfer)
                    {
                        var totalAmountToTransfer = entry.amount;
                        if (totalAmountToTransfer <= 0) continue;
                        var leftToGetTrash = _salvagers.Count - salvagerFull.Count;
                        if (leftToGetTrash == 0) break;
                        for (var i = _salvagers.Count - 1; i >= 0; i--)
                        {
                            if (Core.TerritoryService.ShouldUpdateYield())
                                yield return null;

                            if (!Core.EntityManager.Exists(salvageSupplierInventory)) break;

                            var salvager = _salvagers[i];
                            if (salvagerFull.Contains(salvager.entity)) continue;

                            var salvagerKey = (salvager.entity, itemType);
                            if (salvagerFullOfItem.Contains(salvagerKey))
                            {
                                leftToGetTrash--;
                                continue;
                            }
                            if (!Core.EntityManager.Exists(salvager.entity))
                            {
                                leftToGetTrash--;
                                continue;
                            }

                            var salvageStation = salvager.station;
                            var inputInventoryEntity = salvageStation.InputInventoryEntity.GetEntityOnServer();
                            if (inputInventoryEntity == Entity.Null)
                            {
                                leftToGetTrash--;
                                continue;
                            }

                            var startInputSlot = 0;
                            var amountTransferred = 0;

                            // Ensure non working ones get at least one otherwise distribute somewhat randomly based on current frame
                            var amountToTransfer = (totalAmountToTransfer + (!salvageStation.IsWorking ? (leftToGetTrash - 1) : Time.frameCount % leftToGetTrash)) / leftToGetTrash;
                            if (amountToTransfer == 0) continue;

                            if (entry.itemEntity)
                                Utilities.TransferItemEntities(salvageSupplierInventory, inputInventoryEntity, itemType, amountToTransfer, ref startInputSlot, out amountTransferred);
                            else
                                amountTransferred = Utilities.TransferItems(Core.ServerGameManager, salvageSupplierInventory, inputInventoryEntity, itemType, amountToTransfer);
                            leftToGetTrash--;

                            if (amountTransferred < amountToTransfer)
                            {
                                if (Core.ServerGameManager.HasFullInventory(inputInventoryEntity))
                                {
                                    _salvagers.RemoveAt(i);
                                }
                                else
                                {
                                    salvagerFullOfItem.Add(salvagerKey);
                                }
                            }

                            if (amountTransferred == 0)
                            {
                                continue;
                            }

                            totalAmountToTransfer -= amountTransferred;

                            if (!salvageStation.IsWorking)
                            {
                                salvageStation.IsWorking = true;
                                salvager.entity.Write(salvageStation);
                                _salvagers[salvager.index] = (salvager.entity, salvageStation, salvager.index);
                            }

                            if (totalAmountToTransfer <= 0) break;
                        }
                    }
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }
        }

        IEnumerator ProcessUnitSpawners(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(0)) return EmptyEnumerator.Instance;
            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return EmptyEnumerator.Instance;
            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(platformID)) return EmptyEnumerator.Instance;
            return ProcessUnitSpawnersImpl(territoryId, castleHeartEntity);
        }

        IEnumerator ProcessUnitSpawnersImpl(int territoryId, Entity castleHeartEntity)
        {

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each unit spawner
            _unitSpawnerNeeds.Clear();
            var receivingNeeds = _unitSpawnerNeeds;
            foreach (var station in Core.UnitSpawnerstationService.GetAllUnitSpawners(territoryId))
            {
                var castleWorkstation = station.Read<CastleWorkstation>();
                var matchFloorReduction = castleWorkstation.WorkstationLevel.HasFlag(WorkstationLevel.MatchingFloor) ? 0.75f : 1f;
                var inputInventoryEntity = Entity.Null;
                DynamicBuffer<InventoryBuffer> inventoryBuffer = new();
                var recipesBuffer = station.ReadBuffer<RefinementstationRecipesBuffer>();
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(station, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    inputInventoryEntity = attachedEntity;
                    inventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                }

                if (inputInventoryEntity == Entity.Null) continue;

                foreach (var recipe in recipesBuffer)
                {
                    if (!recipe.Unlocked) continue;
                    if (recipe.Disabled) continue;

                    if (!Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(recipe.RecipeGuid, out var recipeEntity))
                        continue;
                    var requirements = recipeEntity.ReadBuffer<RecipeRequirementBuffer>();
                    foreach (var requirement in requirements)
                    {
                        // Always desire 2x the transferring so the moment it finishes it immediately starts again
                        var amountWanted = 2 * Mathf.RoundToInt(requirement.Amount * matchFloorReduction);

                        // Check how much is already in the inventory
                        int has = 0;
                        foreach (var item in inventoryBuffer)
                        {
                            if (item.ItemType.Equals(requirement.Guid))
                            {
                                amountWanted -= item.Amount;
                                has = item.Amount;
                            }
                        }

                        if (amountWanted <= 0) continue;

                        if (!receivingNeeds.TryGetValue(requirement.Guid, out var needs))
                        {
                            needs = [];
                            receivingNeeds[requirement.Guid] = needs;
                        }

                        needs.Add((inputInventoryEntity, amountWanted));
                    }
                }
            }

            if (receivingNeeds.Count == 0) yield break;

            // Distribute from all the spawner stashes
            foreach (var sendingStash in Core.Stash.GetAllSpawnerStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    DistributeInventory(receivingNeeds, serverGameManager, attachedEntity, retain: 1);
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }
        }

        IEnumerator ProcessBraziers(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsBrazierEnabled(0)) return EmptyEnumerator.Instance;
            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return EmptyEnumerator.Instance;
            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsBrazierEnabled(platformID)) return EmptyEnumerator.Instance;
            return ProcessBraziersImpl(territoryId, castleHeartEntity);
        }

        IEnumerator ProcessBraziersImpl(int territoryId, Entity castleHeartEntity)
        {
            const int minAmount = 10;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each brazier
            _brazierNeeds.Clear();
            var receivingNeeds = _brazierNeeds;
            foreach (var brazier in Core.BrazierService.GetAllBraziers(territoryId))
            {
                var burnContainer = brazier.Read<BurnContainer>();
                if (!burnContainer.Enabled) continue;

                var inputInventoryEntity = Entity.Null;
                DynamicBuffer<InventoryBuffer> inventoryBuffer = new();
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(brazier, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    inputInventoryEntity = attachedEntity;
                    inventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                }

                if (inputInventoryEntity == Entity.Null) continue;

                // Check how much is already in the inventory
                var bonfire = brazier.Read<Bonfire>();
                var has = 0;
                foreach (var item in inventoryBuffer)
                {
                    if (item.ItemType.Equals(bonfire.InputItem))
                    {
                        has += item.Amount;
                    }
                }

                if (has > minAmount) continue;

                if (!receivingNeeds.TryGetValue(bonfire.InputItem, out var needs))
                {
                    needs = [];
                    receivingNeeds[bonfire.InputItem] = needs;
                }

                needs.Add((inputInventoryEntity, minAmount - has));
            }

            if (receivingNeeds.Count == 0) yield break;

            // Distribute from all the spawner stashes
            foreach (var sendingStash in Core.Stash.GetAllBrazierStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    DistributeInventory(receivingNeeds, serverGameManager, attachedEntity, retain: 1);
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }
        }

        void DistributeInventoryFromOverflow(Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest, int recipeAmount)>> receivingNeeds,
                                 ServerGameManager serverGameManager, Entity inventoryEntity,
                                 ref Dictionary<PrefabGUID, List<List<(Entity receiver, int amount, bool chest, int recipeAmount)>>> ungroupedItemLookup)
        {
            amountToDistribute.Clear();

            var anythingToDistribute = false;
            var inventoryBuffer = inventoryEntity.ReadBuffer<InventoryBuffer>();
            foreach (var item in inventoryBuffer)
            {
                if (item.ItemType.GuidHash == 0) continue;
                if (!item.ItemEntity.Equals(NetworkedEntity.Empty)) continue;

                if (!amountToDistribute.TryGetValue(item.ItemType, out var totalAmountDistribute))
                    totalAmountDistribute = item.Amount;
                else
                    totalAmountDistribute += item.Amount;
                amountToDistribute[item.ItemType] = totalAmountDistribute;
                anythingToDistribute = true;
            }

            if (!anythingToDistribute) return;

            if (ungroupedItemLookup == null)
            {
                ungroupedItemLookup = new();
                foreach (var ((group, item), needs) in receivingNeeds)
                {
                    if (!ungroupedItemLookup.TryGetValue(item, out var list))
                    {
                        list = [];
                        ungroupedItemLookup[item] = list;
                    }
                    list.Add(needs);
                }
            }

            foreach ((var item, var totalAmount) in amountToDistribute)
            {
                // Does anyone need this item?
                if (!ungroupedItemLookup.TryGetValue(item, out var needs)) continue;

                // Calculate totalWanted without LINQ
                var totalWanted = 0;
                for (var li = 0; li < needs.Count; li++)
                    for (var ei = 0; ei < needs[li].Count; ei++)
                        if (needs[li][ei].amount > 0)
                            totalWanted += needs[li][ei].amount;

                // If we have more than enough, distribute evenly
                if (totalWanted <= totalAmount)
                {
                    var leftoverAmount = totalAmount - totalWanted;
                    // Iterate in reverse to safely RemoveAt
                    for (var li = needs.Count - 1; li >= 0; li--)
                    {
                        for (var ei = needs[li].Count - 1; ei >= 0; ei--)
                        {
                            var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[li][ei];

                            if (!Core.EntityManager.Exists(receivingInventoryEntity))
                            {
                                needs[li].RemoveAt(ei);
                                continue;
                            }

                            if (wanted > 0)
                            {
                                Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, wanted);
                                needs[li].RemoveAt(ei);
                            }
                            else
                            {
                                var amountActuallyGiven = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, leftoverAmount);

                                if (amountActuallyGiven < leftoverAmount)
                                {
                                    needs[li].RemoveAt(ei);
                                }

                                leftoverAmount -= amountActuallyGiven;
                            }
                        }
                    }
                }
                else
                {
                    var remainder = 0;
                    // Give out proportionally - iterate in reverse to safely RemoveAt
                    // Pass 1: proportional with recipe rounding
                    var totalTransferred = 0;
                    for (var li = needs.Count - 1; li >= 0; li--)
                    {
                        for (var ei = needs[li].Count - 1; ei >= 0; ei--)
                        {
                            var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[li][ei];
                            if (wanted <= 0) continue;

                            if (!Core.EntityManager.Exists(receivingInventoryEntity))
                            {
                                totalWanted -= wanted;
                                needs[li].RemoveAt(ei);
                                continue;
                            }

                            var numerator = (long)wanted * totalAmount;
                            var transferring = (int)(numerator / totalWanted);
                            remainder += (int)(numerator % totalWanted);
                            if (remainder >= totalWanted && transferring < wanted)
                            {
                                transferring++;
                                remainder -= totalWanted;
                            }
                            if (recipeAmount > 0 && transferring < wanted)
                                transferring = (transferring / recipeAmount) * recipeAmount;
                            if (transferring <= 0) continue;
                            var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                            totalTransferred += transferred;
                            if (transferred < transferring)
                            {
                                needs[li].RemoveAt(ei);
                            }
                            else if (transferred >= wanted)
                            {
                                needs[li].RemoveAt(ei);
                            }
                            else
                            {
                                needs[li][ei] = (receivingInventoryEntity, wanted - transferred, receiverChest, recipeAmount);
                            }
                        }
                    }

                    // Pass 2: remaining to stations in recipe multiples (fair sequential)
                    var overflowRemaining = totalAmount - totalTransferred;
                    if (overflowRemaining > 0)
                    {
                        var stationsLeft = 0;
                        for (var li = 0; li < needs.Count; li++)
                            for (var ei = 0; ei < needs[li].Count; ei++)
                                if (needs[li][ei].recipeAmount > 0 && needs[li][ei].amount > 0)
                                    stationsLeft++;

                        for (var li = needs.Count - 1; li >= 0 && overflowRemaining > 0 && stationsLeft > 0; li--)
                        {
                            for (var ei = needs[li].Count - 1; ei >= 0 && overflowRemaining > 0 && stationsLeft > 0; ei--)
                            {
                                var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[li][ei];
                                if (recipeAmount <= 0 || wanted <= 0) continue;
                                if (overflowRemaining < recipeAmount) { stationsLeft--; continue; }
                                if (!Core.EntityManager.Exists(receivingInventoryEntity))
                                {
                                    needs[li].RemoveAt(ei);
                                    stationsLeft--;
                                    continue;
                                }

                                var fairCap = (overflowRemaining / stationsLeft / recipeAmount) * recipeAmount;
                                if (fairCap < recipeAmount) fairCap = recipeAmount;
                                var transferring = System.Math.Min(wanted, System.Math.Min(overflowRemaining, fairCap));
                                transferring = (transferring / recipeAmount) * recipeAmount;
                                if (transferring <= 0) { stationsLeft--; continue; }

                                var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                                overflowRemaining -= transferred;
                                stationsLeft--;
                                if (transferred >= wanted)
                                    needs[li].RemoveAt(ei);
                                else
                                    needs[li][ei] = (receivingInventoryEntity, wanted - transferred, receiverChest, recipeAmount);
                            }
                        }
                    }
                }
            }
        }

        void DistributeInventory(Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest, int recipeAmount)>> receivingNeeds,
                                 ServerGameManager serverGameManager, int group, Entity inventoryEntity, List<Entity> overflowStashes, int retain = 0, bool chest=false)
        {
            amountToDistribute.Clear();

            var inventoryBuffer = inventoryEntity.ReadBuffer<InventoryBuffer>();
            foreach (var item in inventoryBuffer)
            {
                if (item.ItemType.GuidHash == 0) continue;
                if (!item.ItemEntity.Equals(NetworkedEntity.Empty)) continue;

                if (!amountToDistribute.TryGetValue(item.ItemType, out var totalAmountDistribute))
                    totalAmountDistribute = item.Amount - retain;
                else
                    totalAmountDistribute += item.Amount;
                amountToDistribute[item.ItemType] = totalAmountDistribute;
            }

            foreach ((var item, var totalAmount) in amountToDistribute)
            {
                // Does anyone need this item?
                if (!receivingNeeds.TryGetValue((group, item), out var needs))
                {
                    if (chest) continue;

                    var totalForOverflow = totalAmount;
                    foreach (var overflowStash in overflowStashes)
                    {
                        if (!Core.EntityManager.Exists(overflowStash)) continue;
                        if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buffer))
                            continue;
                        foreach (var attachedBuffer in buffer)
                        {
                            var attachedEntity = attachedBuffer.Entity;
                            if (!attachedEntity.Has<PrefabGUID>()) continue;
                            if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                            var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, attachedEntity, item, totalForOverflow);
                            totalForOverflow -= transferred;
                            if (totalForOverflow <= 0) break;
                        }
                        if (totalForOverflow <= 0) break;
                    }
                    continue;
                }
                
                var totalWanted = 0;
                for (var ni = 0; ni < needs.Count; ni++)
                    if (needs[ni].amount > 0 && (!chest || !needs[ni].chest))
                        totalWanted += needs[ni].amount;

                // If we have more than enough, distribute evenly
                if (totalWanted <= totalAmount)
                {
                    var leftoverAmount = 0;

                    // Only handling leftovers if not a chest
                    if (!chest)
                    {
                        leftoverAmount = totalAmount - totalWanted;
                    }

                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[i];

                        if (chest && receiverChest) continue;

                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            needs.RemoveAt(i);
                            continue;
                        }

                        if (wanted > 0)
                        {
                            Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, wanted);
                            needs.RemoveAt(i);
                        }
                        else if (!chest && leftoverAmount > 0)
                        {
                            var amountActuallyGiven = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, leftoverAmount);

                            if (amountActuallyGiven < leftoverAmount)
                            {
                                needs.RemoveAt(i);
                            }
                            leftoverAmount -= amountActuallyGiven;
                        }
                    }


                    // Distribute any remaining leftovers to overflow stashes
                    if (leftoverAmount > 0)
                    {
                        foreach (var overflowStash in overflowStashes)
                        {
                            if (!Core.EntityManager.Exists(overflowStash)) continue;
                            if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buffer))
                                continue;
                            foreach (var attachedBuffer in buffer)
                            {
                                var attachedEntity = attachedBuffer.Entity;
                                if (!attachedEntity.Has<PrefabGUID>()) continue;
                                if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                                var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, attachedEntity, item, leftoverAmount);
                                leftoverAmount -= transferred;
                                if (leftoverAmount <= 0) break;
                            }
                            if (leftoverAmount <= 0) break;
                        }
                    }
                }
                else
                {
                    var totalTransferred = 0;
                    var remainder = 0;
                    // Pass 1: Distribute proportionally with recipe-multiple rounding
                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[i];

                        if (chest && receiverChest) continue;
                        if (wanted <= 0) continue;

                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            totalWanted -= wanted;
                            needs.RemoveAt(i);
                            continue;
                        }

                        var numerator = (long)wanted * totalAmount;
                        var transferring = (int)(numerator / totalWanted);
                        remainder += (int)(numerator % totalWanted);
                        if (remainder >= totalWanted && transferring < wanted)
                        {
                            transferring++;
                            remainder -= totalWanted;
                        }
                        // Round down to recipe multiples for stations
                        if (recipeAmount > 0 && transferring < wanted)
                            transferring = (transferring / recipeAmount) * recipeAmount;
                        if (transferring <= 0) continue; // handled in pass 2
                        var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                        totalTransferred += transferred;
                        if (transferred < transferring)
                        {
                            needs.RemoveAt(i);
                        }
                        else if (transferred >= wanted)
                        {
                            needs.RemoveAt(i);
                        }
                        else
                        {
                            needs[i] = (receivingInventoryEntity, wanted - transferred, receiverChest, recipeAmount);
                        }
                    }

                    // Pass 2: Distribute remaining to stations in recipe multiples (fair sequential)
                    var remaining = totalAmount - totalTransferred;
                    if (remaining > 0)
                    {
                        var stationsLeft = 0;
                        for (int i = 0; i < needs.Count; i++)
                        {
                            if (chest && needs[i].chest) continue;
                            if (needs[i].recipeAmount > 0 && needs[i].amount > 0)
                                stationsLeft++;
                        }

                        for (int i = needs.Count - 1; i >= 0 && remaining > 0 && stationsLeft > 0; i--)
                        {
                            var (receivingInventoryEntity, wanted, receiverChest, recipeAmount) = needs[i];
                            if (chest && receiverChest) continue;
                            if (recipeAmount <= 0 || wanted <= 0) continue;
                            if (remaining < recipeAmount) break;
                            if (!Core.EntityManager.Exists(receivingInventoryEntity))
                            {
                                needs.RemoveAt(i);
                                stationsLeft--;
                                continue;
                            }

                            // Fair cap: each station gets at most its fair share, minimum 1 recipe
                            var fairCap = (remaining / stationsLeft / recipeAmount) * recipeAmount;
                            if (fairCap < recipeAmount) fairCap = recipeAmount;
                            var transferring = System.Math.Min(wanted, System.Math.Min(remaining, fairCap));
                            transferring = (transferring / recipeAmount) * recipeAmount;
                            if (transferring <= 0) { stationsLeft--; continue; }

                            var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                            totalTransferred += transferred;
                            remaining -= transferred;
                            stationsLeft--;
                            if (transferred >= wanted)
                                needs.RemoveAt(i);
                            else
                                needs[i] = (receivingInventoryEntity, wanted - transferred, receiverChest, recipeAmount);
                        }
                    }

                    if (totalTransferred < totalAmount && !chest)
                    {
                        var leftoverAmount = totalAmount - totalTransferred;
                        // Distribute any remaining leftovers to overflow stashes
                        foreach (var overflowStash in overflowStashes)
                        {
                            if (!Core.EntityManager.Exists(overflowStash)) continue;
                            if (!serverGameManager.TryGetBuffer<AttachedBuffer>(overflowStash, out var buffer))
                                continue;
                            foreach (var attachedBuffer in buffer)
                            {
                                var attachedEntity = attachedBuffer.Entity;
                                if (!attachedEntity.Has<PrefabGUID>()) continue;
                                if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                                var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, attachedEntity, item, leftoverAmount);
                                leftoverAmount -= transferred;
                                if (leftoverAmount <= 0) break;
                            }
                            if (leftoverAmount <= 0) break;
                        }
                    }
                }
            }
        }

        void DistributeInventory(Dictionary<PrefabGUID, List<(Entity receiver, int amount)>> receivingNeeds,
                                 ServerGameManager serverGameManager, Entity inventoryEntity, int retain = 0)
        {
            amountToDistribute.Clear();

            var inventoryBuffer = inventoryEntity.ReadBuffer<InventoryBuffer>();
            foreach (var item in inventoryBuffer)
            {
                if (item.ItemType.GuidHash == 0) continue;
                if (!item.ItemEntity.Equals(NetworkedEntity.Empty)) continue;

                if (!amountToDistribute.TryGetValue(item.ItemType, out var totalAmountDistribute))
                    totalAmountDistribute = item.Amount - retain;
                else
                    totalAmountDistribute += item.Amount;
                amountToDistribute[item.ItemType] = totalAmountDistribute;
            }

            foreach ((var item, var totalAmount) in amountToDistribute)
            {
                // Does anyone need this item?
                if (!receivingNeeds.TryGetValue(item, out var needs)) continue;

                var totalWanted = 0;
                for (var ni = 0; ni < needs.Count; ni++)
                    totalWanted += needs[ni].amount;
                if (totalWanted <= 0) continue;

                // If we have more than enough, distribute evenly
                if (totalWanted <= totalAmount)
                {

                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted) = needs[i];
                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            needs.RemoveAt(i);
                            continue;
                        }
                        Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, wanted);
                    }
                    needs.Clear();
                }
                else
                {
                    var remainder = 0;
                    // Give out proportionally

                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted) = needs[i];

                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            totalWanted -= wanted;
                            needs.RemoveAt(i);
                            continue;
                        }

                        var numerator = (long)wanted * totalAmount;
                        var transferring = (int)(numerator / totalWanted);
                        remainder += (int)(numerator % totalWanted);
                        if (remainder >= totalWanted)
                        {
                            transferring++;
                            remainder -= totalWanted;
                        }
                        var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                        if (transferred < transferring)
                        {
                            remainder += transferring - transferred;
                            needs.RemoveAt(i);
                        }
                        else if (transferred >= wanted)
                        {
                            needs.RemoveAt(i);
                        }
                        else
                        {
                            needs[i] = (receivingInventoryEntity, wanted - transferred);
                        }
                    }
                }
            }
        }
    }
}
