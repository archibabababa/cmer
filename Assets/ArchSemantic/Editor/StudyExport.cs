using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ArchSemanticPilot;
using ArchSemanticGeometry;

namespace ArchSemanticPilotEditor
{
    public static class StudyExport
    {
        [Serializable]
        public sealed class MeshItem
        {
            public string id;
            public float[] xyz;
            public int[] triangles;
        }

        [Serializable]
        public sealed class MeshDocument
        {
            public string schema = "cmer-mesh/0.9", coordinates = "Unity x,y(up),z in metres", contractVersion = GeometryDomain.ContractVersion, geometryHash;
            public MeshItem[] objects;
        }

        [MenuItem("CMER/Export checkpoint for blind review")]
        public static void ExportSelected()
        {
            string path = EditorUtility.OpenFilePanel("Select initial_locked or final_locked checkpoint", "", "json");
            if (string.IsNullOrEmpty(path))
                return;
            string parent = EditorUtility.OpenFolderPanel("Researcher export folder (keep private key outside review folder)", "", "");
            if (string.IsNullOrEmpty(parent))
                return;
            Export(path, parent, true);
        }

        [MenuItem("CMER/Export paired continuous review videos")]
        public static void ExportPair()
        {
            string initialPath = EditorUtility.OpenFilePanel("Select initial_locked checkpoint", "", "json");
            if (string.IsNullOrEmpty(initialPath)) return;
            string finalPath = EditorUtility.OpenFilePanel("Select matching final_locked checkpoint", Path.GetDirectoryName(initialPath), "json");
            if (string.IsNullOrEmpty(finalPath)) return;
            string parent = EditorUtility.OpenFolderPanel("Researcher export folder", "", "");
            if (string.IsNullOrEmpty(parent)) return;
            ExportPair(initialPath, finalPath, parent);
        }

