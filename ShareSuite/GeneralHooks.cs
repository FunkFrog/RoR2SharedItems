using System;
using RoR2;
using System.Collections.Generic;
using UnityEngine;
using MonoMod.Cil;
using Mono.Cecil.Cil;

namespace ShareSuite
{
    public static class GeneralHooks
    {
        private static int _sacrificeOffset = 1;
        private static int _bossItems = 1;
        private static List<string> NoInteractibleOverrideScenes = new List<string>{"MAP_BAZAAR_TITLE", 
            "MAP_ARENA_TITLE", "MAP_LIMBO_TITLE", "MAP_MYSTERYSPACE_TITLE"};

        internal static void Hook()
        {
            //On.RoR2.BossGroup.DropRewards += BossGroup_DropRewards;
            IL.RoR2.BossGroup.DropRewards += BossGroup_DropRewards;
            On.RoR2.SceneDirector.PlaceTeleporter += InteractibleCreditOverride;
            On.RoR2.TeleporterInteraction.OnInteractionBegin += OverrideBossLootScaling;
            On.RoR2.Artifacts.SacrificeArtifactManager.OnPrePopulateSceneServer += SetSacrificeOffset;
            On.RoR2.Util.GetExpAdjustedDropChancePercent += GetExpAdjustedDropChancePercent;
        }

        internal static void UnHook()
        {
            //On.RoR2.BossGroup.DropRewards -= BossGroup_DropRewards;
            IL.RoR2.BossGroup.DropRewards -= BossGroup_DropRewards;
            On.RoR2.SceneDirector.PlaceTeleporter -= InteractibleCreditOverride;
            On.RoR2.TeleporterInteraction.OnInteractionBegin -= OverrideBossLootScaling;
            On.RoR2.Artifacts.SacrificeArtifactManager.OnPrePopulateSceneServer -= SetSacrificeOffset;
            On.RoR2.Util.GetExpAdjustedDropChancePercent -= GetExpAdjustedDropChancePercent;
        }

        private static void BossGroup_DropRewards(ILContext il)
        {
            var cursor = new ILCursor(il);

            cursor.GotoNext(
                x => x.MatchLdloc(0),
                x => x.MatchMul(),
                x => x.MatchStloc(3),
                x => x.MatchLdcR4(out _)
            );
            cursor.Index++;
            cursor.EmitDelegate<Func<int, int>>(i => _bossItems);
        }

        private static void SetSacrificeOffset(On.RoR2.Artifacts.SacrificeArtifactManager.orig_OnPrePopulateSceneServer orig, SceneDirector sceneDirector)
        {
            _sacrificeOffset = 2;
            orig(sceneDirector);
        }

        //private static void BossGroup_DropRewards(On.RoR2.BossGroup.orig_DropRewards orig, BossGroup self)
        //{
        //    ItemDropAPI.BossDropParticipatingPlayerCount = _bossItems;
        //    orig(self);
        //}

        /// <summary>
        /// // Helper function for Bossloot
        /// </summary>
        private static void OverrideBossLootScaling(On.RoR2.TeleporterInteraction.orig_OnInteractionBegin orig,
            TeleporterInteraction self, Interactor activator)
        {
            _bossItems = ShareSuite.OverrideBossLootScalingEnabled.Value ? ShareSuite.BossLootCredit.Value : Run.instance.participatingPlayerCount;
            orig(self, activator);
        }

        public static bool IsMultiplayer()
        {
            // Check whether the quantity of players in the lobby exceeds one.
            return ShareSuite.OverrideMultiplayerCheck.Value || PlayerCharacterMasterController.instances.Count > 1;
        }

        private static void InteractibleCreditOverride(On.RoR2.SceneDirector.orig_PlaceTeleporter orig, SceneDirector self)
        {
            orig(self);

            Debug.Log(SceneInfo.instance.sceneDef.nameToken);

            #region InteractablesCredit

            // This is the standard amount of interactablesCredit we work with.
            // Prior to the interactablesCredit overhaul this was the standard value for all runs.
            var interactableCredit = 200;

            var stageInfo = SceneInfo.instance.GetComponent<ClassicStageInfo>();

            if (stageInfo)
            {
                // Overwrite our base value with the actual amount of director credits.
                interactableCredit = stageInfo.sceneDirectorInteractibleCredits;

                // We require playercount for several of the following computations. We don't want this to break with
                // those crazy 'mega party mods', thus we clamp this value.
                var clampPlayerCount = Math.Min(Run.instance.participatingPlayerCount, 8);

                // The flat creditModifier slightly adjust interactables based on the amount of players.
                // We do not want to reduce the amount of interactables too much for very high amounts of players (to support multiplayer mods).
                var creditModifier = (float)(0.95 + clampPlayerCount * 0.05);

                // In addition to our flat modifier, we additionally introduce a stage modifier.
                // This reduces player strength early game (as having more bodies gives a flat power increase early game).
                creditModifier *= (float)Math.Max(
                                        1.0 + 0.1 * Math.Min(
                                            Run.instance.participatingPlayerCount * 2 - Run.instance.stageClearCount - 2
                                            , 3)
                                        , 1.0);

                // We must apply the transformation to interactableCredit otherwise bonusIntractableCreditObject will be overwritten.
                interactableCredit = (int)(interactableCredit / creditModifier);

                // Fetch the amount of bonus interactables we may play with. We have to do this after our first math block,
                // as we do not want to divide bonuscredits twice.
                if (stageInfo.bonusInteractibleCreditObjects != null)
                {
                    foreach (var bonusInteractableCreditObject in stageInfo.bonusInteractibleCreditObjects)
                    {
                        if (bonusInteractableCreditObject.objectThatGrantsPointsIfEnabled.activeSelf)
                        {
                            interactableCredit += bonusInteractableCreditObject.points / clampPlayerCount;
                        }
                    }
                }
            }

            // Set interactables budget to interactableCredit * config player count / sacrificeOffset.
            if (ShareSuite.OverridePlayerScalingEnabled.Value && (!SceneInfo.instance 
                    || !NoInteractibleOverrideScenes.Contains(SceneInfo.instance.sceneDef.nameToken)))
                self.interactableCredit = (int) (interactableCredit * ShareSuite.InteractablesCredit.Value / _sacrificeOffset) + ShareSuite.InteractablesOffset.Value;

            #endregion

            _sacrificeOffset = 1;
        }

        private static float GetExpAdjustedDropChancePercent(On.RoR2.Util.orig_GetExpAdjustedDropChancePercent orig, float baseChancePercent, GameObject characterBodyObject)
        {
            if (ShareSuite.SacrificeFixEnabled.Value)
            {
                baseChancePercent /= PlayerCharacterMasterController.instances.Count;
            }
            return orig(baseChancePercent, characterBodyObject);
        }
    }
}
