namespace Jaket.Tools;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

using Jaket.IO;
using Jaket.Net;

/// <summary>
/// Dev tool that dumps the active scene hierarchy and the components revamp code cares about
/// into a Markdown file. Triggered via the /dump chat command or F7 from the Debug fragment.
/// </summary>
public static class SceneDumper
{
    /// <summary> Curated set of components surfaced in the stubs appendix. </summary>
    public static readonly Type[] Syncable =
    {
        typeof(ObjectActivator),
        typeof(ScriptActivator),
        typeof(ControllerPointer),
        typeof(Glass),
        typeof(StatueActivator),
        typeof(LimboSwitch),
        typeof(Flammable),
        typeof(ActivateArena),
        typeof(FinalDoor),
        typeof(ActivateNextWaveHP),
        typeof(Door),
        typeof(ItemTrigger),
        typeof(ItemPlaceZone),
    };

    /// <summary>
    /// Subset of Syncable that gets a node in the hierarchy tree. Position-keyed types
    /// (Glass, Flammable, etc.) are excluded because a single ActionType helper covers
    /// every instance and rendering them in-tree explodes the file (e.g. 2008 Flammables
    /// on enemy bones in 0-1). They still appear in the stubs section.
    /// </summary>
    private static readonly HashSet<Type> TreeKeep = new()
    {
        typeof(ObjectActivator),
        typeof(ScriptActivator),
        typeof(ControllerPointer),
        typeof(Door),
        typeof(ItemTrigger),
        typeof(ItemPlaceZone),
        typeof(ActivateNextWaveHP),
    };

    /// <summary> Max children rendered per parent in the tree before collapsing the tail. </summary>
    private const int CHILD_RENDER_CAP = 64;
    /// <summary> Defensive recursion limit. Real ULTRAKILL scenes don't approach this. </summary>
    private const int MAX_DEPTH = 32;
    /// <summary> Max positions listed per position-keyed stub group before collapsing. </summary>
    private const int POS_LIST_CAP = 8;

    /// <summary>
    /// Walks the active scene and writes a Markdown dump to Files.Logs/dumps/.
    /// Returns the absolute path of the file written, or null on refusal.
    /// </summary>
    public static string Dump(string sceneOverride = null)
    {
        string actual = Jaket.Tools.Tools.Scene;
        string friendly = sceneOverride ?? actual;
        if (actual == "Main Menu" || Pending != null)
        {
            Log.Warning("[DUMP] Refused: main menu or scene is still loading.");
            return null;
        }

        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        var stubs = new Dictionary<Type, List<StubEntry>>();
        var counts = new Dictionary<Type, int>();
        var classifierIndex = new List<Component>();
        foreach (var t in Syncable) stubs[t] = new();

        // Pass 1: collect every syncable instance for the stubs appendix and classifier index.
        foreach (var root in roots) WalkForStubs(root.transform, 0, stubs, counts, classifierIndex);

        // Pass 2: mark every subtree that contains a tree-worthy syncable.
        var interesting = new HashSet<Transform>();
        foreach (var root in roots) MarkInteresting(root.transform, 0, interesting);

        // Pass 3: emit the tree.
        var lines = new List<string>(8192);
        int inspected = 0;
        WriteHeader(lines, friendly, actual);
        int summaryLine = lines.Count - 2;
        var tree = new List<string>(4096);
        foreach (var root in roots)
            WalkEmit(root.transform, 0, tree, interesting, ref inspected, /*parentSyncable=*/false);

        int syncables = classifierIndex.Count;
        WriteClassifierIndex(lines, friendly, classifierIndex);
        lines.Add("");
        WriteStubAppendix(lines, stubs);
        lines.Add("");
        lines.Add("## Hierarchy (pruned to syncables + ancestors + direct neighbors)");
        lines.Add("");
        lines.Add("```");
        lines.AddRange(tree);
        lines.Add("```");
        lines.Add("");
        WriteComponentIndex(lines, counts);

        lines[summaryLine] = $"Roots: {roots.Length}   Inspected: {inspected}   Syncables: {syncables}";

        string dir = Files.Join(Files.Logs, "dumps");
        try { Files.MakeDir(dir); }
        catch (Exception ex) { Log.Error("[DUMP] Could not create dumps directory.", ex); return null; }

        string path = Files.Join(dir, $"{SafeName(friendly)}_{Log.Time.Replace(':', '.')}.md");
        Files.Append(path, lines);
        Log.Debug($"[DUMP] Wrote {path} ({lines.Count} lines, {syncables} syncables)");
        return path;
    }

