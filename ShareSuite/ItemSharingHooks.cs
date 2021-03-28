using System;
using RoR2;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EnigmaticThunder.Util;
using MonoMod.Cil;
using UnityEngine;
using UnityEngine.Networking;
using Random = UnityEngine.Random;
using EntityStates.Scrapper;
using On.RoR2.Audio;

namespace ShareSuite
{
    public static class ItemSharingHooks
    {
        private static bool _itemLock = false;
        public static void UnHook()
        {
            On.RoR2.PurchaseInteraction.OnInteractionBegin -= OnShopPurchase;
            On.RoR2.ShopTerminalBehavior.DropPickup -= OnPurchaseDrop;
            On.RoR2.GenericPickupController.GrantItem -= OnGrantItem;
            On.EntityStates.ScavBackpack.Opening.OnEnter -= OnScavengerDrop;
            On.RoR2.Chat.PlayerPickupChatMessage.ConstructChatString -= FixZeroItemCount;
            On.EntityStates.Scrapper.ScrappingToIdle.OnEnter -= ScrappingToIdle_OnEnter;
            On.RoR2.PickupCatalog.FindPickupIndex_string -= ItemLock;

            IL.RoR2.ArenaMissionController.EndRound -= ArenaDropEnable;
            IL.RoR2.GenericPickupController.GrantItem -= RemoveDefaultPickupMessage;
        }

        public static void Hook()
        {
            On.RoR2.PurchaseInteraction.OnInteractionBegin += OnShopPurchase;
            On.RoR2.ShopTerminalBehavior.DropPickup += OnPurchaseDrop;
            On.RoR2.GenericPickupController.GrantItem += OnGrantItem;
            On.EntityStates.ScavBackpack.Opening.OnEnter += OnScavengerDrop;
            On.RoR2.Chat.PlayerPickupChatMessage.ConstructChatString += FixZeroItemCount;
            On.EntityStates.Scrapper.ScrappingToIdle.OnEnter += ScrappingToIdle_OnEnter;
            On.RoR2.PickupCatalog.FindPickupIndex_string += ItemLock;

            if (ShareSuite.OverrideVoidFieldLootScalingEnabled.Value) IL.RoR2.ArenaMissionController.EndRound += ArenaDropEnable;
            if (ShareSuite.RichMessagesEnabled.Value) IL.RoR2.GenericPickupController.GrantItem += RemoveDefaultPickupMessage;
        }

        private static PickupIndex ItemLock(On.RoR2.PickupCatalog.orig_FindPickupIndex_string orig, string pickupName)
        {
            // This is a bit of a dubious hook, but it enables really nice interaction with the scrapper, where we add
            // an item every time the scrapper finishes its animation.
            #region Cauldronfix
            if (_itemLock)
            {
                _itemLock = false;
                return orig("This is not an item!");
            }
            return orig(pickupName);
            #endregion
        }

        private static void ScrappingToIdle_OnEnter(On.EntityStates.Scrapper.ScrappingToIdle.orig_OnEnter orig, ScrappingToIdle self)
        {
            if (!(ShareSuite.PrinterCauldronFixEnabled.Value && NetworkServer.active && GeneralHooks.IsMultiplayer()))
            {
                orig(self);
                return;
            }

            _itemLock = true;
            orig(self);

            ScrapperController scrapperController = GetInstanceField(typeof(ScrapperBaseState), self, "scrapperController") as ScrapperController;

            Debug.Log(scrapperController);
            Debug.Log(_itemLock);
            if (scrapperController)
            {
                PickupIndex pickupIndex = PickupIndex.none;
                ItemDef itemDef = ItemCatalog.GetItemDef(scrapperController.lastScrappedItemIndex);
                if (itemDef != null)
                {
                    switch (itemDef.tier)
                    {
                        case ItemTier.Tier1:
                            pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapWhite");
                            break;
                        case ItemTier.Tier2:
                            pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapGreen");
                            break;
                        case ItemTier.Tier3:
                            pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapRed");
                            break;
                        case ItemTier.Boss:
                            pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapYellow");
                            break;
                    }
                }

                if (pickupIndex == PickupIndex.none) return;
                
                var interactor = GetInstanceField(typeof(ScrapperController), scrapperController, "interactor") as Interactor;
                Debug.Log("Interactor Established");

                PickupDef pickupDef = PickupCatalog.GetPickupDef(pickupIndex);
                
                if (!interactor) return;

                SetInstanceField(typeof(ScrappingToIdle), self, "foundValidScrap", true);
                CharacterBody component = interactor.GetComponent<CharacterBody>();
                component.inventory.GiveItem(pickupDef.itemIndex);
                ChatHandler.SendRichCauldronMessage(component.inventory.GetComponent<CharacterMaster>(), pickupIndex);
                scrapperController.itemsEaten -= 1;
            }
        }

