namespace Jaket.Tools;

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

using Jaket.IO;

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
    public static string Dump()
    {
        string friendly = Jaket.Tools.Tools.Scene; // SceneHelper.CurrentScene — "Level 0-1" rather than asset hash
        if (friendly == "Main Menu" || Pending != null)
        {
            Log.Warning("[DUMP] Refused: main menu or scene is still loading.");
            return null;
        }

        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        var stubs = new Dictionary<Type, List<StubEntry>>();
        var counts = new Dictionary<Type, int>();
        foreach (var t in Syncable) stubs[t] = new();

        // Pass 1: collect every syncable instance for the stubs appendix.
        foreach (var root in roots) WalkForStubs(root.transform, 0, stubs, counts);

        // Pass 2: mark every subtree that contains a tree-worthy syncable.
        var interesting = new HashSet<Transform>();
        foreach (var root in roots) MarkInteresting(root.transform, 0, interesting);

        // Pass 3: emit the tree.
        var lines = new List<string>(8192);
        int inspected = 0;
        WriteHeader(lines, friendly);
        var tree = new List<string>(4096);
        foreach (var root in roots)
            WalkEmit(root.transform, 0, tree, interesting, ref inspected, /*parentSyncable=*/false);

        WriteStubAppendix(lines, stubs);
        lines.Add("");
        lines.Add("## Hierarchy (pruned to syncables + ancestors + direct neighbors)");
        lines.Add("");
        lines.Add("```");
        lines.AddRange(tree);
        lines.Add("```");
        lines.Add("");
        WriteComponentIndex(lines, counts);

        int syncables = 0;
        foreach (var s in stubs.Values) syncables += s.Count;
        lines[2] = $"Roots: {roots.Length}   Inspected: {inspected}   Syncables: {syncables}";

        string dir = Files.Join(Files.Logs, "dumps");
        try { Files.MakeDir(dir); }
        catch (Exception ex) { Log.Error("[DUMP] Could not create dumps directory.", ex); return null; }

        string path = Files.Join(dir, $"{SafeName(friendly)}_{Log.Time.Replace(':', '.')}.md");
        Files.Append(path, lines);
        Log.Debug($"[DUMP] Wrote {path} ({lines.Count} lines, {syncables} syncables)");
        return path;
    }

    #region walk

    /// <summary> Pass 1: collects every syncable component in the scene for the stubs appendix. </summary>
    private static void WalkForStubs(Transform t, int depth,
                                     Dictionary<Type, List<StubEntry>> stubs,
                                     Dictionary<Type, int> counts)
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
                    CountAdd(counts, k);
                    break;
                }
            }
        }

        for (int i = 0; i < t.childCount; i++) WalkForStubs(t.GetChild(i), depth + 1, stubs, counts);
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

    private static void WriteHeader(List<string> lines, string scene)
    {
        lines.Add($"# Scene Dump: {scene}");
        lines.Add($"Generated: {Log.Time}");
        lines.Add("Roots: ?   Inspected: ?   Syncables: ?"); // patched after walk
        lines.Add("");
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

    #endregion
    #region helpers

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
