using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
namespace Ecaty.Flecs.Capabilities {
    public sealed class TerrainCapability:CapabilityInstance {
        MeshFilter filter;MeshCollider collider;Mesh mesh;
        public TerrainCapability(CapabilityContext c):base(c){filter=Host.AddComponent<MeshFilter>();var r=Host.AddComponent<MeshRenderer>();r.sharedMaterial=Context.Runtime.World.presentationMaterial;collider=Host.AddComponent<MeshCollider>();}
        public override void Apply(JObject j){
            float size=Mathf.Clamp(j.F("size",10),1,2000),baseline=j.F("height"),falloff=Mathf.Clamp(j.F("falloff",1),.05f,size);int n=Mathf.Clamp(j.I("resolution",33),3,129);
            string points=j.S("points");var source=j.Entity("source",Context.Session);
            if(source!=0){var input=Context.Session.ComponentJson(source,"CapSpline");if(string.IsNullOrEmpty(input))throw new ArgumentException("Terrain source must have CapSpline.");points=JObject.Parse(input).S("points");}
            var controls=Fields.Points(points);
            if(source!=0){var binding=JObject.Parse(Context.Session.ComponentJson(source,"CapBinding"));var sourceTarget=Context.Resolve(binding.Entity("target",Context.Session));if(!sourceTarget)throw new ArgumentException("Source spline target is missing.");for(int k=0;k<controls.Count;k++){var a=(JArray)controls[k];var local=Target.InverseTransformPoint(sourceTarget.TransformPoint(new Vector3((float)a[0],(float)a[1],(float)a[2])));controls[k]=new JArray(local.x,local.y,local.z);}}if(controls.Count>256)throw new ArgumentException("Too many ground controls.");
            if(source!=0&&JObject.Parse(Context.Session.ComponentJson(source,"CapSpline")).B("closed")&&controls.Count>2)controls.Add(controls[0].DeepClone());
            var vertices=new Vector3[n*n];var uv=new Vector2[n*n];var indices=new int[(n-1)*(n-1)*6];
            for(int z=0;z<n;z++)for(int x=0;x<n;x++){
                float px=(x/(float)(n-1)-.5f)*size,pz=(z/(float)(n-1)-.5f)*size,y=baseline,best=float.MaxValue;
                for(int k=0;k<controls.Count-1;k++){
                    var a=(JArray)controls[k];var b=(JArray)controls[k+1];var p0=new Vector3((float)a[0],(float)a[1],(float)a[2]);var p1=new Vector3((float)b[0],(float)b[1],(float)b[2]);
                    var ab=new Vector2(p1.x-p0.x,p1.z-p0.z);float t=Mathf.Clamp01(Vector2.Dot(new Vector2(px-p0.x,pz-p0.z),ab)/Mathf.Max(.00001f,ab.sqrMagnitude));float distance=Vector2.Distance(new Vector2(px,pz),new Vector2(p0.x,p0.z)+ab*t);
                    if(distance<best){best=distance;float weight=1-Mathf.SmoothStep(0,1,distance/falloff);y=Mathf.Lerp(baseline,Mathf.Lerp(p0.y,p1.y,t),weight);}
                }
                vertices[z*n+x]=new Vector3(px,y,pz);uv[z*n+x]=new Vector2(x/(float)(n-1),z/(float)(n-1));
            }
            int at=0;for(int z=0;z<n-1;z++)for(int x=0;x<n-1;x++){int a=z*n+x;indices[at++]=a;indices[at++]=a+n;indices[at++]=a+1;indices[at++]=a+1;indices[at++]=a+n;indices[at++]=a+n+1;}
            var next=new Mesh{name="Runtime modifier ground"};next.vertices=vertices;next.uv=uv;next.triangles=indices;next.RecalculateNormals();next.RecalculateBounds();filter.sharedMesh=next;collider.sharedMesh=null;collider.sharedMesh=next;if(mesh)UnityEngine.Object.Destroy(mesh);mesh=next;
            var bounds=collider.bounds;Context.Publish("CapBounds",new{minX=bounds.min.x,minY=bounds.min.y,minZ=bounds.min.z,maxX=bounds.max.x,maxY=bounds.max.y,maxZ=bounds.max.z});
        }
        public override void Dispose(){if(collider)collider.sharedMesh=null;if(mesh)UnityEngine.Object.Destroy(mesh);}
    }
    public sealed class CapabilityHandleMarker:MonoBehaviour { public ulong Entity; }
    public static class CapabilityInput {
        public static bool OverHandle(Camera camera){if(HandleCapability.IsDragging)return true;var mouse=Mouse.current;return camera&&mouse!=null&&Physics.Raycast(camera.ScreenPointToRay(mouse.position.ReadValue()),out var hit,10000,~0,QueryTriggerInteraction.Collide)&&hit.collider.GetComponent<CapabilityHandleMarker>();}
    }
    public sealed class HandleCapability:CapabilityInstance {
        static HandleCapability active;public static bool IsDragging=>active!=null;GameObject knob;float grid;Plane plane;bool ownsPose;ulong edit;string component;int index;JObject original;Vector3 begin;
        public HandleCapability(CapabilityContext c):base(c){knob=GameObject.CreatePrimitive(PrimitiveType.Sphere);knob.name="Manipulation handle";knob.GetComponent<Collider>().isTrigger=true;knob.transform.SetParent(Host.transform,false);knob.AddComponent<CapabilityHandleMarker>().Entity=c.Entity;knob.GetComponent<Renderer>().sharedMaterial=Context.Runtime.World.presentationMaterial;}
        public override void Apply(JObject j){knob.SetActive(j.B("enabled"));knob.transform.localScale=Vector3.one*Mathf.Clamp(j.F("radius",.12f)*2,.02f,2);grid=Mathf.Max(0,j.F("grid"));edit=j.Entity("edit",Context.Session);if(edit==0)edit=Context.TargetEntity;component=j.S("component");index=j.I("index");}
        public override void Tick(float delta){
            var mouse=Mouse.current;var camera=Context.Runtime.ViewCamera?Context.Runtime.ViewCamera:Camera.main??UnityEngine.Object.FindAnyObjectByType<Camera>();if(mouse==null||!camera)return;
            var ray=camera.ScreenPointToRay(mouse.position.ReadValue());
            if(active==null&&mouse.leftButton.wasPressedThisFrame&&Physics.Raycast(ray,out var hit,10000,~0,QueryTriggerInteraction.Collide)&&hit.collider.gameObject==knob){active=this;plane=new Plane(camera.transform.forward,Target.position);begin=Target.position;ownsPose=Context.Runtime.World.AcquirePose(Context.TargetEntity,this);if(!string.IsNullOrEmpty(component)){var json=Context.Session.ComponentJson(edit,component);original=string.IsNullOrEmpty(json)?null:JObject.Parse(json);}}
            if(active!=this)return;
            if(mouse.leftButton.isPressed&&plane.Raycast(ray,out float distance)){
                var world=ray.GetPoint(distance);if(grid>0)world=new Vector3(Mathf.Round(world.x/grid)*grid,Mathf.Round(world.y/grid)*grid,Mathf.Round(world.z/grid)*grid);
                Context.Session.SetComponentJson(Context.Session.EntityPath(Context.TargetEntity),"CapDrag",JsonConvert.SerializeObject(new{x=world.x,y=world.y,z=world.z,active=true}));
                if(ownsPose)Target.position=world;
                if(original!=null){var next=(JObject)original.DeepClone();var local=Target.parent?Target.parent.InverseTransformPoint(world):world;
                    if(component=="CapSpline"||component=="CapBuilding"){var points=JArray.Parse((string)next["points"]);if(index<0||index>=points.Count)throw new ArgumentException("Handle point is outside the recipe.");points[index]=new JArray(local.x,local.y,local.z);next["points"]=points.ToString(Formatting.None);}
                    else {string prefix=component=="ClayRest"?"p":"";next[prefix+"x"]=local.x;next[prefix+"y"]=local.y;next[prefix+"z"]=local.z;}
                    Context.Session.SetComponentJson(Context.Session.EntityPath(edit),component,next.ToString(Formatting.None));
                    // Selected edited state is saved alongside its reconstructable managed source.
                    if(Context.Session.Lookup("PersistState")!=0)Context.Session.SetComponentJson(Context.Session.EntityPath(edit),"PersistState",JsonConvert.SerializeObject(new{components=new JArray(component).ToString(Formatting.None)}));
                }
            }
            if(mouse.leftButton.wasReleasedThisFrame)End();
        }
        void End(){if(active!=this)return;if(Context.Session==null||Context.Session.IsDisposed){active=null;return;}Context.Session.SetComponentJson(Context.Session.EntityPath(Context.TargetEntity),"CapDrag",JsonConvert.SerializeObject(new{x=Target.position.x,y=Target.position.y,z=Target.position.z,active=false}));if(ownsPose)Context.Runtime.World.ReleasePose(Context.TargetEntity,this);ownsPose=false;active=null;}
        public override void Dispose(){if(active==this)End();}
    }
}
