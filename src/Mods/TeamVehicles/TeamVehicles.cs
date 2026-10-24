// Team Vehicles (with Linked Garages): a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// - Shared fleet: when anyone buys a vehicle, every crewmate in the lobby gets their own one.
// - Shared upgrades: upgrade a vehicle and every vehicle of that model gets it.
// - Linked garages: every garage holds everyone's whole fleet, so you can call any of your vehicles
//   out of any garage. Demolishing a garage doesn't lose vehicles: they're still in your other garages,
//   and if no garage is left, they're waiting in the next one you build. (A garage's refund now covers
//   just the building, since you keep the vehicles.)
// (Crewmates can already drive each other's vehicles in the base game: walk up to a parked one.)
//
// The game keeps vehicles per garage and forgets vehicles whose garage is gone, so this mod keeps a
// small fleet list in the save folder (vettatech-fleet.txt): who owns which models, with upgrades.
// The host is authoritative for vehicles and sends the full vehicle list to everyone, so this runs on
// the host (or solo) and clients just receive the result.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace TeamVehicles
{
    public static class ModEntry
    {
        public static void Init()
        {
            try
            {
                Mod.Boot();
            }
            catch (Exception e)
            {
                Debug.Log("[TeamVehicles] failed to start: " + e);
            }
        }
    }

    internal static class Mod
    {
        public const string Version = "2.0.0";
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int MaxVehicles = 2000;          // the game refuses saves with more than 4096
        private const float SyncEvery = 2f;
        private const string FleetFile = "vettatech-fleet.txt";

        private static FieldInfo fOwned;
        private static FieldInfo fDepots;
        private static MethodInfo mOwnedChanged;
        private static MethodInfo mUpgradesVersionGet;
        private static MethodInfo mUpgradesVersionSet;
        private static PropertyInfo pOwnedRevision;
        private static FieldInfo fProgressDirty;

        private static MoonWorld world;
        private static bool booted;
        private static bool busy;
        private static int seenRevision = int.MinValue;
        private static float nextSync;

        // owner -> model -> upgrades; persisted per save so vehicles survive having no garage
        private static readonly Dictionary<string, Dictionary<int, byte[]>> fleet = new Dictionary<string, Dictionary<int, byte[]>>(StringComparer.Ordinal);
        private static string fleetDir;
        private static string fleetSaved;

        internal static void Log(string msg)
        {
            Debug.Log("[TeamVehicles] " + msg);
        }

        internal static void Boot()
        {
            if (booted)
            {
                return;
            }
            booted = true;
            Type t = typeof(MoonWorld);
            fOwned = t.GetField("ownedVehicles", Inst);
            fDepots = t.GetField("depots", Inst);
            mOwnedChanged = t.GetMethod("OwnedChanged", Inst, null, Type.EmptyTypes, null);
            fProgressDirty = t.GetField("progressDirty", Inst);
            pOwnedRevision = t.GetProperty("OwnedVehiclesRevision", Inst);
            PropertyInfo uv = t.GetProperty("VehicleUpgradesVersion", Inst);
            mUpgradesVersionGet = uv != null ? uv.GetGetMethod(true) : null;
            mUpgradesVersionSet = uv != null ? uv.GetSetMethod(true) : null;
            MethodInfo price = t.GetMethod("GarageVehiclesPrice", Inst, null, new[] { typeof(int) }, null);
            MethodInfo canRemove = t.GetMethod("CanRemoveStructure", Inst);
            if (fOwned == null || fDepots == null || mOwnedChanged == null || price == null)
            {
                Log("could not find the game's vehicle data (game updated?). Mod is off.");
                return;
            }
            Harmony h = new Harmony("vettatech.moonsweepers.teamvehicles");
            // Vehicles are kept when a garage goes, so its refund must not pay for them.
            h.Patch(price, prefix: new HarmonyMethod(typeof(Patches), "NoVehicleRefund"));
            if (canRemove != null)
            {
                h.Patch(canRemove, postfix: new HarmonyMethod(typeof(Patches), "RemovePrompt"));
            }
            GameObject go = new GameObject("TeamVehicles");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();
            Log("v" + Version + " loaded (linked garages on).");
        }

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined && (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost); }
        }

        internal static void Tick()
        {
            if (world == null)
            {
                MoonWorld w = UnityEngine.Object.FindAnyObjectByType<MoonWorld>();
                if (w == null)
                {
                    return;
                }
                world = w;
                fleet.Clear();
                fleetDir = null;
                fleetSaved = null;
                seenRevision = int.MinValue;
                Log("attached to world.");
            }
            if (!IsAuthority)
            {
                return;
            }
            int rev = pOwnedRevision != null ? (int)pOwnedRevision.GetValue(world, null) : 0;
            if (rev != seenRevision || Time.unscaledTime >= nextSync)
            {
                nextSync = Time.unscaledTime + SyncEvery;
                Sync();
                seenRevision = pOwnedRevision != null ? (int)pOwnedRevision.GetValue(world, null) : 0;
            }
        }

        // ------------------------------------------------------------------ fleet file

        private static void LoadFleet()
        {
            string dir = world.SaveDirectory;
            if (string.IsNullOrEmpty(dir) || dir == fleetDir)
            {
                return;
            }
            fleetDir = dir;
            fleet.Clear();
            fleetSaved = null;
            string path = Path.Combine(dir, FleetFile);
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split('\t');
                int model;
                if (p.Length < 3 || p[0].Length == 0 || p[0].Length > 128 || !int.TryParse(p[1], out model) || MoonWorld.GarageOfferFor(model) == null)
                {
                    continue;
                }
                byte[] up = new byte[7];
                string[] u = p[2].Split(',');
                for (int i = 0; i < up.Length && i < u.Length; i++)
                {
                    byte b;
                    if (byte.TryParse(u[i], out b))
                    {
                        up[i] = b;
                    }
                }
                Remember(p[0], model, up);
            }
            fleetSaved = Serialize();
            int n = 0;
            foreach (Dictionary<int, byte[]> m in fleet.Values)
            {
                n += m.Count;
            }
            Log("fleet list loaded: " + n + " vehicle(s).");
        }

        private static string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            List<string> owners = new List<string>(fleet.Keys);
            owners.Sort(StringComparer.Ordinal);
            foreach (string o in owners)
            {
                List<int> models = new List<int>(fleet[o].Keys);
                models.Sort();
                foreach (int m in models)
                {
                    byte[] u = fleet[o][m];
                    sb.Append(o).Append('\t').Append(m).Append('\t');
                    for (int i = 0; i < u.Length; i++)
                    {
                        sb.Append(i > 0 ? "," : "").Append(u[i]);
                    }
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        private static void SaveFleet()
        {
            if (string.IsNullOrEmpty(fleetDir))
            {
                return;
            }
            string text = Serialize();
            if (text == fleetSaved)
            {
                return;
            }
            string path = Path.Combine(fleetDir, FleetFile);
            File.WriteAllText(path + ".tmp", text);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(path + ".tmp", path);
            fleetSaved = text;
        }

        private static void Remember(string owner, int model, byte[] upgrades)
        {
            Dictionary<int, byte[]> mine;
            if (!fleet.TryGetValue(owner, out mine))
            {
                mine = new Dictionary<int, byte[]>();
                fleet[owner] = mine;
            }
            byte[] have;
            if (!mine.TryGetValue(model, out have))
            {
                have = new byte[7];
                mine[model] = have;
            }
            if (upgrades != null)
            {
                for (int i = 0; i < have.Length && i < upgrades.Length; i++)
                {
                    have[i] = Math.Max(have[i], upgrades[i]);
                }
            }
        }

        // ------------------------------------------------------------------ sync

        private static Dictionary<string, string> Crew()
        {
            Dictionary<string, string> crew = new Dictionary<string, string>(StringComparer.Ordinal);
            crew[MoonCrew.LocalPlayerId] = null;
            if (MoonSteamLobby.InLobby && MoonSteamLobby.Members != null)
            {
                foreach (MoonSteamLobby.Member m in MoonSteamLobby.Members)
                {
                    if (m != null && m.id != 0)
                    {
                        crew[MoonCrew.PlayerId(m.id)] = m.name;
                    }
                }
            }
            return crew;
        }

        private static List<int> Garages()
        {
            List<int> list = new List<int>();
            var depots = (Dictionary<int, MoonDepotSnapshot>)fDepots.GetValue(world);
            if (depots != null)
            {
                foreach (MoonDepotSnapshot d in depots.Values)
                {
                    if (d != null && d.kind == 98)
                    {
                        list.Add(d.id);
                    }
                }
            }
            list.Sort();
            return list;
        }

        private static void Sync()
        {
            if (busy || world == null)
            {
                return;
            }
            busy = true;
            try
            {
                var owned = (Dictionary<string, MoonOwnedVehicle>)fOwned.GetValue(world);
                if (owned == null || world.Terrain == null)
                {
                    return; // world still loading
                }
                LoadFleet();

                // 1. Learn every vehicle that exists now (purchases, upgrades) into the fleet list.
                foreach (MoonOwnedVehicle v in owned.Values)
                {
                    if (v != null && !string.IsNullOrEmpty(v.owner) && MoonWorld.GarageOfferFor(v.vehicle) != null)
                    {
                        Remember(v.owner, v.vehicle, v.upgrades);
                    }
                }

                // 2. Shared fleet and upgrades: crewmates in the lobby get every model anyone has, and
                //    every model gets the best upgrades anyone has on it.
                Dictionary<string, string> crew = Crew();
                Dictionary<int, byte[]> best = new Dictionary<int, byte[]>();
                foreach (Dictionary<int, byte[]> mine in fleet.Values)
                {
                    foreach (KeyValuePair<int, byte[]> m in mine)
                    {
                        byte[] b;
                        if (!best.TryGetValue(m.Key, out b))
                        {
                            b = new byte[7];
                            best[m.Key] = b;
                        }
                        for (int i = 0; i < b.Length; i++)
                        {
                            b[i] = Math.Max(b[i], m.Value[i]);
                        }
                    }
                }
                foreach (string p in crew.Keys)
                {
                    foreach (KeyValuePair<int, byte[]> m in best)
                    {
                        Remember(p, m.Key, m.Value);
                    }
                }
                foreach (Dictionary<int, byte[]> mine in fleet.Values)
                {
                    foreach (KeyValuePair<int, byte[]> m in mine)
                    {
                        Array.Copy(best[m.Key], m.Value, 7);
                    }
                }

                // 3. Linked garages: every garage holds every fleet vehicle.
                int added = 0;
                foreach (int g in Garages())
                {
                    foreach (KeyValuePair<string, Dictionary<int, byte[]>> o in fleet)
                    {
                        foreach (int model in o.Value.Keys)
                        {
                            string key = MoonWorld.GarageVehicleStorage(g, model, o.Key);
                            if (owned.ContainsKey(key) || owned.Count >= MaxVehicles)
                            {
                                continue;
                            }
                            string name;
                            crew.TryGetValue(o.Key, out name);
                            owned[key] = new MoonOwnedVehicle
                            {
                                garage = g,
                                vehicle = model,
                                owner = o.Key,
                                ownerName = name ?? "",
                                inWorld = false,
                                holder = "",
                                upgrades = (byte[])best[model].Clone()
                            };
                            added++;
                        }
                    }
                }

                // 4. Bring every vehicle's upgrades up to the model's best.
                int upgraded = 0;
                foreach (MoonOwnedVehicle v in owned.Values)
                {
                    byte[] b;
                    if (v == null || !best.TryGetValue(v.vehicle, out b))
                    {
                        continue;
                    }
                    if (v.upgrades == null || v.upgrades.Length < b.Length)
                    {
                        Array.Resize(ref v.upgrades, b.Length);
                    }
                    bool changed = false;
                    for (int i = 0; i < b.Length; i++)
                    {
                        if (v.upgrades[i] < b[i])
                        {
                            v.upgrades[i] = b[i];
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        upgraded++;
                    }
                }

                SaveFleet();

                if (added == 0 && upgraded == 0)
                {
                    return;
                }
                if (fProgressDirty != null)
                {
                    fProgressDirty.SetValue(world, true);
                }
                if (upgraded > 0 && mUpgradesVersionGet != null && mUpgradesVersionSet != null)
                {
                    int ver = (int)mUpgradesVersionGet.Invoke(world, null);
                    mUpgradesVersionSet.Invoke(world, new object[] { ver + 1 });
                }
                mOwnedChanged.Invoke(world, null); // also makes the host send the vehicle list to the crew
                Log("garages now hold " + added + " more vehicle(s); upgraded " + upgraded + ".");
            }
            catch (Exception e)
            {
                Log("sync failed: " + e);
            }
            finally
            {
                busy = false;
            }
        }
    }

    internal static class Patches
    {
        // MoonWorld.GarageVehiclesPrice(int garage): the vehicle part of a garage's removal refund.
        public static bool NoVehicleRefund(ref decimal __result)
        {
            __result = 0m;
            return false;
        }

        // MoonWorld.CanRemoveStructure(...): fix the "everyone's vehicles there go too" warning.
        public static void RemovePrompt(bool __result, ref string reason)
        {
            try
            {
                if (__result && reason != null && reason.Contains("(everyone's vehicles there go too)"))
                {
                    reason = reason.Replace("(everyone's vehicles there go too)", "(vehicles stay in your other garages)");
                }
            }
            catch
            {
            }
        }
    }

    public sealed class Ticker : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                Mod.Tick();
            }
            catch (Exception e)
            {
                Mod.Log("tick error: " + e.Message);
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
