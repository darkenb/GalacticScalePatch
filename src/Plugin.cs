using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;

[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace GalacticScalePatch
{
    // Fixes for GalacticScale 2.80.7, applied to the original mod at runtime with Harmony. GalacticScale's own DLL is
    // not changed. A build of GalacticScale that already carries these fixes is left alone.
    [BepInPlugin(Guid, "BUG_GalacticScalePatch", "1.0.1")]
    [BepInDependency(GalacticScale)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "bug.dsp.galacticscalepatch", GalacticScale = "dsp.galactic-scale.2";
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            if (GalacticScaleFixes.AlreadyFixed())
            {
                Log.LogInfo("GalacticScale already contains these fixes, nothing patched");
                return;
            }
            string version = Chainloader.PluginInfos.TryGetValue(GalacticScale, out var info) ? info.Metadata.Version.ToString() : "?";
            var harmony = new Harmony(Guid);
            try
            {
                harmony.PatchAll(typeof(GalacticScaleFixes));
                Log.LogInfo("GalacticScale " + version + " patched");
            }
            catch (Exception e)
            {
                // All or nothing: half of the fixes would leave galaxy generation inconsistent.
                Log.LogError("GalacticScale " + version + " could not be patched; it is left as it is. " + e);
                Undo.All(harmony);
            }
        }
    }

    internal static class Undo
    {
        // Removing patches rebuilds each method, which can fail too; one method that cannot be restored must not stop the others
        // or crash plugin loading.
        internal static void All(Harmony harmony)
        {
            foreach (var method in new List<System.Reflection.MethodBase>(harmony.GetPatchedMethods()))
            {
                try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                catch (Exception e) { Plugin.Log.LogError("Could not remove the patch on " + method.DeclaringType?.Name + "." + method.Name + ": " + e.Message); }
            }
        }
    }

    internal static class Il
    {
        // Transpiler helper: fails the whole patch when a pattern is missing, instead of patching half.
        internal static void Require(bool found, string what)
        {
            if (!found) throw new InvalidOperationException("pattern not found: " + what);
        }

        internal static int Count(List<CodeInstruction> code, Func<CodeInstruction, bool> match)
        {
            int n = 0;
            foreach (var c in code) if (match(c)) n++;
            return n;
        }
    }
}
