using System;
using System.IO;
using System.Linq;
using ArchSemanticPilot;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArchSemanticPilotEditor
{
    /// <summary>
    /// Authoring-only model preparation. Runtime uses the existing driver and geometry rules;
    /// incoming GH geometry remains an isolated review asset until its behavior is reconciled.
    /// No automatic scene regeneration or replacement runs on project open.
    /// </summary>
    public static class ModelPreparation
    {
        public const string RuntimeRoot = "Assets/ArchSemantic/Models/Runtime";
        static readonly string[] Slots = { "Wall", "Floor", "Glass", "Frame", "Door", "Ceiling", "Fixture", "Light" };

        [Serializable]
        sealed class ReviewPacket
        {
            public string schema, scenario, binaryFile, binarySha256, sourceJsonSha256;
        }

        static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play mode before preparing model assets.");
        }

        static Material[] Palette() => Slots.Select(n => AssetDatabase.LoadAssetAtPath<Material>(ModelImporter.Materials + "/" + n + ".mat")).ToArray();

        static bool CollisionSlot(int i) => i == 0 || i == 1 || i == 2 || i == 4 || i == 6;

        /// <summary>Bake the current runtime kernel, not the older GH reference snapshot.</summary>
        [MenuItem("CMER/Model assets/Bake new baseline prefab copies")]
        public static void BakeNewCopies()
        {
            RequireEditMode();
            string folder = RuntimeRoot + "/Draft_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            for (int scenario = 0; scenario < 2; scenario++) Bake(scenario, folder);
            Debug.Log("Created draft A/B prefabs at " + folder + ". Existing assets and scenes were not replaced.");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/A/Model.prefab"));
        }

        public static GameObject Bake(int scenario, string destination)
        {
            if (scenario < 0 || scenario > 1) throw new ArgumentOutOfRangeException(nameof(scenario));
            string folder = destination + "/" + (scenario == 0 ? "A" : "B");
            if (!folder.StartsWith(RuntimeRoot + "/", StringComparison.Ordinal) || folder.Contains("..") || Directory.Exists(folder))
                throw new InvalidOperationException("Choose a new folder under " + RuntimeRoot + "; authored assets are never overwritten.");
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
                throw new InvalidOperationException("This scene is already bound. Existing prefab bindings were preserved.");
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

        /// <summary>Structural checks only; existing geometry regressions remain authoritative.</summary>
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

        /// <summary>
        /// Tools/prepare_cmer_gh.py converts supported GH JSON to a checked ASB1 packet.
        /// Reuse the existing importer; review meshes, materials and metadata stay Editor-only.
        /// The packet does not certify runtime deformation compatibility.
        /// </summary>
        [MenuItem("CMER/Model assets/Import GH review packet")]
        public static void ImportReviewPacket()
        {
            RequireEditMode();
            string path = EditorUtility.OpenFilePanel("Select a prepared GH review manifest", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            var packet = JsonUtility.FromJson<ReviewPacket>(File.ReadAllText(path));
            if (packet == null || packet.schema != "cmer-gh-review/1" || (packet.scenario != "A" && packet.scenario != "B") ||
                string.IsNullOrEmpty(packet.binaryFile) || Path.GetFileName(packet.binaryFile) != packet.binaryFile)
                throw new InvalidDataException("Unsupported review manifest. Prepare the export with Tools/prepare_cmer_gh.py.");
            string binary = Path.Combine(Path.GetDirectoryName(path), packet.binaryFile);
            if (!File.Exists(binary) || !string.Equals(StudyRecorder.FileHash(binary), packet.binarySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Review binary hash does not match the manifest.");
            string folder = "Assets/ArchSemantic/Editor/GHReview/Import_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            if (Directory.Exists(folder)) throw new IOException("Review folder already exists; retry to create a new copy.");
            var model = ModelImporter.Import(packet.scenario, binary, folder);
            File.Copy(path, folder + "/ReviewManifest.json", false);
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(model.prefab);
            Debug.Log("Imported GH geometry for review only: " + folder + ". Runtime model, hand gestures and study rules were not replaced.");
        }
    }
}