    /// <summary> Whether a /dumpall pass is currently running. Prevents double-launch. </summary>
    public static bool DumpAllRunning { get; private set; }

    /// <summary>
    /// Cycles every known campaign scene, runs Dump() on each, then returns to the original.
    /// Single-player only — refuses inside a lobby because it would drag co-op players through
    /// every level. Runs as a coroutine on a DontDestroyOnLoad MonoBehaviour, so the caller
    /// returns immediately. Progress and errors land in the regular Jaket log.
    /// </summary>
    public static bool DumpAll()
    {
        if (DumpAllRunning) { Log.Warning("[DUMPALL] Already running."); return false; }
        if (LobbyController.Online) { Log.Warning("[DUMPALL] Refused: leave the lobby first."); return false; }
        if (Pending != null) { Log.Warning("[DUMPALL] Refused: scene is loading."); return false; }

        var scenes = BuildSceneList();
        var current = Jaket.Tools.Tools.Scene;
        var go = new GameObject("Jaket.SceneDumper.DumpAllRunner");
        Keep(go);
        go.AddComponent<DumpAllRunner>().Begin(scenes, current);
        DumpAllRunning = true;
        return true;
    }

    /// <summary>
    /// Every campaign scene known to exist as of ULTRAKILL's Fraud update.
    /// Update when new layer-8 levels ship. Sandbox, Cyber Grind, and Credits are
    /// excluded — they don't carry the revamp-relevant syncables we're looking for.
    /// </summary>
    private static List<string> BuildSceneList()
    {
        var s = new List<string>();
        // Layer 0 - Prelude / Limbo
        for (int n = 1; n <= 5; n++) s.Add($"Level 0-{n}");
        s.Add("Level 0-S"); s.Add("Level 0-E");
        // Layer 1 - Limbo
        for (int n = 1; n <= 4; n++) s.Add($"Level 1-{n}");
        s.Add("Level 1-S"); s.Add("Level 1-E");
        // Layer 2 - Lust
        for (int n = 1; n <= 4; n++) s.Add($"Level 2-{n}");
        s.Add("Level 2-S");
        // Layer 3 - Gluttony
        for (int n = 1; n <= 2; n++) s.Add($"Level 3-{n}");
        // Layer 4 - Greed
        for (int n = 1; n <= 4; n++) s.Add($"Level 4-{n}");
        s.Add("Level 4-S");
        // Layer 5 - Wrath
        for (int n = 1; n <= 4; n++) s.Add($"Level 5-{n}");
        s.Add("Level 5-S");
        // Layer 6 - Heresy
        for (int n = 1; n <= 2; n++) s.Add($"Level 6-{n}");
        // Layer 7 - Violence
        for (int n = 1; n <= 4; n++) s.Add($"Level 7-{n}");
        s.Add("Level 7-S");
        // Layer 8 - Fraud. Scenes that do not exist in the installed build are skipped.
        for (int n = 1; n <= 4; n++) s.Add($"Level 8-{n}");
        // Prime Sanctums
        s.Add("Level P-1"); s.Add("Level P-2");
        return s;
    }

    private class DumpAllRunner : MonoBehaviour
    {
        private const float LOAD_TIMEOUT = 60f;
        private const float SETTLE_SECONDS = 4f;
        private const float POST_DUMP_SECONDS = .35f;

        public void Begin(List<string> scenes, string back) => StartCoroutine(Run(scenes, back));

