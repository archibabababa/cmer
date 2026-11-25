using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace ArchSemanticPilot
{
    public sealed class StudyDesktopView : IDisposable
    {
        struct Layout
        {
            public float scale, width, height, padding, top, bottom, left;
            public Rect view;
            public static Layout For(bool setup)
            {
                var l = new Layout();
                l.scale = Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f), .75f, 2f);
                l.width = Screen.width / l.scale;
                l.height = Screen.height / l.scale;
                l.padding = 18;
                l.top = 70;
                l.bottom = setup ? 52 : 138;
                l.left = setup ? Mathf.Min(336, l.width * .44f) + l.padding * 2 : l.padding;
                l.view = new Rect(l.left, l.top, Mathf.Max(80, l.width - l.left - l.padding), Mathf.Max(80, l.height - l.top - l.bottom));
                return l;
            }
        }
        public static Rect CameraViewport(bool setup)
        {
            var l = Layout.For(setup);
            return new Rect(l.view.x / l.width, 1 - l.view.yMax / l.height, l.view.width / l.width, l.view.height / l.height);
        }

        static readonly Color backdrop = new Color(.066f, .087f, .103f);
        static readonly Color panel = new Color(.13f, .16f, .18f);
        static readonly Color field = new Color(.19f, .225f, .245f);
        static readonly Color accent = new Color(.55f, .83f, .74f);
        static readonly Color muted = new Color(.66f, .73f, .76f);
        GUIStyle title, heading, body, small, label, value, button, primary, input, badge;
        Font font;
        Vector2 scroll;
        bool advanced;
        bool ready;
        static void Fill(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }
        GUIStyle Text(int size, Color color, FontStyle weight = FontStyle.Normal) => new GUIStyle
        {
            font = font,
            fontSize = size,
            fontStyle = weight,
            wordWrap = true,
            normal =
            {
                textColor = color
            },
            alignment = TextAnchor.UpperLeft
        };
        void Initialize()
        {
            if (ready)
                return;
            font = Resources.Load<Font>("QuestUI/Inter-Regular");
            title = Text(28, Color.white, FontStyle.Bold);
            heading = Text(22, Color.white, FontStyle.Bold);
            body = Text(18, new Color(.89f, .93f, .94f));
            small = Text(14, muted);
            label = Text(13, muted, FontStyle.Bold);
            value = Text(24, accent, FontStyle.Bold);
            button = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = 16,
                wordWrap = true,
                fixedHeight = 48
            };
            button.normal.background = Texture2D.whiteTexture;
            button.hover.background = Texture2D.whiteTexture;
            button.active.background = Texture2D.whiteTexture;
            button.normal.textColor = button.hover.textColor = button.active.textColor = Color.white;
            primary = new GUIStyle(button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 48
            };
            primary.normal.textColor = primary.hover.textColor = primary.active.textColor = backdrop;
            input = new GUIStyle(GUI.skin.textField)
            {
                font = font,
                fontSize = 19,
                fixedHeight = 40,
                padding = new RectOffset(10, 10, 8, 8)
            };
            input.normal.background = Texture2D.whiteTexture;
            input.focused.background = Texture2D.whiteTexture;
            input.normal.textColor = input.focused.textColor = Color.white;
            badge = Text(14, Color.white, FontStyle.Bold);
            badge.alignment = TextAnchor.MiddleCenter;
            ready = true;
        }
        bool Button(string text, bool selected = false, bool isPrimary = false)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = isPrimary ? accent : selected ? new Color(.25f, .42f, .39f) : field;
            bool clicked = GUILayout.Button(text, isPrimary ? primary : button);
            GUI.backgroundColor = old;
            return clicked;
        }
        void FieldLabel(string text)
        {
            GUILayout.Space(12);
            GUILayout.Label(text, label);
            GUILayout.Space(5);
        }
        public void UpdateInput(PilotSession s)
        {
            var k = Keyboard.current;
            if (k == null)
                return;
            if (k.escapeKey.wasPressedThisFrame)
                s.TogglePause();
            if (!s.DesktopControlsEnabled)
                return;
            if (s.Flow.Phase == StudyPhase.Setup && GUIUtility.keyboardControl != 0)
                return;
            if (k.fKey.wasPressedThisFrame)
                s.RefitDesktopView();
            if (k.spaceKey.wasPressedThisFrame)
                s.ContextCommand();
            if (k.digit1Key.wasPressedThisFrame)
                s.SelectAxis(SemanticAxis.P1);
            if (k.digit2Key.wasPressedThisFrame)
                s.SelectAxis(SemanticAxis.P2);
            if (k.digit3Key.wasPressedThisFrame)
                s.SelectAxis(SemanticAxis.P3);
            if (k.tabKey.wasPressedThisFrame)
                s.ToggleScale();
            if (k.uKey.wasPressedThisFrame)
                s.Undo();
            if (k.yKey.wasPressedThisFrame)
                s.Redo();
            if (k.rKey.wasPressedThisFrame)
                s.ResetState();
            float delta = ((k.rightArrowKey.isPressed ? 1 : 0) - (k.leftArrowKey.isPressed ? 1 : 0)) * Time.unscaledDeltaTime * .5f;
            if (delta != 0)
                s.SetAxis(s.SelectedAxis, s.AxisValue(s.SelectedAxis) + delta);
            if (k.enterKey.wasPressedThisFrame)
                s.Commit(s.SelectedAxis);
            if (s.Flow.Paused)
                return;
            var head = s.headCamera;
            // Clicks in setup forms or status bars must not pass through to the building model.
            var mouse = Mouse.current;
            bool overView = mouse != null && head.pixelRect.Contains(mouse.position.ReadValue());
            if (overView && mouse.leftButton.wasPressedThisFrame)
            {
                var ray = head.ScreenPointToRay(mouse.position.ReadValue());
                s.PointAt(ray.origin, ray.direction);
            }

            if (overView && mouse.rightButton.isPressed)
            {
                Vector2 m = mouse.delta.ReadValue();
                s.xrOrigin.RotateAround(head.transform.position, Vector3.up, m.x * .12f);
                float pitch = Mathf.DeltaAngle(0, head.transform.localEulerAngles.x);
                head.transform.localRotation = Quaternion.Euler(Mathf.Clamp(pitch - m.y * .12f, -80, 80), 0, 0);
            }

            if (s.Flow.Phase == StudyPhase.Setup || s.HasPreview || s.Flow.Phase == StudyPhase.InitialWalkthrough || s.Flow.Phase == StudyPhase.FinalWalkthrough)
                return;
            float x = (k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0), z = (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0);
            Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
            Vector3 move = Vector3.ClampMagnitude(forward * z + Vector3.Cross(Vector3.up, forward) * x, 1) * s.Flow.Settings.desktopSpeed * Time.unscaledDeltaTime;
            Vector3 feet = head.transform.position - Vector3.up * 1.35f;
            if (move.sqrMagnitude > 0 && (s.Scale == StudyScale.Model || !Physics.CapsuleCast(feet, feet + Vector3.up * 1.05f, .22f, move.normalized, move.magnitude + .02f, 1 << 8)))
                s.xrOrigin.position += move;
        }
        public void Draw(PilotSession s)
        {
            if (!s.showDiagnostics || s.XrRunning || Application.platform == RuntimePlatform.Android)
                return;
            Initialize();
            bool setup = s.Flow == null || s.Flow.Phase == StudyPhase.Setup;
            var l = Layout.For(setup);
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * l.scale);
            try
            {
                Fill(new Rect(0, 0, l.width, l.top), backdrop);
                Fill(new Rect(0, l.view.yMax, l.width, l.height - l.view.yMax), backdrop);
                Fill(new Rect(0, l.top, l.left, l.view.height), backdrop);
                Fill(new Rect(l.view.xMax, l.top, l.width - l.view.xMax, l.view.height), backdrop);
                GUI.Label(new Rect(l.padding, 18, 140, 40), "CMER", title);
                GUI.Label(new Rect(l.padding + 94, 26, 60, 24), "v0.9.4", small);
                GUI.Label(new Rect(184, 23, Mathf.Max(160, l.width - 362), 35), "Continuous Multiscale\nEmbodied Revision", small);
                GUI.Label(new Rect(l.width - 170, 25, 150, 25), setup ? "SESSION PREPARATION" : s.Flow.Phase.ToString().ToUpperInvariant(), label);
                if (s.Flow == null)
                {
                    GUI.Label(new Rect(l.padding, l.top + 24, l.width - 2 * l.padding, 140), "Unable to initialize\n" + s.inputStatus, body);
                    return;
                }

                if (Event.current.type == EventType.MouseDown && Mouse.current != null && s.headCamera.pixelRect.Contains(Mouse.current.position.ReadValue()))
                    GUI.FocusControl(null);
                if (setup)
                    DrawSetup(s, l);
                else
                    DrawStatus(s, l);
                DrawModelLabels(s, l);
                if (!s.geometry.gameObject.activeInHierarchy)
                {
                    DrawPhaseMessage(s, l);
                    return;
                }

                string viewName = s.geometry.Current != null && s.geometry.Current.practice ? "NEUTRAL PRACTICE" : "BUILDING " + (s.Scenario == 0 ? "A" : "B");
                if (s.Scale == StudyScale.Body)
                    viewName += "  /  1:1";
                else
                    viewName += "  /  MODEL";
                Fill(new Rect(l.view.x + 12, l.view.y + 12, 228, 30), new Color(.07f, .1f, .12f, .8f));
                GUI.Label(new Rect(l.view.x + 12, l.view.y + 12, 228, 30), viewName, badge);
                if (setup)
                    GUI.Label(new Rect(l.left, l.height - 35, l.view.width, 24), "Right mouse: look   /   F: fit model   /   no recording before Start", small);
            }
            finally
            {
                GUI.matrix = previous;
            }
        }
        void DrawSetup(PilotSession s, Layout l)
        {
            var rect = new Rect(l.padding, l.top, l.left - 2 * l.padding, l.view.height);
            Fill(rect, panel);
            GUILayout.BeginArea(new Rect(rect.x + 18, rect.y + 18, rect.width - 36, rect.height - 36));
            scroll = GUILayout.BeginScrollView(scroll, false, false);
            GUILayout.Label(s.ResearcherPreview ? "Original building preview" : "Prepare a session", heading);
            GUILayout.Space(8);
            GUILayout.Label(s.ResearcherPreview ? "Full original A/B building. Editing is enabled; no participant data is recorded." : "Choose the input mode and study order. Parameters remain locked until practice begins.", small);
            FieldLabel("INPUT MODE");
            GUILayout.BeginHorizontal();
            if (Button("Desktop\nrehearsal", s.settings.inputMode == StudyInputMode.DesktopRehearsal))
                s.SetSetupInputMode(StudyInputMode.DesktopRehearsal);
            if (Button("Quest\nhands", s.settings.inputMode == StudyInputMode.QuestHands))
                s.SetSetupInputMode(StudyInputMode.QuestHands);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.Label(s.settings.inputMode == StudyInputMode.DesktopRehearsal ? "Desktop preview." : "Quest preview. Show both hands.", small);
            FieldLabel("PARTICIPANT CODE");
            var old = GUI.backgroundColor;
            GUI.backgroundColor = field;
            s.settings.participantId = GUILayout.TextField(s.settings.participantId, input);
            GUI.backgroundColor = old;
            FieldLabel("FIXED GROUP / BUILDING ORDER");
            string[] sequences =
            {
                "G: A then B",
                "M: A then B",
                "G: B then A",
                "M: B then A"
            };
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (int col = 0; col < 2; col++)
                {
                    int i = row * 2 + col;
                    if (Button(sequences[i], s.settings.sequence == i))
                        s.SetSetupSequence(i);
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.Label("G: model only   /   M: model + body", small);
            FieldLabel("DOMINANT HAND");
            GUILayout.BeginHorizontal();
            if (Button("Left", !s.settings.rightHandDominant))
                s.settings.rightHandDominant = false;
            if (Button("Right", s.settings.rightHandDominant))
                s.settings.rightHandDominant = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(16);
            bool enabled = GUI.enabled;
            GUI.enabled = s.InputReady;
            if (Button(s.ResearcherPreview ? "Switch building A / B" : "Start practice", false, true))
                s.ContextCommand();
            GUI.enabled = enabled;
            GUILayout.Space(8);
            GUILayout.Label(s.ResearcherPreview ? "Use the participant configuration script before running a formal study." : s.InputReady ? "Independent practice: " + (s.settings.practiceSeconds / 60).ToString("0.#") + " min. Formal tasks remain hidden during practice." : "Waiting for the selected input. Use Desktop rehearsal without a headset.", small);
            if (!string.IsNullOrEmpty(s.inputStatus) && !s.inputStatus.StartsWith("Enter anonymous", StringComparison.Ordinal))
            {
                GUILayout.Space(8);
                GUILayout.Label(s.inputStatus, small);
            }

            GUILayout.Space(18);
            advanced = GUILayout.Toggle(advanced, advanced ? "−  Recovery / advanced" : "+  Recovery / advanced", small);
            if (advanced)
            {
                GUILayout.Space(8);
                GUILayout.Label("Checkpoint path (creates a new log segment)", small);
                s.recoveryCheckpoint = GUILayout.TextField(s.recoveryCheckpoint, input);
                if (Button("Recover session"))
                    s.Recover();
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
        void DrawStatus(PilotSession s, Layout l)
        {
            float y = l.view.yMax + 12;
            string mode = s.Flow.Settings.inputMode == StudyInputMode.DesktopRehearsal ? "REHEARSAL" : "QUEST HANDS";
            GUI.Label(new Rect(l.padding, y, l.width - 2 * l.padding, 24), mode + "  /  " + s.Flow.Condition + "  /  " + s.Scale + "  /  " + Math.Ceiling(s.Flow.Remaining) + " s", label);
            var state = s.Displayed;
            string[] names =
            {
                "P1  ARRIVAL",
                "P2  SHELTER",
                "P3  CONTINUITY"
            };
            double[] parameters =
            {
                state.p1,
                state.p2,
                state.p3
            };
            float cell = (l.width - 2 * l.padding) / 3;
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(l.padding + i * cell, y + 26, cell - 8, 38);
                Fill(r, panel);
                if ((int)s.SelectedAxis == i + 1)
                    Fill(new Rect(r.x, r.y, 3, r.height), accent);
                GUI.Label(new Rect(r.x + 12, r.y + 10, r.width - 95, 24), names[i], label);
                GUI.Label(new Rect(r.xMax - 80, r.y + 5, 72, 30), parameters[i].ToString("F2"), value);
            }

            string status = s.Flow.Paused ? "PAUSED: " + s.Flow.PauseReason : s.inputStatus;
            GUI.Label(new Rect(l.padding, y + 72, l.width - 2 * l.padding, 22), status, small);
            string hint = s.DesktopControlsEnabled ? "1/2/3 select  |  arrows preview  |  Enter save  |  Tab scale  |  U/Y history  |  F fit" : "Open hand(s) to edit  |  Close to save  |  Both pinch to switch scale";
            GUI.Label(new Rect(l.padding, y + 94, l.width - 2 * l.padding, 24), hint, small);
        }
        void DrawPhaseMessage(PilotSession s, Layout l)
        {
            string caption, message;
            switch (s.Flow.Phase)
            {
                case StudyPhase.Questionnaire:
                    caption = "Questionnaire";
                    message = "Complete the external questionnaire. Then use the context gesture or Space in rehearsal to continue.";
                    break;
                case StudyPhase.Washout:
                    caption = "Rest between tasks";
                    message = "The next task starts when the timer ends. The building is hidden during this interval.";
                    break;
                case StudyPhase.Complete:
                    caption = "Session complete";
                    message = "Session files are sealed. Follow the researcher instructions for the remaining study materials.";
                    break;
                default:
                    caption = s.Flow.Phase.ToString();
                    message = s.inputStatus;
                    break;
            }

            float width = Mathf.Min(480, l.view.width - 40);
            var r = new Rect(l.view.center.x - width / 2, l.view.center.y - 88, width, 176);
            Fill(r, panel);
            GUI.Label(new Rect(r.x + 24, r.y + 24, r.width - 48, 38), caption, heading);
            GUI.Label(new Rect(r.x + 24, r.y + 72, r.width - 48, 96), message, body);
        }
        void DrawModelLabels(PilotSession s, Layout l)
        {
            if (s.Scale != StudyScale.Model || s.geometry.Metrics == null || !s.geometry.gameObject.activeInHierarchy)
                return;
            foreach (var room in s.geometry.Metrics.rooms)
            {
                if (room.room != "F" && room.room != "R" && room.room != "G")
                    continue;
                double x = 0, z = 0;
                int count = room.footprintXY.Length / 2;
                for (int i = 0; i < room.footprintXY.Length; i += 2)
                {
                    x += room.footprintXY[i];
                    z += room.footprintXY[i + 1];
                }

                Vector3 point = s.geometry.transform.TransformPoint(new Vector3((float)(x / count), (float)room.height + .4f, (float)(z / count)));
                Vector3 screen = s.headCamera.WorldToScreenPoint(point);
                Vector2 p = new Vector2(screen.x / l.scale, l.height - screen.y / l.scale);
                if (screen.z <= 0 || !l.view.Contains(p))
                    continue;
                string name = room.room == "F" ? "F  /  ARRIVAL" : room.room == "R" ? "R  /  LOUNGE" : "G  /  GALLERY";
                float width = l.view.width < 430 ? 32 : 120;
                var rect = new Rect(Mathf.Clamp(p.x - width / 2, l.view.x, l.view.xMax - width), Mathf.Clamp(p.y, l.view.y + 50, l.view.yMax - 28), width, 26);
                Fill(rect, new Color(.07f, .1f, .12f, .86f));
                GUI.Label(rect, width == 32 ? room.room : name, badge);
            }
        }

        public void Dispose()
        {
            // Resources owns the font; clear references on disposal without destroying the font object.
            font = null;
        }
    }
}
