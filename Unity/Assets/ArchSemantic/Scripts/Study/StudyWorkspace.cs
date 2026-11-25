using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArchSemanticPilot
{
    public sealed class StudyWorkspace : MonoBehaviour
    {
        static readonly string[] Rooms = { "F", "R", "G", "C" };
        static readonly string[] Labels = { "FOYER", "LOUNGE", "GALLERY", "COURTYARD" };
        PilotSession session;
        Transform rig, controls, groupControls;
        readonly TextMesh[] groups = new TextMesh[2];
        Font font;
        Material ink, glass, accent;
        Mesh quad;
        TextMesh status, action;
        readonly TextMesh[] buttons = new TextMesh[4];
        readonly TextMesh[] modes = new TextMesh[3];
        readonly TextMesh[] locks = new TextMesh[3];
        TextMesh modeHint, hideLabel;
        readonly Transform[] screens = new Transform[2];
        readonly TextMesh[] captions = new TextMesh[2];
        readonly Camera[] cameras = new Camera[2];
        readonly RenderTexture[] textures = new RenderTexture[2];
        readonly List<Material> owned = new List<Material>();
        readonly Bounds[] frames = new Bounds[4];
        int scenario = -1, version = -1, axis = -1, scale = -1, hover = -1, condition = -1;
        bool cachedPractice;
        LineRenderer pointer;
        Vector3 placementOffset, dragStartHand, dragStartOffset;
        bool uiPinched, uiPress, dragging, uiArmed, hidePending;
        float hideStarted;
        int recentHover = -1;   
        float recentHoverAt;
        float nextRender;
        public bool Ready => rig != null;
        public bool RequestedVisible { get; private set; } = true;
        public bool IsVisible => Ready && rig.gameObject.activeSelf;
        public void Initialize(PilotSession owner, TextMesh text)
        {
            session = owner;
            font = Resources.Load<Font>("QuestUI/Inter-Regular");
            var shader = Resources.Load<Shader>("QuestUI/ParticipantOverlay");
            if (font == null || shader == null || text == null) throw new InvalidOperationException("Bundled workspace assets missing.");
            ink = Material(shader, "Workspace text", Color.white, 3512);
            ink.SetFloat("_FontAlphaOnly", 1);
            ink.mainTexture = font.material.mainTexture;
            glass = Material(shader, "Translucent workspace", new Color(.025f, .04f, .055f, .72f), 3510);
            accent = Material(shader, "Navigation backing", new Color(.13f, .24f, .30f, .86f), 3511);
            quad = new Mesh { name = "Workspace quad" };
            quad.vertices = new[] { new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(-.5f, -.5f, 0) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
            quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            quad.RecalculateBounds();
            rig = new GameObject("Participant side workspace").transform;
            rig.SetParent(transform, false);
            pointer = new GameObject("Visible hand pointer").AddComponent<LineRenderer>();
            pointer.transform.SetParent(rig, false); pointer.positionCount = 2; pointer.useWorldSpace = true;
            pointer.startWidth = pointer.endWidth = .0025f;
            pointer.sharedMaterial = Material(shader, "UI pointer", new Color(.45f, 1, .84f), 3513);
            pointer.enabled = false;
            controls = Panel("Parameters and destinations", new Vector3(-.88f, -.17f, 1.25f), -35);
            Quad(controls, "Glass", new Vector3(0, -.035f, 0), new Vector2(.60f, .86f), glass);
            groupControls = new GameObject("Preview group selection").transform;
            groupControls.SetParent(controls, false);
            for (int i = 0; i < 2; i++)
            {
                var center = new Vector3(i == 0 ? -.145f : .145f, .445f, -.004f);
                Quad(groupControls, "Group " + i, center, new Vector2(.27f, .065f), accent);
                groups[i] = Text(groupControls, "Group label " + i, center + new Vector3(-.125f, .022f, -.006f), .0054f);
            }
            status = text;
            ConfigureText(status, controls, new Vector3(-.28f, .365f, -.008f), .0066f);
            for (int i = 0; i < 3; i++)
            {
                var center = new Vector3((i - 1) * .195f, .13f, -.004f);
                Quad(controls, "P" + (i + 1) + " mode", center, new Vector2(.18f, .06f), accent);
                modes[i] = Text(controls, "Mode " + i, center + new Vector3(-.07f, .021f, -.006f), .0068f);
                var lockCenter = new Vector3(center.x, .060f, -.004f);
                Quad(controls, "Parameter lock " + i, lockCenter, new Vector2(.18f, .05f), accent);
                locks[i] = Text(controls, "Lock label " + i, lockCenter + new Vector3(-.072f, .018f, -.006f), .0056f);
            }
            modeHint = Text(controls, "Selected relation and gesture", new Vector3(-.28f, .010f, -.008f), .0059f);
            action = Text(controls, "Navigation mode", new Vector3(-.28f, -.090f, -.008f), .0050f);
            for (int i = 0; i < 4; i++)
            {
                var center = DestinationCenter(i);
                Quad(controls, Labels[i] + " button", center, new Vector2(.27f, .065f), accent);
                buttons[i] = Text(controls, Labels[i], center + new Vector3(-.125f, .022f, -.006f), .0056f);
            }
            Quad(controls, "Hide UI button", new Vector3(0, -.365f, -.004f), new Vector2(.56f, .05f), accent);
            hideLabel = Text(controls, "Hide UI", new Vector3(-.26f, -.349f, -.010f), .0050f);
            hideLabel.text = "HIDE UI | hold pinch 0.6 s";
            Text(controls, "Point + pinch", new Vector3(-.28f, -.416f, -.008f), .0046f).text = "Pinch: select | Drag header: move";
            for (int i = 0; i < 2; i++)
            {
                screens[i] = Panel("Remote model " + i, new Vector3(.88f, .10f - i * .36f, 1.25f), 35);
                Quad(screens[i], "Frame", Vector3.zero, new Vector2(.55f, .33f), glass);
                captions[i] = Text(screens[i], "Model label", new Vector3(-.25f, .155f, -.008f), .0054f);
                textures[i] = new RenderTexture(384, 216, 16, RenderTextureFormat.ARGB32) { name = "Remote model " + i, antiAliasing = 1 };
                textures[i].Create();
                var screenMaterial = Material(shader, "Live model screen " + i, Color.white, 3511);
                screenMaterial.mainTexture = textures[i];
                Quad(screens[i], "Live image", new Vector3(0, -.018f, -.004f), new Vector2(.51f, .287f), screenMaterial); //Create a screen-sized quad using screenMaterial, positioned slightly below the centre.
                cameras[i] = new GameObject("Remote model camera " + i).AddComponent<Camera>();
                cameras[i].transform.SetParent(transform, false);
                cameras[i].enabled = false;
                cameras[i].stereoTargetEye = StereoTargetEyeMask.None;
                cameras[i].targetTexture = textures[i];
                cameras[i].cullingMask = 1 << 8;
                cameras[i].orthographic = true;
                cameras[i].clearFlags = CameraClearFlags.SolidColor;
                cameras[i].backgroundColor = new Color(.16f, .20f, .23f);
                cameras[i].allowHDR = cameras[i].allowMSAA = cameras[i].useOcclusionCulling = false;
            }
            Font.textureRebuilt += FontChanged;
            Recenter();
            Debug.Log("[CMER] Workspace ready: P1/P2/P3 selection, 4 destinations, fixed G/M groups; M interior views, G exterior views; callable side UI.");
        }
        Material Material(Shader shader, string name, Color color, int queue)
        {
            var m = new Material(shader) { name = name, color = color, renderQueue = queue }; owned.Add(m); return m;
        }
        Transform Panel(string name, Vector3 position, float yaw)
        {
            var t = new GameObject(name).transform; t.SetParent(rig, false); t.localPosition = position; t.localRotation = Quaternion.Euler(0, yaw, 0); return t;
        }
        void Quad(Transform parent, string name, Vector3 position, Vector2 size, Material material)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = new Vector3(size.x, size.y, 1);
            go.AddComponent<MeshFilter>().sharedMesh = quad; go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
        TextMesh Text(Transform parent, string name, Vector3 position, float size)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            ConfigureText(text, parent, position, size); return text;
        }
        void ConfigureText(TextMesh text, Transform parent, Vector3 position, float size)
        {
            text.transform.SetParent(parent, false); text.transform.localPosition = position; text.transform.localRotation = Quaternion.identity;
            text.font = font; text.fontSize = 48; text.characterSize = size; text.lineSpacing = 1.14f; text.anchor = TextAnchor.UpperLeft;
            text.color = new Color(.94f, .97f, 1, 1); text.GetComponent<Renderer>().sharedMaterial = ink;
        }
        void FontChanged(Font f) { if (f == font && ink != null) ink.mainTexture = font.material.mainTexture; }
        public void Recenter()
        {
            if (!Ready) return;
            rig.position = session.headCamera.transform.position + placementOffset;
            rig.rotation = Quaternion.Euler(0, session.headCamera.transform.eulerAngles.y, 0);
        }
        public void SetVisible(bool visible, bool recall = false)
        {
            RequestedVisible = visible;
            hover = -1;
            if (recall) placementOffset = Vector3.zero;
            if (visible) { Recenter(); rig.gameObject.SetActive(true); }
            else if (Ready) rig.gameObject.SetActive(false);
        }
        public bool ProcessPointer(Vector3 origin, Vector3 direction, Vector3 wrist, bool pinch, bool pointing, bool tracked)
        {
            SetPointer(origin, direction, tracked);
            if (!Ready || !IsVisible || !tracked || !session.Flow.CanToggleWorkspace)
            {
                if (pointer != null) pointer.enabled = false;
                uiPinched = pinch; uiPress = dragging = uiArmed = hidePending = false; recentHover = -1; return false;
            }
            if (!pinch) uiArmed = true;
            if (hover >= 0 && !pinch) { recentHover = hover; recentHoverAt = Time.unscaledTime; }
            bool onPanel = HitControls(origin, direction, out var local);    //Test the ray against the control panel and obtain the hit in panel-local coordinates.
            pointer.enabled = onPanel || pointing || pinch;
            pointer.SetPosition(0, origin);     //Ray start; pointer.enabled controls line rendering.
            pointer.SetPosition(1, onPanel ? controls.TransformPoint(local) : origin + direction.normalized * 1.6f);//Use the world-space panel hit as the endpoint, or extend the ray by 1.6 metres when it misses.
            if (pinch && !uiPinched && uiArmed)
            {
                uiArmed = false;
                int target = hover >= 0 ? hover : !onPanel && recentHover >= 0 && recentHover < 7 && Time.unscaledTime - recentHoverAt < .18f ? recentHover : -1;
                uiPress = target >= 0 || onPanel;
                if (target == 7) { hidePending = true; hideStarted = Time.unscaledTime; }
                else if (target >= 0) session.RequestWorkspaceAction(target);   //Pass the hit button index to the session to execute its action.
                else if (onPanel && local.y > .20f && session.CanToggleWorkspace)
                { dragging = true; dragStartHand = wrist; dragStartOffset = placementOffset; }
            }
            if (hidePending)
            {
                if (!pinch || hover != 7) hidePending = false;
                else if (Time.unscaledTime - hideStarted >= 0.6f) { hidePending = false; session.RequestWorkspaceAction(7); }
            }
            if (dragging && pinch) placementOffset = Vector3.ClampMagnitude(dragStartOffset + (wrist - dragStartHand) * 2, .75f);
            bool consumed = uiPress && (pinch || uiPinched) || (onPanel && pointing);
            if (!pinch) { uiPress = false; dragging = false; }
            uiPinched = pinch;
            return consumed;
        }
        public void SetPointer(Vector3 origin, Vector3 direction, bool tracked)
        {
            hover = -1;
            if (!Ready || !rig.gameObject.activeSelf || !tracked) return;
            if (HitControls(origin, direction, out var p))
            {
                hover = ButtonAt(p);      //Resolve the button index from panel-local coordinates.
            }           
        }
        bool HitControls(Vector3 origin, Vector3 direction, out Vector3 p) => Hit(controls, new Vector2(.60f, session.ResearcherPreview ? .955f : .86f), origin, direction, out p, session.ResearcherPreview ? .0125f : -.035f);
        static Vector3 DestinationCenter(int i) => new Vector3(i % 2 == 0 ? -.145f : .145f, -.19f - (i / 2) * .085f, -.004f);
        int ButtonAt(Vector3 p)
        {
            if (session.ResearcherPreview) for (int i = 0; i < 2; i++)
                    if (Mathf.Abs(p.x - (i == 0 ? -.145f : .145f)) <= .135f && Mathf.Abs(p.y - .445f) <= .0325f) return 11 + i;
            for (int i = 0; i < 4; i++)
            {
                var center = DestinationCenter(i);
                if (Mathf.Abs(p.x - center.x) <= .135f && Mathf.Abs(p.y - center.y) <= .0325f) return i;
            }
            for (int i = 0; i < 3; i++) if (Mathf.Abs(p.x - (i - 1) * .195f) <= .09f && Mathf.Abs(p.y - .13f) <= .03f) return 4 + i;
            for (int i = 0; i < 3; i++) if (Mathf.Abs(p.x - (i - 1) * .195f) <= .09f && Mathf.Abs(p.y - .060f) <= .025f) return 8 + i;
            if (Mathf.Abs(p.x) <= .28f && Mathf.Abs(p.y + .365f) <= .025f) return 7;
            return -1;
        }
        public bool TryActivate(Vector3 origin, Vector3 direction)
        {
            if (!Ready || !rig.gameObject.activeSelf) return false;
            if (HitControls(origin, direction, out var p))
            {
                int index = ButtonAt(p);    //Resolve the button index from panel-local coordinates.
                if (index >= 0 && index != 7) session.RequestWorkspaceAction(index);
                return true; // Never let a UI pinch teleport through the panel.
            }
            foreach (var screen in screens) if (Hit(screen, new Vector2(.55f, .33f), origin, direction, out _)) return true;
            return false;
        }
        static bool Hit(Transform surface, Vector2 size, Vector3 origin, Vector3 direction, out Vector3 point, float centerY = 0)
        {
            point = default;
            var plane = new Plane(surface.forward, surface.position);
            if (!plane.Raycast(new Ray(origin, direction), out float distance) || distance > 3) return false;
            point = surface.InverseTransformPoint(origin + direction.normalized * distance);
            return Mathf.Abs(point.x) <= size.x * .5f && Mathf.Abs(point.y - centerY) <= size.y * .5f;
        }
        void LateUpdate()
        {
            if (!Ready || !session.IsInitialized) return;
            bool freePhase = session.Flow.Phase == StudyPhase.Setup || session.Flow.Phase == StudyPhase.Practice || session.Flow.Phase == StudyPhase.Revision || session.Flow.Phase == StudyPhase.Recall;
            bool statusOnly = (session.Flow.Paused && (!RequestedVisible || !freePhase)) || session.Flow.Phase == StudyPhase.Questionnaire || session.Flow.Phase == StudyPhase.Washout || session.Flow.Phase == StudyPhase.Complete;
            bool visible = (session.XrRunning || Application.platform == RuntimePlatform.Android) &&
                (statusOnly || ((session.Flow.CanToggleWorkspace || session.Flow.Paused && freePhase) && RequestedVisible));
            rig.gameObject.SetActive(visible);
            if (!visible) return;
            // Translation follows the wearer; yaw stays put while they turn to read/click.
            rig.position = session.headCamera.transform.position + placementOffset;
            float turn = Mathf.Abs(Mathf.DeltaAngle(rig.eulerAngles.y, session.headCamera.transform.eulerAngles.y));
            bool reading = LookingAt(controls) || LookingAt(screens[0]) || LookingAt(screens[1]);
            if (turn > 15 && !reading && !session.HasPreview && hover < 0 && !uiPinched)
            {
                float delta = Mathf.DeltaAngle(rig.eulerAngles.y, session.headCamera.transform.eulerAngles.y);
                float target = session.headCamera.transform.eulerAngles.y - Mathf.Sign(delta) * 15;
                rig.rotation = Quaternion.Euler(0, Mathf.MoveTowardsAngle(rig.eulerAngles.y, target, 90 * Time.unscaledDeltaTime), 0);
            }
            status.text = session.HeadsetStatusText();
            // Procedure prompts must remain available even when free inspection UI is locked.
            foreach (Transform child in controls)
            {
                bool background = child.name == "Glass";
                child.gameObject.SetActive(!statusOnly || background || child == status.transform);
                if (background)
                {
                    child.localPosition = statusOnly ? new Vector3(0, .22f, 0) : new Vector3(0, session.ResearcherPreview ? .0125f : -.035f, 0);
                    child.localScale = new Vector3(.60f, statusOnly ? .35f : session.ResearcherPreview ? .955f : .86f, 1);
                }
            }
            groupControls.gameObject.SetActive(session.ResearcherPreview && !statusOnly);
            for (int i = 0; i < 2; i++)
            {
                groups[i].text = ((int)session.Flow.Condition == i ? "> " : "  ") + (i == 0 ? "G MODEL ONLY" : "M ENTER+EDIT");
                groups[i].color = (int)session.Flow.Condition == i || hover == 11 + i ? new Color(.45f, 1, .84f) : Color.white;
            }
            for (int i = 0; i < 3; i++)
            {
                bool selected = (int)session.SelectedAxis == i + 1;
                modes[i].text = (selected ? "> " : "  ") + "P" + (i + 1);
                modes[i].color = !session.CanEdit ? new Color(.5f, .55f, .59f) : selected || hover == i + 4 ? new Color(.45f, 1, .84f) : Color.white;
                bool locked = session.IsAxisLocked((SemanticAxis)(i + 1));
                locks[i].text = locked ? "UNLOCK" : "LOCK";
                locks[i].color = !session.CanEdit ? Color.gray : locked ? new Color(1, .79f, .4f) : hover == 8 + i ? new Color(.45f, 1, .84f) : Color.white;
            }
            modeHint.text = session.SelectedAxis == SemanticAxis.P1 ? "P1 ARRIVAL / REVEAL\nSpread / close BOTH palms" :
                session.SelectedAxis == SemanticAxis.P2 ? "P2 OPENNESS / SHELTER\nSpread / close BOTH palms" :
                "P3 ENCLOSURE / CONTINUITY\n" + RouteHint();
            if (session.IsAxisLocked(session.SelectedAxis)) modeHint.text = session.SelectedAxis + " LOCKED | value held\nUNLOCK below mode to edit";
            hideLabel.color = session.CanToggleWorkspace ? (hover == 7 ? new Color(.45f, 1, .84f) : Color.white) : new Color(.5f, .55f, .59f);
            action.text = session.Flow.Paused ? "PAUSED | " + session.Flow.PauseReason :
                session.WorkspaceFeedback ?? (session.CanNavigate ? (session.Flow.CanEnterBody ? "GO TO SPACE / 1:1" : "FOCUS SPACE / MODEL") : "NAVIGATION LOCKED");
            for (int i = 0; i < 4; i++)
            {
                buttons[i].text = Rooms[i] + "  " + Labels[i];
                buttons[i].color = !session.CanNavigate ? new Color(.5f, .55f, .59f) : hover == i ? new Color(.45f, 1, .84f) : Color.white;
            }
            // Hide supplemental information during matched guided walks and questionnaires.
            bool previews = !statusOnly && (session.Flow.CanNavigateSpaces || session.Flow.Paused && freePhase);
            foreach (var screen in screens) screen.gameObject.SetActive(previews);
            if (!previews || session.Flow.Paused || Time.unscaledTime < nextRender) return;
            int currentAxis = (int)session.SelectedAxis;
            if (version == session.geometry.displayedVersion && axis == currentAxis && scale == (int)session.Scale && scenario == session.Scenario && condition == (int)session.Flow.Condition) return;
            if (scenario != session.Scenario || version < 0 || cachedPractice != session.geometry.Current.practice) CacheFrames();
            int slot = 0;
            for (int room = 0; room < 3; room++)
                if (room != currentAxis - 1 && slot < 2)
                {
                    bool interior = session.Flow.Condition == StudyCondition.M;
                    captions[slot].text = Rooms[room] + " " + Labels[room] + (interior ? " | INSIDE / EYE LEVEL" : " | EXTERIOR MODEL");
                    RenderRoom(cameras[slot], frames[room], room, interior); slot++;
                }
            condition = (int)session.Flow.Condition;
            version = session.geometry.displayedVersion; axis = currentAxis; scale = (int)session.Scale; scenario = session.Scenario;
            cachedPractice = session.geometry.Current.practice;
            nextRender = Time.unscaledTime + .1f;
        }
        bool LookingAt(Transform panel)
        {
            var head = session.headCamera.transform;
            return Vector3.Dot(head.forward, (panel.position - head.position).normalized) > .94f;
        }
        void CacheFrames()
        {
            // Fixed framing per building prevents camera zoom from concealing size changes.
            for (int r = 0; r < 4; r++)
            {
                var room = Array.Find(session.geometry.Metrics.rooms, m => m.room == Rooms[r]);
                if (room == null) continue;
                var b = new Bounds(new Vector3((float)room.footprintXY[0], 0, (float)room.footprintXY[1]), Vector3.zero);
                for (int i = 0; i < room.footprintXY.Length; i += 2) b.Encapsulate(new Vector3((float)room.footprintXY[i], (float)room.height, (float)room.footprintXY[i + 1]));
                b.Expand(3); frames[r] = b;
            }
        }
        string RouteHint()
        {
            // Express the authored gallery axis relative to the current wearer, including remote edits from F/R.
            Vector3 axis = Vector3.ProjectOnPlane(session.geometry.transform.TransformDirection(-session.geometry.liveDirections[2]), Vector3.up).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(session.headCamera.transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float f = Vector3.Dot(axis, forward), r = Vector3.Dot(axis, right);
            if (Mathf.Abs(r) > Mathf.Abs(f)) return r > 0 ? "Palm RIGHT + / LEFT -" : "Palm LEFT + / RIGHT -";
            return f > 0 ? "Palm AWAY + / TOWARD -" : "Palm TOWARD + / AWAY -";
        }
        void RenderRoom(Camera camera, Bounds bounds, int room, bool interior)
        {
            Transform model = session.geometry.transform;
            float s = model.lossyScale.x;
            camera.orthographic = !interior;
            camera.aspect = 16f / 9f;
            if (interior)
            {
                Vector3 position = model.TransformPoint(session.geometry.liveStarts[room]);
                Vector3 forward = model.TransformDirection(Quaternion.Euler(0, room == 1 ? -65 : room == 2 ? 65 : 0, 0) * -session.geometry.liveDirections[room]);
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
                camera.fieldOfView = 72;
                camera.nearClipPlane = Mathf.Max(.001f, .05f * s);
                camera.farClipPlane = 150 * s;
                camera.Render();
                return;
            }
            var rotation = model.rotation * Quaternion.Euler(50, -25, 0);
            Vector3 target = model.TransformPoint(bounds.center);
            float radius = bounds.extents.magnitude;
            camera.transform.SetPositionAndRotation(target - rotation * Vector3.forward * radius * 3 * s, rotation);
            camera.orthographicSize = radius * .9f * s;
            camera.nearClipPlane = .01f * s; camera.farClipPlane = radius * 6 * s;
            camera.Render();
        }
        void OnDestroy()
        {
            Font.textureRebuilt -= FontChanged;
            foreach (var camera in cameras) if (camera != null) Destroy(camera.gameObject);
            foreach (var texture in textures) if (texture != null) { texture.Release(); Destroy(texture); }
            foreach (var material in owned) if (material != null) Destroy(material);
            if (quad != null) Destroy(quad);
            if (rig != null) Destroy(rig.gameObject);
        }
    }
}
