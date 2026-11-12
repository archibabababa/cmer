using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArchSemanticPilot;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArchSemanticPilotEditor
{
    public static class ModelImporter
    {
        public const string Root = "Assets/ArchSemantic";
        public const string Generated = Root + "/Editor/ReferenceModels";
        public const string Materials = Root + "/Materials";
        public static ModelDefinition Import(string scenario, string sourcePath = null, string generatedRoot = null)
        {
            // Optional paths are used only for isolated GH review imports. Existing callers
            // retain their original source and destination, including hand-edited materials.
            string folder = (generatedRoot ?? Generated) + "/" + scenario;
            string materialFolder = generatedRoot == null ? Materials : generatedRoot + "/Materials";
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(materialFolder);
            AssetDatabase.Refresh();
            string definitionPath = folder + "/Model.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ModelDefinition>(definitionPath);
            if (existing != null && existing.prefab != null)
                return existing;
            var definition = ScriptableObject.CreateInstance<ModelDefinition>();
            GameObject root = null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(sourcePath ?? Root + "/Editor/SourceData/Model_" + scenario + ".bytes")))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "ASB1")
                        throw new InvalidDataException("Unknown model format");
                    definition.scenario = String(r);
                    if (definition.scenario != scenario)
                        throw new InvalidDataException("Scenario mismatch");
                    definition.sourceGhSha256 = String(r);
                    definition.modelSha256 = String(r);
                    int materialCount = Count(r, 64);
                    var materials = new Material[materialCount];
                    for (int i = 0; i < materialCount; i++)
                    {
                        string name = String(r);
                        Vector3 rgb = Vector(r);
                        float alpha = r.ReadSingle();
                        string path = materialFolder + "/" + name + ".mat";
                        materials[i] = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (materials[i] == null)
                        {
                            Shader shader = Shader.Find("Standard");
                            if (shader == null)
                                throw new InvalidOperationException("Built-in Standard shader missing");
                            var mat = new Material(shader)
                            {
                                name = name,
                                color = new Color(rgb.x, rgb.y, rgb.z, alpha)
                            };
                            mat.SetFloat("_Glossiness", name == "Glass" ? .65f : .15f);
                            if (alpha < 1)
                            {
                                mat.SetFloat("_Mode", 3);
                                mat.SetOverrideTag("RenderType", "Transparent");
                                mat.SetInt("_SrcBlend", (int)BlendMode.One);
                                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                                mat.SetInt("_ZWrite", 0);
                                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                                mat.renderQueue = 3000;
                            }

                            AssetDatabase.CreateAsset(mat, path);
                            materials[i] = mat;
                        }
                    }

                    int starts = Count(r, 32);
                    if (starts != 3)
                        throw new InvalidDataException("Expected three room starts");
                    definition.starts = new Vector3[starts];
                    definition.viewDirections = new Vector3[starts];
                    for (int i = 0; i < starts; i++)
                    {
                        definition.starts[i] = Vector(r);
                        definition.viewDirections[i] = Vector(r);
                    }

                    int anchors = Count(r, 64);
                    definition.anchors = new ModelAnchor[anchors];
                    for (int i = 0; i < anchors; i++)
                        definition.anchors[i] = new ModelAnchor
                        {
                            id = String(r),
                            position = Vector(r)
                        };
                    int count = Count(r, 10000);
                    definition.objectCount = count;
                    definition.objects = new ModelObjectMetadata[count];
                    root = new GameObject("Model_" + scenario);
                    var owners = new Dictionary<string, Transform>();
                    var names = new HashSet<string>();
                    var meshes = new List<Mesh>();
                    for (int i = 0; i < count; i++)
                    {
                        string id = String(r), owner = String(r), category = String(r), role = String(r);
                        if (!names.Add(id))
                            throw new InvalidDataException("Duplicate object " + id);
                        int material = r.ReadInt32();
                        if (material < 0 || material >= materials.Length)
                            throw new InvalidDataException("Material index invalid");
                        int n = Count(r, 1000000);
                        var vertices = new Vector3[n];
                        var normals = new Vector3[n];
                        for (int j = 0; j < n; j++)
                            vertices[j] = Vector(r);
                        for (int j = 0; j < n; j++)
                            normals[j] = Vector(r);
                        int t = Count(r, 6000000);
                        if (t % 3 != 0)
                            throw new InvalidDataException("Triangle count invalid");
                        var triangles = new int[t];
                        for (int j = 0; j < t; j++)
                        {
                            triangles[j] = r.ReadInt32();
                            if (triangles[j] < 0 || triangles[j] >= n)
                                throw new InvalidDataException("Triangle index invalid");
                        }

                        definition.triangleCount += t / 3;
                        var mesh = new Mesh
                        {
                            name = id,
                            indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
                        };
                        mesh.vertices = vertices;
                        mesh.normals = normals;
                        mesh.triangles = triangles;
                        mesh.RecalculateBounds();
                        meshes.Add(mesh);
                        if (!owners.TryGetValue(owner, out Transform parent))
                        {
                            parent = new GameObject(owner).transform;
                            parent.SetParent(root.transform, false);
                            owners.Add(owner, parent);
                        }

                        var go = new GameObject(id);
                        go.transform.SetParent(parent, false);
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        go.AddComponent<MeshRenderer>().sharedMaterial = materials[material];
                        // Reference metadata is indexed by the stable object name; no runtime component.
                        definition.objects[i] = new ModelObjectMetadata
                        {
                            semanticId = id,
                            owner = owner,
                            category = category,
                            role = role
                        };
                    }

                    if (r.BaseStream.Position != r.BaseStream.Length)
                        throw new InvalidDataException("Unexpected trailing model data");
                    if (meshes.Count == 0)
                        throw new InvalidDataException("Empty model");
                    string meshPath = folder + "/Meshes.asset";
                    // These files are importer-owned; ordinary scene/material edits are preserved.
                    AssetDatabase.DeleteAsset(meshPath);
                    AssetDatabase.CreateAsset(meshes[0], meshPath);
                    for (int i = 1; i < meshes.Count; i++)
                        AssetDatabase.AddObjectToAsset(meshes[i], meshPath);
                    AssetDatabase.SaveAssets();
                    definition.prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Model.prefab");
                    if (existing != null)
                    {
                        EditorUtility.CopySerialized(definition, existing);
                        UnityEngine.Object.DestroyImmediate(definition);
                        definition = existing;
                    }
                    else
                        AssetDatabase.CreateAsset(definition, definitionPath);
                    EditorUtility.SetDirty(definition);
                    AssetDatabase.SaveAssets();
                    Debug.Log("Imported model " + scenario + ": " + count + " objects, " + definition.triangleCount + " triangles.");
                    return definition;
                }
            }
            finally
            {
                if (root != null)
                    UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static int Count(BinaryReader r, int maximum)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > maximum)
                throw new InvalidDataException("Invalid source count");
            return n;
        }

        private static string String(BinaryReader r)
        {
            int n = Count(r, 8192);
            byte[] bytes = r.ReadBytes(n);
            if (bytes.Length != n)
                throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static Vector3 Vector(BinaryReader r)
        {
            var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z))
                throw new InvalidDataException("Non-finite coordinate");
            return v;
        }
    }
}