        private IEnumerator Run(List<string> scenes, string back)
        {
            int ok = 0, failed = 0;
            var manifest = new List<string>
            {
                "# DumpAll manifest",
                "",
                $"Started: {Log.Time}",
                $"Return scene: {back}",
                "",
                "| Scene | Status | Dump | Notes |",
                "|---|---|---|---|",
            };

            for (int i = 0; i < scenes.Count; i++)
            {
                string sc = scenes[i];
                Log.Info($"[DUMPALL] ({i + 1}/{scenes.Count}) loading {sc}");

                bool loadOk = true;
                try { SceneHelper.LoadScene(sc); }
                catch (Exception ex)
                {
                    Log.Error($"[DUMPALL] LoadScene threw for {sc}", ex);
                    manifest.Add($"| {sc} | skipped |  | LoadScene threw: {EscapeTable(ex.GetType().Name)} |");
                    loadOk = false;
                }
                if (!loadOk) { failed++; continue; }

                float t = 0f;
                while (Pending != null && t < LOAD_TIMEOUT) { t += Time.unscaledDeltaTime; yield return null; }
                if (Pending != null)
                {
                    Log.Warning($"[DUMPALL] {sc} timed out loading after {LOAD_TIMEOUT:0}s; stopping pass so later dumps are not mislabeled.");
                    manifest.Add($"| {sc} | timeout |  | Scene was still pending after {LOAD_TIMEOUT:0}s |");
                    failed++;
                    break;
                }

                yield return new WaitForSecondsRealtime(SETTLE_SECONDS);

                try
                {
                    var path = SceneDumper.Dump(sc);
                    if (path != null)
                    {
                        ok++;
                        manifest.Add($"| {sc} | ok | `{System.IO.Path.GetFileName(path)}` |  |");
                    }
                    else
                    {
                        Log.Warning($"[DUMPALL] Dump returned null for {sc}");
                        manifest.Add($"| {sc} | failed |  | Dump returned null |");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[DUMPALL] Dump threw for {sc}", ex);
                    manifest.Add($"| {sc} | failed |  | Dump threw: {EscapeTable(ex.GetType().Name)} |");
                    failed++;
                }

                yield return Resources.UnloadUnusedAssets();
                GC.Collect();
                yield return new WaitForSecondsRealtime(POST_DUMP_SECONDS);
            }

            Log.Info($"[DUMPALL] Done — {ok} ok, {failed} failed. Returning to {back}.");
            manifest.Add("");
            manifest.Add($"Finished: {Log.Time}");
            manifest.Add($"Succeeded: {ok}");
            manifest.Add($"Failed or skipped: {failed}");
            WriteDumpAllManifest(manifest);

            try { SceneHelper.LoadScene(back); } catch { /* best-effort */ }

            DumpAllRunning = false;
            Destroy(gameObject);
        }
    }

    #region walk

    /// <summary> Pass 1: collects every syncable component in the scene for the stubs appendix. </summary>
    private static void WalkForStubs(Transform t, int depth,
                                     Dictionary<Type, List<StubEntry>> stubs,
                                     Dictionary<Type, int> counts,
                                     List<Component> classifierIndex)
    {
        if (depth >= MAX_DEPTH) return;
        if (!IsReal(t.gameObject)) return;

        var comps = t.GetComponents(typeof(Component));
        foreach (var c in comps)
        {
            if (c == null) continue;
            var ct = c.GetType();
            foreach (var k in Syncable)
            {
                if (k.IsAssignableFrom(ct))
                {
                    stubs[k].Add(new(c.Path(), c.transform.position));
                    classifierIndex.Add(c);
                    CountAdd(counts, k);
                    break;
                }
            }
        }

        for (int i = 0; i < t.childCount; i++) WalkForStubs(t.GetChild(i), depth + 1, stubs, counts, classifierIndex);
    }

