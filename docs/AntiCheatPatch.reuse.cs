using HarmonyLib;
using System;

namespace HsMod.Reuse
{
    /// <summary>
    /// Reusable Harmony patch for Hearthstone AntiCheatSDK.AntiCheatManager.
    /// Drop this file into a BepInEx/Harmony project and call Patch() during plugin Awake().
    /// </summary>
    public static class AntiCheatPatchInstaller
    {
        private const string HarmonyId = "HsMod.Reuse.AntiCheatPatch";
        private const string TargetTypeName = "AntiCheatSDK.AntiCheatManager";

        private static Harmony harmony;

        public static Action<string> LogDebug { get; set; }
        public static Action<string> LogWarning { get; set; }

        public static bool IsPatched => harmony != null;

        public static int Patch()
        {
            if (harmony != null)
            {
                return CountPatchedMethods(harmony);
            }

            if (AccessTools.TypeByName(TargetTypeName) == null)
            {
                throw new TypeLoadException($"Cannot find target type: {TargetTypeName}");
            }

            harmony = Harmony.CreateAndPatchAll(typeof(PatchAntiCheat), HarmonyId);

            int patchedCount = CountPatchedMethods(harmony);
            LogWarning?.Invoke($"PatchAntiCheat => Patched {patchedCount} methods");
            return patchedCount;
        }

        public static void Unpatch()
        {
            if (harmony == null)
            {
                return;
            }

            harmony.UnpatchSelf();
            harmony = null;
            LogWarning?.Invoke("PatchAntiCheat => Unpatched");
        }

        internal static void Debug(string message)
        {
            LogDebug?.Invoke(message);
        }

        private static int CountPatchedMethods(Harmony instance)
        {
            int count = 0;
            foreach (var _ in instance.GetPatchedMethods())
            {
                count++;
            }

            return count;
        }

        private static class PatchAntiCheat
        {
            [HarmonyPrefix]
            [HarmonyPatch(TargetTypeName, "OnLoginComplete")]
            public static bool PatchAntiCheatManagerOnLoginComplete()
            {
                Debug("AntiCheat OnLoginComplete feature is disabled.");
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch(TargetTypeName, "Shutdown")]
            public static bool PatchAntiCheatManagerShutdown()
            {
                Debug("AntiCheat Shutdown feature is disabled.");
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch(TargetTypeName, "TryCallSDK")]
            [HarmonyPatch(TargetTypeName, "CallInterfaceCallSDK")]
            public static bool PatchAntiCheatManagerTryCallSDK(ref string scriptId)
            {
                Debug("AntiCheat TryCallSDK feature is disabled.");
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch(TargetTypeName, "InnerSDKMethodCall")]
            public static bool PatchAntiCheatManagerInnerSDKMethodCall(ref Action<string> handler, ref string args)
            {
                Debug("AntiCheat InnerSDKMethodCall feature is disabled.");
                return false;
            }
        }
    }
}