        private static void OnGrantItem(On.RoR2.GenericPickupController.orig_GrantItem orig,
            GenericPickupController self, CharacterBody body, Inventory inventory)
        {
            var item = PickupCatalog.GetPickupDef(self.pickupIndex);
            var itemDef = ItemCatalog.GetItemDef(item.itemIndex);
            var randomizedPlayerDict = new Dictionary<CharacterMaster, PickupDef>();

            // If the player is dead, they might not have a body. The game uses inventory.GetComponent, avoiding the issue entirely.
            var master = body?.master ?? inventory?.GetComponent<CharacterMaster>();

            if ((//ShareSuite.RandomizeSharedPickups.Value ||
                 !Blacklist.HasItem(item.itemIndex))
                && NetworkServer.active
                && IsValidItemPickup(self.pickupIndex)
                && GeneralHooks.IsMultiplayer())
            {
                if (ShareSuite.RandomizeSharedPickups.Value)
                {
                    randomizedPlayerDict.Add(master, item);
                }

                foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
                {
                    // Ensure character is not original player that picked up item
                    if (player.inventory == inventory) continue;

                    // Do not reward dead players if not required
                    if (!ShareSuite.DeadPlayersGetItems.Value && player.IsDeadAndOutOfLivesServer()) continue;

                    if (ShareSuite.RandomizeSharedPickups.Value)
                    {
                        var pickupIndex = GetRandomItemOfTier(itemDef.tier, item.pickupIndex);
                        if (pickupIndex == null)
                        {
                            // Could not find any not blacklisted item in that tier. You get nothing! Good day, sir!
                            continue;
                        }
                        var giveItem = PickupCatalog.GetPickupDef(pickupIndex.Value);

                        player.inventory.GiveItem(giveItem.itemIndex);
                        // Alternative: Only show pickup text for yourself
                        // var givePickupDef = PickupCatalog.GetPickupDef(givePickupIndex);
                        // Chat.AddPickupMessage(body, givePickupDef.nameToken, givePickupDef.baseColor, 1);

                        // Legacy -- old normal pickup message handler
                        //SendPickupMessage(player, giveItem);

                        randomizedPlayerDict.Add(player, giveItem);
                    }
                    // Otherwise give everyone the same item
                    else
                    {
                        player.inventory.GiveItem(item.itemIndex);
                    }
                }

                if (ShareSuite.RandomizeSharedPickups.Value)
                {
                    orig(self, body, inventory);
                    ChatHandler.SendRichRandomizedPickupMessage(master, item, randomizedPlayerDict);
                    return;
                }
            }

            orig(self, body, inventory);
            ChatHandler.SendRichPickupMessage(master, item);
        }

        public static void RemoveDefaultPickupMessage(ILContext il)
        {
            var cursor = new ILCursor(il);

            cursor.GotoNext(
                x => x.MatchLdarg(2),
                x => x.MatchCallvirt(out _),
                x => x.MatchLdarg(0),
                x => x.MatchLdfld(out _),
                x => x.MatchCall(out _)
            );

            cursor.RemoveRange(5);
        }