    /// <summary> Pass 2: returns true if t or any descendant carries a TreeKeep component. </summary>
    private static bool MarkInteresting(Transform t, int depth, HashSet<Transform> set)
    {
        if (depth >= MAX_DEPTH) return false;
        if (!IsReal(t.gameObject)) return false;

        bool here = HasTreeKeep(t);
        bool descendant = false;
        for (int i = 0; i < t.childCount; i++)
            if (MarkInteresting(t.GetChild(i), depth + 1, set)) descendant = true;

        if (here || descendant) set.Add(t);
        return here || descendant;
    }

    private static bool HasTreeKeep(Transform t)
    {
        var comps = t.GetComponents(typeof(Component));
        foreach (var c in comps)
        {
            if (c == null) continue;
            var ct = c.GetType();
            foreach (var k in TreeKeep) if (k.IsAssignableFrom(ct)) return true;
        }
        return false;
    }

    private static void WalkEmit(Transform t, int depth, List<string> tree,
                                 HashSet<Transform> interesting,
                                 ref int inspected,
                                 bool parentSyncable)
    {
        if (depth >= MAX_DEPTH) { tree.Add(Indent(depth) + "... (max depth)"); return; }
        if (!IsReal(t.gameObject)) return;

        bool thisInteresting = interesting.Contains(t);
        // Keep this node if it's interesting OR if its parent was syncable (sister-context).
        if (!thisInteresting && !parentSyncable) return;

        inspected++;

        var comps = t.GetComponents(typeof(Component));
        var tier1 = new List<Component>();
        bool thisSyncable = false;
        foreach (var c in comps)
        {
            if (c == null) continue;
            var ct = c.GetType();
            foreach (var k in TreeKeep)
            {
                if (k.IsAssignableFrom(ct)) { tier1.Add(c); thisSyncable = true; break; }
            }
        }

        // GameObject line
        var sb = new StringBuilder();
        sb.Append(Indent(depth));
        sb.Append(t.name);
        sb.Append(t.gameObject.activeSelf ? "  [active]" : "  [inactive]");
        if (tier1.Count > 0)
        {
            sb.Append("  ");
            for (int i = 0; i < tier1.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append('*').Append(SimpleName(tier1[i].GetType()));
            }
        }
        tree.Add(sb.ToString());

        foreach (var c in tier1) WriteTierOneFields(tree, depth + 1, c);

        // Recurse. Children are visited if they're interesting (will keep themselves)
        // or if THIS node is syncable (so the maintainer can see immediate siblings/children
        // referenced by toActivateObjects, like "Blockers/0").
        int childCount = t.childCount;
        int kept = 0, skipped = 0;
        if (childCount > CHILD_RENDER_CAP && !thisSyncable)
        {
            int head = CHILD_RENDER_CAP / 2;
            int tail = CHILD_RENDER_CAP - head;
            for (int i = 0; i < head; i++)
            {
                int before = tree.Count;
                WalkEmit(t.GetChild(i), depth + 1, tree, interesting, ref inspected, thisSyncable);
                if (tree.Count > before) kept++; else skipped++;
            }
            tree.Add(Indent(depth + 1) + $"... (+{childCount - head - tail} more children)");
            for (int i = childCount - tail; i < childCount; i++)
                WalkEmit(t.GetChild(i), depth + 1, tree, interesting, ref inspected, thisSyncable);
        }
        else
        {
            for (int i = 0; i < childCount; i++)
            {
                int before = tree.Count;
                WalkEmit(t.GetChild(i), depth + 1, tree, interesting, ref inspected, thisSyncable);
                if (tree.Count > before) kept++; else skipped++;
            }
            if (skipped > 0 && thisSyncable) tree.Add(Indent(depth + 1) + $"... (+{skipped} uninteresting children)");
        }
    }

