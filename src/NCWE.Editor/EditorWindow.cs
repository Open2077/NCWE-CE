// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Editor;

internal sealed partial class EditorWindow : Form
{
    private readonly string? _initialGame, _pipeName;
    private readonly bool _noAutoOpen;
    private D3DRenderer? _renderer;
    private ImGuiRenderer? _ui;
    private AutomationServer? _automation;
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=1};
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private double _lastTime;
    private long _frame;
    private readonly SceneCamera _meshCamera=new(), _worldCamera=new();
    private SceneCamera _camera => _worldMode ? _worldCamera : _meshCamera;
    private MeshAsset? _mesh;
    private WeakReference<MeshAsset>? _previousMesh;
    private bool[] _visible=[];
    private uint _lod=1;
    private GameArchiveIndex? _index;
    private string _game="",_search="",_status="Ready",_error="";
    private ArchiveResource[] _filtered=[];
    private bool _busy, _gizmo=true, _wireframe;
    private int _operation;
    private bool _firstLayout=true;
    private float _fontScale=1;
    private readonly CancellationTokenSource _shutdown=new();
    private readonly Dictionary<string,float[]> _controls=[];
    private readonly Dictionary<string,object> _windows=[];
    private readonly Queue<(JsonElement Command,AutomationReply Result)> _commands=[];
    private readonly Queue<(string Path,bool Scene,AutomationReply Result)> _captures=[];
    private readonly Queue<(long Frame,Action Action)> _input=[];
    private Vector2 _viewportPosition,_viewportSize;

    public EditorWindow(string? game,string? pipe,bool noAutoOpen=false)
    {
        _initialGame=game;_pipeName=pipe;_noAutoOpen=noAutoOpen;
        Text="NCWE — Night City World Editor";ClientSize=new(1440,900);MinimumSize=new(900,600);StartPosition=FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.Opaque|ControlStyles.UserPaint,true);
        _timer.Tick+=(_,_)=>Frame();
        MouseMove+=(_,e)=>FeedPointer(e.X,e.Y);
        MouseDown+=(_,e)=>{Focus();FeedMouse(e.X,e.Y);FeedButton(e.Button,true);if(e.Button==MouseButtons.Right)_nativeRightDown=true;};
        MouseUp+=(_,e)=>{FeedButton(e.Button,false);if(e.Button==MouseButtons.Right){_nativeRightDown=false;StopFlyLook();}};
        MouseCaptureChanged+=(_,_)=>{if(!Capture&&_flyPointerCaptured)StopFlyLook(false);};
        MouseWheel+=(_,e)=>{if(_ui!=null)ImGui.GetIO().AddMouseWheelEvent(0,e.Delta/120f);};
        KeyDown+=(_,e)=>FeedKey(e,true);KeyUp+=(_,e)=>FeedKey(e,false);
        KeyPress+=(_,e)=>{if(_ui!=null && !char.IsControl(e.KeyChar))ImGui.GetIO().AddInputCharacter(e.KeyChar);};
        Deactivate+=(_,_)=>SetInputFocus(false);
        Activated+=(_,_)=>SetInputFocus(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);_renderer=new(Handle,ClientSize.Width,ClientSize.Height);_ui=new(_renderer.Device,_renderer.Context);
        if(Gizmo.Version!=ImGui.GetVersion())throw new InvalidOperationException($"ImGuizmo ABI mismatch: {Gizmo.Version} / {ImGui.GetVersion()}");
        _fontScale=DeviceDpi/96f;_ui.RebuildFont(_fontScale);SetMesh(SyntheticMesh.Cube());
        if(_pipeName!=null)_automation=new(_pipeName,QueueCommand);
        _timer.Start();
        if(!string.IsNullOrWhiteSpace(_initialGame)) _=OpenGame(_initialGame);
        else if(!_noAutoOpen) {
            var settings=Path.Combine(Program.StateDirectory,"settings.json");
            if(File.Exists(settings))try {var saved=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(settings));if(saved?.TryGetValue("game",out var path)==true)_=OpenGame(path);}catch(JsonException){}
        }
    }

    protected override void OnPaint(PaintEventArgs e){}
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void OnResize(EventArgs e){StopFlyLook();base.OnResize(e);_firstLayout=true;}
    private void FeedMouse(float x,float y){if(_ui!=null)ImGui.GetIO().AddMousePosEvent(x,y);}
    private void FeedButton(MouseButtons button,bool down){if(_ui!=null)ImGui.GetIO().AddMouseButtonEvent(button==MouseButtons.Right?1:button==MouseButtons.Middle?2:0,down);}
    private void FeedKey(KeyEventArgs e,bool down)
    {
        if(_ui==null)return;var io=ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl,e.Control);io.AddKeyEvent(ImGuiKey.ModShift,e.Shift);io.AddKeyEvent(ImGuiKey.ModAlt,e.Alt);
        var key=e.KeyCode switch {
            Keys.Tab=>ImGuiKey.Tab,Keys.Enter=>ImGuiKey.Enter,Keys.Escape=>ImGuiKey.Escape,Keys.Back=>ImGuiKey.Backspace,
            Keys.Delete=>ImGuiKey.Delete,Keys.Left=>ImGuiKey.LeftArrow,Keys.Right=>ImGuiKey.RightArrow,Keys.Up=>ImGuiKey.UpArrow,Keys.Down=>ImGuiKey.DownArrow,
            Keys.Home=>ImGuiKey.Home,Keys.End=>ImGuiKey.End,Keys.Space=>ImGuiKey.Space,
            >=Keys.A and <=Keys.Z=>(ImGuiKey)((int)ImGuiKey.A+(int)e.KeyCode-(int)Keys.A),_=>ImGuiKey.None};
        if(key!=ImGuiKey.None)io.AddKeyEvent(key,down);
    }

    private async Task OpenGame(string path)
    {
        if(_busy||_shutdown.IsCancellationRequested)return;_busy=true;_error="";_game=path;
        GameArchiveIndex? candidate=null;
        try {
            await StopNeighborhood();
            candidate=new(path);
            await candidate.BuildAsync(new Progress<string>(s=>_status=s),_shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            _index?.Dispose();_index=candidate;candidate=null;_catalog=null;_spatial=null;ApplySearch();
            _status=$"{_index.Meshes.Length:N0} meshes · {_index.ArchiveCount} vanilla archives";
            File.WriteAllText(Path.Combine(Program.StateDirectory,"settings.json"),JsonSerializer.Serialize(new{game=path}));
        }catch(OperationCanceledException){}catch(Exception ex){_error=ex.Message;_status="Installation could not be opened";}
        finally{candidate?.Dispose();EndOperation();}
    }

    private async Task LoadMesh(ulong hash)
    {
        if(_busy||_index==null||_shutdown.IsCancellationRequested)return;_busy=true;_error="";_status="Loading mesh...";
        try {var mesh=await _index.LoadMeshAsync(hash,_shutdown.Token);if(!_shutdown.IsCancellationRequested){SetMesh(mesh);_status=$"Loaded in {mesh.LoadMilliseconds:F0} ms";}}
        catch(OperationCanceledException){}catch(Exception e){_error=e.Message;_status="Unsupported or invalid resource";SetMesh(SyntheticMesh.Cube());}
        finally{EndOperation();}
    }
    private void EndOperation(){_busy=false;if(_shutdown.IsCancellationRequested&&!IsDisposed)BeginInvoke(Close);}
    private void SetMesh(MeshAsset? mesh)
    {
        StopFlyLook();
        _worldMode=false;
        if(_mesh!=null)_previousMesh=new(_mesh);
        _renderer!.SetMesh(mesh);_mesh=mesh;_visible=mesh?.Parts.Select(_=>true).ToArray()??[];_lod=mesh?.LodMasks.FirstOrDefault()??1;
        if(mesh!=null)_camera.Frame(mesh.Bounds);
    }
    private void ApplySearch(){_filtered=_index?.Meshes.Where(m=>m.Path.Contains(_search,StringComparison.OrdinalIgnoreCase)).Take(1500).ToArray()??[];ApplySectorSearch();}
    private void Remember(string name)=>_controls[name]=[ImGui.GetItemRectMin().X,ImGui.GetItemRectMin().Y,ImGui.GetItemRectSize().X,ImGui.GetItemRectSize().Y];
    private bool Button(string text,string id){var hit=ImGui.Button(text);Remember(id);return hit;}
    private void RememberWindow(string name)
    {
        var p=ImGui.GetWindowPos();var s=ImGui.GetWindowSize();
        _windows[name]=new{dockId=ImGui.GetWindowDockID(),rect=new[]{p.X,p.Y,s.X,s.Y}};
    }

    private void Frame()
    {
        if(_ui==null||_renderer==null||WindowState==FormWindowState.Minimized)return;
        _renderer.Resize(ClientSize.Width,ClientSize.Height);
        var scale=DeviceDpi/96f;if(scale!=_fontScale){_fontScale=scale;_ui.RebuildFont(scale);_firstLayout=true;}
        while(_commands.TryDequeue(out var command))Execute(command.Command,command.Result);
        while(_input.TryPeek(out var input)&&input.Frame<=_frame){_input.Dequeue();input.Action();}
        var now=_clock.Elapsed.TotalSeconds;_ui.NewFrame(ClientSize.Width,ClientSize.Height,(float)(now-_lastTime));_lastTime=now;
        Gizmo.Begin(_ui.ImGuiContext);_controls.Clear();
        AdvanceNeighborhood();
        BuildUi();ImGui.Render();
        if(_worldMode)PublishWorldInstances();
        _renderer.UploadWorldStep();
        AutosaveIfNeeded();
        _renderer.RenderScene(_camera,_lod,_visible);_renderer.BeginUi();_ui.Render(ImGui.GetDrawData(),_renderer.SceneTexture);
        while(_captures.TryDequeue(out var capture))try{SaveCapture(capture.Path,capture.Scene);capture.Result.SetResult(new{ok=true,path=capture.Path,frame=_frame});}catch(Exception e){capture.Result.SetResult(new{ok=false,error=e.Message});}
        _renderer.Present();_frame++;
    }

    private void BuildUi()
    {
        ImGui.DockSpaceOverViewport();
        if(ImGui.BeginMainMenuBar()) {
            ImGui.TextColored(new(.32f,.9f,.8f,1),"NCWE");ImGui.SameLine();ImGui.TextDisabled("/  NIGHT CITY WORLD EDITOR");
            ImGui.SameLine(420);BuildProjectMenu();ImGui.EndMainMenuBar();
        }
        float top=32*_fontScale;float left=Math.Min(340*_fontScale,ClientSize.Width*.32f);float right=Math.Min(300*_fontScale,ClientSize.Width*.30f);
        if(_firstLayout){ImGui.SetNextWindowPos(new(0,top));ImGui.SetNextWindowSize(new(left,ClientSize.Height-top));}
        ImGui.Begin("Asset browser");
        RememberWindow("browser");
        ImGui.TextDisabled("GAME INSTALLATION");
        ImGui.SetNextItemWidth(-1);ImGui.InputText("##game",ref _game,1024);Remember("game-path");
        ImGui.BeginDisabled(_busy);
        if(Button("Browse...","browse-game")){using var picker=new FolderBrowserDialog{Description="Select Cyberpunk 2077 installation",UseDescriptionForTitle=true};if(picker.ShowDialog(this)==DialogResult.OK)_=OpenGame(picker.SelectedPath);}
        ImGui.SameLine();if(Button("Index archives","index-game"))_=OpenGame(_game);
        ImGui.EndDisabled();ImGui.Separator();
        BuildNeighborhoodControls();
        BuildBrowserMode();
        ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##search","Search mesh paths...",ref _search,256))ApplySearch();Remember("search");
        ImGui.TextDisabled(BrowserCount());
        ImGui.BeginChild("MeshList",new(0,-95*_fontScale),ImGuiChildFlags.Borders);
        if(_browserMode!=0)BuildWorldBrowser();
        else
        foreach(var resource in _filtered) {
            var label=Path.GetFileName(resource.Path)+"##"+resource.Hash;
            if(ImGui.Selectable(label,_mesh?.ResourcePath==resource.Path)&&!_busy)_=LoadMesh(resource.Hash);
            if(ImGui.IsItemHovered())ImGui.SetTooltip(resource.Path);
        }
        ImGui.EndChild();
        ImGui.BeginDisabled(_busy);if(Button("Synthetic cube","synthetic")){SetMesh(SyntheticMesh.Cube());_error="";_status="Synthetic reference geometry";}
        ImGui.SameLine();if(Button("Close mesh","close-mesh"))SetMesh(null);ImGui.EndDisabled();
        BuildWorldActions();
        ImGui.TextWrapped(_status);ImGui.End();

        if(_firstLayout){ImGui.SetNextWindowPos(new(ClientSize.Width-right,top));ImGui.SetNextWindowSize(new(right,ClientSize.Height-top));}
        ImGui.Begin("Inspector");
        RememberWindow("inspector");
        ImGui.TextDisabled(_worldMode?"SELECTED PLACEMENT":"RESOURCE");
        if(_worldMode)BuildWorldInspector();else ImGui.TextWrapped(_mesh?.ResourcePath??"No mesh selected");
        if(!_worldMode&&_mesh!=null){
            ImGui.TextDisabled(_mesh.SourceArchive);ImGui.Separator();ImGui.Text("LEVEL OF DETAIL");
            foreach(var lod in _mesh.LodMasks){if(ImGui.RadioButton($"LOD mask {lod}",_lod==lod))_lod=lod;Remember($"lod-{lod}");}
            ImGui.Separator();ImGui.Text("SUBMESHES");
            for(int i=0;i<_mesh.Parts.Length;i++){var p=_mesh.Parts[i];if(p.LodMask!=_lod)continue;ImGui.Checkbox($"{i}: {p.Indices.Length/3:N0} triangles",ref _visible[i]);Remember($"part-{i}");}
            ImGui.Separator();ImGui.Text($"Bounds (m): {_mesh.Bounds.Size.X:F2} × {_mesh.Bounds.Size.Y:F2} × {_mesh.Bounds.Size.Z:F2}");
        }
        ImGui.Checkbox("Wireframe",ref _wireframe);Remember("wireframe");_renderer!.Wireframe=_wireframe;
        ImGui.Checkbox("Transform gizmo",ref _gizmo);Remember("gizmo");
        ImGui.Combo("Operation",ref _operation,"Translate\0Rotate\0Scale\0");Remember("operation");
        if(Button("Reset transform","reset-transform")){if(_worldMode)ResetWorldTransform();else _renderer.Model=Matrix4x4.Identity;}
        if(Button(_worldMode?"Frame selection  [F]":"Frame mesh  [F]","frame-mesh")){if(_worldMode)FrameSelection();else if(_mesh!=null)_camera.Frame(_mesh.Bounds);}
        if(_worldMode&&Button("Frame scene  [Home]","frame-world"))FrameWorld();
        ImGui.Separator();ImGui.TextDisabled("DIAGNOSTICS");
        ImGui.Text($"{ImGui.GetIO().Framerate:F0} FPS · frame {_frame}");
        ImGui.Text($"{_renderer.DrawCalls} draws · {_renderer.DrawnTriangles:N0} triangles");
        if(_worldMode) {
            ImGui.Text($"{_renderer.VisiblePlacements:N0} visible · {_renderer.CulledPlacements:N0} outside view");
            ImGui.Text($"World CPU: {_renderer.WorldRenderCpuMilliseconds:F2} ms");
            bool autoLod=_renderer.AutomaticWorldLod;
            if(ImGui.Checkbox("Automatic mesh detail",ref autoLod))_renderer.AutomaticWorldLod=autoLod;
        }
        ImGui.Text($"Mesh GPU: {_renderer.MeshGpuBytes/1048576f:F2} MiB");
        ImGui.Text($"Mesh buffers: {_renderer.MeshBufferCount}");
        ImGui.TextWrapped(_renderer.AdapterName);
        ImGui.TextDisabled($"D3D11 · ImGui {ImGui.GetVersion()} · {DeviceDpi} DPI");
        if(_error.Length>0){ImGui.Separator();ImGui.TextColored(new(1,.45f,.3f,1),"RESOURCE DIAGNOSTIC");ImGui.TextWrapped(_error);}
        ImGui.End();

        if(_firstLayout){ImGui.SetNextWindowPos(new(left,top));ImGui.SetNextWindowSize(new(ClientSize.Width-left-right,ClientSize.Height-top));}
        ImGui.Begin("Viewport",ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse);
        RememberWindow("viewport");
        BuildCameraControls();
        ImGui.TextDisabled(_worldMode?(_camera.IsFlying?"Hold RMB + movement keys · Space/C up/down · Shift faster":"Click select · drag orbit · MMB pan · F frame"):"LMB orbit · MMB pan · wheel zoom · F frame");
        _viewportPosition=ImGui.GetCursorScreenPos();_viewportSize=Vector2.Max(ImGui.GetContentRegionAvail(),new(1));
        _renderer.ResizeScene((int)_viewportSize.X,(int)_viewportSize.Y);
        ImGui.Image(_renderer.SceneTexture!.NativePointer,_viewportSize);Remember("viewport");
        bool hovered=ImGui.IsItemHovered();
        NavigateFlyCamera(hovered);
        if(_worldMode)WorldViewport(hovered);
        else if(_gizmo&&_mesh!=null)Gizmo.Manipulate(_camera.View,_camera.Projection(_viewportSize.X/_viewportSize.Y),ref _renderer.Model,
            _viewportPosition.X,_viewportPosition.Y,_viewportSize.X,_viewportSize.Y,_operation);
        bool activeGizmo=_gizmo&&(_worldMode?Selected is {CanEdit:true}:_mesh!=null);
        if(hovered&&!_flyLookActive&&_inputFocused&&!ImGui.GetIO().WantTextInput&&(!activeGizmo||(Gizmo.IsUsing()==0&&Gizmo.IsOver()==0))){
            var io=ImGui.GetIO();if(!_camera.IsFlying&&ImGui.IsMouseDragging(ImGuiMouseButton.Left))_camera.Orbit(-io.MouseDelta.X*.008f,io.MouseDelta.Y*.008f);
            if(ImGui.IsMouseDragging(ImGuiMouseButton.Middle))_camera.Pan(-io.MouseDelta.X,io.MouseDelta.Y);
            if(io.MouseWheel!=0&&!_camera.IsFlying)_camera.Zoom(io.MouseWheel);
            if(ImGui.IsKeyPressed(ImGuiKey.F)){if(_worldMode)FrameSelection();else if(_mesh!=null)_camera.Frame(_mesh.Bounds);}
            if(_worldMode&&ImGui.IsKeyPressed(ImGuiKey.Home))FrameWorld();
        }
        ImGui.End();_firstLayout=false;
    }

    private Task<string> QueueCommand(JsonElement command)
    {
        var result=new AutomationReply();
        if(IsDisposed){result.SetResult(new{ok=false,error="Window closed"});return result.Task;}
        BeginInvoke(()=>_commands.Enqueue((command,result)));return result.Task;
    }

    private void Execute(JsonElement command,AutomationReply result)
    {
        try {
            var op=command.GetProperty("op").GetString();
            string Str(string key)=>command.GetProperty(key).GetString()!;
            float Float(string key)=>command.GetProperty(key).GetSingle();
            switch(op){
                case "status":result.SetResult(Status());return;
                case "search":_search=Str("text");ApplySearch();result.SetResult(new{ok=true,meshes=_filtered.Take(100)});return;
                case "open-game":_=OpenGame(Str("path"));break;
                case "open":if(_busy)throw new InvalidOperationException("An asset operation is already running.");
                    var path=Str("path");var resource=_index?.Meshes.FirstOrDefault(m=>m.Path.Equals(path,StringComparison.OrdinalIgnoreCase))??throw new FileNotFoundException(path);_=LoadMesh(resource.Hash);break;
                case "synthetic":SetMesh(SyntheticMesh.Cube());_error="";break;
                case "close-mesh":SetMesh(null);break;
                case "resize":ClientSize=new(command.GetProperty("width").GetInt32(),command.GetProperty("height").GetInt32());break;
                case "dpi-message":
                    var dpi=command.GetProperty("dpi").GetInt32();if(dpi is <96 or >192)throw new ArgumentOutOfRangeException("dpi");
                    var suggested=new NativeRect{Left=Left,Top=Top,Right=Right,Bottom=Bottom};
                    SendMessage(Handle,0x02E0,(nint)(dpi|(dpi<<16)),ref suggested);break;
                case "capture":_captures.Enqueue((Str("path"),command.TryGetProperty("scene",out var scene)&&scene.GetBoolean(),result));return;
                case "mouse":FeedMouse(Float("x"),Float("y"));if(command.TryGetProperty("down",out var down))ImGui.GetIO().AddMouseButtonEvent(command.TryGetProperty("button",out var b)?b.GetInt32():0,down.GetBoolean());break;
                case "click":var rect=_controls[Str("control")];FeedMouse(rect[0]+rect[2]/2,rect[1]+rect[3]/2);ImGui.GetIO().AddMouseButtonEvent(0,true);_input.Enqueue((_frame+2,()=>ImGui.GetIO().AddMouseButtonEvent(0,false)));break;
                case "text":ImGui.GetIO().AddInputCharactersUTF8(Str("text"));break;
                case "wheel":ImGui.GetIO().AddMouseWheelEvent(0,Float("delta"));break;
                case "key":var key=Enum.Parse<ImGuiKey>(Str("key"),true);ImGui.GetIO().AddKeyEvent(key,command.GetProperty("down").GetBoolean());break;
                case "orbit":_camera.Orbit(Float("x"),Float("y"));break;
                case "diagnostic-collect":GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();break;
                case "quit":BeginInvoke(Close);break;
                default:if(ExecuteCameraCommand(command,result)||ExecuteRenderingCommand(command,result)||ExecuteNeighborhoodCommand(command,result)||ExecuteWorldCommand(command,result))return;throw new ArgumentException("Unknown command.");
            }
            result.SetResult(new{ok=true,frame=_frame});
        }catch(Exception e){result.SetResult(new{ok=false,error=e.Message});}
    }
    private object Status()=>new{ok=true,frame=_frame,busy=_busy||_renderer!.PendingWorldUploads>0,status=_status,error=_error,game=_game,meshCount=_index?.Meshes.Length??0,
        world=_worldMode,projectName=_document?.Project.Name,dirty=_document?.IsDirty??false,selected=_selectedObject,sectorCount=_document?.ResidentSectors.Count??0,
        mesh=_mesh?.ResourcePath,sourceHash=_mesh?.SourceHash,lod=_lod,lods=_mesh?.LodMasks,parts=_mesh?.Parts.Select((p,i)=>new{i,p.Name,p.LodMask,visible=_visible[i],triangles=p.Indices.Length/3}),
        cpuBytes=_mesh?.CpuBytes??0,gpuBytes=_renderer!.MeshGpuBytes,gpuBuffers=_renderer.MeshBufferCount,drawCalls=_renderer.DrawCalls,triangles=_renderer.DrawnTriangles,
        render=RenderingStatus(),
        width=ClientSize.Width,height=ClientSize.Height,dpi=DeviceDpi,fontScale=_fontScale,adapter=_renderer.AdapterName,controls=_controls.ToDictionary(p=>p.Key,p=>p.Value),windows=_windows.ToDictionary(p=>p.Key,p=>p.Value),
        camera=new{_camera.Yaw,_camera.Pitch,_camera.Distance,Target=new[]{_camera.Target.X,_camera.Target.Y,_camera.Target.Z},
            Eye=new[]{_camera.Eye.X,_camera.Eye.Y,_camera.Eye.Z},Forward=new[]{_camera.Forward.X,_camera.Forward.Y,_camera.Forward.Z},
            flying=_camera.IsFlying,looking=_flyLookActive,speed=_flySpeed,layout=_flyLayout==0?"WASD":"ZQSD",inputFocused=_inputFocused},
        transform=new[]{_renderer.Model.M11,_renderer.Model.M12,_renderer.Model.M13,_renderer.Model.M14,_renderer.Model.M21,_renderer.Model.M22,_renderer.Model.M23,_renderer.Model.M24,_renderer.Model.M31,_renderer.Model.M32,_renderer.Model.M33,_renderer.Model.M34,_renderer.Model.M41,_renderer.Model.M42,_renderer.Model.M43,_renderer.Model.M44},
        processBytes=Process.GetCurrentProcess().PrivateMemorySize64,managedBytes=GC.GetTotalMemory(false),previousMeshAlive=_previousMesh?.TryGetTarget(out _)==true,wireframe=_wireframe,gizmo=_gizmo,
        gizmoOver=Gizmo.IsOver(),gizmoUsing=Gizmo.IsUsing(),mouse=new[]{ImGui.GetIO().MousePos.X,ImGui.GetIO().MousePos.Y}};

    private void SaveCapture(string path,bool scene)
    {
        var frame=_renderer!.Capture(scene);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bitmap=new Bitmap(frame.Width,frame.Height,PixelFormat.Format32bppArgb);
        var bits=bitmap.LockBits(new(0,0,frame.Width,frame.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<frame.Height;y++)Marshal.Copy(frame.Pixels,y*frame.Width*4,bits.Scan0+y*bits.Stride,frame.Width*4);}
        finally{bitmap.UnlockBits(bits);}bitmap.Save(path,ImageFormat.Png);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopFlyLook();
        _timer.Stop();_shutdown.Cancel();_automation?.Dispose();_ui?.Dispose();_renderer?.Dispose();
        _index?.Dispose();_shutdown.Dispose();_timer.Dispose();base.OnFormClosed(e);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if(_document?.IsDirty==true)TryAutosave();
        _shutdown.Cancel();
        var stopping=StopNeighborhood();
        if(!stopping.IsCompleted){e.Cancel=true;_=CloseAfterNeighborhood(stopping);}
        // Keep the message loop alive until the worker relinquishes its archive views.
        if(_busy)e.Cancel=true;
        base.OnFormClosing(e);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",EntryPoint="SendMessageW")]
    private static extern nint SendMessage(nint window,uint message,nint wParam,ref NativeRect lParam);
}
