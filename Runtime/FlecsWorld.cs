using System;
using System.Collections.Generic;
using UnityEngine;

namespace Runtime.Flecs
{
    [DisallowMultipleComponent]
    public sealed class FlecsWorld : MonoBehaviour
    {
        [Tooltip("Local Flecs REST/Explorer port (127.0.0.1). Editor and development builds only; release players never open it.")]
        public int restPort = 27750;
        [Tooltip("REST refuses requests that modify the world (inspection only).")]
        public bool restReadOnly;
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
            {
                appearanceOwners.Remove(id);
                // Flecs data may not change again, so show it now instead of keeping what the owner left.
                if (objects.TryGetValue(id, out var view) && view.HasLast)
                    ApplyColor(view, view.Last);
            }
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
            {
                poseOwners.Remove(id);
                if (objects.TryGetValue(id, out var view) && view.HasLast)
                    ApplyPose(view, view.Last);
            }
        }
        public string Error
        {
            get; private set;
        }
        private readonly Dictionary<ulong, PresentedEntity> objects = new Dictionary<ulong, PresentedEntity>();
        // Roots back to their entity, so a removed view can find the child views hanging below it.
        private readonly Dictionary<Transform, ulong> entityOfRoot = new Dictionary<Transform, ulong>();
        // Parent entity id -> child views created before that parent became a view.
        private readonly Dictionary<ulong, List<ulong>> waiting = new Dictionary<ulong, List<ulong>>();
        private FlecsSession.View[] acknowledgements = new FlecsSession.View[32];
        private int appliedFrame;
        private readonly HashSet<ulong> acknowledged = new HashSet<ulong>();
        private Material material;
        private MaterialPropertyBlock properties;
        private static readonly int ColorId = Shader.PropertyToID("_Color"), BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly PrimitiveType[] PrimitiveKinds = { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule,
            PrimitiveType.Cylinder, PrimitiveType.Plane, PrimitiveType.Quad };
        private byte[] labelBuffer = new byte[256];
        private sealed class PresentedEntity
        {
            public GameObject Root, Mesh;
            public ulong Parent;
            public FlecsSession.View Last;
            public bool HasLast;
            public Renderer Renderer;
            public TextMesh Text;
            public int Kind = int.MinValue;
            public byte[] LabelBytes = Array.Empty<byte>();
            public int LabelLength = -1;
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
            Session = new FlecsSession(port, restReadOnly);
            if (Session.RestStatus < 0)
                Debug.LogWarning(Session.RestError + " Flecs Explorer is unavailable for this world.", this);
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
            // The spans point at native memory valid until the next ViewChanges call.
            Session.ViewChanges(out var changed, out var gone);
            for (int i = 0; i < gone.Length; i++)
                RemoveView(gone[i]);
            // First pass creates missing objects so parents exist before the second pass links and applies data.
            for (int i = 0; i < changed.Length; i++)
            {
                ulong entity = changed[i].Entity;
                if (objects.ContainsKey(entity))
                    continue;
                // Names are fetched once per new view; per-frame name strings were a steady GC source.
                var created = new PresentedEntity { Root = new GameObject(Session.EntityName(entity)) };
                created.Root.transform.SetParent(transform, false);
                objects.Add(entity, created);
                entityOfRoot.Add(created.Root.transform, entity);
                if (waiting.TryGetValue(entity, out var children))
                {
                    waiting.Remove(entity);
                    foreach (var childId in children)
                        if (objects.TryGetValue(childId, out var child) && child.Parent == entity && child.Root.transform.parent != created.Root.transform)
                            child.Root.transform.SetParent(created.Root.transform, false);
                }
            }
            for (int i = 0; i < changed.Length; i++)
            {
                ref readonly var v = ref changed[i];
                var view = objects[v.Entity];
                view.Parent = v.Parent;
                view.Last = v;
                view.HasLast = true;
                Transform parent = transform;
                if (v.Parent != 0)
                {
                    if (objects.TryGetValue(v.Parent, out var p))
                        parent = p.Root.transform;
                    else
                        WaitFor(v.Parent, v.Entity);
                }
                if (view.Root.transform.parent != parent)
                    view.Root.transform.SetParent(parent, false);
                if (!poseOwners.ContainsKey(v.Entity))
                    ApplyPose(view, v);
                view.Root.transform.localScale = new Vector3(v.Sx, v.Sy, v.Sz);
                if (view.Kind != v.Primitive)
                {
                    DestroyOwnedObject(view.Mesh);
                    view.Mesh = null;
                    view.Renderer = null;
                    view.Kind = v.Primitive;
                    if (v.Primitive >= 0 && v.Primitive <= 5)
                    {
                        view.Mesh = GameObject.CreatePrimitive(PrimitiveKinds[v.Primitive]);
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
                if (!appearanceOwners.ContainsKey(v.Entity))
                    ApplyColor(view, v);
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
                    UpdateLabel(view, v.Entity);
                    view.Text.color = color;
                }
                else if (view.Text)
                {
                    DestroyOwnedObject(view.Text.gameObject);
                    view.Text = null;
                    view.LabelLength = -1;
                }
            }
            // Presented observes what Unity shows: changed views plus pose-owned ones, whose owners move them every frame.
            acknowledged.Clear();
            int total = changed.Length + poseOwners.Count;
            if (total == 0)
                return;
            if (acknowledgements.Length < total)
                acknowledgements = new FlecsSession.View[Math.Max(total, acknowledgements.Length * 2)];
            int n = 0;
            for (int i = 0; i < changed.Length; i++)
            {
                acknowledged.Add(changed[i].Entity);
                acknowledgements[n++] = Observe(objects[changed[i].Entity], changed[i].Entity, changed[i].Parent);
            }
            foreach (var id in poseOwners.Keys)
                if (objects.TryGetValue(id, out var owned) && owned.HasLast && acknowledged.Add(id))
                    acknowledgements[n++] = Observe(owned, owned.Last.Entity, owned.Last.Parent);
            if (n > 0)
                Session.WritePresented(acknowledgements, n, ++appliedFrame);
        }
        private void ApplyPose(PresentedEntity view, in FlecsSession.View v)
        {
            view.Root.transform.localPosition = new Vector3(v.Px, v.Py, v.Pz);
            view.Root.transform.localRotation = (v.Flags & FlecsSession.ViewOriented) != 0
                ? new Quaternion(v.Qx, v.Qy, v.Qz, v.Qw) : Quaternion.Euler(v.Rx, v.Ry, v.Rz);
        }
        private void ApplyColor(PresentedEntity view, in FlecsSession.View v)
        {
            if (!view.Renderer)
                return;
            var color = new Color(v.R, v.G, v.B, v.A);
            properties.Clear();
            properties.SetColor(ColorId, color);
            properties.SetColor(BaseColorId, color);
            view.Renderer.SetPropertyBlock(properties);
        }
        private FlecsSession.View Observe(PresentedEntity view, ulong entity, ulong parent)
        {
            var t = view.Root.transform;
            var position = t.localPosition;
            var rotation = t.localEulerAngles;
            var scale = t.localScale;
            Color color = Color.white;
            if (view.Renderer)
            {
                view.Renderer.GetPropertyBlock(properties);
                color = properties.GetColor(BaseColorId);
            }
            else if (view.Text)
                color = view.Text.color;
            return new FlecsSession.View
            {
                Entity = entity,
                Parent = parent,
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
        private void WaitFor(ulong parent, ulong child)
        {
            if (!waiting.TryGetValue(parent, out var list))
                waiting.Add(parent, list = new List<ulong>());
            if (!list.Contains(child))
                list.Add(child);
        }
        private void RemoveView(ulong id)
        {
            if (!objects.TryGetValue(id, out var view))
                return;
            var root = view.Root.transform;
            // Surviving child views move to the world root; Unity would destroy them with this parent.
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (!entityOfRoot.TryGetValue(child, out ulong childId))
                    continue;
                child.SetParent(transform, false);
                WaitFor(id, childId);
            }
            entityOfRoot.Remove(root);
            if (view.Parent != 0 && waiting.TryGetValue(view.Parent, out var list))
            {
                list.Remove(id);
                if (list.Count == 0)
                    waiting.Remove(view.Parent);
            }
            DestroyOwnedObject(view.Root);
            objects.Remove(id);
        }
        // Compares the native label bytes with the last applied ones and builds a string only when the text changed.
        private void UpdateLabel(PresentedEntity view, ulong entity)
        {
            int length = Session.LabelBytes(entity, ref labelBuffer);
            if (length == view.LabelLength && length >= 0 && System.MemoryExtensions.SequenceEqual(
                new ReadOnlySpan<byte>(labelBuffer, 0, length), new ReadOnlySpan<byte>(view.LabelBytes, 0, length)))
                return;
            if (length < 0)
            {
                if (view.LabelLength != -1 || view.Text.text != "")
                    view.Text.text = "";
                view.LabelLength = -1;
                return;
            }
            if (view.LabelBytes.Length < length)
                view.LabelBytes = new byte[length];
            Buffer.BlockCopy(labelBuffer, 0, view.LabelBytes, 0, length);
            view.LabelLength = length;
            view.Text.text = System.Text.Encoding.UTF8.GetString(labelBuffer, 0, length);
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
            entityOfRoot.Clear();
            waiting.Clear();
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
