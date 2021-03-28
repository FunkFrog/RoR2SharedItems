using System;
using System.Linq;
using EnigmaticThunder.Util;
using EntityStates.GoldGat;
using MonoMod.Cil;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace ShareSuite
{
    public static class MoneySharingHooks
    {
        public static bool MapTransitionActive;
        public static int SharedMoneyValue;

        internal static void UnHook()
        {
            On.RoR2.SceneDirector.Start -= ResetClassValues;
            On.RoR2.HealthComponent.TakeDamage -= BrittleCrownDamageHook;
            On.RoR2.GlobalEventManager.OnHitEnemy -= BrittleCrownOnHitHook;
            On.RoR2.DeathRewards.OnKilledServer -= ShareKillMoney;
            On.RoR2.BarrelInteraction.OnInteractionBegin -= ShareBarrelMoney;
            On.RoR2.MoneyPickup.OnTriggerStay -= ShareTomeMoney;
            On.RoR2.SceneExitController.Begin -= SplitExitMoney;
            On.RoR2.PurchaseInteraction.OnInteractionBegin -= OnShopPurchase;
            On.EntityStates.GoldGat.GoldGatFire.FireBullet -= GoldGatFireHook;
            On.RoR2.Networking.GameNetworkManager.OnClientConnect -= GoldGatConnect;
            On.RoR2.Networking.GameNetworkManager.OnClientDisconnect -= GoldGatDisconnect;

            IL.EntityStates.GoldGat.GoldGatFire.FireBullet -= RemoveGoldGatMoneyLine;
        }

        internal static void Hook()
        {
            On.RoR2.SceneDirector.Start += ResetClassValues;
            On.RoR2.HealthComponent.TakeDamage += BrittleCrownDamageHook;
            On.RoR2.GlobalEventManager.OnHitEnemy += BrittleCrownOnHitHook;
            On.RoR2.DeathRewards.OnKilledServer += ShareKillMoney;
            On.RoR2.BarrelInteraction.OnInteractionBegin += ShareBarrelMoney;
            On.RoR2.MoneyPickup.OnTriggerStay += ShareTomeMoney;
            On.RoR2.SceneExitController.Begin += SplitExitMoney;
            On.RoR2.PurchaseInteraction.OnInteractionBegin += OnShopPurchase;
            On.EntityStates.GoldGat.GoldGatFire.FireBullet += GoldGatFireHook;
            On.RoR2.Networking.GameNetworkManager.OnClientConnect += GoldGatConnect;
            On.RoR2.Networking.GameNetworkManager.OnClientDisconnect += GoldGatDisconnect;

            if (ShareSuite.MoneyIsShared.Value && GeneralHooks.IsMultiplayer()) IL.EntityStates.GoldGat.GoldGatFire.FireBullet += RemoveGoldGatMoneyLine;
        }

        private static void ShareTomeMoney(On.RoR2.MoneyPickup.orig_OnTriggerStay orig, MoneyPickup self, Collider other)
        {
            #region Sharedmoney
            if ((self.baseObject == null) || (!ShareSuite.MoneyIsShared.Value))
            {
                orig(self, other);
                return;
            }

            int amount;
            if (self.shouldScale)
            {
                amount = (int) (self.baseGoldReward * Mathf.Pow(Run.instance.difficultyCoefficient, 1.25f));
            }
            else
            {
                amount = self.baseGoldReward;
            }

            orig(self, other);

            if (self.GetFieldValue<bool>("alive")) return;

            // Collect reward from tome and put it into shared pool
            SharedMoneyValue += amount;

            if (!ShareSuite.MoneyScalarEnabled.Value
                || !NetworkServer.active) return;
            AddToSharedMoneyValue(amount);
            #endregion
        }

        public static void AddMoneyExternal(int amount)
        {
            if (ShareSuite.MoneyIsShared.Value)
            {
                SharedMoneyValue += amount;
            }
            else
            {
                foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
                {
                    player.money += (uint) amount;
                }
            }
        }

        public static bool SharedMoneyEnabled()
        {
            return ShareSuite.MoneyIsShared.Value;
        }

        private static void OnShopPurchase(On.RoR2.PurchaseInteraction.orig_OnInteractionBegin orig,
            PurchaseInteraction self, Interactor activator)
        {
            #region Sharedmoney

            if (ShareSuite.MoneyIsShared.Value)
            {
                switch (self.costType)
                {
                    case CostTypeIndex.Money:
                    {
                        if (self.cost > SharedMoneyValue) return;
                        // Remove money from shared money pool
                        orig(self, activator);
                        SharedMoneyValue -= self.cost;
                        return;
                    }

                    case CostTypeIndex.PercentHealth:
                    {
                        // Share the damage taken from a sacrifice
                        // as it generates shared money
                        orig(self, activator);
                        var teamMaxHealth = 0;
                        foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                        {
                            var charMaxHealth = playerCharacterMasterController.master.GetBody().maxHealth;
                            if (charMaxHealth > teamMaxHealth)
                            {
                                teamMaxHealth = (int) charMaxHealth;
                            }
                        }

                        var purchaseInteraction = self.GetComponent<PurchaseInteraction>();
                        var shrineBloodBehavior = self.GetComponent<ShrineBloodBehavior>();
                        var amount = (uint) (teamMaxHealth * purchaseInteraction.cost / 100.0 *
                                             shrineBloodBehavior.goldToPaidHpRatio);

                        if (ShareSuite.MoneyScalarEnabled.Value) amount *= (uint) ShareSuite.MoneyScalar.Value;

                        SharedMoneyValue += (int) amount;
                        return;
                    }
                }
            }

            orig(self, activator);

            #endregion
        }

        private static void SplitExitMoney(On.RoR2.SceneExitController.orig_Begin orig, SceneExitController self)
        {
            MapTransitionActive = true;
            if (!ShareSuite.MoneyIsShared.Value)
            {
                orig(self);
                return;
            }

            var players = PlayerCharacterMasterController.instances.Count;
            foreach (var player in PlayerCharacterMasterController.instances)
            {
                player.master.money = (uint)
                    // ReSharper disable once PossibleLossOfFraction
                    Mathf.FloorToInt(player.master.money / players);
            }

            orig(self);
        }

        private static void ShareBarrelMoney(On.RoR2.BarrelInteraction.orig_OnInteractionBegin orig,
            BarrelInteraction self, Interactor activator)
        {
            orig(self, activator);

            #region Sharedmoney

            // Collect reward from barrel and put it into shared pool
            SharedMoneyValue += self.goldReward;

            if (!ShareSuite.MoneyScalarEnabled.Value
                || !NetworkServer.active) return;

            AddToSharedMoneyValue(self.goldReward);

            #endregion
        }

        private static void ShareKillMoney(On.RoR2.DeathRewards.orig_OnKilledServer orig, DeathRewards self,
            DamageReport damageReport)
        {
            orig(self, damageReport);

            #region Sharedmoney

            // Collect reward from kill and put it into shared pool
            SharedMoneyValue += (int) self.goldReward;

            if (!ShareSuite.MoneyScalarEnabled.Value
                || !NetworkServer.active) return;

            AddToSharedMoneyValue(self.goldReward);

            #endregion
        }

        private static void ResetClassValues(On.RoR2.SceneDirector.orig_Start orig, SceneDirector self)
        {
            // Allow for money sharing triggers as teleporter is inactive
            SetTeleporterActive(false);

            // This should run on every map, as it is required to fix shared money.
            // Reset shared money value to the default (0) at the start of each round
            SharedMoneyValue = 0;

            orig(self);
        }

        private static void GoldGatFireHook(On.EntityStates.GoldGat.GoldGatFire.orig_FireBullet orig, 
            GoldGatFire self)
        {
            if (!GeneralHooks.IsMultiplayer() || !ShareSuite.MoneyIsShared.Value)
            {
                orig(self);
                return;
            }
            
            var bodyMaster = self.GetFieldValue<CharacterMaster>("bodyMaster");
            var cost = (int)(GoldGatFire.baseMoneyCostPerBullet * 
                             (1f + (TeamManager.instance.GetTeamLevel(bodyMaster.teamIndex) - 1f) * 0.25f));
            SharedMoneyValue = Math.Max(SharedMoneyValue - cost, 0);
            orig(self);
        }

        public static void RemoveGoldGatMoneyLine(ILContext il)
        {
            var cursor = new ILCursor(il);
            
            cursor.GotoNext(
                x => x.MatchLdarg(0),
                x => x.MatchLdfld(out _),
                x => x.MatchLdcR4(out _),
                x => x.MatchLdarg(0),
                x => x.MatchLdfld(out _),
                x => x.MatchCallvirt(out _)
            );

            cursor.RemoveRange(14);
        }


        private static void GoldGatDisconnect(On.RoR2.Networking.GameNetworkManager.orig_OnClientDisconnect orig, RoR2.Networking.GameNetworkManager self, NetworkConnection conn)
        {
            var wasMultiplayer = GeneralHooks.IsMultiplayer();
            orig(self, conn);
            ToggleGoldGat(wasMultiplayer);
        }

        private static void GoldGatConnect(On.RoR2.Networking.GameNetworkManager.orig_OnClientConnect orig, RoR2.Networking.GameNetworkManager self, NetworkConnection conn)
        {
            var wasMultiplayer = GeneralHooks.IsMultiplayer();
            orig(self, conn);
            ToggleGoldGat(wasMultiplayer);
        }

        private static void ToggleGoldGat(bool wasMultiplayer)
        {
            var isMultiplayer = GeneralHooks.IsMultiplayer();
            if (wasMultiplayer == isMultiplayer) return;
            if (ShareSuite.MoneyIsShared.Value && isMultiplayer)
            {
                IL.EntityStates.GoldGat.GoldGatFire.FireBullet += RemoveGoldGatMoneyLine;
            }
            else
            {
                IL.EntityStates.GoldGat.GoldGatFire.FireBullet -= RemoveGoldGatMoneyLine;
            }
        }

        private static void BrittleCrownDamageHook(On.RoR2.HealthComponent.orig_TakeDamage orig, HealthComponent self,
            DamageInfo info)
        {
            if (!NetworkServer.active)
            {
                orig(self, info); //It is not our place to fix faulty mods calling this.
                return;
            }

            if (!ShareSuite.MoneyIsShared.Value
                || !(bool) self.body
                || !(bool) self.body.inventory
            )
            {
                orig(self, info);
                return;
            }

            #region Sharedmoney

            // The idea here is that we track amount of money pre and post function evaluation.
            // We can subsequently apply the difference to the shared pool.
            var body = self.body;

            var preDamageMoney = self.body.master.money;

            orig(self, info);

            if (!self.alive)
            {
                return;
            }

            var postDamageMoney = self.body.master.money;

            // Ignore all of this if we do not actually have the item
            if (body.inventory.GetItemCount(ItemCatalog.FindItemIndex("GoldOnHit")) <= 0) return;

            // Apply the calculation to the shared money pool
            SharedMoneyValue += (int) postDamageMoney - (int) preDamageMoney;

            // Add impact effect
            foreach (var player in PlayerCharacterMasterController.instances)
            {
                if (!(bool) player.master.GetBody() || player.master.GetBody() == body) continue;
                EffectManager.SimpleImpactEffect(Resources.Load<GameObject>(
                        "Prefabs/Effects/ImpactEffects/CoinImpact"),
                    player.master.GetBody().corePosition, Vector3.up, true);
            }

            #endregion
        }

        private static void BrittleCrownOnHitHook(On.RoR2.GlobalEventManager.orig_OnHitEnemy orig,
            GlobalEventManager self, DamageInfo info, GameObject victim)
        {
            if (!ShareSuite.MoneyIsShared.Value
                || !(bool) info.attacker
                || !(bool) info.attacker.GetComponent<CharacterBody>()
                || !(bool) info.attacker.GetComponent<CharacterBody>().master
            )
            {
                orig(self, info, victim);
                return;
            }

            #region Sharedmoney

            // The idea here is that we track amount of money pre and post function evaluation.
            // We can subsequently apply the difference to the shared pool.
            var body = info.attacker.GetComponent<CharacterBody>();

            var preDamageMoney = body.master.money;

            orig(self, info, victim);

            var postDamageMoney = body.master.money;

            // Ignore all of this if we do not actually have the item
            if (!body.inventory || body.inventory.GetItemCount(ItemCatalog.FindItemIndex("GoldOnHit")) <= 0) return;

            // Apply the calculation to the shared money pool
            SharedMoneyValue += (int) postDamageMoney - (int) preDamageMoney;

            #endregion
        }

        private static void AddToSharedMoneyValue(float goldReward)
        {
            //Apply gold rewards to shared money pool
            SharedMoneyValue = Math.Max(SharedMoneyValue + (int) Mathf.Floor(goldReward * (float) ShareSuite.MoneyScalar.Value - goldReward), 0);
        }

        public static void SetTeleporterActive(bool active)
        {
            MapTransitionActive = active;
        }
    }
}