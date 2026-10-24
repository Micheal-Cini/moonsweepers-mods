// Team Vehicles: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// - When anyone buys a vehicle at a garage, every crewmate in the lobby gets their own one there.
// - When anyone upgrades a vehicle, every vehicle of the same model gets that upgrade.
// (Crewmates can already drive each other's vehicles in the base game: walk up to a parked one.)
//
// The host is authoritative for vehicles and sends the full vehicle list to everyone whenever it
// changes, so this runs on the host and clients just receive the result.
//
// Garage refunds: demolishing a garage refunds 75% (100% with Full Refunds) of every vehicle in it, and free copies
// would count too. When the host sees a garage that had copies get demolished AND a refund paid out,
// it takes back the part of the refund that came from copies, so a garage refunds one of each model
// parked there (what was actually paid for).

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
        public const string Version = "1.1.0";
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int MaxVehicles = 2000;          // the game refuses saves with more than 4096
        private const float SyncEvery = 2f;
        private const float RefundWindow = 15f;

        private static FieldInfo fOwned;
        private static MethodInfo mOwnedChanged;
        private static MethodInfo mChargeBuild;
        private static MethodInfo mUpgradesVersionGet;
        private static MethodInfo mUpgradesVersionSet;
        private static PropertyInfo pOwnedRevision;
        private static FieldInfo fProgressDirty;

        private static MoonWorld world;
        private static bool booted;
        private static bool busy;
        private static int seenRevision = int.MinValue;
        private static float nextSync;

        // garage id -> extra refund its free copies would add (only garages that exist right now)
        private static readonly Dictionary<int, decimal> copyRefund = new Dictionary<int, decimal>();
        // recent positive money changes (time, amount): a demolition refund shows up as one of these
        private static readonly List<KeyValuePair<float, decimal>> recentIncome = new List<KeyValuePair<float, decimal>>();

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
            mChargeBuild = t.GetMethod("ChargeBuild", Inst, null, new[] { typeof(string), typeof(decimal) }, null);
            fProgressDirty = t.GetField("progressDirty", Inst);
            pOwnedRevision = t.GetProperty("OwnedVehiclesRevision", Inst);
            PropertyInfo uv = t.GetProperty("VehicleUpgradesVersion", Inst);
            mUpgradesVersionGet = uv != null ? uv.GetGetMethod(true) : null;
            mUpgradesVersionSet = uv != null ? uv.GetSetMethod(true) : null;
            if (fOwned == null || mOwnedChanged == null)
            {
                Log("could not find the game's vehicle data (game updated?). Mod is off.");
                return;
            }
            GameObject go = new GameObject("TeamVehicles");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();
            Log("v" + Version + " loaded.");
        }

        // Host of a crew, or a solo game (copies only go to lobby members, so solo only settles refunds).
        // A lobby member who hasn't joined the host's world yet is NOT authority.
        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined && (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost); }
        }

        // Called by the Ticker every frame; all game access happens here, on the main thread.
        internal static void Tick()
        {
            if (world == null)
            {
                MoonWorld w = UnityEngine.Object.FindAnyObjectByType<MoonWorld>();
                if (w == null)
                {
                    return;
                }
                Attach(w);
            }
            if (!IsAuthority)
            {
                return;
            }
            int rev = pOwnedRevision != null ? (int)pOwnedRevision.GetValue(world, null) : 0;
            if (rev != seenRevision || Time.unscaledTime >= nextSync)
            {
                seenRevision = rev;
                nextSync = Time.unscaledTime + SyncEvery;
                Sync();
                SettleDemolishedGarages();
                seenRevision = pOwnedRevision != null ? (int)pOwnedRevision.GetValue(world, null) : 0;
            }
        }

        private static void Attach(MoonWorld w)
        {
            world = w;
            copyRefund.Clear();
            recentIncome.Clear();
            seenRevision = int.MinValue;
            world.MoneyChanged += OnMoneyChanged;
            Log("attached to world.");
        }

        // Thin wrapper: never let anything escape into the game's wallet code.
        private static void OnMoneyChanged(string player, decimal delta)
        {
            try
            {
                NoteIncome(delta);
            }
            catch
            {
            }
        }

        private static void NoteIncome(decimal delta)
        {
            if (delta > 0m)
            {
                recentIncome.Add(new KeyValuePair<float, decimal>(Time.unscaledTime, delta));
                if (recentIncome.Count > 200)
                {
                    recentIncome.RemoveRange(0, 100);
                }
            }
        }

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

        // Use the game's own refund calculation, so this matches whatever the game pays
        // (75% normally, 100% with the Full Refunds mod).
        private static MethodInfo mRefund;

        private static decimal Refund(decimal price)
        {
            if (mRefund == null)
            {
                mRefund = typeof(MoonWorld).GetMethod("Refund", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(decimal) }, null);
            }
            if (mRefund != null)
            {
                return (decimal)mRefund.Invoke(null, new object[] { price });
            }
            return Math.Max(0m, decimal.Floor(price * 0.75m * 100m) / 100m);
        }

        private static decimal Price(int vehicle)
        {
            MoonWorld.GarageOffer offer = MoonWorld.GarageOfferFor(vehicle);
            return offer != null ? offer.price : 0m;
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
                if (owned == null)
                {
                    return;
                }
                Dictionary<string, string> crew = Crew();

                HashSet<long> garageModels = new HashSet<long>();
                Dictionary<int, byte[]> best = new Dictionary<int, byte[]>();
                foreach (MoonOwnedVehicle v in owned.Values)
                {
                    if (v == null || !world.IsGarage(v.garage))
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

                // 1. Every crewmate in the lobby gets their own of every vehicle at each existing garage.
                if (crew.Count >= 2)
                {
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
                            if (owned.Count >= MaxVehicles)
                            {
                                break;
                            }
                            owned[key] = new MoonOwnedVehicle
                            {
                                garage = garage,
                                vehicle = vehicle,
                                owner = p.Key,
                                ownerName = p.Value ?? "",
                                inWorld = false,
                                holder = "",
                                upgrades = new byte[7]
                            };
                            added++;
                        }
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

                RememberCopyRefunds(owned);

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
                Log("shared " + added + " vehicle(s), upgraded " + upgraded + " vehicle(s).");
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

        // For every garage that exists now: how much of its demolition refund would come from copies.
        private static void RememberCopyRefunds(Dictionary<string, MoonOwnedVehicle> owned)
        {
            Dictionary<int, decimal> all = new Dictionary<int, decimal>();
            Dictionary<int, HashSet<int>> models = new Dictionary<int, HashSet<int>>();
            foreach (MoonOwnedVehicle v in owned.Values)
            {
                if (v == null || !world.IsGarage(v.garage))
                {
                    continue;
                }
                decimal total;
                all.TryGetValue(v.garage, out total);
                all[v.garage] = total + Price(v.vehicle);
                HashSet<int> set;
                if (!models.TryGetValue(v.garage, out set))
                {
                    set = new HashSet<int>();
                    models[v.garage] = set;
                }
                set.Add(v.vehicle);
            }
            foreach (KeyValuePair<int, decimal> g in all)
            {
                decimal paid = 0m;
                foreach (int m in models[g.Key])
                {
                    paid += Price(m);
                }
                decimal extra = Refund(g.Value) - Refund(paid);
                if (extra > 0m)
                {
                    copyRefund[g.Key] = extra;
                }
                else
                {
                    copyRefund.Remove(g.Key);
                }
            }
        }

        private static void SettleDemolishedGarages()
        {
            if (world == null || copyRefund.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            recentIncome.RemoveAll(x => now - x.Key > RefundWindow);
            List<int> gone = null;
            foreach (int g in copyRefund.Keys)
            {
                if (!world.IsGarage(g))
                {
                    if (gone == null)
                    {
                        gone = new List<int>();
                    }
                    gone.Add(g);
                }
            }
            if (gone == null)
            {
                return;
            }
            foreach (int g in gone)
            {
                decimal extra = copyRefund[g];
                copyRefund.Remove(g);
                // Only act if a refund big enough to include the copies was actually paid recently.
                int hit = recentIncome.FindIndex(x => x.Value >= extra);
                if (hit < 0 || mChargeBuild == null)
                {
                    Log("garage " + g + " is gone but no matching refund was paid; nothing to take back.");
                    continue;
                }
                recentIncome.RemoveAt(hit);
                decimal balance = world.WalletDollars(MoonCrew.LocalPlayerId);
                decimal take = Math.Min(extra, Math.Max(0m, balance));
                if (take > 0m)
                {
                    mChargeBuild.Invoke(world, new object[] { MoonCrew.LocalPlayerId, -take });
                }
                Log("garage " + g + " demolished: took back " + take + " of refund that came from free vehicle copies.");
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
