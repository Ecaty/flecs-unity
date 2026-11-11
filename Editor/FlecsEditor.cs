using UnityEditor;

namespace Ecaty.Flecs.Editor
{
    [InitializeOnLoad]
    public static class FlecsEditor
    {
        static FlecsEditor()
        {
            AssemblyReloadEvents.beforeAssemblyReload += FlecsSession.DisposeAll;
        }
        [MenuItem("Flecs/Open Explorer")]
        public static void OpenExplorer() => UnityEngine.Application.OpenURL("https://www.flecs.dev/explorer/?host=localhost:27750");
    }
}
