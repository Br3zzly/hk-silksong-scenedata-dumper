using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HKSilksongSceneloadDumper
{
    [BepInPlugin("com.you.worlddump", "WorldDump", "2.4.0")]
    public class HKSilksongSceneloadDumper : BaseUnityPlugin
    {
        private string dumpPath;
        private bool isScanning = false;
        private bool suppressOnSceneLoaded = false;
        private string bundlesRootAbsolute;

        // overlay/debug ui
        private int _totalBundles = 0;
        private int _currentBundleIndex = 0;
        private string _currentBundlePath = "";
        private string _currentSceneName = "";
        private string _currentStatus = "Idle (waiting for F7)";
        private bool _overlayActive = false;

        private List<string> _allBundleFilesSorted;

        private void Awake()
        {
            dumpPath = Path.Combine(Paths.GameRootPath, "AllSceneDataDump.csv");
            if (!File.Exists(dumpPath))
            {
                File.WriteAllText(dumpPath,
                    "Category,SceneNameSaveFile,ID,Value,Mutator,IsSemiPersistent,LoadedUnityScene\n");
            }

            SceneManager.sceneLoaded += OnSceneLoaded;

            bundlesRootAbsolute = Path.Combine(
                Application.dataPath,
                "StreamingAssets",
                "aa",
                "StandaloneWindows64"
            );

            Logger.LogInfo("[WorldDump] Ready. Press F7 anytime (try from title BEFORE loading a save).");
        }

        private void Update()
        {
            if (!isScanning && Input.GetKeyDown(KeyCode.F7))
            {
                isScanning = true;
                _overlayActive = true;
                _currentStatus = "Starting scan";

                Logger.LogInfo("[WorldDump] F7 pressed. Starting full scan from current state.");
                StartCoroutine(RunFullBundleScan());
            }
        }

        private void OnGUI()
        {
            // Always show overlay, even when idle.

            // Pick a size for the box.
            float boxWidth = 480f;
            float boxHeight = 150f;

            // Anchor top-right with a 10px margin.
            float x = Screen.width - boxWidth - 10f;
            float y = 10f;

            // Style
            GUI.color = Color.white;
            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.alignment = TextAnchor.UpperLeft;
            boxStyle.fontSize = 14;
            boxStyle.normal.textColor = Color.white;
            boxStyle.wordWrap = true;

            // Build display text.
            // If not scanning yet, show instructions.
            // If scanning, show live progress.
            string header = "WorldDump Scanner";
            string pressHint = "Press F7 to start full scan (best from main menu)";
            string body;

            if (!isScanning)
            {
                body =
                    $"{pressHint}\n\n" +
                    $"Status: {_currentStatus}\n" +
                    $"Last Bundle: {_currentBundleIndex}/{_totalBundles}\n" +
                    $"Last Scene:  {_currentSceneName}\n";
            }
            else
            {
                body =
                    $"{pressHint}\n\n" +
                    $"Scanning...\n" +
                    $"Bundle: {_currentBundleIndex}/{_totalBundles}\n" +
                    $"Scene:  {_currentSceneName}\n" +
                    $"Status: {_currentStatus}\n";
            }

            string content = $"{header}\n{body}";

            GUI.Box(new Rect(x, y, boxWidth, boxHeight), content, boxStyle);
        }


        private IEnumerator RunFullBundleScan()
        {
            // gather bundle files (sorted for stability between runs)
            if (!Directory.Exists(bundlesRootAbsolute))
            {
                Logger.LogError("[WorldDump] Bundle root not found: " + bundlesRootAbsolute);
                _currentStatus = "ERROR: no bundle dir";
                isScanning = false;
                _overlayActive = false;
                yield break;
            }

            if (_allBundleFilesSorted == null)
            {
                string[] raw;
                try
                {
                    raw = Directory.GetFiles(
                        bundlesRootAbsolute,
                        "*.bundle",
                        SearchOption.AllDirectories
                    );
                }
                catch (Exception e)
                {
                    Logger.LogError("[WorldDump] Could not list bundles: " + e);
                    _currentStatus = "ERROR: list bundles";
                    isScanning = false;
                    _overlayActive = false;
                    yield break;
                }

                Array.Sort(raw, StringComparer.OrdinalIgnoreCase);
                _allBundleFilesSorted = new List<string>(raw);
            }

            _totalBundles = _allBundleFilesSorted.Count;
            Logger.LogInfo($"[WorldDump] Found {_totalBundles} bundles total.");

            // track which scenes we've already dumped in THIS run
            HashSet<string> dumpedScenesThisRun =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // remember whatever scene is currently active (title/menu or whatever)
            Scene originalActiveScene = SceneManager.GetActiveScene();
            string originalActiveSceneName = originalActiveScene.name;

            suppressOnSceneLoaded = true;

            for (int i = 0; i < _allBundleFilesSorted.Count; i++)
            {
                _currentBundleIndex = i;
                _currentBundlePath = _allBundleFilesSorted[i];
                _currentSceneName = "";
                _currentStatus = "Loading bundle";

                string bundlePath = _allBundleFilesSorted[i];
                Logger.LogInfo($"[WorldDump] [{i}/{_totalBundles}] Bundle: {bundlePath}");

                AssetBundle bundle = null;
                bool failedToLoad = false;

                try
                {
                    bundle = AssetBundle.LoadFromFile(bundlePath);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[WorldDump] Failed to load bundle '{bundlePath}': {e}");
                    _currentStatus = "bundle load fail";
                    failedToLoad = true;
                }

                if (failedToLoad)
                {
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }

                if (bundle == null)
                {
                    Logger.LogWarning($"[WorldDump] AssetBundle.LoadFromFile null for '{bundlePath}'");
                    _currentStatus = "bundle null";
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }

                string[] scenePathsFromBundle;
                try
                {
                    scenePathsFromBundle = bundle.GetAllScenePaths();
                }
                catch (Exception e)
                {
                    Logger.LogWarning("[WorldDump] GetAllScenePaths threw: " + e);
                    _currentStatus = "paths error";
                    scenePathsFromBundle = Array.Empty<string>();
                }

                if (scenePathsFromBundle == null || scenePathsFromBundle.Length == 0)
                {
                    Logger.LogInfo("[WorldDump] Bundle has 0 scene(s).");
                    _currentStatus = "0 scenes";
                    SafeUnloadBundle(bundle);
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }

                Logger.LogInfo($"[WorldDump] Bundle has {scenePathsFromBundle.Length} scene(s).");
                _currentStatus = $"scenes: {scenePathsFromBundle.Length}";

                for (int s = 0; s < scenePathsFromBundle.Length; s++)
                {
                    string fullScenePath = scenePathsFromBundle[s];
                    string shortSceneName = Path.GetFileNameWithoutExtension(fullScenePath);

                    _currentSceneName = shortSceneName;
                    _currentStatus = "Loading scene";

                    // skip if this scene already dumped
                    if (dumpedScenesThisRun.Contains(shortSceneName))
                    {
                        Logger.LogInfo($"[WorldDump]   -> already dumped '{shortSceneName}', skipping.");
                        _currentStatus = "already dumped";
                        continue;
                    }

                    // skip menu / credits / travel hub stuff that we KNOW hard-breaks scanning
                    if (IsDangerousBootstrapScene(shortSceneName))
                    {
                        Logger.LogInfo($"[WorldDump]   -> skipping bootstrap '{shortSceneName}'");
                        _currentStatus = "skipped (bootstrap)";
                        continue;
                    }

                    // load / dump / unload with timeouts
                    yield return StartCoroutine(
                        LoadDumpUnloadSceneDirect(
                            shortSceneName,
                            fullScenePath,
                            originalActiveScene,
                            originalActiveSceneName,
                            dumpedScenesThisRun
                        )
                    );

                    // after each scene attempt, kill any global "loading screen" objects
                    CleanupGlobalLoadingScreens();

                    // slight pacing
                    yield return new WaitForSeconds(0.05f);
                }

                SafeUnloadBundle(bundle);
                _currentStatus = "bundle done";
                yield return new WaitForSeconds(0.1f);
            }

            suppressOnSceneLoaded = false;
            isScanning = false;
            _currentStatus = "Full scan complete";
            Logger.LogInfo("[WorldDump] Full scan complete.");
        }

        private IEnumerator LoadDumpUnloadSceneDirect(
            string sceneShortName,
            string sceneFullPath,
            Scene originalActiveScene,
            string originalActiveSceneName,
            HashSet<string> dumpedScenesThisRun
        )
        {
            Logger.LogInfo($"[WorldDump]   -> Loading scene '{sceneShortName}'");

            AsyncOperation loadOp = null;
            bool usedShort = false;

            // try short name first
            try
            {
                loadOp = SceneManager.LoadSceneAsync(sceneShortName, LoadSceneMode.Additive);
                usedShort = true;
            }
            catch (Exception eShort)
            {
                Logger.LogInfo($"[WorldDump]   LoadSceneAsync(short) threw for {sceneShortName}: {eShort}");
            }

            // fallback full path
            if (loadOp == null)
            {
                try
                {
                    loadOp = SceneManager.LoadSceneAsync(sceneFullPath, LoadSceneMode.Additive);
                    usedShort = false;
                }
                catch (Exception eFull)
                {
                    Logger.LogWarning($"[WorldDump]   LoadSceneAsync(full) threw for {sceneFullPath}: {eFull}");
                }
            }

            if (loadOp == null)
            {
                Logger.LogWarning($"[WorldDump]   Could not start load for '{sceneShortName}' or '{sceneFullPath}'");
                _currentStatus = "load failed(start)";
                yield break;
            }

            // wait for load, but bail if it stalls >2s (curtain etc)
            float loadStart = Time.realtimeSinceStartup;
            while (!loadOp.isDone)
            {
                if (Time.realtimeSinceStartup - loadStart > 2f)
                {
                    Logger.LogWarning($"[WorldDump]   Load timeout '{sceneShortName}', skipping dump.");
                    _currentStatus = "load timeout";
                    yield break;
                }
                yield return null;
            }

            // let scene Awake/Start finish one frame
            yield return null;
            yield return new WaitForSeconds(0.05f);

            // figure which scene struct we just loaded
            Scene loadedSceneObj = SceneManager.GetSceneByName(
                usedShort ? sceneShortName : Path.GetFileNameWithoutExtension(sceneFullPath)
            );
            if ((!loadedSceneObj.IsValid() || !loadedSceneObj.isLoaded) && usedShort)
            {
                loadedSceneObj = SceneManager.GetSceneByName(Path.GetFileNameWithoutExtension(sceneFullPath));
            }

            if (loadedSceneObj.IsValid() && loadedSceneObj.isLoaded)
            {
                _currentStatus = "dumping";

                DumpSingleSceneOnly(loadedSceneObj);
                dumpedScenesThisRun.Add(loadedSceneObj.name);

                Logger.LogInfo($"[WorldDump]   Dumped '{loadedSceneObj.name}'");
                _currentStatus = "dumped OK";

                // try unload unless it's literally our original scene
                if (!string.Equals(loadedSceneObj.name, originalActiveSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInfo($"[WorldDump]   Unloading '{loadedSceneObj.name}'");
                    _currentStatus = "unloading";

                    yield return SafeUnloadSceneIfLoaded(loadedSceneObj);
                }
                else
                {
                    Logger.LogInfo($"[WorldDump]   Skipping unload for active '{loadedSceneObj.name}'");
                }
            }
            else
            {
                Logger.LogWarning($"[WorldDump]   Scene '{sceneShortName}' didn't end up loaded? Skipping dump.");
                _currentStatus = "not loaded?";
            }

            // try to restore whatever was active (menu/title)
            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
            {
                SceneManager.SetActiveScene(originalActiveScene);
            }

            yield return new WaitForSeconds(0.05f);
        }

        private IEnumerator SafeUnloadSceneIfLoaded(Scene sceneToUnload)
        {
            if (!sceneToUnload.IsValid() || !sceneToUnload.isLoaded)
                yield break;

            AsyncOperation unloadOp = null;
            try
            {
                unloadOp = SceneManager.UnloadSceneAsync(sceneToUnload);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"[WorldDump]   Unload start threw for '{sceneToUnload.name}': {e}");
                _currentStatus = "unload fail(start)";
                yield break;
            }

            if (unloadOp == null)
            {
                Logger.LogWarning($"[WorldDump]   UnloadSceneAsync null for '{sceneToUnload.name}'");
                _currentStatus = "unload null";
                yield break;
            }

            float start = Time.realtimeSinceStartup;
            while (!unloadOp.isDone)
            {
                if (Time.realtimeSinceStartup - start > 2f)
                {
                    Logger.LogWarning($"[WorldDump]   Unload timeout '{sceneToUnload.name}', continue.");
                    _currentStatus = "unload timeout";
                    break;
                }
                yield return null;
            }
        }

        private void SafeUnloadBundle(AssetBundle bundle)
        {
            try
            {
                bundle.Unload(false);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[WorldDump] bundle.Unload threw: " + e);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // When not scanning (normal play), we still dump automatically.
            if (suppressOnSceneLoaded)
                return;

            try
            {
                DumpSingleSceneOnly(scene);
                Logger.LogInfo($"[WorldDump] (live load) {scene.name} dumped.");
            }
            catch (Exception e)
            {
                Logger.LogError($"[WorldDump] Dump failed in live scene {scene.name}: {e}");
            }
        }

        // This nukes global "loading curtain" style objects between scenes.
        // We don't know exact class name, so we go by heuristic: any root that looks like loading / transition UI.
        // It only touches DontDestroyOnLoad roots (so we don't trash legit room content we just loaded/unloaded).
        private void CleanupGlobalLoadingScreens()
        {
            try
            {
                // Unity doesn't expose DontDestroyOnLoad scene directly,
                // but we can find all active root GameObjects and look for suspect names.
                // We'll destroy ones that look like global black fade / loading overlays.
                string[] suspectSubs =
                {
                    "loading",
                    "loadingscreen",
                    "loading_screen",
                    "transition",
                    "fade",
                    "blackout"
                };

                var allRoots = Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(go =>
                    {
                        // we only want persistent globals, not inactive prefabs
                        if (!go.scene.IsValid()) return false; // usually means it's hidden (like prefab asset or internal)
                        if (!go.activeInHierarchy) return false;

                        // we only want DontDestroyOnLoad-style or globally persistent stuff,
                        // i.e. scene.name == null/empty or special manager scenes. Easy heuristic:
                        // we'll just allow all and filter by name match.
                        return true;
                    })
                    .ToArray();

                foreach (var go in allRoots)
                {
                    string nm = go.name.ToLowerInvariant();
                    bool match = false;
                    for (int i = 0; i < suspectSubs.Length; i++)
                    {
                        if (nm.Contains(suspectSubs[i]))
                        {
                            match = true;
                            break;
                        }
                    }

                    if (match)
                    {
                        // Destroy the root. This should remove any persistent black overlay / curtain UI.
                        Logger.LogInfo("[WorldDump] Cleanup: destroying possible loading screen object '" + go.name + "'");
                        GameObject.Destroy(go);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[WorldDump] CleanupGlobalLoadingScreens error: " + e);
            }
        }

        // We skip scenes we know are "state managers", "cinematics", or "travel hubs"
        // that either (1) dump 0 items or (2) try to hijack global flow / boot menu / credits
        private bool IsDangerousBootstrapScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
                return false;

            string n = sceneName.ToLowerInvariant();

            // Stuff that re-inits menu / quits to menu / cinematic sequences / credits.
            if (n.StartsWith("menu_")) return true;
            if (n.StartsWith("pre_menu")) return true;
            if (n.StartsWith("quit_to_menu")) return true;
            if (n.StartsWith("opening_sequence")) return true;
            if (n.StartsWith("cinematic_")) return true;
            if (n.Contains("credits")) return true;
            if (n.Contains("end_game_completion")) return true;
            if (n.StartsWith("room_caravan_")) return true;

            return false;
        }

        private void DumpSingleSceneOnly(Scene scene)
        {
            string currentSceneName = scene.name;

            var boolItems = new List<PersistentBoolItem>();
            var intItems = new List<PersistentIntItem>();
            var rockItems = new List<GeoRock>();

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                boolItems.AddRange(root.GetComponentsInChildren<PersistentBoolItem>(true));
                intItems.AddRange(root.GetComponentsInChildren<PersistentIntItem>(true));
                rockItems.AddRange(root.GetComponentsInChildren<GeoRock>(true));
            }

            foreach (var item in boolItems)
            {
                try
                {
                    var data = item.ItemData;
                    AppendRow(
                        "persistentBool",
                        data.SceneName,
                        data.ID,
                        data.Value.ToString(),
                        data.Mutator.ToString(),
                        data.IsSemiPersistent.ToString(),
                        currentSceneName
                    );
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[WorldDump] BoolItem err in {currentSceneName}: {e}");
                }
            }

            foreach (var item in intItems)
            {
                try
                {
                    var data = item.ItemData;
                    AppendRow(
                        "persistentInt",
                        data.SceneName,
                        data.ID,
                        data.Value.ToString(),
                        data.Mutator.ToString(),
                        data.IsSemiPersistent.ToString(),
                        currentSceneName
                    );
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[WorldDump] IntItem err in {currentSceneName}: {e}");
                }
            }

            foreach (var rock in rockItems)
            {
                try
                {
                    GeoRockData data = rock.geoRockData;
                    if (data != null)
                    {
                        AppendRow(
                            "geoRock",
                            data.sceneName,
                            data.id,
                            data.hitsLeft.ToString(),
                            "",
                            "",
                            currentSceneName
                        );
                    }
                    else
                    {
                        Logger.LogWarning($"[WorldDump] GeoRock null data in {currentSceneName}");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[WorldDump] GeoRock err in {currentSceneName}: {e}");
                }
            }

            Logger.LogInfo(
                $"[WorldDump] {currentSceneName}: " +
                $"{boolItems.Count} bools, {intItems.Count} ints, {rockItems.Count} rocks."
            );
        }

        private void AppendRow(
            string category,
            string sceneNameInData,
            string id,
            string value,
            string mutator,
            string semi,
            string loadedUnityScene
        )
        {
            string Safe(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace(",", ";");

            string line =
                $"{Safe(category)}," +
                $"{Safe(sceneNameInData)}," +
                $"{Safe(id)}," +
                $"{Safe(value)}," +
                $"{Safe(mutator)}," +
                $"{Safe(semi)}," +
                $"{Safe(loadedUnityScene)}\n";

            try
            {
                File.AppendAllText(dumpPath, line);
            }
            catch (Exception e)
            {
                Logger.LogError($"[WorldDump] CSV write fail for {id} in {loadedUnityScene}: {e}");
                _currentStatus = "CSV write error";
            }
        }
    }
}
