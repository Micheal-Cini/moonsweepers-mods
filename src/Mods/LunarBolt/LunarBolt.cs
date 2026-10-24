// Lunar Bolt: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Adds an original vehicle to every garage: the Lunar Bolt, a low electric roadster ($1,000).
//   - Fast: much higher top speed than the other vehicles, sharp steering.
//   - Sweeps: a wide dust intake underneath; cleaning switches on when you get in (LMB toggles it).
//   - Afterburner: hold SPACE for rocket thrust (on the ground or in the air).
//   - Flight: hold CTRL to lift off. In the air: W/S pitch, A/D bank and turn, SPACE thrust; at speed it
//     glides like a plane. Let go of CTRL and it settles back down.
//   - Speed limit: press - and = to set a top-speed cap (shown on screen), handy for clean sweeping.
// Every player needs this mod to see and keep the Lunar Bolt (the game drops vehicles it doesn't know).
// If you uninstall it, the game silently drops Lunar Bolts from the save.

using System;
using System.Reflection;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LunarBolt
{
    public static class ModEntry
    {
        public static void Init()
        {
            try
            {
                Bolt.Register();
            }
            catch (Exception e)
            {
                Debug.Log("[LunarBolt] failed to start: " + e);
            }
        }
    }

    internal static class Bolt
    {
        public const string Version = "1.0.0";
        public const int Index = 22;                      // next free slot after the game's 22 vehicle models
        public const string Name = "Lunar Bolt";
        public const decimal Price = 1000m;
        internal static bool Ready;

        internal static void Log(string msg)
        {
            Debug.Log("[LunarBolt] " + msg);
        }

        internal static void Register()
        {
            FieldInfo fVehicles = AccessTools.Field(typeof(MoonPrototypeCatalog), "Vehicles");
            FieldInfo fOffers = AccessTools.Field(typeof(MoonWorld), "GarageOffers");
            var vehicles = (MoonPrototypeCatalog.Item[])fVehicles.GetValue(null);
            var offers = (MoonWorld.GarageOffer[])fOffers.GetValue(null);
            if (vehicles.Length != Index)
            {
                Log("the game's vehicle list changed (" + vehicles.Length + " models, expected " + Index + "). Mod is off until it's updated.");
                return;
            }

            // 1. The vehicle model entry (name, work, hint, sweep width, depth, reach, top speed m/s, turn deg/s, cargo).
            var bigger = new MoonPrototypeCatalog.Item[Index + 1];
            Array.Copy(vehicles, bigger, vehicles.Length);
            bigger[Index] = new MoonPrototypeCatalog.Item(MoonLoc.N(Name), MoonPrototypeCatalog.Work.Clean,
                "W drive | S brake/reverse | A/D steer | SPACE afterburner | CTRL fly | -/= speed limit | F exit | LMB toggle cleaning",
                2.1f, 0.6f, 6f, 24f, 110f, 10);
            fVehicles.SetValue(null, bigger);

            // 2. Sold in every garage.
            var moreOffers = new MoonWorld.GarageOffer[offers.Length + 1];
            Array.Copy(offers, moreOffers, offers.Length);
            moreOffers[offers.Length] = new MoonWorld.GarageOffer
            {
                vehicle = Index,
                price = Price,
                summary = "An electric roadster with rocket afterburners and a wide dust intake underneath. Hold Space to boost, Ctrl to take off and fly."
            };
            fOffers.SetValue(null, moreOffers);

            // 3. Model, driving extras, no stunt money while flying.
            Harmony h = new Harmony("vettatech.moonsweepers.lunarbolt");
            h.Patch(AccessTools.Method(typeof(MoonPrototypeModels), "Vehicle", new[] { typeof(int), typeof(Transform) }),
                prefix: new HarmonyMethod(typeof(Patches), "BuildModel"));
            h.Patch(AccessTools.Method(typeof(MoonPrototypeVehicle), "Initialize"),
                postfix: new HarmonyMethod(typeof(Patches), "AfterInitialize"));
            MethodInfo airSteer = AccessTools.Method(typeof(MoonPrototypeVehicle), "AirSteer");
            if (airSteer != null)
            {
                h.Patch(airSteer, prefix: new HarmonyMethod(typeof(Patches), "SkipAirSteerWhileFlying"));
            }
            Ready = true;
            Log("v" + Version + " loaded: " + Name + " added to the garages ($" + Price + ").");
        }
    }

    internal static class Patches
    {
        private static MethodInfo mMat;

        private static Material Mat(MoonPrototypeModels models, string name, Color color)
        {
            if (mMat == null)
            {
                mMat = AccessTools.Method(typeof(MoonPrototypeModels), "Mat", new[] { typeof(string), typeof(Color) });
            }
            return (Material)mMat.Invoke(models, new object[] { name, color });
        }

        // MoonPrototypeModels.Vehicle(int index, Transform parent): build the roadster for our index.
        public static bool BuildModel(MoonPrototypeModels __instance, int index, Transform parent, ref GameObject __result)
        {
            if (index != Bolt.Index)
            {
                return true;
            }
            try
            {
                __result = Model.Build(__instance, parent, (n, c) => Mat(__instance, n, c));
                return false;
            }
            catch (Exception e)
            {
                Bolt.Log("model failed, using the plain one: " + e.Message);
                return true;
            }
        }

        // MoonPrototypeVehicle.Initialize(owner, index, storageId)
        public static void AfterInitialize(MoonPrototypeVehicle __instance)
        {
            try
            {
                if (__instance.Index != Bolt.Index)
                {
                    return;
                }
                Component tricks = __instance.GetComponent("MoonVehicleTricks");
                if (tricks != null)
                {
                    UnityEngine.Object.Destroy(tricks); // flying would farm the air-time stunt money
                }
                if (__instance.GetComponent<BoltDrive>() == null)
                {
                    __instance.gameObject.AddComponent<BoltDrive>();
                }
            }
            catch (Exception e)
            {
                Bolt.Log("setup failed: " + e.Message);
            }
        }

        // MoonPrototypeVehicle.AirSteer(float dt): our flight controls replace it while flying.
        public static bool SkipAirSteerWhileFlying(MoonPrototypeVehicle __instance)
        {
            try
            {
                if (__instance.Index != Bolt.Index)
                {
                    return true;
                }
                BoltDrive d = __instance.GetComponent<BoltDrive>();
                return d == null || !d.Flying;
            }
            catch
            {
                return true;
            }
        }
    }

    internal static class Model
    {
        internal static GameObject Build(MoonPrototypeModels m, Transform parent, Func<string, Color, Material> mat)
        {
            Material paint = mat("Lunar Bolt paint", new Color(0.08f, 0.22f, 0.62f));
            Material trim = mat("Lunar Bolt trim", new Color(0.92f, 0.93f, 0.95f));
            Material black = mat("Lunar Bolt black", new Color(0.06f, 0.06f, 0.07f));
            Material glass = mat("Lunar Bolt glass", new Color(0.35f, 0.55f, 0.7f));
            Material chrome = mat("Lunar Bolt chrome", new Color(0.7f, 0.72f, 0.75f));
            Material flame = mat("Lunar Bolt flame", new Color(1f, 0.55f, 0.12f));
            Material glow = mat("Lunar Bolt glow", new Color(0.4f, 0.85f, 1f));

            GameObject go = new GameObject(Bolt.Name + " / mod");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            Transform t = go.transform;
            const float L = 3.9f;

            // Body: low, wide wedge.
            m.Box(t, "Floor pan", new Vector3(0f, 0.5f, 0f), new Vector3(1.7f, 0.18f, L), black);
            m.Box(t, "Lower body", new Vector3(0f, 0.72f, 0f), new Vector3(1.78f, 0.32f, L * 0.96f), paint);
            m.Box(t, "Nose", new Vector3(0f, 0.7f, L * 0.47f), new Vector3(1.6f, 0.22f, 0.4f), paint);
            m.Box(t, "Hood", new Vector3(0f, 0.9f, L * 0.3f), new Vector3(1.66f, 0.1f, 1.2f), paint).localRotation = Quaternion.Euler(-7f, 0f, 0f);
            m.Box(t, "Rear deck", new Vector3(0f, 0.93f, -L * 0.33f), new Vector3(1.7f, 0.14f, 1.15f), paint);
            m.Box(t, "Side stripe L", new Vector3(-0.9f, 0.76f, 0f), new Vector3(0.02f, 0.06f, L * 0.9f), trim);
            m.Box(t, "Side stripe R", new Vector3(0.9f, 0.76f, 0f), new Vector3(0.02f, 0.06f, L * 0.9f), trim);

            // Open cockpit with two seats side by side.
            m.Box(t, "Windscreen", new Vector3(0f, 1.18f, 0.62f), new Vector3(1.45f, 0.45f, 0.05f), glass).localRotation = Quaternion.Euler(-30f, 0f, 0f);
            m.Box(t, "Dash", new Vector3(0f, 1.0f, 0.5f), new Vector3(1.4f, 0.16f, 0.25f), black);
            for (int s = -1; s <= 1; s += 2)
            {
                m.Box(t, "Seat", new Vector3(s * 0.38f, 0.98f, 0.12f), new Vector3(0.5f, 0.12f, 0.5f), black);
                m.Box(t, "Seat back", new Vector3(s * 0.38f, 1.28f, -0.15f), new Vector3(0.5f, 0.55f, 0.1f), black);
                m.Box(t, "Roll hoop", new Vector3(s * 0.38f, 1.62f, -0.25f), new Vector3(0.42f, 0.06f, 0.06f), chrome);
            }
            m.Rod(t, "Steering column", new Vector3(-0.38f, 0.95f, 0.55f), new Vector3(-0.38f, 1.28f, 0.4f), 0.03f, chrome);
            m.Cylinder(t, "Steering hub", new Vector3(-0.38f, 1.3f, 0.38f), 0.17f, 0.04f, black).localRotation = Quaternion.Euler(60f, 0f, 0f);

            // Rear spoiler.
            m.Box(t, "Spoiler", new Vector3(0f, 1.22f, -L * 0.47f), new Vector3(1.6f, 0.05f, 0.28f), black);
            for (int s = -1; s <= 1; s += 2)
            {
                m.Box(t, "Spoiler post", new Vector3(s * 0.6f, 1.08f, -L * 0.46f), new Vector3(0.05f, 0.25f, 0.12f), black);
            }

            // Twin rocket nozzles with flames (scaled by the afterburner).
            for (int s = -1; s <= 1; s += 2)
            {
                m.Cylinder(t, "Rocket nozzle", new Vector3(s * 0.45f, 0.82f, -L * 0.52f), 0.17f, 0.35f, chrome).localRotation = Quaternion.Euler(90f, 0f, 0f);
                m.Cylinder(t, "Rocket throat", new Vector3(s * 0.45f, 0.82f, -L * 0.55f), 0.12f, 0.05f, glow).localRotation = Quaternion.Euler(90f, 0f, 0f);
                Transform f = m.Cylinder(t, "Flame", new Vector3(s * 0.45f, 0.82f, -L * 0.62f), 0.11f, 0.6f, flame);
                f.localRotation = Quaternion.Euler(90f, 0f, 0f);
                f.gameObject.SetActive(false);
            }

            // Dust intake under the nose.
            m.Box(t, "Dust intake", new Vector3(0f, 0.22f, L * 0.42f), new Vector3(1.9f, 0.12f, 0.35f), black);
            m.Box(t, "Intake lip", new Vector3(0f, 0.3f, L * 0.5f), new Vector3(1.9f, 0.06f, 0.06f), glow);

            // Wheels (named "Wheel" so the game spins them).
            for (int s = -1; s <= 1; s += 2)
            {
                for (int a = -1; a <= 1; a += 2)
                {
                    float z = a * L * 0.31f;
                    m.Cylinder(t, "Wheel", new Vector3(s * 0.86f, 0.36f, z), 0.36f, 0.26f, black).localRotation = Quaternion.Euler(0f, 0f, 90f);
                    m.Cylinder(t, "Hub", new Vector3(s * 0.995f, 0.36f, z), 0.2f, 0.03f, chrome).localRotation = Quaternion.Euler(0f, 0f, 90f);
                }
            }

            // Headlight and taillight strips.
            m.Box(t, "Headlights", new Vector3(0f, 0.8f, L * 0.5f + 0.02f), new Vector3(1.3f, 0.05f, 0.02f), glow);
            m.Box(t, "Taillights", new Vector3(0f, 0.86f, -L * 0.5f - 0.02f), new Vector3(1.5f, 0.05f, 0.02f), flame);

            // Colliders (same layout the game uses for its own vehicles).
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.85f, 0f);
            box.size = new Vector3(1.75f, 0.7f, L);
            for (int s = -1; s <= 1; s += 2)
            {
                for (int a = -1; a <= 1; a += 2)
                {
                    SphereCollider c = go.AddComponent<SphereCollider>();
                    c.center = new Vector3(s * 0.82f, 0.36f, a * L * 0.31f);
                    c.radius = 0.35f;
                }
            }
            return go;
        }
    }

    public sealed class BoltDrive : MonoBehaviour
    {
        private const float Afterburner = 14f;     // m/s^2 of rocket thrust
        private const float Climb = 4.5f;          // m/s^2 above gravity while holding Ctrl
        private const float MaxAltitude = 300f;    // the game won't respawn vehicles above ~400 m
        private const float GlideSpeed = 25f;      // m/s at which the body fully carries its weight
        private static readonly int[] Limits = { 0, 20, 30, 40, 50, 60, 80, 100, 120, 150, 200 }; // km/h, 0 = off
        private static int limitStep;              // remembered while the game runs

        private static FieldInfo fBody;
        private static FieldInfo fCleaningLatched;

        private MoonPrototypeVehicle vehicle;
        private Rigidbody body;
        private Transform[] flames;
        private bool wasDriven;
        private bool burning;
        private float flameScale;
        private GUIStyle style;

        public bool Flying { get; private set; }

        private void Awake()
        {
            vehicle = GetComponent<MoonPrototypeVehicle>();
            if (fBody == null)
            {
                fBody = AccessTools.Field(typeof(MoonPrototypeVehicle), "body");
                fCleaningLatched = AccessTools.Field(typeof(MoonPrototypeVehicle), "cleaningLatched");
            }
            var list = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in GetComponentsInChildren<Transform>(true))
            {
                if (c.name == "Flame")
                {
                    list.Add(c);
                }
            }
            flames = list.ToArray();
        }

        private bool Controlling
        {
            get
            {
                if (vehicle == null || !vehicle.IsDriven)
                {
                    return false;
                }
                MoonWalker w = vehicle.Owner;
                return w != null && !w.UIBlocked && !w.Building && Application.isFocused && Cursor.lockState == CursorLockMode.Locked;
            }
        }

        private void Update()
        {
            try
            {
                if (vehicle == null)
                {
                    return;
                }
                bool driven = vehicle.IsDriven;
                if (driven && !wasDriven && fCleaningLatched != null && !(bool)fCleaningLatched.GetValue(vehicle))
                {
                    vehicle.ToggleCleaning(); // it's a sweeper: start with the intake on
                }
                wasDriven = driven;
                if (!Controlling)
                {
                    return;
                }
                Keyboard kb = MoonInput.PlayKeyboard;
                if (kb == null)
                {
                    return;
                }
                if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
                {
                    limitStep = limitStep <= 0 ? Limits.Length - 1 : limitStep - 1;
                }
                if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
                {
                    limitStep = (limitStep + 1) % Limits.Length;
                }
                // Flame flicker.
                flameScale = Mathf.MoveTowards(flameScale, burning ? 1f : 0f, Time.deltaTime * 6f);
                for (int i = 0; i < flames.Length; i++)
                {
                    if (flames[i] == null)
                    {
                        continue;
                    }
                    flames[i].gameObject.SetActive(flameScale > 0.02f);
                    float f = flameScale * (0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 18f, i));
                    flames[i].localScale = new Vector3(0.22f, 0.3f * f + 0.01f, 0.22f);
                }
            }
            catch (Exception e)
            {
                Bolt.Log("update error: " + e.Message);
                enabled = false;
            }
        }

        private void FixedUpdate()
        {
            try
            {
                Fly();
            }
            catch (Exception e)
            {
                Bolt.Log("drive error: " + e.Message);
                enabled = false;
            }
        }

        private void Fly()
        {
            if (body == null && fBody != null)
            {
                body = (Rigidbody)fBody.GetValue(vehicle);
            }
            MoonWorld world = vehicle != null ? vehicle.World : null;
            if (body == null || body.isKinematic || world == null || !vehicle.IsDriven)
            {
                Flying = false;
                burning = false;
                return;
            }
            Keyboard kb = Controlling ? MoonInput.PlayKeyboard : null;
            bool burn = kb != null && kb.spaceKey.isPressed && !vehicle.Flipped;
            bool lift = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            burning = burn;

            float dt = Time.fixedDeltaTime;
            Vector3 center = world.transform.position;
            Vector3 up = (body.position - center).normalized;
            float gravity = world.settings.gravity;
            float altitude = (body.position - center).magnitude - world.settings.radius;

            if (lift)
            {
                Flying = true;
            }
            else if (vehicle.Grounded)
            {
                Flying = false;
            }

            // Afterburner.
            if (burn)
            {
                body.AddForce(transform.forward * Afterburner, ForceMode.Acceleration);
            }

            // Lift off / climb.
            if (lift && altitude < MaxAltitude)
            {
                body.AddForce(up * (gravity + Climb), ForceMode.Acceleration);
            }

            if (Flying && !vehicle.Grounded)
            {
                // Plane controls: S/Down nose up, W/Up nose down, A/D bank and turn.
                float pitch = 0f;
                float turn = 0f;
                if (kb != null)
                {
                    pitch = (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f) - (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f);
                    turn = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
                }
                body.AddRelativeTorque(new Vector3(-pitch * 2.2f, turn * 1.6f, -turn * 1.4f), ForceMode.Acceleration);

                // Keep it flyable: damp spin and slowly level the wings.
                body.angularVelocity *= 1f - Mathf.Clamp01(2.5f * dt);
                float rollError = Vector3.Dot(transform.right, up);
                body.AddRelativeTorque(new Vector3(0f, 0f, rollError * 3f), ForceMode.Acceleration);

                // Wings: forward speed carries the weight, and the flight path follows the nose.
                Vector3 vel = body.linearVelocity;
                float forward = Vector3.Dot(vel, transform.forward);
                float carry = Mathf.Clamp01(forward / GlideSpeed);
                body.AddForce(up * gravity * carry, ForceMode.Acceleration);
                if (vel.magnitude > 4f)
                {
                    body.linearVelocity = Vector3.Lerp(vel, transform.forward * vel.magnitude, Mathf.Clamp01(1.2f * dt));
                }
            }

            // Altitude ceiling.
            if (altitude > MaxAltitude)
            {
                Vector3 vel = body.linearVelocity;
                float climbing = Vector3.Dot(vel, up);
                if (climbing > 0f)
                {
                    body.linearVelocity = vel - up * climbing;
                }
            }

            // Speed limit.
            int limit = Limits[Mathf.Clamp(limitStep, 0, Limits.Length - 1)];
            if (limit > 0)
            {
                float max = limit / 3.6f;
                Vector3 vel = body.linearVelocity;
                float speed = vel.magnitude;
                if (speed > max)
                {
                    body.linearVelocity = vel * (max / speed);
                }
            }
        }

        private void OnGUI()
        {
            if (vehicle == null || !vehicle.IsDriven || body == null)
            {
                return;
            }
            int limit = Limits[Mathf.Clamp(limitStep, 0, Limits.Length - 1)];
            float kmh = body.linearVelocity.magnitude * 3.6f;
            MoonWorld world = vehicle.World;
            float altitude = world != null ? (body.position - world.transform.position).magnitude - world.settings.radius : 0f;
            string text = Bolt.Name.ToUpperInvariant() + "   " + Mathf.RoundToInt(kmh) + " km/h"
                + "   |   LIMIT " + (limit > 0 ? limit + " km/h" : "OFF") + "  ( - / = )"
                + (Flying ? "   |   FLYING  " + Mathf.RoundToInt(Mathf.Max(0f, altitude)) + " m" : "")
                + (burning ? "   |   AFTERBURNER" : "")
                + "\nSPACE afterburner    CTRL fly    W/S pitch  A/D bank (in the air)";
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 55), richText = false, wordWrap = false };
                style.normal.textColor = new Color(0.9f, 0.96f, 1f);
            }
            Vector2 size = style.CalcSize(new GUIContent(text));
            Rect r = new Rect((Screen.width - size.x) / 2f, Screen.height * 0.06f, size.x, size.y);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(r.x - 12f, r.y - 6f, r.width + 24f, r.height + 12f), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(r, text, style);
        }
    }
}
