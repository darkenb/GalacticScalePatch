using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using GalacticScale;
using GSSerializer;
using HarmonyLib;
using NebulaCompatibility;
using UnityEngine;
using Object = UnityEngine.Object;
using static PlanetModelingManager;

namespace GalacticScalePatch
{
    public static class GalacticScaleFixes
    {
        public static bool AlreadyFixed() => AccessTools.Method(typeof(Modeler), "CancelPendingScans") != null;

        // ---- Galaxy generation: one generation at a time, from a clean scan queue and a galaxy view ----

        // A settings change must not replace GSSettings while a worker scans the old galaxy.
        [HarmonyPrefix, HarmonyPatch(typeof(GS2), nameof(GS2.ProcessGalaxy))]
        static void LockGeneration(out object __state)
        {
            __state = planetProcessingLock;
            Monitor.Enter(__state);
            Modeler.Reset();
            SystemDisplay.inSystemDisplay = false;
            SystemDisplay.viewStar = null;
            SystemDisplay.systemLabelsHidden = false;
            SystemDisplay.altWasDown = false;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(GS2), nameof(GS2.ProcessGalaxy))]
        static Exception UnlockGeneration(object __state, Exception __exception)
        {
            if (__state != null) Monitor.Exit(__state);
            return __exception;
        }

        // ---- Planet scan worker: keep existing planet data, free scan data also on failure, stop on out of memory ----

        static volatile bool scanMemoryExhausted;

