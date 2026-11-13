using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Runtime.Flecs.Editor
{
    [ScriptedImporter(1, "flecs")]
    public sealed class FlecsScriptImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = new TextAsset(File.ReadAllText(context.assetPath));
            context.AddObjectToAsset("Script", asset);
            context.SetMainObject(asset);
        }
    }
}
