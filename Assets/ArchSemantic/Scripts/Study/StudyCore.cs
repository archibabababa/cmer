using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Study conditions: G uses model-scale editing and external views; M permits body-scale entry and interior editing.
    /// </summary>
    public enum StudyCondition
    {
        //Edit only at model scale with external model views.
        G,

        //Also permit body-scale entry for interior inspection and editing.
        M
    }

    /// <summary>
    /// In condition G, participants inspect the building at model scale;
    /// In condition M, participants may enter at body scale and edit parameters.
    /// </summary>
    public enum StudyScale
    {
        //Miniature model scale for external inspection and model-scale interaction.
        Model,

        //Body scale (1:1) for inspection and interaction inside the building or full-scale space.
        Body
    }

    /// <summary>
    /// Study session phase.
    /// StudyFlow manages phases and gates input, navigation, editing and recording.
    /// </summary>
    public enum StudyPhase
    {
        /// <summary>Setup: configure participant, input mode and sequence before formal recording starts.</summary>
        Setup,

        /// <summary>Practice: learn controls in neutral geometry, separate from the formal A/B tasks.</summary>
        Practice,

        /// <summary>Initial walkthrough: follow the prescribed route with parameters locked.</summary>
        InitialWalkthrough,

        /// <summary>Revision: inspect and edit the building at scales permitted by the study condition.</summary>
        Revision,

        /// <summary>Final walkthrough: follow the prescribed route again after revision, with parameters locked.</summary>
        FinalWalkthrough,

        /// <summary>Questionnaire: pause building interaction while the participant completes external ratings.</summary>
        Questionnaire,

        /// <summary>Washout: hide the scene during the interval between building tasks; advance after the countdown.</summary>
        Washout,

        /// <summary>Recall: inspect recorded state cues without parameter editing.</summary>
        Recall,

        /// <summary>Complete: finalize recording after all tasks and reject further session operations.</summary>
        Complete
    }

    /// <summary>Study input mode, determining device requirements and input gates.</summary>
    public enum StudyInputMode
    {
        /// <summary>Desktop rehearsal uses keyboard/mouse and requires XR to be stopped.</summary>
        DesktopRehearsal = 0,

        /// <summary>Quest gestures use XR hand tracking and require running XR plus valid two-hand tracking.</summary>
        QuestHands = 1
    }

    /// <summary>
    /// Serializable study session configuration:
    /// Participant information, group/order, input mode, phase durations, display scale and sampling settings.
    /// </summary>
    [Serializable]
    public sealed class StudySettings
    {
        /// <summary>Anonymous participant ID, limited to letters, digits, underscores and hyphens.</summary>
        public string participantId = "PILOT001";
        /// <summary>
        /// Fixed group and building order: 0=G/AB, 1=M/AB, 2=G/BA, 3=M/BA.
        /// The group remains fixed throughout the session.
        /// </summary>
        public int sequence;

        /// <summary>Current study design version.</summary>
        public const string CurrentDesign = "CMER-between-groups/1";

        /// <summary>Study design version marker carried by the session.</summary>
        public string studyDesign = CurrentDesign;

        /// <summary>Derive the fixed condition from sequence parity: even=G, odd=M.</summary>
        public StudyCondition AssignedCondition => sequence % 2 == 0 ? StudyCondition.G : StudyCondition.M;

        /// <summary>Remote view identifier: interior eye-level for M, exterior model for G.</summary>
        public string RemoteViewType => AssignedCondition == StudyCondition.M ? "InteriorEyeLevel" : "ExteriorModel";

        /// <summary>Researcher preview inspects scenes and interactions without creating formal participant records.</summary>
        public bool researcherPreview;

        /// <summary>Session input mode: Quest gestures or desktop rehearsal.</summary>
        public StudyInputMode inputMode = StudyInputMode.QuestHands;

        /// <summary>Execution environment identifier used in logs, configuration contracts and recovery checks.</summary>
        public string executionEnvironment = "Unspecified";

        /// <summary>
        /// Whether to use training preview geometry.
        /// </summary>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <returns>True outside researcher preview when using gestures or running XR.</returns>
        public bool UseTrainingPreview(bool xrRunning) => !researcherPreview && (inputMode == StudyInputMode.QuestHands || xrRunning);

        /// <summary>
        /// Whether desktop input is available.
        /// </summary>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <returns>Available in desktop rehearsal with XR stopped.</returns>
        public bool CanUseDesktopInput(bool xrRunning) => inputMode == StudyInputMode.DesktopRehearsal && !xrRunning;

        /// <summary>
        /// Whether current tracking meets input requirements.
        /// </summary>
        /// <param name="xrRunning">Whether XR is running.</param>
        /// <param name="leftTracked">Whether the left hand is tracked.</param>
        /// <param name="rightTracked">Whether the right hand is tracked.</param>
        /// <returns>Gesture mode requires XR and both hands; desktop mode requires XR to be stopped.</returns>
        public bool HasRequiredTracking(bool xrRunning, bool leftTracked, bool rightTracked) => inputMode == StudyInputMode.QuestHands ? xrRunning && leftTracked && rightTracked : !xrRunning;

        /// <summary>True for right-hand dominance; false for left-hand dominance.</summary>
        public bool rightHandDominant = true;

        /// <summary>Phase durations in seconds: practice, each walkthrough, revision and inter-task washout.</summary>
        public double practiceSeconds = 480, walkthroughSeconds = 60, revisionSeconds = 720, washoutSeconds = 180;

        /// <summary>Display and interaction settings: model scale, observation sampling Hz and desktop movement speed.</summary>
        public float modelScale = .025f, sampleHz = 2f, desktopSpeed = 1.3f;

        /// <summary>
        /// SHA-256 configuration contract combining the geometry contract version and key study settings.
        /// Used for checkpoint recovery and cross-environment checks; changing any included setting changes the hash.
        /// </summary>
        public string ContractHash => StateIdentity.Hash(GeometryDomain.ContractVersion + "|" + inputMode + "|" + executionEnvironment + "|" + studyDesign + "|G=model+exterior;M=model+body-edit+interior|" + practiceSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + walkthroughSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + revisionSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + washoutSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + modelScale.ToString("R", CultureInfo.InvariantCulture) + "|" + sampleHz.ToString("R", CultureInfo.InvariantCulture) + "|" + sequence + "|" + rightHandDominant + "|" + desktopSpeed.ToString("R", CultureInfo.InvariantCulture));

        /// <summary>
        /// Validate participant ID, input mode, design version, sequence, durations and display settings.
        /// Throw ArgumentException when any field violates the study configuration constraints.
        /// </summary>
        public void Validate()
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(participantId ?? "", "^[A-Za-z0-9_-]{1,48}$"))
                throw new ArgumentException("Use an anonymous participant code (letters, digits, underscore or hyphen).");
            if (!Enum.IsDefined(typeof(StudyInputMode), inputMode))
                throw new ArgumentException("Unknown input mode.");
            if (studyDesign != CurrentDesign)
                throw new ArgumentException("Outdated study design. Configure a fixed G/M group before beginning.");
            if (sequence < 0 || sequence > 3)
                throw new ArgumentException("Sequence must be 0–3.");
            foreach (double t in new[]
            {
                practiceSeconds,
                walkthroughSeconds,
                revisionSeconds,
                washoutSeconds
            }
            )
                if (!GeometryDomain.Finite(t) || t <= 0)
                    throw new ArgumentException("Phase durations must be positive.");
            if (!GeometryDomain.Finite(modelScale) || !GeometryDomain.Finite(sampleHz) || !GeometryDomain.Finite(desktopSpeed) || desktopSpeed <= 0 || modelScale < .01f || modelScale > .06f || sampleHz < .25f || sampleHz > 10)
                throw new ArgumentException("Invalid scale or sample rate.");
        }
    }

    /// <summary>
    /// Study flow state machine retaining phase, period, timing and pause reasons,
    /// and determining whether editing, body-scale entry and free navigation are permitted.
    /// </summary>
    public sealed class StudyFlow
    {
        /// <summary>Settings used to create the flow and supply its condition and durations.</summary>
        public readonly StudySettings Settings;

        /// <summary>Current phase; new flows begin at Setup.</summary>
        public StudyPhase Phase { get; private set; } = StudyPhase.Setup;

        /// <summary>Zero-based period, changing to 1 for the second building task.</summary>
        public int Period { get; private set; }

        /// <summary>Remaining phase time in seconds, excluding pauses.</summary>
        public double Remaining { get; private set; }

        /// <summary>Accumulated active flow time in seconds, unchanged during pauses.</summary>
        public double ActiveElapsed { get; private set; }

        // Track independent pause sources, such as manual pause, tracking loss or faults, in a set.
        // Resume only when every pause reason has been removed.
        readonly HashSet<string> pauses = new HashSet<string>();

        /// <summary>Whether any active pause reason exists.</summary>
        public bool Paused => pauses.Count != 0;

        /// <summary>All current pause reasons separated by semicolons for status and logging.</summary>
        public string PauseReason => string.Join(";", pauses);

        /// <summary>Fixed G/M condition determined by the sequence.</summary>
        public StudyCondition Condition => Settings.AssignedCondition;

        /// <summary>
        /// Building for the current period: AB for the first half of sequence codes, BA for the second.
        /// Return 0 for A and 1 for B.
        /// </summary>
        public int Scenario => Settings.sequence < 2 ? Period : 1 - Period;

        /// <summary>Whether this is a formal observation/editing phase: initial walkthrough, revision or final walkthrough.</summary>
        public bool IsFormal => Phase == StudyPhase.InitialWalkthrough || Phase == StudyPhase.Revision || Phase == StudyPhase.FinalWalkthrough;

        /// <summary>Create a flow state machine after validating the session settings.</summary>
        /// <param name="settings">Participant, group, input mode, phase durations and related session settings.</param>
        public StudyFlow(StudySettings settings)
        {
            settings.Validate();
            Settings = settings;
        }

        /// <summary>
        /// Check editing permission from phase, pause state, condition and display scale.
        /// Researcher setup preview, practice and revision allow editing; body-scale editing is M-only.
        /// </summary>
        /// <param name="scale">Display scale: Model is miniature; Body is full-scale 1:1.</param>
        /// <returns>True when editing is currently permitted.</returns>
        public bool CanEdit(StudyScale scale) => !Paused && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision) && (scale == StudyScale.Model || Condition == StudyCondition.M);

        /// <summary>
        /// Check whether body-scale 1:1 entry or use is permitted.
        /// Walkthrough and Recall allow entry; practice, revision and researcher setup preview allow entry only for M.
        /// </summary>
        /// <returns>True when body-scale entry is permitted and the flow is not paused.</returns>
        public bool CanEnterBody => !Paused && (Phase == StudyPhase.InitialWalkthrough || Phase == StudyPhase.FinalWalkthrough || Phase == StudyPhase.Recall || (Condition == StudyCondition.M && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision)));

        /// <summary>
        /// Check free navigation permission.
        /// Disable free navigation during controlled walkthroughs, questionnaires, washout and completion to preserve the standard route.
        /// </summary>
        /// <returns>True for unpaused researcher setup preview, practice, revision or Recall.</returns>
        public bool CanNavigateSpaces => !Paused && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision || Phase == StudyPhase.Recall);

        /// <summary>
        /// Check whether workspace visibility may be toggled.
        /// Setup permits its configuration workspace; other phases follow free-navigation permissions, excluding walkthroughs and questionnaires.
        /// </summary>
        /// <returns>True when showing/hiding the workspace is permitted and the flow is unpaused.</returns>
        public bool CanToggleWorkspace => !Paused && (Phase == StudyPhase.Setup || CanNavigateSpaces);

        /// <summary>Add a pause source without duplicating an existing reason.</summary>
        /// <param name="reason">Pause source identifier, such as manual, tracking, application or fault.</param>
        public void Pause(string reason)
        {
            pauses.Add(reason);
        }

        /// <summary>Remove one pause source; other active reasons keep the flow paused.</summary>
        /// <param name="reason">Reason to clear, matching the identifier passed to Pause.</param>
        public void Resume(string reason)
        {
            pauses.Remove(reason);
        }

        /// <summary>
        /// Start the participant flow from Setup and enter timed Practice.
        /// Researcher preview cannot start a formal participant trial.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown in researcher preview or outside Setup.</exception>
        public void Begin()
        {
            if (Settings.researcherPreview) throw new InvalidOperationException("Researcher preview cannot start a participant trial.");
            if (Phase != StudyPhase.Setup)
                throw new InvalidOperationException("Session already started.");
            Set(StudyPhase.Practice, Settings.practiceSeconds);
        }

        /// <summary>Set the phase and initial remaining time without changing accumulated activity or pause reasons.</summary>
        /// <param name="phase">Phase to enter.</param>
        /// <param name="seconds">Initial phase duration in seconds.</param>
        void Set(StudyPhase phase, double seconds)
        {
            Phase = phase;
            Remaining = seconds;
        }

        /// <summary>
        /// Advance the clock while unpaused and call Advance when the phase timer expires.
        /// </summary>
        /// <param name="seconds">Time increment in seconds, normally Unity's unscaled frame duration.</param>
        /// <returns>True if this call advances the phase; otherwise false.</returns>
        public bool Tick(double seconds)
        {
            if (Paused || seconds <= 0 || !GeometryDomain.Finite(seconds) || Remaining <= 0)
                return false;
            double consumed = Math.Min(seconds, Remaining);
            Remaining -= consumed;
            ActiveElapsed += consumed;
            if (Remaining > 1e-8)
                return false;
            Advance();
            return true;
        }

        /// <summary>
        /// Advance through the fixed study sequence and load the next phase duration.
        /// Do not transition while paused.
        /// </summary>
        public void Advance()
        {
            if (Paused)
                return;
            switch (Phase)
            {
                case StudyPhase.Practice:
                    Set(StudyPhase.InitialWalkthrough, Settings.walkthroughSeconds);
                    break;
                case StudyPhase.InitialWalkthrough:
                    Set(StudyPhase.Revision, Settings.revisionSeconds);
                    break;
                case StudyPhase.Revision:
                    Set(StudyPhase.FinalWalkthrough, Settings.walkthroughSeconds);
                    break;
                case StudyPhase.FinalWalkthrough:
                    Set(StudyPhase.Questionnaire, 0);
                    break;
                case StudyPhase.Questionnaire:
                    Set(Period == 0 ? StudyPhase.Washout : StudyPhase.Recall, Period == 0 ? Settings.washoutSeconds : 0);
                    break;
                case StudyPhase.Washout:
                    Period = 1;
                    Set(StudyPhase.InitialWalkthrough, Settings.walkthroughSeconds);
                    break;
                case StudyPhase.Recall:
                    Set(StudyPhase.Complete, 0);
                    break;
            }
        }

        /// <summary>
        /// Restore phase, period and timers from a checkpoint; add a recovery pause pending external confirmation.
        /// Allowed only while a new flow remains in Setup.
        /// </summary>
        /// <param name="phase">Phase stored in the checkpoint.</param>
        /// <param name="period">Checkpoint period, currently restricted to 0 or 1.</param>
        /// <param name="remaining">Restored remaining phase time in seconds.</param>
        /// <param name="activeElapsed">Restored accumulated active time in seconds.</param>
        /// <exception cref="ArgumentException">Thrown for invalid phase, period or time, or when the flow has left initial Setup.</exception>
        public void Restore(StudyPhase phase, int period, double remaining, double activeElapsed)
        {
            if (!Enum.IsDefined(typeof(StudyPhase), phase) || activeElapsed < 0 || Phase != StudyPhase.Setup || period < 0 || period > 1 || remaining < 0 || !GeometryDomain.Finite(remaining) || !GeometryDomain.Finite(activeElapsed))
                throw new ArgumentException("Invalid recovery checkpoint.");
            Phase = phase;
            Period = period;
            Remaining = remaining;
            ActiveElapsed = activeElapsed;
            Pause("recovery");
        }
    }

    /// <summary>
    /// Serializable recovery checkpoint containing configuration, phase, timing,
    /// parameter state, undo/redo history, and build/geometry identity checks.
    /// </summary>
    [Serializable]
    public sealed class StudyCheckpoint
    {
        /// <summary>Checkpoint format version identifying the serialized structure.</summary>
        public string schema = "cmer-checkpoint/0.9";
        /// <summary>Unique ID of the session that created the checkpoint.</summary>
        public string sessionId;
        /// <summary>Geometry contract version at save time.</summary>
        public string contractVersion;
        /// <summary>Configuration contract hash for recovery consistency checks.</summary>
        public string configHash;
        /// <summary>Build identity hash of the application creating the checkpoint.</summary>
        public string buildHash;
        /// <summary>Stable identifier of the current parameter state.</summary>
        public string stateId;
        /// <summary>Hash of the committed geometry.</summary>
        public string geometryHash;
        /// <summary>Checkpoint label, such as accepted_candidate or a phase-lock marker.</summary>
        public string label;

        /// <summary>Study settings used when creating the checkpoint.</summary>
        public StudySettings settings;
        /// <summary>Study phase at save time.</summary>
        public StudyPhase phase;
        /// <summary>Model or body display scale at save time.</summary>
        public StudyScale scale;
        /// <summary>Selected semantic parameter axis at save time.</summary>
        public SemanticAxis selectedAxis;
        /// <summary>Period index at save time.</summary>
        public int period;
        /// <summary>Building/scenario index at save time; practice uses index 2.</summary>
        public int scenario;
        /// <summary>P1/P2/P3 lock mask in the low three bits.</summary>
        public int parameterLockMask;
        /// <summary>Last event ID before the checkpoint, defining the log/checkpoint boundary.</summary>
        public long precedingEventId;
        /// <summary>Remaining phase time in seconds.</summary>
        public double remaining;
        /// <summary>Accumulated active session time in seconds.</summary>
        public double activeElapsed;
        /// <summary>Committed P1/P2/P3 state at save time.</summary>
        public SpatialState state;
        /// <summary>Undo state sequence at save time.</summary>
        public SpatialState[] undo;
        /// <summary>Redo state sequence at save time.</summary>
        public SpatialState[] redo;
    }

    /// <summary>
    /// Checkpoint protocol validator: verify completeness, build/configuration identity, phase/period combinations,
    /// parameter states and geometry hashes to reject incompatible or damaged recovery/rating data.
    /// </summary>
    public static class CheckpointValidation
    {
        /// <summary>
        /// Validate structure, identity and study constraints; rating checkpoints must come from locked walkthrough phases.
        /// Throw ArgumentException on validation failure.
        /// </summary>
        /// <param name="c">Session checkpoint to validate.</param>
        /// <param name="expectedBuild">Expected current build identity hash.</param>
        /// <param name="forRatings">Whether validating for ratings; only initial/final locked checkpoints are allowed when true.</param>
        public static void Validate(StudyCheckpoint c, string expectedBuild, bool forRatings = false)
        {
            if (c == null || c.settings == null || c.schema != "cmer-checkpoint/0.9" || string.IsNullOrWhiteSpace(c.sessionId))
                throw new ArgumentException("Missing checkpoint or settings.");
            c.settings.Validate();
            if(c.parameterLockMask<0||c.parameterLockMask>7)throw new ArgumentException("Invalid parameter lock mask.");
            if (c.settings.researcherPreview) throw new ArgumentException("Preview is not participant study data.");
            // Build identity, geometry contract and configuration hash must match the current environment exactly.
            if (!IsDigest(expectedBuild) || c.buildHash != expectedBuild || c.contractVersion != GeometryDomain.ContractVersion || c.configHash != c.settings.ContractHash)
                throw new ArgumentException("Checkpoint build or configuration mismatch.");
            if (!Enum.IsDefined(typeof(StudyPhase), c.phase) || !Enum.IsDefined(typeof(StudyScale), c.scale) || !Enum.IsDefined(typeof(SemanticAxis), c.selectedAxis))
                throw new ArgumentException("Unknown checkpoint phase, scale or axis.");
            if (c.phase == StudyPhase.Setup || c.phase == StudyPhase.Complete || c.period < 0 || c.period > 1)
                throw new ArgumentException("Checkpoint is not a resumable study phase.");
            if ((c.phase == StudyPhase.Practice || c.phase == StudyPhase.Washout) && c.period != 0 || c.phase == StudyPhase.Recall && c.period != 1)
                throw new ArgumentException("Phase and period mismatch.");
            // Use the state machine to validate phase/period relationships and building order.
            var flow = new StudyFlow(c.settings);
            flow.Restore(c.phase, c.period, c.remaining, c.activeElapsed);
            bool practice = c.phase == StudyPhase.Practice;
            if (practice ? c.scenario != 2 : c.scenario < 0 || c.scenario > 1 || (c.phase != StudyPhase.Recall && c.scenario != flow.Scenario))
                throw new ArgumentException("Checkpoint phase and scenario mismatch.");
            if ((c.phase == StudyPhase.Revision || c.phase == StudyPhase.Practice) && flow.Condition == StudyCondition.G && c.scale != StudyScale.Model)
                throw new ArgumentException("G revision requires model scale.");
            bool walk = c.phase == StudyPhase.InitialWalkthrough || c.phase == StudyPhase.FinalWalkthrough;
            if (walk && c.scale != StudyScale.Body)
                throw new ArgumentException("Walkthrough requires body scale.");
            double duration = c.phase == StudyPhase.Practice ? c.settings.practiceSeconds : walk ? c.settings.walkthroughSeconds : c.phase == StudyPhase.Revision ? c.settings.revisionSeconds : c.phase == StudyPhase.Washout ? c.settings.washoutSeconds : 0;
            if (c.remaining > duration + 1e-8)
                throw new ArgumentException("Checkpoint remaining time exceeds phase duration.");
            // The current state and every history entry must satisfy parameter ranges and building geometry constraints.
            ValidateState(c.scenario, c.state);
            foreach (var state in c.undo ?? Array.Empty<SpatialState>())
                ValidateState(c.scenario, state);
            foreach (var state in c.redo ?? Array.Empty<SpatialState>())
                ValidateState(c.scenario, state);
            if (c.phase == StudyPhase.InitialWalkthrough && (!c.state.CloseTo(default) || (c.undo?.Length ?? 0) != 0 || (c.redo?.Length ?? 0) != 0))
                throw new ArgumentException("Initial walkthrough must use the neutral state without edit history.");
            if (c.stateId != StateIdentity.Id(c.scenario, c.state) || !IsDigest(c.geometryHash))
                throw new ArgumentException("Checkpoint state or geometry identity mismatch.");
            if ((c.label == "initial_locked" && c.phase != StudyPhase.InitialWalkthrough) || (c.label == "final_locked" && c.phase != StudyPhase.FinalWalkthrough))
                throw new ArgumentException("Locked label does not match its phase.");
            if (forRatings && c.label != "initial_locked" && c.label != "final_locked")
                throw new ArgumentException("Ratings require a locked initial or final checkpoint.");
        }

        /// <summary>Check for a 64-character lowercase hexadecimal SHA-256 digest.</summary>
        /// <param name="value">String to check.</param>
        /// <returns>True when the digest format is valid.</returns>
        static bool IsDigest(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$");

        /// <summary>Validate finite parameters, the [-1, 1] range and formal-scenario geometry validity.</summary>
        /// <param name="scenario">Scenario index; practice scenario 2 bypasses formal geometry-domain constraints.</param>
        /// <param name="state">P1/P2/P3 state to validate.</param>
        /// <exception cref="ArgumentException">Thrown for non-finite, out-of-range or geometrically invalid formal parameters.</exception>
        static void ValidateState(int scenario, SpatialState state)
        {
            for (int i = 0; i < 3; i++)
                if (!GeometryDomain.Finite(state[i]) || Math.Abs(state[i]) > 1)
                    throw new ArgumentException("Invalid checkpoint parameter state.");
            if (scenario != 2 && !GeometryDomain.IsLegal(scenario, state))
                throw new ArgumentException("Checkpoint state is outside the geometry domain.");
        }
    }

    /// <summary>Maintain the committed state and undo/redo stacks.</summary>
    public sealed class RevisionHistory
    {
        /// <summary>Current committed effective parameter state.</summary>
        public SpatialState Current { get; private set; }

        // past stores undoable states; future stores states available after undo for redo.
        readonly List<SpatialState> past = new List<SpatialState>(), future = new List<SpatialState>();

        /// <summary>Return a copy of undo history to protect the internal stack.</summary>
        public SpatialState[] Past => past.ToArray();

        /// <summary>Return a copy of redo history to protect the internal stack.</summary>
        public SpatialState[] Future => future.ToArray();

        /// <summary>Set the current state and clear both history stacks.</summary>
        /// <param name="state">New current parameter state.</param>
        public void Reset(SpatialState state)
        {
            Current = state;
            past.Clear();
            future.Clear();
        }

        /// <summary>Whether an undo state is available.</summary>
        public bool CanUndo => past.Count > 0;

        /// <summary>Whether a redo state is available.</summary>
        public bool CanRedo => future.Count > 0;

        /// <summary>Next undo target, or the current state when undo history is empty.</summary>
        public SpatialState UndoTarget => CanUndo ? past[past.Count - 1] : Current;

        /// <summary>Next redo target, or the current state when redo history is empty.</summary>
        public SpatialState RedoTarget => CanRedo ? future[future.Count - 1] : Current;

        /// <summary>
        /// Accept a validated state and update history according to the action.
        /// Normal edits push the current state onto undo and clear redo; undo/redo verify that their targets still match the stack top.
        /// </summary>
        /// <param name="state">Validated parameter state to accept as current.</param>
        /// <param name="action">Action type: undo and redo use their respective stacks; other values represent a new edit.</param>
        /// <exception cref="InvalidOperationException">Thrown when an undo/redo target is stale or inconsistent with history.</exception>
        public void Accept(SpatialState state, string action)
        {
            if (action == "undo")
            {
                if (!CanUndo || !state.CloseTo(UndoTarget))
                    throw new InvalidOperationException("Stale undo.");
                future.Add(Current);
                past.RemoveAt(past.Count - 1);
            }
            else if (action == "redo")
            {
                if (!CanRedo || !state.CloseTo(RedoTarget))
                    throw new InvalidOperationException("Stale redo.");
                past.Add(Current);
                future.RemoveAt(future.Count - 1);
            }
            else
            {
                if (Current.CloseTo(state))
                    return;
                past.Add(Current);
                future.Clear();
            }

            Current = state;
        }

        /// <summary>Restore the current state and undo/redo stacks from a checkpoint.</summary>
        /// <param name="current">Restored current state.</param>
        /// <param name="undo">Restored undo history; null means an empty stack.</param>
        /// <param name="redo">Restored redo history; null means an empty stack.</param>
        public void Restore(SpatialState current, SpatialState[] undo, SpatialState[] redo)
        {
            Current = current;
            past.Clear();
            future.Clear();
            if (undo != null)
                past.AddRange(undo);
            if (redo != null)
                future.AddRange(redo);
        }
    }

    //Generate state verification identifiers using SHA-256.
    public static class StateIdentity
    {
        /// <summary>Compute SHA-256 over UTF-8 text and return lowercase hexadecimal.</summary>
        /// <param name="text">Text to hash.</param>
        /// <returns>64-character lowercase hexadecimal SHA-256 digest.</returns>
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Generate a stable state ID from geometry contract version, scenario and three parameter values.</summary>
        /// <param name="scenario">Scenario owning the state.</param>
        /// <param name="state">P1/P2/P3 state to identify.</param>
        /// <returns>SHA-256 identifier derived from state contents.</returns>
        public static string Id(int scenario, SpatialState state) => Hash(GeometryDomain.ContractVersion + "|" + scenario + "|" + state.p1.ToString("R", CultureInfo.InvariantCulture) + "|" + state.p2.ToString("R", CultureInfo.InvariantCulture) + "|" + state.p3.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Semantic editing axes, with numeric values corresponding to the parameter indexing convention.</summary>
    public enum SemanticAxis
    {
        /// <summary>No selected axis; value 0.</summary>
        None,

        /// <summary>P1 arrival/reveal axis; value 1.</summary>
        P1,

        /// <summary>P2 openness/refuge axis; value 2.</summary>
        P2,

        /// <summary>P3 enclosure/continuity axis; value 3.</summary>
        P3
    }

    /// <summary>
    /// Map hand movement to semantic parameter values using input kinematics only;
    /// GeometryDomain separately checks architectural geometry constraints.
    /// </summary>
    public static class GestureMapping
    {
        /// <summary>Convert the change in palm separation from its baseline to a P1 or P2 value.</summary>
        /// <param name="axis">Axis to update; only P1 or P2 is allowed.</param>
        /// <param name="baseline">Parameter baseline at the start of this gesture segment.</param>
        /// <param name="startSeparation">Baseline distance between palms, in metres.</param>
        /// <param name="currentSeparation">Current distance between palms, in metres.</param>
        /// <param name="travelMeters">Calibrated hand travel in metres for the parameter mapping.</param>
        /// <returns>Value mapped from palm separation and clamped to [-1, 1].</returns>
        /// <exception cref="ArgumentException">Thrown for an axis other than P1/P2 or invalid separation data.</exception>
        public static double FromPalmSeparation(SemanticAxis axis, double baseline, double startSeparation, double currentSeparation, double travelMeters)
        {
            if (axis != SemanticAxis.P1 && axis != SemanticAxis.P2)
                throw new ArgumentException("Palm separation is used for P1 and P2 only.");
            if (!GeometryDomain.Finite(startSeparation) || !GeometryDomain.Finite(currentSeparation) || startSeparation < 0 || currentSeparation < 0)
                throw new ArgumentException("Invalid palm separation.");
            return FromRouteTravel(baseline, currentSeparation - startSeparation, travelMeters);
        }

        /// <summary>Convert signed travel along the gesture route to a parameter value and clamp to its numeric range.</summary>
        /// <param name="baseline">Parameter baseline at the start of this gesture segment.</param>
        /// <param name="signedTravel">Travel in metres, positive along the route and negative against it.</param>
        /// <param name="travelMeters">Calibrated travel in metres for mapping from -1 to +1.</param>
        /// <returns>Value calculated from the baseline and travel, clamped to [-1, 1].</returns>
        /// <exception cref="ArgumentException">Thrown for non-finite inputs or calibration distances below 0.1 metres.</exception>
        public static double FromRouteTravel(double baseline, double signedTravel, double travelMeters)
        {
            if (!GeometryDomain.Finite(baseline) || !GeometryDomain.Finite(signedTravel) || !GeometryDomain.Finite(travelMeters) || travelMeters < .1)
                throw new ArgumentException("Invalid gesture calibration or tracking sample.");
            return Math.Max(-1, Math.Min(1, baseline + signedTravel / travelMeters));
        }
    }

    /// <summary>
    /// Scene-independent perspective-frustum fitting shared by desktop model framing and projection regression tests.
    /// </summary>
    public static class ModelFraming
    {
        /// <summary>
        /// Compute the minimum camera distance fitting every view-space bounding-box corner for the given field of view and aspect ratio.
        /// </summary>
        /// <param name="verticalFovDegrees">Vertical camera field of view in degrees.</param>
        /// <param name="aspect">Viewport width divided by height.</param>
        /// <param name="viewCorners">Eight view-space corners stored as 24 values in x/y/z triples.</param>
        /// <param name="padding">Bounding-box padding factor, at least 1; default 1.15.</param>
        /// <returns>Forward camera distance satisfying horizontal/vertical frustum and near-plane requirements.</returns>
        /// <exception cref="ArgumentException">Thrown for invalid field of view, aspect, padding or corner-array format.</exception>
        public static double Distance(double verticalFovDegrees, double aspect, double[] viewCorners, double padding = 1.15)
        {
            // Validate viewport parameters and array length; each 3D corner occupies three consecutive values.
            if (!GeometryDomain.Finite(verticalFovDegrees) || verticalFovDegrees <= 1 || verticalFovDegrees >= 170 || !GeometryDomain.Finite(aspect) || aspect <= 0 || !GeometryDomain.Finite(padding) || padding < 1 || viewCorners == null || viewCorners.Length != 24)
                throw new ArgumentException("Invalid model viewport or bounds.");
            // At distance d, perspective half-height is d*tan(FOV/2); multiply by aspect for half-width.
            double tangent = Math.Tan(verticalFovDegrees * Math.PI / 360);
            double distance = .1;
            for (int i = 0; i < viewCorners.Length; i += 3)
            {
                double x = viewCorners[i], y = viewCorners[i + 1], z = viewCorners[i + 2];
                if (!GeometryDomain.Finite(x) || !GeometryDomain.Finite(y) || !GeometryDomain.Finite(z))
                    throw new ArgumentException("Nonfinite model bounds.");
                // Compute horizontal and vertical distance requirements per corner and take their maximum.
                distance = Math.Max(distance, Math.Max(Math.Abs(x) * padding / (tangent * aspect) - z, Math.Abs(y) * padding / tangent - z));
                // Keep corners at least 0.01 units beyond the camera near plane.
                distance = Math.Max(distance, .01 - z);
            }

            return distance;
        }
    }
}
