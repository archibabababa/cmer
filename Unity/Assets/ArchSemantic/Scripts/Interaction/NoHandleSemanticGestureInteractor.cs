using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace ArchSemanticPilot
{
    public sealed class NoHandleSemanticGestureInteractor : MonoBehaviour
    {
        public Transform trackingOrigin;
        public Transform headPoseSource;
        public PilotSession engine;
        public SemanticGestureZone[] zones;
        [Min(.1f)]
        public float subsystemRetrySeconds = 1f;
        [Range(.2f, .8f)]
        public float activationDwellSeconds = .35f;
        [Range(.1f, .5f)]
        public float releaseDwellSeconds = .20f;
        public string trackingStatus = "Hands: awaiting tracking";

        // Current XR hand-tracking subsystem and retry timing.
        private XRHandSubsystem subsystem;
        private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        private float nextSubsystemSearchTime;   // Earliest subsystem retry time, avoiding enumeration every frame.

        // Per-hand tracking state, pinch hysteresis and stable pointing rays.
        private TrackedWrist leftPoseSource, rightPoseSource;
        public bool LeftTracked => leftPoseSource != null && leftPoseSource.IsTracked;
        public bool RightTracked => rightPoseSource != null && rightPoseSource.IsTracked;
        public Vector3 LeftPosition => LeftTracked ? leftPoseSource.Position : Vector3.zero;
        public Vector3 RightPosition => RightTracked ? rightPoseSource.Position : Vector3.zero;

        // Candidate gesture zone, active/suspended flags and pose dwell times.
        private SemanticGestureZone candidate;
        private bool active, suspended;
        private float dwellStarted, releaseStarted = -1f, resumeStarted = -1f;

        // Initial parameter value, palm separation, dominant-hand position and P3 route direction.
        private float baselineAxis, baselinePalmSeparation;
        private Vector3 baselineRightPoint, gestureDirection;
        private void Start()
        {
            if (trackingOrigin == null)
                trackingOrigin = transform;
            if (headPoseSource == null && Camera.main != null)
                headPoseSource = Camera.main.transform;
            if (engine == null)
                engine = FindFirstObjectByType<PilotSession>();
            if (zones == null || zones.Length == 0)
                zones = FindObjectsByType<SemanticGestureZone>(FindObjectsSortMode.None);
            leftPoseSource = new TrackedWrist();
            rightPoseSource = new TrackedWrist();
            FindRunningSubsystem();
        }
        private SemanticAxis SelectedAxis => engine.SelectedAxis;
        private static bool Tracked(SemanticAxis axis, HandFrame l, HandFrame r) => r.tracked && (axis == SemanticAxis.P3 || l.tracked);
        private static bool PoseReady(SemanticAxis axis, HandFrame l, HandFrame r) => Tracked(axis, l, r) && r.openPalm && (axis == SemanticAxis.P3 || l.openPalm);
        private static bool Released(SemanticAxis axis, HandFrame l, HandFrame r) => Tracked(axis, l, r) && !r.openPalm && (axis == SemanticAxis.P3 || !l.openPalm);
        private static string Hint(SemanticAxis axis) => axis == SemanticAxis.P1 ? "Reveal: spread both palms" : axis == SemanticAxis.P2 ? "P2: spread palms to increase; bring together to decrease" : "Connect: push dominant palm along route";
        private void Update()
        {
            if (engine == null || !engine.IsInitialized || !engine.UsesHandInput || headPoseSource == null)
                return;
            if (subsystem == null || !subsystem.running)
            {
                if (Time.unscaledTime >= nextSubsystemSearchTime)
                    FindRunningSubsystem();
                Suspend();
                trackingStatus = "Tracking unavailable | release and retry";
                return;
            }

            var physicalLeft = ReadHand(subsystem.leftHand, leftPoseSource);       //Read left-hand data.
            var physicalRight = ReadHand(subsystem.rightHand, rightPoseSource);   //Read right-hand data.
            var left = engine.RightHandDominant ? physicalLeft : physicalRight;   //Remap to the support-hand role according to handedness settings.
            var right = engine.RightHandDominant ? physicalRight : physicalLeft;
            ProcessHands(left,right);      //Process workspace pointing, active edits, commands and new editing gestures in priority order.
        }
        private void ProcessHands(HandFrame left,HandFrame right)
        {
            bool ui=engine.ProcessWorkspacePointer(right.hasAim?right.aimOrigin:right.wrist,
                right.hasAim?right.aimDirection:(right.indexTip-right.wrist).normalized,right.wrist,right.pinch,right.pointing,right.tracked);
            if(ui)
            {
                // Hovering over the workspace suspends an unfinished edit; only a deliberate release/click commits it.
                if(active){suspended=true;resumeStarted=-1f;releaseStarted=-1f;}
                else candidate=null;
                command="";commandLatched=true;commandCooldown=Time.unscaledTime+.25f;
                trackingStatus="UI | Pinch a highlighted button; pinch-drag the header to move";
                return;
            }
            if (active)
            {
                if (candidate == null || candidate.axis != SelectedAxis || !engine.CanEdit)
                {
                    CancelGesture();
                    return;
                }

                UpdateActive(left, right); //Update the active gesture: check tracking and release, compute previews or commit.
                return;
            }

            if (HandleCommands(left, right))
                return;
            if (!engine.CanEdit||engine.IsAxisLocked(SelectedAxis))
            {
                candidate = null;
                trackingStatus = engine.IsAxisLocked(SelectedAxis)?SelectedAxis+" LOCKED | unlock below its mode button":"Inspection / paused: editing unavailable";
                return;
            }

            trackingStatus = Hint(SelectedAxis);
            if (!PoseReady(SelectedAxis, left, right))
            {
                candidate = null;
                if (!Tracked(SelectedAxis, left, right))
                    trackingStatus = "Show " + (SelectedAxis == SemanticAxis.P3 ? "dominant hand" : "both hands");
                return;
            }

            SemanticGestureZone found = null;
            if (zones != null)
                foreach (var z in zones)
                    if (z != null && z.axis == SelectedAxis)
                    {
                        found = z;
                        break;
                    }

            if (found == null)
            {
                trackingStatus = "Missing gesture context";
                return;
            }

            if (candidate != found)
            {
                candidate = found;
                dwellStarted = Time.unscaledTime;
            }

            // Require a continuous activationDwellSeconds pose to prevent brief misrecognition from starting an edit.
            trackingStatus = "P" + (int)SelectedAxis + " | Hold open hand(s)...";
            if (Time.unscaledTime - dwellStarted < activationDwellSeconds)
                return;
            active = true;
            suspended = false;
            gestureDirection = Vector3.ProjectOnPlane(engine.geometry.transform.TransformDirection(-engine.geometry.liveDirections[(int)candidate.axis - 1]), Vector3.up).normalized;
            if (gestureDirection.sqrMagnitude < .01f)
                gestureDirection = Vector3.ProjectOnPlane(headPoseSource.forward, Vector3.up).normalized;
            CaptureBaseline(left, right);
            trackingStatus = "P" + (int)candidate.axis + " ACTIVE | Close hand(s) to save";
        }

        private string command = "";
        private float commandSince, commandCooldown;
        private Vector3 commandOrigin;
        private bool commandLatched;
        // The support hand selects command type; the dominant hand performs the action.
        // Trigger once per pose and require a neutral release before retriggering.
        private static string CommandPose(HandFrame support, HandFrame dominant)
        {
            if(!support.tracked||!dominant.tracked)
                return "";
            if(support.pinch&&dominant.pinch)
                return "scale";
            if(support.openPalm&&support.palmUp&&dominant.pinch)
                return "context";
            if(support.openPalm&&support.palmUp&&!dominant.openPalm&&!dominant.pinch&&!dominant.pointing)
                return "workspace";
            if(support.pinch&&dominant.openPalm)
                return "history";
            if(support.pinch&&!dominant.openPalm&&!dominant.pointing&&!dominant.pinch)
                return "reset";
            return dominant.pinch&&!support.openPalm?"point":"";
        }
        private bool HandleCommands(HandFrame support, HandFrame dominant)
        {
            if (!support.tracked || !dominant.tracked)
            {
                command = "";
                return false;
            }

            string next = CommandPose(support,dominant);
            if (next == "")
            {
                command = "";
                commandLatched = false;
                return Time.unscaledTime < commandCooldown;
            }

            candidate = null;
            if (commandLatched)
                return true;
            if (command != next)
            {
                command = next;
                commandSince = Time.unscaledTime;
                commandOrigin = dominant.wrist;
            }

            float elapsed = Time.unscaledTime - commandSince;
            trackingStatus = "Hold: " + next;
            bool fired = false;
            if (next == "history" && elapsed > .2f)
            {
                float x = Vector3.Dot(dominant.wrist - commandOrigin, headPoseSource.right);
                if (Mathf.Abs(x) > .16f)
                {
                    if (x < 0)
                        engine.Undo();
                    else
                        engine.Redo();
                    fired = true;
                }
            }
            else if (next == "scale" && elapsed > .8f)
            {
                engine.ToggleScale();
                fired = true;
            }
            else if (next == "workspace" && elapsed > .8f)
            {
                engine.SetWorkspaceVisible(true,true);
                fired = true;
            }
            else if (next == "context" && elapsed > .8f)
            {
                engine.ContextCommand();
                fired = true;
            }
            else if (next == "reset" && elapsed > 1.3f)
            {
                engine.ResetState();
                fired = true;
            }
            else if (next == "point" && elapsed > .25f)
            {
                engine.PointAt(dominant.hasAim?dominant.aimOrigin:dominant.wrist,
                    dominant.hasAim?dominant.aimDirection:(dominant.indexTip-dominant.wrist).normalized);
                fired = true;
            }

            if (fired)
            {
                commandLatched = true;
                commandCooldown = Time.unscaledTime + .5f;
            }

            return true;
        }
        public void CancelGesture()
        {
            active = false;
            suspended = false;
            candidate = null;
            releaseStarted = resumeStarted = -1;
            commandCooldown = Time.unscaledTime + .4f;
            command = "";
            commandLatched = true;
        }
        private void CaptureBaseline(HandFrame left, HandFrame right)
        {
            baselineAxis = AxisValue(candidate.axis);
            baselineRightPoint = right.wrist;
            baselinePalmSeparation = Vector3.Distance(left.palm, right.palm);
            releaseStarted = -1f;
            resumeStarted = -1f;
        }
        private void UpdateActive(HandFrame left, HandFrame right)
        {
            var axis = candidate.axis;
            if (!Tracked(axis, left, right))
            {
                Suspend();
                trackingStatus = "Tracking lost | unfinished edit cancelled";
                return;
            }

            if (Released(axis, left, right))
            {
                if (releaseStarted < 0)
                    releaseStarted = Time.unscaledTime;
                trackingStatus = "Release detected | Saving";
                if (Time.unscaledTime - releaseStarted >= releaseDwellSeconds)
                    EndGesture("HAND_RELEASE");
                return;
            }

            releaseStarted = -1f;
            if (!PoseReady(axis, left, right))
            {
                suspended = true;
                resumeStarted = -1f;
                trackingStatus = "Preview held | Open palms to resume";
                return;
            }

            if (suspended)
            {
                if (resumeStarted < 0)
                    resumeStarted = Time.unscaledTime;
                trackingStatus = "Preview held | Hold pose to resume";
                if (Time.unscaledTime - resumeStarted < activationDwellSeconds)
                    return;
                CaptureBaseline(left, right);
                suspended = false;
                // Recalibrate after occlusion or a brief pose interruption so unobserved hand movement does not change parameters.
            }

            float value = axis == SemanticAxis.P3 ? (float)GestureMapping.FromRouteTravel(baselineAxis, Vector3.Dot(right.wrist - baselineRightPoint, gestureDirection), candidate.travelMeters) : (float)GestureMapping.FromPalmSeparation(axis, baselineAxis, baselinePalmSeparation, Vector3.Distance(left.palm, right.palm), candidate.travelMeters);
            // Do not create zero-displacement previews for stationary open hands, which would unnecessarily block workspace selection.
            if(!engine.HasPreview && Mathf.Abs(value-AxisValue(axis))<.015f)return;
            engine.SetAxis(axis, value);
            trackingStatus = "P" + (int)axis + " ACTIVE | Close hand(s) to save";
        }
        public void EndGesture(string reason)
        {
            if (reason == "HAND_RELEASE" && active && candidate != null && engine != null)
                engine.Commit(candidate.axis);
            active = false;
            suspended = false;
            candidate = null;
            releaseStarted = -1f;
            resumeStarted = -1f;
            commandLatched = true;
            command = "";
            trackingStatus = "Released; validation pending | " + (engine != null ? Hint(SelectedAxis) : "");
        }
        private void Suspend()
        {
            // Command dwell must be continuous; tracking loss must not count toward the hold duration.
            command = "";
            commandSince = Time.unscaledTime;
            leftPoseSource?.ClearTracking();
            rightPoseSource?.ClearTracking();
            if (active)
                suspended = true;
            else
                candidate = null;
            releaseStarted = -1f;
            resumeStarted = -1f;
        }
        private void OnDisable()
        {
            CancelGesture();
            if (engine != null && engine.IsInitialized)
                engine.CancelPreview("hand_component_disabled");
        }
        private void OnApplicationPause(bool paused)
        {
            if (paused)
                Suspend();
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused && !((Application.isEditor || Application.platform == RuntimePlatform.WindowsPlayer) && engine != null && engine.XrRunning))
                Suspend();
        }
        private float AxisValue(SemanticAxis axis) => engine.AxisValue(axis);
        private HandFrame ReadHand(XRHand hand, TrackedWrist poseSource)
        {
            HandFrame result = new HandFrame();
            var aim = hand.handedness == Handedness.Left ? MetaAimHand.left : MetaAimHand.right;
            bool systemGesture = aim != null && (((MetaAimFlags)aim.aimFlags.ReadValue()) & MetaAimFlags.SystemGesture) != 0;
            if (systemGesture || !hand.isTracked || !TryWorldPose(hand, XRHandJointID.Wrist, out Pose wrist) || !TryWorldPose(hand, XRHandJointID.Palm, out Pose palm) || !TryWorldPose(hand, XRHandJointID.IndexTip, out Pose index) || !TryWorldPose(hand, XRHandJointID.ThumbTip, out Pose thumb) || !TryWorldPose(hand, XRHandJointID.MiddleTip, out Pose middle) || !TryWorldPose(hand, XRHandJointID.RingTip, out Pose ring) || !TryWorldPose(hand, XRHandJointID.LittleTip, out Pose little))
            {
                poseSource?.ClearTracking();
                return result;
            }

            result.tracked = true;
            result.wrist = wrist.position;
            result.palm = palm.position;
            result.indexTip = index.position;
            // Index direction is unstable during pinching, so prefer the runtime Aim pose.
            if(aim!=null&&aim.isTracked.isPressed&&(((MetaAimFlags)aim.aimFlags.ReadValue())&MetaAimFlags.Valid)!=0)
            {
                result.hasAim=true;
                result.aimOrigin=trackingOrigin.TransformPoint(aim.devicePosition.ReadValue());
                result.aimDirection=trackingOrigin.rotation*aim.deviceRotation.ReadValue()*Vector3.forward;
            }
            else
            {
                var usage=hand.handedness==Handedness.Left?UnityEngine.InputSystem.CommonUsages.LeftHand:UnityEngine.InputSystem.CommonUsages.RightHand;
                var device=InputSystem.GetDevice<HandInteractionProfile.HandInteraction>(usage);
                if(device!=null)
                {
                    var pose=device.pointer.ReadValue();
                    var required=InputTrackingState.Position|InputTrackingState.Rotation;
                    if(pose.isTracked&&(pose.trackingState&required)==required)
                    {
                        result.hasAim=true;
                        result.aimOrigin=trackingOrigin.TransformPoint(pose.position);
                        result.aimDirection=trackingOrigin.rotation*pose.rotation*Vector3.forward;
                    }
                }
            }
            result.pinch = poseSource != null && poseSource.UpdatePinch(Vector3.Distance(index.position, thumb.position));
            poseSource?.StabilizeAim(result.pinch,ref result.hasAim,ref result.aimOrigin,ref result.aimDirection);
            result.palmUp = Vector3.Dot(palm.rotation * Vector3.down, Vector3.up) > .65f;
            float handScale = Mathf.Max(.10f, Vector3.Distance(wrist.position, palm.position) * 2.5f);
            result.openPalm = Vector3.Distance(palm.position, index.position) > handScale * .55f && Vector3.Distance(palm.position, middle.position) > handScale * .55f && Vector3.Distance(palm.position, ring.position) > handScale * .48f && Vector3.Distance(palm.position, little.position) > handScale * .42f;
            result.pointing = Vector3.Distance(palm.position, index.position) > handScale * .58f && Vector3.Distance(palm.position, middle.position) < handScale * .62f && Vector3.Distance(palm.position, ring.position) < handScale * .58f && Vector3.Distance(palm.position, little.position) < handScale * .55f;
            Pose localWrist = new Pose(trackingOrigin.InverseTransformPoint(wrist.position), Quaternion.Inverse(trackingOrigin.rotation) * wrist.rotation);
            poseSource?.SetLocalPose(localWrist, trackingOrigin);
            return result;
        }
        private bool TryWorldPose(XRHand hand, XRHandJointID id, out Pose world)
        {
            world = default;
            if (!hand.GetJoint(id).TryGetPose(out Pose local) || trackingOrigin == null)
                return false;
            world.position = trackingOrigin.TransformPoint(local.position);
            world.rotation = trackingOrigin.rotation * local.rotation;
            return true;
        }
        private void FindRunningSubsystem()
        {
            nextSubsystemSearchTime = Time.unscaledTime + subsystemRetrySeconds;
            subsystems.Clear();
            SubsystemManager.GetSubsystems(subsystems);
            subsystem = null;
            foreach (XRHandSubsystem candidateSubsystem in subsystems)
            {
                if (candidateSubsystem != null && candidateSubsystem.running)
                {
                    subsystem = candidateSubsystem;
                    break;
                }
            }
        }
        private sealed class TrackedWrist
        {
            public bool IsTracked { get; private set; }
            public Vector3 Position { get; private set; }
            public void SetLocalPose(Pose pose, Transform origin)
            {
                IsTracked = origin != null;
                if (IsTracked)
                    Position = origin.TransformPoint(pose.position);
            }

            // Separate press/release distance thresholds reduce pinch jitter; stabilize the pointing ray during a pinch.
            private bool pinched;
            private bool hasPointingRay;
            private Vector3 pointingOrigin,pointingDirection;
            public void StabilizeAim(bool pressed,ref bool valid,ref Vector3 origin,ref Vector3 direction)
            {
                if(!pressed){hasPointingRay=valid;if(valid){pointingOrigin=origin;pointingDirection=direction;}}
                else if(hasPointingRay){valid=true;origin=pointingOrigin;direction=pointingDirection;}
            }
            public bool UpdatePinch(float distance)
            {
                pinched = !float.IsNaN(distance) && distance >= 0 && distance < (pinched ? .035f : .025f);
                return pinched;
            }
            public void ClearTracking(){IsTracked = false;pinched = false;hasPointingRay=false;}
        }
        private struct HandFrame
        {
            public bool tracked;
            public bool openPalm;
            public bool pointing, pinch, palmUp;
            public bool hasAim;
            public Vector3 aimOrigin,aimDirection;
            public Vector3 wrist;
            public Vector3 palm;
            public Vector3 indexTip;
        }
    }
}
