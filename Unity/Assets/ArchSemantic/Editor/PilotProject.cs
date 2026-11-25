using System;
using System.IO;
using ArchSemanticPilot;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using System.Linq;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace ArchSemanticPilotEditor
{
    [InitializeOnLoad]
    public static class PilotProject
    {

        [MenuItem("CMER/Run mode/Meta XR Simulator (Windows OpenXR)")]
        public static void UseMetaSimulator()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play mode before changing XR settings.");
            var group = BuildTargetGroup.Standalone;
            XRGeneralSettingsPerBuildTarget container;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out container) || container == null)
                throw new InvalidOperationException("XR settings asset missing. Reimport the supplied Assets/XR folder.");
            var general = container.SettingsForBuildTarget(group);
            if (general == null || general.Manager == null)
            {
                container.CreateDefaultManagerSettingsForBuildTarget(group);
                general = container.SettingsForBuildTarget(group);
            }
            if (!general.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == "UnityEngine.XR.OpenXR.OpenXRLoader") &&
                !XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group))
                throw new InvalidOperationException("Could not assign Windows OpenXR loader.");
            general.InitManagerOnStart = true;
            var xr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            foreach (var feature in xr.GetFeatures<UnityEngine.XR.OpenXR.Features.OpenXRFeature>())
                if (feature is HandTracking || feature is MetaHandTrackingAim || feature is HandInteractionProfile || feature is OculusTouchControllerProfile)
                { feature.enabled = true; EditorUtility.SetDirty(feature); }
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.Direct3D11 });
            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(general.Manager);
            EditorUtility.SetDirty(xr);
            AssetDatabase.SaveAssets();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                EditorUserBuildSettings.SwitchActiveBuildTarget(group, BuildTarget.StandaloneWindows64);
            Debug.Log("Windows OpenXR enabled. Start Meta XR Simulator, then Play. Confirm the project image appears in the simulator.");
        }

        [MenuItem("CMER/Run mode/Desktop rehearsal (no headset)")]
        public static void UseDesktopRehearsal()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play mode before changing XR settings.");
            var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (general != null) { general.InitManagerOnStart = false; EditorUtility.SetDirty(general); }
            AssetDatabase.SaveAssets();
            Debug.Log("Desktop rehearsal selected. Android Quest settings remain available.");
        }

        private const string Scenes = "Assets/ArchSemantic/Scenes";
        static PilotProject()
        {
            EditorApplication.delayCall += FirstOpen;
        }

        private static void FirstOpen()
        {
            if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || SessionState.GetBool("CMER.StartSceneOpened", false))
                return;
            SessionState.SetBool("CMER.StartSceneOpened", true);
            if (SceneManager.GetActiveScene().isDirty || !string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                return;
            if (File.Exists(Scenes + "/Model_A.unity"))
            {
                EditorSceneManager.OpenScene(Scenes + "/Model_A.unity");
                return;
            }
            try
            {
                Prepare();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [MenuItem("CMER/Reference assets/Prepare source models and missing scenes")]
        public static void Prepare()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Stop Play mode before preparing scenes.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Directory.CreateDirectory(Scenes);
            AssetDatabase.Refresh();
            foreach (string id in new[]
            {
                "A",
                "B"
            }

            )
            {
                var model = ModelImporter.Import(id);
                CheckSource(model);
                if (!File.Exists(Scenes + "/Model_" + id + ".unity"))
                    CreateScene(model);
            }

            EditorSceneManager.OpenScene(Scenes + "/Model_A.unity");
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(Scenes + "/Model_A.unity", true)
            };
            AssetDatabase.SaveAssets();
        }

        [MenuItem("CMER/Open study (A first)")]
        public static void OpenA()
        {
            Open("A");
        }

        [MenuItem("CMER/Open study (B first)")]
        public static void OpenB()
        {
            Open("B");
        }

        private static void Open(string id)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("Stop Play mode to switch A/B scenes.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            EditorSceneManager.OpenScene(Scenes + "/Model_" + id + ".unity");
        }

        private static void CreateScene(ModelDefinition model)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var architecture = new GameObject("Live Architecture");
            var driver = architecture.AddComponent<LiveGeometryDriver>();
            driver.materials = new Material[8];
            string[] palette =
            {
                "Wall",
                "Floor",
                "Glass",
                "Frame",
                "Door",
                "Ceiling",
                "Fixture",
                "Light"
            };
            for (int i = 0; i < 8; i++)
                driver.materials[i] = AssetDatabase.LoadAssetAtPath<Material>(ModelImporter.Materials + "/" + palette[i] + ".mat");
            var root = new GameObject("Pilot Session");
            var session = root.AddComponent<PilotSession>();
            session.settings.sequence = model.scenario == "B" ? 2 : 0;
            session.settings.inputMode = StudyInputMode.DesktopRehearsal;
            session.geometry = driver;
            driver.session = session;
            var xr = new GameObject("XR Origin");
            var origin = xr.AddComponent<XROrigin>();
            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(xr.transform, false);
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(offset.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            // LDR is sufficient for the study palette and avoids the HDR intermediate
            // target that initialized black on the Intel / Meta simulator test machine.
            camera.allowHDR = false;
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 150;
            camera.backgroundColor = new Color(.115f, .14f, .155f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            cameraObject.AddComponent<AudioListener>();
            var pose = cameraObject.AddComponent<TrackedPoseDriver>();
            pose.positionInput = new InputActionProperty(new InputAction("Head position", InputActionType.PassThrough, "<XRHMD>/centerEyePosition"));
            pose.rotationInput = new InputActionProperty(new InputAction("Head rotation", InputActionType.PassThrough, "<XRHMD>/centerEyeRotation"));
            // Read a stationary headset immediately after enabling or resuming tracking.
            pose.positionInput.action.wantsInitialStateCheck = true;
            pose.rotationInput.action.wantsInitialStateCheck = true;
            pose.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            pose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            origin.Camera = camera;
            origin.CameraFloorOffsetObject = offset;
            origin.CameraYOffset = 0;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            xr.transform.position = model.starts[0] - new Vector3(0, 1.71f, 0);
            cameraObject.transform.localPosition = new Vector3(0, 1.71f, 0);
            xr.transform.rotation = Quaternion.LookRotation(model.viewDirections[0]);
            session.xrOrigin = xr.transform;
            session.headCamera = camera;
            var statusObject = new GameObject("Live Status");
            statusObject.transform.SetParent(cameraObject.transform, false);
            statusObject.transform.localPosition = new Vector3(-.48f, .24f, 1.1f);
            var text = statusObject.AddComponent<TextMesh>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 48;
            text.characterSize = .007f;
            text.anchor = TextAnchor.UpperLeft;
            text.color = Color.black;
            statusObject.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
            session.headsetStatus = text;
            var hands = xr.AddComponent<NoHandleSemanticGestureInteractor>();
            hands.trackingOrigin = offset.transform;
            hands.headPoseSource = cameraObject.transform;
            hands.engine = session;
            session.hands = hands;
            var skeleton = xr.AddComponent<HandSkeletonView>();
            skeleton.trackingOrigin = offset.transform;
            string handMaterialPath = ModelImporter.Materials + "/Hands.mat";
            var handMaterial = AssetDatabase.LoadAssetAtPath<Material>(handMaterialPath);
            if (handMaterial == null)
            {
                handMaterial = new Material(Shader.Find("Unlit/Color"))
                {
                    color = new Color(.9f, .75f, .5f)
                };
                AssetDatabase.CreateAsset(handMaterial, handMaterialPath);
            }

            skeleton.material = handMaterial;
            var zones = new SemanticGestureZone[3];
            for (int i = 0; i < 3; i++)
            {
                string prefix = model.scenario + "__ANCHOR__P" + (i + 1) + "_";
                Vector3 position = model.starts[i];
                foreach (var anchor in model.anchors)
                    if (anchor.id.StartsWith(prefix, StringComparison.Ordinal))
                        position = anchor.position;
                var zoneObject = new GameObject("P" + (i + 1) + " Gesture Zone");
                zoneObject.transform.SetParent(root.transform, false);
                zoneObject.transform.position = position;
                var zone = zoneObject.AddComponent<SemanticGestureZone>();
                zone.axis = (SemanticAxis)(i + 1);
                zone.semantic = new[]
                {
                    "THRESHOLD_REVELATION",
                    "OCCUPIED_REFUGE",
                    "ROUTE_INTEGRATION"
                }[i];
                zone.travelMeters = i == 1 ? .35f : .45f;
                zones[i] = zone;
            }

            hands.zones = zones;
            var sun = new GameObject("Directional Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50, -35, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.65f, .68f, .72f);
            QualitySettings.shadowDistance = 30;
            QualitySettings.antiAliasing = 4;
            QualitySettings.vSyncCount = 0;
            EditorSceneManager.SaveScene(scene, Scenes + "/Model_" + model.scenario + ".unity");
        }

        [MenuItem("CMER/Build study APK")]
        public static void BuildA()
        {
            Build("A");
        }

        public static void BuildB()
        {
            Build("B");
        }

        private static void Build(string scenario)
        {
            if (EditorApplication.isPlaying)
                throw new BuildFailedException("Stop Play mode first.");
            foreach (char c in Path.GetFullPath("."))
                if (c > 127)
                    throw new BuildFailedException("Use an ASCII-only project path, for example C:/ArchSemantic/UnityProject.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                    throw new BuildFailedException("Install Android Build Support in Unity Hub.");
            }

            if (!QuestProjectConfigurator.TryConfigureForQuest3())
                return;
            string scene = Scenes + "/Model_" + scenario + ".unity";
            if (!File.Exists(scene))
                throw new BuildFailedException("Run 1 Prepare Models and Scenes first.");
            CheckSource(AssetDatabase.LoadAssetAtPath<ModelDefinition>(ModelImporter.Generated + "/" + scenario + "/Model.asset"));
            string output = EditorUtility.SaveFilePanel("Save APK", Path.GetFullPath(".."), "ArchSemantic_" + scenario + "", "apk");
            if (string.IsNullOrEmpty(output))
                return;
            foreach (char c in output)
                if (c > 127)
                    throw new BuildFailedException("Use an ASCII-only APK output path.");
            string oldId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android), oldName = PlayerSettings.productName;
            bool oldBundle = EditorUserBuildSettings.buildAppBundle;
            try
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "org.archsemantic.cmer.study");
                PlayerSettings.productName = "CMER Study";
                PlayerSettings.bundleVersion = "0.9.13";
                PlayerSettings.Android.bundleVersionCode = 103;
                EditorUserBuildSettings.buildAppBundle = false;
                StudyValidation.StampBuild();
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = output, target = BuildTarget.Android, options = BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new BuildFailedException("Build failed; inspect the first Console error.");
                EditorUtility.RevealInFinder(output);
            }
            finally
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, oldId);
                PlayerSettings.productName = oldName;
                EditorUserBuildSettings.buildAppBundle = oldBundle;
            }
        }

        private static void CheckSource(ModelDefinition model)
        {
            if (model == null || model.sourceGhSha256 != LiveGeometryKernel.SourceGhSha256)
                throw new BuildFailedException("Model data and live geometry rules come from different GH revisions.");
            // The GH digest identifies imported provenance, not parity with the revised rules.
            GeometryValidation.Measure(LiveGeometryKernel.Evaluate(model.scenario == "A" ? 0 : 1, 0, 0, 0));
        }
    }
    public static class QuestProjectConfigurator
    {
        private const string XrSettingsFolder = "Assets/XR/Settings";
        private const string XrSettingsPath = XrSettingsFolder + "/XRGeneralSettingsPerBuildTarget.asset";
        private const string OpenXrLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string AndroidManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string InputRestartRequiredKey = "ArchSemanticPilot.InputRestartRequired";
        [MenuItem("CMER/Configure Quest 3 OpenXR")]
        public static void ConfigureForQuest3()
        {
            TryConfigureForQuest3();
        }

        public static bool TryConfigureForQuest3()
        {
            if (SessionState.GetBool(InputRestartRequiredKey, false))
            {
                const string pending = "Input System Package (New) has been selected, but Unity must be restarted before Quest configuration or building can continue.";
                if (Application.isBatchMode)
                    throw new BuildFailedException(pending);
                EditorUtility.DisplayDialog("Restart Unity required", pending + "\n\nSave the project, close Unity, reopen it, then run Configure Quest 3 OpenXR again.", "OK");
                Debug.LogWarning("Arch Semantic Pilot: " + pending);
                return false;
            }

            if (ConfigureInputSystem())
            {
                AssetDatabase.SaveAssets();
                SessionState.SetBool(InputRestartRequiredKey, true);
                const string changed = "Active Input Handling was changed to Input System Package (New). Unity must be restarted once before configuration continues.";
                if (Application.isBatchMode)
                    throw new BuildFailedException(changed + " Re-run the same command after restarting Unity.");
                EditorUtility.DisplayDialog("Restart Unity required", changed + "\n\nSave the project, close Unity, reopen it, then run Configure Quest 3 OpenXR again.", "OK");
                Debug.LogWarning("Arch Semantic Pilot: " + changed);
                return false;
            }

            EnsureAndroidXrSettings();
            ConfigurePlayer();
            AssetDatabase.SaveAssets();
            ValidateOrThrow();
            Debug.Log("Arch Semantic Pilot: Quest 3 OpenXR configuration is present.");
            return true;
        }

        public static void ValidateOrThrow()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new BuildFailedException("Active build target is not Android. Start Unity with -buildTarget Android or switch the platform first.");
            if (ReadActiveInputHandler() != 1)
                throw new BuildFailedException("Active Input Handling must be Input System Package (New). Run Configure Quest 3 OpenXR and restart Unity when prompted.");
            XRGeneralSettings general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (general == null || general.Manager == null)
                throw new BuildFailedException("Android XR Plug-in Management settings are missing.");
            bool hasOpenXr = general.Manager.activeLoaders.Any(loader => loader != null && loader.GetType().FullName == OpenXrLoaderType);
            if (!hasOpenXr)
                throw new BuildFailedException("The OpenXR loader is not assigned for Android.");
            OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr == null)
                throw new BuildFailedException("Android OpenXR settings were not generated. Re-run Configure Quest 3 OpenXR after package import finishes.");
            RequireEnabled<MetaQuestFeature>(openXr, "Meta Quest Support");
            RequireEnabled<HandTracking>(openXr, "Hand Tracking Subsystem");
            RequireEnabled<HandInteractionProfile>(openXr, "Hand Interaction Profile");
            RequireEnabled<OculusTouchControllerProfile>(openXr, "Oculus Touch Controller Profile");
            if ((PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) == 0)
                throw new BuildFailedException("ARM64 is not enabled for Android.");
            if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.GameActivity)
                throw new BuildFailedException("Quest OpenXR requires Android GameActivity for this Unity 6 pilot.");
            string identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            if (identifier != "org.archsemantic.cmer.study" && identifier != "org.archsemantic.pilot.immediate" && identifier != "org.archsemantic.pilot.delayed" && identifier != "org.archsemantic.live.a" && identifier != "org.archsemantic.live.b")
                throw new BuildFailedException("Unexpected Android application identifier: " + identifier);
            if (!File.Exists(AndroidManifestPath))
                throw new BuildFailedException("Quest hand-tracking AndroidManifest.xml is missing.");
            string manifest = File.ReadAllText(AndroidManifestPath);
            if (!manifest.Contains("com.oculus.permission.HAND_TRACKING") || !manifest.Contains("oculus.software.handtracking"))
                throw new BuildFailedException("AndroidManifest.xml lacks the explicit Quest hand-tracking permission or feature.");
        }

        private static void EnsureAndroidXrSettings()
        {
            XRGeneralSettingsPerBuildTarget container = null;
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out container);
            if (container == null)
            {
                Directory.CreateDirectory(XrSettingsFolder);
                container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(container, XrSettingsPath);
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, container, true);
            }

            XRGeneralSettings general = container.SettingsForBuildTarget(BuildTargetGroup.Android);
            if (general == null || general.Manager == null)
            {
                container.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
                general = container.SettingsForBuildTarget(BuildTargetGroup.Android);
            }

            if (general == null || general.Manager == null)
                throw new BuildFailedException("Could not create Android XR Plug-in Management settings.");
            general.InitManagerOnStart = true;
            bool alreadyAssigned = general.Manager.activeLoaders.Any(loader => loader != null && loader.GetType().FullName == OpenXrLoaderType);
            if (!alreadyAssigned && !XRPackageMetadataStore.AssignLoader(general.Manager, OpenXrLoaderType, BuildTargetGroup.Android))
                throw new BuildFailedException("Could not assign Unity OpenXR loader for Android.");
            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(general.Manager);
            AssetDatabase.SaveAssets();
            OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr == null)
                throw new BuildFailedException("OpenXR package settings are not ready. Let package import finish, then run the build again.");
            EnableFeature<MetaQuestFeature>(openXr);
            EnableFeature<HandTracking>(openXr);
            EnableFeature<HandInteractionProfile>(openXr);
            EnableFeature<OculusTouchControllerProfile>(openXr);
            EditorUtility.SetDirty(openXr);
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "org.archsemantic.cmer.study");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)35;
            PlayerSettings.companyName = "Architectural Semantic Research";
            PlayerSettings.productName = "CMER Study";
            PlayerSettings.bundleVersion = "0.9.13";
            PlayerSettings.Android.bundleVersionCode = 103;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        }

        private static bool ConfigureInputSystem()
        {
            UnityEngine.Object[] settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settingsAssets == null || settingsAssets.Length == 0)
                throw new BuildFailedException("ProjectSettings.asset could not be loaded.");
            SerializedObject serialized = new SerializedObject(settingsAssets[0]);
            serialized.Update();
            SerializedProperty inputHandler = serialized.FindProperty("activeInputHandler");
            if (inputHandler == null)
                throw new BuildFailedException("Unity activeInputHandler setting was not found.");
            if (inputHandler.intValue == 1)
                return false;
            inputHandler.intValue = 1; // Input System Package (New)
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settingsAssets[0]);
            return true;
        }

        private static int ReadActiveInputHandler()
        {
            UnityEngine.Object[] settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settingsAssets == null || settingsAssets.Length == 0)
                return -1;
            SerializedObject serialized = new SerializedObject(settingsAssets[0]);
            serialized.Update();
            SerializedProperty inputHandler = serialized.FindProperty("activeInputHandler");
            return inputHandler != null ? inputHandler.intValue : -1;
        }

        private static void EnableFeature<T>(OpenXRSettings settings)
            where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
        {
            T feature = settings.GetFeature<T>();
            if (feature == null)
                throw new BuildFailedException("Required OpenXR feature is unavailable: " + typeof(T).FullName);
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
        }

        private static void RequireEnabled<T>(OpenXRSettings settings, string label)
            where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
        {
            T feature = settings.GetFeature<T>();
            if (feature == null || !feature.enabled)
                throw new BuildFailedException(label + " is not enabled in Android OpenXR settings.");
        }
    }
}
