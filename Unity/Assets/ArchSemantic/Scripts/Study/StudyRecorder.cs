using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ArchSemanticPilot
{
    [Serializable]
    public sealed class StudyEvent
    {
        public string schema = "cmer-event/0.9";
        public string sessionId;
        public string participantId;
        public string contractVersion;
        public string configHash;
        public string buildHash;
        public string inputMode;
        public string executionEnvironment;
        public string utc;
        public string phase;
        public string condition;
        public string scenario;
        public string scale;
        public string zone;
        public string eventType;
        public string operationId;
        public string activeParameter;
        public string previousStateId;
        public string stateId;
        public string geometryHash;
        public string projection;
        public string fault;
        public string detail;
        public string displayedGeometryHash;
        public string criticalRegion;
        public string occupiedFloor;
        public string studyDesign;
        public string assignedGroup;
        public string remoteViewType;
        public long eventId;
        public double elapsedSeconds;
        public double activeSeconds;
        public double remainingSeconds;
        public int period;
        public int geometryRevision;
        public int parameterLockMask;
        public bool accepted;
        public bool practice;
        public bool leftTracked;
        public bool rightTracked;
        public SpatialState requested;
        public SpatialState effective;
        public SpatialState previous;
        public SpatialState displayedState;
        public Vector3 geometryPosition, geometryScale;
        public Quaternion geometryRotation;
        public Vector3 headPosition, leftPosition, rightPosition;
        public Quaternion headRotation;
    }
    [Serializable]
    public sealed class SealedFile
    {
        public string file;
        public string sha256;
        public long bytes;
    }
    [Serializable]
    public sealed class SealedManifest
    {
        public string schema = "cmer-seal/0.9";
        public string sessionId;
        public string utc;
        public string status;
        public SealedFile[] files;
    }
    public sealed class StudyRecorder : IDisposable
    {
        public string DirectoryPath { get; private set; }
        public string SessionId { get; private set; }
        public long EventCount { get; private set; }
        public string BuildHash => buildHash;
        public string ConfigHash => settings.ContractHash;

        // Internal timer, fixed settings and build identity.
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly StudySettings settings;
        readonly string buildHash;

        // Writers for the JSONL event stream and CSV summary.
        StreamWriter writer, summary;
        // Event-relative time of the last flush, controlling routine flush frequency.
        double lastFlush;
        // Finalization flag preventing repeated closure or manifest generation.
        bool sealedSession;
        public StudyRecorder(string parent, StudySettings settings, string buildHash)
        {
            if (settings.researcherPreview) throw new InvalidOperationException("Researcher preview does not create participant records.");
            this.settings = settings;
            this.buildHash = buildHash;
            SessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            DirectoryPath = Path.Combine(parent, SessionId);
            Directory.CreateDirectory(DirectoryPath);
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "states"));
            writer = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            summary = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "events.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            summary.WriteLine("event_id,elapsed_s,participant,period,scenario,condition,phase,scale,event,operation,state_id,previous_state_id,p1,p2,p3,accepted,input_mode,execution_environment,occupied_floor,study_design,assigned_group,remote_view_type");
            WriteNew("settings.json", JsonUtility.ToJson(settings, true));
        }
        public void Append(StudyEvent e, bool durable = true)
        {
            if (writer == null)
                throw new IOException("The event recorder is closed.");
            e.studyDesign = settings.studyDesign;
            e.assignedGroup = settings.AssignedCondition.ToString();
            e.remoteViewType = settings.RemoteViewType;
            e.inputMode = settings.inputMode.ToString();
            e.executionEnvironment = settings.executionEnvironment;
            e.sessionId = SessionId;
            e.participantId = settings.participantId;
            e.contractVersion = GeometryDomain.ContractVersion;
            e.configHash = settings.ContractHash;
            e.buildHash = buildHash;
            e.eventId = ++EventCount;
            e.elapsedSeconds = clock.Elapsed.TotalSeconds;
            e.utc = DateTime.UtcNow.ToString("o");
            writer.WriteLine(JsonUtility.ToJson(e));
            summary.WriteLine(string.Join(",", new[] { e.eventId.ToString(), e.elapsedSeconds.ToString("F6", CultureInfo.InvariantCulture), settings.participantId, e.period.ToString(), e.scenario, e.condition, e.phase, e.scale, e.eventType, e.operationId, e.stateId, e.previousStateId, e.effective.p1.ToString("R", CultureInfo.InvariantCulture), e.effective.p2.ToString("R", CultureInfo.InvariantCulture), e.effective.p3.ToString("R", CultureInfo.InvariantCulture), e.accepted ? "1" : "0", e.inputMode, e.executionEnvironment, e.occupiedFloor, e.studyDesign, e.assignedGroup, e.remoteViewType }.Select(Csv)));
            if (durable || e.elapsedSeconds - lastFlush >= 1)
            {
                Flush();
                lastFlush = e.elapsedSeconds;
            }
        }
        static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        public string SaveCheckpoint(StudyCheckpoint checkpoint)
        {
            checkpoint.buildHash = buildHash;
            checkpoint.sessionId = SessionId;
            checkpoint.contractVersion = GeometryDomain.ContractVersion;
            checkpoint.configHash = settings.ContractHash;
            checkpoint.settings = settings;
            checkpoint.precedingEventId = EventCount;
            string name = "states/" + EventCount.ToString("D8") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
            WriteNew(name, JsonUtility.ToJson(checkpoint, true));
            return name;
        }
        public void WriteNew(string relative, string contents)
        {
            string target = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
            if (!target.StartsWith(Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Output path is outside the session.");
            using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var text = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                text.Write(contents);
                text.Flush();
                stream.Flush(true);
            }
        }
        public void Flush()
        {
            writer?.Flush();
            summary?.Flush();
            (writer?.BaseStream as FileStream)?.Flush(true);
            (summary?.BaseStream as FileStream)?.Flush(true);
        }
        public void Seal(string status)
        {
            if (sealedSession)
                return;
            Flush();
            writer?.Dispose();
            summary?.Dispose();
            writer = null;
            summary = null;
            var list = new List<SealedFile>();
            foreach (string path in Directory.GetFiles(DirectoryPath, "*", SearchOption.AllDirectories).OrderBy(p => p))
                list.Add(new SealedFile { file = path.Substring(DirectoryPath.Length + 1).Replace('\\', '/'), sha256 = FileHash(path), bytes = new FileInfo(path).Length });
            WriteNew("seal.json", JsonUtility.ToJson(new SealedManifest { sessionId = SessionId, utc = DateTime.UtcNow.ToString("o"), status = status, files = list.ToArray() }, true));
            sealedSession = true;
        }
        public static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static IEnumerable<string> ReadEvents(string path)
        {
            // Recall may read while the recorder is open, so the read stream must share read/write access.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                while (!reader.EndOfStream) yield return reader.ReadLine();
        }
        public void Dispose()
        {
            if (!sealedSession)
                Seal("interrupted");
        }
    }
    [Serializable]
    public sealed class RecallSegment
    {
        public string sessionId;
        public string checkpointFile;
        public string checkpointSha256;
        public long throughEventId;
        public StudyEvent[] events;
        public StudyCheckpoint[] states;
    }
    [Serializable]
    public sealed class RecallArchive
    {
        public string schema = "cmer-recall-lineage/1";
        public List<RecallSegment> segments = new List<RecallSegment>();
        public static void Attach(StudyRecorder destination, string checkpointPath, StudyCheckpoint checkpoint)
        {
            // Require the checkpoint to reside in its original session's states directory to avoid mixing unrelated histories.
            string path = Path.GetFullPath(checkpointPath);
            var statesDirectory = Directory.GetParent(path);
            var source = statesDirectory?.Parent;
            if (source == null || statesDirectory.Name != "states" || source.Name != checkpoint.sessionId)
                throw new InvalidDataException("Recovery must use a checkpoint inside its original session/states folder.");
            var archive = Read(source.FullName, checkpoint.buildHash, checkpoint.configHash);
            if (archive.segments.Any(s => s.sessionId == checkpoint.sessionId))
                throw new InvalidDataException("Cyclic recovery lineage.");
            // The accepted_candidate confirmation follows precedingEventId, so include that additional event in the cutoff.
            long cutoff = checkpoint.precedingEventId + (checkpoint.label == "accepted_candidate" ? 1 : 0);
            var segment = ReadSegment(source.FullName, cutoff, checkpoint.buildHash, checkpoint.configHash);
            if (checkpoint.label == "accepted_candidate" && !segment.events.Any(e =>
                e.eventId == cutoff && e.accepted && e.eventType == "commit_accepted" &&
                e.stateId == checkpoint.stateId && e.geometryHash == checkpoint.geometryHash &&
                (e.detail ?? "").EndsWith("|states/" + Path.GetFileName(path), StringComparison.Ordinal)))
                throw new InvalidDataException("No matching durable acknowledgement at the recovery boundary.");
            segment.checkpointFile = Path.GetFileName(path);
            segment.checkpointSha256 = StudyRecorder.FileHash(path);
            archive.segments.Add(segment);
            destination.WriteNew("recall_history.json", JsonUtility.ToJson(archive, true));
        }
        public static RecallArchive Read(string directory, string build, string config)
        {
            string path = Path.Combine(directory, "recall_history.json");
            if (!File.Exists(path)) return new RecallArchive();
            var archive = JsonUtility.FromJson<RecallArchive>(File.ReadAllText(path));
            if (archive == null || archive.schema != "cmer-recall-lineage/1" || archive.segments == null)
                throw new InvalidDataException("Invalid recall lineage.");
            // Require unique source sessions and validate event/checkpoint identities and ordering within each.
            var seen = new HashSet<string>();
            foreach (var segment in archive.segments)
            {
                if (segment == null || !seen.Add(segment.sessionId) || segment.events == null || segment.states == null)
                    throw new InvalidDataException("Invalid or duplicate recall segment.");
                long previous = 0;
                foreach (var e in segment.events)
                {
                    ValidateEvent(e, segment.sessionId, build, config);
                    if (e.eventId <= previous || e.eventId > segment.throughEventId || !IsCueEvent(e))
                        throw new InvalidDataException("Invalid recall event ordering.");
                    previous = e.eventId;
                }
                foreach (var c in segment.states)
                {
                    CheckpointValidation.Validate(c, build);
                    if (c.sessionId != segment.sessionId || c.configHash != config || c.precedingEventId > segment.throughEventId)
                        throw new InvalidDataException("Recall state does not belong to the accepted prefix.");
                }
            }
            return archive;
        }
        public static RecallSegment ReadSegment(string directory, long cutoff, string build, string config)
        {
            if (cutoff < 0) throw new InvalidDataException("Invalid recovery event boundary.");
            string session = new DirectoryInfo(directory).Name;
            var events = new List<StudyEvent>();
            long previous = 0;
            foreach (string line in StudyRecorder.ReadEvents(Path.Combine(directory, "events.jsonl")))
            {
                if (previous == cutoff) break; // Ignore logs beyond the recovery boundary, including potentially partial discarded tails.
                if (string.IsNullOrWhiteSpace(line)) continue;
                var e = JsonUtility.FromJson<StudyEvent>(line);
                ValidateEvent(e, session, build, config);
                if (e.eventId != previous + 1) throw new InvalidDataException("Missing or reordered recovery event.");
                previous = e.eventId;
                if (IsCueEvent(e)) events.Add(e);
            }
            if (previous != cutoff) throw new InvalidDataException("Recovery log ends before the checkpoint.");
            // Include candidate checkpoints only when an accepted-commit confirmation event exists.
            var acknowledged = new HashSet<string>(events.Where(e => e.eventType == "commit_accepted" && e.accepted)
                .Select(e => (e.detail ?? "").Split('|').Last()), StringComparer.Ordinal);
            var states = new List<StudyCheckpoint>();
            foreach (string path in Directory.GetFiles(Path.Combine(directory, "states"), "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                // SaveCheckpoint prefixes filenames with event IDs; do not parse potentially corrupted future checkpoints beyond the recovery boundary.
                string prefix = Path.GetFileName(path).Split('_')[0];
                if (!long.TryParse(prefix, out long eventId)) throw new InvalidDataException("Invalid checkpoint filename.");
                if (eventId > cutoff) continue;
                var c = JsonUtility.FromJson<StudyCheckpoint>(File.ReadAllText(path));
                CheckpointValidation.Validate(c, build);
                if (c.sessionId != session || c.configHash != config || c.precedingEventId != eventId)
                    throw new InvalidDataException("Checkpoint session/configuration/event boundary mismatch.");
                if (c.scenario > 1 || c.label == "accepted_candidate" && !acknowledged.Contains("states/" + Path.GetFileName(path))) continue;
                states.Add(c);
            }
            return new RecallSegment { sessionId = session, throughEventId = cutoff, events = events.ToArray(), states = states.ToArray() };
        }
        static bool IsCueEvent(StudyEvent e) => e.phase == "Revision" && !e.practice &&
            (e.eventType == "commit_accepted" || e.eventType == "scale_changed");
        static void ValidateEvent(StudyEvent e, string session, string build, string config)
        {
            if (e == null || e.eventId <= 0 || e.sessionId != session || e.buildHash != build ||
                e.configHash != config || e.contractVersion != GeometryDomain.ContractVersion)
                throw new InvalidDataException("Recall event identity mismatch.");
        }
    }
    public sealed class StudyRecall
    {
        // states stores replay checkpoints; cueIds/cueSessions identify their triggering source events.
        readonly List<StudyCheckpoint> states = new List<StudyCheckpoint>();
        readonly List<long> cueIds = new List<long>();
        readonly List<string> cueSessions = new List<string>();
        public int Count => states.Count;
        public int Index { get; private set; } = -1;
        public long CueId => cueIds[Index];
        public string CueSession => cueSessions[Index];
        public void Load(StudyRecorder recorder)
        {
            // Clear previous results and rebuild cues from the latest flushed events and recovery lineage.
            states.Clear();
            cueIds.Clear();
            cueSessions.Clear();
            Index = -1;
            recorder.Flush();
            var archive = RecallArchive.Read(recorder.DirectoryPath, recorder.BuildHash, recorder.ConfigHash);
            archive.segments.Add(RecallArchive.ReadSegment(recorder.DirectoryPath, recorder.EventCount, recorder.BuildHash, recorder.ConfigHash));
            var events = archive.segments.SelectMany(s => s.events).ToList();

            // Per period, choose the first accepted edit, largest parameter change, first undo and participant scale changes as cues.
            var cues = new SortedDictionary<long, StudyEvent>();
            foreach (int period in new[]
            {
                1,
                2
            }

            )
            {
                var edits = events.Where(e => e.period == period && e.eventType == "commit_accepted" && e.accepted).ToArray();
                if (edits.Length > 0)
                {
                    cues[events.IndexOf(edits[0])] = edits[0];
                    var largest = edits.OrderByDescending(e => Math.Abs(e.effective.p1 - e.previous.p1) + Math.Abs(e.effective.p2 - e.previous.p2) + Math.Abs(e.effective.p3 - e.previous.p3)).First();
                    cues[events.IndexOf(largest)] = largest;
                }

                var undo = edits.FirstOrDefault(e => (e.detail ?? "").StartsWith("undo|", StringComparison.Ordinal));
                if (undo != null)
                    cues[events.IndexOf(undo)] = undo;
                var scale = events.FirstOrDefault(e => e.period == period && e.eventType == "scale_changed" && (e.detail ?? "").EndsWith("|participant", StringComparison.Ordinal));
                if (scale != null)
                    cues[events.IndexOf(scale)] = scale;
            }

            // Index checkpoints by source session and state ID to resolve cue before/after states.
            var checkpoints = new Dictionary<string, StudyCheckpoint>();
            foreach (var segment in archive.segments)
            foreach (var c in segment.states)
            {
                string key = c.sessionId + ":" + c.stateId;
                if (!checkpoints.ContainsKey(key)) checkpoints.Add(key, c);
            }

            foreach (var e in cues.Values)
                foreach (string id in new[]
                {
                    e.previousStateId,
                    e.stateId
                }

                )
                    if (!string.IsNullOrEmpty(id) && checkpoints.TryGetValue(e.sessionId + ":" + id, out var c) && (Count == 0 || states[Count - 1].stateId != id))
                    {
                        states.Add(c);
                        cueIds.Add(e.eventId);
                        cueSessions.Add(e.sessionId);
                    }
        }
        public StudyCheckpoint Next(int direction)
        {
            if (Count == 0)
                throw new InvalidOperationException("No recall cues.");
            int step = direction < 0 ? -1 : 1;
            Index = Index < 0 ? (step > 0 ? 0 : Count - 1) : (Index + step + Count) % Count;
            return states[Index];
        }
    }
}
