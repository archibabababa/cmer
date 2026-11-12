using ArchSemanticPilot;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Manage study navigation, floor following, model presentation and XR/desktop camera positioning.
    /// Desktop and XR share building geometry and floor checks but use different camera-placement rules.
    /// </summary>
    public sealed class StudyNavigation
    {
        // XR Origin parents the camera and tracking space; moving or rotating it repositions the user.
        readonly Transform origin;
        // User head camera, also supplying gaze direction and height.
        readonly Camera head;
        // Provider of live building geometry, floors, zones and spatial anchors.
        readonly LiveGeometryDriver geometry;
        // XR head pose driver, enabled only while XR is running.
        readonly TrackedPoseDriver trackedPose;
        // Recorded floor height and owning floor/zone.
        float floor = .06f;
        string floorOwner, rememberedFloorOwner;
        // Cached version and position reduce per-frame floor queries.
        int checkedFloorVersion = -1;
        Vector3 checkedFloorPosition;
        public string OccupiedFloor => floorOwner;
        // Remembered body-scale position for returning from model scale.
        bool hasRememberedBody;
        Vector3 rememberedPosition, rememberedDirection;

        /// <summary>Create the navigation controller and initialize desktop tracking mode.</summary>
        /// <param name="origin">XR Origin or desktop navigation root.</param>
        /// <param name="head">Head camera representing the user's view.</param>
        /// <param name="geometry">Live architectural geometry driver for the scene.</param>
        public StudyNavigation(Transform origin, Camera head, LiveGeometryDriver geometry)
        {
            this.origin = origin;
            this.head = head;
            this.geometry = geometry;
            trackedPose = head.GetComponent<TrackedPoseDriver>();
            SetTrackingMode(false);
        }

        /// <summary>Enable pose tracking and configure basic camera display settings according to XR state.</summary>
        /// <param name="xrRunning">True uses a full-viewport XR camera; false disables head pose tracking.</param>
        public void SetTrackingMode(bool xrRunning)
        {
            if (trackedPose != null)
                trackedPose.enabled = xrRunning;
            if (xrRunning)
            {
                origin.rotation = Quaternion.Euler(0, origin.eulerAngles.y, 0);
                head.rect = new Rect(0, 0, 1, 1);
                head.transform.localRotation = Quaternion.identity;
                head.nearClipPlane = .05f;
            }

            head.backgroundColor = new Color(.115f, .14f, .155f);
            head.clearFlags = CameraClearFlags.SolidColor;
        }

        /// <summary>Set the viewport, reserving desktop researcher UI space while using the full viewport in XR.</summary>
        /// <param name="setup">Whether Setup is active, selecting the desktop viewport layout.</param>
        /// <param name="xrRunning">Whether XR is running.</param>
        public void SetViewport(bool setup, bool xrRunning)
        {
            head.rect = xrRunning ? new Rect(0, 0, 1, 1) : StudyDesktopView.CameraViewport(setup);
        }

        /// <summary>Clear the remembered body position so it will no longer be restored.</summary>
        public void ForgetBodyPosition() => hasRememberedBody = false;

        /// <summary>Remember head position, direction and floor for restoring body scale.</summary>
        public void RememberBodyPosition()
        {
            hasRememberedBody = true;
            rememberedPosition = head.transform.position;
            rememberedDirection = head.transform.forward;
            rememberedFloorOwner = floorOwner;
        }

        /// <summary>Display the building at 1:1 and synchronize physics transforms for current collision queries.</summary>
        /// <param name="xrRunning">XR state determining the viewport and desktop camera settings.</param>
        public void ShowBody(bool xrRunning)
        {
            geometry.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            geometry.transform.localScale = Vector3.one;
            SetViewport(false, xrRunning);
            if (!xrRunning)
            {
                head.fieldOfView = 60;
                head.nearClipPlane = .05f;
            }

            Physics.SyncTransforms();
        }

        /// <summary>Restore the remembered body position only after validating floor support and standing clearance.</summary>
        /// <param name="xrRunning">XR state determining the repositioning method.</param>
        /// <param name="inputs">XR input subsystems used to align the tracked floor after teleportation.</param>
        /// <returns>True when the position is valid and restored successfully; otherwise false.</returns>
        public bool TryRestoreBodyPosition(bool xrRunning, List<XRInputSubsystem> inputs)
        {
            if (!hasRememberedBody || !FloorNavigation.Supported(geometry.Current.source, rememberedPosition.x, rememberedPosition.z,
                rememberedFloorOwner, out double height)) return false;
            Vector3 feet = new Vector3(rememberedPosition.x, (float)height, rememberedPosition.z);
            if (!hasRememberedBody || !CanStandAt(feet))
                return false;
            floor = feet.y;
            Warp(rememberedPosition, rememberedDirection, xrRunning, inputs);
            return true;
        }

        /// <summary>Compute local building bounds from every current geometry vertex.</summary>
        /// <returns>Local bounding box containing all current vertices.</returns>
        Bounds ModelBounds()
        {
            Bounds bounds = default;
            bool first = true;
            foreach (var group in geometry.Current.groups)
                foreach (Vector3 p in group.vertices)
                {
                    if (first)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        first = false;
                    }
                    else
                        bounds.Encapsulate(p);
                }

            return bounds;
        }

        /// <summary>Display the model at the specified scale and position it for XR or desktop use.</summary>
        /// <param name="scale">Model scale relative to original building dimensions.</param>
        /// <param name="xrRunning">XR places the model ahead of the user; desktop fits it to the viewport.</param>
        /// <param name="setup">Whether Setup is active, determining the usable desktop viewport.</param>
        public void ShowModel(float scale, bool xrRunning, bool setup)
        {
            var bounds = ModelBounds();
            geometry.transform.rotation = Quaternion.identity;
            geometry.transform.localScale = Vector3.one * scale;
            SetViewport(setup, xrRunning);
            if (xrRunning)
            {

                Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
                geometry.transform.position = head.transform.position + forward * .85f + Vector3.down * .38f - bounds.center * scale;
                return;
            }

            geometry.transform.position = Vector3.zero;
            var view = Quaternion.Euler(28, -24, 0);
            var inverse = Quaternion.Inverse(view);
            var corners = new double[24];
            int index = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 point = inverse * Vector3.Scale(bounds.extents, new Vector3(x, y, z)) * scale;
                        corners[index++] = point.x;
                        corners[index++] = point.y;
                        corners[index++] = point.z;
                    }

            head.fieldOfView = 48;
            head.nearClipPlane = .005f;
            float aspect = Mathf.Max(.1f, Screen.width * head.rect.width / Mathf.Max(1, Screen.height * head.rect.height));
            float distance = (float)ModelFraming.Distance(head.fieldOfView, aspect, corners);
            origin.rotation = view;
            head.transform.localRotation = Quaternion.identity;
            Vector3 target = bounds.center * scale - view * Vector3.forward * distance;
            origin.position += target - head.transform.position;
        }

        /// <summary>Translate the chosen room to the view target without changing model scale or entering 1:1.</summary>
        /// <param name="room">Room identifier to focus, such as F, R or G.</param>
        /// <param name="xrRunning">XR state determining whether to focus ahead of the user or at the model centre.</param>
        public void FocusModelSpace(string room, bool xrRunning)
        {
            var region = Array.Find(geometry.Metrics.rooms, r => r.room == room);
            if (region == null) return;
            Vector3 center = Vector3.zero;
            for (int i = 0; i < region.footprintXY.Length; i += 2) center += new Vector3((float)region.footprintXY[i], 0, (float)region.footprintXY[i + 1]);
            center /= region.footprintXY.Length / 2;
            Vector3 target;
            if (xrRunning)
            {
                var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
                target = head.transform.position + forward * .85f + Vector3.down * .38f;
            }
            else target = geometry.transform.TransformPoint(ModelBounds().center);
            geometry.transform.position += target - geometry.transform.TransformPoint(center);
            Physics.SyncTransforms();
        }

        /// <summary>Search the courtyard for supported standing positions and teleport to the first valid candidate.</summary>
        /// <param name="xrRunning">XR state determining the teleport transform.</param>
        /// <param name="inputs">XR input subsystems used to align the tracked floor after teleportation.</param>
        /// <returns>True when a valid candidate is found and teleportation succeeds.</returns>
        public bool GoToCourtyard(bool xrRunning, List<XRInputSubsystem> inputs)
        {
            var room = Array.Find(geometry.Metrics.rooms, r => r.room == "C");
            if (room == null) return false;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < room.footprintXY.Length; i += 2)
            {
                float x = (float)room.footprintXY[i], z = (float)room.footprintXY[i + 1];
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
            }
            Physics.SyncTransforms();
            // Search broadly within the source courtyard contour; revisions may obstruct the centre.
            // Every candidate must still pass finished-floor and actual standing-clearance checks.
            foreach (float u in new[] { .5f, .4f, .6f, .3f, .7f, .2f, .8f, .1f, .9f }) foreach (float v in new[] { .5f, .4f, .6f, .3f, .7f, .2f, .8f, .1f, .9f })
                {
                    var p = new Vector3(Mathf.Lerp(minX, maxX, u), geometry.groundFloorHeight, Mathf.Lerp(minZ, maxZ, v));
                    if (!room.Contains(p.x, p.z) || geometry.Metrics.Zone(p.x, p.z) != "C") continue;
                    if (!FloorNavigation.Find(geometry.Current.source, p.x, p.z, geometry.groundFloorHeight + .12, "C", out _, out double top)) continue;
                    p.y = (float)top;
                    if (TryTeleport(p, xrRunning, inputs)) return true;
                }
            return false;
        }

        /// <summary>Navigate to the preset start of a main space.</summary>
        /// <param name="index">Space index: 0, 1 and 2 correspond to F, R and G.</param>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <param name="inputs">XR input subsystems used to align the tracked floor after teleportation.</param>
        public void GoToSpace(int index, bool xrRunning, List<XRInputSubsystem> inputs)
        {
            floor = geometry.groundFloorHeight;
            floorOwner = new[] { "F", "R", "G" }[index];
            Warp(geometry.liveStarts[index], -geometry.liveDirections[index], xrRunning, inputs);
        }

        /// <summary>Move the user to a walkthrough stop or semantic anchor in the selected space.</summary>
        /// <param name="room">Space index: 0, 1 and 2 correspond to F, R and G.</param>
        /// <param name="atSemanticAnchor">Use the semantic anchor when true; otherwise use the preset start.</param>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <param name="inputs">XR input subsystems used to align the tracked floor after teleportation.</param>
        public void WalkthroughStop(int room, bool atSemanticAnchor, bool xrRunning, List<XRInputSubsystem> inputs)
        {
            floor = geometry.groundFloorHeight;
            floorOwner = new[] { "F", "R", "G" }[room];
            Warp(atSemanticAnchor ? geometry.liveZonePositions[room] : geometry.liveStarts[room], -geometry.liveDirections[room], xrRunning, inputs);
        }

        bool CanStandAt(Vector3 feet)
        {
            if (!FloorNavigation.Find(geometry.Current.source, feet.x, feet.z, feet.y + .12, null, out string owner, out double height) ||
                Math.Abs(height - feet.y) > .12 || !FloorNavigation.Supported(geometry.Current.source, feet.x, feet.z, owner, out _)) return false;
            float standingHeight = Mathf.Max(1.8f, head.transform.position.y - floor + .12f);
            if (Physics.CheckCapsule(feet + Vector3.up * .30f, feet + Vector3.up * (standingHeight - .25f), .25f, 1 << 8)) return false;
            floorOwner = owner;
            return true;
        }

        /// <summary>Map a model pointer hit to the supported finished floor below and remember the body-scale destination.</summary>
        /// <param name="modelPoint">Pointer hit in building world coordinates.</param>
        /// <returns>True when a supported finished floor is found and the destination is cached.</returns>
        public bool SelectBodyDestination(Vector3 modelPoint)
        {
            if (!FloorNavigation.Find(geometry.Current.source, modelPoint.x, modelPoint.z, modelPoint.y + .12, null, out string owner, out double height) ||
                !FloorNavigation.Supported(geometry.Current.source, modelPoint.x, modelPoint.z, owner, out _)) return false;
            rememberedPosition = new Vector3(modelPoint.x, (float)height + 1.65f, modelPoint.z);
            rememberedDirection = head.transform.forward;
            rememberedFloorOwner = owner;
            hasRememberedBody = true;
            return true;
        }

        /// <summary>Update the occupied floor from the head's horizontal position and align the navigation root vertically.</summary>
        /// <param name="preview">Preview may continue without a floor; formal operation returns failure in that case.</param>
        /// <returns>True when a valid floor exists or the current preview permits continuing.</returns>
        public bool FollowFloor(bool preview)
        {
            var position = head.transform.position;
            Vector3 horizontalChange = position - checkedFloorPosition;
            horizontalChange.y = 0;
            if (checkedFloorVersion == geometry.displayedVersion && horizontalChange.sqrMagnitude < .0025f) return true;
            checkedFloorVersion = geometry.displayedVersion;
            checkedFloorPosition = position;
            // Ground-level movement may cross F/R/G/C; upper-level movement must remain on its named finished floor,
            // preventing accidental drops when the spatial contour shrinks.
            string required = floorOwner != null && floorOwner.StartsWith("U_", StringComparison.Ordinal) ? floorOwner : null;
            double ceiling = required == null ? geometry.groundFloorHeight + .12 : 100;
            if (!FloorNavigation.Find(geometry.Current.source, position.x, position.z, ceiling, required, out string owner, out double height) ||
                !FloorNavigation.Supported(geometry.Current.source, position.x, position.z, owner, out _)) return preview;
            origin.position += Vector3.up * ((float)height - floor);
            floor = (float)height;
            floorOwner = owner;
            return true;
        }
        /// <summary>Teleport after checking standing support and clearance.</summary>
        /// <param name="feet">Target foot position in building world coordinates.</param>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <param name="inputs">XR input subsystems used to align the tracked floor after teleportation.</param>
        /// <returns>True when the destination is valid and teleportation completes.</returns>
        public bool TryTeleport(Vector3 feet, bool xrRunning, List<XRInputSubsystem> inputs)
        {
            if (!CanStandAt(feet))
                return false;
            floor = feet.y;
            Warp(feet, head.transform.forward, xrRunning, inputs);
            return true;
        }

        /// <summary>Move the navigation root to place the user at the target facing the specified direction.</summary>
        /// <param name="target">Teleport or navigation destination.</param>
        /// <param name="direction">Desired facing direction on arrival.</param>
        /// <param name="xrRunning">XR preserves local tracked head pose; desktop sets camera direction directly.</param>
        /// <param name="inputs">XR input subsystems used for floor calibration.</param>
        void Warp(Vector3 target, Vector3 direction, bool xrRunning, List<XRInputSubsystem> inputs)
        {
            if (xrRunning)
            {
                // Discrete navigation targets also specify a viewing direction. Rotate Origin around the current eye position,
                // preserving local head tracking and floor height; otherwise walkthrough stops retain the Setup direction,
                // which may face a wall behind the intended space.
                Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
                Vector3 facing = Vector3.ProjectOnPlane(direction, Vector3.up);
                if (forward.sqrMagnitude > .001f && facing.sqrMagnitude > .001f)
                    origin.RotateAround(head.transform.position, Vector3.up, Vector3.SignedAngle(forward, facing, Vector3.up));
                Vector3 shift = target - head.transform.position;
                shift.y = 0;
                origin.position += shift;
                AlignFloor(inputs);
                return;
            }

            direction.y = 0;
            if (direction.sqrMagnitude > .001f)
                origin.rotation = Quaternion.LookRotation(direction);
            head.transform.localRotation = Quaternion.identity;
            // Reposition after rotating desktop views because the camera offset rotates with Origin;
            // otherwise returning from model view shifts the foot position.
            Vector3 desiredHead = new Vector3(target.x, floor + 1.65f, target.z);
            origin.position += desiredHead - head.transform.position;
        }

        bool warnedFloorUnavailable;
        /// <summary>Attempt to enable floor tracking, or calibrate Origin height if floor tracking is already active.</summary>
        /// <param name="inputs">XR input subsystems to inspect.</param>
        public void AlignFloor(List<XRInputSubsystem> inputs)
        {
            foreach (var input in inputs)
            {
                if (!input.running) continue;
                var mode = input.GetTrackingOriginMode();
                if ((mode & TrackingOriginModeFlags.Floor) != 0)
                { AlignTrackedFloor(); warnedFloorUnavailable = false; return; }
                if ((input.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0)
                { input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor); return; }
                if (!warnedFloorUnavailable && mode != 0)
                {
                    warnedFloorUnavailable = true;
                    Debug.LogWarning("[CMER] Floor tracking unavailable; keeping the existing vertical origin instead of adding an assumed user height.");
                }
            }

        }

        void AlignTrackedFloor()
        {
            origin.position += Vector3.up * (floor - head.transform.parent.position.y);
        }

    }
}
