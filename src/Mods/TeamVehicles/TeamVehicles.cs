// Team Vehicles: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// - When anyone buys a vehicle at a garage, every crewmate gets their own one of that vehicle there.
// - When anyone upgrades a vehicle, every vehicle of the same model gets that upgrade.
// (Crewmates can already drive each other's vehicles in the base game: walk up to a parked one.)
//
// The host is authoritative for vehicles and sends the full vehicle list to everyone whenever it
// changes, so this runs on the host (or solo) and clients just receive the result.
//
// Known limit: demolishing a garage refunds 75% of the price of every vehicle in it, including the free
// copies. Don't demolish garages to farm money.

using System;
using System.Collections.Generic;
using System.Reflection;
using MoonCleaners;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamVehicles
{
    public static class ModEntry
    {
        public static void Init()
        {
            Mod.Boot();
        }
    }

    internal static class Mod
    {
        public const string Version = "1.0.0";
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo fOwned;
        private static MethodInfo mOwnedChanged;
        private static MethodInfo mUpgradesVersionGet;
        private static MethodInfo mUpgradesVersionSet;
        private static FieldInfo fProgressDirty;

        private static MoonWorld world;
        private static bool booted;
        private static bool busy;

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
            mOwnedChanged = t.GetMethod("OwnedChanged", Inst, null, Type.EmptyTypes, null);
            fProgressDirty = t.GetField("progressDirty", Inst);
            PropertyInfo uv = t.GetProperty("VehicleUpgradesVersion", Inst);
            mUpgradesVersionGet = uv != null ? uv.GetGetMethod(true) : null;
            mUpgradesVersionSet = uv != null ? uv.GetSetMethod(true) : null;
            if (fOwned == null || mOwnedChanged == null)
            {
                Log("could not find the game's vehicle data (game updated?). Mod is off.");
                return;
            }
            SceneManager.sceneLoaded += (s, m) => Hook();
            MoonSteamLobby.Changed += OnCrewChanged;
            Hook();
            Log("v" + Version + " loaded.");
        }

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined || MoonSteamLobby.IsHost; }
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
                    world.MoneyChanged -= OnMoneyChanged;
                }
                world = w;
                if (world == null)
                {
                    return;
                }
                // Every vehicle purchase and vehicle upgrade spends money, on the host too when a
                // crewmate buys, so a wallet change is the moment to re-check vehicles.
                world.MoneyChanged += OnMoneyChanged;
                Log("attached to world.");
                Sync("world loaded");
            }
            catch (Exception e)
            {
                Log("hook error: " + e.Message);
            }
        }

        private static void OnMoneyChanged(string player, decimal delta)
        {
            if (delta < 0m)
            {
                Sync("purchase");
            }
        }

        private static void OnCrewChanged()
        {
            Hook();
            Sync("crew changed");
        }

        private static Dictionary<string, string> CrewNames()
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

        private static void Sync(string reason)
        {
            if (busy || world == null || !IsAuthority)
            {
                return;
            }
            busy = true;
            try
            {
                var owned = (Dictionary<string, MoonOwnedVehicle>)fOwned.GetValue(world);
                if (owned == null)
                {
                    return;
                }
                Dictionary<string, string> crew = CrewNames();
                foreach (MoonOwnedVehicle v in owned.Values)
                {
                    if (v != null && !string.IsNullOrEmpty(v.owner) && !crew.ContainsKey(v.owner))
                    {
                        crew[v.owner] = v.ownerName;
                    }
                }
                if (crew.Count < 2)
                {
                    return;
                }

                // Which vehicles exist at which garage, and the best upgrades per vehicle model.
                HashSet<long> garageModels = new HashSet<long>();
                Dictionary<int, byte[]> best = new Dictionary<int, byte[]>();
                foreach (MoonOwnedVehicle v in owned.Values)
                {
                    if (v == null)
                    {
                        continue;
                    }
                    garageModels.Add(((long)v.garage << 32) | (uint)v.vehicle);
                    byte[] b;
                    if (!best.TryGetValue(v.vehicle, out b))
                    {
                        b = new byte[7];
                        best[v.vehicle] = b;
                    }
                    if (v.upgrades != null)
                    {
                        for (int i = 0; i < b.Length && i < v.upgrades.Length; i++)
                        {
                            b[i] = Math.Max(b[i], v.upgrades[i]);
                        }
                    }
                }

                int added = 0;
                int upgraded = 0;

                // 1. Everyone gets their own of every vehicle bought at each garage (parked in that garage).
                foreach (long gm in garageModels)
                {
                    int garage = (int)(gm >> 32);
                    int vehicle = (int)(uint)gm;
                    foreach (KeyValuePair<string, string> p in crew)
                    {
                        string key = MoonWorld.GarageVehicleStorage(garage, vehicle, p.Key);
                        if (owned.ContainsKey(key))
                        {
                            continue;
                        }
                        MoonOwnedVehicle copy = new MoonOwnedVehicle
                        {
                            garage = garage,
                            vehicle = vehicle,
                            owner = p.Key,
                            ownerName = p.Value ?? "",
                            inWorld = false,
                            holder = "",
                            upgrades = new byte[7]
                        };
                        owned[key] = copy;
                        added++;
                    }
                }

                // 2. Every vehicle of a model gets the best upgrades anyone has on that model.
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
                Log("shared " + added + " vehicle(s) and upgraded " + upgraded + " vehicle(s) (" + reason + ").");
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
}
