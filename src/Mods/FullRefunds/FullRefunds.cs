// Full Refunds: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Removing a building, garage or belt gives back 100% of what it cost instead of 75%.
// Every removal refund in the game goes through one calculation (MoonWorld.Refund, the
// "RemovalRefundShare" of 0.75); this mod makes it return the full price. The "+$X BACK" prompt in build
// mode shows the full amount too. Uses Harmony (installed in Mods/lib by the installer).
//
// In multiplayer the player who removes something calculates the refund, so everyone should have it.

using System;
using System.Reflection;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace FullRefunds
{
    public static class ModEntry
    {
        public const string Version = "1.0.0";

        public static void Init()
        {
            try
            {
                Patch();
            }
            catch (Exception e)
            {
                Debug.Log("[FullRefunds] could not start: " + e);
            }
        }

        private static void Patch()
        {
            MethodInfo refund = typeof(MoonWorld).GetMethod("Refund", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(decimal) }, null);
            if (refund == null || refund.ReturnType != typeof(decimal))
            {
                Debug.Log("[FullRefunds] could not find the game's refund calculation (game updated?). Mod is off.");
                return;
            }
            Harmony harmony = new Harmony("vettatech.moonsweepers.fullrefunds");
            harmony.Patch(refund, prefix: new HarmonyMethod(typeof(ModEntry), "FullPrice"));
            decimal check = (decimal)refund.Invoke(null, new object[] { 100m });
            Debug.Log("[FullRefunds] v" + Version + " loaded. Refund on a $100 item is now " + check + ".");
        }

        // Prefix: return the full price (never negative), skip the 75% calculation.
        public static bool FullPrice(decimal price, ref decimal __result)
        {
            __result = Math.Max(0m, decimal.Floor(price * 100m) / 100m);
            return false;
        }
    }
}
