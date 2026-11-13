using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Runtime.Flecs
{
    /// <summary>Owns one native Flecs world. Use only on Unity's main thread.</summary>
    public sealed class FlecsSession : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct View
        {
            public ulong Entity, Parent;
            public float Px, Py, Pz, Rx, Ry, Rz, Sx, Sy, Sz, R, G, B, A;
            public int Primitive, HasLabel;
            public float Qx, Qy, Qz, Qw;
            public uint Flags;
        }
        /// <summary>Set in <see cref="View.Flags"/> when Qx..Qw is the rotation and Rx..Rz must be ignored.</summary>
        public const uint ViewOriented = 1;
        /// <summary>Set in <see cref="View.Flags"/> while a (TransformOwner, owner) pair is on the view.</summary>
        public const uint ViewPoseOwned = 2;
        /// <summary>Set in <see cref="View.Flags"/> while an (AppearanceOwner, owner) pair is on the view.</summary>
        public const uint ViewAppearanceOwned = 4;

        /// <summary>A fact Unity reports to the world; <see cref="Kind"/> is the event entity's id.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Event
        {
            public ulong Kind, Entity, Other;
            public float X, Y, Z, Value;
        }

        public sealed class TypeField
        {
            public string Name;
            public int Offset, Size;
            public string Kind;
        }
        /// <summary>Native layout of a reflected type, nested structs flattened to leaf fields.</summary>
        public sealed class TypeLayout
        {
            public int Size, Alignment;
            public TypeField[] Fields;
        }
        [Serializable] private sealed class LayoutJson
        {
            public int size, alignment;
            public FieldJson[] fields;
        }
        [Serializable] private sealed class FieldJson
        {
            public string name, kind;
            public int offset, size;
        }
        private struct Leaf
        {
            public string Name;
            public int Offset, Size;
            public bool Float;
        }

        [DllImport("flecs_unity")] private static extern IntPtr fu_entity_path(IntPtr ctx, ulong id);
        private const string Library = "flecs_unity";
        private static readonly HashSet<FlecsSession> Sessions = new HashSet<FlecsSession>();
        private IntPtr context;
        private readonly Dictionary<string, ulong> typeIds = new Dictionary<string, ulong>();
        private readonly Dictionary<string, int> queryHandles = new Dictionary<string, int>();
        public const int AbiVersion = 5;
        private readonly List<string> managedPaths = new List<string>();
        public IReadOnlyList<string> ManagedPaths => managedPaths;
        public int SourceRevision
        {
            get; private set;
        }
        // Sources and worlds add and remove entities and kinds: what was cached by name or id is looked up again.
        private void SourcesChanged()
        {
            typeIds.Clear();
            verified.Clear();
            SourceRevision++;
        }
        private View[] buffer = new View[32];
        private byte[] layoutBuffer = new byte[1024];
        // (native type id, managed type) pairs whose layout already matched; cleared when sources change.
        private readonly HashSet<(ulong, Type)> verified = new HashSet<(ulong, Type)>();
        public IntPtr NativeContext
        {
            get
            {
                Check();
                return context;
            }
        }
        public IntPtr NativeWorld
        {
            get
            {
                Check();
                return fu_world(context);
            }
        }
        public static string Version => Utf8(fu_version());
        public bool IsDisposed => context == IntPtr.Zero;

        /// <param name="restPort">Local REST/Explorer port; 0 disables it. A busy port does not fail creation (see RestStatus).</param>
        /// <param name="restReadOnly">REST refuses requests that modify the world.</param>
        public FlecsSession(ushort restPort = 27750, bool restReadOnly = false)
        {
            if (fu_abi_version() != AbiVersion || fu_view_size() != Marshal.SizeOf<View>())
                throw new InvalidOperationException("Native/managed ABI mismatch. Rebuild matching sources.");
            context = fu_create_ex(restPort, restReadOnly ? 1u : 0u);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException(Utf8(fu_last_error(IntPtr.Zero)));
            Sessions.Add(this);
            RestStatus = fu_rest_status(context);
            if (RestStatus < 0)
                RestError = Utf8(fu_last_error(context));
        }
        /// <summary>1 REST running, 0 not requested, -1 could not start (RestError explains).</summary>
        public int RestStatus
        {
            get;
        }
        public string RestError
        {
            get;
        }

        public void Tick(float seconds)
        {
            Check();
            if (fu_tick(context, seconds) != 0)
                throw new InvalidOperationException("Flecs progress stopped or invalid timestep.");
        }
        public void RunScript(string script)
        {
            Check();
            if (fu_run_script(context, script) != 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
        }
        public void SetManagedScript(string path, string script)
        {
            Check();
            if (fu_set_managed_script(context, path, script) != 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            typeIds.Clear();
            verified.Clear();
            SourceRevision++;
            if (!managedPaths.Contains(path))
                managedPaths.Add(path);
        }
        /// <summary>Applies a named source text in place under <paramref name="scope"/>. A refused text changes nothing and returns false with the reason.</summary>
        /// <param name="removed">True when something the previous text defined is gone.</param>
        /// <param name="line">1-based line of the error in the text, 0 when unknown.</param>
        public bool TryApplySource(string name, string scope, string code, out bool removed, out string error, out int line, out int column)
        {
            Check();
            SourcesChanged();
            int rc = fu_source_apply(context, name, scope, code);
            removed = rc == 1;
            error = null;
            line = column = 0;
            if (rc >= 0)
                return true;
            error = Utf8(fu_last_error(context));
            if (fu_error_position(context, out int l, out int c) == 1)
            {
                line = l;
                column = c;
            }
            return false;
        }
        /// <summary>Applies a named source text in place and returns whether something was removed. Throws with the reason when the text is refused.</summary>
        public bool ApplySource(string name, string scope, string code)
        {
            if (TryApplySource(name, scope, code, out bool removed, out string error, out int line, out _))
                return removed;
            throw new InvalidOperationException(name + (line > 0 ? " line " + line : "") + ": " + error);
        }
        /// <summary>Applies the text the source had before its last change. False with the reason when there is none or it is refused.</summary>
        public bool TryUndoSource(string name, out string error)
        {
            Check();
            SourcesChanged();
            error = null;
            if (fu_source_undo(context, name) >= 0)
                return true;
            error = Utf8(fu_last_error(context));
            return false;
        }
        /// <summary>Deletes a source and what it defines. False with the reason when an instance still uses one of its kinds.</summary>
        public bool TryRemoveSource(string name, out string error)
        {
            Check();
            SourcesChanged();
            error = null;
            if (fu_source_remove(context, name) >= 0)
                return true;
            error = Utf8(fu_last_error(context));
            return false;
        }
        /// <summary>Current text of a source, or null when there is no such source.</summary>
        public unsafe string SourceText(string name)
        {
            Check();
            int length;
            fixed (byte* data = layoutBuffer)
                length = fu_source_text_into(context, name, data, layoutBuffer.Length);
            if (length > layoutBuffer.Length)
            {
                layoutBuffer = new byte[Math.Max(length, layoutBuffer.Length * 2)];
                fixed (byte* data = layoutBuffer)
                    length = fu_source_text_into(context, name, data, layoutBuffer.Length);
            }
            return length < 0 ? null : Encoding.UTF8.GetString(layoutBuffer, 0, length);
        }
        /// <summary>Creates an instance of a kind under <paramref name="parent"/> (0 for the root); its parts follow the kind's parts. <paramref name="name"/> may be null.</summary>
        public ulong Spawn(ulong kind, ulong parent, string name)
        {
            Check();
            ulong entity = fu_spawn(context, kind, parent, name);
            if (entity == 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            return entity;
        }
        /// <summary>Attaches the parts of an instance made another way to its kinds. Returns the number of kinds it has.</summary>
        public int Attach(ulong entity)
        {
            Check();
            return fu_attach(context, entity);
        }
        /// <summary>Creates the world <c>worlds.&lt;id&gt;</c> (letters, digits and '_') and returns its entity. Returns the existing one when it exists. Throws with the reason on failure.</summary>
        public ulong CreateWorld(string id)
        {
            Check();
            ulong world = fu_world_create(context, id);
            if (world == 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            return world;
        }
        /// <summary>Deletes a world with its content and its sources. False when the entity is not a world.</summary>
        public bool DeleteWorld(ulong world)
        {
            Check();
            SourcesChanged();
            return fu_world_delete(context, world) == 1;
        }
        /// <summary>Enters a world; 0 leaves the current one. Throws when the entity is not a world.</summary>
        public void EnterWorld(ulong world)
        {
            Check();
            if (fu_world_enter(context, world) < 0)
                throw new InvalidOperationException("Entity " + world + " is not a world.");
        }
        /// <summary>The world that is entered, or 0.</summary>
        public ulong EnteredWorld
        {
            get
            {
                Check();
                return fu_world_entered(context);
            }
        }
        /// <summary>Like <see cref="QueryOnce"/>, but returns only entities inside the world. Throws with the reason when the expression is invalid.</summary>
        public string QueryWorld(ulong world, string expression, int limit = 0)
        {
            Check();
            return Take(fu_world_query_json(context, world, expression, limit), true);
        }
        /// <summary>Evaluates a script under a world, so the entities it creates are created inside it. False with the reason and the 1-based position of the error (0 when unknown).</summary>
        public bool TryRunWorldScript(ulong world, string code, out string error, out int line, out int column)
        {
            Check();
            SourcesChanged();
            error = null;
            line = column = 0;
            if (fu_world_run_script(context, world, code) == 0)
                return true;
            error = Utf8(fu_last_error(context));
            if (fu_error_position(context, out int l, out int c) == 1)
            {
                line = l;
                column = c;
            }
            return false;
        }
        /// <summary>Evaluates a script under a world. Throws with the reason when it fails.</summary>
        public void RunWorldScript(ulong world, string code)
        {
            if (!TryRunWorldScript(world, code, out string error, out int line, out _))
                throw new InvalidOperationException((line > 0 ? "line " + line + ": " : "") + error);
        }
        /// <summary>The world as a JSON document: the texts of its sources and what each entity itself stores. Throws when the entity is not a world.</summary>
        public string SaveWorld(ulong world)
        {
            Check();
            return Take(fu_world_save_json(context, world), true);
        }
        /// <summary>Loads a document as <c>worlds.&lt;id&gt;</c>, replacing the world with that id only when all of it is accepted. Entities get new ids. False with the reason when refused.</summary>
        public bool TryLoadWorld(string id, string document, out ulong world, out string error)
        {
            Check();
            SourcesChanged();
            error = null;
            world = fu_world_load_json(context, id, document);
            if (world != 0)
                return true;
            error = Utf8(fu_last_error(context));
            return false;
        }
        /// <summary>Keeps a document of the world in memory for <see cref="TryRewindWorld"/>. Throws when the entity is not a world.</summary>
        public void SnapshotWorld(ulong world)
        {
            Check();
            if (fu_world_snapshot(context, world) < 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
        }
        /// <summary>Loads the snapshot of a world again. <paramref name="restored"/> is the new world entity. False with the reason when there is no snapshot.</summary>
        public bool TryRewindWorld(ulong world, out ulong restored, out string error)
        {
            Check();
            SourcesChanged();
            error = null;
            restored = fu_world_rewind(context, world);
            if (restored != 0)
                return true;
            error = Utf8(fu_last_error(context));
            return false;
        }
        public string ManagedScriptSource(string path)
        {
            Check();
            return Take(fu_managed_script_source(context, path), false);
        }
        public string Query(string expression)
        {
            Check();
            return Take(fu_query_json(context, expression), true);
        }
        /// <summary>
        /// One-off query for tools and the AI: evaluated without a cache and without taking one of the query slots
        /// that features read every frame. A positive <paramref name="limit"/> caps the number of rows.
        /// </summary>
        public string QueryOnce(string expression, int limit = 0)
        {
            Check();
            return Take(fu_query_once_json(context, expression, limit), true);
        }
        public string EntityJson(string path)
        {
            Check();
            return Take(fu_entity_json(context, path), true);
        }
        public string EntityName(ulong id)
        {
            Check();
            return Take(fu_entity_name(context, id), false) ?? "Entity";
        }
        public string EntityPath(ulong id)
        {
            Check();
            return Take(fu_entity_path(context, id), false);
        }
        public string Label(ulong id)
        {
            Check();
            return Take(fu_label(context, id), false) ?? "";
        }
        /// <summary>Copies the label's UTF-8 bytes into <paramref name="buffer"/> (grown as needed) without allocating a string.</summary>
        /// <returns>Byte length, or -1 when the entity has no label.</returns>
        public unsafe int LabelBytes(ulong id, ref byte[] buffer)
        {
            Check();
            int length;
            fixed (byte* data = buffer)
                length = fu_label_into(context, id, data, buffer.Length);
            if (length > buffer.Length)
            {
                buffer = new byte[Math.Max(length, buffer.Length * 2)];
                fixed (byte* data = buffer)
                    length = fu_label_into(context, id, data, buffer.Length);
            }
            return length;
        }
        /// <summary>Copies one component as JSON (UTF-8) into <paramref name="buffer"/> (grown as needed) without allocating.</summary>
        /// <returns>Byte length, or -1 when the entity does not have the component.</returns>
        public unsafe int ComponentJsonBytes(ulong entity, ulong type, ref byte[] buffer)
        {
            Check();
            int length;
            fixed (byte* data = buffer)
                length = fu_component_json_into(context, entity, type, data, buffer.Length);
            if (length > buffer.Length)
            {
                buffer = new byte[Math.Max(length, buffer.Length * 2)];
                fixed (byte* data = buffer)
                    length = fu_component_json_into(context, entity, type, data, buffer.Length);
            }
            return length;
        }
        /// <summary>Entity id of a named type, cached until sources change. 0 when unknown.</summary>
        public ulong TypeId(string path)
        {
            Check();
            if (!typeIds.TryGetValue(path, out ulong type))
            {
                type = Lookup(path);
                if (type != 0)
                    typeIds[path] = type;
            }
            return type;
        }
        public int ReadViews(out View[] views)
        {
            Check();
            int count = fu_read_views(context, buffer, buffer.Length);
            if (count < 0)
                throw new InvalidOperationException("Could not read Flecs presentation query.");
            if (count > buffer.Length)
            {
                buffer = new View[Math.Max(count, buffer.Length * 2)];
                count = fu_read_views(context, buffer, buffer.Length);
            }
            views = buffer;
            return count;
        }
        /// <summary>
        /// Views changed since the previous call and entities that stopped being views (first call: every view).
        /// Both spans point at native memory that stays valid only until the next call for this session.
        /// </summary>
        public unsafe void ViewChanges(out ReadOnlySpan<View> changed, out ReadOnlySpan<ulong> removed)
        {
            Check();
            IntPtr views = fu_view_changes(context, out int count, out IntPtr gone, out int goneCount);
            if (views == IntPtr.Zero)
                throw new InvalidOperationException("Could not read Flecs view changes: " + Utf8(fu_last_error(context)));
            changed = new ReadOnlySpan<View>((void*)views, count);
            removed = new ReadOnlySpan<ulong>((void*)gone, goneCount);
        }
        /// <summary>Delivers facts to the world as Flecs events. Returns how many were delivered (dead entities are skipped).</summary>
        public unsafe int Emit(Event[] events, int count)
        {
            Check();
            if (events == null || count < 0 || count > events.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            int rc = fu_emit_events(context, events, count);
            if (rc < 0)
                throw new InvalidOperationException("Could not emit events: " + Utf8(fu_last_error(context)));
            return rc;
        }
        /// <summary>Native layout of a reflected type, or null when the type is unknown.</summary>
        public TypeLayout Layout(string typePath)
        {
            Check();
            ulong type = TypeId(typePath);
            return type == 0 ? null : LayoutOf(type);
        }
        private unsafe TypeLayout LayoutOf(ulong type)
        {
            int length;
            fixed (byte* data = layoutBuffer)
                length = fu_type_layout_into(context, type, data, layoutBuffer.Length);
            if (length > layoutBuffer.Length)
            {
                layoutBuffer = new byte[Math.Max(length, layoutBuffer.Length * 2)];
                fixed (byte* data = layoutBuffer)
                    length = fu_type_layout_into(context, type, data, layoutBuffer.Length);
            }
            if (length < 0)
                return null;
            var json = JsonUtility.FromJson<LayoutJson>(Encoding.UTF8.GetString(layoutBuffer, 0, length));
            if (json == null)
                return null;
            var layout = new TypeLayout { Size = json.size, Alignment = json.alignment };
            int n = json.fields?.Length ?? 0;
            layout.Fields = new TypeField[n];
            for (int i = 0; i < n; i++)
            {
                var f = json.fields[i];
                layout.Fields[i] = new TypeField { Name = f.name, Offset = f.offset, Size = f.size, Kind = f.kind };
            }
            return layout;
        }
        public void WritePresented(View[] views, int count, int frame)
        {
            Check();
            if (views == null || count < 0 || count > views.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (fu_write_presented(context, views, count, frame) != 0)
                throw new InvalidOperationException("Presentation acknowledgement failed.");
        }
        public void DefineMetric(string path, string component, string member, bool counter = false)
        {
            Check();
            if (fu_define_metric(context, path, component, member, counter ? 1 : 0) == 0)
                throw new InvalidOperationException("Cannot define metric: " + path);
        }
        public void DefineAlert(string path, string query, string message)
        {
            Check();
            if (fu_define_alert(context, path, query, message) == 0)
                throw new InvalidOperationException("Cannot define alert: " + path);
        }
        public void Document(string path, string brief)
        {
            Check();
            if (fu_document(context, path, brief) != 0)
                throw new InvalidOperationException("Unknown documented entity: " + path);
        }
        public string ComponentJson(ulong entity, string type)
        {
            Check();
            return Take(fu_component_json(context, entity, type), false);
        }
        /// <summary>Stable native query handle for an expression, created once per session and kept across source reloads.</summary>
        public int QueryHandle(string expression)
        {
            Check();
            if (queryHandles.TryGetValue(expression, out int handle))
                return handle;
            handle = fu_query_create(context, expression);
            if (handle <= 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            queryHandles.Add(expression, handle);
            return handle;
        }
        /// <summary>True when data matched by the query changed since it was last read (new handle: true).</summary>
        public bool QueryChanged(int handle)
        {
            Check();
            int rc = fu_query_changed(context, handle);
            if (rc < 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            return rc == 1;
        }
        /// <summary>JSON rows of a stable query handle (also resets its change state).</summary>
        public string QueryJson(int handle)
        {
            Check();
            return Take(fu_query_json_h(context, handle), true);
        }
        public unsafe int ReadQuery<T>(string expression, string variable, ref ulong[] entities, ref ulong[] targets, ref T[] values) where T : unmanaged
        {
            Check();
            int handle = QueryHandle(expression);
            int Read(ulong[] e, ulong[] t, T[] v)
            {
                fixed (T* data = v)
                    return fu_query_read_data(context, handle, variable, e, t, data, sizeof(T), v.Length);
            }
            int count = Read(entities, targets, values);
            if (count < 0)
                throw new InvalidOperationException("Query data layout/variable mismatch: " + expression);
            if (count > values.Length)
            {
                entities = new ulong[count * 2];
                targets = new ulong[count * 2];
                values = new T[count * 2];
                count = Read(entities, targets, values);
            }
            return count;
        }
        public ulong Lookup(string path)
        {
            Check();
            return fu_lookup(context, path);
        }
        ulong transformOwner, appearanceOwner;
        /// <summary>The exclusive relationship whose target owns a view's pose.</summary>
        public ulong TransformOwner => transformOwner != 0 ? transformOwner : transformOwner = Relation("TransformOwner");
        /// <summary>The exclusive relationship whose target owns a view's appearance.</summary>
        public ulong AppearanceOwner => appearanceOwner != 0 ? appearanceOwner : appearanceOwner = Relation("AppearanceOwner");
        private ulong Relation(string name)
        {
            ulong id = Lookup(name);
            if (id == 0)
                throw new InvalidOperationException("The native library does not define " + name + "; rebuild it for this bridge version.");
            return id;
        }
        /// <summary>True when <paramref name="owner"/> holds <paramref name="relation"/> on <paramref name="entity"/> afterwards.</summary>
        public bool Claim(ulong entity, ulong relation, ulong owner)
        {
            Check();
            return fu_claim(context, entity, relation, owner) == 1;
        }
        /// <summary>True when the pair was removed; false when <paramref name="owner"/> did not hold it.</summary>
        public bool Release(ulong entity, ulong relation, ulong owner)
        {
            Check();
            return fu_release(context, entity, relation, owner) == 1;
        }
        public int DeleteEntity(string path)
        {
            Check();
            typeIds.Clear();
            verified.Clear();
            SourceRevision++;
            managedPaths.Remove(path);
            return fu_delete_entity(context, path);
        }
        public int Pointer(ulong entity, float x, float y, uint buttons, uint modifiers = 0)
        {
            Check();
            return fu_pointer(context, entity, x, y, buttons, modifiers);
        }
        public int Keyboard(string key, bool down, bool repeat = false, uint modifiers = 0)
        {
            Check();
            return fu_keyboard(context, key, down ? 1 : 0, repeat ? 1 : 0, modifiers);
        }
        public string Shell(string command)
        {
            Check();
            return Take(fu_shell_json(context, command), true);
        }
        public string QueryChanges(string expression)
        {
            Check();
            return Take(fu_query_changes(context, expression), false);
        }
        public void SetComponentJson(string entity, string type, string json)
        {
            Check();
            if (fu_set_component_json(context, entity, type, json) != 0)
                throw new InvalidOperationException("Cannot set component: " + entity + ":" + type);
        }
        public string BridgeDiagnostics()
        {
            Check();
            return Take(fu_bridge_diagnostics(context), true);
        }
        public int CaptureScript(string path)
        {
            Check();
            int rc = fu_script_capture(context, path);
            if (rc < 0)
                throw new InvalidOperationException("Cannot capture this managed source.");
            return rc;
        }
        public void UndoScript(string path)
        {
            Check();
            if (fu_script_undo(context, path) != 0)
                throw new InvalidOperationException("No valid source undo available.");
            typeIds.Clear();
            verified.Clear();
            SourceRevision++;
        }
        public void EditScript(string source, string entity, string type, string expression)
        {
            Check();
            if (fu_script_edit(context, source, entity, type, expression) != 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            typeIds.Clear();
            verified.Clear();
            SourceRevision++;
        }
        private unsafe ulong DataType<T>(string path) where T : unmanaged
        {
            ulong type = TypeId(path);
            if (type == 0 || fu_component_size(context, type) != sizeof(T))
                throw new InvalidOperationException("Runtime component layout mismatch or non-POD type: " + path);
            if (!verified.Contains((type, typeof(T))))
            {
                VerifyLayout(path, type, typeof(T));
                verified.Add((type, typeof(T)));
            }
            return type;
        }
        // Size alone cannot catch swapped or re-typed fields; compare every leaf once per (type, managed type).
        private void VerifyLayout(string path, ulong type, Type managed)
        {
            var native = LayoutOf(type);
            var leaves = new List<Leaf>();
            Flatten(managed, 0, "", leaves);
            string problem = native == null ? "native layout unavailable" : Compare(native, leaves);
            if (problem == null)
                return;
            var text = new StringBuilder("Runtime component layout mismatch (" + problem + "): " + path + " vs " + managed.FullName + "\nnative:");
            if (native != null)
                foreach (var f in native.Fields)
                    text.Append("\n  ").Append(f.Name).Append(" offset ").Append(f.Offset).Append(" size ").Append(f.Size).Append(' ').Append(f.Kind);
            text.Append("\nmanaged:");
            foreach (var l in leaves)
                text.Append("\n  ").Append(l.Name).Append(" offset ").Append(l.Offset).Append(" size ").Append(l.Size).Append(l.Float ? " float" : "");
            throw new InvalidOperationException(text.ToString());
        }
        private static void Flatten(Type type, int offset, string prefix, List<Leaf> leaves)
        {
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);
            if (type.IsPrimitive)
            {
                leaves.Add(new Leaf { Name = prefix, Offset = offset, Size = UnsafeUtility.SizeOf(type), Float = type == typeof(float) || type == typeof(double) });
                return;
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string name = field.Name;
                if (name.Length > 2 && name[0] == '<')
                    name = name.Substring(1, name.IndexOf('>') - 1);
                Flatten(field.FieldType, offset + UnsafeUtility.GetFieldOffset(field), prefix.Length == 0 ? name : prefix + "." + name, leaves);
            }
        }
        private static string Normalize(string name)
        {
            int dot = name.LastIndexOf('.');
            return (dot >= 0 ? name.Substring(dot + 1) : name).ToLowerInvariant().Replace("_", "");
        }
        private static string Compare(TypeLayout native, List<Leaf> leaves)
        {
            var fields = native.Fields;
            if (fields.Length != leaves.Count)
                return "field count " + leaves.Count + " vs native " + fields.Length;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                var l = leaves[i];
                if (f.Offset != l.Offset || f.Size != l.Size)
                    return "field " + i + " offset/size";
                bool nativeFloat = f.Kind == "f32" || f.Kind == "f64";
                if (nativeFloat != l.Float)
                    return "field " + i + " kind " + f.Kind;
                string managedName = Normalize(l.Name);
                if (managedName == Normalize(f.Name))
                    continue;
                for (int j = 0; j < fields.Length; j++)
                    if (j != i && managedName == Normalize(fields[j].Name))
                        return "field " + i + " name looks swapped with " + j;
            }
            return null;
        }
        public unsafe bool TryRead<T>(ulong entity, string type, out T value) where T : unmanaged
        {
            T local = default;
            int rc = fu_read_component(context, entity, DataType<T>(type), &local, sizeof(T));
            if (rc < 0)
                throw new InvalidOperationException("Invalid component read: " + type);
            value = local;
            return rc == 1;
        }
        public unsafe void WriteBatch<T>(string type, ulong[] entities, T[] values, int count) where T : unmanaged
        {
            if (entities == null || values == null || count < 0 || count > entities.Length || count > values.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0)
                return;
            ulong id = DataType<T>(type);
            fixed (T* data = values)
                if (fu_write_components(context, id, entities, data, sizeof(T), count) != 0)
                    throw new InvalidOperationException("Invalid component batch: " + type);
        }
        public void Dispose()
        {
            if (context == IntPtr.Zero)
                return;
            var old = context;
            context = IntPtr.Zero;
            Sessions.Remove(this);
            fu_destroy(old);
        }
        public static void DisposeAll()
        {
            foreach (var session in new List<FlecsSession>(Sessions))
                session.Dispose();
        }
        private void Check()
        {
            if (context == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(FlecsSession));
        }
        private string Take(IntPtr text, bool required)
        {
            if (text == IntPtr.Zero)
            {
                if (required)
                    throw new InvalidOperationException(Utf8(fu_last_error(context)));
                return null;
            }
            try
            {
                return Utf8(text);
            }
            finally { fu_free(text); }
        }
        private static string Utf8(IntPtr value)
        {
            if (value == IntPtr.Zero)
                return "";
            int length = 0;
            while (Marshal.ReadByte(value, length) != 0)
                length++;
            var bytes = new byte[length];
            Marshal.Copy(value, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_define_metric(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string component, [MarshalAs(UnmanagedType.LPUTF8Str)] string member, int counter);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_define_alert(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_document(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string brief);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_component_json(IntPtr ctx, ulong entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_changes(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_set_component_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_lookup(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_component_size(IntPtr ctx, ulong type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_read_component(IntPtr ctx, ulong entity, ulong type, void* value, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_write_components(IntPtr ctx, ulong type, [In] ulong[] entities, void* values, int size, int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_claim(IntPtr ctx, ulong entity, ulong relation, ulong owner);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_release(IntPtr ctx, ulong entity, ulong relation, ulong owner);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_delete_entity(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_pointer(IntPtr ctx, ulong entity, float x, float y, uint buttons, uint modifiers);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_keyboard(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int down, int repeat, uint modifiers);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_shell_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_bridge_diagnostics(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_once_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression, int limit);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_capture(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_undo(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_edit(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_abi_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_view_size();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void fu_destroy(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_tick(IntPtr ctx, float dt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_run_script(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_set_managed_script(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_managed_script_source(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_last_error(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_read_views(IntPtr ctx, [Out] View[] views, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_write_presented(IntPtr ctx, [In] View[] views, int count, int frame);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_entity_name(IntPtr ctx, ulong entity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_label(IntPtr ctx, ulong entity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_entity_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void fu_free(IntPtr memory);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_world(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_create_ex(ushort port, uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_rest_status(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_query_create(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_query_changed(IntPtr ctx, int handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_json_h(IntPtr ctx, int handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_query_read_data(IntPtr ctx, int handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string variable, [Out] ulong[] entities, [Out] ulong[] targets, void* values, int size, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_component_json_into(IntPtr ctx, ulong entity, ulong type, byte* output, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_view_changes(IntPtr ctx, out int count, out IntPtr removed, out int removedCount);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_type_layout_into(IntPtr ctx, ulong type, byte* output, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_emit_events(IntPtr ctx, [In] Event[] events, int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_label_into(IntPtr ctx, ulong entity, byte* output, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_source_apply(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string scope, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_source_undo(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_source_remove(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_source_text_into(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte* output, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_error_position(IntPtr ctx, out int line, out int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_spawn(IntPtr ctx, ulong kind, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_attach(IntPtr ctx, ulong entity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_world_create(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_world_delete(IntPtr ctx, ulong world);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_world_enter(IntPtr ctx, ulong world);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_world_entered(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_world_query_json(IntPtr ctx, ulong world, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression, int limit);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_world_run_script(IntPtr ctx, ulong world, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_world_save_json(IntPtr ctx, ulong world);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_world_load_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_world_snapshot(IntPtr ctx, ulong world);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_world_rewind(IntPtr ctx, ulong world);
    }
}