        static void CancelPendingScans()
        {
            lock (scnPlanetReqList)
                while (scnPlanetReqList.Count > 0) scnPlanetReqList.Dequeue().scanning = false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Modeler), nameof(Modeler.Reset))]
        static void AfterReset() => scanMemoryExhausted = false;

        [HarmonyPrefix, HarmonyPatch(typeof(Modeler), nameof(Modeler.Scan))]
        static bool Scan()
        {
            Thread thread;
            lock (planetProcessingLock) thread = planetScanThread;

            for (;;)
            {
                PlanetData calcPlanet = null;
                lock (planetProcessingLock)
                {
                    lock (planetScanThreadFlagLock)
                    {
                        if (thread != planetScanThread)
                        {
                            GS2.Log("End due to planetCalculateThread mismatch");
                            return false;
                        }
                        if (planetScanThreadFlag != ThreadFlag.Running)
                        {
                            GS2.Log("ThreadFlag: Ending => Ended");
                            planetScanThreadFlag = ThreadFlag.Ended;
                            return false;
                        }
                    }

                    if (scanMemoryExhausted) CancelPendingScans();
                    lock (scnPlanetReqList)
                        if (scnPlanetReqList.Count > 0) calcPlanet = scnPlanetReqList.Dequeue();

                    if (calcPlanet != null && calcPlanet.loading)
                    {
                        calcPlanet.scanning = false;
                        calcPlanet = null;
                    }

                    if (calcPlanet != null)
                    {
                        Modeler.processing.Add(calcPlanet);
                        bool hadData = calcPlanet.data != null;
                        bool scanSucceeded = false;
                        try
                        {
                            Modeler.ScanPlanetGS(calcPlanet);
                            scanSucceeded = true;
                        }
                        catch (Exception ex)
                        {
                            lock (planetScanThreadError)
                                if (string.IsNullOrEmpty(planetScanThreadError)) planetScanThreadError = ex.ToString();
                            if (ex is OutOfMemoryException)
                            {
                                // Stop allocation retries for this galaxy; Reset opens a fresh generation.
                                scanMemoryExhausted = true;
                                CancelPendingScans();
                            }
                            GS2.Warn($"Error during calculating planet {calcPlanet.name}: {ex}");
                            if (calcPlanet != GameMain.localPlanet && !hadData && calcPlanet.data != null) Modeler.FreeScanData(calcPlanet);
                            calcPlanet.scanned = false;
                            Modeler.processing.Remove(calcPlanet);
                        }
                        finally
                        {
                            calcPlanet.scanning = false;
                            // An empty scan during a quit to the main menu must not be committed as scanned.
                            calcPlanet.scanned = scanSucceeded && (UIRoot.instance == null || !UIRoot.instance.backToMainMenu);
                            if (Modeler.processing.Contains(calcPlanet)) Modeler.processing.Remove(calcPlanet);
                        }
                    }
                }
                Thread.Sleep(calcPlanet == null ? 50 : 2);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Modeler), nameof(Modeler.ScanPlanetGS))]
        static bool ScanPlanetGS(PlanetData calcPlanet)
        {
            PlanetAlgorithm planetAlgorithm = Algorithm(calcPlanet);
            if (planetAlgorithm == null) return false;
            bool hadData = calcPlanet.data != null;
            GS2.Log($"Scanning {calcPlanet.name}" + (planetAlgorithm is GS2PlanetAlgorithm ? " (GS)" : ""));
            var highStopwatch = new HighStopwatch();
            highStopwatch.Begin();
            try
            {
                // A loaded planet keeps its data; the old code replaced it with fresh scan data.
                if (!hadData)
                {
                    calcPlanet.data = new PlanetRawData(calcPlanet.precision);
                    calcPlanet.modData = calcPlanet.data.InitModData(calcPlanet.modData);
                    calcPlanet.data.CalcVerts();
                    calcPlanet.aux = new PlanetAuxData(calcPlanet);
                }
                try
                {
                    planetAlgorithm.GenerateTerrain(calcPlanet.mod_x, calcPlanet.mod_y);
                }
                catch (Exception ex)
                {
                    Modeler.LogTerrainGenerationFailure(calcPlanet, planetAlgorithm, ex);
                    throw;
                }
                planetAlgorithm.CalcWaterPercent();
                double duration = highStopwatch.duration;

                highStopwatch.Begin();
                calcPlanet.data.vegeCursor = 1;
                if (calcPlanet.type != EPlanetType.Gas) planetAlgorithm.GenerateVegetables();
                double duration2 = highStopwatch.duration;

                highStopwatch.Begin();
                calcPlanet.data.veinCursor = 1;
                if (calcPlanet.type != EPlanetType.Gas) planetAlgorithm.GenerateVeins();
                calcPlanet.SummarizeVeinGroups();
                calcPlanet.GenBirthPoints();
                double duration3 = highStopwatch.duration;

                if (planetScanThreadLogs != null)
                    lock (planetScanThreadLogs)
                        planetScanThreadLogs.Add($"[Terrain]:{duration:F3}s [Vegetables]:{duration2:F3}s [Veins]:{duration3:F3}s  Planet: {calcPlanet.displayName}");
            }
            finally
            {
                // Free the scan data of non-local planets, also when generation fails.
                if (calcPlanet != GameMain.localPlanet && !hadData && calcPlanet.data != null)
                {
                    calcPlanet.data.Free();
                    calcPlanet.data = null;
                    if (calcPlanet.aux != null)
                    {
                        calcPlanet.aux.Free();
                        calcPlanet.aux = null;
                    }
                }
            }
            GS2.Log($"Finished calculating planet {calcPlanet.name}");
            calcPlanet.NotifyScanEnded();
            return false;
        }

        // ---- Theme resources: Unity objects made for a generated galaxy are destroyed at the next generation ----

        // Unity native objects survive managed GC. Track only objects created by a theme.
        static readonly Dictionary<GSTheme, List<Object>> owned = new();
        static readonly ConditionalWeakTable<GSAmbientSettings, TintCache> tints = new();
        sealed class TintCache { public Cubemap map; public Color tint; }

        public static T Own<T>(T resource, GSTheme theme) where T : Object
        {
            if (!ReferenceEquals(resource, null) && theme != null)
            {
                if (!owned.TryGetValue(theme, out var list)) owned[theme] = list = new List<Object>();
                list.Add(resource);
            }
            return resource;
        }

        // Theme variants share the immutable clip (and clones share the read-only cubemap) instead of copying it.
        public static T Share<T>(T resource) where T : Object => resource;

        public static Cubemap CachedTint(Cubemap map, Color tint, GSAmbientSettings settings, GSTheme theme)
        {
            var cache = tints.GetOrCreateValue(settings);
            if (cache.map == null || cache.tint != tint)
            {
                cache.map = Own(Utils.TintCubeMap(map, tint), theme);
                cache.tint = tint;
            }
            return cache.map;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GSSettings), nameof(GSSettings.Reset))]
        static void ReleaseGenerationResources()
        {
            var persistent = new HashSet<GSTheme>(ThemeLibrary.Vanilla().Values);
            if (GS2.externalThemes != null) persistent.UnionWith(GS2.externalThemes.Values);
            var shared = new HashSet<Object>();
            foreach (var theme in persistent)
            {
                if (theme == null) continue;
                if (owned.TryGetValue(theme, out var list)) shared.UnionWith(list);
                shared.UnionWith(new Object[] { theme.terrainMat, theme.oceanMat, theme.atmosMat, theme.nephogramMat, theme.cloudMat, theme.thumbMat, theme.minimapMat,
                    theme.ambientDesc, theme.ambientDesc?.reflectionMap, theme.AmbientSettings?.ReflectionMap });
            }
            foreach (var theme in new List<GSTheme>(owned.Keys))
            {
                if (persistent.Contains(theme)) continue;
                foreach (var resource in owned[theme])
                    // Instantiating an AmbientDesc component also clones its owning GameObject.
                    if (resource != null && !shared.Contains(resource)) Object.Destroy(resource is Component component ? component.gameObject : resource);
                owned.Remove(theme);
            }
        }

        static bool IsInstantiate(CodeInstruction c, Type type) =>
            c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.Name == "Instantiate" && m.DeclaringType == typeof(Object) && m.IsGenericMethod
            && m.GetParameters().Length == 1 && m.GetGenericArguments()[0] == type;

        static bool IsCall(CodeInstruction c, Type type, string name) =>
            c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.DeclaringType == type && m.Name == name;

        static MethodInfo OwnOf(Type t) => AccessTools.Method(typeof(GalacticScaleFixes), nameof(Own)).MakeGenericMethod(t);

        // After each matching call (result on the stack): ldarg.0 (the theme); call Own<T>.
        static List<CodeInstruction> OwnResults(IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, Type> resultType, int expected, string where)
        {
            var code = new List<CodeInstruction>(instructions);
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var type = resultType(code[i]);
                if (type == null) continue;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                code.Insert(i + 2, new CodeInstruction(OpCodes.Call, OwnOf(type)));
                i += 2;
                n++;
            }
            Il.Require(n == expected, where + " (" + n + " of " + expected + ")");
            return code;
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(GSTheme), nameof(GSTheme.CopyFrom))]
        static IEnumerable<CodeInstruction> CopyFrom(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int clips = 0;
            foreach (var c in code)
                if (IsInstantiate(c, typeof(AudioClip)))
                {
                    c.operand = AccessTools.Method(typeof(GalacticScaleFixes), nameof(Share)).MakeGenericMethod(typeof(AudioClip));
                    clips++;
                }
            Il.Require(clips == 1, "GSTheme.CopyFrom ambientSfx copy");
            return OwnResults(code, c => IsInstantiate(c, typeof(Material)) ? typeof(Material) : IsInstantiate(c, typeof(AmbientDesc)) ? typeof(AmbientDesc) : null, 8, "GSTheme.CopyFrom material copies");
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(GSTheme), nameof(GSTheme.CreateMaterial))]
        static IEnumerable<CodeInstruction> CreateMaterial(IEnumerable<CodeInstruction> instructions) =>
            OwnResults(instructions, c => IsInstantiate(c, typeof(Material)) ? typeof(Material) : IsCall(c, typeof(Utils), nameof(Utils.GetTextureFromFile)) ? typeof(Texture) : null, 3, "GSTheme.CreateMaterial copies");

        [HarmonyTranspiler, HarmonyPatch(typeof(GSTheme), nameof(GSTheme.ProcessTints))]
        static IEnumerable<CodeInstruction> ProcessTints(IEnumerable<CodeInstruction> instructions) =>
            OwnResults(instructions, c => IsCall(c, typeof(GSTheme), nameof(GSTheme.TintMaterial)) ? typeof(Material) : null, 2, "GSTheme.ProcessTints tinted materials");

        // The tinted reflection cubemap is made once per settings object and colour, not on every ToTheme.
        [HarmonyTranspiler, HarmonyPatch(typeof(GSAmbientSettings), nameof(GSAmbientSettings.ToTheme))]
        static IEnumerable<CodeInstruction> ToTheme(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int i = code.FindIndex(c => IsCall(c, typeof(Utils), nameof(Utils.TintCubeMap)));
            Il.Require(i >= 0 && Il.Count(code, c => IsCall(c, typeof(Utils), nameof(Utils.TintCubeMap))) == 1, "GSAmbientSettings.ToTheme TintCubeMap");
            code[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GalacticScaleFixes), nameof(CachedTint))).MoveLabelsFrom(code[i]);
            code.InsertRange(i, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1) });
            return code;
        }

        // Clones share the read-only cubemap (MemberwiseClone already copied the reference) instead of copying it.
        [HarmonyTranspiler, HarmonyPatch(typeof(GSAmbientSettings), nameof(GSAmbientSettings.Clone))]
        static IEnumerable<CodeInstruction> AmbientClone(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int n = 0;
            foreach (var c in code)
                if (IsInstantiate(c, typeof(Cubemap)))
                {
                    c.operand = AccessTools.Method(typeof(GalacticScaleFixes), nameof(Share)).MakeGenericMethod(typeof(Cubemap));
                    n++;
                }
            Il.Require(n == 1, "GSAmbientSettings.Clone cubemap copy");
            return code;
        }

        // ---- Save loading: a missing .gs2 settings file stops the load instead of continuing with stale settings ----

        // GS2.Import itself is not patched: in the game Harmony rebuilds it with the MethodBuilder backend, which fails on it.
        // Only saves load .gs2 files (the custom JSON generator and ForceImport use .json), so the check sits in the loader.
        [HarmonyPostfix, HarmonyPatch(typeof(GS2), nameof(GS2.LoadSettingsFromJson))]
        static void RequireSettings(bool __result, string path)
        {
            if (!__result && path != null && path.EndsWith(".gs2", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("GalacticScale settings file is missing.", path);
        }

        // ---- Error log: the full exception (type, inner exception, stack) ----

        [HarmonyPrefix, HarmonyPatch(typeof(GS2), nameof(GS2.DumpException))]
        static void FullException(Exception e) => GS2.Error(e.ToString());

        // ---- New game screen: one preview after a slider drag ends ----

        // UniverseGenTweaks' four sliders request a full galaxy on every drag tick.
        [HarmonyPrefix, HarmonyPatch(typeof(PatchOnUIGalaxySelect), nameof(PatchOnUIGalaxySelect.SetStarmapGalaxy))]
        static bool DeferWhileDragging(ref UIGalaxySelect __0, ref bool __result)
        {
            if (NebulaCompat.IsMultiplayerActive || __0.starmap?.galaxyData == null || !VFInput.rtsConfirm.pressing) return true;
            if (PatchOnUIGalaxySelect.delayer == null) PatchOnUIGalaxySelect.delayer = __0.starCountSlider.gameObject.AddComponent<PatchOnUIGalaxySelect.Delayer>();
            PatchOnUIGalaxySelect.delayer.Wait();
            __result = false;
            return false;
        }

        // The delayer waits for the pointer to be released (not a fixed second), restarts on every change and
        // does not regenerate after the new game screen is closed.
        [HarmonyPrefix, HarmonyPatch(typeof(PatchOnUIGalaxySelect.Delayer), nameof(PatchOnUIGalaxySelect.Delayer.Wait))]
        static bool Wait(PatchOnUIGalaxySelect.Delayer __instance)
        {
            __instance.StopAllCoroutines();
            __instance.active = true;
            __instance.StartCoroutine(CommitAfterRelease(__instance));
            return false;
        }

        static IEnumerator CommitAfterRelease(PatchOnUIGalaxySelect.Delayer delayer)
        {
            yield return new WaitUntil(() => !VFInput.rtsConfirm.pressing);
            if (!delayer.active) yield break;
            delayer.active = false;
            if (UIRoot.instance.galaxySelect.active) UIRoot.instance.galaxySelect.SetStarmapGalaxy();
        }

        // ---- New game screen: a press on a setting is not a click on the star behind it ----

        // GalacticScale takes the star nearest the cursor on every press, also a press on a slider or toggle of the settings;
        // the system view then opened under the other mods' settings rows.
        // GS clicks on every frame the button is held, at the cursor. Dragging a slider past the end of its bar put the cursor
        // over the map and opened a star system mid-drag, so a press that began on a setting stays off the map until released.
        [HarmonyPrefix, HarmonyPatch(typeof(SystemDisplay), nameof(SystemDisplay.OnStarMapClick))]
        static bool StarMapClick() => !PressOnControl() && !DragFromControl();

        static bool DragFromControl()
        {
            GameObject selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            return selected != null && selected.GetComponentInParent<UnityEngine.UI.Selectable>() != null;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SystemDisplay), nameof(SystemDisplay.OnStarMapRightClick))]
        static bool StarMapRightClick() => !PressOnControl();

        static readonly List<UnityEngine.EventSystems.RaycastResult> hits = new();

        static bool PressOnControl()
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events == null) return false;
            hits.Clear();
            events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = Input.mousePosition }, hits);
            foreach (var hit in hits)
                if (hit.gameObject.GetComponentInParent<UnityEngine.UI.Selectable>() != null) return true;
            return false;
        }

        // ---- Spray coaters on planets of other sizes ----

        // GalacticScale's Reshape packs the coater's bend for its shader with the planet radius (across*R + along*R*2R). The
        // shader unpacks it the game's way, with 200 and 400, so on every planet whose radius is not 200 the coater was drawn
        // bent and twisted, on a belt and off it. The bend itself is right: it is unpacked and packed again the game's way.
        [HarmonyPostfix, HarmonyPatch(typeof(SpraycoaterComponent), nameof(SpraycoaterComponent.Reshape))]
        static void CoaterShape(SpraycoaterComponent __instance, PlanetFactory factory, AnimData[] _animPool)
        {
            int id = __instance.entityId;
            if (factory?.planet == null || _animPool == null || id <= 0 || id >= _animPool.Length) return;
            Vector3 up = factory.entityPool[id].pos.normalized;
            if (up.x * up.x + up.z * up.z <= 1E-06f) return;   // at a pole Reshape writes nothing
            _animPool[id].prepare_length = Repack(_animPool[id].prepare_length, factory.planet.realRadius);
        }

        public static float Repack(float packed, float radius)
        {
            if (radius <= 0f || radius == 200f) return packed;
            float twice = radius * 2f;
            float along = Mathf.Round(packed / twice);
            float across = packed - along * twice;
            return Mathf.Round(across / radius * 200f) + Mathf.Round(along / radius * 200f) * 400f;
        }

        // Saves keep the packed bend, so coaters built before this fix are shaped again when their planet is loaded.
        [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.Import))]
        static void ReshapeLoadedCoaters(PlanetFactory __instance)
        {
            try
            {
                var traffic = __instance.cargoTraffic;
                if (traffic?.spraycoaterPool == null || __instance.planet == null || __instance.planet.realRadius == 200f) return;
                for (int i = 1; i < traffic.spraycoaterCursor; i++)
                    if (traffic.spraycoaterPool[i].id == i && traffic.spraycoaterPool[i].entityId > 0)
                        traffic.spraycoaterPool[i].Reshape(__instance, __instance.entityAnimPool);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Spray coaters keep their saved shape: " + e.Message); }
        }
    }
}
