// Team Upgrades: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Tools, container upgrades, upgrade-tree unlocks and inventory tier are shared by the whole crew.
// When anyone buys something, everyone gets it; money is already shared, so you pay once.
//
// The host is authoritative for upgrades in this game and syncs them to clients. On the host (or solo),
// whenever upgrades change, a lobby member joins, or a world loads, every crewmate is raised to the best
// level anyone on the crew has. Clients just receive the result. Vehicles are not shared.

using System;
using System.Collections.Generic;
using System.Reflection;
using MoonCleaners;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamUpgrades
{
    public static class ModEntry
    {
        public static void Init()
        {
            Loader.Boot();
        }
    }

    public static class Loader
    {
        public const string Version = "1.2.0";
        private static bool booted;

        public static void Boot()
        {
            if (booted)
            {
                return;
            }
            booted = true;
            try
            {
                if (!TeamSync.Init())
                {
                    Log("could not find the game's upgrade data (game updated?). Mod is off.");
                    return;
                }
                SceneManager.sceneLoaded += OnSceneLoaded;
                MoonSteamLobby.Changed += OnLobbyChanged;
                Hook();
                Log("v" + Version + " loaded.");
            }
            catch (Exception e)
            {
                Log("failed to start: " + e);
            }
        }

        internal static void Log(string msg)
        {
            Debug.Log("[TeamUpgrades] " + msg);
        }

        private static MoonWorld world;

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined || MoonSteamLobby.IsHost; }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Hook();
        }

        private static void OnLobbyChanged()
        {
            try
            {
                Hook();
                if (world != null && IsAuthority)
                {
                    TeamSync.Merge(world, "crew changed");
                }
            }
            catch (Exception e)
            {
                Log("lobby handler error: " + e.Message);
            }
        }

        private static void OnUpgradesChanged(string player)
        {
            try
            {
                if (world != null && IsAuthority)
                {
                    TeamSync.Merge(world, "upgrade");
                }
            }
            catch (Exception e)
            {
                Log("upgrade handler error: " + e.Message);
            }
        }

        private static void Hook()
        {
            try
            {
                MoonWorld w = UnityEngine.Object.FindAnyObjectByType<MoonWorld>();
                if (w == world)
                {
                    return;
                }
                if (world != null)
                {
                    world.UpgradesChanged -= OnUpgradesChanged;
                }
                world = w;
                if (world == null)
                {
                    return;
                }
                world.UpgradesChanged += OnUpgradesChanged;
                Log("attached to world.");
                if (IsAuthority)
                {
                    TeamSync.Merge(world, "world loaded");
                }
            }
            catch (Exception e)
            {
                Log("hook error: " + e.Message);
            }
        }
    }

    internal static class TeamSync
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo fUpgrades;
        private static FieldInfo fContainerTiers;
        private static FieldInfo fTreeLevels;
        private static FieldInfo fProgressDirty;
        private static FieldInfo fUpgradesChanged;
        private static MethodInfo mTreeVersionSet;
        private static MethodInfo mTreeVersionGet;
        private static bool busy;

        internal static bool Init()
        {
            Type t = typeof(MoonWorld);
            fUpgrades = t.GetField("upgrades", Inst);
            fContainerTiers = t.GetField("containerTiers", Inst);
            fTreeLevels = t.GetField("treeLevels", Inst);
            fProgressDirty = t.GetField("progressDirty", Inst);
            fUpgradesChanged = t.GetField("UpgradesChanged", Inst);
            PropertyInfo treeVersion = t.GetProperty("TreeVersion", Inst);
            mTreeVersionGet = treeVersion != null ? treeVersion.GetGetMethod(true) : null;
            mTreeVersionSet = treeVersion != null ? treeVersion.GetSetMethod(true) : null;
            return fUpgrades != null && fContainerTiers != null && fTreeLevels != null;
        }

        private static bool ValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Trim().Length > 0 && id.Length <= 128;
        }

        private static HashSet<string> CrewIds(Dictionary<string, MoonUpgradeSnapshot> upgrades,
            Dictionary<string, Dictionary<string, int>> containers,
            Dictionary<string, Dictionary<string, int>> tree)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            ids.UnionWith(upgrades.Keys);
            ids.UnionWith(containers.Keys);
            ids.UnionWith(tree.Keys);
            ids.Add(MoonCrew.LocalPlayerId);
            if (MoonSteamLobby.InLobby && MoonSteamLobby.Members != null)
            {
                foreach (MoonSteamLobby.Member m in MoonSteamLobby.Members)
                {
                    if (m != null && m.id != 0)
                    {
                        ids.Add(MoonCrew.PlayerId(m.id));
                    }
                }
            }
            ids.RemoveWhere(id => !ValidId(id));
            return ids;
        }

        private static void TakeMax(Dictionary<string, int> into, Dictionary<string, int> from)
        {
            if (from == null)
            {
                return;
            }
            foreach (KeyValuePair<string, int> kv in from)
            {
                int have;
                if (kv.Key != null && kv.Value != 0 && (!into.TryGetValue(kv.Key, out have) || kv.Value > have))
                {
                    into[kv.Key] = kv.Value;
                }
            }
        }

        private static bool RaiseTo(Dictionary<string, Dictionary<string, int>> all, string player, Dictionary<string, int> team)
        {
            if (team.Count == 0)
            {
                return false;
            }
            bool changed = false;
            Dictionary<string, int> mine;
            if (!all.TryGetValue(player, out mine) || mine == null)
            {
                mine = new Dictionary<string, int>();
                all[player] = mine;
            }
            foreach (KeyValuePair<string, int> kv in team)
            {
                int have;
                if (!mine.TryGetValue(kv.Key, out have) || have < kv.Value)
                {
                    mine[kv.Key] = kv.Value;
                    changed = true;
                }
            }
            return changed;
        }

        internal static void Merge(MoonWorld world, string reason)
        {
            if (busy || world == null)
            {
                return;
            }
            busy = true;
            try
            {
                var upgrades = (Dictionary<string, MoonUpgradeSnapshot>)fUpgrades.GetValue(world);
                var containers = (Dictionary<string, Dictionary<string, int>>)fContainerTiers.GetValue(world);
                var tree = (Dictionary<string, Dictionary<string, int>>)fTreeLevels.GetValue(world);
                if (upgrades == null || containers == null || tree == null)
                {
                    return;
                }

                HashSet<string> crew = CrewIds(upgrades, containers, tree);
                if (crew.Count < 2)
                {
                    return;
                }

                // 1. Best the crew has of everything.
                int teamTier = 0;
                var teamTools = new Dictionary<string, int>(StringComparer.Ordinal);
                var teamContainers = new Dictionary<string, int>(StringComparer.Ordinal);
                var teamTree = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string p in crew)
                {
                    MoonUpgradeSnapshot s;
                    if (upgrades.TryGetValue(p, out s) && s != null)
                    {
                        teamTier = Math.Max(teamTier, s.inventoryTier);
                        foreach (MoonToolUpgradeSnapshot tool in s.tools)
                        {
                            int lvl;
                            if (tool != null && tool.id != null && tool.level >= 0
                                && (!teamTools.TryGetValue(tool.id, out lvl) || tool.level > lvl))
                            {
                                teamTools[tool.id] = tool.level;
                            }
                        }
                    }
                    Dictionary<string, int> c;
                    containers.TryGetValue(p, out c);
                    TakeMax(teamContainers, c);
                    Dictionary<string, int> tr;
                    tree.TryGetValue(p, out tr);
                    TakeMax(teamTree, tr);
                }

                // 2. Raise every crewmate to it.
                List<string> changed = new List<string>();
                foreach (string p in crew)
                {
                    bool ch = false;
                    if (teamTools.Count > 0 || teamTier > 0)
                    {
                        MoonUpgradeSnapshot s;
                        if (!upgrades.TryGetValue(p, out s) || s == null)
                        {
                            s = new MoonUpgradeSnapshot { player = p };
                            upgrades[p] = s;
                        }
                        if (s.inventoryTier < teamTier)
                        {
                            s.inventoryTier = teamTier;
                            ch = true;
                        }
                        foreach (KeyValuePair<string, int> kv in teamTools)
                        {
                            string id = kv.Key;
                            MoonToolUpgradeSnapshot own = s.tools.Find(x => x != null && x.id == id);
                            if (own == null)
                            {
                                s.tools.Add(new MoonToolUpgradeSnapshot { id = id, level = kv.Value });
                                ch = true;
                            }
                            else if (own.level < kv.Value)
                            {
                                own.level = kv.Value;
                                ch = true;
                            }
                        }
                    }
                    if (RaiseTo(containers, p, teamContainers))
                    {
                        ch = true;
                    }
                    if (RaiseTo(tree, p, teamTree))
                    {
                        ch = true;
                    }
                    if (ch)
                    {
                        changed.Add(p);
                    }
                }

                if (changed.Count == 0)
                {
                    return;
                }

                // 3. Save, refresh the upgrade tree, notify shop/HUD/network.
                if (fProgressDirty != null)
                {
                    fProgressDirty.SetValue(world, true);
                }
                if (mTreeVersionGet != null && mTreeVersionSet != null)
                {
                    int v = (int)mTreeVersionGet.Invoke(world, null);
                    mTreeVersionSet.Invoke(world, new object[] { v + 1 });
                }
                Action<string> handler = fUpgradesChanged != null ? fUpgradesChanged.GetValue(world) as Action<string> : null;
                if (handler != null)
                {
                    foreach (string p in changed)
                    {
                        try
                        {
                            handler(p);
                        }
                        catch (Exception e)
                        {
                            Loader.Log("listener failed for " + p + ": " + e.Message);
                        }
                    }
                }
                Loader.Log("shared upgrades with " + changed.Count + " crewmate(s) (" + reason + ").");
            }
            catch (Exception e)
            {
                Loader.Log("merge failed: " + e);
            }
            finally
            {
                busy = false;
            }
        }
    }
}
