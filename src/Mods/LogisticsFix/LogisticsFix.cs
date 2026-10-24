// Logistics Fix: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Fixes a base-game bug: splitters and mergers built by a crewmate who joined your world never take
// capsules. Capsules stop at the end of the belt as if the machine weren't there.
//
// Why: the game numbers a capsule's path through a machine as -(machineId * 16 + input * 4 + output).
// Players who join a world build with ids from their own id block (10,000,000 x a number from 1 to 199
// picked from their Steam id). For any id above 134,217,727, "machineId * 16" overflows a 32-bit
// number, the path can't be found, the game treats it as full, and the capsule waits forever. (The host
// builds with small ids, so the host's own splitters and mergers work.)
//
// Fix: on the host, any splitter or merger with an id that's too big gets renumbered with one of the
// host's own small ids. Splitters and mergers hold nothing, so nothing is lost. The renumbering goes
// through the game's own structure-update code and is sent to the crew like any other building change.
// It runs on world load (fixing existing broken machines) and a couple of seconds after one is built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MoonCleaners;
using UnityEngine;

namespace LogisticsFix
{
    public static class ModEntry
    {
        public static void Init()
        {
            try
            {
                Fix.Boot();
            }
            catch (Exception e)
            {
                Debug.Log("[LogisticsFix] failed to start: " + e);
            }
        }
    }

    internal static class Fix
    {
        public const string Version = "1.0.0";
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int Splitter = 11;
        private const int Merger = 12;
        private const int MaxSafeId = (int.MaxValue - 15) / 16;   // 134,217,727

        private static FieldInfo fDepots;
        private static FieldInfo fCrewDepots;
        private static FieldInfo fNextDepotId;
        private static MethodInfo mWriteDepot;
        private static MoonWorld world;
        private static float next;

        internal static void Log(string msg)
        {
            Debug.Log("[LogisticsFix] " + msg);
        }

        internal static void Boot()
        {
            Type t = typeof(MoonWorld);
            fDepots = t.GetField("depots", Inst);
            fCrewDepots = t.GetField("crewDepots", Inst);
            fNextDepotId = t.GetField("nextDepotId", Inst);
            mWriteDepot = t.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(BinaryWriter), typeof(MoonDepotSnapshot) }, null);
            if (fDepots == null || fCrewDepots == null || fNextDepotId == null || mWriteDepot == null)
            {
                Log("could not find the game's building data (game updated?). Mod is off.");
                return;
            }
            GameObject go = new GameObject("VettaTech LogisticsFix");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();
            Log("v" + Version + " loaded.");
        }

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined && (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost); }
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + 2f;
            if (world == null)
            {
                world = UnityEngine.Object.FindAnyObjectByType<MoonWorld>();
                if (world == null)
                {
                    return;
                }
            }
            if (!IsAuthority || world.Terrain == null)
            {
                return;
            }
            var depots = (Dictionary<int, MoonDepotSnapshot>)fDepots.GetValue(world);
            if (depots == null)
            {
                return;
            }
            List<MoonDepotSnapshot> broken = null;
            foreach (MoonDepotSnapshot d in depots.Values)
            {
                if (d != null && (d.kind == Splitter || d.kind == Merger) && d.id > MaxSafeId)
                {
                    if (broken == null)
                    {
                        broken = new List<MoonDepotSnapshot>();
                    }
                    broken.Add(d);
                }
            }
            if (broken == null)
            {
                return;
            }
            Renumber(depots, broken);
        }

        private static void Renumber(Dictionary<int, MoonDepotSnapshot> depots, List<MoonDepotSnapshot> broken)
        {
            var crew = (Dictionary<int, ulong>)fCrewDepots.GetValue(world);
            int nextId = (int)fNextDepotId.GetValue(world);
            List<KeyValuePair<int, MoonDepotSnapshot>> moves = new List<KeyValuePair<int, MoonDepotSnapshot>>();
            foreach (MoonDepotSnapshot d in broken)
            {
                while (depots.ContainsKey(nextId))
                {
                    nextId++;
                }
                if (nextId > MaxSafeId)
                {
                    Log("no safe id left; can't fix building " + d.id + ".");
                    break;
                }
                MoonDepotSnapshot copy = d.Copy();
                copy.id = nextId++;
                moves.Add(new KeyValuePair<int, MoonDepotSnapshot>(d.id, copy));
            }
            if (moves.Count == 0)
            {
                return;
            }
            fNextDepotId.SetValue(world, nextId);

            // Same message the game applies when a crewmate's building changes arrive:
            // [new/changed buildings] [removed building ids] [belts] [removed belts]
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(moves.Count);
            foreach (KeyValuePair<int, MoonDepotSnapshot> m in moves)
            {
                mWriteDepot.Invoke(null, new object[] { w, m.Value });
            }
            w.Write(moves.Count);
            foreach (KeyValuePair<int, MoonDepotSnapshot> m in moves)
            {
                w.Write(m.Key);
            }
            w.Write(0);
            w.Write(0);
            ms.Position = 0;
            world.ApplyCrewStructures(new BinaryReader(ms), true);

            // Applying it that way marks the change as already known to the crew; un-mark it so the
            // game sends it to everyone with its next building update.
            if (crew != null)
            {
                foreach (KeyValuePair<int, MoonDepotSnapshot> m in moves)
                {
                    crew.Remove(m.Value.id);
                    crew[m.Key] = 0UL;
                }
            }
            foreach (KeyValuePair<int, MoonDepotSnapshot> m in moves)
            {
                Log("fixed a " + (m.Value.kind == Merger ? "merger" : "splitter") + " built by a crewmate (id " + m.Key + " -> " + m.Value.id + ").");
            }
        }
    }

    public sealed class Ticker : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                Fix.Tick();
            }
            catch (Exception e)
            {
                Fix.Log("tick error: " + e.Message);
                enabled = false;
                Invoke("Resume", 10f);
            }
        }

        private void Resume()
        {
            enabled = true;
        }
    }
}
