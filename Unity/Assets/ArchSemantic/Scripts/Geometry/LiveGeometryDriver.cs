using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    public sealed class LiveGeometryDriver : MonoBehaviour
    {
        public PilotSession session;
        public Material[] materials;
        public MeshFilter[] modelParts;
        [Range(5, 20)]
        public float maximumUpdatesPerSecond = 10;
        public string status = "Preparing geometry";
        public float groundFloorHeight = .06f;
        public int displayedVersion;
        public Vector3[] liveStarts, liveDirections, liveZonePositions;
        public GeometryMetrics Metrics => current?.metrics;
        public Prepared Current => current;

        // Unity Mesh cache for eight groups.
        readonly Mesh[] meshes = new Mesh[8];

        // Only selected groups need colliders, such as walls, slabs, glazing, doors and furnishings.
        readonly MeshCollider[] colliders = new MeshCollider[8];

        // Current background computation task.
        Task<Prepared> work;

        // Latest request not yet dispatched; newer requests replace older ones.
        Request pending;

        // Currently displayed data snapshot.
        Prepared current;

        // ticket is the latest request ID; workTicket identifies the running background task.
        long ticket, workTicket;

        // Earliest allowed preview request time for throttling.
        float nextSubmit;

        // Lifecycle flag preventing session callbacks after destruction.
        bool alive = true, initialized;
        sealed class Request
        {
            // Request sequence number used to reject stale results.
            public long ticket;

            // Scenario index and active axis (0/1/2 correspond to P1/P2/P3).
            public int scenario, axis;

            // previous is the committed state; requested is the desired state.
            public SpatialState previous, requested;

            // commit selects submission; exact skips active-axis solving; practice selects practice mode.
            public bool commit, exact, practice;

            // Action semantics and transaction ID for deduplication and log correlation.
            public string action, operation;
        }
        public sealed class Group
        {
            // Triangle vertices, normals and indices.
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<int> indices = new List<int>();
        }
        public sealed class Prepared
        {
            // Request identifier.
            public long ticket;

            // Scenario index.
            public int scenario;

            // Commit and practice flags.
            public bool commit, practice;

            // Action, transaction ID and geometry projection reason.
            public string action, operation, projection;

            // Previous, requested and effective states.
            public SpatialState previous, requested, effective;

            // Raw geometry result and validation metrics.
            public LiveGeometryKernel.Result source;
            public GeometryMetrics metrics;

            // Renderable mesh data for eight groups.
            public Group[] groups;

            // Preparation duration in milliseconds.
            public double milliseconds;
        }
        public void Initialize()
        {
            if (initialized)
                return;
            LiveGeometryKernel.LoadResources();
            if (materials == null || materials.Length != 8 || Array.Exists(materials, m => m == null))
                throw new InvalidOperationException("Assign all eight architecture materials.");
            bool bound = modelParts != null && modelParts.Length > 0;
            if (bound)
            {
                if (modelParts.Length != 8)
                    throw new InvalidOperationException("The architecture prefab requires exactly eight mesh slots.");
                var unique = new HashSet<MeshFilter>();
                for (int i = 0; i < 8; i++)
                {
                    var part = modelParts[i];
                    if (part == null || !unique.Add(part) || !part.transform.IsChildOf(transform) ||
                        part.GetComponent<MeshRenderer>() == null || part.gameObject.layer != 8)
                        throw new InvalidOperationException("Invalid architecture prefab binding at slot " + i);
                    // Kernel vertices use driver-local coordinates. Reject shifted mesh containers
                    // rather than silently moving a user's authored objects back to the origin.
                    Matrix4x4 relative = transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++)
                            if (Mathf.Abs(relative[row, col] - (row == col ? 1 : 0)) > .0001f)
                                throw new InvalidOperationException("Prefab mesh containers must keep identity transforms relative to Live Architecture.");
                    if (NeedsCollider(i) && part.GetComponent<MeshCollider>() == null)
                        throw new InvalidOperationException("Missing architecture collider at slot " + i);
                }
            }
            for (int i = 0; i < 8; i++)
            {
                GameObject go;
                if (bound)
                    go = modelParts[i].gameObject;
                else
                {
                    go = new GameObject(materials[i].name);
                    go.layer = 8;
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>();
                    go.AddComponent<MeshRenderer>().sharedMaterial = materials[i];
                    if (NeedsCollider(i)) go.AddComponent<MeshCollider>();
                }
                meshes[i] = new Mesh
                {
                    name = "Live_" + materials[i].name,
                    indexFormat = IndexFormat.UInt32
                };
                meshes[i].MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = meshes[i];
                if (NeedsCollider(i)) colliders[i] = go.GetComponent<MeshCollider>();
            }
            initialized = true;
        }

        static bool NeedsCollider(int slot) => slot == 0 || slot == 1 || slot == 2 || slot == 4 || slot == 6;
        public void CancelPending()
        {
            ticket++;
            pending = null;
        }
        public void RequestState(int scenario, SpatialState previous, SpatialState requested, int axis, bool commit, bool exact, string action, string operation, bool practice)
        {
            pending = new Request
            {
                ticket = ++ticket,
                scenario = scenario,
                previous = previous,
                requested = requested,
                axis = axis,
                commit = commit,
                exact = exact,
                action = action,
                operation = operation,
                practice = practice
            };
        }
        public Prepared BuildNow(int scenario, SpatialState state, bool practice = false)
        {
            Initialize();
            return Prepare(new Request { scenario = scenario, previous = state, requested = state, exact = true, practice = practice });
        }
        void Update()
        {
            if (work != null && work.IsCompleted)
            {
                try
                {
                    var data = work.GetAwaiter().GetResult();
                    // Accept only the current ticket or a still-relevant result from the same preview transaction to avoid out-of-order replacement.
                    if (alive && (data.ticket == ticket || (!data.commit && pending != null && !pending.commit && data.operation == pending.operation)))
                        session.Prepared(data);
                }
                catch (Exception e)
                {
                    if (alive && workTicket == ticket)
                        session.GeometryFailed(e.Message);
                }

                work = null;
            }

            if (work == null && pending != null && (pending.commit || Time.unscaledTime >= nextSubmit))
            {
                // Consume and clear the latest pending request to avoid repeating old work.
                var request = pending;
                pending = null;
                nextSubmit = Time.unscaledTime + 1 / maximumUpdatesPerSecond;
                workTicket = request.ticket;
                // Compute geometry on a background thread to avoid blocking main-thread rendering.
                work = Task.Run(() => Prepare(request));
            }
        }
        public void Apply(Prepared data, bool collision)
        {
            Initialize();
            for (int i = 0; i < 8; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = false;
                    colliders[i].sharedMesh = null;
                }

                meshes[i].Clear();
                meshes[i].SetVertices(data.groups[i].vertices);
                meshes[i].SetNormals(data.groups[i].normals);
                meshes[i].SetTriangles(data.groups[i].indices, 0, true);
                // Restore colliders as needed to prevent stale collision geometry during updates.
                if (colliders[i] != null && collision && data.groups[i].indices.Count > 0)
                {
                    colliders[i].sharedMesh = meshes[i];
                    colliders[i].enabled = true;
                }
            }

            current = data;
            groundFloorHeight = (float)data.source.groundFloorHeight;
            displayedVersion++;

            // Cache starts, directions and semantic anchors of the three main spaces for navigation and interaction.
            liveStarts = new Vector3[3];
            liveDirections = new Vector3[3];
            liveZonePositions = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                liveStarts[i] = Point(data.source.starts[i]);
                liveDirections[i] = (Point(data.source.views[i].PointAtEnd) - Point(data.source.views[i].PointAtStart)).normalized;
                liveZonePositions[i] = Point(data.source.anchors[6 + i]);
            }

            status = data.projection ?? "NONE";
        }
        public static Vector3 Point(Point3d p) => new Vector3((float)p.X, (float)p.Z, (float)p.Y);
        static Prepared Prepare(Request request)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string reason = "NONE";

            // Formal previews use active-axis reachability; exact and practice requests use the requested values directly.
            var effective = request.exact || request.practice ? request.requested : GeometryDomain.SolveActive(request.scenario, request.previous, request.axis, request.requested[request.axis], out reason);

            // Formal mode uses LiveGeometryKernel; practice mode uses PracticeGeometry.
            var result = request.practice ? PracticeGeometry.Evaluate(effective) : LiveGeometryKernel.Evaluate(request.scenario, effective.p1, effective.p2, effective.p3);
            var data = new Prepared
            {
                ticket = request.ticket,
                scenario = request.scenario,
                previous = request.previous,
                requested = request.requested,
                effective = effective,
                projection = reason,
                commit = request.commit,
                action = request.action,
                operation = request.operation,
                practice = request.practice,
                source = result,

                // Commit and exact builds perform stricter checks; previews may relax selected validations.
                metrics = GeometryValidation.Measure(result, request.practice, request.commit || request.exact),
                groups = new Group[8]
            };
            for (int i = 0; i < 8; i++)
                data.groups[i] = new Group();

            // Distribute semantic solids among eight material slots and triangulate them for Unity rendering.
            for (int i = 0; i < result.solids.Count; i++)
            {
                string category = result.names[i].Split(new[] { "__" }, StringSplitOptions.None)[1];
                int mat = category == "WAL" ? 0 : category == "SLB" || category == "FLR" ? 1 : category == "GLS" ? 2 : category == "FRM" ? 3 : category == "DOR" ? 4 : category == "CLG" ? 5 : category == "FIX" ? 6 : 7;
                var group = data.groups[mat];
                var solid = (Solid)result.solids[i];
                var t = solid.Triangles();
                for (int j = 0; j < t.Count; j += 3)
                {
                   
                    Vector3 a = Point(solid.Vertices[t[j]].Location), b = Point(solid.Vertices[t[j + 2]].Location), c = Point(solid.Vertices[t[j + 1]].Location);
                    Vector3 cross = Vector3.Cross(b - a, c - a);

                    if (cross.sqrMagnitude < 1e-16f)
                        continue;
                    Vector3 normal = cross.normalized;
                    int k = group.vertices.Count;
                    group.vertices.Add(a);
                    group.vertices.Add(b);
                    group.vertices.Add(c);
                    for (int n = 0; n < 3; n++)
                    {
                        group.normals.Add(normal);
                        group.indices.Add(k + n);
                    }
                }
            }

            data.milliseconds = timer.Elapsed.TotalMilliseconds;
            return data;
        }
        void OnDestroy()
        {
            alive = false;
            CancelPending();
            foreach (var mesh in meshes)
                if (mesh != null)
                {
                    if (Application.isPlaying)
                        Destroy(mesh);
                    else
                        DestroyImmediate(mesh);
                }
        }
    }
}
