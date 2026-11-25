using System;
using System.IO;
using System.Linq;
using ArchSemanticPilot;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArchSemanticPilotEditor
{
    public sealed class BuildStamp : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            StudyValidation.StampBuild();
        }
    }

    public static class StudyValidation
    {
        [MenuItem("CMER/Validate source and scene bindings")]
        public static void Validate()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Stop Play mode before validating saved scenes.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Directory.CreateDirectory("../Validation");
                File.WriteAllText("../Validation/unity-core-tests.json", JsonUtility.ToJson(CoreRegression.Run(), true));
                ValidateXrDestinationHeading();
                foreach (string id in new[]
                {
                    "A",
                    "B"
                }

                )
                {
                    var scene = EditorSceneManager.OpenScene("Assets/ArchSemantic/Scenes/Model_" + id + ".unity");
                    var s = UnityEngine.Object.FindFirstObjectByType<PilotSession>();
                    if (s == null || s.geometry == null || s.hands == null || s.headCamera == null || s.xrOrigin == null)
                        throw new InvalidDataException("Missing study binding in " + id);
                    if (s.headCamera.allowHDR)
                        throw new InvalidDataException("Study camera must use the validated LDR output in " + id);
                    s.settings.Validate();
                    ModelPreparation.ValidateBinding(s);
                    if (s.geometry.materials.Length != 8 || s.geometry.materials.Any(m => m == null))
                        throw new InvalidDataException("Missing palette");
                    var m = GeometryValidation.Measure(LiveGeometryKernel.Evaluate(id == "A" ? 0 : 1, 0, 0, 0));
                    if (m.minimumPortal < 2 || m.volumeError > .001)
                        throw new InvalidDataException("Invalid baseline");
                }

                StampBuild();
                Directory.CreateDirectory("../Validation");
                File.WriteAllText("../Validation/unity-validation.json", "{\"result\":\"PASS\",\"scope\":\"Unity native scene deserialization, references and geometry; no headset test\",\"unity\":\"" + Application.unityVersion + "\"}");
                Debug.Log("CMER native scene validation PASS");
            }
            finally
            {
                if (previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }

        static void ValidateXrDestinationHeading()
        {
            var root = new GameObject("XR destination regression");
            try
            {
                var origin = new GameObject("Origin").transform;origin.SetParent(root.transform);
                var offset = new GameObject("Offset").transform;offset.SetParent(origin,false);
                var camera = new GameObject("Head").AddComponent<Camera>();camera.transform.SetParent(offset,false);
                var driver = root.AddComponent<LiveGeometryDriver>();
                driver.liveStarts = new[]{new Vector3(3,1,5),new Vector3(-4,1,2),new Vector3(8,1,-3)};
                driver.liveDirections = new[]{Vector3.forward,Vector3.right,Vector3.back};
                var navigation = new StudyNavigation(origin,camera,driver);
                Vector3 local = new Vector3(.2f,1.7f,.3f);Quaternion rotation = Quaternion.Euler(12,25,0);
                camera.transform.localPosition=local;camera.transform.localRotation=rotation;
                origin.rotation=Quaternion.Euler(0,117,0);
                for(int i=0;i<3;i++)
                {
                    navigation.GoToSpace(i,true,new System.Collections.Generic.List<UnityEngine.XR.XRInputSubsystem>());
                    Vector3 delta=camera.transform.position-driver.liveStarts[i];delta.y=0;
                    Vector3 heading=Vector3.ProjectOnPlane(camera.transform.forward,Vector3.up).normalized;
                    if(delta.magnitude>.0001f||Vector3.Dot(heading,-driver.liveDirections[i])<.9999f||
                        Vector3.Distance(camera.transform.localPosition,local)>.0001f||Quaternion.Angle(camera.transform.localRotation,rotation)>.001f||origin.localScale!=Vector3.one)
                        throw new InvalidOperationException("XR destination changed tracking pose or missed its inspection heading");
                }
                File.WriteAllText("../Validation/xr-destination-regression.json","{\"passed\":true,\"destinations\":3,\"checks\":[\"eye XZ reaches destination\",\"world yaw faces inspection direction\",\"tracked local position and rotation preserved\",\"unit origin scale\"],\"hardwareValidated\":false}");
            }
            finally {UnityEngine.Object.DestroyImmediate(root);}
        }

        [MenuItem("CMER/Stamp build identity")]
        public static void StampBuild()
        {
            var paths = Directory.GetFiles("Assets", "*", SearchOption.AllDirectories).Where(p => !p.Replace('\\', '/').StartsWith("Assets/Resources/BuildIdentity.txt", StringComparison.Ordinal)).Concat(Directory.GetFiles("Packages", "*.json")).Concat(Directory.GetFiles("ProjectSettings", "*", SearchOption.TopDirectoryOnly)).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal);
            string list = string.Join("\n", paths.Select(p => p.Replace('\\', '/') + " " + StudyRecorder.FileHash(p)));
            Directory.CreateDirectory("Assets/Resources");
            File.WriteAllText("Assets/Resources/BuildIdentity.txt", StateIdentity.Hash(list));
            Directory.CreateDirectory("../Documentation");
            File.WriteAllText("../Documentation/BuildSourceHashes.txt", list);
            AssetDatabase.Refresh();
        }
    }
}
