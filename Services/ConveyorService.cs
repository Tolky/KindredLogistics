using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using ProjectM.Shared;
using Stunlock.Core;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace KindredLogistics.Services
{
    internal class ConveyorService
    {
        readonly Dictionary<PrefabGUID, int> amountToDistribute = [];

        public ConveyorService()
        {
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessConveyors);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessSalvagers);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessUnitSpawners);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessBraziers);
        }

        IEnumerator ProcessConveyors(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsConveyorEnabled(0)) yield break;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) yield break;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsConveyorEnabled(platformID)) yield break;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each station
            var receivingNeeds = new Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest)>>();
            foreach (var (group, station) in Core.RefinementStations.GetAllReceivingStations(territoryId))
            {
                var receivingStation = station.Read<Refinementstation>();
                var castleWorkstation = station.Read<CastleWorkstation>();
                var matchFloorReduction = castleWorkstation.WorkstationLevel.HasFlag(WorkstationLevel.MatchingFloor) ? 0.75f : 1f;
                var inputInventoryEntity = receivingStation.InputInventoryEntity.GetEntityOnServer();
                var inventoryBuffer = inputInventoryEntity.ReadBuffer<InventoryBuffer>();
                var recipesBuffer = station.ReadBuffer<RefinementstationRecipesBuffer>();
                foreach (var recipe in recipesBuffer)
                {
                    if (!recipe.Unlocked) continue;
                    if (recipe.Disabled) continue;

                    Entity recipeEntity = Core.PrefabCollectionSystem._PrefabGuidToEntityMap[recipe.RecipeGuid];
                    var requirements = recipeEntity.ReadBuffer<RecipeRequirementBuffer>();
                    foreach (var requirement in requirements)
                    {
                        // Always desire 5x the transferring so the moment it finishes it immediately starts again
                        var amountWanted = 5 * Mathf.RoundToInt(requirement.Amount * matchFloorReduction);

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

                        if (!receivingNeeds.TryGetValue((group, requirement.Guid), out var needs))
                        {
                            needs = [];
                            receivingNeeds[(group, requirement.Guid)] = needs;
                        }

                        needs.Add((inputInventoryEntity, amountWanted, false));
                    }
                }
            }

            // Determine what is desired by each receiving stash
            var alreadyAdded = new HashSet<PrefabGUID>();
            foreach (var (group, stash) in Core.Stash.GetAllReceivingStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;
                    if (Core.ServerGameManager.HasFullInventory(attachedEntity)) continue;

                    alreadyAdded.Clear();
                    var inventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                    foreach (var item in inventoryBuffer)
                    {
                        if (item.ItemType.GuidHash == 0) continue;

                        if (alreadyAdded.Contains(item.ItemType)) continue;
                        alreadyAdded.Add(item.ItemType);

                        if (!receivingNeeds.TryGetValue((group, item.ItemType), out var needs))
                        {
                            needs = [];
                            receivingNeeds[(group, item.ItemType)] = needs;
                        }

                        needs.Add((attachedEntity, -1, true));
                    }
                }
            }

            if (receivingNeeds.Count == 0) yield break;

            Dictionary<PrefabGUID, List<List<(Entity receiver, int amount, bool chest)>>> ungroupedItemLookup = null;
            // First distribute from overflow stashes
            var overflowStashes = Core.Stash.GetAllOverflowStashes(territoryId).ToArray();
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
                    DistributeInventoryFromOverflow(receivingNeeds, serverGameManager, attachedEntity, ref ungroupedItemLookup);
                }
                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

            // Now distribute from all the sender stations to the stations in need
            foreach (var (group, sendingStation) in Core.RefinementStations.GetAllSendingStations(territoryId).ToArray())
            {
                if (!Core.EntityManager.Exists(sendingStation)) continue;

                var refinementStation = sendingStation.Read<Refinementstation>();
                var outputInventoryEntity = refinementStation.OutputInventoryEntity.GetEntityOnServer();
                if (outputInventoryEntity.Equals(Entity.Null)) continue;
                DistributeInventory(receivingNeeds, serverGameManager, group, outputInventoryEntity, overflowStashes);

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }

            // Next distribute from all the send stashes
            var emptyArray = System.Array.Empty<Entity>();
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

                    DistributeInventory(receivingNeeds, serverGameManager, group, attachedEntity, emptyArray, retain: 1, chest: true);
                }

                if (Core.TerritoryService.ShouldUpdateYield())
                    yield return null;
            }
        }

        readonly HashSet<Entity> salvagerFull = [];
        readonly HashSet<(Entity entity, PrefabGUID item)> salvagerFullOfItem = [];

        IEnumerator ProcessSalvagers(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsSalvageEnabled(0)) yield break;

            var salvagers = Core.SalvageService.GetAllSalvageStations(territoryId)
                            .Select((s, i) => (entity: s, station: s.Read<Salvagestation>(), index: i))
                            .ToList();

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) yield break;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsSalvageEnabled(platformID)) yield break;

            // Empty all salvage outputs first
            var itemStashes = Utilities.GetItemStashesOnTerritory(territoryId);
            var overflows = Core.Stash.GetAllOverflowStashes(territoryId).ToList();
            foreach (var salvager in salvagers)
            {
                if (!Core.EntityManager.Exists(salvager.entity)) continue;
                
                var salvageStation = salvager.station;
                var outputInventoryEntity = salvageStation.OutputInventoryEntity.GetEntityOnServer();

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
            var itemAmountsToTransfer = new Dictionary<PrefabGUID, (bool itemEntity, int amount)>();
            foreach (var salvageSupplier in Core.Stash.GetAllSalvageStashes(territoryId))
            {
                if (!Core.ServerGameManager.TryGetBuffer<AttachedBuffer>(salvageSupplier, out var buffer))
                    continue;

                var name = salvageSupplier.Read<NameableInteractable>().Name.ToString().ToLower();
                var isReceiverStash = Core.Stash.ReceiverRegex.IsMatch(name);
                foreach (var attachedBuffer in buffer)
                {
                    var salvageSupplierInventory = attachedBuffer.Entity;
                    if (!salvageSupplierInventory.Has<PrefabGUID>()) continue;
                    if (!salvageSupplierInventory.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    itemAmountsToTransfer.Clear();
                    var inventoryBuffer = salvageSupplierInventory.ReadBuffer<InventoryBuffer>();
                    foreach (var item in inventoryBuffer)
                    {
                        Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(item.ItemType, out var prefabEntity);
                        if (!prefabEntity.Has<Salvageable>()) continue;

                        var amount = 0;
                        if (itemAmountsToTransfer.TryGetValue(item.ItemType, out var entry))
                        {
                            amount = entry.amount;
                        }
                        else if (isReceiverStash)
                        {
                            amount = -1;
                        }
                        amount += item.Amount;
                        itemAmountsToTransfer[item.ItemType] = (!item.ItemEntity.Equals(NetworkedEntity.Empty), amount);
                    }

                    foreach(var (itemType, entry) in itemAmountsToTransfer)
                    { 
                        var totalAmountToTransfer = entry.amount;
                        if (totalAmountToTransfer <= 0) continue;
                        var leftToGetTrash = salvagers.Count - salvagerFull.Count;
                        if (leftToGetTrash == 0) break;
                        for (var i = salvagers.Count - 1; i >= 0; i--)
                        {
                            if (Core.TerritoryService.ShouldUpdateYield())
                                yield return null;

                            if (!Core.EntityManager.Exists(salvageSupplierInventory)) break;

                            var salvager = salvagers[i];
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
                                    salvagers.RemoveAt(i);
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
                                salvagers[salvager.index] = (salvager.entity, salvageStation, salvager.index);
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
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(0)) yield break;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) yield break;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(platformID)) yield break;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each brazier
            var receivingNeeds = new Dictionary<PrefabGUID, List<(Entity, int)>>();
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

                foreach (var recipe in recipesBuffer)
                {
                    if (!recipe.Unlocked) continue;
                    if (recipe.Disabled) continue;

                    Entity recipeEntity = Core.PrefabCollectionSystem._PrefabGuidToEntityMap[recipe.RecipeGuid];
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
            if (!Core.PlayerSettings.IsBrazierEnabled(0)) yield break;

            const int minAmount = 10;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) yield break;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsBrazierEnabled(platformID)) yield break;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each brazier
            var receivingNeeds = new Dictionary<PrefabGUID, List<(Entity, int)>>();
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

        void DistributeInventoryFromOverflow(Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest)>> receivingNeeds,
                                 ServerGameManager serverGameManager, Entity inventoryEntity,
                                 ref Dictionary<PrefabGUID, List<List<(Entity receiver, int amount, bool chest)>>> ungroupedItemLookup)
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

                // Flatten the needs into a single list
                var totalNeeds = needs.SelectMany((list, listIndex) => list.Select((entry, index) => (listIndex, index, entry)).Reverse());

                var totalWanted = totalNeeds.Where(x => x.entry.amount > 0).Sum(x => x.entry.amount);

                // If we have more than enough, distribute evenly
                if (totalWanted <= totalAmount)
                {
                    var leftoverAmount = totalAmount - totalWanted;
                    foreach (var (listIndex, index, (receivingInventoryEntity, wanted, receiverChest)) in totalNeeds)
                    {
                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            needs[listIndex].RemoveAt(index);
                            continue;
                        }

                        if (wanted > 0)
                        {
                            Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, wanted);
                            needs[listIndex].RemoveAt(index);
                        }
                        else
                        {
                            var amountActuallyGiven = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, leftoverAmount);

                            if (amountActuallyGiven < leftoverAmount)
                            {
                                needs[listIndex].RemoveAt(index);
                            }

                            leftoverAmount -= amountActuallyGiven;
                        }
                    }
                }
                else
                {
                    var remainder = 0;
                    // Give out proportionally
                    foreach (var (listIndex, index, (receivingInventoryEntity, wanted, receiverChest)) in totalNeeds)
                    {
                        if (wanted <= 0) continue;

                        if (!Core.EntityManager.Exists(receivingInventoryEntity))
                        {
                            totalWanted -= wanted;
                            needs[listIndex].RemoveAt(index);
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
                        var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                        if (transferred < transferring)
                        {
                            remainder += (transferring - transferred)*totalWanted;
                            needs[listIndex].RemoveAt(index);
                        }
                        else if (transferred >= wanted)
                        {
                            needs[listIndex].RemoveAt(index);
                        }
                        else
                        {
                            needs[listIndex][index] = (receivingInventoryEntity, wanted - transferred, receiverChest);
                        }
                    }
                }
            }
        }

        void DistributeInventory(Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount, bool chest)>> receivingNeeds,
                                 ServerGameManager serverGameManager, int group, Entity inventoryEntity, Entity[] overflowStashes, int retain = 0, bool chest=false)
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
                
                var totalWanted = needs.Where(x => x.amount > 0 && (!chest || !x.chest)).Sum(x => x.amount);

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
                        var (receivingInventoryEntity, wanted, receiverChest) = needs[i];

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
                    // Give out proportionally
                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted, receiverChest) = needs[i];

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
                        var transferred = Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, transferring);
                        totalTransferred += transferred;
                        if (transferred < transferring)
                        {
                            remainder += totalWanted * (transferring - transferred);
                            needs.RemoveAt(i);
                        }
                        else if (transferred >= wanted)
                        {
                            needs.RemoveAt(i);
                        }
                        else
                        {
                            needs[i] = (receivingInventoryEntity, wanted - transferred, receiverChest);
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

                var totalWanted = needs.Sum(x => x.amount);
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