        private static string FixZeroItemCount(On.RoR2.Chat.PlayerPickupChatMessage.orig_ConstructChatString orig,
            Chat.PlayerPickupChatMessage self)
        {
            self.pickupQuantity = Math.Max(1u, self.pickupQuantity);
            return orig(self);
        }

        private static void OnPurchaseDrop(On.RoR2.ShopTerminalBehavior.orig_DropPickup orig, ShopTerminalBehavior self)
        {
            if (!NetworkServer.active)
            {
                orig(self);
                return;
            }

            var costType = self.GetComponent<PurchaseInteraction>().costType;

            //If is valid drop and dupe fix not enabled, true -> we want the item to pop
            //if is valid drop and dupe fix is enabled, false -> item IS shared, we don't want the item to pop, PrinterCauldronFix should deal with this
            //if is not valid drop and dupe fix is not enabled, true -> item ISN'T shared, and dupe fix isn't enabled, we want to pop 
            //if is not valid drop and dupe fix is enabled, false -> item ISN'T shared, dupe fix should catch, we don't want to pop

            if (!GeneralHooks.IsMultiplayer() // is not multiplayer
                || (!IsValidItemPickup(self.CurrentPickupIndex()) && !ShareSuite.PrinterCauldronFixEnabled.Value) //if it's not a valid drop AND the dupe fix isn't enabled
                || self.itemTier == ItemTier.Lunar
                || costType == CostTypeIndex.Money)
            {
                orig(self);
            }
        }

        private static void OnShopPurchase(On.RoR2.PurchaseInteraction.orig_OnInteractionBegin orig,
            PurchaseInteraction self, Interactor activator)
        {
            if (!self.CanBeAffordedByInteractor(activator)) return;

            if (!GeneralHooks.IsMultiplayer())
            {
                orig(self, activator);
                return;
            }

            var shop = self.GetComponent<ShopTerminalBehavior>();

            #region Cauldronfix

            if (ShareSuite.PrinterCauldronFixEnabled.Value)
            {
                var characterBody = activator.GetComponent<CharacterBody>();
                var inventory = characterBody.inventory;

                if (self.costType == CostTypeIndex.WhiteItem
                    || self.costType == CostTypeIndex.GreenItem
                    || self.costType == CostTypeIndex.RedItem
                    || self.costType == CostTypeIndex.BossItem
                    || self.costType == CostTypeIndex.LunarItemOrEquipment)
                {
                    var item = PickupCatalog.GetPickupDef(shop.CurrentPickupIndex()).itemIndex;
                    inventory.GiveItem(item);
                    
                    orig(self, activator);
                    ChatHandler.SendRichCauldronMessage(inventory.GetComponent<CharacterMaster>(),
                        shop.CurrentPickupIndex());
                    return;
                }
            }

            #endregion Cauldronfix

            #region EquipDronefix

            if (ShareSuite.EquipmentShared.Value)
            {
                if (self.costType == CostTypeIndex.Equipment)
                {
                    var rng = self.GetComponent<Xoroshiro128Plus>();
                    var itemIndex = ItemIndex.None;
            
                    var costTypeDef = CostTypeCatalog.GetCostTypeDef(self.costType);
                    if (shop)
                    {
                        itemIndex = PickupCatalog.GetPickupDef(shop.CurrentPickupIndex()).itemIndex;
                    }
            
                    var payCostResults = costTypeDef.PayCost(self.cost,
                        activator, self.gameObject, rng, itemIndex);
            
                    if (payCostResults.equipmentTaken.Count >= 1)
                    {
                        orig(self, activator);
                        EquipmentSharingHooks.RemoveAllUnBlacklistedEquipment();
                        return;
                    }
                }
            }
            #endregion

            orig(self, activator);
        }


