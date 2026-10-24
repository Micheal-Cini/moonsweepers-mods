// Lunar Bolt: a Moon Sweepers mod by VettaTech (https://vettatech.ca)
// Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Adds an original vehicle to every garage: the Lunar Bolt, a two-seat electric roadster ($1,000).
//   - Drive: fast, sharp steering, strong downforce so it stays planted. Sweeps a wide strip
//     (cleaning switches on when you get in; LMB toggles it).
//   - SPACE: rocket afterburner (along the ground when driving, so it doesn't launch you).
//   - J: hover mode. Jets under the car hold it ~1.6 m off the ground. W/S drive, A/D turn.
//   - CTRL: take off / climb. In the air a flight stabiliser keeps it level: W nose down, S nose up,
//     A/D bank into a turn; let go and it levels out. SPACE for thrust. It lands when it touches down.
//   - - and =: speed limit (shown on screen).
//   - U: horn. A rude one. Crewmates with the mod hear it too.
// Every player needs this mod to see and keep the Lunar Bolt (the game drops vehicles it doesn't know).
// If you uninstall it, the game silently drops Lunar Bolts from the save.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MoonCleaners;
using Steamworks;
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
        public const string Version = "2.0.0";
        public const int Index = 22;                      // next free slot after the game's 22 vehicle models
        public const string Name = "Lunar Bolt";
        public const decimal Price = 1000m;

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

            var bigger = new MoonPrototypeCatalog.Item[Index + 1];
            Array.Copy(vehicles, bigger, vehicles.Length);
            bigger[Index] = new MoonPrototypeCatalog.Item(MoonLoc.N(Name), MoonPrototypeCatalog.Work.Clean,
                "W drive | S brake | A/D steer | SPACE afterburner | J hover | CTRL fly | -/= speed limit | U horn | F exit | LMB cleaning",
                2.1f, 0.6f, 6f, 24f, 110f, 10);
            fVehicles.SetValue(null, bigger);

            var moreOffers = new MoonWorld.GarageOffer[offers.Length + 1];
            Array.Copy(offers, moreOffers, offers.Length);
            moreOffers[offers.Length] = new MoonWorld.GarageOffer
            {
                vehicle = Index,
                price = Price,
                summary = "A two-seat electric roadster with rocket afterburners, hover jets and a wide dust intake. Space boosts, J hovers, Ctrl takes off."
            };
            fOffers.SetValue(null, moreOffers);

            Harmony h = new Harmony("vettatech.moonsweepers.lunarbolt");
            h.Patch(AccessTools.Method(typeof(MoonPrototypeModels), "Vehicle", new[] { typeof(int), typeof(Transform) }),
                prefix: new HarmonyMethod(typeof(Patches), "BuildModel"));
            h.Patch(AccessTools.Method(typeof(MoonPrototypeVehicle), "Initialize"),
                postfix: new HarmonyMethod(typeof(Patches), "AfterInitialize"));
            MethodInfo airSteer = AccessTools.Method(typeof(MoonPrototypeVehicle), "AirSteer");
            if (airSteer != null)
            {
                h.Patch(airSteer, prefix: new HarmonyMethod(typeof(Patches), "SkipAirSteer"));
            }

            GameObject go = new GameObject("VettaTech LunarBolt horn");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Horn>();

            Log("v" + Version + " loaded: " + Name + " added to the garages ($" + Price + ").");
        }
    }

    internal static class Patches
    {
        private static MethodInfo mMat;

        internal static Material Mat(MoonPrototypeModels models, string name, Color color, float metallic, float smooth, Color? emission = null)
        {
            if (mMat == null)
            {
                mMat = AccessTools.Method(typeof(MoonPrototypeModels), "Mat", new[] { typeof(string), typeof(Color) });
            }
            Material m = (Material)mMat.Invoke(models, new object[] { name, color });
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
            }
            return m;
        }

        public static bool BuildModel(MoonPrototypeModels __instance, int index, Transform parent, ref GameObject __result)
        {
            if (index != Bolt.Index)
            {
                return true;
            }
            try
            {
                __result = Model.Build(__instance, parent);
                return false;
            }
            catch (Exception e)
            {
                Bolt.Log("model failed, using the plain one: " + e);
                return true;
            }
        }

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

        // The game's loose air controls are replaced by our stabiliser while flying or hovering.
        public static bool SkipAirSteer(MoonPrototypeVehicle __instance)
        {
            try
            {
                if (__instance.Index != Bolt.Index)
                {
                    return true;
                }
                BoltDrive d = __instance.GetComponent<BoltDrive>();
                return d == null || !d.InControl;
            }
            catch
            {
                return true;
            }
        }
    }

    // ---------------------------------------------------------------------------------------- model

    internal static class Model
    {
        // Body sections, tail to nose: z, half width, bottom, top. A low roadster with wheel arches and a
        // dipped cockpit between them. Interpolated smoothly into a lofted mesh.
        private static readonly float[][] Sections =
        {
            new[] { -2.04f, 0.64f, 0.50f, 0.78f },
            new[] { -1.94f, 0.82f, 0.42f, 0.90f },
            new[] { -1.70f, 0.88f, 0.36f, 0.95f },
            new[] { -1.52f, 0.91f, 0.62f, 0.97f },
            new[] { -1.25f, 0.92f, 0.80f, 0.98f },
            new[] { -0.98f, 0.91f, 0.62f, 0.96f },
            new[] { -0.80f, 0.89f, 0.34f, 0.92f },
            new[] { -0.45f, 0.88f, 0.32f, 0.80f },
            new[] { 0.00f, 0.88f, 0.32f, 0.78f },
            new[] { 0.45f, 0.88f, 0.32f, 0.84f },
            new[] { 0.80f, 0.89f, 0.34f, 0.88f },
            new[] { 0.98f, 0.91f, 0.62f, 0.88f },
            new[] { 1.25f, 0.92f, 0.80f, 0.87f },
            new[] { 1.52f, 0.90f, 0.62f, 0.82f },
            new[] { 1.70f, 0.86f, 0.38f, 0.76f },
            new[] { 1.88f, 0.76f, 0.40f, 0.66f },
            new[] { 2.05f, 0.54f, 0.44f, 0.57f },
        };

        internal static GameObject Build(MoonPrototypeModels m, Transform parent)
        {
            Material paint = Patches.Mat(m, "Bolt paint", new Color(0.05f, 0.12f, 0.34f), 0.7f, 0.86f);
            Material black = Patches.Mat(m, "Bolt black", new Color(0.04f, 0.04f, 0.045f), 0.2f, 0.45f);
            Material tyre = Patches.Mat(m, "Bolt tyre", new Color(0.05f, 0.05f, 0.05f), 0f, 0.15f);
            Material rim = Patches.Mat(m, "Bolt rim", new Color(0.62f, 0.64f, 0.67f), 0.95f, 0.8f);
            Material glass = Patches.Mat(m, "Bolt glass", new Color(0.08f, 0.12f, 0.15f), 0.1f, 0.96f);
            Material leather = Patches.Mat(m, "Bolt seats", new Color(0.12f, 0.1f, 0.09f), 0f, 0.35f);
            Material chrome = Patches.Mat(m, "Bolt chrome", new Color(0.8f, 0.82f, 0.85f), 1f, 0.92f);
            Material head = Patches.Mat(m, "Bolt headlights", new Color(0.9f, 0.95f, 1f), 0f, 0.9f, new Color(2.2f, 2.4f, 2.6f));
            Material tail = Patches.Mat(m, "Bolt taillights", new Color(0.7f, 0.05f, 0.05f), 0f, 0.9f, new Color(2.5f, 0.15f, 0.1f));
            Material flame = Patches.Mat(m, "Bolt flame", new Color(1f, 0.55f, 0.15f), 0f, 0.5f, new Color(4f, 1.6f, 0.4f));
            Material jet = Patches.Mat(m, "Bolt jets", new Color(0.4f, 0.8f, 1f), 0f, 0.5f, new Color(0.8f, 2.2f, 3.5f));

            GameObject go = new GameObject(Bolt.Name + " / mod");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            Transform t = go.transform;

            // Smooth body shell.
            GameObject shell = new GameObject("Body");
            shell.transform.SetParent(t, false);
            Mesh bodyMesh = Loft(Sections, 32, 6);
            shell.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            var meshes = AccessTools.Field(typeof(MoonPrototypeModels), "meshes")?.GetValue(m) as List<Mesh>;
            if (meshes != null)
            {
                meshes.Add(bodyMesh); // freed with the game's own vehicle meshes
            }
            shell.AddComponent<MeshRenderer>().sharedMaterial = paint;

            // Lower details: splitter, side skirts, diffuser, intake.
            m.Box(t, "Front splitter", new Vector3(0f, 0.36f, 1.98f), new Vector3(1.4f, 0.04f, 0.22f), black);
            m.Box(t, "Front intake", new Vector3(0f, 0.47f, 2.04f), new Vector3(0.9f, 0.1f, 0.05f), black);
            m.Box(t, "Rear diffuser", new Vector3(0f, 0.4f, -1.98f), new Vector3(1.2f, 0.08f, 0.2f), black);
            for (int s = -1; s <= 1; s += 2)
            {
                m.Box(t, "Side skirt", new Vector3(s * 0.9f, 0.34f, 0f), new Vector3(0.05f, 0.06f, 1.7f), black);
            }

            // Lights.
            for (int s = -1; s <= 1; s += 2)
            {
                m.Box(t, "Headlight", new Vector3(s * 0.55f, 0.64f, 1.93f), new Vector3(0.36f, 0.05f, 0.08f), head).localRotation = Quaternion.Euler(-10f, s * 18f, 0f);
            }
            m.Box(t, "Taillight bar", new Vector3(0f, 0.8f, -1.97f), new Vector3(1.4f, 0.04f, 0.04f), tail);

            // Cockpit: windscreen, door tops, two seats with headrests, steering wheel, mirrors.
            m.Box(t, "Windscreen", new Vector3(0f, 1.07f, 0.6f), new Vector3(1.34f, 0.6f, 0.03f), glass).localRotation = Quaternion.Euler(-55f, 0f, 0f);
            m.Box(t, "Windscreen frame", new Vector3(0f, 1.24f, 0.35f), new Vector3(1.38f, 0.04f, 0.05f), black).localRotation = Quaternion.Euler(-55f, 0f, 0f);
            m.Box(t, "Dash", new Vector3(0f, 0.88f, 0.55f), new Vector3(1.4f, 0.12f, 0.3f), black);
            for (int s = -1; s <= 1; s += 2)
            {
                m.Box(t, "Door top", new Vector3(s * 0.86f, 0.9f, -0.25f), new Vector3(0.1f, 0.05f, 1.35f), black);
                m.Box(t, "Seat", new Vector3(s * 0.38f, 0.8f, 0.05f), new Vector3(0.5f, 0.12f, 0.52f), leather);
                m.Box(t, "Seat back", new Vector3(s * 0.38f, 1.08f, -0.24f), new Vector3(0.48f, 0.55f, 0.12f), leather).localRotation = Quaternion.Euler(-12f, 0f, 0f);
                m.Box(t, "Headrest", new Vector3(s * 0.38f, 1.42f, -0.32f), new Vector3(0.28f, 0.16f, 0.1f), leather);
                m.Box(t, "Mirror arm", new Vector3(s * 0.88f, 0.98f, 0.62f), new Vector3(0.14f, 0.03f, 0.03f), black);
                m.Box(t, "Mirror", new Vector3(s * 0.98f, 1.02f, 0.6f), new Vector3(0.12f, 0.08f, 0.05f), paint);
            }
            m.Rod(t, "Steering column", new Vector3(-0.38f, 0.78f, 0.5f), new Vector3(-0.38f, 1.0f, 0.33f), 0.025f, black);
            m.Cylinder(t, "Steering wheel", new Vector3(-0.38f, 1.02f, 0.31f), 0.16f, 0.025f, black).localRotation = Quaternion.Euler(55f, 0f, 0f);

            // Twin rocket nozzles in the rear deck, with flames.
            for (int s = -1; s <= 1; s += 2)
            {
                m.Cylinder(t, "Rocket nozzle", new Vector3(s * 0.42f, 0.66f, -2.02f), 0.13f, 0.18f, chrome).localRotation = Quaternion.Euler(90f, 0f, 0f);
                m.Cylinder(t, "Rocket throat", new Vector3(s * 0.42f, 0.66f, -2.1f), 0.09f, 0.02f, flame).localRotation = Quaternion.Euler(90f, 0f, 0f);
                Transform f = m.Cylinder(t, "Flame", new Vector3(s * 0.42f, 0.66f, -2.35f), 0.09f, 0.5f, flame);
                f.localRotation = Quaternion.Euler(90f, 0f, 0f);
                f.gameObject.SetActive(false);
            }

            // Hover jets underneath.
            for (int s = -1; s <= 1; s += 2)
            {
                for (int a = -1; a <= 1; a += 2)
                {
                    Vector3 p = new Vector3(s * 0.5f, 0.3f, a * 0.75f);
                    m.Cylinder(t, "Hover jet", p, 0.14f, 0.05f, jet);
                    Transform hf = m.Cylinder(t, "Hover flame", p + new Vector3(0f, -0.25f, 0f), 0.1f, 0.4f, jet);
                    hf.gameObject.SetActive(false);
                }
            }

            // Wheels: tyre + rim + hub on a "Wheel" pivot (the game spins anything named "Wheel").
            for (int s = -1; s <= 1; s += 2)
            {
                for (int a = -1; a <= 1; a += 2)
                {
                    Transform w = new GameObject("Wheel").transform;
                    w.SetParent(t, false);
                    w.localPosition = new Vector3(s * 0.86f, 0.36f, a * 1.25f);
                    w.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    m.Cylinder(w, "Tyre", Vector3.zero, 0.36f, 0.27f, tyre);
                    m.Cylinder(w, "Rim", new Vector3(0f, s * -0.005f, 0f), 0.25f, 0.28f, rim);
                    m.Cylinder(w, "Hub", new Vector3(0f, s * -0.14f, 0f), 0.07f, 0.02f, chrome);
                    for (int k = 0; k < 5; k++)
                    {
                        Transform spoke = m.Box(w, "Spoke", new Vector3(0f, s * -0.13f, 0f), new Vector3(0.06f, 0.015f, 0.42f), rim);
                        spoke.localRotation = Quaternion.Euler(0f, k * 36f, 0f);
                    }
                    m.Cylinder(w, "Brake", new Vector3(0f, s * 0.04f, 0f), 0.18f, 0.03f, black);
                }
            }

            // Colliders (same layout the game uses for its own vehicles).
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.72f, 0f);
            box.size = new Vector3(1.8f, 0.6f, 4.0f);
            for (int s = -1; s <= 1; s += 2)
            {
                for (int a = -1; a <= 1; a += 2)
                {
                    SphereCollider c = go.AddComponent<SphereCollider>();
                    c.center = new Vector3(s * 0.82f, 0.36f, a * 1.25f);
                    c.radius = 0.35f;
                }
            }
            return go;
        }

        private static float CatmullRom(float p0, float p1, float p2, float p3, float u)
        {
            float u2 = u * u;
            float u3 = u2 * u;
            return 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
        }

        private static float Shape(float v, float power)
        {
            return Mathf.Sign(v) * Mathf.Pow(Mathf.Abs(v), power);
        }

        // A smooth closed body: rounded-rectangle rings along z, capped at both ends.
        private static Mesh Loft(float[][] sections, int ringPoints, int samples)
        {
            List<float[]> rows = new List<float[]>();
            for (int i = 0; i < sections.Length - 1; i++)
            {
                float[] a = sections[Math.Max(0, i - 1)];
                float[] b = sections[i];
                float[] c = sections[i + 1];
                float[] d = sections[Math.Min(sections.Length - 1, i + 2)];
                for (int k = 0; k < samples; k++)
                {
                    float u = k / (float)samples;
                    float[] r = new float[4];
                    for (int j = 0; j < 4; j++)
                    {
                        r[j] = CatmullRom(a[j], b[j], c[j], d[j], u);
                    }
                    rows.Add(r);
                }
            }
            rows.Add(sections[sections.Length - 1]);

            List<Vector3> v = new List<Vector3>();
            List<int> tris = new List<int>();
            foreach (float[] r in rows)
            {
                float mid = (r[2] + r[3]) * 0.5f;
                float hh = Mathf.Max(0.01f, (r[3] - r[2]) * 0.5f);
                for (int k = 0; k < ringPoints; k++)
                {
                    float th = k * Mathf.PI * 2f / ringPoints;
                    float c = Mathf.Cos(th);
                    float s = Mathf.Sin(th);
                    v.Add(new Vector3(r[1] * Shape(c, 0.45f), mid + hh * Shape(s, s > 0f ? 0.6f : 0.3f), r[0]));
                }
            }
            for (int i = 0; i < rows.Count - 1; i++)
            {
                for (int k = 0; k < ringPoints; k++)
                {
                    int k1 = (k + 1) % ringPoints;
                    int a = i * ringPoints + k;
                    int b = i * ringPoints + k1;
                    int c = (i + 1) * ringPoints + k;
                    int d = (i + 1) * ringPoints + k1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }
            // End caps with their own vertices so they shade flat.
            for (int end = 0; end < 2; end++)
            {
                int ring = end == 0 ? 0 : rows.Count - 1;
                Vector3 center = Vector3.zero;
                int start = v.Count;
                for (int k = 0; k < ringPoints; k++)
                {
                    Vector3 p = v[ring * ringPoints + k];
                    center += p;
                    v.Add(p);
                }
                center /= ringPoints;
                int ci = v.Count;
                v.Add(center);
                for (int k = 0; k < ringPoints; k++)
                {
                    int k1 = (k + 1) % ringPoints;
                    if (end == 0)
                    {
                        tris.Add(ci); tris.Add(start + k1); tris.Add(start + k);
                    }
                    else
                    {
                        tris.Add(ci); tris.Add(start + k); tris.Add(start + k1);
                    }
                }
            }
            Mesh mesh = new Mesh { name = "Lunar Bolt body" };
            mesh.SetVertices(v);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // ---------------------------------------------------------------------------------------- driving

    public sealed class BoltDrive : MonoBehaviour
    {
        private enum Mode { Drive, Hover, Flight }

        private const float Afterburner = 14f;     // m/s^2 rocket thrust
        private const float Downforce = 3f;        // m/s^2 extra grip when driving (+ more with speed)
        private const float Climb = 4.5f;          // m/s^2 above gravity while holding Ctrl
        private const float MaxAltitude = 300f;    // the game won't respawn vehicles above ~400 m
        private const float HoverHeight = 1.6f;
        private static readonly int[] Limits = { 0, 20, 30, 40, 50, 60, 80, 100, 120, 150, 200 }; // km/h, 0 = off
        private static int limitStep;

        private static FieldInfo fBody;
        private static FieldInfo fCleaningLatched;

        private MoonPrototypeVehicle vehicle;
        private Rigidbody body;
        private readonly List<Transform> flames = new List<Transform>();
        private readonly List<Transform> hoverFlames = new List<Transform>();
        private Mode mode = Mode.Drive;
        private Vector3 heading;
        private bool wasDriven;
        private bool burning;
        private float flameScale;
        private float jetScale;
        private float groundHeight = -1f;
        private GUIStyle style;
        private AudioSource audioSource;

        public bool InControl { get { return mode != Mode.Drive; } }

        private void Awake()
        {
            vehicle = GetComponent<MoonPrototypeVehicle>();
            if (fBody == null)
            {
                fBody = AccessTools.Field(typeof(MoonPrototypeVehicle), "body");
                fCleaningLatched = AccessTools.Field(typeof(MoonPrototypeVehicle), "cleaningLatched");
            }
            foreach (Transform c in GetComponentsInChildren<Transform>(true))
            {
                if (c.name == "Flame")
                {
                    flames.Add(c);
                }
                else if (c.name == "Hover flame")
                {
                    hoverFlames.Add(c);
                }
            }
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.maxDistance = 80f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
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
                if (!driven)
                {
                    mode = Mode.Drive;
                }
                wasDriven = driven;

                Keyboard kb = Controlling ? MoonInput.PlayKeyboard : null;
                if (kb != null)
                {
                    if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
                    {
                        limitStep = limitStep <= 0 ? Limits.Length - 1 : limitStep - 1;
                    }
                    if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
                    {
                        limitStep = (limitStep + 1) % Limits.Length;
                    }
                    if (kb.jKey.wasPressedThisFrame)
                    {
                        mode = mode == Mode.Hover ? Mode.Drive : Mode.Hover;
                        heading = Vector3.zero;
                    }
                    if (kb.uKey.wasPressedThisFrame)
                    {
                        Horn.Honk(audioSource);
                    }
                }

                flameScale = Mathf.MoveTowards(flameScale, burning ? 1f : 0f, Time.deltaTime * 6f);
                jetScale = Mathf.MoveTowards(jetScale, mode != Mode.Drive && driven ? 1f : 0f, Time.deltaTime * 4f);
                for (int i = 0; i < flames.Count; i++)
                {
                    float f = flameScale * (0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 18f, i));
                    flames[i].gameObject.SetActive(flameScale > 0.02f);
                    flames[i].localScale = new Vector3(0.18f, 0.25f * f + 0.01f, 0.18f);
                }
                for (int i = 0; i < hoverFlames.Count; i++)
                {
                    float f = jetScale * (0.8f + 0.4f * Mathf.PerlinNoise(Time.time * 14f, i + 7));
                    hoverFlames[i].gameObject.SetActive(jetScale > 0.02f);
                    hoverFlames[i].localScale = new Vector3(0.2f, 0.2f * f + 0.01f, 0.2f);
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
                Step();
            }
            catch (Exception e)
            {
                Bolt.Log("drive error: " + e.Message);
                enabled = false;
            }
        }

        private void Step()
        {
            if (body == null && fBody != null)
            {
                body = (Rigidbody)fBody.GetValue(vehicle);
            }
            MoonWorld world = vehicle != null ? vehicle.World : null;
            if (body == null || body.isKinematic || world == null || !vehicle.IsDriven)
            {
                burning = false;
                return;
            }
            Keyboard kb = Controlling ? MoonInput.PlayKeyboard : null;
            bool burn = kb != null && kb.spaceKey.isPressed && !vehicle.Flipped;
            bool lift = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            float fwdIn = kb == null ? 0f : (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f);
            float turnIn = kb == null ? 0f : (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
            burning = burn;

            float dt = Time.fixedDeltaTime;
            Vector3 center = world.transform.position;
            Vector3 up = (body.position - center).normalized;
            float gravity = world.settings.gravity;
            float altitude = (body.position - center).magnitude - world.settings.radius;

            if (lift)
            {
                mode = Mode.Flight;
            }
            else if (mode == Mode.Flight && vehicle.Grounded)
            {
                mode = Mode.Drive;
            }
            if (heading == Vector3.zero || mode == Mode.Drive)
            {
                heading = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            }
            heading = Vector3.ProjectOnPlane(heading, up).normalized;

            switch (mode)
            {
                case Mode.Drive:
                    {
                        Vector3 vel = body.linearVelocity;
                        bool grounded = vehicle.Grounded;
                        // Planted: press into the road (more with speed); if a bump lifts it, pull it straight back down.
                        if (grounded)
                        {
                            body.AddForce(-transform.up * (Downforce + 0.08f * vel.magnitude), ForceMode.Acceleration);
                        }
                        else
                        {
                            body.AddForce(-up * (Downforce + gravity * 2.5f), ForceMode.Acceleration);
                            float rising = Vector3.Dot(vel, up);
                            if (rising > 1f)
                            {
                                body.linearVelocity = vel - up * (rising - 1f);
                            }
                        }
                        if (burn)
                        {
                            Vector3 along = Vector3.ProjectOnPlane(transform.forward, up).normalized;
                            body.AddForce(along * Afterburner, ForceMode.Acceleration);
                        }
                        if (!grounded)
                        {
                            Stabilise(up, heading, 0f, 0f, dt, 2.5f); // land on its wheels
                        }
                        break;
                    }
                case Mode.Hover:
                    {
                        float h = HeightAboveGround(up);
                        Vector3 vel = body.linearVelocity;
                        float vUp = Vector3.Dot(vel, up);
                        float err = HoverHeight - (h < 0f ? HoverHeight + 2f : h);
                        body.AddForce(up * (gravity + 9f * err - 4f * vUp), ForceMode.Acceleration);
                        heading = Quaternion.AngleAxis(turnIn * 75f * dt, up) * heading;
                        body.AddForce(heading * fwdIn * 9f, ForceMode.Acceleration);
                        if (burn)
                        {
                            body.AddForce(heading * Afterburner, ForceMode.Acceleration);
                        }
                        Vector3 flat = Vector3.ProjectOnPlane(vel, up);
                        Vector3 side = flat - heading * Vector3.Dot(flat, heading);
                        body.AddForce(-side * 2.5f - flat * (fwdIn == 0f && !burn ? 0.6f : 0.1f), ForceMode.Acceleration);
                        Stabilise(up, heading, 0f, turnIn * 12f, dt, 5f);
                        break;
                    }
                case Mode.Flight:
                    {
                        Vector3 vel = body.linearVelocity;
                        float forward = Vector3.Dot(vel, transform.forward);
                        heading = Quaternion.AngleAxis(turnIn * 55f * dt, up) * heading;
                        // Lift: wings carry the weight at speed; a gentle float when slow; Ctrl climbs.
                        float carry = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(forward / 22f));
                        body.AddForce(up * gravity * carry, ForceMode.Acceleration);
                        if (lift && altitude < MaxAltitude)
                        {
                            body.AddForce(up * Climb, ForceMode.Acceleration);
                        }
                        // Cruise thrust so it keeps flying; Space for more.
                        if (forward < 22f)
                        {
                            body.AddForce(transform.forward * 5f, ForceMode.Acceleration);
                        }
                        if (burn)
                        {
                            body.AddForce(transform.forward * Afterburner, ForceMode.Acceleration);
                        }
                        // Flight path follows the nose; vertical bobbing damped.
                        if (vel.magnitude > 4f)
                        {
                            body.linearVelocity = Vector3.Lerp(vel, transform.forward * vel.magnitude, Mathf.Clamp01(1.5f * dt));
                        }
                        // W nose down, S nose up; A/D bank. Let go and it flies level.
                        Stabilise(up, heading, -fwdIn * 22f + (lift ? 8f : 0f), turnIn * 30f, dt, 4f);
                        break;
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

        // Turn the car toward a target attitude: facing `heading` (along the surface), nose pitched by
        // `pitchDeg` (+ = up), banked by `bankDeg` (+ = right wing down). Directly steers angular velocity,
        // which is what keeps it from tumbling.
        private void Stabilise(Vector3 up, Vector3 headingDir, float pitchDeg, float bankDeg, float dt, float gain)
        {
            Vector3 right = Vector3.Cross(up, headingDir).normalized;
            Vector3 fwd = Quaternion.AngleAxis(-pitchDeg, right) * headingDir;
            Vector3 upT = Quaternion.AngleAxis(-bankDeg, fwd) * Vector3.Cross(fwd, right).normalized;
            if (Vector3.Dot(upT, up) < 0f)
            {
                upT = -upT;
            }
            Quaternion target = Quaternion.LookRotation(fwd, upT);
            Quaternion delta = target * Quaternion.Inverse(body.rotation);
            float angle;
            Vector3 axis;
            delta.ToAngleAxis(out angle, out axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f)
            {
                return;
            }
            if (angle > 180f)
            {
                angle -= 360f;
            }
            Vector3 want = axis.normalized * (angle * Mathf.Deg2Rad * gain);
            want = Vector3.ClampMagnitude(want, 3.5f);
            body.angularVelocity = Vector3.Lerp(body.angularVelocity, want, Mathf.Clamp01(8f * dt));
        }

        private static readonly RaycastHit[] hits = new RaycastHit[8];

        private float HeightAboveGround(Vector3 up)
        {
            Vector3 origin = body.position + up * 0.6f;
            int n = Physics.RaycastNonAlloc(origin, -up, hits, 12f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].rigidbody == body)
                {
                    continue;
                }
                best = Mathf.Min(best, hits[i].distance - 0.6f);
            }
            groundHeight = best == float.MaxValue ? -1f : best;
            return groundHeight;
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
            string modeText = mode == Mode.Flight ? "FLYING " + Mathf.RoundToInt(Mathf.Max(0f, altitude)) + " m" : mode == Mode.Hover ? "HOVER" : "DRIVE";
            string text = Bolt.Name.ToUpperInvariant() + "   " + Mathf.RoundToInt(kmh) + " km/h   |   " + modeText
                + "   |   LIMIT " + (limit > 0 ? limit + " km/h" : "OFF") + "  ( - / = )"
                + (burning ? "   |   AFTERBURNER" : "")
                + "\nSPACE afterburner    J hover    CTRL fly    U horn"
                + (mode == Mode.Flight ? "    W nose down  S nose up  A/D bank" : "");
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

    // ---------------------------------------------------------------------------------------- horn

    public sealed class Horn : MonoBehaviour
    {
        private const string Key = "vt_honk";
        private static AudioClip clip;
        private static int sent;
        private static readonly Dictionary<ulong, string> heard = new Dictionary<ulong, string>();
        private AudioSource flat;
        private float nextPoll;

        internal static AudioClip Clip
        {
            get
            {
                if (clip == null)
                {
                    clip = Synthesize();
                }
                return clip;
            }
        }

        // Local horn on the car; tell the crew through a Steam lobby note so they hear it too.
        internal static void Honk(AudioSource source)
        {
            if (source != null)
            {
                source.pitch = UnityEngine.Random.Range(0.85f, 1.15f);
                source.PlayOneShot(Clip, 1f);
            }
            try
            {
                if (MoonSteamLobby.InLobby)
                {
                    sent++;
                    SteamMatchmaking.SetLobbyMemberData(new CSteamID(MoonSteamLobby.LobbyId), Key, sent.ToString());
                }
            }
            catch
            {
            }
        }

        private void Awake()
        {
            flat = gameObject.AddComponent<AudioSource>();
            flat.spatialBlend = 0f;
            flat.volume = 0.55f;
        }

        private void Update()
        {
            if (Time.unscaledTime < nextPoll)
            {
                return;
            }
            nextPoll = Time.unscaledTime + 0.15f;
            try
            {
                if (!MoonSteamLobby.InLobby || MoonSteamLobby.Members == null)
                {
                    heard.Clear();
                    return;
                }
                CSteamID lobby = new CSteamID(MoonSteamLobby.LobbyId);
                foreach (MoonSteamLobby.Member m in MoonSteamLobby.Members)
                {
                    if (m == null || m.local)
                    {
                        continue;
                    }
                    string v = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(m.id), Key);
                    string last;
                    if (heard.TryGetValue(m.id, out last))
                    {
                        if (!string.IsNullOrEmpty(v) && v != last)
                        {
                            flat.pitch = UnityEngine.Random.Range(0.85f, 1.15f);
                            flat.PlayOneShot(Clip, 0.8f);
                        }
                    }
                    heard[m.id] = v;
                }
            }
            catch
            {
            }
        }

        // A synthesized horn of the rude variety: a buzzy low tone that wobbles and drops in pitch.
        private static AudioClip Synthesize()
        {
            const int rate = 44100;
            const float seconds = 1.1f;
            int n = (int)(rate * seconds);
            float[] data = new float[n];
            System.Random rng = new System.Random(7);
            float phase = 0f;
            float noise = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float u = t / seconds;
                float freq = Mathf.Lerp(118f, 62f, Mathf.Pow(u, 0.7f)) * (1f + 0.08f * Mathf.Sin(t * 2f * Mathf.PI * 7f));
                phase += freq / rate;
                phase -= Mathf.Floor(phase);
                float saw = phase * 2f - 1f;
                float pulse = phase < 0.32f ? 1f : -0.45f;
                noise = noise * 0.8f + (float)(rng.NextDouble() * 2.0 - 1.0) * 0.2f;
                float flutter = 0.55f + 0.45f * Mathf.Sin(t * 2f * Mathf.PI * (26f + 14f * u));
                float env = Mathf.Clamp01(t / 0.03f) * Mathf.Clamp01((seconds - t) / 0.25f) * (u > 0.82f ? Mathf.Lerp(1f, 0.3f, (u - 0.82f) / 0.18f) : 1f);
                data[i] = Mathf.Clamp((0.45f * saw + 0.35f * pulse + 0.35f * noise) * flutter * env, -1f, 1f) * 0.8f;
            }
            AudioClip c = AudioClip.Create("Lunar Bolt horn", n, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
