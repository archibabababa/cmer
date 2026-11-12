using UnityEngine;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Semantic gesture zone configuration in the scene.
    /// The interactor selects a zone by parameter axis and maps hand displacement to parameter changes using the calibrated travel distance.
    /// </summary>
    public sealed class SemanticGestureZone : MonoBehaviour
    {
        /// <summary>Associated semantic axis, such as P1, P2 or P3.</summary>
        public SemanticAxis axis;

        /// <summary>Semantic name or description used for scene configuration and identification.</summary>
        public string semantic;

        /// <summary>
        /// Hand travel in metres corresponding to the full parameter range from -1 to +1.
        /// P1/P2 use palm-separation changes; P3 uses dominant-hand displacement along the route.
        /// </summary>
        [Min(0.1f)]
        public float travelMeters = 0.45f;
    }
}
