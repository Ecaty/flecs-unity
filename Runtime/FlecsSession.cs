using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ecaty.Flecs
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
        }

        [DllImport("flecs_unity")] private static extern IntPtr fu_entity_path(IntPtr ctx,ulong id);
        private const string Library = "flecs_unity";
        private static readonly HashSet<FlecsSession> Sessions = new HashSet<FlecsSession>();
        private IntPtr context;
        private readonly Dictionary<string, ulong> typeIds = new Dictionary<string, ulong>();
        private readonly List<string> managedPaths = new List<string>();
        public IReadOnlyList<string> ManagedPaths => managedPaths;
        public int SourceRevision { get; private set; }
        private View[] buffer = new View[32];
        public IntPtr NativeContext { get { Check(); return context; } }
        public IntPtr NativeWorld { get { Check(); return fu_world(context); } }
        public static string Version => Utf8(fu_version());
        public bool IsDisposed => context == IntPtr.Zero;

        public FlecsSession(ushort restPort = 27750)
        {
            if (fu_abi_version() != 2 || fu_view_size() != Marshal.SizeOf<View>())
                throw new InvalidOperationException("Native/managed ABI mismatch. Rebuild matching sources.");
            context = fu_create(restPort);
            if (context == IntPtr.Zero) throw new InvalidOperationException(Utf8(fu_last_error(IntPtr.Zero)));
            Sessions.Add(this);
        }

        public void Tick(float seconds)
        {
            Check();
            if (fu_tick(context, seconds) != 0) throw new InvalidOperationException("Flecs progress stopped or invalid timestep.");
        }
        public void RunScript(string script)
        {
            Check();
            if (fu_run_script(context, script) != 0) throw new InvalidOperationException(Utf8(fu_last_error(context)));
        }
        public void SetManagedScript(string path, string script)
        {
            Check();
            if (fu_set_managed_script(context, path, script) != 0)
                throw new InvalidOperationException(Utf8(fu_last_error(context)));
            typeIds.Clear(); SourceRevision++;
            if (!managedPaths.Contains(path)) managedPaths.Add(path);
        }
        public string ManagedScriptSource(string path) { Check(); return Take(fu_managed_script_source(context, path), false); }
        public string Query(string expression) { Check(); return Take(fu_query_json(context, expression), true); }
        public string EntityJson(string path) { Check(); return Take(fu_entity_json(context, path), true); }
        public string EntityName(ulong id) { Check(); return Take(fu_entity_name(context, id), false) ?? "Entity"; }
        public string EntityPath(ulong id) { Check(); return Take(fu_entity_path(context,id),false); }
        public string Label(ulong id) { Check(); return Take(fu_label(context, id), false) ?? ""; }
        public int ReadViews(out View[] views)
        {
            Check();
            int count = fu_read_views(context, buffer, buffer.Length);
            if (count < 0) throw new InvalidOperationException("Could not read Flecs presentation query.");
            if (count > buffer.Length)
            {
                buffer = new View[Math.Max(count, buffer.Length * 2)];
                count = fu_read_views(context, buffer, buffer.Length);
            }
            views = buffer;
            return count;
        }
        public void WritePresented(View[] views, int count, int frame)
        {
            Check();
            if (views == null || count < 0 || count > views.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (fu_write_presented(context, views, count, frame) != 0) throw new InvalidOperationException("Presentation acknowledgement failed.");
        }
        public void DefineMetric(string path, string component, string member, bool counter = false)
        { Check(); if (fu_define_metric(context, path, component, member, counter ? 1 : 0) == 0) throw new InvalidOperationException("Cannot define metric: " + path); }
        public void DefineAlert(string path, string query, string message)
        { Check(); if (fu_define_alert(context, path, query, message) == 0) throw new InvalidOperationException("Cannot define alert: " + path); }
        public void Document(string path, string brief)
        { Check(); if (fu_document(context, path, brief) != 0) throw new InvalidOperationException("Unknown documented entity: " + path); }
        public string ComponentJson(ulong entity, string type) { Check(); return Take(fu_component_json(context, entity, type), false); }
        public unsafe int ReadQuery<T>(string expression, string variable, ref ulong[] entities, ref ulong[] targets, ref T[] values) where T : unmanaged
        {
            Check();
            int Read(ulong[] e, ulong[] t, T[] v) {
                fixed (T* data = v) return fu_read_query_data(context, expression, variable, e, t, data, sizeof(T), v.Length);
            }
            int count = Read(entities, targets, values);
            if (count < 0) throw new InvalidOperationException("Query data layout/variable mismatch: " + expression);
            if (count > values.Length) {
                entities = new ulong[count * 2]; targets = new ulong[count * 2]; values = new T[count * 2];
                count = Read(entities, targets, values);
            }
            return count;
        }
        public ulong Lookup(string path) { Check(); return fu_lookup(context, path); }
        public int DeleteEntity(string path) { Check(); typeIds.Clear(); SourceRevision++; managedPaths.Remove(path); return fu_delete_entity(context, path); }
        public int Pointer(ulong entity, float x, float y, uint buttons, uint modifiers = 0)
        { Check(); return fu_pointer(context, entity, x, y, buttons, modifiers); }
        public int Keyboard(string key, bool down, bool repeat = false, uint modifiers = 0)
        { Check(); return fu_keyboard(context, key, down ? 1 : 0, repeat ? 1 : 0, modifiers); }
        public string Shell(string command) { Check(); return Take(fu_shell_json(context, command), true); }
        public string QueryChanges(string expression) { Check(); return Take(fu_query_changes(context, expression), false); }
        public void SetComponentJson(string entity, string type, string json)
        { Check(); if (fu_set_component_json(context, entity, type, json) != 0) throw new InvalidOperationException("Cannot set component: " + entity + ":" + type); }
        public string BridgeDiagnostics() { Check(); return Take(fu_bridge_diagnostics(context), true); }
        public int CaptureScript(string path)
        { Check(); int rc = fu_script_capture(context, path); if (rc < 0) throw new InvalidOperationException("Cannot capture this managed source."); return rc; }
        public void UndoScript(string path)
        { Check(); if (fu_script_undo(context, path) != 0) throw new InvalidOperationException("No valid source undo available."); typeIds.Clear(); SourceRevision++; }
        public void EditScript(string source, string entity, string type, string expression)
        { Check(); if (fu_script_edit(context, source, entity, type, expression) != 0) throw new InvalidOperationException(Utf8(fu_last_error(context))); typeIds.Clear(); SourceRevision++; }
        private unsafe ulong DataType<T>(string path) where T : unmanaged
        {
            Check();
            if (!typeIds.TryGetValue(path, out ulong type)) { type = Lookup(path); if (type != 0) typeIds[path] = type; }
            if (type == 0 || fu_component_size(context, type) != sizeof(T))
                throw new InvalidOperationException("Runtime component layout mismatch or non-POD type: " + path);
            return type;
        }
        public unsafe bool TryRead<T>(ulong entity, string type, out T value) where T : unmanaged
        {
            T local = default;
            int rc = fu_read_component(context, entity, DataType<T>(type), &local, sizeof(T));
            if (rc < 0) throw new InvalidOperationException("Invalid component read: " + type);
            value = local; return rc == 1;
        }
        public unsafe void WriteBatch<T>(string type, ulong[] entities, T[] values, int count) where T : unmanaged
        {
            if (entities == null || values == null || count < 0 || count > entities.Length || count > values.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;
            ulong id = DataType<T>(type);
            fixed (T* data = values)
                if (fu_write_components(context, id, entities, data, sizeof(T), count) != 0)
                    throw new InvalidOperationException("Invalid component batch: " + type);
        }
        public void Dispose()
        {
            if (context == IntPtr.Zero) return;
            var old = context;
            context = IntPtr.Zero;
            Sessions.Remove(this);
            fu_destroy(old);
        }
        public static void DisposeAll()
        {
            foreach (var session in new List<FlecsSession>(Sessions)) session.Dispose();
        }
        private void Check()
        {
            if (context == IntPtr.Zero) throw new ObjectDisposedException(nameof(FlecsSession));
        }
        private string Take(IntPtr text, bool required)
        {
            if (text == IntPtr.Zero)
            {
                if (required) throw new InvalidOperationException(Utf8(fu_last_error(context)));
                return null;
            }
            try { return Utf8(text); }
            finally { fu_free(text); }
        }
        private static string Utf8(IntPtr value)
        {
            if (value == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(value, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(value, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_define_metric(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string component, [MarshalAs(UnmanagedType.LPUTF8Str)] string member, int counter);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_define_alert(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_document(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string brief);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_component_json(IntPtr ctx, ulong entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_read_query_data(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression, [MarshalAs(UnmanagedType.LPUTF8Str)] string variable, [Out] ulong[] entities, [Out] ulong[] targets, void* values, int size, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_changes(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_set_component_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern ulong fu_lookup(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_component_size(IntPtr ctx, ulong type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_read_component(IntPtr ctx, ulong entity, ulong type, void* value, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int fu_write_components(IntPtr ctx, ulong type, [In] ulong[] entities, void* values, int size, int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_delete_entity(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_pointer(IntPtr ctx, ulong entity, float x, float y, uint buttons, uint modifiers);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_keyboard(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int down, int repeat, uint modifiers);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_shell_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_bridge_diagnostics(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_capture(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_undo(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_script_edit(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string entity, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_abi_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_view_size();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_create(ushort port);
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
    }
}
