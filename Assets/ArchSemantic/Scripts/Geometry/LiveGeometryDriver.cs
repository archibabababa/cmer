using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Live geometry driver:
    /// 1) Receive geometry requests from the session;
    /// 2) Run the geometry kernel on a background thread;
    /// 3) Convert the result to Unity Mesh objects and apply it to the scene;
    /// 4) Report success or failure to the session.
    /// </summary>
    public sealed class LiveGeometryDriver : MonoBehaviour
    {
        /// <summary>Session controller receiving Prepared and GeometryFailed callbacks.</summary>
        public PilotSession session;

        /// <summary>
        /// Materials for the eight rendering groups.
        /// Prepare defines the index-to-category mapping.
        /// </summary>
        public Material[] materials;

        /// <summary>
        /// Optional prefab mesh slots in the existing material order:
        /// Wall, Floor, Glass, Frame, Door, Ceiling, Fixture, Light.
        /// Scene instances are reused; only their runtime mesh buffers are modified.
        /// Empty bindings preserve compatibility with previously saved scenes.
        /// </summary>
        public MeshFilter[] modelParts;

        /// <summary>
        /// Maximum non-committed preview update frequency in Hz, throttled to reduce computation.
        /// </summary>
        [Range(5, 20)]
        public float maximumUpdatesPerSecond = 10;

        /// <summary>Latest geometry projection or constraint status, such as NONE or ACTIVE_AXIS_BOUNDARY.</summary>
        public string status = "Preparing geometry";

        /// <summary>Floor elevation of the displayed geometry in Unity Y.</summary>
        public float groundFloorHeight = .06f;

        /// <summary>Display revision, incremented after every successful Apply.</summary>
        public int displayedVersion;

        /// <summary>
        /// Live navigation aids for the three main spaces: start points, viewing directions and semantic anchors.
        /// Derived from the geometry result's starts, views and anchors.
        /// </summary>
        public Vector3[] liveStarts, liveDirections, liveZonePositions;

        /// <summary>Metrics of the displayed result, including volume, rooms and routes.</summary>
        public GeometryMetrics Metrics => current?.metrics;

        /// <summary>Prepared result currently applied to the scene.</summary>
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

        /// <summary>
        /// Data for one geometry request.
        /// </summary>
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

        /// <summary>
        /// Mesh buffer for one material group.
        /// </summary>
        public sealed class Group
        {
            // Triangle vertices, normals and indices.
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<int> indices = new List<int>();
        }

        /// <summary>
        /// Complete data packet prepared by the background worker.
        /// </summary>
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

        /// <summary>
        /// Bind eight prefab parts, or create the legacy containers for an unbound scene.
        /// </summary>
        public void Initialize()
        {
            if (initialized)
                return;
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

        /// <summary>
        /// Cancel pending requests by advancing the ticket and invalidating existing results.
        /// </summary>
        public void CancelPending()
        {
            ticket++;
            pending = null;
        }

        /// <summary>
        /// Submit or replace a geometry request, retaining only the newest.
        /// </summary>
        /// <param name="scenario">Scenario index (0=A, 1=B).</param>
        /// <param name="previous">Current committed state.</param>
        /// <param name="requested">Requested target state.</param>
        /// <param name="axis">Active axis index (0/1/2).</param>
        /// <param name="commit">Whether to commit the final result.</param>
        /// <param name="exact">Whether to build exactly, bypassing active-axis correction.</param>
        /// <param name="action">Action label, such as edit.</param>
        /// <param name="operation">Operation transaction ID.</param>
        /// <param name="practice">Whether practice mode is active.</param>
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

        /// <summary>
        /// Build synchronously for initialization or a forced rebuild.
        /// </summary>
        /// <param name="scenario">Scenario index.</param>
        /// <param name="state">Input state.</param>
        /// <param name="practice">True for practice mode; false for the formal task.</param>
        /// <returns>Prepared geometry data packet.</returns>
        public Prepared BuildNow(int scenario, SpatialState state, bool practice = false)
        {
            Initialize();
            return Prepare(new Request { scenario = scenario, previous = state, requested = state, exact = true, practice = practice });
        }

        /// <summary>
        /// Per-frame scheduling:
        /// 1) Collect background results and notify the session;
        /// 2) Dispatch new requests according to the throttle policy.
        /// </summary>
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

        /// <summary>
        /// Apply Prepared data to Unity meshes and colliders and refresh navigation caches.
        /// </summary>
        /// <param name="data">Data to apply.</param>
        /// <param name="collision">Whether to enable colliders.</param>
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


        /// <summary>
        /// Convert kernel coordinates to Unity coordinates:
        /// kernel(X,Y,Z) -> unity(X,Z,Y)
        /// Map kernel plan Y to Unity Z and kernel elevation Z to Unity Y.
        /// </summary>
        public static Vector3 Point(Point3d p) => new Vector3((float)p.X, (float)p.Z, (float)p.Y);

        /// <summary>
        /// Background preparation pipeline:
        /// 1) Solve the effective state;
        /// 2) Run the formal or practice geometry kernel;
        /// 3) Measure and validate geometry;
        /// 4) Generate triangle meshes grouped by material.
        /// </summary>
        /// <param name="request">Geometry request.</param>
        /// <returns>Data packet ready for the rendering layer.</returns>
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

        /// <summary>
        /// Stop callbacks and destroy dynamically created meshes during cleanup.
        /// </summary>
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
