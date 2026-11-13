using UnityEngine;

namespace Runtime.Flecs
{
    public static class FlecsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSessions() => FlecsSession.DisposeAll();

        public static FlecsWorld Create(TextAsset script, Material material, ushort port = 27750, string scriptEntity = "World")
        {
            var root = new GameObject("Flecs World");
            root.SetActive(false);
            var world = root.AddComponent<FlecsWorld>();
            world.initialScript = script;
            world.scriptEntity = scriptEntity;
            world.presentationMaterial = material;
            world.restPort = port;
            Object.DontDestroyOnLoad(root);
            root.SetActive(true);
            return world;
        }
    }
}