        public static void ExportPair(string initialPath, string finalPath, string parent)
        {
            var initial = JsonUtility.FromJson<StudyCheckpoint>(File.ReadAllText(initialPath));
            var final = JsonUtility.FromJson<StudyCheckpoint>(File.ReadAllText(finalPath));
            string build = Resources.Load<TextAsset>("BuildIdentity")?.text.Trim();
            CheckpointValidation.Validate(initial, build, true);
            CheckpointValidation.Validate(final, build, true);
            if (initial.label != "initial_locked" || final.label != "final_locked" || initial.period != final.period ||
                initial.scenario != final.scenario || initial.configHash != final.configHash || initial.settings.participantId != final.settings.participantId)
                throw new InvalidDataException("Select an initial/final pair from the same participant, period, scenario and protocol.");
            if (initial.sessionId != final.sessionId)
            {
                string finalSession = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(finalPath))).FullName;
                var lineage = RecallArchive.Read(finalSession, build, final.configHash);
                if (!lineage.segments.Any(s => s.sessionId == initial.sessionId))
                    throw new InvalidDataException("The two checkpoints are not in the same recovery lineage.");
            }
            var a = LiveGeometryKernel.Evaluate(initial.scenario, initial.state.p1, initial.state.p2, initial.state.p3);
            var b = LiveGeometryKernel.Evaluate(final.scenario, final.state.p1, final.state.p2, final.state.p3);
            var path = RouteValidation.PairedReviewPath(a, b).Select(LiveGeometryDriver.Point).ToArray();
            // A random private pair key links two independently blinded output folders.
            string first = Export(initialPath, parent, false, path);
            string second;
            try { second = Export(finalPath, parent, false, path); }
            catch
            {
                File.WriteAllText(Path.Combine(first, "PAIR_INCOMPLETE.txt"), "The second export failed or was cancelled. This is not a complete comparison pair.");
                throw;
            }
            File.AppendAllText(Path.Combine(parent, "private_pairs.csv"), Path.GetFileName(first) + "," + Path.GetFileName(second) + "\n");
            Debug.Log("Paired continuous frame sequences exported. Run Tools/encode_review_video.py on each tour folder for MP4, or use the included viewer. Inspect both videos before assigning ratings.");
        }

        public static string Export(string path, string parent, bool frames, Vector3[] tour = null)
        {
            var c = JsonUtility.FromJson<StudyCheckpoint>(File.ReadAllText(path));
            string build = Resources.Load<TextAsset>("BuildIdentity")?.text.Trim();
            CheckpointValidation.Validate(c, build, forRatings: true);
            var r = LiveGeometryKernel.Evaluate(c.scenario, c.state.p1, c.state.p2, c.state.p3);
            var m = GeometryValidation.Measure(r);
            if (m.geometryHash != c.geometryHash)
                throw new InvalidDataException("Geometry does not match checkpoint");
            string outputKind = c.settings.inputMode == StudyInputMode.QuestHands && c.settings.executionEnvironment == "AndroidXR" ? "blind" : "rehearsal_preview";
            string code = Guid.NewGuid().ToString("N").Substring(0, 12), folder = Path.Combine(parent, outputKind, code);
            Directory.CreateDirectory(folder);
            var doc = new MeshDocument
            {
                geometryHash = m.geometryHash,
                objects = r.solids.Select((s, i) =>
                {
                    var solid = (Solid)s;
                    var t = solid.Triangles();
                    var indices = new List<int>();
                    for (int j = 0; j < t.Count; j += 3)
                    {
                        indices.Add(t[j]);
                        indices.Add(t[j + 2]);
                        indices.Add(t[j + 1]);
                    }

                    return new MeshItem
                    {
                        id = r.names[i],
                        xyz = solid.Vertices.SelectMany(v => new[] { (float)v.Location.X, (float)v.Location.Z, (float)v.Location.Y }).ToArray(),
                        triangles = indices.ToArray()
                    };
                }).ToArray()
            };
            // Geometry contains no participant, condition, phase, duration or hand information.
            File.WriteAllText(Path.Combine(folder, "geometry.json"), JsonUtility.ToJson(doc));
            File.WriteAllText(Path.Combine(folder, "metrics.json"), JsonUtility.ToJson(m, true));
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject root = null, camRoot = null;
            var mats = new Material[8];
            RenderTexture texture = null;
            Texture2D image = null;
            try
            {
                var shader = Shader.Find("Hidden/CMER/Section");
                if (shader == null)
                    throw new InvalidDataException("Section shader unavailable");
                for (int i = 0; i < 8; i++)
                {
                    mats[i] = new Material(shader);
                    mats[i].name = "Review_" + i;
                    mats[i].SetColor("_Color", i == 2 ? new Color(.72f, .81f, .86f) : i == 1 ? new Color(.78f, .78f, .75f) : new Color(.87f, .85f, .81f));
                }

                root = new GameObject("Blind geometry");
                SceneManager.MoveGameObjectToScene(root, preview);
                var driver = root.AddComponent<LiveGeometryDriver>();
                driver.materials = mats;
                driver.Apply(driver.BuildNow(c.scenario, c.state), false);
                camRoot = new GameObject("Standard review camera");
                SceneManager.MoveGameObjectToScene(camRoot, preview);
                var camera = camRoot.AddComponent<Camera>();
                camera.scene = preview;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.white;
                camera.nearClipPlane = .08f;
                camera.farClipPlane = 150;
                camera.fieldOfView = 70;
                texture = new RenderTexture(960, 640, 24);
                image = new Texture2D(960, 640, TextureFormat.RGB24, false);
                camera.targetTexture = texture;
                Action<string, Vector3, Vector3, bool, Vector4> render = (name, position, target, ortho, cut) =>
                {
                    foreach (var mat in mats)
                        mat.SetVector("_CutPlane", cut);
                    camera.orthographic = ortho;
                    camera.orthographicSize = 23;
                    camera.transform.position = position;
                    Vector3 direction = target - position;
                    camera.transform.rotation = Quaternion.LookRotation(direction, Math.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .99f ? Vector3.forward : Vector3.up);
                    camera.Render();
                    var old = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = texture;
                        image.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
                        image.Apply();
                        File.WriteAllBytes(Path.Combine(folder, name + ".png"), image.EncodeToPNG());
                    }
                    finally
                    {
                        RenderTexture.active = old;
                    }
                };
                render("plan", new Vector3(5, 60, 3), new Vector3(5, 0, 3), true, new Vector4(0, 1, 0, 1.65f));
                render("section", new Vector3(5, 7, -55), new Vector3(5, 7, 0), true, new Vector4(0, 0, -1, 0));
                render("axonometric", new Vector3(40, 36, -40), new Vector3(5, 3, 3), true, new Vector4(0, 0, 0, 1));
                if (frames)
                {
                    Directory.CreateDirectory(Path.Combine(folder, "walk"));
                    var neutral = LiveGeometryKernel.Evaluate(c.scenario, 0, 0, 0);
                    for (int i = 0; i < 24; i++)
                    {
                        int stop = i / 4, room = stop % 3;
                        Vector3 position = LiveGeometryDriver.Point(stop < 3 ? neutral.starts[room] : neutral.anchors[6 + room]);
                        position.y = 1.71f;
                        Vector3 baseDirection = LiveGeometryDriver.Point(neutral.views[room].PointAtStart) - LiveGeometryDriver.Point(neutral.views[room].PointAtEnd);
                        Vector3 direction = Quaternion.AngleAxis((i % 4 - 1.5f) * 30, Vector3.up) * baseDirection;
                        render("walk/frame_" + i.ToString("D3"), position, position + direction, false, new Vector4(0, 0, 0, 1));
                    }
                }

                if (tour != null)
                {
                    Directory.CreateDirectory(Path.Combine(folder, "tour"));
                    const int frameCount = 720;
                    float length = (tour.Length - 1) * .25f;
                    Vector3 PointAt(float distance)
                    {
                        float index = Mathf.Clamp(distance / .25f, 0, tour.Length - 1);
                        int a = Mathf.Min(Mathf.FloorToInt(index), tour.Length - 2);
                        return Vector3.Lerp(tour[a], tour[a + 1], index - a);
                    }
                    Vector3 lastDirection = (tour[1] - tour[0]).normalized;
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        if (frame % 12 == 0 && EditorUtility.DisplayCancelableProgressBar("Paired review export", "Rendering frame " + frame + " / " + frameCount, (float)frame / frameCount))
                            throw new OperationCanceledException("Export cancelled; incomplete output must not be used for ratings.");
                        float distance = length * frame / (frameCount - 1f);
                        Vector3 position = PointAt(distance);
                        Vector3 look = PointAt(Mathf.Min(length, distance + 1.2f)) - PointAt(Mathf.Max(0, distance - .3f));
                        if (look.sqrMagnitude > .0001f) lastDirection = Vector3.Slerp(lastDirection, look.normalized, .35f).normalized;
                        render("tour/frame_" + frame.ToString("D4"), position, position + lastDirection, false, new Vector4(0, 0, 0, 1));
                    }
                    File.WriteAllText(Path.Combine(folder, "tour/path.csv"), "x,y,z\n" + string.Join("\n", tour.Select(p => string.Join(",", new[] { p.x, p.y, p.z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
                    File.WriteAllText(Path.Combine(folder, "tour/play.html"), "<!doctype html><meta charset='utf-8'><title>Review</title><style>body{margin:0;background:#222;text-align:center}img{max-width:100vw;max-height:90vh}button{font-size:18px}</style><img id='frame' src='frame_0000.png'><br><button id='toggle'>Play / pause</button><script>let n=0,play=false;document.getElementById('toggle').onclick=()=>play=!play;setInterval(()=>{if(play){n=(n+1)%720;document.getElementById('frame').src='frame_'+String(n).padStart(4,'0')+'.png'}},1000/12)</script>");
                }

                File.WriteAllText(Path.Combine(folder, "view_protocol.txt"), GeometryDomain.ContractVersion + "; 960x640; 70deg perspective; plan y=1.65m; section z=0; no participant UI. " +
                    (tour == null ? "Single checkpoint export: static views only; use paired export for continuous comparison. " : "Paired tour: 720 frames, 12fps, 60s; identical path, height, timing and camera orientation for the initial/final pair; 0.25m grid with conservative 1.2m common ground clearance. Path visits three room starts and three semantic anchors (nearest common clear cell within 2m). ") +
                    "Native visual inspection, semantic calibration and headset performance remain separate validations.");
                string key = Path.Combine(parent, outputKind == "blind" ? "researcher_key.csv" : "rehearsal_key.csv");
                if (!File.Exists(key))
                    File.WriteAllText(key, "review_code,session,participant,period,scenario,condition,label,source_checkpoint\n");
                string condition = c.settings.AssignedCondition.ToString();
                File.AppendAllText(key, string.Join(",", new[] { code, c.sessionId, c.settings.participantId, (c.period + 1).ToString(), c.scenario == 0 ? "A" : "B", condition, c.label, Path.GetFullPath(path) }.Select(x => "\"" + x.Replace("\"", "\"\"") + "\"")) + "\n");
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => new SealedFile { file = f.Substring(folder.Length + 1).Replace('\\', '/'), bytes = new FileInfo(f).Length, sha256 = StudyRecorder.FileHash(f) }).ToArray();
                File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonUtility.ToJson(new SealedManifest { sessionId = code, utc = DateTime.UtcNow.ToString("o"), status = outputKind + "_export", files = files }, true));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (root != null)
                    UnityEngine.Object.DestroyImmediate(root);
                if (camRoot != null)
                    UnityEngine.Object.DestroyImmediate(camRoot);
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                if (image != null)
                    UnityEngine.Object.DestroyImmediate(image);
                foreach (var mat in mats)
                    if (mat != null)
                        UnityEngine.Object.DestroyImmediate(mat);
                EditorSceneManager.ClosePreviewScene(preview);
            }

            return folder;
        }
    }
}
