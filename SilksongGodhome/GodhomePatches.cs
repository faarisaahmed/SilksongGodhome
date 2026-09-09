using System;
using System.Reflection;
using HarmonyLib;
using SilksongGodhome.Rebuild;

namespace SilksongGodhome
{
    /// <summary>
    /// Every hook into the base game lives here, so there's one place to look when a
    /// Silksong patch renames something.
    /// </summary>
    [HarmonyPatch]
    internal static class GodhomePatches
    {
        /// <summary>
        /// StartGameEventTrigger.bossRush is private and we build against the stock
        /// assembly, so identifying the Godhome menu entry goes through reflection.
        /// </summary>
        private static readonly FieldInfo BossRushField =
            AccessTools.Field(typeof(StartGameEventTrigger), "bossRush");

        public static bool IsBossRushTrigger(StartGameEventTrigger t)
        {
            if (t == null || BossRushField == null) return false;
            try { return (bool)BossRushField.GetValue(t); }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // 1. Reveal the menu entry.
        // ------------------------------------------------------------------

        /// <summary>
        /// SetupStatusModifiers is where the game applies its own unlock flags during
        /// boot (it's the method that would set RecBossRushMode if the shipped
        /// gameConfig.unlockBossRushMode were true). Hooking it means the record is in
        /// place before any menu screen evaluates its button conditions.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "SetupStatusModifiers")]
        private static void GameManager_SetupStatusModifiers_Postfix(GameManager __instance)
        {
            ModeUnlock.Unlock(__instance);

            // Idempotent (AddRespawnPoint checks Contains first). Doing it here as well
            // as in the loadout covers continuing an existing Godseeker save and the
            // debug warp, not just starting a new one.
            GodhomeLoadout.RegisterHubWithTeleportMap();
        }

        // ------------------------------------------------------------------
        // 2. Give the mode a save state.
        // ------------------------------------------------------------------

        /// <summary>
        /// The shipped AddGGPlayerDataOverrides has an empty body. A postfix is enough:
        /// there's nothing there to run first, and running after leaves the door open
        /// should a future patch restore some of it.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.AddGGPlayerDataOverrides))]
        private static void PlayerData_AddGGPlayerDataOverrides_Postfix(PlayerData __instance)
        {
            GodhomeLoadout.Apply(__instance);
        }

        // ------------------------------------------------------------------
        // 3. Serve the GG_* scenes, which Silksong doesn't ship.
        // ------------------------------------------------------------------

        /// <summary>
        /// Godhome's scene names are still hard-coded all through the game
        /// (Constants.GG_ENTRANCE_SCENE, GG_RETURN_SCENE, every BossStatue's target),
        /// but none of them exist in Silksong's Addressables catalog, so the load would
        /// fail outright.
        ///
        /// Rather than register a custom Addressables provider - scene providers are
        /// awkward to fake, and SceneLoad wants a real AsyncOperationHandle it can
        /// activate and later unload - we let the load run against a real, cheap donor
        /// scene and then replace its contents once it's live. The engine's transition,
        /// camera and hero-placement machinery all behave normally because from its
        /// point of view an ordinary scene loaded; we just own what's inside it.
        ///
        /// <see cref="SceneRedirect"/> records the logical Godhome name so the rebuilder
        /// knows what to construct when the donor comes up.
        ///
        /// BeginSceneTransition is the single funnel every scene change goes through -
        /// a better hook than SceneLoad's constructors, of which there are two.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.BeginSceneTransition))]
        private static bool GameManager_BeginSceneTransition_Prefix(GameManager.SceneLoadInfo info)
        {
            // Returning false cancels the transition - used when we're already in Godhome
            // and can swap the room in place instead.
            return SceneRedirect.Intercept(info);
        }
    }
}
