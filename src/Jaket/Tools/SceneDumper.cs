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
    /// <summary> Curated set of components whose presence on a GameObject merits a full field dump. </summary>
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

    /// <summary> Max children rendered per parent in the tree before collapsing the tail. </summary>
    private const int CHILD_RENDER_CAP = 64;
    /// <summary> Defensive recursion limit. Real ULTRAKILL scenes don't approach this. </summary>
    private const int MAX_DEPTH = 32;

    /// <summary>
    /// Walks the active scene and writes a Markdown dump to Files.Logs/dumps/.
    /// Returns the absolute path of the file written, or null on refusal.
    /// </summary>
    public static string Dump()
    {
        var active = SceneManager.GetActiveScene();
        if (active.name == "Main Menu" || Pending != null)
        {
            Log.Warning("[DUMP] Refused: main menu or scene is still loading.");
            return null;
        }

        var roots = active.GetRootGameObjects();

        var lines = new List<string>(8192);
        var counts = new Dictionary<Type, int>();
        var stubs = new Dictionary<Type, List<StubEntry>>();
        int inspected = 0;

        foreach (var t in Syncable) stubs[t] = new();

        WriteHeader(lines, active.name, roots.Length);

        // Tree section is written after the walk so the stubs/index appendix can appear first.
        var tree = new List<string>(4096);
        foreach (var root in roots)
            WalkNode(root.transform, 0, tree, counts, stubs, ref inspected);

        WriteStubAppendix(lines, stubs);
        lines.Add("");
        lines.Add("## Hierarchy");
        lines.Add("");
        lines.Add("```");
        lines.AddRange(tree);
        lines.Add("```");
        lines.Add("");
        WriteComponentIndex(lines, counts);

        // Patch in the totals line we couldn't know until the walk finished.
        int syncables = 0;
        foreach (var s in stubs.Values) syncables += s.Count;
        lines[2] = $"Roots: {roots.Length}   Inspected: {inspected}   Syncables: {syncables}";

        string dir = Files.Join(Files.Logs, "dumps");
        try { Files.MakeDir(dir); }
        catch (Exception ex) { Log.Error("[DUMP] Could not create dumps directory.", ex); return null; }

        string path = Files.Join(dir, $"{SafeName(active.name)}_{Log.Time.Replace(':', '.')}.md");
        Files.Append(path, lines);
        Log.Debug($"[DUMP] Wrote {path} ({lines.Count} lines, {syncables} syncables)");
        return path;
    }

    #region walk

    private static void WalkNode(Transform t, int depth, List<string> tree,
                                 Dictionary<Type, int> counts,
                                 Dictionary<Type, List<StubEntry>> stubs,
                                 ref int inspected)
    {
        if (depth >= MAX_DEPTH) { tree.Add(Indent(depth) + "... (max depth)"); return; }
        if (!IsReal(t.gameObject)) return;

        inspected++;

        var comps = t.GetComponents(typeof(Component));
        var tier1 = new List<Component>();
        var tier2 = new List<string>();

        foreach (var c in comps)
        {
            if (c == null) continue; // missing scripts come through as nulls
            var ct = c.GetType();

            // Highlight if exactly this type — or a derived type — is in the curated list.
            bool curated = false;
            foreach (var k in Syncable)
            {
                if (k.IsAssignableFrom(ct)) { curated = true; CountAdd(counts, k); break; }
            }

            if (curated) tier1.Add(c);
            else if (ct != typeof(Transform) && ct != typeof(RectTransform)) tier2.Add(ct.Name);
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
        if (tier2.Count > 0)
        {
            sb.Append("   (");
            for (int i = 0; i < tier2.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(tier2[i]);
            }
            sb.Append(')');
        }
        tree.Add(sb.ToString());

        // Tier-1 field dumps inline under the GameObject line
        foreach (var c in tier1) WriteTierOneFields(tree, depth + 1, c, stubs);

        // Children with cap
        int childCount = t.childCount;
        if (childCount > CHILD_RENDER_CAP)
        {
            int head = CHILD_RENDER_CAP / 2;
            int tail = CHILD_RENDER_CAP - head;
            for (int i = 0; i < head; i++) WalkNode(t.GetChild(i), depth + 1, tree, counts, stubs, ref inspected);
            tree.Add(Indent(depth + 1) + $"... (+{childCount - head - tail} more children)");
            for (int i = childCount - tail; i < childCount; i++) WalkNode(t.GetChild(i), depth + 1, tree, counts, stubs, ref inspected);
        }
        else
        {
            for (int i = 0; i < childCount; i++) WalkNode(t.GetChild(i), depth + 1, tree, counts, stubs, ref inspected);
        }
    }

    private static void WriteTierOneFields(List<string> tree, int depth, Component c,
                                           Dictionary<Type, List<StubEntry>> stubs)
    {
        string pad = Indent(depth) + "    ";

        if (c is ObjectActivator oa)
        {
            stubs[typeof(ObjectActivator)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"delay: {oa.delay}");
            DumpEventArray(tree, pad, "toActivateObjects", oa.events?.toActivateObjects);
            DumpEventArray(tree, pad, "toDisActivateObjects", oa.events?.toDisActivateObjects);
            DumpUnityEvent(tree, pad, "onActivate", oa.events?.onActivate);
            DumpUnityEvent(tree, pad, "onDisActivate", oa.events?.onDisActivate);
            return;
        }

        if (c is ScriptActivator sa)
        {
            stubs[typeof(ScriptActivator)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"pistons: {sa.pistons?.Length ?? 0}, lightpillars: {sa.lightpillars?.Length ?? 0}");
            return;
        }

        if (c is ControllerPointer cp)
        {
            stubs[typeof(ControllerPointer)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"OnPressed: {ListenerSummary(cp.OnPressed)}");
            return;
        }

        if (c is Glass g)
        {
            stubs[typeof(Glass)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"wall: {g.wall}  pos: {FormatXZ(g.transform.position)}");
            return;
        }

        if (c is ActivateNextWaveHP anw)
        {
            stubs[typeof(ActivateNextWaveHP)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"health: {anw.health}");
            return;
        }

        if (c is ItemPlaceZone ipz)
        {
            stubs[typeof(ItemPlaceZone)].Add(new(c.Path(), c.transform.position));
            tree.Add(pad + $"activateOnSuccess: {ipz.activateOnSuccess?.Length ?? 0}, deactivateOnSuccess: {ipz.deactivateOnSuccess?.Length ?? 0}");
            return;
        }

        // Generic stub-only entry for the rest: StatueActivator, LimboSwitch, Flammable,
        // ActivateArena, FinalDoor, Door, ItemTrigger. The maintainer mostly needs the
        // path + position for these; deep field dumps aren't usually rewritten.
        foreach (var k in Syncable)
            if (k.IsAssignableFrom(c.GetType()))
            {
                stubs[k].Add(new(c.Path(), c.transform.position));
                tree.Add(pad + $"pos: {FormatXZ(c.transform.position)}");
                return;
            }
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

    private static void WriteHeader(List<string> lines, string scene, int roots)
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

            lines.Add($"// --- {SimpleName(t)} ({entries.Count}) ---");
            string helper = StubHelper(t);

            if (IsPositionKeyed(t))
            {
                // One helper call covers all instances; list positions in a comment.
                lines.Add($"{helper};");
                foreach (var e in entries) lines.Add($"//   pos: {FormatXZ(e.Position)}");
            }
            else if (helper != null)
            {
                foreach (var e in entries) lines.Add($"{helper}, \"{e.Path}\");");
            }
            else
            {
                // No ActionType helper — emit a TODO scaffold.
                lines.Add($"// TODO: no ActionType.{SimpleName(t)} helper exists; write a custom Find lambda.");
                foreach (var e in entries) lines.Add($"//   path: \"{e.Path}\"   pos: {FormatXZ(e.Position)}");
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

    /// <summary> Position-keyed syncables use a single-arg helper that finds every instance. </summary>
    private static bool IsPositionKeyed(Type t) =>
        t == typeof(Glass) || t == typeof(StatueActivator) || t == typeof(LimboSwitch) ||
        t == typeof(Flammable) || t == typeof(ActivateArena) || t == typeof(FinalDoor);

    /// <summary> Maps a syncable type to its ActionType helper prefix, or null if none exists. </summary>
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
        return null; // ActivateNextWaveHP, Door, ItemTrigger, ItemPlaceZone — custom lambda required
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
