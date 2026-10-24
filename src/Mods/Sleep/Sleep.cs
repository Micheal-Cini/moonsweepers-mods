// Sleep: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// At night, lie down in a bunk (the base's bunk beds: aim and press Interact). When everyone in the
// crew is in a bunk, the night is skipped: after a few seconds the host moves the lunar clock forward to
// morning at the host's location, and the game's own clock sync brings everyone along.
//
// Who is in bed is shared through a Steam lobby member note ("vt_sleep"), the same way the game shares
// its own "ready" flag, so no game network messages are changed. Solo: just you.

using System;
using System.Reflection;
using MoonCleaners;
using Steamworks;
using UnityEngine;

namespace Sleep
{
    public static class ModEntry
    {
        public static void Init()
        {
            try
            {
                GameObject go = new GameObject("VettaTech Sleep");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<SleepMod>();
                Debug.Log("[Sleep] v" + SleepMod.Version + " loaded.");
            }
            catch (Exception e)
            {
                Debug.Log("[Sleep] failed to start: " + e);
            }
        }
    }

    public sealed class SleepMod : MonoBehaviour
    {
        public const string Version = "1.0.0";
        private const string Key = "vt_sleep";
        private const float FallAsleepSeconds = 4f;
        private const float WakeElevation = 10f;   // degrees of sun above the horizon at "morning"
        private const float NightBelow = 0f;       // sun below the horizon = night

        private MoonWalker walker;
        private MoonSolarSystem solar;
        private string sentState = "";
        private float nextSend;
        private float allAsleepSince = -1f;
        private float cooldownUntil;
        private float morningUntil;
        private int inBed;
        private int crewSize = 1;
        private bool failed;

        private void Update()
        {
            if (failed)
            {
                return;
            }
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                failed = true;
                Debug.Log("[Sleep] turned off after an error: " + e);
            }
        }

        private static bool IsAuthority
        {
            get { return !MoonCrew.Joined && (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost); }
        }

        private bool LocalInBunk()
        {
            if (walker == null || !walker.Resting)
            {
                return false;
            }
            MoonLoungePlayer lounge = walker.GetComponent<MoonLoungePlayer>();
            return lounge == null || lounge.PoseCode == 0; // lounge seats also "rest": those don't count
        }

        private bool LocalNight()
        {
            return solar != null && solar.LunarDay && solar.SunHeight < NightBelow;
        }

        private void Tick()
        {
            if (walker == null || solar == null)
            {
                walker = UnityEngine.Object.FindAnyObjectByType<MoonWalker>();
                solar = UnityEngine.Object.FindAnyObjectByType<MoonSolarSystem>();
                if (walker == null || solar == null || walker.world == null)
                {
                    return;
                }
            }

            bool sleeping = LocalInBunk() && LocalNight();
            string state = sleeping ? "1" : "0";
            if (MoonSteamLobby.InLobby && (state != sentState || Time.unscaledTime >= nextSend))
            {
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(MoonSteamLobby.LobbyId), Key, state);
                sentState = state;
                nextSend = Time.unscaledTime + 10f;
            }

            // Count the crew in bed (everyone computes it for the on-screen note; only the host acts).
            inBed = sleeping ? 1 : 0;
            crewSize = 1;
            if (MoonSteamLobby.InLobby && MoonSteamLobby.Members != null)
            {
                CSteamID lobby = new CSteamID(MoonSteamLobby.LobbyId);
                crewSize = 0;
                inBed = 0;
                foreach (MoonSteamLobby.Member m in MoonSteamLobby.Members)
                {
                    if (m == null)
                    {
                        continue;
                    }
                    crewSize++;
                    bool asleep = m.local ? sleeping : SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(m.id), Key) == "1";
                    if (asleep)
                    {
                        inBed++;
                    }
                }
                crewSize = Math.Max(crewSize, 1);
            }

            if (!IsAuthority || Time.unscaledTime < cooldownUntil)
            {
                allAsleepSince = -1f;
                return;
            }
            bool everyone = sleeping && inBed >= crewSize;
            if (!everyone)
            {
                allAsleepSince = -1f;
                return;
            }
            if (allAsleepSince < 0f)
            {
                allAsleepSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - allAsleepSince >= FallAsleepSeconds)
            {
                SkipToMorning();
                allAsleepSince = -1f;
                cooldownUntil = Time.unscaledTime + 30f;
            }
        }

        private void SkipToMorning()
        {
            MoonWorld world = walker.world;
            Vector3 up = (walker.transform.position - world.transform.position).normalized;
            float now = solar.LunarTime;
            float threshold = Mathf.Sin(WakeElevation * Mathf.Deg2Rad);
            float prev = Vector3.Dot(MoonSolarSystem.SunDirectionAt(now), up);
            float target = -1f;
            for (int k = 1; k <= 1440; k++)
            {
                float t = now + k / 1440f;
                float e = Vector3.Dot(MoonSolarSystem.SunDirectionAt(t), up);
                if (prev < threshold && e >= threshold)
                {
                    target = t;
                    break;
                }
                prev = e;
            }
            if (target < 0f)
            {
                Debug.Log("[Sleep] couldn't find a sunrise here; not skipping.");
                return;
            }
            solar.SetLunarTime(Mathf.Repeat(target, 1f));
            PushClockToCrew();
            morningUntil = Time.unscaledTime + 6f;
            Debug.Log("[Sleep] everyone slept: skipped " + Mathf.RoundToInt(Mathf.Repeat(target - now, 1f) * solar.lunarDaySeconds / 60f) + " minutes to morning.");
        }

        // The host sends the clock every 5 seconds; send it right away so nobody waits in the dark.
        private static void PushClockToCrew()
        {
            try
            {
                Type session = typeof(MoonWorld).Assembly.GetType("MoonCleaners.MoonCrewSession");
                if (session == null)
                {
                    return;
                }
                PropertyInfo inst = session.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object s = inst != null ? inst.GetValue(null, null) : null;
                MethodInfo send = session.GetMethod("SendSky", BindingFlags.NonPublic | BindingFlags.Instance);
                if (s != null && send != null)
                {
                    send.Invoke(s, null);
                }
            }
            catch (Exception e)
            {
                Debug.Log("[Sleep] clock will sync on its own (" + e.Message + ").");
            }
        }

        private GUIStyle style;

        private void OnGUI()
        {
            if (failed || walker == null)
            {
                return;
            }
            string text = null;
            if (Time.unscaledTime < morningUntil)
            {
                text = "Good morning! The night was skipped.";
            }
            else if (LocalInBunk())
            {
                if (!LocalNight())
                {
                    text = "You can only sleep through the night.";
                }
                else if (crewSize > 1 && inBed < crewSize)
                {
                    text = "Sleeping... " + inBed + "/" + crewSize + " crew in a bunk. Everyone needs to lie down.";
                }
                else
                {
                    text = "Sleeping...";
                }
            }
            if (text == null)
            {
                return;
            }
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(16, Screen.height / 45), richText = true, wordWrap = false };
                style.normal.textColor = new Color(0.95f, 0.93f, 0.85f);
            }
            Vector2 size = style.CalcSize(new GUIContent(text));
            Rect r = new Rect((Screen.width - size.x) / 2f, Screen.height * 0.18f, size.x, size.y);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(r.x - 14f, r.y - 6f, r.width + 28f, r.height + 12f), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(r, text, style);
        }
    }
}
