using System;
using System.IO;
using System.Linq;
using ArchSemanticPilot;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArchSemanticPilotEditor
{
    public static class ModelPreparation
    {
        public const string RuntimeRoot = "Assets/ArchSemantic/Models/Runtime";
        static readonly string[] Slots = { "Wall", "Floor", "Glass", "Frame", "Door", "Ceiling", "Fixture", "Light" };


        static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play mode before preparing model assets.");
        }

        static Material[] Palette() => Slots.Select(n => AssetDatabase.LoadAssetAtPath<Material>(ModelImporter.Materials + "/" + n + ".mat")).ToArray();

        static bool CollisionSlot(int i) => i == 0 || i == 1 || i == 2 || i == 4 || i == 6;
        [MenuItem("CMER/Model assets/Bake new baseline prefab copies")]
        public static void BakeNewCopies()
        {
            RequireEditMode();
            string folder = RuntimeRoot + "/Draft_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            for (int scenario = 0; scenario < 2; scenario++) Bake(scenario, folder);
            Debug.Log("Created draft A/B prefabs at " + folder + ".");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/A/Model.prefab"));
        }

        public static GameObject Bake(int scenario, string destination)
        {
            if (scenario < 0 || scenario > 1) throw new ArgumentOutOfRangeException(nameof(scenario));
            string folder = destination + "/" + (scenario == 0 ? "A" : "B");
            if (!folder.StartsWith(RuntimeRoot + "/", StringComparison.Ordinal) || folder.Contains("..") || Directory.Exists(folder))
                throw new InvalidOperationException("Choose a new folder under " + RuntimeRoot + ".");
            var palette = Palette();
            if (palette.Any(m => m == null)) throw new InvalidDataException("Missing architecture material.");
            GameObject worker = new GameObject("Temporary baseline computation");
            GameObject root = new GameObject("CMER_" + (scenario == 0 ? "A" : "B") + "_Architecture");
            try
            {
                var driver = worker.AddComponent<LiveGeometryDriver>();
                driver.materials = palette;
                var prepared = driver.BuildNow(scenario, default);
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
                for (int i = 0; i < Slots.Length; i++)
                {
                    var group = prepared.groups[i];
                    var mesh = new Mesh { name = Slots[i], indexFormat = IndexFormat.UInt32 };
                    mesh.SetVertices(group.vertices);
                    mesh.SetNormals(group.normals);
                    mesh.SetTriangles(group.indices, 0, true);
                    AssetDatabase.CreateAsset(mesh, folder + "/" + Slots[i] + ".asset");
                    var part = new GameObject(Slots[i]);
                    part.layer = 8;
                    part.transform.SetParent(root.transform, false);
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = part.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = palette[i];
                    if (CollisionSlot(i)) part.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Model.prefab");
                AssetDatabase.SaveAssets();
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(worker);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [MenuItem("CMER/Model assets/Attach baseline to an unbound study scene")]
        public static void AttachToActiveScene()
        {
            RequireEditMode();
            var session = UnityEngine.Object.FindFirstObjectByType<PilotSession>();
            if (session == null || session.geometry == null) throw new InvalidOperationException("Open a CMER study scene first.");
            var driver = session.geometry;
            if (driver.modelParts != null && driver.modelParts.Length > 0)
                throw new InvalidOperationException("Scene already bound.");
            if (driver.transform.childCount != 0)
                throw new InvalidOperationException("Live Architecture has authored children. Bind its eight modelParts explicitly; nothing was removed.");
            string scenario = session.settings.sequence < 2 ? "A" : "B";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RuntimeRoot + "/" + scenario + "/Model.prefab");
            if (prefab == null) throw new InvalidDataException("Baseline prefab is missing.");
            Undo.RecordObject(driver, "Bind architecture prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, driver.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Bind architecture prefab");
            driver.modelParts = Slots.Select(n => instance.transform.Find(n).GetComponent<MeshFilter>()).ToArray();
            for (int i = 0; i < Slots.Length; i++)
            {
                // Scene-authored material choices take precedence over the default palette.
                var renderer = driver.modelParts[i].GetComponent<MeshRenderer>();
                renderer.sharedMaterial = driver.materials[i];
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            EditorUtility.SetDirty(driver);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(driver.gameObject.scene);
        }
        public static void ValidateBinding(PilotSession session)
        {
            var driver = session.geometry;
            if (driver.modelParts == null || driver.modelParts.Length == 0) return; // Older scenes remain supported.
            if (driver.modelParts.Length != 8 || driver.modelParts.Distinct().Count() != 8)
                throw new InvalidDataException("Expected eight unique architecture prefab bindings.");
            for (int i = 0; i < 8; i++)
            {
                var part = driver.modelParts[i];
                if (part == null || part.sharedMesh == null || !part.transform.IsChildOf(driver.transform) ||
                    part.GetComponent<MeshRenderer>() == null || part.gameObject.layer != 8 ||
                    (CollisionSlot(i) && part.GetComponent<MeshCollider>() == null))
                    throw new InvalidDataException("Invalid architecture prefab slot " + i);
                Matrix4x4 relative = driver.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        if (Mathf.Abs(relative[row, col] - (row == col ? 1 : 0)) > .0001f)
                            throw new InvalidDataException("Non-identity architecture mesh container transform.");
                if (part.GetComponent<MeshRenderer>().sharedMaterial == null)
                    throw new InvalidDataException("Missing architecture prefab material at slot " + i);
            }
        }
        [MenuItem("CMER/Model assets/Refresh GH prefabs")]
        public static void RefreshGhPrefabs()
        {
            RequireEditMode();
            LiveGeometryKernel.ReloadResources();
            var worker = new GameObject("GH import");
            try {
                var driver = worker.AddComponent<LiveGeometryDriver>(); driver.materials = Palette();
                for (int scenario = 0; scenario < 2; scenario++) {
                    var data = driver.BuildNow(scenario, default);
                    string id = scenario == 0 ? "A" : "B";
                    for (int i = 0; i < Slots.Length; i++) {
                        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(RuntimeRoot + "/" + id + "/" + Slots[i] + ".asset");
                        var group = data.groups[i]; mesh.Clear();mesh.SetVertices(group.vertices);mesh.SetNormals(group.normals);mesh.SetTriangles(group.indices,0,true);EditorUtility.SetDirty(mesh);
                    }
                    ModelImporter.Import(id);
                }
                AssetDatabase.SaveAssets();
            } finally { UnityEngine.Object.DestroyImmediate(worker); }
        }
    }
}
