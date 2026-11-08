using System;
using UnityEngine;
namespace Ecaty.Flecs.Capabilities
{
    [CreateAssetMenu(menuName="Ecaty/Runtime capability assets")]
    public sealed class CapabilityAssets : ScriptableObject {
        [Serializable] public struct Entry { public string Key; public UnityEngine.Object Asset; }
        public Entry[] Entries=Array.Empty<Entry>();
        public T Resolve<T>(string key) where T:UnityEngine.Object {
            if(string.IsNullOrEmpty(key))return null;
            foreach(var entry in Entries)if(entry.Key==key)return entry.Asset as T;
            throw new ArgumentException("Unregistered or incompatible asset: "+key);
        }
    }
}
