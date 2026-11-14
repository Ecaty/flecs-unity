using System;
using System.Collections.Generic;
using Unity.Profiling;
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
        /// <summary>Goes up whenever a view's visual object is replaced or removed (its shape changed): presenters that hold its renderer look again.</summary>
        public int VisualGeneration { get; private set; }
        // Ownership lives in the world as exclusive (TransformOwner, owner) / (AppearanceOwner, owner) pairs.
        // These sets only mirror it for the per-frame loop; the view stream is the authority.
        readonly HashSet<ulong> poseOwned = new();
        readonly HashSet<ulong> appearanceOwned = new();
        public bool AcquireAppearance(ulong id, ulong owner)
        {
            if (Session == null || Session.IsDisposed || !Session.Claim(id, Session.AppearanceOwner, owner))
                return false;
            appearanceOwned.Add(id);
            if (objects.TryGetValue(id, out var view))
                view.ColorStale = true;
            return true;
        }
        public void ReleaseAppearance(ulong id, ulong owner)
        {
            if (Session == null || Session.IsDisposed || !Session.Release(id, Session.AppearanceOwner, owner))
                return;
            appearanceOwned.Remove(id);
            // Flecs data may not change again, so show it now instead of keeping what the owner left.
            if (objects.TryGetValue(id, out var view) && view.HasLast)
                ApplyColor(view, view.Last);
        }
        public bool AcquirePose(ulong id, ulong owner)
        {
            if (Session == null || Session.IsDisposed || !Session.Claim(id, Session.TransformOwner, owner))
                return false;
            poseOwned.Add(id);
            if (objects.TryGetValue(id, out var view))
                view.PoseStale = true;
            return true;
        }
        public void ReleasePose(ulong id, ulong owner)
        {
            if (Session == null || Session.IsDisposed || !Session.Release(id, Session.TransformOwner, owner))
                return;
            poseOwned.Remove(id);
            if (objects.TryGetValue(id, out var view) && view.HasLast)
                ApplyPose(view, view.Last);
        }
        // A physics body must not sit under a scaled or moving parent: a detached view stays under the world root
        // while its pose owner moves it, whatever its Flecs parent is.
        readonly HashSet<ulong> detached = new();
        public void SetDetached(ulong id, bool value)
        {
            if (value ? !detached.Add(id) : !detached.Remove(id))
                return;
            if (!objects.TryGetValue(id, out var view))
                return;
            var t = view.Transform;
            // The writer of the pose changes here, so the apply loop writes it again the next time it may.
            view.PoseStale = true;
            if (value)
            {
                var position = t.position;
                var rotation = t.rotation;
                t.SetParent(transform, false);
                t.SetPositionAndRotation(position, rotation);
                return;
            }
            // Back under its Flecs parent; an owner that still holds the pose keeps it, otherwise Flecs' pose applies.
            Transform parent = view.Parent != 0 && objects.TryGetValue(view.Parent, out var p) ? p.Transform : transform;
            if (t.parent != parent)
                t.SetParent(parent, false);
            if (view.HasLast && !poseOwned.Contains(id))
                ApplyPose(view, view.Last);
        }
        public string Error
        {
            get; private set;
        }
        private readonly Dictionary<ulong, PresentedEntity> objects = new Dictionary<ulong, PresentedEntity>();
        // Roots back to their entity, so a removed view can find the child views hanging below it.
        private readonly Dictionary<Transform, ulong> entityOfRoot = new Dictionary<Transform, ulong>();
        // Parent entity id -> child views created before that parent became a view. A set: a view whose parent never
        // becomes a view (a world root without a pose) asks again on every change, and a list made that a scan of all
        // its siblings (1,000 turning elements: 5 ms per frame; 5,000: 105 ms).
        private readonly Dictionary<ulong, HashSet<ulong>> waiting = new Dictionary<ulong, HashSet<ulong>>();
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
            public Transform Transform;
            // True when this class did not write the pose (or the colour) it last saw, so Unity may show something else:
            // the next time the loop may write it, it does, whatever changed.
            public bool PoseStale = true, ColorStale = true;
            // Rotation as Unity reports it for the quaternion last applied; the acknowledgement uses it instead of reading back.
            public Vector3 AppliedEuler;
            public ulong Parent;
            public FlecsSession.View Last;
            public bool HasLast;
            public Renderer Renderer;
            public TextMesh Text;
            public int Kind = int.MinValue;
            public byte[] LabelBytes = Array.Empty<byte>();
            public int LabelLength = -1;
        }

        private static readonly ProfilerMarker TickMarker = new ProfilerMarker("FlecsWorld.Tick");
        private static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("FlecsWorld.Apply");
        private static readonly ProfilerMarker AcknowledgeMarker = new ProfilerMarker("FlecsWorld.Acknowledge");

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
            using (TickMarker.Auto())
                Session.Tick(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            SyncViews();
        }
        public void SyncViews()
        {
            if (Session == null || Session.IsDisposed)
                return;
            // The spans point at native memory valid until the next ViewChanges call.
            Session.ViewChanges(out var changed, out var gone);
            using (ApplyMarker.Auto())
                Apply(changed, gone);
            using (AcknowledgeMarker.Auto())
                Acknowledge(changed);
        }
        private void Apply(ReadOnlySpan<FlecsSession.View> changed, ReadOnlySpan<ulong> gone)
        {
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
                created.Transform = created.Root.transform;
                created.Transform.SetParent(transform, false);
                objects.Add(entity, created);
                entityOfRoot.Add(created.Transform, entity);
                if (waiting.TryGetValue(entity, out var children))
                {
                    waiting.Remove(entity);
                    foreach (var childId in children)
                        if (objects.TryGetValue(childId, out var child) && child.Parent == entity && !detached.Contains(childId) && child.Transform.parent != created.Transform)
                        {
                            child.Transform.SetParent(created.Transform, false);
                            child.PoseStale = true;
                        }
                }
            }
            for (int i = 0; i < changed.Length; i++)
            {
                ref readonly var v = ref changed[i];
                var view = objects[v.Entity];
                // Compare with the previous view before it is replaced: only what differs is written to Unity.
                bool isNew = !view.HasLast;
                bool poseChanged = isNew || PoseDiffers(view.Last, v);
                bool scaleChanged = isNew || view.Last.Sx != v.Sx || view.Last.Sy != v.Sy || view.Last.Sz != v.Sz;
                bool colorChanged = isNew || view.Last.R != v.R || view.Last.G != v.G || view.Last.B != v.B || view.Last.A != v.A;
                view.Parent = v.Parent;
                view.Last = v;
                view.HasLast = true;
                bool isDetached = detached.Contains(v.Entity);
                bool poseHeld = (v.Flags & FlecsSession.ViewPoseOwned) != 0, appearanceHeld = (v.Flags & FlecsSession.ViewAppearanceOwned) != 0;
                if (poseHeld)
                    poseOwned.Add(v.Entity);
                else
                    poseOwned.Remove(v.Entity);
                if (appearanceHeld)
                    appearanceOwned.Add(v.Entity);
                else
                    appearanceOwned.Remove(v.Entity);
                Transform parent = transform;
                if (v.Parent != 0 && !isDetached)
                {
                    if (objects.TryGetValue(v.Parent, out var p))
                        parent = p.Transform;
                    else
                        WaitFor(v.Parent, v.Entity);
                }
                if (view.Transform.parent != parent)
                {
                    view.Transform.SetParent(parent, false);
                    view.PoseStale = true;
                }
                // Written only when the pose changed or this loop did not write it last time (held, detached, re-parented).
                // So a transform that some code moved without owning the pose is no longer snapped back by an unrelated
                // change of the same view, a colour change for example.
                if (!poseHeld && !isDetached)
                {
                    if (poseChanged || view.PoseStale)
                        ApplyPose(view, v);
                }
                else
                    view.PoseStale = true;
                if (scaleChanged)
                    view.Transform.localScale = new Vector3(v.Sx, v.Sy, v.Sz);
                bool rendererReplaced = false;
                if (view.Kind != v.Primitive)
                {
                    rendererReplaced = true;
                    if (view.Mesh)
                    {
                        // Play mode destroys it only at the end of the frame: until then a lookup of "Visual" must not find it.
                        view.Mesh.name = "Visual (replaced)";
                        VisualGeneration++;
                    }
                    DestroyOwnedObject(view.Mesh);
                    view.Mesh = null;
                    view.Renderer = null;
                    view.Kind = v.Primitive;
                    if (v.Primitive >= 0 && v.Primitive <= 5)
                    {
                        view.Mesh = GameObject.CreatePrimitive(PrimitiveKinds[v.Primitive]);
                        view.Mesh.name = "Visual";
                        view.Mesh.transform.SetParent(view.Transform, false);
                        // This hello world is presentation only: no hidden second physics model.
                        DestroyOwnedObject(view.Mesh.GetComponent<Collider>());
                        view.Renderer = view.Mesh.GetComponent<Renderer>();
                        EnsureMaterial();
                        view.Renderer.sharedMaterial = material;
                    }
                }
                var color = new Color(v.R, v.G, v.B, v.A);
                if (!appearanceHeld)
                {
                    if (colorChanged || rendererReplaced || view.ColorStale)
                        ApplyColor(view, v);
                }
                else
                    view.ColorStale = true;
                if (v.HasLabel != 0)
                {
                    if (!view.Text)
                    {
                        var text = new GameObject("Label");
                        text.transform.SetParent(view.Transform, false);
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
        }
        private void Acknowledge(ReadOnlySpan<FlecsSession.View> changed)
        {
            // Presented observes what Unity shows: changed views plus pose-owned ones, whose owners move them every frame.
            acknowledged.Clear();
            int total = changed.Length + poseOwned.Count;
            if (total == 0)
                return;
            if (acknowledgements.Length < total)
                acknowledgements = new FlecsSession.View[Math.Max(total, acknowledgements.Length * 2)];
            int n = 0;
            for (int i = 0; i < changed.Length; i++)
            {
                acknowledged.Add(changed[i].Entity);
                acknowledgements[n++] = Acknowledgement(objects[changed[i].Entity], changed[i]);
            }
            foreach (var id in poseOwned)
                if (objects.TryGetValue(id, out var owned) && owned.HasLast && acknowledged.Add(id))
                    acknowledgements[n++] = Observe(owned, owned.Last.Entity, owned.Last.Parent);
            if (n > 0)
                Session.WritePresented(acknowledgements, n, ++appliedFrame);
        }
        private void ApplyPose(PresentedEntity view, in FlecsSession.View v)
        {
            var rotation = (v.Flags & FlecsSession.ViewOriented) != 0
                ? new Quaternion(v.Qx, v.Qy, v.Qz, v.Qw) : Quaternion.Euler(v.Rx, v.Ry, v.Rz);
            view.Transform.SetLocalPositionAndRotation(new Vector3(v.Px, v.Py, v.Pz), rotation);
            view.AppliedEuler = rotation.eulerAngles;
            view.PoseStale = false;
        }
        private static bool PoseDiffers(in FlecsSession.View a, in FlecsSession.View b) =>
            a.Px != b.Px || a.Py != b.Py || a.Pz != b.Pz || a.Rx != b.Rx || a.Ry != b.Ry || a.Rz != b.Rz
            || a.Qx != b.Qx || a.Qy != b.Qy || a.Qz != b.Qz || a.Qw != b.Qw
            || ((a.Flags ^ b.Flags) & FlecsSession.ViewOriented) != 0;
        private void ApplyColor(PresentedEntity view, in FlecsSession.View v)
        {
            if (!view.Renderer)
                return;
            var color = new Color(v.R, v.G, v.B, v.A);
            properties.Clear();
            properties.SetColor(ColorId, color);
            properties.SetColor(BaseColorId, color);
            view.Renderer.SetPropertyBlock(properties);
            view.ColorStale = false;
        }
        // What Unity shows, built from the values just applied when this loop wrote (or kept) both the pose and the colour;
        // a view held by an owner, detached or waiting for its parent is read back instead.
        private FlecsSession.View Acknowledgement(PresentedEntity view, in FlecsSession.View v)
        {
            bool parentResolved = v.Parent == 0 || objects.ContainsKey(v.Parent);
            if (view.PoseStale || (view.Renderer && view.ColorStale) || !parentResolved || detached.Contains(v.Entity))
                return Observe(view, v.Entity, v.Parent);
            Color color = Color.white;
            if (view.Renderer)
                color = new Color(v.R, v.G, v.B, v.A);
            else if (view.Text)
                color = view.Text.color;
            return new FlecsSession.View
            {
                Entity = v.Entity,
                Parent = v.Parent,
                Px = v.Px,
                Py = v.Py,
                Pz = v.Pz,
                Rx = view.AppliedEuler.x,
                Ry = view.AppliedEuler.y,
                Rz = view.AppliedEuler.z,
                Sx = v.Sx,
                Sy = v.Sy,
                Sz = v.Sz,
                R = color.r,
                G = color.g,
                B = color.b,
                A = color.a,
                Primitive = view.Renderer ? view.Kind : -1,
                HasLabel = view.Text ? 1 : 0
            };
        }
        private FlecsSession.View Observe(PresentedEntity view, ulong entity, ulong parent)
        {
            var t = view.Transform;
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
            if (!waiting.TryGetValue(parent, out var set))
                waiting.Add(parent, set = new HashSet<ulong>());
            set.Add(child);
        }
        private void RemoveView(ulong id)
        {
            if (!objects.TryGetValue(id, out var view))
                return;
            var root = view.Transform;
            // Surviving child views move to the world root; Unity would destroy them with this parent.
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (!entityOfRoot.TryGetValue(child, out ulong childId))
                    continue;
                child.SetParent(transform, false);
                if (objects.TryGetValue(childId, out var orphan))
                    orphan.PoseStale = true;
                WaitFor(id, childId);
            }
            entityOfRoot.Remove(root);
            if (view.Parent != 0 && waiting.TryGetValue(view.Parent, out var list))
            {
                list.Remove(id);
                if (list.Count == 0)
                    waiting.Remove(view.Parent);
            }
            poseOwned.Remove(id);
            appearanceOwned.Remove(id);
            detached.Remove(id);
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
            poseOwned.Clear();
            appearanceOwned.Clear();
            detached.Clear();
            if (Session != null)
            {
                Session.Dispose();
                Session = null;
            }
            // Destroy only top-level owned views; child destruction follows hierarchy.
            foreach (var view in objects.Values)
                if (view.Root && view.Transform.parent == transform)
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
