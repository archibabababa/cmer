using System;
using System.Collections.Generic;
using ArchSemanticPilot;

public static class CoreRegression
{
    [Serializable]
    public sealed class Report
    {
        public int assertions, geometryStates, framingCases;
        public double maxVolumeError, minPortal = double.MaxValue;
        public string result = "PASS";
        public List<string> checks = new List<string>();
    }

    static Report report;
    static StudyCheckpoint Checkpoint(int sequence = 0, int period = 0)
    {
        var settings = new StudySettings
        {
            sequence = sequence
        };
        int scenario = sequence < 2 ? period : 1 - period;
        return new StudyCheckpoint
        {
            sessionId = "regression",
            settings = settings,
            buildHash = new string ('a', 64),
            contractVersion = GeometryDomain.ContractVersion,
            configHash = settings.ContractHash,
            phase = StudyPhase.Revision,
            scale = StudyScale.Model,
            selectedAxis = SemanticAxis.P1,
            period = period,
            scenario = scenario,
            remaining = 700,
            activeElapsed = 560,
            state = default,
            stateId = StateIdentity.Id(scenario, default),
            geometryHash = new string ('b', 64),
            label = "periodic",
            undo = Array.Empty<SpatialState>(),
            redo = Array.Empty<SpatialState>()
        };
    }

