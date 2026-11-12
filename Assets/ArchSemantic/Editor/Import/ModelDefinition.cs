using UnityEngine;

namespace ArchSemanticPilot
{
    [System.Serializable]
    public class ModelAnchor
    {
        public string id;
        public Vector3 position;
    }

    [System.Serializable]
    public sealed class ModelObjectMetadata
    {
        public string semanticId, owner, category, role;
    }

    public sealed class ModelDefinition : ScriptableObject
    {
        public string scenario, sourceGhSha256, modelSha256;
        public GameObject prefab;
        public Vector3[] starts, viewDirections;
        public ModelAnchor[] anchors;
        public int objectCount, triangleCount;
        public ModelObjectMetadata[] objects;
    }
}
