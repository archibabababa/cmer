using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands;

namespace ArchSemanticPilot
{
    // The class/GUID is retained to preserve existing A/B scenes. Hand motion is
    // provided by the official XR Hands skeleton driver, never by recorded poses.
    public sealed class HandSkeletonView : MonoBehaviour
    {
        public Transform trackingOrigin;
        [HideInInspector] public Material material; // legacy serialized field
        readonly GameObject[] hands = new GameObject[2];
        void OnEnable()
        {
            foreach (var hand in hands)
            {
                if (hand != null)
                {
                    hand.SetActive(true);
                }
            }
        }

        void Start()
        {
            if (trackingOrigin == null) { Debug.LogError("Hand tracking origin is missing.", this); enabled = false; return; }
            for (int i = 0; i < 2; i++)
            {
                string side = i == 0 ? "Left" : "Right";
                var prefab = Resources.Load<GameObject>("CMERHands/" + side + " Hand Tracking");
                if (prefab == null) { Debug.LogError("Official XR Hands prefab missing: " + side, this); continue; }
                hands[i] = Instantiate(prefab, trackingOrigin, false);
                hands[i].transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                hands[i].GetComponent<XRHandTrackingEvents>().updateType =
                    XRHandTrackingEvents.UpdateTypes.Dynamic | XRHandTrackingEvents.UpdateTypes.BeforeRender;
                foreach (var skin in hands[i].GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.shadowCastingMode = ShadowCastingMode.Off;
                    skin.receiveShadows = false;
                    skin.enabled = hands[i].GetComponent<XRHandMeshController>().handIsTracked;
                }
            }
        }


        void OnDisable() { foreach (var hand in hands) if (hand != null) hand.SetActive(false); }
        void OnDestroy() { foreach (var hand in hands) if (hand != null) Destroy(hand); }
    }
}
