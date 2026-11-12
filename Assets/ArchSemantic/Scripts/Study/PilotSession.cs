using System;
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Study session controller:
    /// - Manage phases such as Setup, Practice, Revision and Recall;
    /// - Coordinate input, geometry preview/commit, navigation and scale switching;
    /// - Handle event recording, checkpoints and recovery.
    /// </summary>
    public sealed class PilotSession : MonoBehaviour
    {
        /// <summary>XR origin moved or rotated when repositioning the player.</summary>
        public Transform xrOrigin;
        /// <summary>Main head camera.</summary>
        public Camera headCamera;
        /// <summary>Gesture interactor.</summary>
        public NoHandleSemanticGestureInteractor hands;
        /// <summary>Live geometry driver.</summary>
        public LiveGeometryDriver geometry;
        /// <summary>Headset UI text, styled by the workspace.</summary>
        public TextMesh headsetStatus;
        /// <summary>Study settings: participant, group, input mode, durations and related configuration.</summary>
        public StudySettings settings = new StudySettings();
        /// <summary>Checkpoint path supplied by the researcher for recovery.</summary>
        public string recoveryCheckpoint = "";
        /// <summary>Whether to display desktop diagnostics.</summary>
        public bool showDiagnostics = true;
        /// <summary>Default room start index (0/1/2 map to F/R/G).</summary>
        public int startSpace;
        /// <summary>Current participant-visible input hint.</summary>
        public string inputStatus = "Configure participant, fixed G/M group and A/B order, then begin.";
        bool outdatedConfiguration;
        /// <summary>Current study flow state machine.</summary>
        public StudyFlow Flow { get; private set; }
        /// <summary>Current scale: model or 1:1.</summary>
        public StudyScale Scale { get; private set; } = StudyScale.Model;
        /// <summary>Selected semantic parameter axis (P1/P2/P3).</summary>
        public SemanticAxis SelectedAxis { get; private set; } = SemanticAxis.P1;
        /// <summary>Whether flow and geometry initialization is complete.</summary>
        public bool IsInitialized => Flow != null && committedGeometry != null;
        /// <summary>Whether parameter editing is currently permitted.</summary>
        public bool CanEdit => IsInitialized && Flow.CanEdit(Scale) && !commitPending && !fatalLogFault && InputReady;
        /// <summary>Whether input requirements are met: XR/two-hand tracking or desktop mode.</summary>
        public bool InputReady => Flow != null && Flow.Settings.HasRequiredTracking(XrRunning, hands != null && hands.LeftTracked, hands != null && hands.RightTracked);
        /// <summary>Whether hand-gesture input is enabled.</summary>
        public bool UsesHandInput => Flow != null && Flow.Phase != StudyPhase.Complete && Flow.Settings.inputMode == StudyInputMode.QuestHands;
        /// <summary>Whether desktop input is enabled.</summary>
        public bool DesktopControlsEnabled => Flow != null && Flow.Phase != StudyPhase.Complete && Flow.Settings.CanUseDesktopInput(XrRunning);
        /// <summary>Right-hand dominance, preferring frozen Flow settings.</summary>
        public bool RightHandDominant => Flow == null ? settings.rightHandDominant : Flow.Settings.rightHandDominant;
        /// <summary>Whether an XR subsystem is running.</summary>
        public bool XrRunning { get; private set; }
        /// <summary>Current committed effective state.</summary>
        public SpatialState Effective => history.Current;
        /// <summary>Current session recording directory.</summary>
        public string SessionDirectory => recorder?.DirectoryPath;
        /// <summary>Occupied semantic zone (MODEL/F/R/G/C/...).</summary>
        public string Zone { get; private set; } = "MODEL";
        /// <summary>Whether a preview or commit is in progress.</summary>
        public bool HasPreview => previewing || commitPending;
        /// <summary>Scenario index, normally from Flow; Recall/Complete may pin the replay scenario.</summary>
        public int Scenario => Flow != null && (Flow.Phase == StudyPhase.Recall || Flow.Phase == StudyPhase.Complete) && recallScenario >= 0 ? recallScenario : Flow == null ? 0 : Flow.Scenario;

        /// <summary>Whether researcher preview is active, without participant data recording.</summary>
        public bool ResearcherPreview => Flow != null && Flow.Settings.researcherPreview && Flow.Phase == StudyPhase.Setup;
        bool HeadsetPresentation => XrRunning || Application.platform == RuntimePlatform.Android;
        StudyWorkspace workspace;
        /// <summary>Whether the workspace may currently be shown or hidden.</summary>
        public bool CanToggleWorkspace => IsInitialized && Flow.CanToggleWorkspace && InputReady && !HasPreview && !fatalLogFault;
        /// <summary>Requested workspace visibility.</summary>
        public bool WorkspaceRequested => workspace != null && workspace.RequestedVisible;
        /// <summary>Transient workspace feedback, cleared after its timeout.</summary>
        public string WorkspaceFeedback => Time.unscaledTime<workspaceFeedbackUntil?workspaceFeedback:null;
        string workspaceFeedback;
        float workspaceFeedbackUntil;
        /// <summary>Set temporary workspace feedback and mirror it in the input hint.</summary>
        /// <param name="message">Hint text.</param>
        void WorkspaceNotice(string message)
        {
            workspaceFeedback=message;
            workspaceFeedbackUntil=Time.unscaledTime+5f;
            inputStatus=message;
        }
        /// <summary>Parameter lock bitmask: bits 0/1/2 correspond to P1/P2/P3.</summary>
        public int ParameterLockMask { get; private set; }
        /// <summary>Check whether an axis is locked.</summary>
        /// <param name="axis">Target parameter axis.</param>
        /// <returns>True when locked.</returns>
        public bool IsAxisLocked(SemanticAxis axis) => (int)axis>=1&&(int)axis<=3&&(ParameterLockMask&(1<<((int)axis-1)))!=0;

        /// <summary>
        /// Toggle a parameter lock.
        /// Locked axes cannot be changed by gestures or history operations.
        /// </summary>
        /// <param name="axis">Target axis.</param>
        public void ToggleParameterLock(SemanticAxis axis)
        {
            if(!CanEdit||HasPreview||(int)axis<1||(int)axis>3)return;
            ParameterLockMask^=1<<((int)axis-1);
            hands.CancelGesture();
            WorkspaceNotice(axis+(IsAxisLocked(axis)?" LOCKED | value held":" UNLOCKED | ready to edit"));
            Log(IsAxisLocked(axis)?"parameter_locked":"parameter_unlocked",axis.ToString());
            Checkpoint("parameter_lock_changed");
        }
        /// <summary>
        /// Show or hide the workspace.
        /// </summary>
        /// <param name="visible">Whether to show it.</param>
        /// <param name="recall">Whether this is a recall request that resets the placement offset.</param>
        public void SetWorkspaceVisible(bool visible, bool recall=false)
        {
            if(!CanToggleWorkspace)return;
            workspace.SetVisible(visible,recall);
            Log(workspace.RequestedVisible?"workspace_opened":"workspace_closed");
        }
        int pendingWorkspaceAction = -1;    /// Pending workspace action ID (0-12, as mapped by StudyWorkspace).
        StudyPhase pendingWorkspacePhase;
        /// <summary>Forward gesture pointer input to the workspace UI.</summary>
        /// <param name="origin">Ray origin.</param>
        /// <param name="direction">Ray direction.</param>
        /// <param name="wrist">Wrist position.</param>
        /// <param name="pinch">Whether pinching.</param>
        /// <param name="pointing">Whether using a pointing pose.</param>
        /// <param name="tracked">Whether the hand is tracked.</param>
        /// <returns>Whether the workspace consumed the input.</returns>
        public bool ProcessWorkspacePointer(Vector3 origin,Vector3 direction,Vector3 wrist,bool pinch,bool pointing,bool tracked) =>
            workspace != null && workspace.ProcessPointer(origin,direction,wrist,pinch,pointing,tracked);

        /// <summary>
        /// Request a workspace action: navigation, axis selection, locking, hiding or preview group selection.
        /// Check input, phase and fault state first. End an existing preview gesture and commit it where appropriate.
        /// </summary>
        /// <param name="action">Action ID (0-12, as mapped by StudyWorkspace).</param>
        public void RequestWorkspaceAction(int action)
        {
            if(action<0||action>12)return;
            if(action>=11&&!ResearcherPreview)return;
            if(!InputReady||fatalLogFault||!Flow.CanToggleWorkspace)
            {WorkspaceNotice(!InputReady?"SHOW BOTH HANDS TO SELECT":"CONTROL LOCKED IN THIS PHASE");return;}
            pendingWorkspaceAction=action;
            pendingWorkspacePhase=Flow.Phase;
            // A deliberate UI click ends the current transaction before selecting a new relation.
            if(HasPreview)
            {
                hands.EndGesture("HAND_RELEASE");
                if(previewing&&!commitPending)Commit(SelectedAxis);
            }
            else hands.CancelGesture();
            FlushWorkspaceAction();  // Dispatch by action ID; defer or cancel when prerequisites are not met.
        }
        /// <summary>
        /// Execute a pending workspace action when safe.
        /// Require ready input, an unpaused and unchanged phase, and no preview transaction; otherwise cancel the pending action.
        /// </summary>
        void FlushWorkspaceAction()
        {
            if(pendingWorkspaceAction<0)return;
            if(!InputReady||Flow.Paused||Flow.Phase!=pendingWorkspacePhase||fatalLogFault){pendingWorkspaceAction=-1;return;}
            if(HasPreview)return;
            int action=pendingWorkspaceAction;pendingWorkspaceAction=-1;
            if(action<4)NavigateSpace(action);
            else if(action<7)SelectAxis((SemanticAxis)(action-3));
            else if(action==7)SetWorkspaceVisible(false);
            else if(action<11)ToggleParameterLock((SemanticAxis)(action-7));
            else SetPreviewCondition((StudyCondition)(action-11));
        }
        /// <summary>Whether navigation is permitted: model focus or 1:1 movement.</summary>
        public bool CanNavigate => IsInitialized && Flow.CanNavigateSpaces && !HasPreview && !fatalLogFault && InputReady;

        bool Practice => Flow == null || (!ResearcherPreview && Flow.Phase == StudyPhase.Setup) || Flow.Phase == StudyPhase.Practice;

        readonly RevisionHistory history = new RevisionHistory();
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        LiveGeometryDriver.Prepared committedGeometry;
        StudyRecorder recorder;
        SpatialState requested;
        bool previewing, commitPending, fatalLogFault, wasTracked = true;
        string operation = "", buildHash;
        float nextSample, nextCheckpoint;
        bool wasXr, boundaryWarning;
        int viewWidth, viewHeight;
        StudyPhase viewPhase;
        int walkStop = -1, recallScenario = -1;
        StudyNavigation navigation;
        readonly StudyRecall recall = new StudyRecall();
        readonly StudyDesktopView desktop = new StudyDesktopView();
        /// <summary>
        /// Unity lifecycle entry point:
        /// Read settings, detect XR, create the workspace, and initialize navigation and geometry.
        /// </summary>
        void Start()
        {
            try
            {
                // Runtime settings path in persistent storage, outside Assets.
                string setupPath = Path.Combine(Application.persistentDataPath, "study-settings.json");
                if (File.Exists(setupPath))
                {
                    // Clear the design marker first to distinguish legacy protocol files from the current study configuration.
                    settings.studyDesign = "";
                    // Overwrite settings from local JSON while retaining the object reference.
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(setupPath), settings);
                    // Fall back to default preview settings if the design version does not match.
                    outdatedConfiguration = settings.studyDesign != StudySettings.CurrentDesign;
                }

                // Inspect available XR input subsystems to determine whether XR is running (underlying OpenXR support).
                inputs.Clear();
                SubsystemManager.GetSubsystems(inputs);
                foreach (var input in inputs)
                    if (input.running)
                        XrRunning = true;

                // Create safe researcher-preview defaults when settings are absent or obsolete.
                if (!File.Exists(setupPath) || outdatedConfiguration)
                {
                    // Default to hand input on XR/Android and desktop rehearsal otherwise.
                    settings.inputMode = XrRunning || Application.platform == RuntimePlatform.Android ? StudyInputMode.QuestHands : StudyInputMode.DesktopRehearsal;
                    // Start in researcher preview rather than participant recording.
                    settings.researcherPreview = true;
                    // Write the current study design version explicitly.
                    settings.studyDesign = StudySettings.CurrentDesign;
                    // Default sequence 1 selects group M and building A for preview only.
                    settings.sequence = 1;
                }

                // Create and initialize the workspace UI used by participants and researchers.
                workspace = gameObject.AddComponent<StudyWorkspace>();
                workspace.Initialize(this, headsetStatus);

                // Initialize navigation: model/1:1 transitions, teleportation and floor following.
                navigation = new StudyNavigation(xrOrigin, headCamera, geometry);
                navigation.SetTrackingMode(XrRunning);
                wasXr = XrRunning;

                // Construct the flow state machine from the current settings.
                Flow = new StudyFlow(settings);

                // Read the build identity hash for logs, checkpoints and recovery checks.
                buildHash = Resources.Load<TextAsset>("BuildIdentity")?.text.Trim() ?? "UNSTAMPED";

                // Set a common target frame rate, typically 72 Hz on Quest.
                Application.targetFrameRate = 72;

                // Bind the geometry driver's callback target.
                geometry.session = this;

                // Load the initial state: participant setup normally uses practice geometry; researcher preview follows its settings.
                LoadState(default, settings.UseTrainingPreview(XrRunning));

                // Choose the initial scale by condition and device: M may start at 1:1 in a headset; otherwise use model scale.
                SetScale(HeadsetPresentation && Flow.Condition == StudyCondition.M ? StudyScale.Body : StudyScale.Model, true);

                // Log the startup summary.
                Debug.Log("[CMER] Entry ready: scale=" + Scale + ", neutralPractice=" + geometry.Current.practice + ", participantUI=" + (workspace != null && workspace.Ready));
            }
            catch (Exception e)
            {
                // Route initialization exceptions to fault pause.
                Fault(e.Message);
            }
        }

        /// <summary>
        /// Refresh desktop camera viewports and composition when window dimensions or phase change.
        /// </summary>
        public void RefitDesktopView()
        {
            if (!IsInitialized || XrRunning)
                return;
            if (Scale == StudyScale.Model)
                navigation.ShowModel(Flow.Settings.modelScale, false, Flow.Phase == StudyPhase.Setup);
            else
                navigation.SetViewport(false, false);
            Physics.SyncTransforms();
        }

        /// <summary>Switch input mode and refresh the Setup preview.</summary>
        /// <param name="mode">Target input mode.</param>
        public void SetSetupInputMode(StudyInputMode mode)
        {
            if (Flow == null || Flow.Phase != StudyPhase.Setup || settings.inputMode == mode)
                return;
            settings.inputMode = mode;
            RefreshSetupPreview();
        }

        /// <summary>Switch group/order settings and refresh the Setup preview.</summary>
        /// <param name="sequence">Study sequence index (0-3).</param>
        public void SetSetupSequence(int sequence)
        {
            if (Flow == null || Flow.Phase != StudyPhase.Setup || sequence < 0 || sequence > 3 || settings.sequence == sequence)
                return;
            settings.sequence = sequence;
            RefreshSetupPreview();
        }

        /// <summary>Switch G/M in researcher preview and update the UI hint.</summary>
        /// <param name="condition">Target condition (G/M).</param>
        public void SetPreviewCondition(StudyCondition condition)
        {
            if(!ResearcherPreview||!Enum.IsDefined(typeof(StudyCondition),condition))return;
            SetSetupSequence((settings.sequence / 2)*2+(int)condition);
            WorkspaceNotice(condition==StudyCondition.G?"G | MODEL ONLY / EXTERIOR VIEWS":"M | ENTER + EDIT / INTERIOR VIEWS");
            workspace.SetVisible(true,true);
        }

        /// <summary>
        /// Refresh the Setup preview:
        /// - Researcher preview can inspect the full A/B buildings;
        /// - Participant setup uses neutral practice spaces.
        /// </summary>
        void RefreshSetupPreview()
        {
            // A/B inspection is an explicit researcher-only desktop preview.
            // Participant preparation uses neutral rooms; researcher preview uses the full original A/B building.
            LoadState(default, settings.UseTrainingPreview(XrRunning));
            SetScale(HeadsetPresentation && Flow.Condition == StudyCondition.M ? StudyScale.Body : StudyScale.Model, true);
            inputStatus = "";
        }

        /// <summary>
        /// Enter the formal participant flow from Setup:
        /// Freeze settings, create the Recorder and enter Practice.
        /// </summary>
        public void BeginStudy()
        {
            if (Flow == null || Flow.Phase != StudyPhase.Setup || fatalLogFault || ResearcherPreview)
                return;
            try
            {
                settings.Validate();
            }
            catch (ArgumentException e)
            {
                inputStatus = e.Message;
                return;
            }

            try
            {
                if (!InputReady)
                {
                    inputStatus = UsesHandInput ? "Connect the headset and show both hands, or select Desktop rehearsal." : "Desktop rehearsal requires XR to be stopped.";
                    return;
                }

                settings.executionEnvironment = ExecutionEnvironment();
                Flow = new StudyFlow(JsonUtility.FromJson<StudySettings>(JsonUtility.ToJson(settings)));
                recorder = new StudyRecorder(Path.Combine(Application.persistentDataPath, "CMER_Sessions"), Flow.Settings, buildHash);
                Flow.Begin();
                wasTracked = true;
                LoadState(default, true);
                SetScale(HeadsetPresentation && Flow.Condition == StudyCondition.M ? StudyScale.Body : StudyScale.Model, true);
                Log("session_begin", "G=model edit; M=model and body edit; practice entry=" + Scale);
                Checkpoint("practice_start");
                inputStatus = "Practice: neutral rooms; formal A/B remain hidden.";
            }
            catch (Exception e)
            {
                Fault(e.Message);
            }
        }

        /// <summary>
        /// Load a state and build geometry immediately.
        /// </summary>
        /// <param name="state">Target parameter state.</param>
        /// <param name="practice">Whether to use practice geometry.</param>
        void LoadState(SpatialState state, bool practice)
        {
            navigation.ForgetBodyPosition();
            geometry.CancelPending();
            committedGeometry = geometry.BuildNow(Scenario, state, practice);   // Build and display geometry immediately.
            geometry.Apply(committedGeometry, true);
            history.Reset(state);
            ParameterLockMask=0;
            requested = state;
            previewing = commitPending = false;
        }

        /// <summary>Read an axis value from the current requested state.</summary>
        public float AxisValue(SemanticAxis axis)
        {
            return axis == SemanticAxis.None ? 0 : (float)requested[(int)axis - 1];
        }

        /// <summary>Select the editing axis.</summary>
        /// <param name="axis">Target parameter axis.</param>
        public void SelectAxis(SemanticAxis axis)
        {
            if (axis == SemanticAxis.None || HasPreview || !CanEdit)
                return;
            if (SelectedAxis != axis)
            {
                SelectedAxis = axis;
                WorkspaceNotice(axis+(IsAxisLocked(axis)?" SELECTED | LOCKED":" SELECTED | value kept"));
                Log("select_parameter", axis.ToString());
            }
        }

        /// <summary>
        /// Set an axis value and request a geometry preview without committing.
        /// </summary>
        /// <param name="axis">Target axis.</param>
        /// <param name="value">Target value, internally clamped to [-1, 1].</param>
        public void SetAxis(SemanticAxis axis, float value)
        {
            if (!CanEdit || axis == SemanticAxis.None || IsAxisLocked(axis) || !GeometryDomain.Finite(value))
                return;
            if (!previewing)
            {
                SelectedAxis = axis;
                requested = history.Current;
                operation = Guid.NewGuid().ToString("N");
                previewing = true;
                Log("gesture_begin", axis.ToString());
                if (fatalLogFault)
                    return;
            }

            if (axis != SelectedAxis)
                return;
            requested[(int)axis - 1] = Math.Max(-1, Math.Min(1, value));
            geometry.RequestState(Scenario, history.Current, requested, (int)axis - 1, false, false, "edit", operation, Practice);
        }

        /// <summary>
        /// Submit the preview transaction for validation and saving.
        /// </summary>
        /// <param name="axis">Commit axis, which must match the currently selected axis.</param>
        public void Commit(SemanticAxis axis)
        {
            if (!previewing || !CanEdit || axis != SelectedAxis)
                return;
            if (requested.CloseTo(history.Current, 1e-6))
            {
                CancelPreview("no_change");
                inputStatus = "No change";
                return;
            }

            commitPending = true;
            geometry.RequestState(Scenario, history.Current, requested, (int)axis - 1, true, false, "edit", operation, Practice);
            inputStatus = "Validating and saving...";
        }

        /// <summary>
        /// Receive data from the geometry driver:
        /// - Preview packets update the display only;
        /// - Commit packets update history, events, checkpoints and committed state.
        /// </summary>
        /// <param name="data">Prepared geometry data packet.</param>
        public void Prepared(LiveGeometryDriver.Prepared data)
        {
            if (fatalLogFault || !Flow.CanEdit(Scale) || !InputReady)
            {
                CancelPreview("permission_changed");
                return;
            }

            if (!data.commit)
            {
                geometry.Apply(data, false);
                inputStatus = data.projection == "NONE" ? "Preview " + SelectedAxis : "Geometry boundary: other parameters preserved";
                return;
            }

            if (data.effective.CloseTo(history.Current))
            {
                geometry.Apply(committedGeometry, true);
                requested = history.Current;
                previewing = commitPending = false;
                Log("commit_no_change", data.projection);
                inputStatus = "No effective change";
                FlushWorkspaceAction();
                return;
            }

            var old = committedGeometry;
            var past = history.Past;
            var future = history.Future;
            try
            {
                geometry.Apply(data, true);
                history.Accept(data.effective, data.action);
                committedGeometry = data;
                requested = data.effective;
                string checkpoint = Checkpoint("accepted_candidate");
                var e = Event("commit_accepted");
                e.accepted = true;
                e.previous = old.effective;
                e.previousStateId = StateId(old.effective);
                e.requested = data.requested;
                e.projection = data.projection;
                e.operationId = data.operation;
                e.detail = data.action + "|" + checkpoint;
                if (!ResearcherPreview) recorder.Append(e, true);
                else Debug.Log("[CMER] Preview edit: building="+(Scenario==0?"A":"B")+", axis="+SelectedAxis+", state="+StateId(data.effective));
                previewing = commitPending = false;
                inputStatus = (ResearcherPreview ? "Preview retained " : "Saved ") + SelectedAxis + (data.projection == "NONE" ? "" : " (geometry boundary)");
                FlushWorkspaceAction();
            }
            catch (Exception e)
            {
                history.Restore(old.effective, past, future);
                committedGeometry = old;
                requested = old.effective;
                geometry.Apply(old, true);
                previewing = commitPending = false;
                Fault("Commit rolled back: " + e.Message);
            }
        }

        /// <summary>Geometry-build failure callback.</summary>
        /// <param name="message">Failure reason.</param>
        public void GeometryFailed(string message)
        {
            CancelPreview("geometry_rejected");
            inputStatus = "Rejected: " + message;
            Log("geometry_rejected", message);
        }

        /// <summary>
        /// Cancel a preview/commit transaction and restore the committed state on screen.
        /// </summary>
        /// <param name="reason">Cancellation reason for logging.</param>
        public void CancelPreview(string reason)
        {
            bool had = HasPreview;
            geometry.CancelPending();
            previewing = commitPending = false;
            if (committedGeometry != null)
            {
                requested = history.Current;
                if (had)
                    geometry.Apply(committedGeometry, true);
            }

            hands?.CancelGesture();
            if (had)
                inputStatus = "Unfinished edit cancelled; saved values retained.";
            if (had && !fatalLogFault)
                Log("preview_cancelled", reason);
        }

        /// <summary>Shared entry point for undo, redo and reset.</summary>
        /// <param name="action">Action name.</param>
        /// <param name="target">Target state.</param>
        void HistoryAction(string action, SpatialState target)
        {
            for(int i=0;i<3;i++)if(IsAxisLocked((SemanticAxis)(i+1))&&Math.Abs(target[i]-Effective[i])>1e-6)
            {WorkspaceNotice("UNLOCK P"+(i+1)+" BEFORE "+action.ToUpperInvariant());Log("history_denied","locked_parameter="+(i+1));return;}
            if (!CanEdit)
                return;
            CancelPreview(action);
            operation = Guid.NewGuid().ToString("N");
            commitPending = true;
            geometry.RequestState(Scenario, history.Current, target, -1, true, true, action, operation, Practice);
        }

        /// <summary>Undo; in Recall, select the previous cue.</summary>
        public void Undo()
        {
            if (Flow.Phase == StudyPhase.Recall)
            {
                ReplayCue(-1);
                return;
            }

            if (history.CanUndo)
                HistoryAction("undo", history.UndoTarget);
        }

        /// <summary>Redo; in Recall, select the next cue.</summary>
        public void Redo()
        {
            if (Flow.Phase == StudyPhase.Recall)
            {
                ReplayCue(1);
                return;
            }

            if (history.CanRedo)
                HistoryAction("redo", history.RedoTarget);
        }

        /// <summary>Reset to the neutral state.</summary>
        public void ResetState()
        {
            HistoryAction("reset", default);
        }

        /// <summary>Toggle between model and 1:1 scales.</summary>
        public void ToggleScale()
        {
            SetScale(Scale == StudyScale.Model ? StudyScale.Body : StudyScale.Model);
        }

        /// <summary>
        /// Set the display scale and perform necessary navigation repositioning.
        /// </summary>
        /// <param name="scale">Target scale.</param>
        /// <param name="internalChange">Whether system-triggered, bypassing selected participant restrictions.</param>
        public void SetScale(StudyScale scale, bool internalChange = false)
        {
            if (Flow == null || Flow.Phase == StudyPhase.Complete || geometry.Current == null)
                return;
            if (!internalChange && (Flow.Paused || (!ResearcherPreview && Flow.Phase != StudyPhase.Practice && Flow.Phase != StudyPhase.Revision && Flow.Phase != StudyPhase.Recall) || (scale == StudyScale.Body && !Flow.CanEnterBody)))
            {
                inputStatus = "Scale unavailable in this phase / condition";
                Log("scale_denied", scale.ToString());
                return;
            }

            if (commitPending && !internalChange)
            {
                inputStatus = "Wait for the current save";
                return;
            }

            CancelPreview("scale_change");
            if (Scale == StudyScale.Body && scale == StudyScale.Model)
                navigation.RememberBodyPosition();
            Scale = scale;
            if (scale == StudyScale.Body)
            {
                navigation.ShowBody(XrRunning);
                if (internalChange || !navigation.TryRestoreBodyPosition(XrRunning, inputs))
                    GoToSpace(startSpace, true);
            }
            else
                navigation.ShowModel(Flow.Settings.modelScale, XrRunning, Flow.Phase == StudyPhase.Setup);
            Physics.SyncTransforms();
            workspace?.Recenter();
            Log("scale_changed", scale + "|" + (internalChange ? "system" : "participant"));
        }

        /// <summary>Move to an F/R/G observation start at 1:1 scale.</summary>
        /// <param name="index">Room index (0=F, 1=R, 2=G).</param>
        /// <param name="internalChange">Whether system-triggered.</param>
        public void GoToSpace(int index, bool internalChange = false)
        {
            if (Scale != StudyScale.Body || (!internalChange && (Flow.Paused || HasPreview || !Flow.CanEnterBody)))
                return;
            startSpace = Mathf.Clamp(index, 0, 2);
            navigation.GoToSpace(startSpace, XrRunning, inputs);
            workspaceFeedback=null;workspaceFeedbackUntil=-1;
            Log("navigation", new[] { "F", "R", "G" }[startSpace]);
        }

        /// <summary>Update workspace pointer hover without clicking.</summary>
        /// <param name="origin">Ray origin.</param>
        /// <param name="direction">Ray direction.</param>
        /// <param name="tracked">Whether a hand is tracked.</param>
        public void SetWorkspacePointer(Vector3 origin, Vector3 direction, bool tracked) => workspace?.SetPointer(origin,direction,tracked);

        /// <summary>
        /// Navigate to the selected space button target:
        /// - G or restricted phases: focus the model;
        /// - M when entry is allowed: enter at 1:1, including the courtyard.
        /// </summary>
        /// <param name="index">Target button index (0=F, 1=R, 2=G, 3=C).</param>
        public void NavigateSpace(int index)
        {
            if (!CanNavigate || index < 0 || index > 3) return;
            string room = new[] { "F", "R", "G", "C" }[index];
            // Destinations change the observation position, never the selected relation or its values.
            if (!Flow.CanEnterBody)
            {
                if (Scale != StudyScale.Model) return;
                navigation.FocusModelSpace(room, XrRunning);
                Log("navigation_model_focus",room);
            }
            else
            {
                if (Scale != StudyScale.Body) SetScale(StudyScale.Body);
                if (Scale != StudyScale.Body) return;
                if (index < 3) GoToSpace(index);
                else if (!navigation.GoToCourtyard(XrRunning,inputs))
                {
                    WorkspaceNotice("COURTYARD BLOCKED | change shape");
                    Log("navigation_denied","C: standing clearance");return;
                }
                if(index==3)Log("navigation","C");
            }
            workspace?.Recenter();
            string arrived=Scale==StudyScale.Body?CurrentZone():room;
            WorkspaceNotice(arrived + (Scale==StudyScale.Body ? " | ARRIVED / 1:1" : " | MODEL FOCUS")+"\nMODE "+SelectedAxis+" | values kept");
        }

        /// <summary>
        /// Point-command semantics:
        /// - First attempt to activate the workspace;
        /// - At model scale, select a 1:1 destination;
        /// - At body scale, teleport to a floor point.
        /// </summary>
        /// <param name="origin">Ray origin.</param>
        /// <param name="direction">Ray direction.</param>
        public void PointAt(Vector3 origin, Vector3 direction)
        {
            if (workspace != null && workspace.TryActivate(origin,direction)) return;
            if (Flow.Paused || HasPreview)
                return;
            if (!Physics.Raycast(origin, direction, out var hit, Scale == StudyScale.Model ? 3 : 30, 1 << 8))
                return;
            Vector3 local = geometry.transform.InverseTransformPoint(hit.point);
            string room = geometry.Metrics.Zone(local.x, local.z);
            int axis = room == "F" ? 1 : room == "R" ? 2 : room == "G" ? 3 : 0;
            if (Scale == StudyScale.Model)
            {
                // Pointing selects a destination only; P1/P2/P3 are explicit workspace choices.
                if (Flow.CanEnterBody && navigation.SelectBodyDestination(local))
                    inputStatus = "Room selected; use the scale gesture to inspect its clear floor at 1:1.";
            }
            else if (Flow.CanEnterBody && (ResearcherPreview || Flow.Phase == StudyPhase.Revision || Flow.Phase == StudyPhase.Practice) && hit.normal.y > .7f && (axis > 0 || room == "C"))
            {
                if (navigation.TryTeleport(hit.point, XrRunning, inputs))
                    Log("teleport", room);
                else
                    inputStatus = "Choose a finished floor with sufficient standing clearance.";
            }
        }

        /// <summary>Manually pause or resume.</summary>
        public void TogglePause()
        {
            if (recorder == null || fatalLogFault || Flow.Phase == StudyPhase.Complete)
                return;
            CancelPreview("manual_pause");
            if (Flow.PauseReason.Contains("manual"))
                Flow.Resume("manual");
            else
                Flow.Pause("manual");
            Log("pause_changed", Flow.PauseReason);
            Checkpoint("pause");
        }

        /// <summary>
        /// Context command: begin the study, resume recovery, advance the phase or toggle pause according to the current phase.
        /// </summary>
        public void ContextCommand()
        {
            if (ResearcherPreview)
            {
                if (!InputReady) return;
                CancelPreview("preview_building_change");
                settings.sequence = (Scenario == 0 ? 2 : 0) + (int)Flow.Condition;
                startSpace = 0;SelectedAxis = SemanticAxis.P1;
                LoadState(default,false);
                SetScale(HeadsetPresentation && Flow.Condition == StudyCondition.M ? StudyScale.Body : StudyScale.Model,true);
                Debug.Log("[CMER] Full original building " + (Scenario==0?"A":"B") + " ready; researcher preview, not recording.");
                return;
            }
            if (Flow.Phase == StudyPhase.Setup)
            {
                BeginStudy();
                return;
            }

            if (Flow.PauseReason.Contains("recovery"))
            {
                Flow.Resume("recovery");
                Log("recovery_resumed");
                return;
            }

            if (Flow.Phase == StudyPhase.Questionnaire || Flow.Phase == StudyPhase.Recall)
            {
                if (!Flow.Paused)
                {
                    Checkpoint("phase_exit");
                    Flow.Advance();
                    PhaseChanged();
                }

                return;
            }

            TogglePause();
        }

        /// <summary>Shared phase-transition handling for scale, geometry activation and recording finalization.</summary>
        void PhaseChanged()
        {
            CancelPreview("phase_change");
            walkStop = -1;
            if (Flow.Phase == StudyPhase.InitialWalkthrough)
            {
                LoadState(default, false);
                SetScale(StudyScale.Body, true);
                Checkpoint("initial_locked");
            }
            else if (Flow.Phase == StudyPhase.Revision)
                SetScale(StudyScale.Model, true);
            else if (Flow.Phase == StudyPhase.FinalWalkthrough)
            {
                SetScale(StudyScale.Body, true);
                Checkpoint("final_locked");
            }

            if (Flow.Phase == StudyPhase.Recall)
                BuildRecallQueue();
            geometry.gameObject.SetActive(Flow.Phase != StudyPhase.Washout && Flow.Phase != StudyPhase.Questionnaire && Flow.Phase != StudyPhase.Complete);
            Log("phase_enter");
            Checkpoint("phase_enter");
            if (Flow.Phase == StudyPhase.Complete && !fatalLogFault)
            {
                recorder.Seal("complete");
                inputStatus = "Session sealed.";
            }
        }

        /// <summary>Build the Recall cue queue.</summary>
        void BuildRecallQueue()
        {
            recall.Load(recorder);
            Log("recall_cues_ready", recall.Count.ToString());
            inputStatus = "Recall: support pinch + dominant swipe cycles before/after states";
        }

        /// <summary>
        /// Replay a Recall cue for inspection only, without editing.
        /// </summary>
        /// <param name="direction">Direction: -1 for previous, 1 for next.</param>
        public void ReplayCue(int direction)
        {
            if (Flow.Phase != StudyPhase.Recall || Flow.Paused || fatalLogFault || recall.Count == 0)
                return;
            var c = recall.Next(direction);
            try
            {
                var prepared = geometry.BuildNow(c.scenario, c.state);
                if (prepared.metrics.geometryHash != c.geometryHash)
                    throw new InvalidDataException("Recall geometry hash mismatch");
                CancelPreview("recall");
                recallScenario = c.scenario;
                committedGeometry = prepared;
                history.Reset(c.state);
                requested = c.state;
                geometry.Apply(prepared, true);
                SetScale(StudyScale.Model, true);
                Log("recall_state", recall.CueSession + "|source_event=" + recall.CueId + "|period=" + (c.period + 1) + "|" + c.stateId);
                inputStatus = "Recall " + (recall.Index + 1) + " / " + recall.Count + " (inspection only)";
            }
            catch (Exception e)
            {
                Fault(e.Message);
            }
        }

        /// <summary>
        /// Main per-frame loop:
        /// XR changes, tracking/input gates, phase progression, observation samples, desktop input and floor following.
        /// </summary>
        void Update()
        {
            if (!IsInitialized)
                return;
            inputs.Clear();
            SubsystemManager.GetSubsystems(inputs);
            XrRunning = false;
            foreach (var i in inputs)
                if (i.running)
                    XrRunning = true;
            if (XrRunning != wasXr)
            {
                navigation.SetTrackingMode(XrRunning);
                if (Flow.Phase == StudyPhase.Setup && XrRunning)
                {
                    settings.inputMode = StudyInputMode.QuestHands;
                    // Reframe even if Android had already chosen QuestHands before XR started.
                    RefreshSetupPreview();
                    Debug.Log("[CMER] XR ready: entered 1:1 neutral practice room.");
                }
                else
                    SetScale(Scale, true);
            }

            wasXr = XrRunning;
            bool tracked = InputReady;
            if (tracked != wasTracked && (recorder != null || ResearcherPreview) && Flow.Phase != StudyPhase.Complete)
            {
                if (!tracked)
                {
                    CancelPreview("tracking_lost");
                    Flow.Pause("tracking");
                }
                else
                    Flow.Resume("tracking");
                Log(tracked ? "tracking_restored" : "tracking_lost");
            }

            wasTracked = tracked;
            FlushWorkspaceAction();
            if (!fatalLogFault && recorder != null && Flow.Phase != StudyPhase.Complete)
            {
                try
                {
                    if (!Flow.Paused && Flow.Remaining > 0 && Flow.Remaining <= Time.unscaledDeltaTime)
                    {
                        CancelPreview("deadline");
                        Checkpoint("phase_exit");
                    }

                    if (Flow.Tick(Time.unscaledDeltaTime))
                        PhaseChanged();
                    if (Flow.Phase == StudyPhase.Complete)
                        return;
                    if ((Flow.Phase == StudyPhase.InitialWalkthrough || Flow.Phase == StudyPhase.FinalWalkthrough) && !Flow.Paused)
                    {
                        int stop = Math.Min(5, (int)((1 - Flow.Remaining / Flow.Settings.walkthroughSeconds) * 6));
                        if (stop != walkStop)
                        {
                            walkStop = stop;
                            int room = stop % 3;
                            navigation.WalkthroughStop(room, stop >= 3, XrRunning, inputs);
                            Log("walkthrough_stop", stop.ToString());
                        }
                    }

                    if (Time.unscaledTime >= nextSample)
                    {
                        nextSample = Time.unscaledTime + 1 / Flow.Settings.sampleHz;
                        Sample();
                    }

                    if (Time.unscaledTime >= nextCheckpoint)
                    {
                        nextCheckpoint = Time.unscaledTime + 15;
                        Checkpoint("periodic");
                    }
                }
                catch (Exception e)
                {
                    Fault(e.Message);
                }
            }

            if (!XrRunning && (viewWidth != Screen.width || viewHeight != Screen.height || viewPhase != Flow.Phase))
            {
                RefitDesktopView();
                viewWidth = Screen.width;
                viewHeight = Screen.height;
                viewPhase = Flow.Phase;
            }

            desktop.UpdateInput(this);
            if (Scale == StudyScale.Body && !navigation.FollowFloor(HasPreview))
            {
                GoToSpace(startSpace, true);
                Log("navigation_relocated", "The edited floor no longer supports the observer; returned to the room start.");
            }
            if (XrRunning && Scale == StudyScale.Body)
                navigation.AlignFloor(inputs);

        }

        /// <summary>Record observations and boundary warnings at the sampling rate.</summary>
        void Sample()
        {
            var p = geometry.transform.InverseTransformPoint(headCamera.transform.position);
            Zone = CurrentZone();
            // Location is recorded independently; it must not overwrite the chosen P1/P2/P3 relation.

            bool boundary = Scale == StudyScale.Body && (Zone == "OUTSIDE" || (!HasPreview && Physics.CheckSphere(headCamera.transform.position, .2f, 1 << 8)));
            if (boundary != boundaryWarning)
            {
                boundaryWarning = boundary;
                Log(boundary ? "virtual_boundary_near" : "virtual_boundary_clear");
            }

            if (boundary)
                inputStatus = "Near virtual boundary: return to clear floor / use point-pinch teleport";
            else if (inputStatus.StartsWith("Near virtual boundary"))
                inputStatus = "Ready";
            var e = Event("observation");
            for (int i = 0; i < geometry.liveZonePositions.Length; i++)
                if (Vector3.Distance(p, geometry.liveZonePositions[i]) < 1.5f)
                    e.criticalRegion = "P" + (i + 1);
            e.detail = "frame_ms=" + (Time.unscaledDeltaTime * 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            if (Physics.Raycast(headCamera.transform.position, headCamera.transform.forward, out var hit, 100, 1 << 8))
            {
                var q = geometry.transform.InverseTransformPoint(hit.point);
                e.detail += "|head_ray=" + geometry.Metrics.Zone(q.x, q.z);
            }

            Write(e, false);
        }

        /// <summary>Infer the occupied semantic zone.</summary>
        string CurrentZone()
        {
            if (Scale == StudyScale.Model)
                return "MODEL";
            if (geometry.Metrics == null)
                return "UNKNOWN";
            if (navigation.OccupiedFloor == "U_R1" || navigation.OccupiedFloor == "U_R2") return "R";
            if (navigation.OccupiedFloor == "U_G") return "G";
            var position = geometry.transform.InverseTransformPoint(headCamera.transform.position);
            return geometry.Metrics.Zone(position.x, position.z);
        }

        /// <summary>Generate a state ID, using scenario=2 for Practice.</summary>
        /// <param name="p">State parameters.</param>
        /// <returns>Unique state identifier.</returns>
        string StateId(SpatialState p) => StateIdentity.Id(Practice ? 2 : Scenario, p);

        /// <summary>Construct a standardized event.</summary>
        /// <param name="kind">Event type.</param>
        /// <returns>Initialized event object.</returns>
        StudyEvent Event(string kind) => new StudyEvent
        {
            eventType = kind,
            phase = Flow.Phase.ToString(),
            condition = Flow.Condition.ToString(),
            period = Flow.Period + 1,
            scenario = Practice ? "TRAINING" : Scenario == 0 ? "A" : "B",
            scale = Scale.ToString(),
            zone = CurrentZone(),
            occupiedFloor = Scale == StudyScale.Body ? navigation.OccupiedFloor : "MODEL",
            practice = Practice,
            activeSeconds = Flow.ActiveElapsed,
            remainingSeconds = Flow.Remaining,
            activeParameter = SelectedAxis.ToString(),
            operationId = operation,
            stateId = StateId(history.Current),
            previousStateId = StateId(history.Current),
            geometryHash = committedGeometry?.metrics.geometryHash,
            displayedGeometryHash = geometry.Current?.metrics.geometryHash,
            displayedState = geometry.Current != null ? geometry.Current.effective : history.Current,
            geometryPosition = geometry.transform.position,
            geometryScale = geometry.transform.localScale,
            geometryRotation = geometry.transform.rotation,
            geometryRevision = geometry.displayedVersion,
            parameterLockMask = ParameterLockMask,
            requested = requested,
            effective = history.Current,
            previous = history.Current,
            headPosition = headCamera.transform.position,
            headRotation = headCamera.transform.rotation,
            leftTracked = hands != null && hands.LeftTracked,
            rightTracked = hands != null && hands.RightTracked,
            leftPosition = hands != null ? hands.LeftPosition : Vector3.zero,
            rightPosition = hands != null ? hands.RightPosition : Vector3.zero
        };
        /// <summary>Write an event, optionally flushing durably to disk.</summary>
        /// <param name="e">Event object.</param>
        /// <param name="durable">Whether to perform an immediate durable flush.</param>
        void Write(StudyEvent e, bool durable = true)
        {
            if (recorder == null || fatalLogFault)
                return;
            try
            {
                recorder.Append(e, durable);
            }
            catch (Exception x)
            {
                Fault(x.Message);
            }
        }

        /// <summary>Convenience logging entry point.</summary>
        /// <param name="kind">Event type.</param>
        /// <param name="detail">Additional details.</param>
        void Log(string kind, string detail = "")
        {
            if (Flow == null)
                return;
            var e = Event(kind);
            e.detail = detail;
            Write(e);
        }

        /// <summary>Save a checkpoint and return its relative path.</summary>
        /// <param name="label">Checkpoint label.</param>
        /// <returns>Relative checkpoint file path.</returns>
        string Checkpoint(string label)
        {
            if (recorder == null || fatalLogFault)
                return "";
            try
            {
                return recorder.SaveCheckpoint(new StudyCheckpoint { phase = Flow.Phase, period = Flow.Period, scenario = Practice ? 2 : Scenario, remaining = Flow.Remaining, activeElapsed = Flow.ActiveElapsed, scale = Scale, selectedAxis = SelectedAxis, parameterLockMask = ParameterLockMask, state = history.Current, undo = history.Past, redo = history.Future, stateId = StateId(history.Current), geometryHash = committedGeometry.metrics.geometryHash, label = label });
            }
            catch (Exception e)
            {
                Fault(e.Message);
                throw;
            }
        }

        /// <summary>
        /// Restore a session: validate, load, attach recovery lineage, then continue the flow.
        /// </summary>
        public void Recover()
        {
            if (Flow.Phase != StudyPhase.Setup || fatalLogFault)
                return;
            try
            {
                var c = JsonUtility.FromJson<StudyCheckpoint>(File.ReadAllText(recoveryCheckpoint));
                CheckpointValidation.Validate(c, buildHash);
                if (c.settings.executionEnvironment != ExecutionEnvironment())
                    throw new InvalidDataException("Recovery cannot switch between editor/simulator, desktop player and Android device environments.");
                CheckRecoveryAcknowledgement(recoveryCheckpoint, c);
                Flow = new StudyFlow(c.settings);
                Flow.Restore(c.phase, c.period, c.remaining, c.activeElapsed);
                if (c.phase == StudyPhase.Recall)
                    recallScenario = c.scenario;
                LoadState(c.state, c.scenario == 2);
                if (committedGeometry.metrics.geometryHash != c.geometryHash)
                    throw new InvalidDataException("Geometry hash mismatch.");
                history.Restore(c.state, c.undo, c.redo);
                ParameterLockMask=c.parameterLockMask;
                recorder = new StudyRecorder(Path.Combine(Application.persistentDataPath, "CMER_Sessions"), Flow.Settings, buildHash);
                RecallArchive.Attach(recorder, recoveryCheckpoint, c);
                recorder.WriteNew("recovery_source.json", JsonUtility.ToJson(c, true));
                SetScale(c.scale, true);
                geometry.gameObject.SetActive(Flow.Phase != StudyPhase.Washout && Flow.Phase != StudyPhase.Questionnaire);
                SelectedAxis = c.selectedAxis;
                Log("recovery_loaded", c.sessionId);
                Checkpoint("recovered");
                if (Flow.Phase == StudyPhase.Recall) BuildRecallQueue();
                inputStatus = "Recovered; Space / context gesture resumes.";
            }
            catch (Exception e)
            {
                Fault("Recovery failed: " + e.Message);
            }
        }

        /// <summary>
        /// Verify that the durable confirmation event for accepted_candidate actually exists.
        /// </summary>
        /// <param name="path">Checkpoint file path.</param>
        /// <param name="c">Checkpoint object.</param>
        public static void CheckRecoveryAcknowledgement(string path, StudyCheckpoint c)
        {
            if (c.label != "accepted_candidate")
                return;
            string log = Path.Combine(Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(path))).FullName, "events.jsonl");
            string name = "states/" + Path.GetFileName(path);
            bool found = false;
            foreach (string line in StudyRecorder.ReadEvents(log))
            {
                StudyEvent e;
                try
                {
                    e = JsonUtility.FromJson<StudyEvent>(line);
                }
                catch
                {
                    continue;
                }

                if (e != null && e.accepted && e.eventType == "commit_accepted" && e.sessionId == c.sessionId &&
                    e.buildHash == c.buildHash && e.configHash == c.configHash && e.eventId == c.precedingEventId + 1 &&
                    e.stateId == c.stateId && e.geometryHash == c.geometryHash && e.detail != null && e.detail.EndsWith("|" + name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                throw new InvalidDataException("This candidate has no durable commit acknowledgement. Select the previous periodic / phase checkpoint.");
        }

        /// <summary>Current execution environment: Editor, Android or DesktopPlayer.</summary>
        static string ExecutionEnvironment() => Application.isEditor ? "EditorTest" :
            Application.platform == RuntimePlatform.Android ? "AndroidXR" : "DesktopPlayerTest";

        /// <summary>
        /// Enter fault pause, stopping previews while retaining the committed state.
        /// </summary>
        /// <param name="message">Fault message.</param>
        void Fault(string message)
        {
            fatalLogFault = true;
            Flow?.Pause("fault");
            geometry?.CancelPending();
            if (committedGeometry != null)
            {
                geometry.Apply(committedGeometry, true);
                requested = history.Current;
            }

            previewing = commitPending = false;
            hands?.CancelGesture();
            inputStatus = "PAUSED: " + message;
            Debug.LogError(inputStatus, this);
        }

        /// <summary>Handle flow and logging on application pause/resume.</summary>
        /// <param name="pause">Whether entering pause.</param>
        void OnApplicationPause(bool pause)
        {
            if (ResearcherPreview) { if (pause) CancelPreview("preview_app_pause"); return; }
            if (Flow == null || recorder == null || Flow.Phase == StudyPhase.Complete)
                return;
            try
            {
                if (pause)
                {
                    CancelPreview("app_pause");
                    Flow.Pause("application");
                    Log("application_pause");
                    Checkpoint("application_pause");
                }
                else
                {
                    Flow.Resume("application");
                    Flow.Pause("manual");
                    Log("application_return_requires_resume");
                }
            }
            catch (Exception e)
            {
                Fault(e.Message);
            }
        }

        /// <summary>Handle focus loss, pausing manually where necessary.</summary>
        /// <param name="focus">Whether focus was gained.</param>
        void OnApplicationFocus(bool focus)
        {
            if (ResearcherPreview && !focus) { CancelPreview("preview_focus_loss"); return; }
            if (!focus && !((Application.isEditor || Application.platform == RuntimePlatform.WindowsPlayer) && XrRunning) && recorder != null && Flow.Phase != StudyPhase.Complete)
            {
                CancelPreview("focus_loss");
                Flow.Pause("manual");
                Log("focus_lost");
            }
        }

        /// <summary>Finalize and dispose of the recorder on destruction.</summary>
        void OnDestroy()
        {
            desktop.Dispose();
            try
            {
                if (fatalLogFault)
                    recorder?.Seal("technical_failure");
                else
                    recorder?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError("Seal failed: " + e.Message);
            }
        }

        /// <summary>Displayed state, preferring geometry.Current.</summary>
        public SpatialState Displayed => geometry != null && geometry.Current != null ? geometry.Current.effective : history.Current;

        /// <summary>Full researcher-facing status text.</summary>
        public string StatusText() => string.Format("{0} | {1} | {2} | {3:F0}s\nP1 {4:F2}   P2 {5:F2}   P3 {6:F2}\n{7}", Flow.Phase, Flow.Condition, Scale, Flow.Remaining, Displayed.p1, Displayed.p2, Displayed.p3, Flow.Paused ? "PAUSED " + Flow.PauseReason : inputStatus);
        // Compact participant feedback; full diagnostics remain in the researcher desktop view.
        /// <summary>Compact participant-facing headset status text.</summary>
        public string HeadsetStatusText()
        {
            if (Flow == null) return "CMER | PREPARING SPACE";
            string parameters=string.Format("P1 {0:F2}   P2 {1:F2}   P3 {2:F2}",Displayed.p1,Displayed.p2,Displayed.p3);
            if (ResearcherPreview)
                return "CMER | BUILDING " + (Scenario==0?"A":"B") + " | " + (Scale==StudyScale.Body?"1:1":"MODEL") +
                    "\n" + parameters + "\n" + CurrentZone() + " | HANDS " + (InputReady?"READY":"SHOW BOTH") + "\n" + (outdatedConfiguration?"OLD CONFIG / PREVIEW ONLY":Flow.Condition+" PREVIEW / NOT RECORDING");
            if (Flow.Phase == StudyPhase.Setup)
                return "CMER | GROUP " + Flow.Condition + " | " + (Scale==StudyScale.Body?"1:1":"MODEL") + "\n" +
                    "L " + (hands != null && hands.LeftTracked ? "OK" : "--") + " / R " + (hands != null && hands.RightTracked ? "OK" : "--") +
                    "\nPalm UP + other index pinch\nHold 0.8 s: begin practice";
            if(!Flow.Paused && Flow.Phase==StudyPhase.Questionnaire)
                return "QUESTIONNAIRE\nComplete external ratings\nPalm UP + other index pinch\nHold 0.8 s to continue";
            if(!Flow.Paused && Flow.Phase==StudyPhase.Washout)
                return "REST | "+Flow.Remaining.ToString("F0")+" s\nNext task begins automatically";
            if(!Flow.Paused && Flow.Phase==StudyPhase.Complete)
                return "SESSION COMPLETE\nPlease notify the researcher\nExport data before uninstalling";
            string hint=Flow.Paused?"PAUSED: "+Flow.PauseReason:inputStatus??"";
            if(hint.Length>29)
            {
                int split=hint.LastIndexOf(' ',28);if(split<12)split=29;
                string rest=hint.Substring(split).TrimStart();
                hint=hint.Substring(0,split)+"\n"+(rest.Length>29?rest.Substring(0,26)+"...":rest);
            }
            return string.Format("{0} | {1} | {2:F0}s\n{3}\n{4}",Flow.Condition+" "+Flow.Phase,Scale,Flow.Remaining,parameters,hint);
        }
        /// <summary>Desktop diagnostics UI drawing entry point.</summary>
        void OnGUI() => desktop.Draw(this);
    }
}
