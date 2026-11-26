using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ArchSemanticPilot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Explicit editor-only acceptance checks. Never attached to the participant build.
public static class WorkspaceValidation
{
    static int checks;
    static void Check(bool ok,string reason) { if(!ok)throw new InvalidOperationException(reason);checks++; }
    static SpatialState Reachable(int scenario, SpatialState requested)
    {
        var state = default(SpatialState);
        for (int axis = 0; axis < 3; axis++)
            state = GeometryDomain.SolveActive(scenario, state, axis, requested[axis], out _);
        Check(GeometryDomain.IsLegal(scenario, state), "Navigation fixture respects authored GH limits");
        return state;
    }
    static void Set(object target,string field,object value) => target.GetType().GetField("<"+field+">k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
    static void Invoke(object target,string method) => target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,null);
    public static void Run()
    {
        Directory.CreateDirectory("Builds/WorkspaceQA");
        File.WriteAllText("Builds/WorkspaceQA/result.json","{\"result\":\"INCOMPLETE\",\"scope\":\"Current run has not completed\"}");
        EditorSceneManager.OpenScene("Assets/ArchSemantic/Scenes/Model_A.unity");
        var s=UnityEngine.Object.FindFirstObjectByType<PilotSession>();
        Invoke(s,"Start");
        Check(s.IsInitialized&&s.ResearcherPreview&&!s.geometry.Current.practice,"Startup must preserve the complete original building");
        var workspace=s.GetComponent<StudyWorkspace>();
        Check(workspace!=null&&workspace.Ready,"Workspace must be initialized");
        // Desktop input avoids pretending to have physical hand tracking in this editor check.
        s.Flow.Settings.inputMode=StudyInputMode.DesktopRehearsal;
        for(int scenario=0;scenario<2;scenario++)
        {
            s.settings.sequence=scenario*2+1;
            for(int state=0;state<2;state++)
            {
                var data=s.geometry.BuildNow(scenario,state==0?default:Reachable(scenario,new SpatialState(.6,-.4,.5)));
                s.geometry.Apply(data,true);
                s.SetScale(StudyScale.Body,true);
                for(int room=0;room<4;room++)
                {
                    s.NavigateSpace(room);
                    var p=s.geometry.transform.InverseTransformPoint(s.headCamera.transform.position);
                    Check(s.geometry.Metrics.Zone(p.x,p.z)==new[]{"F","R","G","C"}[room],"Named navigation must land in its original room, including courtyard");
                }
            }
        }
        // G cannot enter body via any destination; guided walks and pause lock buttons.
        foreach(int sequence in new[]{0,1})
        {
            var flow=new StudyFlow(new StudySettings{sequence=sequence,inputMode=StudyInputMode.DesktopRehearsal});
            flow.Begin();flow.Advance();
            Set(s,"Flow",flow);s.SetScale(StudyScale.Body,true);
            var before=s.headCamera.transform.position;
            s.NavigateSpace(1);Check(s.headCamera.transform.position==before&&!flow.CanNavigateSpaces,"Guided walk navigation lock");
            flow.Advance();s.SetScale(StudyScale.Model,true);
            for(int room=0;room<4;room++)
            {
                s.NavigateSpace(room);
                Check(s.Scale==(sequence==0?StudyScale.Model:StudyScale.Body),"Destination respects G/M scale permissions");
            }
            flow.Pause("tracking");before=s.headCamera.transform.position;
            s.NavigateSpace(0);Check(s.headCamera.transform.position==before&&!s.CanNavigate,"Tracking pause blocks navigation");
        }
        // Actual Unity render of the complete original model and workspace.
        Set(s,"Flow",new StudyFlow(new StudySettings{researcherPreview=true,sequence=1,inputMode=StudyInputMode.DesktopRehearsal}));
        s.geometry.Apply(s.geometry.BuildNow(0,default),true);
        s.SetScale(StudyScale.Body,true);s.GoToSpace(0,true);
        CheckModesAndWorkspace(s,workspace);
        CheckPriorityInput(s,workspace);
        CheckLocksAndNavigation(s,workspace);
        CheckVisibilityRecovery(s,workspace);
        CheckCourtyardRevisions(s);
        CheckTrackedHeight(s);
        CheckFixedGroupsAndViews(s,workspace);
        s.SelectAxis(SemanticAxis.P1);
        s.GoToSpace(0,true);
        Field(s,"workspaceFeedbackUntil",-1f);
        Set(s,"XrRunning",true);
        workspace.Recenter();Field(workspace,"nextRender",0f);Invoke(workspace,"LateUpdate");
        var followRig=(Transform)typeof(StudyWorkspace).GetField("rig",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace);
        var beforeFollow=followRig.position;
        s.headCamera.transform.position+=Vector3.right*.35f;Invoke(workspace,"LateUpdate");
        Check(Vector3.Distance(followRig.position-beforeFollow,Vector3.right*.35f)<.001f,"Called workspace follows body translation at 1:1");
        s.headCamera.transform.position-=Vector3.right*.35f;Invoke(workspace,"LateUpdate");
        var head=s.headCamera;head.stereoTargetEye=StereoTargetEyeMask.None;head.fieldOfView=75;head.rect=new Rect(0,0,1,1);
        var output=new RenderTexture(1280,720,24);output.Create();head.targetTexture=output;
        Render(head,output,"Builds/WorkspaceQA/CMER_Foyer_SideWorkspace.png");
        // Look toward each side to verify legibility and unoccluded previews.
        var original=head.transform.rotation;
        head.transform.rotation=original*Quaternion.Euler(0,-35,0);
        Render(head,output,"Builds/WorkspaceQA/CMER_Parameters_Navigation.png");
        head.transform.rotation=original*Quaternion.Euler(0,35,0);
        Render(head,output,"Builds/WorkspaceQA/CMER_Remote_Models.png");
        head.transform.rotation=original;
        foreach(var mode in new[]{SemanticAxis.P2,SemanticAxis.P3})
        {
            Set(s,"SelectedAxis",mode);
            typeof(StudyWorkspace).GetField("nextRender",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(workspace,0f);
            Invoke(workspace,"LateUpdate");
            Render(head,output,"Builds/WorkspaceQA/CMER_"+mode+"_Mode.png");
        }
        Set(s,"SelectedAxis",SemanticAxis.P1);
        typeof(StudyWorkspace).GetField("nextRender",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(workspace,0f);
        Invoke(workspace,"LateUpdate");
        foreach(var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            if(camera.name.StartsWith("Remote model camera"))
            {
                Check(camera.cullingMask==(1<<8)&&camera.stereoTargetEye==StereoTargetEyeMask.None&&!camera.orthographic,"M remote view is geometry-only interior perspective");
                var rt=camera.targetTexture;RenderTexture.active=rt;
                var pixels=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);pixels.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);pixels.Apply();
                var colors=pixels.GetPixels32();int contrast=0;
                foreach(var c in colors)if(Math.Abs(c.r-colors[0].r)+Math.Abs(c.g-colors[0].g)+Math.Abs(c.b-colors[0].b)>30)contrast++;
                Check(contrast>colors.Length/30,"Remote texture must contain rendered geometry, not a blank quad");
            }
        var remote=GameObject.Find("Remote model camera 0").GetComponent<Camera>();
        var beforePixels=Read(remote.targetTexture);
        s.geometry.Apply(s.geometry.BuildNow(0,Reachable(0,new SpatialState(.8,0,0))),true);
        typeof(StudyWorkspace).GetField("nextRender",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(workspace,0f);
        Invoke(workspace,"LateUpdate");
        var afterPixels=Read(remote.targetTexture);int changed=0;
        for(int i=0;i<beforePixels.Length;i++)if(!beforePixels[i].Equals(afterPixels[i]))changed++;
        Check(changed>100,"P1 must change the actual remote model texture");
        Render(head,output,"Builds/WorkspaceQA/CMER_P1_RemoteChange.png");
        var panel=(Transform)typeof(StudyWorkspace).GetField("controls",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace);
        var position=head.transform.position;
        Check(workspace.TryActivate(position,(panel.TransformPoint(new Vector3(-.145f,-.19f,0))-position).normalized),"A navigation pinch hits the actual rendered button surface");
        Check(head.transform.position==position,"Missing tracking cannot activate a UI destination");
        Check(!workspace.TryActivate(position,head.transform.forward),"A center-view ray is not intercepted by side panels");
        foreach(bool practice in new[]{true,false})
        {
            s.geometry.Apply(s.geometry.BuildNow(0,default,practice),true);
            typeof(StudyWorkspace).GetField("nextRender",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(workspace,0f);
            Invoke(workspace,"LateUpdate");
            Check((bool)typeof(StudyWorkspace).GetField("cachedPractice",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace)==practice,"Remote framing follows practice-to-original-building transitions");
        }
        var procedural=new StudyFlow(new StudySettings());procedural.Begin();procedural.Advance();procedural.Advance();procedural.Advance();procedural.Advance();
        Set(s,"Flow",procedural);workspace.SetVisible(false);Invoke(workspace,"LateUpdate");
        Check(workspace.IsVisible&&s.headsetStatus.gameObject.activeInHierarchy,"Questionnaire instructions remain visible with free UI closed");
        Check(!panel.Find("P1 mode").gameObject.activeInHierarchy,"Questionnaire cannot expose mode controls");
        procedural.Advance();Invoke(workspace,"LateUpdate");
        Check(workspace.IsVisible&&s.headsetStatus.gameObject.activeInHierarchy,"Washout instructions remain visible");
        File.WriteAllText("Builds/WorkspaceQA/result.json","{\"result\":\"PASS\",\"checks\":"+checks+",\"scope\":\"Unity editor navigation permissions, original A/B destinations, actual rendered side UI and remote textures; not physical Quest acceptance\"}");
        RenderTexture.active=null;
        Debug.Log("WORKSPACE_QA_PASS checks="+checks);
    }
    static void CheckModesAndWorkspace(PilotSession s,StudyWorkspace workspace)
    {
        Invoke(s.hands,"Start");
        workspace.Recenter();
        var panel=(Transform)typeof(StudyWorkspace).GetField("controls",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace);
        foreach(int axis in new[]{1,2,3})
        {
            var origin=s.headCamera.transform.position;
            var target=panel.TransformPoint(new Vector3((axis-2)*.195f,.13f,0));
            Check(workspace.TryActivate(origin,(target-origin).normalized),"Mode button is hittable");
            Check((int)s.SelectedAxis==axis,"P1/P2/P3 can all be selected from the same foyer");
            Invoke(s,"Sample");Check((int)s.SelectedAxis==axis,"Sampling body location cannot overwrite selected relation");
        }
        Check(s.CanToggleWorkspace,"Preview permits callable UI");
        s.SetWorkspaceVisible(false);Check(!workspace.RequestedVisible&&!workspace.IsVisible,"Workspace closes entirely");
        Check(!workspace.TryActivate(s.headCamera.transform.position,(panel.position-s.headCamera.transform.position).normalized),"Hidden UI cannot intercept gestures");
        s.SetWorkspaceVisible(true);Check(workspace.RequestedVisible,"Workspace can be recalled");
        foreach(int sequence in new[]{0,1})
        {
            var f=new StudyFlow(new StudySettings{sequence=sequence});f.Begin();f.Advance();
            Check(!f.CanToggleWorkspace,"Initial walkthrough locks supplemental UI");f.Advance();
            Check(f.CanToggleWorkspace,"G and M both allow callable UI during revision");f.Advance();
            Check(!f.CanToggleWorkspace,"Final walkthrough locks supplemental UI");
        }
        // Exercise the real gesture mapping -> asynchronous geometry -> commit pipeline.
        // Synthetic hand inputs for editor tests.
        var handType=typeof(NoHandleSemanticGestureInteractor).GetNestedType("HandFrame",BindingFlags.NonPublic);
        var gesture=s.hands;
        var hashes=new System.Collections.Generic.HashSet<string>();
        foreach(int axis in new[]{1,2,3})foreach(float sign in new[]{-1f,1f})
        {
            typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});
            s.SetScale(StudyScale.Body,true);s.SelectAxis((SemanticAxis)axis);
            var zone=Array.Find(gesture.zones,z=>(int)z.axis==axis);
            Check(zone!=null,"Every relation has an authored gesture calibration");
            typeof(NoHandleSemanticGestureInteractor).GetField("candidate",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(gesture,zone);
            typeof(NoHandleSemanticGestureInteractor).GetField("active",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(gesture,true);
            var direction=Vector3.ProjectOnPlane(-s.geometry.liveDirections[axis-1],Vector3.up).normalized;
            typeof(NoHandleSemanticGestureInteractor).GetField("gestureDirection",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(gesture,direction);
            object left=Hand(handType,new Vector3(-.2f,1,0),true),right=Hand(handType,new Vector3(.2f,1,0),true);
            typeof(NoHandleSemanticGestureInteractor).GetMethod("CaptureBaseline",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,right});
            var travel=axis==3?direction*(zone.travelMeters*.6f*sign):Vector3.right*(zone.travelMeters*.6f*sign);
            right=Hand(handType,new Vector3(.2f,1,0)+travel,true);
            int previous=s.geometry.displayedVersion;
            typeof(NoHandleSemanticGestureInteractor).GetMethod("UpdateActive",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,right});
            Pump(s,()=>s.geometry.displayedVersion>previous);
            Check(s.Displayed[axis-1]*sign>.05,"Each gesture changes its intended parameter in both directions");
            for(int other=0;other<3;other++)if(other!=axis-1)Check(Math.Abs(s.Displayed[other])<1e-6,"Changing a relation does not substitute another parameter");
            hashes.Add(s.geometry.Metrics.geometryHash);
            gesture.EndGesture("HAND_RELEASE");Pump(s,()=>!s.HasPreview);
            Check(s.Effective[axis-1]*sign>.05,"Each semantic gesture commits through the actual session pipeline");
        }
        Check(hashes.Count==6,"Three semantic modes in both directions produce distinct complete geometry");
        var command=typeof(NoHandleSemanticGestureInteractor).GetMethod("CommandPose",BindingFlags.Static|BindingFlags.NonPublic);
        var support=Hand(handType,Vector3.zero,true);handType.GetField("palmUp").SetValue(support,true);
        var fist=Hand(handType,Vector3.zero,false);
        Check((string)command.Invoke(null,new[]{support,fist})=="workspace","Palm up plus other fist selects UI command");
        Check((string)command.Invoke(null,new[]{support,Hand(handType,Vector3.zero,true)})=="","Both open palms remain available for semantic editing");
        var pinch=Hand(handType,Vector3.zero,false);handType.GetField("pinch").SetValue(pinch,true);
        Check((string)command.Invoke(null,new[]{support,pinch})=="context","Existing context command is preserved");
        Check((string)command.Invoke(null,new[]{pinch,pinch})=="scale","Existing scale gesture is preserved");
        typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});
        s.SetScale(StudyScale.Body,true);
    }
    static object Hand(Type type,Vector3 position,bool open)
    {
        var frame=Activator.CreateInstance(type);
        foreach(var name in new[]{"palm","wrist","indexTip"})type.GetField(name).SetValue(frame,position);
        type.GetField("tracked").SetValue(frame,true);type.GetField("openPalm").SetValue(frame,open);return frame;
    }
    static void Field(object target,string name,object value) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
    static object Field(object target,string name) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
    static void CheckPriorityInput(PilotSession s,StudyWorkspace workspace)
    {
        // Synthetic tracking state exercises the same dispatch as XR frames; it is not device evidence.
        var gesture=s.hands;
        var type=typeof(NoHandleSemanticGestureInteractor).GetNestedType("HandFrame",BindingFlags.NonPublic);
        var dispatch=typeof(NoHandleSemanticGestureInteractor).GetMethod("ProcessHands",BindingFlags.Instance|BindingFlags.NonPublic);
        s.Flow.Settings.inputMode=StudyInputMode.QuestHands;Set(s,"XrRunning",true);
        Set(Field(gesture,"leftPoseSource"),"IsTracked",true);Set(Field(gesture,"rightPoseSource"),"IsTracked",true);
        workspace.SetVisible(true);Invoke(workspace,"LateUpdate");workspace.Recenter();
        var panel=(Transform)Field(workspace,"controls");
        var origin=s.headCamera.transform.position;
        var support=Hand(type,origin+Vector3.left*.2f,true);type.GetField("palmUp").SetValue(support,true);
        object Pointer(int mode,bool pinched,Vector3? wrist=null)
        {
            var hand=Hand(type,wrist??origin,false);
            type.GetField("hasAim").SetValue(hand,true);
            type.GetField("aimOrigin").SetValue(hand,origin);
            type.GetField("aimDirection").SetValue(hand,(panel.TransformPoint(new Vector3((mode-2)*.195f,.13f,0))-origin).normalized);
            type.GetField("pointing").SetValue(hand,!pinched);type.GetField("pinch").SetValue(hand,pinched);
            return hand;
        }
        void Send(object right) => dispatch.Invoke(gesture,new[]{support,right});
        s.SelectAxis(SemanticAxis.P1);
        var zone=Array.Find(gesture.zones,z=>z.axis==SemanticAxis.P1);
        var left=Hand(type,new Vector3(-.2f,1,0),true);var rightOpen=Hand(type,new Vector3(.2f,1,0),true);
        Field(gesture,"candidate",zone);Field(gesture,"active",true);
        typeof(NoHandleSemanticGestureInteractor).GetMethod("CaptureBaseline",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,rightOpen});
        typeof(NoHandleSemanticGestureInteractor).GetMethod("UpdateActive",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,rightOpen});
        Check(!s.HasPreview,"Resting open palms cannot create a zero-motion transaction lock");
        Send(Pointer(2,false));
        Check((bool)Field(gesture,"active")&&Field(gesture,"candidate")!=null,"UI hover preserves the unfinished gesture context");
        Check(((LineRenderer)Field(workspace,"pointer")).enabled,"Aiming at a button produces a visible hand pointer");
        Send(Pointer(2,true));
        Check(s.SelectedAxis==SemanticAxis.P2&&!s.HasPreview,"Actual hand dispatcher selects P2 despite open palm-up support and active P1");
        Check(s.Scenario==0,"UI pinch must not trigger the competing building context command");
        Send(Pointer(3,true));Check(s.SelectedAxis==SemanticAxis.P2,"Holding one pinch cannot select additional buttons");
        Send(Pointer(3,false));Send(Pointer(3,true));
        Check(s.SelectedAxis==SemanticAxis.P3,"A released and renewed pinch selects P3");
        Send(Pointer(3,false));
        s.RequestWorkspaceAction(4);
        Field(gesture,"candidate",zone);Field(gesture,"active",true);
        typeof(NoHandleSemanticGestureInteractor).GetMethod("CaptureBaseline",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,rightOpen});
        var moved=Hand(type,new Vector3(.2f+zone.travelMeters*.4f,1,0),true);
        int version=s.geometry.displayedVersion;
        typeof(NoHandleSemanticGestureInteractor).GetMethod("UpdateActive",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gesture,new[]{left,moved});
        Pump(s,()=>s.geometry.displayedVersion>version);
        Send(Pointer(2,false));Check(s.HasPreview,"Aiming does not discard a changed spatial preview");
        Send(Pointer(2,true));Pump(s,()=>!s.HasPreview&&s.SelectedAxis==SemanticAxis.P2);
        Check(s.Effective[0]>.1,"Clicking a new mode saves the preceding changed relation before switching");
        Send(Pointer(2,false));
        s.Flow.Pause("manual");Send(Pointer(3,true));Check(s.SelectedAxis==SemanticAxis.P2,"Paused study blocks UI mode changes");s.Flow.Resume("manual");
        Send(Pointer(3,false));
        var header=(panel.TransformPoint(new Vector3(0,.29f,0))-origin).normalized;
        workspace.ProcessPointer(origin,header,origin,false,true,true);
        workspace.ProcessPointer(origin,header,origin,true,false,true);
        workspace.ProcessPointer(origin,header,origin+Vector3.up*.1f,true,false,true);
        Check(Vector3.Distance((Vector3)Field(workspace,"placementOffset"),Vector3.up*.2f)<.001f,"Pinch-dragging the header moves the whole UI");
        workspace.ProcessPointer(origin,header,origin,false,true,true);Field(workspace,"placementOffset",Vector3.zero);
        workspace.SetVisible(false);Send(Pointer(3,true));Check(s.SelectedAxis==SemanticAxis.P2,"Closed workspace cannot consume or activate a button");
        Set(Field(gesture,"rightPoseSource"),"IsTracked",false);s.RequestWorkspaceAction(6);
        Check(s.SelectedAxis==SemanticAxis.P2,"Untracked hands cannot issue a stale queued UI action");
        gesture.CancelGesture();s.Flow.Settings.inputMode=StudyInputMode.DesktopRehearsal;Set(s,"XrRunning",false);
        typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});
        workspace.SetVisible(true);s.SetScale(StudyScale.Body,true);
    }
    static void CheckLocksAndNavigation(PilotSession s,StudyWorkspace workspace)
    {
        var panel=(Transform)Field(workspace,"controls");
        void Click(float x,float y)
        {
            var origin=s.headCamera.transform.position;
            Check(workspace.TryActivate(origin,(panel.TransformPoint(new Vector3(x,y,0))-origin).normalized),"Rendered control hit");
        }
        foreach(int axis in new[]{1,2,3})
        {
            s.SelectAxis((SemanticAxis)axis);s.SetAxis((SemanticAxis)axis,.25f);
            s.Commit((SemanticAxis)axis);Pump(s,()=>!s.HasPreview);
            Click((axis-2)*.195f,.060f);
            Check(s.IsAxisLocked((SemanticAxis)axis),"Each LOCK button maps to its own relation");
            var before=s.Effective;s.SetAxis((SemanticAxis)axis,-.4f);
            Check(!s.HasPreview&&s.Effective.CloseTo(before),"A locked parameter rejects gesture changes");
        }
        Check(s.ParameterLockMask==7,"P1 P2 and P3 can be held independently and simultaneously");
        var kept=s.Effective;var selected=s.SelectedAxis;
        for(int room=0;room<4;room++)
        {
            Click(room%2==0?-.145f:.145f,-.19f-(room/2)*.085f);
            var p=s.geometry.transform.InverseTransformPoint(s.headCamera.transform.position);
            Check(s.geometry.Metrics.Zone(p.x,p.z)==new[]{"F","R","G","C"}[room],"F/R/G/C actual buttons land in the matching authored space");
            Check(s.SelectedAxis==selected&&s.ParameterLockMask==7&&s.Effective.CloseTo(kept),"Changing rooms preserves selected relation, all values and all holds");
        }
        s.SetScale(StudyScale.Model);s.SetScale(StudyScale.Body);
        Check(s.ParameterLockMask==7&&s.Effective.CloseTo(kept),"Changing scale preserves all held values");
        s.Undo();s.ResetState();Check(!s.HasPreview&&s.Effective.CloseTo(kept),"History cannot silently change held values");
        Click(0,.060f);Check(!s.IsAxisLocked(SemanticAxis.P2)&&s.IsAxisLocked(SemanticAxis.P1)&&s.IsAxisLocked(SemanticAxis.P3),"UNLOCK P2 leaves P1 and P3 held");
        Click(0,.13f);s.SetAxis(SemanticAxis.P2,-.2f);s.Commit(SemanticAxis.P2);Pump(s,()=>!s.HasPreview);
        Check(Math.Abs(s.Effective.p1-kept.p1)<1e-6&&Math.Abs(s.Effective.p3-kept.p3)<1e-6&&s.Effective.p2<0,"In another space, unlocked P2 continues from retained state without changing held P1/P3");
        var evt=(StudyEvent)typeof(PilotSession).GetMethod("Event",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{"validation"});
        Check(evt.parameterLockMask==5,"Research events identify all held parameters");
        var checkpoint=JsonUtility.FromJson<StudyCheckpoint>(JsonUtility.ToJson(new StudyCheckpoint{parameterLockMask=s.ParameterLockMask}));
        Check(checkpoint.parameterLockMask==5,"Checkpoint format preserves independent parameter holds");
        typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});
        Check(s.ParameterLockMask==0,"New building/trial clears temporary holds");s.SetScale(StudyScale.Body,true);
    }
    static void CheckVisibilityRecovery(PilotSession s,StudyWorkspace workspace)
    {
        s.Flow.Settings.inputMode=StudyInputMode.QuestHands;Set(s,"XrRunning",true);
        Set(Field(s.hands,"leftPoseSource"),"IsTracked",true);Set(Field(s.hands,"rightPoseSource"),"IsTracked",true);
        s.SetWorkspaceVisible(true);Invoke(workspace,"LateUpdate");
        var type=typeof(NoHandleSemanticGestureInteractor).GetNestedType("HandFrame",BindingFlags.NonPublic);
        var support=Hand(type,Vector3.zero,true);type.GetField("palmUp").SetValue(support,true);
        var fist=Hand(type,Vector3.zero,false);
        for(int repeat=0;repeat<2;repeat++)
        {
            Field(s.hands,"command","workspace");Field(s.hands,"commandSince",Time.unscaledTime-1f);Field(s.hands,"commandLatched",false);
            typeof(NoHandleSemanticGestureInteractor).GetMethod("HandleCommands",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s.hands,new[]{support,fist});
            Check(workspace.RequestedVisible,"Repeating a recall hand pose cannot hide the workspace");
        }
        s.Flow.Pause("tracking");Invoke(workspace,"LateUpdate");
        var panel=(Transform)Field(workspace,"controls");
        Check(workspace.IsVisible&&panel.Find("P1 mode").gameObject.activeInHierarchy,"Brief tracking loss retains visible controls, with input locked");
        Check(((Transform[])Field(workspace,"screens"))[0].gameObject.activeInHierarchy,"Brief tracking loss retains the last remote model view");
        s.Flow.Resume("tracking");Invoke(workspace,"LateUpdate");
        var origin=s.headCamera.transform.position;var ray=(panel.TransformPoint(new Vector3(0,-.365f,0))-origin).normalized;
        workspace.ProcessPointer(origin,ray,origin,false,true,true);workspace.ProcessPointer(origin,ray,origin,true,false,true);
        Check(workspace.RequestedVisible,"A short accidental pinch cannot hide the UI");
        Field(workspace,"hideStarted",Time.unscaledTime-.7f);workspace.ProcessPointer(origin,ray,origin,true,false,true);
        Check(!workspace.RequestedVisible,"An explicit held pinch on HIDE UI closes it");
        Field(workspace,"placementOffset",Vector3.up*.7f);s.SetWorkspaceVisible(true,true);Invoke(workspace,"LateUpdate");
        Check(workspace.IsVisible&&((Vector3)Field(workspace,"placementOffset")).sqrMagnitude<.0001,"Recall restores a lost or dragged workspace beside the wearer");
        s.hands.CancelGesture();s.Flow.Settings.inputMode=StudyInputMode.DesktopRehearsal;Set(s,"XrRunning",false);
    }
    static void CheckCourtyardRevisions(PilotSession s)
    {
        var nav=(StudyNavigation)Field(s,"navigation");int states=0;
        s.Flow.Settings.inputMode=StudyInputMode.QuestHands;Set(s,"XrRunning",true);
        Set(Field(s.hands,"leftPoseSource"),"IsTracked",true);Set(Field(s.hands,"rightPoseSource"),"IsTracked",true);
        var localHead=s.headCamera.transform.localPosition;
        for(int building=0;building<2;building++)foreach(double a in new[]{-.8,0,.8})foreach(double b in new[]{-.8,0,.8})foreach(double c in new[]{-.8,0,.8})
        {
            var state=GeometryDomain.SolveActive(building,default,0,a,out _);
            state=GeometryDomain.SolveActive(building,state,1,b,out _);
            state=GeometryDomain.SolveActive(building,state,2,c,out _);
            s.settings.sequence=building*2+1;s.geometry.Apply(s.geometry.BuildNow(building,state),true);
            s.headCamera.transform.localPosition=new Vector3(.3f,states%2==0?1.5f:1.85f,-.2f);
            s.SetScale(StudyScale.Body,true);Invoke(nav,"AlignTrackedFloor");
            s.NavigateSpace(3);
            var p=s.geometry.transform.InverseTransformPoint(s.headCamera.transform.position);
            Check(s.geometry.Metrics.Zone(p.x,p.z)=="C","XR courtyard navigation reaches C across full A/B revisions: "+building+" "+a+","+b+","+c);
            Check(nav.FollowFloor(false),"Courtyard landing remains supported on the following frame");states++;
        }
        Check(states==54,"Courtyard regression covers 54 projected A/B parameter cases");
        s.headCamera.transform.localPosition=localHead;s.settings.sequence=1;
        s.Flow.Settings.inputMode=StudyInputMode.DesktopRehearsal;Set(s,"XrRunning",false);
        typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});s.SetScale(StudyScale.Body,true);
    }
    static void CheckFixedGroupsAndViews(PilotSession s,StudyWorkspace workspace)
    {
        // Exercise the same visible group targets as the preview wearer. Formal assignments have no group buttons.
        s.settings=new StudySettings{sequence=1,researcherPreview=true,inputMode=StudyInputMode.DesktopRehearsal};
        Set(s,"Flow",new StudyFlow(s.settings));Set(s,"XrRunning",false);
        var panel=(Transform)Field(workspace,"controls");
        foreach(int group in new[]{0,1})
        {
            workspace.SetVisible(true,true);
            var origin=s.headCamera.transform.position;
            var target=panel.TransformPoint(new Vector3(group==0?-.145f:.145f,.445f,0));
            Check(workspace.TryActivate(origin,(target-origin).normalized),"Preview group button hit");
            Check((int)s.Flow.Condition==group,"Preview group selection invokes correct permission set");
            Check(s.Flow.CanEnterBody==(group==1),"Preview entry respects assigned group");
        }
        foreach(int seq in new[]{0,1,2,3})
        {
            s.settings=new StudySettings{sequence=seq,inputMode=StudyInputMode.DesktopRehearsal};
            Set(s,"Flow",new StudyFlow(s.settings));s.Flow.Begin();
            s.SetSetupSequence((seq+1)%4);s.SetPreviewCondition((StudyCondition)(1-seq%2));
            Check(s.Flow.Condition==s.settings.AssignedCondition&&s.settings.sequence==seq,"Formal group cannot be changed by setup or preview controls");
            Set(s,"XrRunning",true);workspace.SetVisible(true);Invoke(workspace,"LateUpdate");
            Check(!((Transform)Field(workspace,"groupControls")).gameObject.activeSelf,"Formal trial hides group choice controls");
            Set(s,"XrRunning",false);
            s.Flow.Advance();s.Flow.Advance();s.Flow.Advance();s.Flow.Advance();s.Flow.Advance();s.Flow.Advance();
            Check(s.Flow.Period==1&&(int)s.Flow.Condition==seq%2,"Second building retains participant group");
        }
        for(int building=0;building<2;building++)foreach(double extent in new[]{-.8,0,.8})
        {
            var state=GeometryDomain.SolveActive(building,default,0,extent,out _);
            state=GeometryDomain.SolveActive(building,state,1,extent,out _);
            state=GeometryDomain.SolveActive(building,state,2,extent,out _);
            s.geometry.Apply(s.geometry.BuildNow(building,state),true);
            foreach(int group in new[]{0,1})foreach(var scale in new[]{StudyScale.Model,StudyScale.Body})
            {
                if(group==0&&scale==StudyScale.Body)continue;
                s.settings=new StudySettings{sequence=building*2+group,researcherPreview=true,inputMode=StudyInputMode.DesktopRehearsal};
                Set(s,"Flow",new StudyFlow(s.settings));s.SetScale(scale,true);Set(s,"XrRunning",true);
                workspace.SetVisible(true,true);
                foreach(int axis in new[]{1,2,3})
                {
                    Set(s,"SelectedAxis",(SemanticAxis)axis);Field(workspace,"nextRender",0f);Invoke(workspace,"LateUpdate");
                    var cameras=(Camera[])Field(workspace,"cameras");int slot=0;
                    for(int room=0;room<3;room++)if(room!=axis-1)
                    {
                        var camera=cameras[slot++];
                        Check(camera.orthographic==(group==0),"Remote projection matches fixed group");
                        if(group==1)
                        {
                            var local=s.geometry.transform.InverseTransformPoint(camera.transform.position);
                            Check(Vector3.Distance(local,s.geometry.liveStarts[room])<.003f,"M camera uses live authored interior eye position");
                            Check(s.geometry.Metrics.Zone(local.x,local.z)==new[]{"F","R","G"}[room],"M remote viewpoint is inside the labelled room");
                            var forward=s.geometry.transform.TransformDirection(Quaternion.Euler(0,room==1?-65:room==2?65:0,0)*-s.geometry.liveDirections[room]).normalized;
                            Check(Vector3.Dot(camera.transform.forward,forward)>.999f,"M remote uses fixed room-depth orientation");
                            Check(Mathf.Abs(camera.fieldOfView-72)<.01f,"M remote FOV stays fixed across deformation and scale");
                            if(extent==0&&scale==StudyScale.Body)
                                Render(camera,camera.targetTexture,"Builds/WorkspaceQA/Interior_"+(building==0?"A":"B")+"_"+new[]{"F","R","G"}[room]+".png");
                        }
                    }
                }
                Set(s,"XrRunning",false);
            }
        }
        s.settings=new StudySettings{sequence=1,researcherPreview=true,inputMode=StudyInputMode.DesktopRehearsal};
        Set(s,"Flow",new StudyFlow(s.settings));Set(s,"XrRunning",false);
        typeof(PilotSession).GetMethod("LoadState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{default(SpatialState),false});
        s.SetScale(StudyScale.Body,true);s.GoToSpace(0,true);
    }
    static void CheckTrackedHeight(PilotSession s)
    {
        var root=new GameObject("Height regression origin");var offset=new GameObject("Camera Offset");offset.transform.SetParent(root.transform,false);
        var camera=new GameObject("Tracked eye").AddComponent<Camera>();camera.transform.SetParent(offset.transform,false);
        var navigation=new StudyNavigation(root.transform,camera,s.geometry);
        foreach(float eye in new[]{1.5f,1.85f})foreach(float floor in new[]{.06f,2.5f})
        {
            root.transform.position=new Vector3(1,7,2);offset.transform.localPosition=Vector3.up*.3f;camera.transform.localPosition=Vector3.up*eye;
            var before=root.transform.position;
            navigation.AlignFloor(new System.Collections.Generic.List<UnityEngine.XR.XRInputSubsystem>());
            Check(root.transform.position==before,"Unknown XR origin mode never adds an assumed eye height");
            Field(navigation,"floor",floor);Invoke(navigation,"AlignTrackedFloor");
            Check(Math.Abs(camera.transform.position.y-(floor+eye))<.0001,"Floor alignment preserves actual tracked eye height on every level");
            Check(camera.transform.localPosition==Vector3.up*eye,"Navigation cannot overwrite the tracked local head pose");
        }
        UnityEngine.Object.DestroyImmediate(root);
    }
    static void Pump(PilotSession session,Func<bool> complete)
    {
        var deadline=DateTime.UtcNow.AddSeconds(8);
        while(!complete()&&DateTime.UtcNow<deadline)
        {
            typeof(LiveGeometryDriver).GetField("nextSubmit",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session.geometry,-1f);
            Invoke(session.geometry,"Update");Thread.Sleep(12);
        }
        Check(complete(),"Geometry transaction completed before timeout");
    }
    static void Render(Camera camera,RenderTexture target,string path)
    {
        camera.Render();RenderTexture.active=target;
        var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
        image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(image);RenderTexture.active=null;
    }
    static Color32[] Read(RenderTexture target)
    {
        RenderTexture.active=target;var texture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
        texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);texture.Apply();var result=texture.GetPixels32();
        UnityEngine.Object.DestroyImmediate(texture);RenderTexture.active=null;return result;
    }
}
