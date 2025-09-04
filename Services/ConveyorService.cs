using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using ProjectM.Shared;
using Stunlock.Core;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace KindredLogistics.Services
{
    internal class ConveyorService
    {
        readonly System.Random random = new();

        readonly List<Entity> distributionList = [];
        readonly Dictionary<Entity, int> amountReceiving = [];
        readonly Dictionary<PrefabGUID, int> amountToDistribute = [];

        public ConveyorService()
        {
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessConveyors);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessSalvagers);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessUnitSpawners);
            Core.TerritoryService.RegisterTerritoryUpdateCallback(ProcessBraziers);
        }

        void ProcessConveyors(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsConveyorEnabled(0)) return;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsConveyorEnabled(platformID)) return;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each station
            var receivingNeeds = new Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount)>>();
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

                        needs.Add((inputInventoryEntity, amountWanted));
                    }
                }
            }

            // Determine what is desired by each receiving stash
            foreach (var (group, stash) in Core.Stash.GetAllReceivingStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    var inventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                    foreach (var item in inventoryBuffer)
                    {
                        if (item.ItemType.GuidHash == 0) continue;

                        if (!receivingNeeds.TryGetValue((group, item.ItemType), out var needs))
                        {
                            needs = [];
                            receivingNeeds[(group, item.ItemType)] = needs;
                        }

                        needs.Add((attachedEntity, 500));
                    }
                }
            }

            if (receivingNeeds.Count == 0) return;

            // Now distribute from all the sender stations to the stations in need
            foreach (var (group, sendingStation) in Core.RefinementStations.GetAllSendingStations(territoryId))
            {
                var refinementStation = sendingStation.Read<Refinementstation>();
                var outputInventoryEntity = refinementStation.OutputInventoryEntity.GetEntityOnServer();
                if (outputInventoryEntity.Equals(Entity.Null)) continue;
                DistributeInventory(receivingNeeds, serverGameManager, group, outputInventoryEntity);
            }

            // Next distribute from all the send stashes
            foreach (var (group, sendingStash) in Core.Stash.GetAllSendingStashes(territoryId))
            {
                if (!serverGameManager.TryGetBuffer<AttachedBuffer>(sendingStash, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var attachedEntity = attachedBuffer.Entity;
                    if (!attachedEntity.Has<PrefabGUID>()) continue;
                    if (!attachedEntity.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    DistributeInventory(receivingNeeds, serverGameManager, group, attachedEntity, retain: 1);
                }
            }
        }

        void DistributeInventory(Dictionary<(int group, PrefabGUID item), List<(Entity receiver, int amount)>> receivingNeeds,
                                 ServerGameManager serverGameManager, int group, Entity inventoryEntity, int retain = 0)
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

            foreach((var item, var totalAmount) in amountToDistribute)
            {
                // Does anyone need this item?
                if (!receivingNeeds.TryGetValue((group, item), out var needs)) continue;

                var totalWanted = needs.Sum(x => x.amount);

                // If we have more than enough, distribute evenly
                if (totalWanted <= totalAmount)
                {
                    foreach (var (receivingInventoryEntity, wanted) in needs)
                        Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item, wanted);
                    needs.Clear();
                }
                else
                {
                    var remainder = 0;
                    // Give out proportionally

                    for (int i = needs.Count - 1; i >= 0; i--)
                    {
                        var (receivingInventoryEntity, wanted) = needs[i];
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

        readonly HashSet<(Entity entity, PrefabGUID item)> salvagerFullOfItem = [];

        void ProcessSalvagers(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsSalvageEnabled(0)) return;

            var salvagers = Core.SalvageService.GetAllSalvageStations(territoryId)
                            .Select((s, i) => (entity: s, station: s.Read<Salvagestation>(), index: i))
                            .ToList();

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsSalvageEnabled(platformID)) return;

            // Empty all salvage outputs first
            var itemStashes = Utilities.GetItemStashesOnTerritory(territoryId);
            foreach (var salvager in salvagers)
            {
                var salvageStation = salvager.station;
                var outputInventoryEntity = salvageStation.OutputInventoryEntity.GetEntityOnServer();

                var inventoryBuffer = Core.EntityManager.GetBuffer<InventoryBuffer>(outputInventoryEntity).ToNativeArray(Allocator.Temp);
                try
                {
                    if (InventoryUtilities.IsInventoryEmpty(inventoryBuffer)) continue;

                    Utilities.StashInventoryEntity(outputInventoryEntity, itemStashes);
                }
                finally
                {
                    inventoryBuffer.Dispose();
                }
            }

            // Now fill all the salvagers
            salvagerFullOfItem.Clear();
            foreach (var salvageSupplier in Core.Stash.GetAllSalvageStashes(territoryId))
            {
                if (!Core.ServerGameManager.TryGetBuffer<AttachedBuffer>(salvageSupplier, out var buffer))
                    continue;
                foreach (var attachedBuffer in buffer)
                {
                    var salvageSupplierInventory = attachedBuffer.Entity;
                    if (!salvageSupplierInventory.Has<PrefabGUID>()) continue;
                    if (!salvageSupplierInventory.Read<PrefabGUID>().Equals(StashService.ExternalInventoryPrefab)) continue;

                    var inventoryBuffer = salvageSupplierInventory.ReadBuffer<InventoryBuffer>();
                    foreach (var item in inventoryBuffer)
                    {
                        Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(item.ItemType, out var prefabEntity);
                        if (!prefabEntity.Has<Salvageable>()) continue;

                        var amountToTransfer = item.Amount;
                        for (var i = salvagers.Count - 1; i >= 0; i--)
                        {
                            var salvager = salvagers[i];
                            var salvagerKey = (salvager.entity, item.ItemType);
                            if (salvagerFullOfItem.Contains(salvagerKey)) continue;

                            var salvageStation = salvager.station;
                            var inputInventoryEntity = salvageStation.InputInventoryEntity.GetEntityOnServer();

                            var startInputSlot = 0;
                            Utilities.TransferItemEntities(salvageSupplierInventory, inputInventoryEntity, item.ItemType, amountToTransfer, ref startInputSlot, out var amountTransferred);

                            var isFull = false;
                            if (amountTransferred < amountToTransfer)
                            {
                                if (Core.ServerGameManager.HasFullInventory(inputInventoryEntity))
                                {
                                    salvagers.RemoveAt(i);
                                    isFull = true;
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

                            amountToTransfer -= amountTransferred;

                            if (!salvageStation.IsWorking)
                            {
                                salvageStation.IsWorking = true;
                                salvager.entity.Write(salvageStation);

                                if (!isFull)
                                    salvagers[salvager.index] = (salvager.entity, salvageStation, salvager.index);
                            }

                            if (amountToTransfer <= 0) break;
                        }
                    }
                }
            }
        }

        void ProcessUnitSpawners(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(0)) return;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsUnitSpawnerEnabled(platformID)) return;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each brazier
            var receivingNeeds = new Dictionary<PrefabGUID, Dictionary<Entity, int>>();
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

                        if (!needs.TryGetValue(inputInventoryEntity, out var amount))
                        {
                            needs[inputInventoryEntity] = amountWanted;
                        }
                        else
                        {
                            needs[inputInventoryEntity] = Mathf.Max(amount, amountWanted);
                        }
                    }
                }
            }

            if (receivingNeeds.Count == 0) return;

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
            }
        }

        void DistributeInventory(Dictionary<PrefabGUID, Dictionary<Entity, int>> receivingNeeds,
                                 ServerGameManager serverGameManager, Entity inventoryEntity, int retain = 0)
        {
            var inventoryBuffer = inventoryEntity.ReadBuffer<InventoryBuffer>();
            foreach (var item in inventoryBuffer)
            {
                if (item.ItemType.GuidHash == 0) continue;
                // Does anyone need this item?
                if (!receivingNeeds.TryGetValue(item.ItemType, out var needs)) continue;

                // Distribute the item to all the stations in need weighted by the transferring needed
                var amount = item.Amount - retain;

                if (amount <= 0) continue;

                var totalWanted = needs.Sum(x => x.Value);

                // If we have more than enough, distribute evenly
                if (totalWanted < amount)
                {
                    foreach (var (receivingInventoryEntity, wanted) in needs)
                        Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item.ItemType, wanted);
                    needs.Clear();
                }
                else
                {
                    // Can only give out whole numbers so need to randomly portion it out based on a weight of the desired transferring
                    // Over time this should even out
                    distributionList.Clear();
                    foreach (var (receivingInventoryEntity, wanted) in needs)
                    {
                        for (int i = 0; i < wanted; i++)
                        {
                            distributionList.Add(receivingInventoryEntity);
                        }
                    }

                    amountReceiving.Clear();
                    for (int i = 0; i < amount; i++)
                    {
                        var index = random.Next(distributionList.Count);

                        // Determine who gets it
                        var receivingInventoryEntity = distributionList[index];
                        if (!amountReceiving.TryGetValue(receivingInventoryEntity, out var receivingAmount))
                            amountReceiving[receivingInventoryEntity] = 1;
                        else
                            amountReceiving[receivingInventoryEntity] = receivingAmount + 1;
                    }

                    foreach (var (receivingInventoryEntity, receivingAmount) in amountReceiving)
                    {
                        Utilities.TransferItems(serverGameManager, inventoryEntity, receivingInventoryEntity, item.ItemType, receivingAmount);

                        // Remove the transferring from the needs list
                        var amountToRemove = receivingAmount;
                        needs[receivingInventoryEntity] -= amountToRemove;
                        if (needs[receivingInventoryEntity] <= 0)
                            needs.Remove(receivingInventoryEntity);
                    }
                }
            }
        }

        void ProcessBraziers(int territoryId, Entity castleHeartEntity)
        {
            if (!Core.PlayerSettings.IsBrazierEnabled(0)) return;
            
            const int minAmount = 10;

            var userOwner = castleHeartEntity.Read<UserOwner>();
            if (userOwner.Owner.GetEntityOnServer() == Entity.Null) return;

            var platformID = userOwner.Owner.GetEntityOnServer().Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsBrazierEnabled(platformID)) return;

            var serverGameManager = Core.ServerGameManager;

            // Determine what is needed for each brazier
            var receivingNeeds = new Dictionary<PrefabGUID, Dictionary<Entity, int>>();
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

                needs[inputInventoryEntity] = minAmount - has;
            }

            if (receivingNeeds.Count == 0) return;

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
            }
        }
    }
}
