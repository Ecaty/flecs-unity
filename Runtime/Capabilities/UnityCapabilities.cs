using System;
using System.Collections.Generic;
using Ecaty.Flecs;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using Unity.Cinemachine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.Animations.Rigging;
using UnityEngine.VFX;
namespace Ecaty.Flecs.Capabilities
{
    public static class UnityCapabilities {
        public static void Register(CapabilityRuntime runtime) {
            runtime.Register(new Adapter<TerrainCapability>("CapTerrain","ground modifiers",10,c=>new(c)));
            runtime.Register(new Adapter<HandleCapability>("CapHandle","direct manipulation",50,c=>new(c)));
            runtime.Register(new Adapter<BodyCapability>("CapBody","physics",20,c=>new(c)));
            runtime.Register(new Adapter<MaterialCapability>("CapMaterial","appearance",40,c=>new(c)));
            runtime.Register(new Adapter<ParticleCapability>("CapParticles","effects",40,c=>new(c)));
            runtime.Register(new Adapter<PostProcessingCapability>("CapPostProcessing","world appearance",40,c=>new(c)));
            runtime.Register(new Adapter<LightCapability>("CapLight","lighting",40,c=>new(c)));
            runtime.Register(new Adapter<AudioCapability>("CapAudio","audio",40,c=>new(c)));
            runtime.Register(new Adapter<SurfaceCapability>("CapNavSurface","navigation surface",30,c=>new(c)));
            runtime.Register(new Adapter<NavigationCapability>("CapNavigation","navigation intent",35,c=>new(c)));
            runtime.Register(new Adapter<CameraCapability>("CapCamera","camera",40,c=>new(c)));
            runtime.Register(new Adapter<TimelineCapability>("CapTimeline","presentation",40,c=>new(c)));
            runtime.Register(new Adapter<RigCapability>("CapRig","IK constraints",40,c=>new(c)));
            runtime.Register(new Adapter<VfxCapability>("CapVfx","GPU effects",40,c=>new(c)));
            runtime.Register(new Adapter<WaterCapability>("CapWater","water volume",20,c=>new(c)));
            runtime.Register(new Adapter<BuoyancyCapability>("CapBuoyancy","buoyancy",25,c=>new(c)));
        }
    }
    public sealed class ContactReadback:MonoBehaviour {
        readonly HashSet<Collider> touching=new();public int Count=>touching.Count;
        void OnCollisionEnter(Collision c)=>touching.Add(c.collider);
        void OnCollisionExit(Collision c)=>touching.Remove(c.collider);
        void OnDisable()=>touching.Clear();
    }
    public sealed class BodyCapability:CapabilityInstance {
        Rigidbody body;CapsuleCollider collider;ContactReadback contacts;float gravity;int forceSequence=int.MinValue;
        bool drag,wasKinematic;float lastReport;
        public BodyCapability(CapabilityContext c):base(c){}
        public override void Apply(JObject j){
            if(j.F("mass",1)<=0||j.F("radius",.5f)<=0)throw new ArgumentException("Mass/radius must be positive.");
            if(!body){
                if(Target.GetComponent<CharacterController>()||Target.GetComponent<Rigidbody>())throw new InvalidOperationException("Target already has a motor/body; remove its binding before switching owner.");
                Context.OwnPose();body=Target.gameObject.AddComponent<Rigidbody>();collider=Target.gameObject.AddComponent<CapsuleCollider>();contacts=Target.gameObject.AddComponent<ContactReadback>();
            }
            body.mass=Mathf.Clamp(j.F("mass",1),.001f,10000);body.useGravity=false;body.isKinematic=j.B("kinematic");
            collider.radius=Mathf.Clamp(j.F("radius",.5f),.01f,100);collider.height=Mathf.Clamp(j.F("height",1),collider.radius*2,200);
            gravity=Mathf.Clamp(j.F("gravity",1),-20,20);
        }
        public override void FixedTick(float delta){
            if(!body)return;
            var request=Context.Session.ComponentJson(Context.TargetEntity,"CapDrag");
            var d=string.IsNullOrEmpty(request)?null:JObject.Parse(request);bool active=d?.B("active")==true;
            if(active&&!drag){wasKinematic=body.isKinematic;body.isKinematic=true;drag=true;}
            if(active){body.MovePosition(d.XYZ());return;}
            if(drag){body.isKinematic=wasKinematic;drag=false;}
            if(!body.isKinematic)body.AddForce(Physics.gravity*gravity,ForceMode.Acceleration);
            var f=Context.Read("CapForce");if(f!=null&&f.I("sequence")!=forceSequence){forceSequence=f.I("sequence");if(!body.isKinematic)body.AddForce(Vector3.ClampMagnitude(f.XYZ(),10000),ForceMode.Impulse);}
            if(Time.unscaledTime-lastReport>.05f){lastReport=Time.unscaledTime;var p=body.position;var v=body.linearVelocity;Context.Publish("CapReadback",new{x=p.x,y=p.y,z=p.z,vx=v.x,vy=v.y,vz=v.z,contacts=contacts.Count});}
        }
        public override JObject CaptureState(){if(!body)return null;return new JObject{["position"]=new JArray(body.position.x,body.position.y,body.position.z),["rotation"]=new JArray(body.rotation.x,body.rotation.y,body.rotation.z,body.rotation.w),["velocity"]=new JArray(body.linearVelocity.x,body.linearVelocity.y,body.linearVelocity.z),["angularVelocity"]=new JArray(body.angularVelocity.x,body.angularVelocity.y,body.angularVelocity.z)};}
        public override void RestoreState(JObject j){var p=(JArray)j["position"];var q=(JArray)j["rotation"];var v=(JArray)j["velocity"];body.position=new Vector3((float)p[0],(float)p[1],(float)p[2]);body.rotation=new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]);if(!body.isKinematic){body.linearVelocity=new Vector3((float)v[0],(float)v[1],(float)v[2]);if(j["angularVelocity"] is JArray a)body.angularVelocity=new Vector3((float)a[0],(float)a[1],(float)a[2]);}}
        public override void Dispose(){if(body)UnityEngine.Object.Destroy(body);if(collider)UnityEngine.Object.Destroy(collider);if(contacts)UnityEngine.Object.Destroy(contacts);Context.ReleasePose();}
    }
    public sealed class MaterialCapability:CapabilityInstance {
        readonly List<(Renderer,Material,MaterialPropertyBlock)> originals=new();
        public MaterialCapability(CapabilityContext c):base(c){if(!c.Runtime.World.AcquireAppearance(c.TargetEntity,this))throw new InvalidOperationException("Target already has an appearance owner.");}
        public override void Apply(JObject j){
            foreach(var r in Target.GetComponentsInChildren<Renderer>()){
                if(!originals.Exists(x=>x.Item1==r)){var old=new MaterialPropertyBlock();r.GetPropertyBlock(old);originals.Add((r,r.sharedMaterial,old));}
                var material=Context.Asset<Material>(j.S("material"));if(material)r.sharedMaterial=material;
                var block=new MaterialPropertyBlock();r.GetPropertyBlock(block);block.SetColor("_BaseColor",j.RGB());block.SetColor("_Color",j.RGB());block.SetFloat("_Metallic",Mathf.Clamp01(j.F("metallic")));block.SetFloat("_Smoothness",Mathf.Clamp01(j.F("smoothness")));r.SetPropertyBlock(block);
            }
        }
        public override void Dispose(){Context.Runtime.World.ReleaseAppearance(Context.TargetEntity,this);foreach(var item in originals)if(item.Item1){item.Item1.sharedMaterial=item.Item2;item.Item1.SetPropertyBlock(item.Item3);}}
    }
    public sealed class ParticleCapability:CapabilityInstance {
        ParticleSystem particles;
        public ParticleCapability(CapabilityContext c):base(c){particles=Host.AddComponent<ParticleSystem>();particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);}
        public override void Apply(JObject j){var m=particles.main;m.maxParticles=2048;m.startLifetime=Mathf.Clamp(j.F("lifetime",1),.01f,60);m.startSpeed=j.F("speed",1);m.startSize=Mathf.Clamp(j.F("size",.1f),.001f,10);m.startColor=j.RGB();var e=particles.emission;e.rateOverTime=Mathf.Clamp(j.F("rate",10),0,1000);particles.Play();}
    }
    public sealed class LightCapability:CapabilityInstance {
        readonly Light light;public LightCapability(CapabilityContext c):base(c){light=Host.AddComponent<Light>();}
        public override void Apply(JObject j){light.type=j.I("kind")==1?LightType.Directional:j.I("kind")==2?LightType.Spot:LightType.Point;light.color=j.RGB();light.intensity=Mathf.Clamp(j.F("intensity",1),0,100);light.range=Mathf.Clamp(j.F("range",10),.01f,1000);}
    }
    public sealed class SurfaceCapability:CapabilityInstance {
        readonly NavMeshSurface surface;
        public SurfaceCapability(CapabilityContext c):base(c){surface=Host.AddComponent<NavMeshSurface>();surface.collectObjects=CollectObjects.Volume;surface.useGeometry=NavMeshCollectGeometry.PhysicsColliders;}
        public override void Apply(JObject j){surface.size=new Vector3(Mathf.Clamp(j.F("radius",10)*2,1,1000),Mathf.Clamp(j.F("height",10),1,1000),Mathf.Clamp(j.F("radius",10)*2,1,1000));surface.BuildNavMesh();}
        public override void Dispose(){surface.RemoveData();}
    }
    public sealed class NavigationCapability:CapabilityInstance {
        readonly NavMeshPath path=new();Vector3 goal;float speed,stopping;ulong command;float next;
        public NavigationCapability(CapabilityContext c):base(c){}
        public override void Apply(JObject j){goal=j.XYZ();speed=Mathf.Clamp(j.F("speed",1),0,100);stopping=Mathf.Max(.01f,j.F("stopping",.1f));command=j.Entity("command",Context.Session);next=0;}
        public override void Tick(float delta){
            if(Time.unscaledTime<next)return;next=Time.unscaledTime+.15f;
            int status=0;Vector3 direction=Vector3.zero;
            if(NavMesh.SamplePosition(Target.position,out var start,2,NavMesh.AllAreas)&&NavMesh.CalculatePath(start.position,goal,NavMesh.AllAreas,path)&&path.corners.Length>1){direction=path.corners[1]-Target.position;direction.y=0;direction=Vector3.Distance(Target.position,goal)>stopping?direction.normalized*speed:Vector3.zero;status=(int)path.status+1;}
            Context.Publish("CapPath",new{x=direction.x,y=direction.y,z=direction.z,status});
            if(command!=0){string name=Context.Session.EntityPath(command);Context.Session.SetComponentJson(name,"MoveIntent",Newtonsoft.Json.JsonConvert.SerializeObject(new{x=direction.x,z=direction.z}));}
        }
    }
    public sealed class CameraCapability:CapabilityInstance {
        readonly CinemachineCamera camera;readonly CinemachineFollow follow;
        public CameraCapability(CapabilityContext c):base(c){camera=Host.AddComponent<CinemachineCamera>();follow=Host.AddComponent<CinemachineFollow>();Host.AddComponent<CinemachineRotationComposer>();camera.Follow=Target;camera.LookAt=Target;}
        public override void Apply(JObject j){follow.FollowOffset=new Vector3(0,j.F("height",4),-Mathf.Max(.1f,j.F("distance",8)));camera.Priority=j.I("priority",10);}
    }
    public sealed class TimelineCapability:CapabilityInstance {
        readonly PlayableDirector director;TimelineAsset timeline;
        public TimelineCapability(CapabilityContext c):base(c){director=Host.AddComponent<PlayableDirector>();director.playOnAwake=false;}
        public override void Apply(JObject j){
            director.Stop();if(timeline)UnityEngine.Object.Destroy(timeline);
            timeline=ScriptableObject.CreateInstance<TimelineAsset>();timeline.durationMode=TimelineAsset.DurationMode.FixedLength;timeline.fixedDuration=Mathf.Clamp(j.F("duration",1),.01f,3600);
            var clip=Context.Asset<AudioClip>(j.S("audio"));if(clip){var track=timeline.CreateTrack<AudioTrack>(null,"Audio");var tc=track.CreateClip<AudioPlayableAsset>();((AudioPlayableAsset)tc.asset).clip=clip;tc.duration=Math.Min(clip.length,timeline.fixedDuration);var source=Host.GetComponent<AudioSource>()??Host.AddComponent<AudioSource>();director.SetGenericBinding(track,source);}
            director.playableAsset=timeline;director.time=Math.Clamp(j.F("time"),0,timeline.fixedDuration);director.Evaluate();if(j.B("playing"))director.Play();
        }
        public override void Dispose(){director.Stop();if(timeline)UnityEngine.Object.Destroy(timeline);}
    }
    public sealed class RigCapability:CapabilityInstance {
        RigBuilder builder;Rig rig;TwoBoneIKConstraint constraint;bool ownBuilder,ownAnimator;float nextReport;
        public RigCapability(CapabilityContext c):base(c){}
        public override void Apply(JObject j){
            Transform root=Context.Resolve(j.Entity("root",Context.Session)),mid=Context.Resolve(j.Entity("mid",Context.Session)),tip=Context.Resolve(j.Entity("tip",Context.Session)),goal=Context.Resolve(j.Entity("goal",Context.Session));
            if(!root||!mid||!tip||!goal||!mid.IsChildOf(root)||!tip.IsChildOf(mid))throw new ArgumentException("IK needs a valid root/mid/tip chain and target.");
            var animator=Target.GetComponent<Animator>();if(!animator){animator=Target.gameObject.AddComponent<Animator>();ownAnimator=true;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;}
            if(!builder){builder=Target.GetComponent<RigBuilder>();if(!builder){builder=Target.gameObject.AddComponent<RigBuilder>();ownBuilder=true;}rig=Host.AddComponent<Rig>();var go=new GameObject("Two bone constraint");go.transform.SetParent(Host.transform,false);constraint=go.AddComponent<TwoBoneIKConstraint>();builder.layers.Add(new RigLayer(rig));}
            builder.Clear();var data=constraint.data;data.root=root;data.mid=mid;data.tip=tip;data.target=goal;data.targetPositionWeight=1;data.targetRotationWeight=0;constraint.data=data;constraint.weight=Mathf.Clamp01(j.F("weight",1));if(!builder.Build())throw new InvalidOperationException("Runtime rig graph could not build.");
        }
        public override void LateTick(float delta){if(Time.unscaledTime<nextReport||!constraint)return;nextReport=Time.unscaledTime+.05f;var data=constraint.data;var p=data.tip.position;Context.Publish("CapRigResult",new{x=p.x,y=p.y,z=p.z,error=Vector3.Distance(p,data.target.position)});}
        public override void Dispose(){if(builder){builder.Clear();builder.layers.RemoveAll(l=>l.rig==rig);if(ownBuilder)UnityEngine.Object.Destroy(builder);else builder.Build();}if(ownAnimator)UnityEngine.Object.Destroy(Target.GetComponent<Animator>());}
    }
    public sealed class VfxCapability:CapabilityInstance {
        readonly VisualEffect vfx;int trigger=int.MinValue;
        public VfxCapability(CapabilityContext c):base(c){vfx=Host.AddComponent<VisualEffect>();}
        public override void Apply(JObject j){var asset=Context.Asset<VisualEffectAsset>(j.S("asset"));if(!asset)throw new ArgumentException("Register a compiled VFX asset in the capability asset catalog.");vfx.visualEffectAsset=asset;if(vfx.HasFloat("Rate"))vfx.SetFloat("Rate",j.F("rate",10));if(j.I("trigger")!=trigger){trigger=j.I("trigger");vfx.SendEvent(string.IsNullOrEmpty(j.S("event"))?"OnPlay":j.S("event"));}}
    }
    public sealed class WaterVolume:MonoBehaviour { public float Level,Density=1000; }
    public sealed class WaterCapability:CapabilityInstance {
        readonly WaterVolume water;public WaterCapability(CapabilityContext c):base(c){water=Host.AddComponent<WaterVolume>();}
        public override void Apply(JObject j){water.Level=j.F("level");water.Density=Mathf.Clamp(j.F("density",1000),1,20000);}
    }
    public sealed class BuoyancyCapability:CapabilityInstance {
        WaterVolume water;Rigidbody body;float displacement,drag;
        public BuoyancyCapability(CapabilityContext c):base(c){}
        public override void Apply(JObject j){var volume=Context.Resolve(j.Entity("volume",Context.Session));water=volume?volume.GetComponentInChildren<WaterVolume>():null;body=Target.GetComponent<Rigidbody>();if(!water||!body)throw new ArgumentException("Buoyancy needs a bound body and water volume.");displacement=Mathf.Clamp(j.F("displacement",.001f),0,100);drag=Mathf.Clamp(j.F("drag",1),0,100);}
        public override void FixedTick(float delta){if(body.isKinematic)return;float submerged=Mathf.Clamp01(water.Level-body.worldCenterOfMass.y+.5f);body.AddForce(-Physics.gravity*water.Density*displacement*submerged-body.linearVelocity*drag*submerged,ForceMode.Force);}
    }
}
