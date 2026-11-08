using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Ecaty.Flecs.Capabilities {
    public sealed class NativeAudio : IDisposable {
        const string Library="flecs_unity";
        [DllImport(Library)] static extern IntPtr fu_audio_create(byte[] data,int size,int rate);
        [DllImport(Library)] static extern void fu_audio_destroy(IntPtr renderer);
        [DllImport(Library)] static extern int fu_audio_note(IntPtr renderer,int kind,int note,float velocity,int program,ulong sample);
        [DllImport(Library)] static extern void fu_audio_render(IntPtr renderer,[Out] float[] output,int count);
        readonly object gate=new();IntPtr renderer;long samples;public long Samples=>Interlocked.Read(ref samples);public float Peak{get;private set;}
        public NativeAudio(byte[] bank,int rate){renderer=fu_audio_create(bank,bank.Length,rate);if(renderer==IntPtr.Zero)throw new InvalidOperationException("SoundFont could not be loaded.");}
        public void Note(int kind,int note,float velocity,int program,double delay,double duration,int rate){
            lock(gate){if(renderer==IntPtr.Zero)return;ulong at=(ulong)Math.Max(0,Samples+delay*rate);if(fu_audio_note(renderer,kind,Math.Clamp(note,0,127),Math.Clamp(velocity,0,1),program,at)!=0)throw new InvalidOperationException("Native audio command queue full.");if(kind==0&&duration>0&&fu_audio_note(renderer,1,note,0,program,at+(ulong)(duration*rate))!=0)throw new InvalidOperationException("Native audio note-off queue full.");}
        }
        // Only this renderer's small lifetime lock is held. No live ECS or Unity API is called here.
        public void Render(float[] output){lock(gate){if(renderer==IntPtr.Zero){Array.Clear(output,0,output.Length);return;}fu_audio_render(renderer,output,output.Length);float peak=0;foreach(float value in output)peak=Math.Max(peak,Math.Abs(value));Peak=peak;Interlocked.Add(ref samples,output.Length);}}
        public void Dispose(){lock(gate){if(renderer==IntPtr.Zero)return;fu_audio_destroy(renderer);renderer=IntPtr.Zero;}}
    }
    public sealed class AudioCapability:CapabilityInstance {
        static byte[] bank;NativeAudio synth;AudioSource source;AudioClip clip;AudioLowPassFilter filter;int trigger=int.MinValue,noteSequence=int.MinValue,program,rate;float nextReport;
        public AudioCapability(CapabilityContext c):base(c){}
        public override void Apply(JObject j){
            if(source==null){
                bank??=File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,"Ecaty/Audio/GeneralUser-GS.sf2"));rate=AudioSettings.outputSampleRate;
                synth=new NativeAudio(bank,rate);clip=AudioClip.Create("Native runtime synthesis",rate,1,rate,true,synth.Render);
                source=Host.AddComponent<AudioSource>();source.playOnAwake=false;source.loop=true;source.clip=clip;filter=Host.AddComponent<AudioLowPassFilter>();source.Play();
            }
            source.volume=Mathf.Clamp01(j.F("gain",.5f));source.spatialBlend=Mathf.Clamp01(j.F("spatial"));source.pitch=Mathf.Clamp(j.F("pitch",1),.1f,3);source.minDistance=1;source.maxDistance=50;
            filter.cutoffFrequency=Mathf.Clamp(j.F("lowPass",22000),10,22000);program=j.I("program");
            if(j.I("trigger")!=trigger){trigger=j.I("trigger");synth.Note(j.I("synthesis")==1?3:j.I("synthesis")==2?4:0,j.I("note",60),1,program,0,.3,rate);}
        }
        public override void Tick(float delta){
            var j=Context.Read("CapAudioNote");if(j!=null&&j.I("sequence")!=noteSequence){noteSequence=j.I("sequence");synth.Note(0,j.I("note",60),j.F("velocity",1),program,Math.Clamp(j.F("delay"),0,60),Math.Clamp(j.F("duration",.3f),.01f,60),rate);}
            if(Time.unscaledTime>nextReport){nextReport=Time.unscaledTime+.15f;Context.Publish("CapAudioMeter",new{peak=synth.Peak,samples=(ulong)synth.Samples});}
        }
        public override void Dispose(){if(source)source.Stop();if(clip)UnityEngine.Object.Destroy(clip);synth?.Dispose();}
    }
    public static class CapabilityNative {
        [DllImport("flecs_unity")] public static extern int fu_cap_has(IntPtr context,ulong entity,[MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport("flecs_unity")] public static extern int fu_cap_ready(IntPtr context,ulong entity);
        [DllImport("flecs_unity")] public static extern void fu_cap_ack(IntPtr context,ulong entity);
    }
    public static class NativeGeometry {
        [DllImport("flecs_unity")] static extern int fu_path_outline(double[] points,int count,double radius,[Out] double[] output,int capacity,[Out] int[] starts,int ringsCapacity,out int ringCount);
        public static Vector2[][] Outline(Vector2[] points,float radius){
            var xy=new double[points.Length*2];for(int i=0;i<points.Length;i++){xy[i*2]=points[i].x;xy[i*2+1]=points[i].y;}
            int count=fu_path_outline(xy,points.Length,radius,null,0,null,0,out int rings);if(count<0)throw new ArgumentException("Invalid polygon footprint.");
            var output=new double[count*2];var starts=new int[rings+1];if(fu_path_outline(xy,points.Length,radius,output,count,starts,starts.Length,out rings)!=count)throw new InvalidOperationException("Footprint changed during generation.");
            var result=new Vector2[rings][];for(int r=0;r<rings;r++){result[r]=new Vector2[starts[r+1]-starts[r]];for(int i=starts[r];i<starts[r+1];i++)result[r][i-starts[r]]=new Vector2((float)output[i*2],(float)output[i*2+1]);}return result;
        }
    }
}