    static void RejectCheckpoint(Action<StudyCheckpoint> corrupt, string reason, bool forRatings = false)
    {
        var checkpoint = Checkpoint();
        corrupt(checkpoint);
        bool rejected = false;
        try
        {
            CheckpointValidation.Validate(checkpoint, new string ('a', 64), forRatings);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Check(rejected, reason);
    }

    static void CheckRecoveryContract()
    {
        for (int seq = 0; seq < 4; seq++)
            for (int period = 0; period < 2; period++)
            {
                var c = Checkpoint(seq, period);
                CheckpointValidation.Validate(c, c.buildHash);
                Check(true, "Every valid fixed-group AB/BA recovery accepted");
                c.scale = StudyScale.Body;
                bool rejected = false;
                try
                {
                    CheckpointValidation.Validate(c, c.buildHash);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }

                Check(rejected == (seq % 2 == 0), "Recovery preserves G/M body permission");
                c.phase = StudyPhase.InitialWalkthrough;
                c.remaining = 60;
                c.label = "initial_locked";
                CheckpointValidation.Validate(c, c.buildHash, true);
                c.phase = StudyPhase.FinalWalkthrough;
                c.label = "final_locked";
                CheckpointValidation.Validate(c, c.buildHash, true);
                Check(true, "Locked initial and final accepted for all sequences");
            }

        RejectCheckpoint(c => c.scenario = 2, "Training checkpoint cannot resume formal revision");
        RejectCheckpoint(c => c.scenario = 1, "Wrong A/B period rejected");
        RejectCheckpoint(c => c.settings.inputMode = StudyInputMode.DesktopRehearsal, "Input-mode change invalidates configuration identity");
        RejectCheckpoint(c => c.configHash = "changed", "Configuration identity required");
        RejectCheckpoint(c => c.buildHash = new string ('c', 64), "Different build rejected");
        RejectCheckpoint(c => c.contractVersion = "older", "Different contract rejected");
        RejectCheckpoint(c => c.stateId = new string ('c', 64), "State identity required");
        RejectCheckpoint(c => c.geometryHash = "", "Geometry identity required");
        RejectCheckpoint(c => c.state = new SpatialState(double.NaN, 0, 0), "Nonfinite state rejected");
        RejectCheckpoint(c => c.undo = new[] { new SpatialState(0, -1, 0) }, "Illegal undo state rejected");
        RejectCheckpoint(c => c.redo = new[] { new SpatialState(0, 0, double.PositiveInfinity) }, "Nonfinite redo state rejected");
        RejectCheckpoint(c => c.remaining = 721, "Phase clock cannot be extended by recovery");
        RejectCheckpoint(c => c.remaining = -1, "Negative clock rejected");
        RejectCheckpoint(c => c.activeElapsed = double.PositiveInfinity, "Nonfinite active clock rejected");
        RejectCheckpoint(c => c.phase = (StudyPhase)999, "Unknown phase rejected");
        RejectCheckpoint(c => c.scale = (StudyScale)999, "Unknown scale rejected");
        RejectCheckpoint(c => c.selectedAxis = (SemanticAxis)999, "Unknown axis rejected");
        RejectCheckpoint(c => c.phase = StudyPhase.Complete, "Completed session cannot resume");
        RejectCheckpoint(c => c.label = "final_locked", "Locked label cannot disguise revision as final", true);
        RejectCheckpoint(c =>
        {
        }, "Periodic state cannot be used for ratings", true);
        RejectCheckpoint(c =>
        {
            c.phase = StudyPhase.InitialWalkthrough;
            c.remaining = 60;
        }, "Initial view must be body scale");
        RejectCheckpoint(c =>
        {
            c.phase = StudyPhase.InitialWalkthrough;
            c.scale = StudyScale.Body;
            c.remaining = 60;
            c.state = new SpatialState(.2, 0, 0);
            c.stateId = StateIdentity.Id(c.scenario, c.state);
        }, "Initial walkthrough must remain neutral");
        RejectCheckpoint(c =>
        {
            c.phase = StudyPhase.InitialWalkthrough;
            c.scale = StudyScale.Body;
            c.remaining = 60;
            c.undo = new[]
            {
                default(SpatialState)
            };
        }, "Initial walkthrough cannot inherit training history");
        RejectCheckpoint(c =>
        {
            c.phase = StudyPhase.Practice;
            c.period = 1;
            c.scenario = 2;
            c.remaining = 480;
        }, "Training cannot occupy second period");
        var practice = Checkpoint();
        practice.phase = StudyPhase.Practice;
        practice.scenario = 2;
        practice.remaining = 480;
        practice.state = new SpatialState(0, -1, 0);
        practice.stateId = StateIdentity.Id(2, practice.state);
        CheckpointValidation.Validate(practice, practice.buildHash);
        Check(true, "Neutral training has its own parameter domain");
        var recall = Checkpoint(0, 1);
        recall.phase = StudyPhase.Recall;
        recall.scenario = 0;
        recall.remaining = 0;
        recall.stateId = StateIdentity.Id(0, recall.state);
        CheckpointValidation.Validate(recall, recall.buildHash);
        Check(true, "Recall can inspect an earlier scenario without altering fixed-group AB/BA order");
        report.checks.Add("Recovery/export identity, all fixed-group AB/BA permissions, phase/scenario clocks, neutral initial state and valid undo/redo states");
    }

    static void Check(bool value, string message)
    {
        report.assertions++;
        if (!value)
            throw new Exception(message);
    }

    public static Report Run()
    {
        report = new Report();
        var previewSettings=new StudySettings { researcherPreview=true, sequence=1 };
        var previewFlow=new StudyFlow(previewSettings);
        Check(!previewSettings.UseTrainingPreview(true),"Researcher try-on uses original building, not practice boxes");
        Check(previewFlow.CanEdit(StudyScale.Model)&&previewFlow.CanEdit(StudyScale.Body)&&previewFlow.CanEnterBody,"Original building preview supports both scales");
        previewFlow.Pause("tracking");Check(!previewFlow.CanEdit(StudyScale.Body),"Preview tracking pause blocks edits");previewFlow.Resume("tracking");
        bool trialRejected=false;try{previewFlow.Begin();}catch(InvalidOperationException){trialRejected=true;}
        Check(trialRejected,"Preview cannot begin participant trial");
        bool recordRejected=false;try{new StudyRecorder("PREVIEW_MUST_NOT_WRITE",previewSettings,new string('a',64));}catch(InvalidOperationException){recordRejected=true;}
        Check(recordRejected&&!System.IO.Directory.Exists("PREVIEW_MUST_NOT_WRITE"),"Preview cannot create participant data");
        RejectCheckpoint(c=>c.settings.researcherPreview=true,"Preview cannot be restored/exported as a participant checkpoint");
        var gPreview=new StudyFlow(new StudySettings{researcherPreview=true,sequence=0});
        Check(gPreview.CanEdit(StudyScale.Model)&&!gPreview.CanEdit(StudyScale.Body)&&!gPreview.CanEnterBody,"G preview cannot bypass body permissions");
        bool legacyRejected=false;try{new StudyFlow(new StudySettings{studyDesign=""});}catch(ArgumentException){legacyRejected=true;}
        Check(legacyRejected,"Old crossover configuration requires explicit reassignment");
        Check(new StudySettings{sequence=0}.RemoteViewType=="ExteriorModel"&&new StudySettings{sequence=1}.RemoteViewType=="InteriorEyeLevel","Remote view exposure recorded by fixed group");
        CheckRecoveryContract();
        for (int seq = 0; seq < 4; seq++)
        {
            var s = new StudySettings
            {
                sequence = seq
            };
            var f = new StudyFlow(s);
            Check(!f.CanEdit(StudyScale.Model), "Setup locked");
            f.Begin();
            Check(f.CanEdit(StudyScale.Body)==(seq%2==1) && f.CanEdit(StudyScale.Model), "Practice respects fixed group");
            f.Pause("manual");
            double t = f.Remaining;
            f.Tick(50);
            Check(f.Remaining == t, "Paused clock");
            f.Pause("tracking");
            f.Resume("manual");
            Check(f.Paused, "Independent pause reasons");
            f.Resume("tracking");
            f.Tick(s.practiceSeconds);
            for (int period = 0; period < 2; period++)
            {
                Check(f.Period == period, "Period order");
                Check(f.Scenario == (seq < 2 ? period : 1 - period), "Scenario order");
                bool g = seq % 2 == 0;
                Check((f.Condition == StudyCondition.G) == g, "Condition order");
                Check(f.Phase == StudyPhase.InitialWalkthrough && !f.CanEdit(StudyScale.Body), "Initial locked");
                f.Tick(s.walkthroughSeconds);
                Check(f.CanEdit(StudyScale.Model), "Model editing");
                Check(f.CanEdit(StudyScale.Body) == !g, "G/M body permission");
                Check(f.CanEnterBody == !g, "G/M body entry");
                t = f.Remaining;
                f.Tick(1.25);
                Check(Math.Abs(t - f.Remaining - 1.25) < 1e-9, "Revision clock advances during interaction");
                f.Tick(s.revisionSeconds);
                Check(f.Phase == StudyPhase.FinalWalkthrough && !f.CanEdit(StudyScale.Model), "Final locked");
                Check(f.Remaining == s.walkthroughSeconds, "Matched initial/final duration");
                f.Tick(s.walkthroughSeconds);
                Check(f.Phase == StudyPhase.Questionnaire, "Questionnaire waits");
                f.Tick(500);
                Check(f.Phase == StudyPhase.Questionnaire, "Questionnaire not timed out");
                f.Advance();
                if (period == 0)
                {
                    Check(f.Phase == StudyPhase.Washout, "Washout order");
                    f.Tick(s.washoutSeconds);
                }
                else
                {
                    Check(f.Phase == StudyPhase.Recall, "Recall order");
                    f.Advance();
                    Check(f.Phase == StudyPhase.Complete, "Complete");
                }
            }
        }

        report.checks.Add("All four fixed-group AB/BA sequences; permissions, clocks, pause reasons and phase locks");
        var quest = new StudySettings();
        Check(quest.inputMode == StudyInputMode.QuestHands, "Default input is explicit Quest hands");
        Check(!quest.CanUseDesktopInput(false) && !quest.CanUseDesktopInput(true), "Quest session rejects keyboard geometry input with or without XR");
        Check(!quest.HasRequiredTracking(false, true, true), "XR loss must pause a Quest session");
        Check(!quest.HasRequiredTracking(true, false, true) && !quest.HasRequiredTracking(true, true, false), "Both tracked hands required");
        Check(quest.HasRequiredTracking(true, true, true), "Quest tracking readiness");
        var rehearsal = new StudySettings
        {
            inputMode = StudyInputMode.DesktopRehearsal
        };
        Check(rehearsal.CanUseDesktopInput(false) && !rehearsal.CanUseDesktopInput(true), "Desktop rehearsal cannot silently become an XR session");
        Check(rehearsal.HasRequiredTracking(false, false, false), "Desktop rehearsal needs no tracked hands");
        Check(quest.UseTrainingPreview(false) && quest.UseTrainingPreview(true), "Quest setup never exposes a task building");
        Check(!rehearsal.UseTrainingPreview(false) && rehearsal.UseTrainingPreview(true), "Task preview is desktop-only, including mismatched input configuration");
        Check(quest.ContractHash != rehearsal.ContractHash, "Input mode included in frozen protocol identity");
        bool badModeRejected = false;
        try
        {
            new StudySettings
            {
                inputMode = (StudyInputMode)99
            }.Validate();
        }
        catch (ArgumentException)
        {
            badModeRejected = true;
        }

        Check(badModeRejected, "Unknown input mode rejected");
        foreach (var axis in new[]
        {
            SemanticAxis.P1,
            SemanticAxis.P2
        }

        )
        {
            Check(Math.Abs(GestureMapping.FromPalmSeparation(axis, .1, .4, .55, .3) - .6) < 1e-9, "Spreading increases P1/P2");
            Check(Math.Abs(GestureMapping.FromPalmSeparation(axis, .1, .4, .25, .3) + .4) < 1e-9, "Closing decreases P1/P2");
            Check(GestureMapping.FromPalmSeparation(axis, .2, .4, .4, .3) == .2, "Rebase produces no jump");
            Check(GestureMapping.FromPalmSeparation(axis, .8, .3, .8, .3) == 1, "Requested gesture endpoint clamped");
        }

        Check(Math.Abs(GestureMapping.FromRouteTravel(-.2, .225, .45) - .3) < 1e-9, "P3 signed route travel");
        Check(GestureMapping.FromRouteTravel(-.8, -.45, .45) == -1, "P3 negative endpoint");
        bool invalidGestureRejected = false;
        try
        {
            GestureMapping.FromRouteTravel(0, double.NaN, .45);
        }
        catch (ArgumentException)
        {
            invalidGestureRejected = true;
        }

        Check(invalidGestureRejected, "Nonfinite hand sample rejected");
        report.checks.Add("Quest/desktop input separation, XR loss, frozen mode identity, P1/P2 spread-positive mapping, P3 and rebase");
        var h = new RevisionHistory();
        var a = new SpatialState(.2, 0, 0);
        var b = new SpatialState(.2, .3, 0);
        h.Reset(default);
        h.Accept(a, "edit");
        h.Accept(b, "edit");
        int entries = h.Past.Length;
        h.Accept(b, "edit");
        Check(h.Past.Length == entries, "No-op does not add a revision");
        h.Accept(h.UndoTarget, "undo");
        Check(h.Current.CloseTo(a) && h.CanRedo, "Undo exact");
        h.Accept(h.RedoTarget, "redo");
        Check(h.Current.CloseTo(b), "Redo exact");
        h.Accept(h.UndoTarget, "undo");
        h.Accept(new SpatialState(.1, 0, 0), "edit");
        Check(!h.CanRedo, "Branch clears redo");
        var restored = new RevisionHistory();
        restored.Restore(h.Current, h.Past, h.Future);
        Check(restored.Current.CloseTo(h.Current) && restored.Past.Length == h.Past.Length, "History checkpoint recovery");
        report.checks.Add("Exact undo / redo, branch and recovery history");
        var random = new Random(93017);
        for (int scenario = 0; scenario < 2; scenario++)
        {
            SpatialState p = default;
            Check(GeometryDomain.IsLegal(scenario, p), "Neutral legal");
            for (int n = 0; n < 150; n++)
            {
                int axis = n % 3;
                double wanted = n < 18 ? (n % 2 == 0 ? -1 : 1) : random.NextDouble() * 2 - 1;
                string reason;
                var q = GeometryDomain.SolveActive(scenario, p, axis, wanted, out reason);
                Check(GeometryDomain.IsLegal(scenario, q), "Effective state legal");
                for (int j = 0; j < 3; j++)
                    if (j != axis)
                        Check(q[j] == p[j], "Inactive axis changed");
                var g = LiveGeometryKernel.Evaluate(scenario, q.p1, q.p2, q.p3);
                var m = GeometryValidation.Measure(g);
                report.geometryStates++;
                if (n == 0 || n == 149)
                    CheckFraming(g);
                Check(m.primaryRouteWidth >= 1.2 && m.primaryRouteCells > 0, "Primary passage F/R/G");
                Check(m.minimumPortal >= 2 - 1e-7, "Final portal width");
                Check(m.volumeError < .001, "Post-taper volume");
                Check(m.rooms.Length == 4 && m.triangles > 0, "Geometry present");
                report.maxVolumeError = Math.Max(report.maxVolumeError, m.volumeError);
                report.minPortal = Math.Min(report.minPortal, m.minimumPortal);
                p = q;
            }

            string r;
            var one = GeometryDomain.SolveActive(scenario, default, 0, -1, out r);
            var two = GeometryDomain.SolveActive(scenario, one, 1, -1, out r);
            Check(one.p1 == two.p1 && two.p3 == 0, "Original radial projection regression");
            var neutral = GeometryValidation.Measure(LiveGeometryKernel.Evaluate(scenario, 0, 0, 0));
            var again = GeometryValidation.Measure(LiveGeometryKernel.Evaluate(scenario, 0, 0, 0));
            Check(neutral.geometryHash == again.geometryHash, "Deterministic mesh identity");
            CheckFraming(LiveGeometryKernel.Evaluate(scenario, 0, 0, 0));
        }

        bool rejected = false;
        try
        {
            string r;
            GeometryDomain.SolveActive(0, default, 0, double.NaN, out r);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Check(rejected, "NaN rejected");
        report.checks.Add("300 generated A/B states; active-axis preservation, final mesh volume/portals and connected 1.2m ground route, identity and finite values");
        var practice = GeometryValidation.Measure(PracticeGeometry.Evaluate(default), true);
        Check(practice.objects == 18, "Independent practice scene");
        CheckFraming(PracticeGeometry.Evaluate(default));
        bool invalidViewport = false;
        try
        {
            ModelFraming.Distance(48, 0, new double[24]);
        }
        catch (ArgumentException)
        {
            invalidViewport = true;
        }

        Check(invalidViewport, "Degenerate viewport rejected");
        report.checks.Add("35 perspective-framing cases using actual A/B/practice bounds; five viewport aspect ratios, corners inside padded frustum and in front of near plane");
        return report;
    }

    static void CheckFraming(LiveGeometryKernel.Result geometry)
    {
        double[] low =
        {
            double.MaxValue,
            double.MaxValue,
            double.MaxValue
        }, high =
        {
            double.MinValue,
            double.MinValue,
            double.MinValue
        };
        foreach (var item in geometry.solids)
            foreach (var vertex in ((ArchSemanticGeometry.Solid)item).Vertices)
            {
                double[] v =
                {
                    vertex.Location.X,
                    vertex.Location.Z,
                    vertex.Location.Y
                };
                for (int j = 0; j < 3; j++)
                {
                    low[j] = Math.Min(low[j], v[j]);
                    high[j] = Math.Max(high[j], v[j]);
                }
            }

        double pitch = 28 * Math.PI / 180, yaw = -24 * Math.PI / 180;
        var corners = new double[24];
        int k = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    double px = x * (high[0] - low[0]) * .0125, py = y * (high[1] - low[1]) * .0125, pz = z * (high[2] - low[2]) * .0125;
                    double rx = Math.Cos(yaw) * px - Math.Sin(yaw) * pz, rz = Math.Sin(yaw) * px + Math.Cos(yaw) * pz;
                    corners[k++] = rx;
                    corners[k++] = Math.Cos(pitch) * py + Math.Sin(pitch) * rz;
                    corners[k++] = -Math.Sin(pitch) * py + Math.Cos(pitch) * rz;
                }

        foreach (double aspect in new[]
        {
            .4,
            .8,
            1,
            16.0 / 9,
            2.4
        }

        )
        {
            double distance = ModelFraming.Distance(48, aspect, corners);
            double tan = Math.Tan(48 * Math.PI / 360);
            report.framingCases++;
            for (int i = 0; i < 24; i += 3)
            {
                double depth = distance + corners[i + 2];
                Check(depth > .005, "Model in front of desktop near plane");
                Check(Math.Abs(corners[i] / (depth * tan * aspect)) <= 1 / 1.15 + 1e-9, "Model horizontal padding retained");
                Check(Math.Abs(corners[i + 1] / (depth * tan)) <= 1 / 1.15 + 1e-9, "Model vertical padding retained");
            }
        }
    }
}
