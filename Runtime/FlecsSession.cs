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

        private const string Library = "flecs_unity";
        private static readonly HashSet<FlecsSession> Sessions = new HashSet<FlecsSession>();
        private IntPtr context;
        private View[] buffer = new View[32];
        public IntPtr NativeWorld { get { Check(); return fu_world(context); } }
        public static string Version => Utf8(fu_version());
        public bool IsDisposed => context == IntPtr.Zero;

        public FlecsSession(ushort restPort = 27750)
        {
            if (fu_abi_version() != 1 || fu_view_size() != Marshal.SizeOf<View>())
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
        }
        public string Query(string expression) { Check(); return Take(fu_query_json(context, expression), true); }
        public string EntityJson(string path) { Check(); return Take(fu_entity_json(context, path), true); }
        public string EntityName(ulong id) { Check(); return Take(fu_entity_name(context, id), false) ?? "Entity"; }
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

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_abi_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_view_size();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_create(ushort port);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void fu_destroy(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_tick(IntPtr ctx, float dt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_run_script(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_set_managed_script(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_last_error(IntPtr ctx);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fu_read_views(IntPtr ctx, [Out] View[] views, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_entity_name(IntPtr ctx, ulong entity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_label(IntPtr ctx, ulong entity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_query_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string expression);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_entity_json(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void fu_free(IntPtr memory);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr fu_world(IntPtr ctx);
    }
}
