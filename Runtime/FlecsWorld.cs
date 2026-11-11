using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ecaty.Flecs
{
    [DisallowMultipleComponent]
    public sealed class FlecsWorld : MonoBehaviour
    {
        [Tooltip("Local Flecs REST/Explorer port (127.0.0.1). Editor and development builds only; release players never open it.")]
        public int restPort = 27750;
        // REST is unauthenticated and read-write, so release players never start it (docs/FLECS-ARCHITECTURE-AUDIT.md).
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public ushort EffectiveRestPort => (ushort)restPort;
#else
        public ushort EffectiveRestPort => 0;
#endif
        public TextAsset initialScript;
        public string scriptEntity = "World";
        public Material presentationMaterial;
        public FlecsSession Session
        {
            get; private set;
        }
        public int ViewCount => objects.Count;
        readonly Dictionary<ulong, object> poseOwners = new();
        readonly Dictionary<ulong, object> appearanceOwners = new();
        public bool AcquireAppearance(ulong id, object owner)
        {
            if (appearanceOwners.TryGetValue(id, out var current) && !ReferenceEquals(current, owner))
                return false;
            appearanceOwners[id] = owner;
            return true;
        }
        public void ReleaseAppearance(ulong id, object owner)
        {
            if (appearanceOwners.TryGetValue(id, out var current) && ReferenceEquals(current, owner))
                appearanceOwners.Remove(id);
        }
        public bool AcquirePose(ulong id, object owner)
        {
            if (poseOwners.TryGetValue(id, out var current) && !ReferenceEquals(current, owner))
                return false;
            poseOwners[id] = owner;
            return true;
        }
        public void ReleasePose(ulong id, object owner)
        {
            if (poseOwners.TryGetValue(id, out var current) && ReferenceEquals(current, owner))
                poseOwners.Remove(id);
        }
        public string Error
        {
            get; private set;
        }
        private readonly Dictionary<ulong, PresentedEntity> objects = new Dictionary<ulong, PresentedEntity>();
        private readonly HashSet<ulong> seen = new HashSet<ulong>();
        private readonly List<ulong> removed = new List<ulong>();
        private FlecsSession.View[] acknowledgements = new FlecsSession.View[32];
        private int appliedFrame;
        private Material material;
        private MaterialPropertyBlock properties;
        private sealed class PresentedEntity
        {
            public GameObject Root, Mesh;
            public Renderer Renderer;
            public TextMesh Text;
            public int Kind = int.MinValue;
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            try
            {
                Initialize(EffectiveRestPort, initialScript, scriptEntity);
            }
            catch (Exception ex) { Error = ex.Message; Debug.LogException(ex, this); enabled = false; }
        }
        public void Initialize(ushort port, TextAsset source = null, string path = "World")
        {
            Shutdown();
            Error = null;
            Session = new FlecsSession(port);
            if (source)
                Session.SetManagedScript(path, source.text);
            SyncViews();
        }
        private void Update()
        {
            if (Session == null || Session.IsDisposed)
                return;
            Session.Tick(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            SyncViews();
        }
        public void SyncViews()
        {
            if (Session == null || Session.IsDisposed)
                return;
            int count = Session.ReadViews(out var views);
            seen.Clear();
            // First pass creates all parents before assigning hierarchy.
            for (int i = 0; i < count; i++)
            {
                var v = views[i];
                seen.Add(v.Entity);
                if (!objects.TryGetValue(v.Entity, out var view))
                {
                    view = new PresentedEntity { Root = new GameObject() };
                    view.Root.transform.SetParent(transform, false);
                    objects.Add(v.Entity, view);
                }
                view.Root.name = Session.EntityName(v.Entity);
            }
            // Detach survivors before removing old parents; never destroy a surviving child.
            for (int i = 0; i < count; i++)
            {
                var v = views[i];
                var view = objects[v.Entity];
                Transform parent = v.Parent != 0 && seen.Contains(v.Parent) && objects.TryGetValue(v.Parent, out var p)
                    ? p.Root.transform : transform;
                if (view.Root.transform.parent != parent)
                    view.Root.transform.SetParent(parent, false);
                if (!poseOwners.ContainsKey(v.Entity))
                {
                    view.Root.transform.localPosition = new Vector3(v.Px, v.Py, v.Pz);
                    view.Root.transform.localRotation = Quaternion.Euler(v.Rx, v.Ry, v.Rz);
                }
                view.Root.transform.localScale = new Vector3(v.Sx, v.Sy, v.Sz);
                if (view.Kind != v.Primitive)
                {
                    DestroyOwnedObject(view.Mesh);
                    view.Mesh = null;
                    view.Renderer = null;
                    view.Kind = v.Primitive;
                    if (v.Primitive >= 0 && v.Primitive <= 5)
                    {
                        PrimitiveType[] kinds = { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule,
                            PrimitiveType.Cylinder, PrimitiveType.Plane, PrimitiveType.Quad };
                        view.Mesh = GameObject.CreatePrimitive(kinds[v.Primitive]);
                        view.Mesh.name = "Visual";
                        view.Mesh.transform.SetParent(view.Root.transform, false);
                        // This hello world is presentation only: no hidden second physics model.
                        DestroyOwnedObject(view.Mesh.GetComponent<Collider>());
                        view.Renderer = view.Mesh.GetComponent<Renderer>();
                        EnsureMaterial();
                        view.Renderer.sharedMaterial = material;
                    }
                }
                var color = new Color(v.R, v.G, v.B, v.A);
                if (view.Renderer && !appearanceOwners.ContainsKey(v.Entity))
                {
                    properties.Clear();
                    properties.SetColor("_Color", color);
                    properties.SetColor("_BaseColor", color);
                    view.Renderer.SetPropertyBlock(properties);
                }
                if (v.HasLabel != 0)
                {
                    if (!view.Text)
                    {
                        var text = new GameObject("Label");
                        text.transform.SetParent(view.Root.transform, false);
                        view.Text = text.AddComponent<TextMesh>();
                        view.Text.anchor = TextAnchor.MiddleCenter;
                        view.Text.alignment = TextAlignment.Center;
                        view.Text.fontSize = 64;
                        view.Text.characterSize = 0.06f;
                        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                        view.Text.font = font;
                        view.Text.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                    }
                    view.Text.text = Session.Label(v.Entity);
                    view.Text.color = color;
                }
                else if (view.Text)
                {
                    DestroyOwnedObject(view.Text.gameObject);
                    view.Text = null;
                }
            }
            // Read actual object properties after application, into a separate observation component.
            if (acknowledgements.Length < count)
                acknowledgements = new FlecsSession.View[Math.Max(count, acknowledgements.Length * 2)];
            for (int i = 0; i < count; i++)
            {
                var source = views[i];
                var view = objects[source.Entity];
                var t = view.Root.transform;
                var position = t.localPosition;
                var rotation = t.localEulerAngles;
                var scale = t.localScale;
                Color color = Color.white;
                if (view.Renderer)
                {
                    view.Renderer.GetPropertyBlock(properties);
                    color = properties.GetColor("_BaseColor");
                }
                else if (view.Text)
                    color = view.Text.color;
                acknowledgements[i] = new FlecsSession.View
                {
                    Entity = source.Entity,
                    Parent = source.Parent,
                    Px = position.x,
                    Py = position.y,
                    Pz = position.z,
                    Rx = rotation.x,
                    Ry = rotation.y,
                    Rz = rotation.z,
                    Sx = scale.x,
                    Sy = scale.y,
                    Sz = scale.z,
                    R = color.r,
                    G = color.g,
                    B = color.b,
                    A = color.a,
                    Primitive = view.Renderer ? view.Kind : -1,
                    HasLabel = view.Text ? 1 : 0
                };
            }
            Session.WritePresented(acknowledgements, count, ++appliedFrame);
            removed.Clear();
            foreach (var pair in objects)
            if (!seen.Contains(pair.Key))
                removed.Add(pair.Key);
            foreach (var id in removed)
            {
                DestroyOwnedObject(objects[id].Root);
                objects.Remove(id);
            }
        }
        private void EnsureMaterial()
        {
            if (material)
                return;
            var bundled = presentationMaterial;
            var shader = bundled ? bundled.shader : Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (!shader)
                throw new InvalidOperationException("No supported shader found.");
            material = bundled ? new Material(bundled) : new Material(shader);
            material.name = "Flecs shared material";
            properties = new MaterialPropertyBlock();
        }
        public GameObject FindView(ulong entity) => objects.TryGetValue(entity, out var value) ? value.Root : null;
        public void Shutdown()
        {
            poseOwners.Clear();
            appearanceOwners.Clear();
            if (Session != null)
            {
                Session.Dispose();
                Session = null;
            }
            // Destroy only top-level owned views; child destruction follows hierarchy.
            foreach (var view in objects.Values)
                if (view.Root && view.Root.transform.parent == transform)
                    DestroyOwnedObject(view.Root);
            objects.Clear();
            DestroyOwnedObject(material);
            material = null;
            properties = null;
        }
        private void OnDisable() => Shutdown();
        private void OnDestroy() => Shutdown();
        private static void DestroyOwnedObject(UnityEngine.Object value)
        {
            if (!value)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }
    }
}