    private static void WriteTierOneFields(List<string> tree, int depth, Component c)
    {
        string pad = Indent(depth) + "    ";

        if (c is ObjectActivator oa)
        {
            tree.Add(pad + $"delay: {oa.delay}");
            DumpEventArray(tree, pad, "toActivateObjects", oa.events?.toActivateObjects);
            DumpEventArray(tree, pad, "toDisActivateObjects", oa.events?.toDisActivateObjects);
            DumpUnityEvent(tree, pad, "onActivate", oa.events?.onActivate);
            DumpUnityEvent(tree, pad, "onDisActivate", oa.events?.onDisActivate);
            return;
        }
        if (c is ScriptActivator sa)
        {
            tree.Add(pad + $"pistons: {sa.pistons?.Length ?? 0}, lightpillars: {sa.lightpillars?.Length ?? 0}");
            return;
        }
        if (c is ControllerPointer cp)
        {
            tree.Add(pad + $"OnPressed: {ListenerSummary(cp.OnPressed)}");
            return;
        }
        if (c is ActivateNextWaveHP anw)
        {
            tree.Add(pad + $"health: {anw.health}");
            return;
        }
        if (c is ItemPlaceZone ipz)
        {
            tree.Add(pad + $"activateOnSuccess: {ipz.activateOnSuccess?.Length ?? 0}, deactivateOnSuccess: {ipz.deactivateOnSuccess?.Length ?? 0}");
            return;
        }
        // Door, ItemTrigger — path + position is all we render inline.
        tree.Add(pad + $"pos: {FormatXZ(c.transform.position)}");
    }

    private static void DumpEventArray(List<string> tree, string pad, string name, GameObject[] arr)
    {
        if (arr == null) { tree.Add(pad + $"{name}: null"); return; }
        tree.Add(pad + $"{name}[{arr.Length}]:");
        for (int i = 0; i < arr.Length; i++)
        {
            var o = arr[i];
            if (o == null) tree.Add(pad + $"  [{i}] <null>");
            else tree.Add(pad + $"  [{i}] {o.name,-24} ({(o.activeSelf ? "active" : "inactive")})    @ {FullPath(o.transform)}");
        }
    }

    private static void DumpUnityEvent(List<string> tree, string pad, string name, UnityEventBase ev)
    {
        if (ev == null) { tree.Add(pad + $"{name}: null"); return; }
        int n = 0;
        try { n = ev.GetPersistentEventCount(); }
        catch { tree.Add(pad + $"{name}: (unreadable)"); return; }

        tree.Add(pad + $"{name}: {n} persistent");
        for (int i = 0; i < n; i++)
        {
            string desc;
            try
            {
                var target = ev.GetPersistentTarget(i);
                var method = ev.GetPersistentMethodName(i);
                desc = target == null ? $"<null target>.{method}" : $"{target.name}.{method}";
            }
            catch { desc = "(unreadable)"; }
            tree.Add(pad + $"  [{i}] {desc}");
        }
    }

    private static string ListenerSummary(UnityEventBase ev)
    {
        if (ev == null) return "null";
        try { return $"{ev.GetPersistentEventCount()} persistent"; }
        catch { return "(unreadable)"; }
    }

    #endregion
    #region writing

    private static void WriteHeader(List<string> lines, string scene, string actual)
    {
        lines.Add($"# Scene Dump: {scene}");
        lines.Add($"Generated: {Log.Time}");
        if (actual != scene) lines.Add($"SceneHelper.CurrentScene: {actual}");
        lines.Add("Roots: ?   Inspected: ?   Syncables: ?"); // patched after walk
        lines.Add("");
    }

    private static void WriteClassifierIndex(List<string> lines, string scene, List<Component> components)
    {
        lines.Add("## Classifier Index");
        lines.Add("");
        lines.Add("One JSON object per syncable component. Runtime clones are preserved here so the classifier can decide whether they are noise.");
        lines.Add("");
        lines.Add("```jsonl");
        foreach (var c in components)
        {
            try { lines.Add(ClassifierJson(scene, c)); }
            catch (Exception ex)
            {
                Log.Warning($"[DUMP] Could not index {c?.name ?? "<null>"}: {ex.Message}");
            }
        }
        lines.Add("```");
    }

