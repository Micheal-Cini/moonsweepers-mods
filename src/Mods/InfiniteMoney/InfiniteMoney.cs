// Infinite Money: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// The crew's shared wallet stays topped up at $10,000,000, so you can buy anything.
// The host presses K to turn it off or on (it's on by default, remembered per save).
//
// Your real money keeps counting in the background: whatever you earn while it's on is added to it, and
// purchases made while it's on are free. Turn it off and the wallet goes back to that real amount.
// Only the host's copy matters (the host's game owns the wallet); everyone else just sees the balance.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;

namespace InfiniteMoney
{
    public static class ModEntry
    {
        public static void Init()
        {
            try
            {
                GameObject go = new GameObject("VettaTech InfiniteMoney");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<Wallet>();
                Debug.Log("[InfiniteMoney] v" + Wallet.Version + " loaded.");
            }
            catch (Exception e)
            {
                Debug.Log("[InfiniteMoney] failed to start: " + e);
            }
        }
    }

    public sealed class Wallet : MonoBehaviour
    {
        public const string Version = "1.0.0";
        private const string StateFile = "vettatech-money.txt";
        private static readonly decimal Target = 10000000m;

        private static FieldInfo fProgressDirty;
        private static FieldInfo fWalletDirty;

        private MoonWorld world;
        private MoonWalker walker;
        private string loadedFor;
        private bool on = true;
        private decimal real;
        private bool realDirty;
        private float nextTick;
        private float nextSave;
        private string toast;
        private float toastUntil;
        private GUIStyle style;
        private bool failed;

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined && (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost); }
        }

        private void Update()
        {
            if (failed)
            {
                return;
            }
            try
            {
                HandleKey();
                if (Time.unscaledTime >= nextTick)
                {
                    nextTick = Time.unscaledTime + 0.25f;
                    Tick();
                }
            }
            catch (Exception e)
            {
                failed = true;
                Debug.Log("[InfiniteMoney] turned off after an error: " + e);
            }
        }

        private bool Ready()
        {
            if (world == null || walker == null)
            {
                world = UnityEngine.Object.FindAnyObjectByType<MoonWorld>();
                walker = UnityEngine.Object.FindAnyObjectByType<MoonWalker>();
                if (fProgressDirty == null)
                {
                    fProgressDirty = typeof(MoonWorld).GetField("progressDirty", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fWalletDirty = typeof(MoonCrewSession).GetField("walletDirty", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
            }
            return world != null && world.Terrain != null && !string.IsNullOrEmpty(world.SaveDirectory);
        }

        private void HandleKey()
        {
            Keyboard kb = MoonInput.PlayKeyboard;
            if (kb == null || !kb.kKey.wasPressedThisFrame || walker == null || walker.UIBlocked || Cursor.lockState != CursorLockMode.Locked || !Ready())
            {
                return;
            }
            if (!IsAuthority)
            {
                Show("Only the host can turn infinite money on or off.");
                return;
            }
            Load();
            if (on)
            {
                on = false;
                Set(Math.Max(0m, real));
                Show("Infinite money OFF. Wallet back to your real money: " + MoonWorld.FormatMoney(Math.Max(0m, real)) + "  (K to turn on)");
            }
            else
            {
                on = true;
                real = world.WalletDollars();
                Set(Target);
                Show("Infinite money ON  (K to turn off)");
            }
            Save();
        }

        private void Tick()
        {
            if (!Ready() || !IsAuthority)
            {
                return;
            }
            if (Load())
            {
                Show(on ? "Infinite money ON  (host: K to turn off)" : "Infinite money is off  (host: K to turn on)");
            }
            if (!on)
            {
                return;
            }
            decimal balance = world.WalletDollars();
            if (balance > Target)
            {
                real += balance - Target; // money earned since the last top-up
                realDirty = true;
            }
            if (balance != Target)
            {
                Set(Target);
            }
            if (realDirty && Time.unscaledTime >= nextSave)
            {
                Save();
            }
        }

        private void Set(decimal dollars)
        {
            world.SetTeamDollars(dollars);
            if (fProgressDirty != null)
            {
                fProgressDirty.SetValue(world, true);
            }
            MoonCrewSession session = MoonCrewSession.Instance;
            if (fWalletDirty != null && session != null)
            {
                fWalletDirty.SetValue(session, true); // tell the crew right away
            }
        }

        // Returns true when a different save was just loaded.
        private bool Load()
        {
            string dir = world.SaveDirectory;
            if (dir == loadedFor)
            {
                return false;
            }
            loadedFor = dir;
            on = true;
            real = world.WalletDollars();
            string path = Path.Combine(dir, StateFile);
            if (File.Exists(path))
            {
                try
                {
                    string[] parts = File.ReadAllText(path).Trim().Split('\t');
                    on = parts[0] == "on";
                    decimal r;
                    if (parts.Length > 1 && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out r))
                    {
                        real = r;
                    }
                }
                catch (Exception e)
                {
                    Debug.Log("[InfiniteMoney] couldn't read " + StateFile + ": " + e.Message);
                }
            }
            else if (real >= Target)
            {
                real = 0m;
            }
            Save();
            return true;
        }

        private void Save()
        {
            realDirty = false;
            nextSave = Time.unscaledTime + 5f;
            try
            {
                File.WriteAllText(Path.Combine(loadedFor, StateFile), (on ? "on" : "off") + "\t" + real.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Debug.Log("[InfiniteMoney] couldn't save " + StateFile + ": " + e.Message);
            }
        }

        private void Show(string text)
        {
            toast = text;
            toastUntil = Time.unscaledTime + 5f;
        }

        private void OnGUI()
        {
            if (failed || toast == null || Time.unscaledTime > toastUntil)
            {
                return;
            }
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(16, Screen.height / 45), wordWrap = false };
                style.normal.textColor = new Color(0.75f, 1f, 0.75f);
            }
            Vector2 size = style.CalcSize(new GUIContent(toast));
            Rect r = new Rect((Screen.width - size.x) / 2f, Screen.height * 0.24f, size.x, size.y);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(r.x - 14f, r.y - 6f, r.width + 28f, r.height + 12f), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(r, toast, style);
        }
    }
}
