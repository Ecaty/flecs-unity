using System;
using System.Collections.Generic;
using System.Linq;
using Ecaty.Flecs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Ecaty.Flecs.Capabilities
{
    public interface ICapabilityAdapter {
        string Component { get; }
        string Category { get; }
        int Order { get; }
        CapabilityInstance Create(CapabilityContext context);
    }
    public abstract class CapabilityInstance : IDisposable {
        protected readonly CapabilityContext Context;
        protected Transform Target => Context.Target;
        protected GameObject Host => Context.Host;
        protected CapabilityInstance(CapabilityContext context) { Context=context; }
        public abstract void Apply(JObject config);
        public virtual void Tick(float delta) {}
        public virtual void FixedTick(float delta) {}
        public virtual void LateTick(float delta) {}
        public virtual JObject CaptureState()=>null;
        public virtual void RestoreState(JObject state) {}
        public virtual void Dispose() {}
    }
    public sealed class CapabilityContext {
        public CapabilityRuntime Runtime; internal FlecsSession SessionOwner; public FlecsSession Session => SessionOwner;
        public ulong Entity, TargetEntity; public string Path; public Transform Target; public GameObject Host;
        public T Asset<T>(string key) where T:UnityEngine.Object => Runtime.Assets != null ? Runtime.Assets.Resolve<T>(key) : null;
        public Transform Resolve(ulong id) => Runtime.Resolve(id);
        public JObject Read(string type) { var value=Session.ComponentJson(Entity,type); return string.IsNullOrEmpty(value)?null:JObject.Parse(value); }
        public void Publish(string type, object value) => Session.SetComponentJson(Path,type,JsonConvert.SerializeObject(value));
        public void OwnPose() { if(!Runtime.World.AcquirePose(TargetEntity,this))throw new InvalidOperationException("Target already has a movement owner."); }
        public void ReleasePose() => Runtime.World.ReleasePose(TargetEntity,this);
    }
    // The ECS is the source of truth. These dictionaries are disposable presentation bindings only.
    [DefaultExecutionOrder(150)]
    public sealed class CapabilityRuntime : MonoBehaviour {
        public FlecsWorld World { get; private set; }
        public CapabilityAssets Assets;
        public Camera ViewCamera;
        readonly Dictionary<string,JObject> pendingRestore=new();
        public JObject CaptureState(){var result=new JObject();foreach(var p in bindings){var state=p.Value.Instance?.CaptureState();if(state!=null)result[p.Key]=state;}return result;}
        public void QueueRestore(JObject values){EnsureSession();pendingRestore.Clear();if(values!=null)foreach(var p in values.Properties())pendingRestore[p.Name]=(JObject)p.Value.DeepClone();}
        public Func<ulong,Transform> TargetResolver;
        public IReadOnlyList<ICapabilityAdapter> Adapters => adapters;
        public int BindingCount => bindings.Count;
        readonly List<ICapabilityAdapter> adapters=new();
        readonly Dictionary<string,Binding> bindings=new();
        sealed class Binding { public CapabilityContext Context; public CapabilityInstance Instance; public string Signature; public int Generation; public int Phase; }
        FlecsSession session;
        public void Initialize(FlecsWorld world) { World=world; EnsureSession(); }
        void EnsureSession() {
            if(!World||World.Session==null||World.Session.IsDisposed)return;
            if(session==World.Session)return;
            Clear();session=World.Session;
            if(session.ManagedScriptSource("CapabilitySchema")==null)session.SetManagedScript("CapabilitySchema",Resources.Load<TextAsset>("CapabilitySchema").text);
            if(session.ManagedScriptSource("CapabilityPostProcessingSchema")==null)
                session.SetManagedScript("CapabilityPostProcessingSchema",Resources.Load<TextAsset>("CapabilityPostProcessingSchema").text);
            foreach(var adapter in adapters)PublishType(adapter);
            if(Assets){var source=new System.Text.StringBuilder("module CapabilityAssetIndex\n");int i=0;foreach(var item in Assets.Entries)if(item.Asset)source.Append("asset").Append(i++).Append(" { CapAsset: { key: ").Append(JsonConvert.ToString(item.Key)).Append(", type: ").Append(JsonConvert.ToString(item.Asset.GetType().FullName)).Append(" } }\n");session.SetManagedScript("CapabilityAssetIndex",source.ToString());}
        }
        void PublishType(ICapabilityAdapter a) {
            string notes="Runtime create/update/remove. One capability per binding entity; compose by shared targets and dependencies. Assets must be registered.";
            string code="module CapabilityTypes."+a.Component+"\ndefinition { CapType: {component:"+JsonConvert.ToString(a.Component)+",category:"+JsonConvert.ToString(a.Category)+",description:"+JsonConvert.ToString(notes)+"}";
            if(session.Lookup("EngineCapability")!=0)code+="; BoundCapability; EngineCapability: {category:"+JsonConvert.ToString(a.Category)+",command:"+JsonConvert.ToString(a.Component+" configuration with CapBinding target/enabled/revision")+",result:"+JsonConvert.ToString("CapState and capability-specific observed outputs")+",notes:"+JsonConvert.ToString(notes)+"}";
            session.SetManagedScript("CapabilityTypes."+a.Component,code+" }");
        }
        public void Register(ICapabilityAdapter adapter) {
            if(adapters.Any(a=>a.Component==adapter.Component))throw new ArgumentException("Duplicate capability adapter: "+adapter.Component);
            adapters.Add(adapter);adapters.Sort((a,b)=>a.Order.CompareTo(b.Order));if(session!=null)PublishType(adapter);
        }
        public Transform Resolve(ulong entity) { var custom=TargetResolver?.Invoke(entity);return custom?custom:World.FindView(entity)?.transform; }
        public void Reconcile() {
            EnsureSession();if(session==null||session.IsDisposed)return;
            var seen=new HashSet<string>();
            foreach(var adapter in adapters) {
                if(session.Lookup(adapter.Component)==0)continue;
                foreach(var row in JObject.Parse(session.Query("CapBinding, "+adapter.Component))["results"]) {
                    var values=row["fields"]?["values"];if(values==null)continue;
                    string path=row["parent"]==null?(string)row["name"]:(string)row["parent"]+"."+(string)row["name"];
                    var input=(JObject)values[0];var config=(JObject)values[1];
                    string key=path+"|"+adapter.Component;
                    if(!(bool)input["enabled"])continue;
                    seen.Add(key);
                    var targetId=session.Lookup((string)input["target"]); // Reflection serializes entity references as paths.
                    if(input["target"]?.Type==JTokenType.Integer)targetId=(ulong)input["target"];
                    if(targetId==0)targetId=session.Lookup(path);
                    string signature=input.ToString(Formatting.None)+config.ToString(Formatting.None);
                    if(!bindings.TryGetValue(key,out var binding)) {
                        if(bindings.Count>=256)throw new InvalidOperationException("Capability binding budget exceeded.");
                        var target=Resolve(targetId);if(!target){WriteState(path,0,(int)input["revision"],0,"Waiting for target view");continue;}
                        var host=new GameObject(adapter.Component+" binding");host.transform.SetParent(target,false);
                        binding=new Binding{Context=new CapabilityContext{Runtime=this,SessionOwner=session,Entity=session.Lookup(path),Path=path,TargetEntity=targetId,Target=target,Host=host}};
                        bindings.Add(key,binding);
                    }
                    try {
                        if(!binding.Context.Target||binding.Context.TargetEntity!=targetId){Remove(key);seen.Remove(key);continue;}
                        int ready=CapabilityNative.fu_cap_ready(session.NativeContext,binding.Context.Entity);
                        if(ready!=1){binding.Phase=0;if(binding.Context.Host)binding.Context.Host.SetActive(false);WriteState(path,ready<0?-1:0,(int)input["revision"],binding.Generation,ready<0?"Dependency failed or cycle":"Waiting for dependency");continue;}
                        bool dirty=CapabilityNative.fu_cap_has(session.NativeContext,binding.Context.Entity,"CapDirty")!=0;
                        if(binding.Signature!=signature||dirty||binding.Phase==0) {
                            if(binding.Signature=="!"+signature)continue;
                            if(!binding.Context.Host){binding.Context.Host=new GameObject(adapter.Component+" binding");binding.Context.Host.transform.SetParent(binding.Context.Target,false);}
                            binding.Instance??=adapter.Create(binding.Context);
                            binding.Context.Host.SetActive(true);binding.Instance.Apply(config);if(pendingRestore.Remove(key,out var restore))binding.Instance.RestoreState(restore);CapabilityNative.fu_cap_ack(session.NativeContext,binding.Context.Entity);binding.Signature=signature;binding.Phase=1;++binding.Generation;
                            WriteState(path,1,(int)input["revision"],binding.Generation,"");
                        }
                        
                    } catch(Exception e) {
                        DisposeBinding(binding);binding.Signature="!"+signature;binding.Phase=-1;
                        WriteState(path,-1,(int)input["revision"],binding.Generation,Failure(e));
                    }
                }
            }
            foreach(string key in bindings.Keys.Where(k=>!seen.Contains(k)).ToArray())Remove(key);
        }
        static string Failure(Exception e)=>(Application.isEditor||Debug.isDebugBuild)?e.ToString():e.Message;
        void WriteState(string path,int phase,int revision,int generation,string message) => session.SetComponentJson(path,"CapState",JsonConvert.SerializeObject(new{phase,appliedRevision=revision,generation,message}));
        float nextReconcile;
        void Update() {
            EnsureSession();if(!World||session==null||session.IsDisposed)return;
            if(Time.unscaledTime>=nextReconcile){nextReconcile=Time.unscaledTime+.05f;Reconcile();}
            foreach(var b in bindings.Values.ToArray())if(b.Phase==1)try{b.Instance?.Tick(Time.unscaledDeltaTime);}catch(Exception e){DisposeBinding(b);b.Phase=-1;WriteState(b.Context.Path,-1,0,b.Generation,Failure(e));}
        }
        void FixedUpdate() { if(session==null||session.IsDisposed)return;foreach(var b in bindings.Values.ToArray())if(b.Phase==1)try{b.Instance?.FixedTick(Time.fixedDeltaTime);}catch(Exception e){DisposeBinding(b);b.Phase=-1;WriteState(b.Context.Path,-1,0,b.Generation,Failure(e));} }
        void LateUpdate(){if(session==null||session.IsDisposed)return;foreach(var b in bindings.Values.ToArray())if(b.Phase==1)try{b.Instance?.LateTick(Time.deltaTime);}catch(Exception e){DisposeBinding(b);b.Phase=-1;WriteState(b.Context.Path,-1,0,b.Generation,Failure(e));}}
        void Remove(string key) {
            if(!bindings.Remove(key,out var b))return;
            DisposeBinding(b);
            if(session!=null&&!session.IsDisposed&&session.Lookup(b.Context.Path)!=0&&CapabilityNative.fu_cap_has(session.NativeContext,b.Context.Entity,"CapBinding")!=0)WriteState(b.Context.Path,0,0,b.Generation,"Disabled or removed");
        }
        static void DisposeBinding(Binding b){
            try{b.Instance?.Dispose();}finally{b.Instance=null;b.Context.ReleasePose();if(b.Context.Host){b.Context.Host.SetActive(false);Destroy(b.Context.Host);b.Context.Host=null;}}
        }
        public void ResetBindings(){Clear();}
        void Clear(){foreach(string key in bindings.Keys.ToArray())Remove(key);}
        void OnDestroy(){Clear();session=null;}
    }
    public sealed class Adapter<T> : ICapabilityAdapter where T:CapabilityInstance {
        public string Component{get;} public string Category{get;} public int Order{get;}
        readonly Func<CapabilityContext,T> create;
        public Adapter(string component,string category,int order,Func<CapabilityContext,T> factory){Component=component;Category=category;Order=order;create=factory;}
        public CapabilityInstance Create(CapabilityContext context)=>create(context);
    }
    public static class Fields {
        public static JArray Points(string json,int minimum=0){
            if(string.IsNullOrEmpty(json))json="[]";if(json.Length>65536)throw new ArgumentException("Point data is too large.");var points=JArray.Parse(json);
            if(points.Count<minimum||points.Count>256)throw new ArgumentException("Expected "+minimum+"..256 points.");
            foreach(var p in points){if(p is not JArray a||a.Count!=3)throw new ArgumentException("Points must be [x,y,z].");foreach(var item in a){if(item.Type!=JTokenType.Float&&item.Type!=JTokenType.Integer)throw new ArgumentException("Point coordinates must be numeric.");float value=(float)item;if(!float.IsFinite(value)||Mathf.Abs(value)>1000000)throw new ArgumentException("Point coordinates are outside the finite supported range.");}}
            return points;
        }
        public static float F(this JObject j,string key,float fallback=0){float f=(float?)j[key]??fallback;if(!float.IsFinite(f))throw new ArgumentException("Nonfinite "+key);return f;}
        public static int I(this JObject j,string key,int fallback=0)=>(int?)j[key]??fallback;
        public static bool B(this JObject j,string key)=>(bool?)j[key]??false;
        public static string S(this JObject j,string key)=>(string)j[key]??"";
        public static Vector3 XYZ(this JObject j)=>new(j.F("x"),j.F("y"),j.F("z"));
        public static Color RGB(this JObject j)=>new(j.F("colorR",1),j.F("colorG",1),j.F("colorB",1));
        public static ulong Entity(this JObject j,string key,FlecsSession s)=>j[key]?.Type==JTokenType.Integer?(ulong)j[key]:s.Lookup(j.S(key));
    }
}