    private static void WriteStubAppendix(List<string> lines, Dictionary<Type, List<StubEntry>> stubs)
    {
        lines.Add("## Paste-ready stubs");
        lines.Add("");
        lines.Add("```csharp");

        foreach (var t in Syncable)
        {
            var entries = stubs[t];
            if (entries.Count == 0) continue;

            // Drop runtime-instantiated clones — their paths won't survive a scene reload.
            entries.RemoveAll(e => e.Path.Contains("(Clone)"));
            if (entries.Count == 0) continue;

            lines.Add($"// --- {SimpleName(t)} ({entries.Count}) ---");
            string helper = StubHelper(t);

            if (IsPositionKeyed(t))
            {
                lines.Add($"{helper};");
                int shown = Math.Min(entries.Count, POS_LIST_CAP);
                for (int i = 0; i < shown; i++) lines.Add($"//   pos: {FormatXZ(entries[i].Position)}");
                if (entries.Count > shown) lines.Add($"//   ... (+{entries.Count - shown} more)");
            }
            else if (helper != null)
            {
                // Dedupe by path; show count when > 1.
                var byPath = new Dictionary<string, int>();
                var order = new List<string>();
                foreach (var e in entries)
                {
                    if (!byPath.ContainsKey(e.Path)) { byPath[e.Path] = 0; order.Add(e.Path); }
                    byPath[e.Path]++;
                }
                foreach (var p in order)
                {
                    int n = byPath[p];
                    string suffix = n > 1 ? $"  // x{n}" : "";
                    lines.Add($"{helper}, \"{p}\");{suffix}");
                }
            }
            else
            {
                lines.Add($"// TODO: no ActionType.{SimpleName(t)} helper; write a custom Find lambda.");
                int shown = Math.Min(entries.Count, POS_LIST_CAP);
                for (int i = 0; i < shown; i++) lines.Add($"//   path: \"{entries[i].Path}\"   pos: {FormatXZ(entries[i].Position)}");
                if (entries.Count > shown) lines.Add($"//   ... (+{entries.Count - shown} more)");
            }
            lines.Add("");
        }

        lines.Add("```");
    }

    private static void WriteComponentIndex(List<string> lines, Dictionary<Type, int> counts)
    {
        lines.Add("## Component Index");
        lines.Add("");
        lines.Add("| Type | Count |");
        lines.Add("|---|---|");
        foreach (var t in Syncable)
        {
            counts.TryGetValue(t, out int c);
            lines.Add($"| {SimpleName(t)} | {c} |");
        }
    }

    private static void WriteDumpAllManifest(List<string> lines)
    {
        string dir = Files.Join(Files.Logs, "dumps");
        try
        {
            Files.MakeDir(dir);
            Files.Append(Files.Join(dir, $"_dumpall_{Log.Time.Replace(':', '.')}.md"), lines);
        }
        catch (Exception ex)
        {
            Log.Error("[DUMPALL] Could not write manifest.", ex);
        }
    }

    #endregion
    #region helpers

