using UnityEngine;

namespace ArchSemanticPilot
{
    public sealed class SemanticGestureZone : MonoBehaviour
    {
        public SemanticAxis axis;
        public string semantic;
        [Min(0.1f)]
        public float travelMeters = 0.45f;
    }
}
