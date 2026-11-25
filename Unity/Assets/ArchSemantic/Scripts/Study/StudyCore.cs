using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArchSemanticPilot
{
    public enum StudyCondition
    {
        //Edit only at model scale with external model views.
        G,

        //Also permit body-scale entry for interior inspection and editing.
        M
    }
    public enum StudyScale
    {
        //Miniature model scale for external inspection and model-scale interaction.
        Model,

        //Body scale (1:1) for inspection and interaction inside the building or full-scale space.
        Body
    }
    public enum StudyPhase
    {
        Setup,
        Practice,
        InitialWalkthrough,
        Revision,
        FinalWalkthrough,
        Questionnaire,
        Washout,
        Recall,
        Complete
    }
    public enum StudyInputMode
    {
        DesktopRehearsal = 0,
        QuestHands = 1
    }
    [Serializable]
    public sealed class StudySettings
    {
        public string participantId = "PILOT001";
        public int sequence;
        public const string CurrentDesign = "CMER-between-groups/1";
        public string studyDesign = CurrentDesign;
        public StudyCondition AssignedCondition => sequence % 2 == 0 ? StudyCondition.G : StudyCondition.M;
        public string RemoteViewType => AssignedCondition == StudyCondition.M ? "InteriorEyeLevel" : "ExteriorModel";
        public bool researcherPreview;
        public StudyInputMode inputMode = StudyInputMode.QuestHands;
        public string executionEnvironment = "Unspecified";
        public bool UseTrainingPreview(bool xrRunning) => !researcherPreview && (inputMode == StudyInputMode.QuestHands || xrRunning);
        public bool CanUseDesktopInput(bool xrRunning) => inputMode == StudyInputMode.DesktopRehearsal && !xrRunning;
        public bool HasRequiredTracking(bool xrRunning, bool leftTracked, bool rightTracked) => inputMode == StudyInputMode.QuestHands ? xrRunning && leftTracked && rightTracked : !xrRunning;
        public bool rightHandDominant = true;
        public double practiceSeconds = 480, walkthroughSeconds = 60, revisionSeconds = 720, washoutSeconds = 180;
        public float modelScale = .025f, sampleHz = 2f, desktopSpeed = 1.3f;
        public string ContractHash => StateIdentity.Hash(GeometryDomain.ContractVersion + "|" + inputMode + "|" + executionEnvironment + "|" + studyDesign + "|G=model+exterior;M=model+body-edit+interior|" + practiceSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + walkthroughSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + revisionSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + washoutSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + modelScale.ToString("R", CultureInfo.InvariantCulture) + "|" + sampleHz.ToString("R", CultureInfo.InvariantCulture) + "|" + sequence + "|" + rightHandDominant + "|" + desktopSpeed.ToString("R", CultureInfo.InvariantCulture));
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
    public sealed class StudyFlow
    {
        public readonly StudySettings Settings;
        public StudyPhase Phase { get; private set; } = StudyPhase.Setup;
        public int Period { get; private set; }
        public double Remaining { get; private set; }
        public double ActiveElapsed { get; private set; }

        // Track independent pause sources, such as manual pause, tracking loss or faults, in a set.
        // Resume only when every pause reason has been removed.
        readonly HashSet<string> pauses = new HashSet<string>();
        public bool Paused => pauses.Count != 0;
        public string PauseReason => string.Join(";", pauses);
        public StudyCondition Condition => Settings.AssignedCondition;
        public int Scenario => Settings.sequence < 2 ? Period : 1 - Period;
        public bool IsFormal => Phase == StudyPhase.InitialWalkthrough || Phase == StudyPhase.Revision || Phase == StudyPhase.FinalWalkthrough;
        public StudyFlow(StudySettings settings)
        {
            settings.Validate();
            Settings = settings;
        }
        public bool CanEdit(StudyScale scale) => !Paused && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision) && (scale == StudyScale.Model || Condition == StudyCondition.M);
        public bool CanEnterBody => !Paused && (Phase == StudyPhase.InitialWalkthrough || Phase == StudyPhase.FinalWalkthrough || Phase == StudyPhase.Recall || (Condition == StudyCondition.M && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision)));
        public bool CanNavigateSpaces => !Paused && ((Settings.researcherPreview && Phase == StudyPhase.Setup) || Phase == StudyPhase.Practice || Phase == StudyPhase.Revision || Phase == StudyPhase.Recall);
        public bool CanToggleWorkspace => !Paused && (Phase == StudyPhase.Setup || CanNavigateSpaces);
        public void Pause(string reason)
        {
            pauses.Add(reason);
        }
        public void Resume(string reason)
        {
            pauses.Remove(reason);
        }
        public void Begin()
        {
            if (Settings.researcherPreview) throw new InvalidOperationException("Researcher preview cannot start a participant trial.");
            if (Phase != StudyPhase.Setup)
                throw new InvalidOperationException("Session already started.");
            Set(StudyPhase.Practice, Settings.practiceSeconds);
        }
        void Set(StudyPhase phase, double seconds)
        {
            Phase = phase;
            Remaining = seconds;
        }
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
    [Serializable]
    public sealed class StudyCheckpoint
    {
        public string schema = "cmer-checkpoint/0.9";
        public string sessionId;
        public string contractVersion;
        public string configHash;
        public string buildHash;
        public string stateId;
        public string geometryHash;
        public string label;
        public StudySettings settings;
        public StudyPhase phase;
        public StudyScale scale;
        public SemanticAxis selectedAxis;
        public int period;
        public int scenario;
        public int parameterLockMask;
        public long precedingEventId;
        public double remaining;
        public double activeElapsed;
        public SpatialState state;
        public SpatialState[] undo;
        public SpatialState[] redo;
    }
    public static class CheckpointValidation
    {
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
        static bool IsDigest(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$");
        static void ValidateState(int scenario, SpatialState state)
        {
            for (int i = 0; i < 3; i++)
                if (!GeometryDomain.Finite(state[i]) || Math.Abs(state[i]) > 1)
                    throw new ArgumentException("Invalid checkpoint parameter state.");
            if (scenario != 2 && !GeometryDomain.IsLegal(scenario, state))
                throw new ArgumentException("Checkpoint state is outside the geometry domain.");
        }
    }
    public sealed class RevisionHistory
    {
        public SpatialState Current { get; private set; }

        // past stores undoable states; future stores states available after undo for redo.
        readonly List<SpatialState> past = new List<SpatialState>(), future = new List<SpatialState>();
        public SpatialState[] Past => past.ToArray();
        public SpatialState[] Future => future.ToArray();
        public void Reset(SpatialState state)
        {
            Current = state;
            past.Clear();
            future.Clear();
        }
        public bool CanUndo => past.Count > 0;
        public bool CanRedo => future.Count > 0;
        public SpatialState UndoTarget => CanUndo ? past[past.Count - 1] : Current;
        public SpatialState RedoTarget => CanRedo ? future[future.Count - 1] : Current;
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
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static string Id(int scenario, SpatialState state) => Hash(GeometryDomain.ContractVersion + "|" + scenario + "|" + state.p1.ToString("R", CultureInfo.InvariantCulture) + "|" + state.p2.ToString("R", CultureInfo.InvariantCulture) + "|" + state.p3.ToString("R", CultureInfo.InvariantCulture));
    }
    public enum SemanticAxis
    {
        None,
        P1,
        P2,
        P3
    }
    public static class GestureMapping
    {
        public static double FromPalmSeparation(SemanticAxis axis, double baseline, double startSeparation, double currentSeparation, double travelMeters)
        {
            if (axis != SemanticAxis.P1 && axis != SemanticAxis.P2)
                throw new ArgumentException("Palm separation is used for P1 and P2 only.");
            if (!GeometryDomain.Finite(startSeparation) || !GeometryDomain.Finite(currentSeparation) || startSeparation < 0 || currentSeparation < 0)
                throw new ArgumentException("Invalid palm separation.");
            return FromRouteTravel(baseline, currentSeparation - startSeparation, travelMeters);
        }
        public static double FromRouteTravel(double baseline, double signedTravel, double travelMeters)
        {
            if (!GeometryDomain.Finite(baseline) || !GeometryDomain.Finite(signedTravel) || !GeometryDomain.Finite(travelMeters) || travelMeters < .1)
                throw new ArgumentException("Invalid gesture calibration or tracking sample.");
            return Math.Max(-1, Math.Min(1, baseline + signedTravel / travelMeters));
        }
    }
    public static class ModelFraming
    {
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