        private static void OnScavengerDrop(On.EntityStates.ScavBackpack.Opening.orig_OnEnter orig,
            EntityStates.ScavBackpack.Opening self)
        {
            orig(self);
            ShareSuite.DefaultMaxScavItemDropCount = Math.Max(EntityStates.ScavBackpack.Opening.maxItemDropCount, ShareSuite.DefaultMaxScavItemDropCount);
            var chest = self.GetFieldValue<ChestBehavior>("chestBehavior");
            if (chest.tier1Chance > 0.0f)
            {
                var adjustedDrops = Math.Max((int) Math.Ceiling((double) ShareSuite.DefaultMaxScavItemDropCount / Run.instance.participatingPlayerCount), 2);
                EntityStates.ScavBackpack.Opening.maxItemDropCount = Math.Min(adjustedDrops, ShareSuite.DefaultMaxScavItemDropCount);
            }
            else
            {
                EntityStates.ScavBackpack.Opening.maxItemDropCount = ShareSuite.DefaultMaxScavItemDropCount;
            }
        }

        //Void Fields item fix
        public static void ArenaDropEnable(ILContext il)
        {
            var cursor = new ILCursor(il);

            cursor.GotoNext(
                x => x.MatchLdloc(1),
                x => x.MatchStloc(out _),
                x => x.MatchLdcR4(out _),
                x => x.MatchLdloc(out _)
            );
            cursor.Index++;
            cursor.EmitDelegate<Func<int, int>>(i => ShareSuite.VoidFieldLootCredit.Value);
        }

        public static bool IsValidItemPickup(PickupIndex pickup)
        {
            var pickupdef = PickupCatalog.GetPickupDef(pickup);
            if (pickupdef.itemIndex != ItemIndex.None)
            {
                var itemdef = ItemCatalog.GetItemDef(pickupdef.itemIndex);
                switch (itemdef.tier)
                {
                    case ItemTier.Tier1:
                        return ShareSuite.WhiteItemsShared.Value;
                    case ItemTier.Tier2:
                        return ShareSuite.GreenItemsShared.Value;
                    case ItemTier.Tier3:
                        return ShareSuite.RedItemsShared.Value;
                    case ItemTier.Lunar:
                        return ShareSuite.LunarItemsShared.Value;
                    case ItemTier.Boss:
                        return ShareSuite.BossItemsShared.Value;
                    case ItemTier.NoTier:
                        break;
                    default:
                        return false;
                }
            }

            if (pickupdef.equipmentIndex != EquipmentIndex.None)
            {
                // var equipdef = EquipmentCatalog.GetEquipmentDef(pickupdef.equipmentIndex);
                // Optional further checks ...
                return false;
            }

            return false;
        }

        private static PickupIndex? GetRandomItemOfTier(ItemTier tier, PickupIndex orDefault)
        {
            switch (tier)
            {
                case ItemTier.Tier1:
                    return PickRandomOf(Blacklist.AvailableTier1DropList);
                case ItemTier.Tier2:
                    return PickRandomOf(Blacklist.AvailableTier2DropList);
                case ItemTier.Tier3:
                    return PickRandomOf(Blacklist.AvailableTier3DropList);
                case ItemTier.Lunar:
                    if (ShareSuite.LunarItemsRandomized.Value)
                        return PickRandomOf(Blacklist.AvailableLunarDropList);
                    break;
                case ItemTier.Boss:
                    if (ShareSuite.BossItemsRandomized.Value)
                        return PickRandomOf(Blacklist.AvailableBossDropList);
                    break;
            }
            var pickupDef = PickupCatalog.GetPickupDef(orDefault);
            if (Blacklist.HasItem(pickupDef.itemIndex))
                return null;
            return orDefault;
        }

        internal static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.GetField;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }

        internal static void SetInstanceField(Type type, object instance, string fieldName, object value)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            field.SetValue(instance, value);
        }

        private static T? PickRandomOf<T>(IList<T> collection) where T : struct =>
            collection.Count > 0
            ? collection[Random.Range(0, collection.Count)]
            : (T?) null;
    }
}