    private static string ClassifierJson(string scene, Component c)
    {
        var t = c.transform;
        var p = t.position;
        var type = SimpleName(c.GetType());
        var path = c.Path();
        var fullPath = FullPath(t);

        var sb = new StringBuilder(1024);
        bool comma = false;
        sb.Append('{');
        JsonProp(sb, ref comma, "scene", scene);
        JsonProp(sb, ref comma, "type", type);
        JsonProp(sb, ref comma, "helper", ActionHelperName(c.GetType()));
        JsonProp(sb, ref comma, "path", path);
        JsonProp(sb, ref comma, "fullPath", fullPath);
        JsonProp(sb, ref comma, "name", c.name);
        JsonProp(sb, ref comma, "parent", t.parent ? t.parent.name : null);
        JsonBoolProp(sb, ref comma, "activeSelf", c.gameObject.activeSelf);
        JsonBoolProp(sb, ref comma, "activeInHierarchy", c.gameObject.activeInHierarchy);
        JsonBoolProp(sb, ref comma, "clone", ContainsClone(path) || ContainsClone(fullPath));
        JsonFloatProp(sb, ref comma, "x", p.x);
        JsonFloatProp(sb, ref comma, "y", p.y);
        JsonFloatProp(sb, ref comma, "z", p.z);

        if (c is ObjectActivator oa)
        {
            JsonFloatProp(sb, ref comma, "delay", oa.delay);
            JsonRawProp(sb, ref comma, "toActivateObjects", GameObjectArrayJson(oa.events?.toActivateObjects));
            JsonRawProp(sb, ref comma, "toDisActivateObjects", GameObjectArrayJson(oa.events?.toDisActivateObjects));
            JsonRawProp(sb, ref comma, "onActivate", UnityEventJson(oa.events?.onActivate));
            JsonRawProp(sb, ref comma, "onDisActivate", UnityEventJson(oa.events?.onDisActivate));
        }
        else if (c is ScriptActivator sa)
        {
            JsonIntProp(sb, ref comma, "pistons", sa.pistons?.Length ?? 0);
            JsonIntProp(sb, ref comma, "lightpillars", sa.lightpillars?.Length ?? 0);
        }
        else if (c is ControllerPointer cp)
        {
            JsonRawProp(sb, ref comma, "onPressed", UnityEventJson(cp.OnPressed));
        }
        else if (c is ActivateNextWaveHP anw)
        {
            JsonFloatProp(sb, ref comma, "health", anw.health);
        }
        else if (c is ItemPlaceZone ipz)
        {
            JsonRawProp(sb, ref comma, "activateOnSuccess", GameObjectArrayJson(ipz.activateOnSuccess));
            JsonRawProp(sb, ref comma, "deactivateOnSuccess", GameObjectArrayJson(ipz.deactivateOnSuccess));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string GameObjectArrayJson(GameObject[] arr)
    {
        if (arr == null) return "null";
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < arr.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var o = arr[i];
            if (o == null) { sb.Append("null"); continue; }

            bool comma = false;
            var fullPath = FullPath(o.transform);
            sb.Append('{');
            JsonIntProp(sb, ref comma, "index", i);
            JsonProp(sb, ref comma, "name", o.name);
            JsonProp(sb, ref comma, "fullPath", fullPath);
            JsonBoolProp(sb, ref comma, "activeSelf", o.activeSelf);
            JsonBoolProp(sb, ref comma, "activeInHierarchy", o.activeInHierarchy);
            JsonBoolProp(sb, ref comma, "clone", ContainsClone(fullPath));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string UnityEventJson(UnityEventBase ev)
    {
        if (ev == null) return "null";

        int n;
        try { n = ev.GetPersistentEventCount(); }
        catch { return "[{\"unreadable\":true}]"; }

        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');

            bool comma = false;
            var target = ev.GetPersistentTarget(i);
            var method = ev.GetPersistentMethodName(i);
            var targetTransform = TargetTransform(target);

            sb.Append('{');
            JsonIntProp(sb, ref comma, "index", i);
            JsonProp(sb, ref comma, "target", target ? target.name : null);
            JsonProp(sb, ref comma, "targetType", target ? SimpleName(target.GetType()) : null);
            JsonProp(sb, ref comma, "targetFullPath", targetTransform ? FullPath(targetTransform) : null);
            JsonProp(sb, ref comma, "method", method);
            JsonBoolProp(sb, ref comma, "clone", targetTransform && ContainsClone(FullPath(targetTransform)));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static Transform TargetTransform(UnityEngine.Object target)
    {
        if (target is Component c) return c.transform;
        if (target is GameObject g) return g.transform;
        return null;
    }

    private static string ActionHelperName(Type t)
    {
        if (t == typeof(ObjectActivator))   return "Act";
        if (t == typeof(ScriptActivator))   return "Scr";
        if (t == typeof(ControllerPointer)) return "Btn";
        if (t == typeof(Glass))             return "Window";
        if (t == typeof(StatueActivator))   return "Statue";
        if (t == typeof(LimboSwitch))       return "Switch";
        if (t == typeof(Flammable))         return "Flammable";
        if (t == typeof(ActivateArena))     return "Arena";
        if (t == typeof(FinalDoor))         return "Final";
        return null;
    }

    private static bool ContainsClone(string s) => s != null && s.Contains("(Clone)");

    private static string EscapeTable(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static void JsonComma(StringBuilder sb, ref bool comma)
    {
        if (comma) sb.Append(',');
        else comma = true;
    }

    private static void JsonProp(StringBuilder sb, ref bool comma, string name, string value)
    {
        JsonComma(sb, ref comma);
        JsonString(sb, name);
        sb.Append(':');
        JsonString(sb, value);
    }

    private static void JsonRawProp(StringBuilder sb, ref bool comma, string name, string value)
    {
        JsonComma(sb, ref comma);
        JsonString(sb, name);
        sb.Append(':');
        sb.Append(value ?? "null");
    }

    private static void JsonBoolProp(StringBuilder sb, ref bool comma, string name, bool value)
    {
        JsonComma(sb, ref comma);
        JsonString(sb, name);
        sb.Append(':');
        sb.Append(value ? "true" : "false");
    }

    private static void JsonIntProp(StringBuilder sb, ref bool comma, string name, int value)
    {
        JsonComma(sb, ref comma);
        JsonString(sb, name);
        sb.Append(':');
        sb.Append(value);
    }

    private static void JsonFloatProp(StringBuilder sb, ref bool comma, string name, float value)
    {
        JsonComma(sb, ref comma);
        JsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString("0.####", CultureInfo.InvariantCulture));
    }

    private static void JsonString(StringBuilder sb, string value)
    {
        if (value == null) { sb.Append("null"); return; }

        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    private readonly struct StubEntry
    {
        public readonly string Path;
        public readonly Vector3 Position;
        public StubEntry(string path, Vector3 pos) { Path = path; Position = pos; }
    }

    private static bool IsPositionKeyed(Type t) =>
        t == typeof(Glass) || t == typeof(StatueActivator) || t == typeof(LimboSwitch) ||
        t == typeof(Flammable) || t == typeof(ActivateArena) || t == typeof(FinalDoor);

    private static string StubHelper(Type t)
    {
        if (t == typeof(ObjectActivator))    return "ActionType.Act(l";
        if (t == typeof(ScriptActivator))    return "ActionType.Scr(l";
        if (t == typeof(ControllerPointer))  return "ActionType.Btn(l";
        if (t == typeof(Glass))              return "ActionType.Window(l)";
        if (t == typeof(StatueActivator))    return "ActionType.Statue(l)";
        if (t == typeof(LimboSwitch))        return "ActionType.Switch(l)";
        if (t == typeof(Flammable))          return "ActionType.Flammable(l)";
        if (t == typeof(ActivateArena))      return "ActionType.Arena(l)";
        if (t == typeof(FinalDoor))          return "ActionType.Final(l)";
        return null;
    }

    private static string Indent(int depth) => new(' ', depth * 3);

    private static string FullPath(Transform t)
    {
        if (t == null) return "<null>";
        var parts = new List<string>(8);
        for (var cur = t; cur != null; cur = cur.parent) parts.Add(cur.name);
        parts.Reverse();
        return string.Join('/', parts);
    }

    private static string FormatXZ(Vector3 p) => $"({p.x:0.##}, {p.z:0.##})";

    private static string SimpleName(Type t) => t.Name;

    private static void CountAdd(Dictionary<Type, int> dict, Type t)
    {
        dict.TryGetValue(t, out int c);
        dict[t] = c + 1;
    }

    private static string SafeName(string scene)
    {
        if (string.IsNullOrEmpty(scene)) return "scene";
        var sb = new StringBuilder(scene.Length);
        foreach (var ch in scene) sb.Append(" /\\:<>\"|?*".IndexOf(ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    #endregion
}
