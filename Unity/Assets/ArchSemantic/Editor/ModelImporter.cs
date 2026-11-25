using System;
using System.IO;
using System.Linq;
using ArchSemanticPilot;
using UnityEditor;
using UnityEngine;

namespace ArchSemanticPilotEditor
{
    public static class ModelImporter
    {
        public const string Root = "Assets/ArchSemantic";
        public const string Generated = Root + "/Editor/ReferenceModels";
        public const string Materials = Root + "/Materials";
        public static ModelDefinition Import(string scenario)
        {
            int index = scenario == "A" ? 0 : scenario == "B" ? 1 : throw new ArgumentException("Expected A or B.");
            LiveGeometryKernel.LoadResources();
            var geometry = LiveGeometryKernel.Evaluate(index, 0, 0, 0);
            string folder = Generated + "/" + scenario;
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            string path = folder + "/Model.asset";
            var model = AssetDatabase.LoadAssetAtPath<ModelDefinition>(path);
            if (model == null) { model = ScriptableObject.CreateInstance<ModelDefinition>(); AssetDatabase.CreateAsset(model, path); }
            model.scenario = scenario; model.sourceGhSha256 = LiveGeometryKernel.SourceGhSha256;
            model.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPreparation.RuntimeRoot + "/" + scenario + "/Model.prefab");
            model.starts = geometry.starts.Select(LiveGeometryDriver.Point).ToArray();
            model.viewDirections = geometry.views.Select(v => LiveGeometryDriver.Point(v.PointAtStart) - LiveGeometryDriver.Point(v.PointAtEnd)).ToArray();
            model.anchors = geometry.anchors.Select((p,i) => new ModelAnchor {id=geometry.anchorNames[i],position=LiveGeometryDriver.Point(p)}).ToArray();
            model.objects = geometry.names.Select(n => {var t=n.Split(new[]{"__"},StringSplitOptions.None);return new ModelObjectMetadata {semanticId=n,category=t[1],owner=t[2],role=t[3]};}).ToArray();
            var metrics = GeometryValidation.Measure(geometry);
            model.objectCount = metrics.objects; model.triangleCount = metrics.triangles; model.modelSha256 = metrics.geometryHash;
            EditorUtility.SetDirty(model); AssetDatabase.SaveAssets(); return model;
        }
    }
}
