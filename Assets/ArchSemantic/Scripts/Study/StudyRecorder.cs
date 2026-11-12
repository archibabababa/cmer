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
    /// <summary>
    /// Study event containing identity, session state, parameters, geometry, observation position and tracking data.
    /// Serialize with Unity JsonUtility and append one line to events.jsonl.
    /// </summary>
    [Serializable]
    public sealed class StudyEvent
    {
        /// <summary>Event format version.</summary>
        public string schema = "cmer-event/0.9";
        /// <summary>Owning session's unique identifier.</summary>
        public string sessionId;
        /// <summary>Anonymous participant identifier.</summary>
        public string participantId;
        /// <summary>Geometry contract version when the event was generated.</summary>
        public string contractVersion;
        /// <summary>Session configuration contract hash.</summary>
        public string configHash;
        /// <summary>Application build identity hash.</summary>
        public string buildHash;
        /// <summary>Input mode name, such as QuestHands or DesktopRehearsal.</summary>
        public string inputMode;
        /// <summary>Current execution environment identifier.</summary>
        public string executionEnvironment;

        /// <summary>UTC ISO 8601 event timestamp.</summary>
        public string utc;
        /// <summary>Study phase name at the time of the event.</summary>
        public string phase;
        /// <summary>Current condition name (G or M).</summary>
        public string condition;
        /// <summary>Current building or scenario identifier.</summary>
        public string scenario;
        /// <summary>Current display scale name.</summary>
        public string scale;
        /// <summary>Occupied semantic zone when the event occurred.</summary>
        public string zone;
        /// <summary>Event type, such as observation, parameter commit, navigation or pause.</summary>
        public string eventType;
        /// <summary>Unique identifier of the associated gesture or operation transaction.</summary>
        public string operationId;
        /// <summary>Selected parameter axis when the event occurred.</summary>
        public string activeParameter;
        /// <summary>Identifier of the state before the operation.</summary>
        public string previousStateId;
        /// <summary>Identifier of the state represented by this event.</summary>
        public string stateId;
        /// <summary>Hash of committed geometry.</summary>
        public string geometryHash;
        /// <summary>Description of projection or constraint handling for the requested state.</summary>
        public string projection;
        /// <summary>Fault-related description.</summary>
        public string fault;
        /// <summary>Additional details, such as action reason, phase information or sampling diagnostics.</summary>
        public string detail;
        /// <summary>Hash of the geometry currently displayed.</summary>
        public string displayedGeometryHash;
        /// <summary>Key-zone identifier associated with this event.</summary>
        public string criticalRegion;

        /// <summary>Occupied floor identifier at body scale.</summary>
        public string occupiedFloor;
        /// <summary>Study design version identifier.</summary>
        public string studyDesign;
        /// <summary>Assigned study group name.</summary>
        public string assignedGroup;
        /// <summary>Remote preview view type for the current condition.</summary>
        public string remoteViewType;

        /// <summary>Increasing event sequence number within the session.</summary>
        public long eventId;
        /// <summary>Total elapsed seconds since recorder startup.</summary>
        public double elapsedSeconds;
        /// <summary>Accumulated active session time in seconds.</summary>
        public double activeSeconds;
        /// <summary>Remaining phase time in seconds.</summary>
        public double remainingSeconds;

        /// <summary>Study period index.</summary>
        public int period;
        /// <summary>Revision number of the displayed geometry.</summary>
        public int geometryRevision;
        /// <summary>P1/P2/P3 lock bitmask.</summary>
        public int parameterLockMask;

        /// <summary>Whether this event represents an accepted formal commit.</summary>
        public bool accepted;
        /// <summary>Whether the event occurred in practice geometry.</summary>
        public bool practice;
        /// <summary>Whether the left hand was tracked at sampling time.</summary>
        public bool leftTracked;
        /// <summary>Whether the right hand was tracked at sampling time.</summary>
        public bool rightTracked;

        /// <summary>Requested parameter state.</summary>
        public SpatialState requested;
        /// <summary>Effective parameter state after geometry constraints.</summary>
        public SpatialState effective;
        /// <summary>Parameter state before the operation.</summary>
        public SpatialState previous;
        /// <summary>Parameter state represented by displayed geometry.</summary>
        public SpatialState displayedState;

        /// <summary>World-space position and scale of the building geometry object.</summary>
        public Vector3 geometryPosition, geometryScale;
        /// <summary>World-space rotation of the building geometry object.</summary>
        public Quaternion geometryRotation;
        /// <summary>World-space head, left-hand and right-hand positions.</summary>
        public Vector3 headPosition, leftPosition, rightPosition;
        /// <summary>World-space head camera rotation.</summary>
        public Quaternion headRotation;
    }

    /// <summary>Relative path, SHA-256 digest and byte count of one sealed file.</summary>
    [Serializable]
    public sealed class SealedFile
    {
        /// <summary>File path relative to the session directory.</summary>
        public string file;
        /// <summary>SHA-256 digest of file contents.</summary>
        public string sha256;
        /// <summary>File size in bytes.</summary>
        public long bytes;
    }

    /// <summary>Session seal manifest recording finalization status and file integrity.</summary>
    [Serializable]
    public sealed class SealedManifest
    {
        /// <summary>Seal manifest format version.</summary>
        public string schema = "cmer-seal/0.9";
        /// <summary>Unique identifier of the sealed session.</summary>
        public string sessionId;
        /// <summary>UTC ISO 8601 seal timestamp.</summary>
        public string utc;
        /// <summary>Seal reason or outcome, such as complete, interrupted or technical_failure.</summary>
        public string status;
        /// <summary>Session file records included in the seal.</summary>
        public SealedFile[] files;
    }

    /// <summary>
    /// Study recorder: create session directories and write JSONL/CSV events, settings and checkpoints,
    /// then finalize a manifest containing file hashes.
    /// </summary>
    public sealed class StudyRecorder : IDisposable
    {
        /// <summary>Full local path of the current session directory.</summary>
        public string DirectoryPath { get; private set; }
        /// <summary>Current session's unique identifier.</summary>
        public string SessionId { get; private set; }
        /// <summary>Number of appended events, also the latest event sequence number.</summary>
        public long EventCount { get; private set; }
        /// <summary>Application build hash for the session.</summary>
        public string BuildHash => buildHash;
        /// <summary>Configuration contract hash derived from session settings.</summary>
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

        /// <summary>
        /// Create a participant session directory and data files and write a settings snapshot.
        /// Researcher preview must not create formal participant records.
        /// </summary>
        /// <param name="parent">Parent data directory, normally under Application.persistentDataPath.</param>
        /// <param name="settings">Validated settings for this session.</param>
        /// <param name="buildHash">Current application build identity hash.</param>
        /// <exception cref="InvalidOperationException">Thrown when researcher preview is enabled.</exception>
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

        /// <summary>
        /// Fill event identity and timing metadata, then append to JSONL and CSV.
        /// Flush immediately when durable is true; otherwise flush approximately once per second at most.
        /// </summary>
        /// <param name="e">Event to write; session, configuration, build and timing fields are populated by this method.</param>
        /// <param name="durable">Whether this write must be flushed durably immediately; defaults to true.</param>
        /// <exception cref="IOException">Thrown when the recorder is closed and cannot accept more writes.</exception>
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

        /// <summary>Encode a CSV field with surrounding double quotes and escaped internal quotes.</summary>
        /// <param name="value">Value to encode; null is treated as an empty string.</param>
        /// <returns>CSV field text with valid quote escaping.</returns>
        static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

        /// <summary>Populate checkpoint identity and save a separate JSON file in the states subdirectory.</summary>
        /// <param name="checkpoint">Checkpoint to save.</param>
        /// <returns>Checkpoint path relative to the session directory.</returns>
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

        /// <summary>
        /// Create and write a new text file inside the session directory without overwriting existing files.
        /// Validate that the normalized destination remains inside the session directory.
        /// </summary>
        /// <param name="relative">Destination path relative to the session directory.</param>
        /// <param name="contents">Complete text to write.</param>
        /// <exception cref="IOException">Thrown when the path escapes the session directory or the file cannot be created.</exception>
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

        /// <summary>Flush JSONL/CSV buffers and request durable file-stream writes.</summary>
        public void Flush()
        {
            writer?.Flush();
            summary?.Flush();
            (writer?.BaseStream as FileStream)?.Flush(true);
            (summary?.BaseStream as FileStream)?.Flush(true);
        }

        /// <summary>
        /// Seal the session: flush and close event files, hash existing session files with SHA-256,
        /// then write seal.json. Repeated calls have no further effect.
        /// </summary>
        /// <param name="status">Seal reason or session outcome.</param>
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

        /// <summary>Compute the SHA-256 digest of a file.</summary>
        /// <param name="path">Path of the file to hash.</param>
        /// <returns>64-character lowercase hexadecimal SHA-256 digest.</returns>
        public static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Read JSONL events line by line while allowing the recorder to keep the file open for appending.</summary>
        /// <param name="path">Path to events.jsonl.</param>
        /// <returns>Raw event lines in file order.</returns>
        public static IEnumerable<string> ReadEvents(string path)
        {
            // Recall may read while the recorder is open, so the read stream must share read/write access.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                while (!reader.EndOfStream) yield return reader.ReadLine();
        }

        /// <summary>Dispose of recorder resources; automatically seal as interrupted if not already finalized.</summary>
        public void Dispose()
        {
            if (!sealedSession)
                Seal("interrupted");
        }
    }

    /// <summary>Valid recall segment retained during recovery, including an event prefix and matching checkpoints.</summary>
    [Serializable]
    public sealed class RecallSegment
    {
        /// <summary>Unique identifier of the source session.</summary>
        public string sessionId;
        /// <summary>Source checkpoint filename defining the segment cutoff.</summary>
        public string checkpointFile;
        /// <summary>SHA-256 digest of the source checkpoint file.</summary>
        public string checkpointSha256;
        /// <summary>Highest event sequence number included in this segment.</summary>
        public long throughEventId;
        /// <summary>Events within the recovery boundary available as recall cues.</summary>
        public StudyEvent[] events;
        /// <summary>Checkpoints matching cue states within the recovery boundary.</summary>
        public StudyCheckpoint[] states;
    }

    /// <summary>
    /// Recall archive retaining valid source-history prefixes as session segments,
    /// without rewriting original records or including discarded events beyond the recovery branch boundary.
    /// </summary>
    [Serializable]
    public sealed class RecallArchive
    {
        /// <summary>Recall archive format version.</summary>
        public string schema = "cmer-recall-lineage/1";
        /// <summary>Session segments forming the recall lineage.</summary>
        public List<RecallSegment> segments = new List<RecallSegment>();

        /// <summary>
        /// Append a snapshot of the actual recovered source-session prefix to the destination recall archive.
        /// Leave the source session unchanged and exclude post-boundary branch edits from the new recall history.
        /// </summary>
        /// <param name="destination">Destination recorder used to write recall_history.json.</param>
        /// <param name="checkpointPath">Full source checkpoint path.</param>
        /// <param name="checkpoint">Loaded and validated source checkpoint.</param>
        /// <exception cref="InvalidDataException">Thrown when source path, lineage or accepted-candidate confirmation does not match the checkpoint.</exception>
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

        /// <summary>Read and validate a recall archive, returning an empty archive if the file is absent.</summary>
        /// <param name="directory">Session directory path.</param>
        /// <param name="build">Expected build identity hash.</param>
        /// <param name="config">Expected configuration contract hash.</param>
        /// <returns>Recall archive passing format, event identity and checkpoint validation.</returns>
        /// <exception cref="InvalidDataException">Thrown for invalid archive structure, segment contents or identity checks.</exception>
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

        /// <summary>
        /// Read events and checkpoints to construct a valid source-session prefix through a specified event ID.
        /// Exclude log tails and checkpoints beyond the cutoff from recovered recall data.
        /// </summary>
        /// <param name="directory">Source session directory.</param>
        /// <param name="cutoff">Highest permitted event ID.</param>
        /// <param name="build">Expected application build identity hash.</param>
        /// <param name="config">Expected session configuration contract hash.</param>
        /// <returns>Recall segment containing valid cue events and checkpoints.</returns>
        /// <exception cref="InvalidDataException">Thrown for missing event sequence numbers, identity mismatches or a log ending before the required boundary.</exception>
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

        /// <summary>Check whether an event is a formal Revision recall cue.</summary>
        /// <param name="e">Event to inspect.</param>
        /// <returns>True for accepted formal Revision commits or participant scale changes.</returns>
        static bool IsCueEvent(StudyEvent e) => e.phase == "Revision" && !e.practice &&
            (e.eventType == "commit_accepted" || e.eventType == "scale_changed");

        /// <summary>Validate an event against its session, build, configuration and geometry contract.</summary>
        /// <param name="e">Event to validate.</param>
        /// <param name="session">Expected source session identifier.</param>
        /// <param name="build">Expected build identity hash.</param>
        /// <param name="config">Expected configuration contract hash.</param>
        /// <exception cref="InvalidDataException">Thrown for mismatched identity fields or invalid events.</exception>
        static void ValidateEvent(StudyEvent e, string session, string build, string config)
        {
            if (e == null || e.eventId <= 0 || e.sessionId != session || e.buildHash != build ||
                e.configHash != config || e.contractVersion != GeometryDomain.ContractVersion)
                throw new InvalidDataException("Recall event identity mismatch.");
        }
    }

    /// <summary>
    /// Build the predefined recall state sequence from the current session and recovery lineage.
    /// Cue selection is not filtered or scored by expected outcomes; preserve raw logs and report damaged data explicitly.
    /// </summary>
    public sealed class StudyRecall
    {
        // states stores replay checkpoints; cueIds/cueSessions identify their triggering source events.
        readonly List<StudyCheckpoint> states = new List<StudyCheckpoint>();
        readonly List<long> cueIds = new List<long>();
        readonly List<string> cueSessions = new List<string>();

        /// <summary>Number of states in the recall queue.</summary>
        public int Count => states.Count;

        /// <summary>Current recall index, or -1 before playback begins.</summary>
        public int Index { get; private set; } = -1;

        /// <summary>Event ID for the current recall state; Index must be valid.</summary>
        public long CueId => cueIds[Index];

        /// <summary>Source session ID for the current recall state; Index must be valid.</summary>
        public string CueSession => cueSessions[Index];

        /// <summary>
        /// Load cues from current logs and recovered history, select representative committed states and map events to checkpoints.
        /// Flush the recorder before reading so the latest events are visible.
        /// </summary>
        /// <param name="recorder">Current recorder providing directory, build/configuration identity and latest event boundary.</param>
        /// <exception cref="InvalidDataException">Thrown for damaged or mismatched archive, event or checkpoint data.</exception>
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

        /// <summary>Cycle to the next or previous recall checkpoint.</summary>
        /// <param name="direction">Negative selects the previous cue; zero or positive selects the next.</param>
        /// <returns>Checkpoint selected after switching.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the recall queue is empty.</exception>
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
