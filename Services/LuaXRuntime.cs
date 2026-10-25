using BullerStudio.Models;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using WattleScript.Interpreter;

namespace BullerStudio.Services;

public sealed class LuaXRuntime
{
    private const float TerrainCellSize = 4;
    private const int MaxTerrainCells = 4096;
    private static readonly IReadOnlyList<string> SupportedKeyCodes = SceneNodeTypes.KeyCodes;
    private readonly GameProject project;
    private readonly Action<SceneNode, string, DynValue>? propertyChanged;
    private readonly Action<string>? output;
    private readonly Action<SceneNode, SceneNode>? sitOnSeat;
    private readonly Dictionary<string, Table> instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneNode> createdNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> linkedChildKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> touchedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> guiActivatedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> toolEquippedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> toolUnequippedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> toolDeactivatedCallbacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> equippedToolIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> activatedToolIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> guiTextChangedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> promptTriggeredCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> clickDetectorCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> humanoidDiedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> humanoidMoveToFinishedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HumanoidMoveTarget> humanoidMoveTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> explosionHitCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> valueChangedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BindableConnection>> bindableEventCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string NodeId, string? Attribute), List<BindableConnection>> attributeCallbacks = [];
    private readonly Dictionary<(string NodeId, string? Attribute), Table> attributeSignals = [];
    private readonly Dictionary<(string Tag, bool Added), List<BindableConnection>> tagCallbacks = [];
    private readonly Dictionary<(string Tag, bool Added), Table> tagSignals = [];
    private readonly Dictionary<string, List<BindableConnection>> runServiceCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BindableConnection>> userInputCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextActionBinding> contextActionBindings = new(StringComparer.Ordinal);
    private readonly HashSet<string> pressedKeyCodes = new(StringComparer.Ordinal);
    private float[] mouseLocation = [0, 0];
    private readonly List<TweenState> activeTweens = [];
    private readonly Dictionary<string, ScriptCallback> bindableFunctionCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string NodeId, string Callback), ScriptCallback> remoteFunctionCallbacks = [];
    private readonly Dictionary<Coroutine, Stack<BindableInvocationScope>> bindableInvocationScopes = new();
    private readonly Dictionary<string, bool> humanoidWasAlive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> humanoidLastHealth = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> humanoidHealthChangedCallbacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeContacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> detonatedExplosions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> activeExplosions = new(StringComparer.Ordinal);
    private readonly List<DebrisItem> debrisItems = [];
    private readonly List<ScriptExecution> activeScripts = [];
    private readonly List<ScheduledTask> scheduledTasks = [];
    private readonly List<WeakReference<PathRuntimeState>> pathStates = [];
    private readonly Queue<string> pendingScriptStarts = new();
    private readonly HashSet<string> queuedScriptIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> startedScriptIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> starterCharacterModels = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Table, LuaXTypeTag> runtimeTypeTags = new();
    private readonly bool isStudio;
    private double simulationTime;
    private Table tweenService = null!;
    private Script script = null!;
    private DynValue bindableInvokeFactory = DynValue.Nil;
    private DynValue contextualRequireFactory = DynValue.Nil;
    private GameScript? currentGameScript;
    private Table vector3Metatable = null!;
    private Table vector2Metatable = null!;

    private sealed record LuaXTypeTag(string Name);
    private sealed record ScriptCallback(GameScript GameScript, DynValue Function, string Name, Table? Connection = null);
    private sealed record BindableInvocationScope(GameScript? Previous, GameScript Owner);
    private sealed class BindableConnection(GameScript owner, DynValue function, Table table)
    {
        public GameScript Owner { get; } = owner;
        public DynValue Function { get; } = function;
        public Table Table { get; } = table;
        public bool Connected { get; private set; } = true;

        public void Disconnect()
        {
            Connected = false;
            Table.Set("Connected", DynValue.NewBoolean(false));
        }
    }
    private sealed class PathRuntimeState(Table path)
    {
        public Table Path { get; } = path;
        public List<Vector3> Waypoints { get; set; } = [];
        public List<string> LinkIds { get; set; } = [];
        public HashSet<int> BlockedWaypoints { get; } = [];
        public List<BindableConnection> BlockedCallbacks { get; } = [];
        public List<BindableConnection> UnblockedCallbacks { get; } = [];
        public float AgentRadius { get; set; }
        public float AgentHeight { get; set; }
        public float CheckElapsed { get; set; }
        public bool Destroyed { get; set; }
    }
    private sealed record ScriptExecution(GameScript GameScript, Coroutine Coroutine, bool ReportFinished, string? CallbackName = null);
    private sealed record ContextActionBinding(string Name, GameScript Owner, DynValue Callback, string[] Inputs, int Priority, bool CreateTouchButton);
    private sealed class DebrisItem(SceneNode node, float remaining)
    {
        public SceneNode Node { get; } = node;
        public float Remaining { get; set; } = remaining;
    }
    private sealed class ScheduledTask(GameScript owner, DynValue handle, DynValue[] arguments, float remaining, bool deferred)
    {
        public GameScript Owner { get; } = owner;
        public DynValue Handle { get; } = handle;
        public DynValue[] Arguments { get; } = arguments;
        public float Remaining { get; set; } = remaining;
        public bool Deferred { get; } = deferred;
    }
    private sealed record TweenInfoData(float Time, string EasingStyle, string EasingDirection, int RepeatCount, bool Reverses, float DelayTime);
    private sealed class HumanoidMoveTarget(Vector3 position)
    {
        public Vector3 Position { get; } = position;
        public float Elapsed { get; set; }
        public bool JumpInitiated { get; set; }
    }
    private readonly record struct PathGridCell(int X, int Z);
    private sealed class TweenState(SceneNode target, TweenInfoData info, Dictionary<string, DynValue> goals, GameScript owner, Table instance)
    {
        public SceneNode Target { get; } = target;
        public TweenInfoData Info { get; } = info;
        public Dictionary<string, DynValue> Goals { get; } = goals;
        public GameScript Owner { get; } = owner;
        public Table Instance { get; } = instance;
        public Dictionary<string, DynValue> Starts { get; set; } = new(StringComparer.Ordinal);
        public List<ScriptCallback> CompletedCallbacks { get; } = [];
        public float Elapsed { get; set; }
        public bool Playing { get; set; }
        public string PlaybackState { get; set; } = "Begin";
    }
    private static readonly HashSet<string> CreatableClasses = new(StringComparer.Ordinal)
    {
        "Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "VehicleSeat", "Camera", "Model", "WorldModel", "Folder", "Tool", "Accessory", "Configuration", "BindableEvent", "BindableFunction", "RemoteEvent", "UnreliableRemoteEvent", "RemoteFunction", "Script", "ModuleScript", "LocalScript", "Humanoid", "BodyColors", "PathfindingModifier", "PathfindingLink",
        "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector2Value", "Vector3Value", "Color3Value", "ObjectValue",
        "PointLight", "SpotLight", "SurfaceLight", "Decal", "Texture", "Sound", "SoundGroup", "PitchShiftSoundEffect", "DistortionSoundEffect", "EqualizerSoundEffect", "ChorusSoundEffect", "CompressorSoundEffect", "EchoSoundEffect", "FlangeSoundEffect", "TremoloSoundEffect", "ReverbSoundEffect", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "Trail", "BodyForce", "BodyThrust", "BodyVelocity", "BodyPosition", "BodyGyro", "BodyAngularVelocity", "RocketPropulsion", "VectorForce", "LineForce", "Torque", "SpringConstraint", "TorsionSpringConstraint", "RopeConstraint", "RodConstraint", "BallSocketConstraint", "HingeConstraint", "PrismaticConstraint", "CylindricalConstraint", "PlaneConstraint", "UniversalConstraint", "RigidConstraint", "LinearVelocity", "AlignPosition", "AlignOrientation", "AngularVelocity", "ProximityPrompt", "ClickDetector", "Highlight", "SelectionBox", "SelectionSphere", "ForceField", "Explosion", "WeldConstraint", "Weld", "Motor6D", "NoCollisionConstraint", "Attachment", "Beam", "Team",
        "Sky", "Atmosphere", "Clouds", "BlurEffect", "ColorCorrectionEffect", "SurfaceAppearance", "ScreenGui", "BillboardGui", "SurfaceGui", "Frame", "ScrollingFrame", "ViewportFrame", "VideoFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox",
        "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIPageLayout", "UICanvasGroup", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"
    };

    public LuaXRuntime(GameProject project, Action<SceneNode, string, DynValue>? propertyChanged = null, Action<string>? output = null, bool isStudio = true, Action<SceneNode, SceneNode>? sitOnSeat = null)
    {
        this.project = project;
        this.propertyChanged = propertyChanged;
        this.output = output;
        this.isStudio = isStudio;
        this.sitOnSeat = sitOnSeat;
        Initialize();
    }

    public IReadOnlyCollection<SceneNode> Parts => GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
    public bool HasPendingScripts => activeScripts.Count > 0 || pendingScriptStarts.Count > 0 || scheduledTasks.Count > 0 || activeExplosions.Count > 0 || debrisItems.Count > 0;

    public void RunAll()
    {
        foreach (var callbacks in bindableEventCallbacks.Values)
            foreach (var connection in callbacks) connection.Disconnect();
        bindableEventCallbacks.Clear();
        foreach (var callbacks in attributeCallbacks.Values)
            foreach (var connection in callbacks) connection.Disconnect();
        attributeCallbacks.Clear();
        foreach (var callbacks in tagCallbacks.Values)
            foreach (var connection in callbacks) connection.Disconnect();
        tagCallbacks.Clear();
        foreach (var callbacks in runServiceCallbacks.Values)
            foreach (var connection in callbacks) connection.Disconnect();
        runServiceCallbacks.Clear();
        foreach (var callbacks in userInputCallbacks.Values)
            foreach (var connection in callbacks) connection.Disconnect();
        userInputCallbacks.Clear();
        contextActionBindings.Clear();
        pressedKeyCodes.Clear();
        mouseLocation = [0, 0];
        touchedCallbacks.Clear();
        guiActivatedCallbacks.Clear();
        toolEquippedCallbacks.Clear();
        toolUnequippedCallbacks.Clear();
        toolDeactivatedCallbacks.Clear();
        guiTextChangedCallbacks.Clear();
        promptTriggeredCallbacks.Clear();
        clickDetectorCallbacks.Clear();
        DisconnectCallbacksInRegistry(humanoidDiedCallbacks);
        DisconnectCallbacksInRegistry(humanoidHealthChangedCallbacks);
        DisconnectCallbacksInRegistry(humanoidMoveToFinishedCallbacks);
        humanoidDiedCallbacks.Clear();
        humanoidHealthChangedCallbacks.Clear();
        humanoidMoveToFinishedCallbacks.Clear();
        humanoidMoveTargets.Clear();
        explosionHitCallbacks.Clear();
        valueChangedCallbacks.Clear();
        foreach (var humanoid in KnownNodes().Where(node => node.Type == "Humanoid"))
        {
            humanoidWasAlive[humanoid.Id] = humanoid.Health > 0;
            humanoidLastHealth[humanoid.Id] = humanoid.Health;
        }
        equippedToolIds.Clear();
        activatedToolIds.Clear();
        foreach (var rocket in KnownNodes().Where(node => node.Type == "RocketPropulsion"))
        {
            rocket.RocketPropulsionActive = false;
            rocket.RocketPropulsionReachedTarget = false;
        }
        bindableFunctionCallbacks.Clear();
        remoteFunctionCallbacks.Clear();
        explosionHitCallbacks.Clear();
        detonatedExplosions.Clear();
        activeExplosions.Clear();
        debrisItems.Clear();
        activeTweens.Clear();
        bindableInvocationScopes.Clear();
        activeScripts.Clear();
        scheduledTasks.Clear();
        pendingScriptStarts.Clear();
        queuedScriptIds.Clear();
        startedScriptIds.Clear();
        simulationTime = 0;
        foreach (var player in project.Players.Children.Where(node => node.Type == "Player"))
            if (FindNode(player.Character) is { Type: "Model" } character) AddStarterCharacterScripts(character);
        foreach (var gameScript in project.Scripts.ToArray())
        {
            var node = FindScriptNode(gameScript.Id);
            if (node is not null && (!SceneNodeTypes.IsBaseScript(node) || !node.Enabled || !IsScriptAttachedForExecution(node))) continue;
            startedScriptIds.Add(gameScript.Id);
            StartScript(gameScript, false);
        }
        StartQueuedScripts();
        ResumePendingScripts(.05f);
        SynchronizeInstances();
    }

    public void Run(string scriptId)
    {
        var gameScript = project.Scripts.FirstOrDefault(item => item.Id == scriptId);
        if (gameScript is null || FindScriptNode(scriptId) is { Type: "ModuleScript" }) return;
        foreach (var player in project.Players.Children.Where(node => node.Type == "Player"))
            if (FindNode(player.Character) is { Type: "Model" } character) AddStarterCharacterScripts(character);
        startedScriptIds.Add(scriptId);
        StartScript(gameScript, true);
        StartQueuedScripts();
    }

    public void Update(float elapsedSeconds = .05f)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        UpdateBeforePhysics(elapsed);
        AdvanceTweens(elapsed);
        FireFrameSignal("PostSimulation", DynValue.NewNumber(elapsed));
        RunDeferredTasks();
        ResumePendingScripts(elapsed);
        RunDelayedTasks(elapsed);
        StartQueuedScripts();
        AdvanceExplosions(elapsed);
        AdvanceDebris(elapsed);
        UpdatePathStates(elapsed);
        FireEndOfFrameSignals(elapsed);
    }

    public void UpdateBeforePhysics(float elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        FireFrameSignal("Stepped", DynValue.NewNumber(simulationTime), DynValue.NewNumber(elapsed));
        FireFrameSignal("PreSimulation", DynValue.NewNumber(elapsed));
        RunDeferredTasks();
        UpdateHumanoidMovement(elapsed);
        simulationTime += elapsed;
    }

    private SceneNode? FindScriptNode(string id) =>
        GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id)
        ?? (createdNodes.TryGetValue(id, out var created) ? created : null);

    private bool IsScriptAttachedForExecution(SceneNode node)
    {
        if (node.Type == "LocalScript")
        {
            var localScriptCurrent = node;
            while (FindParentNode(localScriptCurrent) is { } parent)
            {
                if ((parent.Type is "PlayerGui" or "Backpack" or "PlayerScripts")
                    && project.Players.Children.Any(player => FindParentNode(player, parent) is not null)) return true;
                if (project.Players.Children.Any(player => player.Type == "Player" && player.Character == localScriptCurrent.Id)
                    && GameProjectStore.Flatten(project.Workspace.Children).Any(node => node.Id == localScriptCurrent.Id)) return true;
                if (ReferenceEquals(parent, project.Workspace))
                    return project.Players.Children.Any(player => player.Type == "Player" && player.Character == localScriptCurrent.Id);
                if (ReferenceEquals(parent, project.Players) || ReferenceEquals(parent, project.Gui)
                    || ReferenceEquals(parent, project.ServerScriptService) || ReferenceEquals(parent, project.ServerStorage)
                    || ReferenceEquals(parent, project.StarterPack) || ReferenceEquals(parent, project.StarterPlayer) || ReferenceEquals(parent, project.ReplicatedStorage)) return false;
                localScriptCurrent = parent;
            }
            return false;
        }
        var current = node;
        while (FindParentNode(current) is { } parent)
        {
            if (ReferenceEquals(parent, project.Workspace) || ReferenceEquals(parent, project.ServerScriptService) || ReferenceEquals(parent, project.Gui)) return true;
            if (ReferenceEquals(parent, project.ReplicatedStorage) || ReferenceEquals(parent, project.ServerStorage) || ReferenceEquals(parent, project.StarterPack) || ReferenceEquals(parent, project.StarterPlayer) || ReferenceEquals(parent, project.Players)) return false;
            current = parent;
        }
        return false;
    }

    private void QueueRunnableScripts(SceneNode source)
    {
        foreach (var node in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
            if (SceneNodeTypes.IsBaseScript(node) && node.Enabled && IsScriptAttachedForExecution(node) && !startedScriptIds.Contains(node.Id)
                && project.Scripts.Any(item => item.Id == node.Id) && queuedScriptIds.Add(node.Id))
                pendingScriptStarts.Enqueue(node.Id);
    }

    private void StartQueuedScripts()
    {
        for (var count = 0; count < 64 && pendingScriptStarts.Count > 0; count++)
        {
            var id = pendingScriptStarts.Dequeue();
            queuedScriptIds.Remove(id);
            if (startedScriptIds.Contains(id) || FindScriptNode(id) is not { } node || !SceneNodeTypes.IsBaseScript(node) || !node.Enabled || !IsScriptAttachedForExecution(node)) continue;
            var gameScript = project.Scripts.FirstOrDefault(item => item.Id == id);
            if (gameScript is null) continue;
            startedScriptIds.Add(id);
            StartScript(gameScript, false);
        }
    }

    private void AddStarterCharacterScripts(SceneNode character)
    {
        if (starterCharacterModels.Contains(character.Id)) return;
        var templates = project.StarterPlayer.Children.FirstOrDefault(node => node.Name == "StarterCharacterScripts" && node.Type == "Folder");
        if (templates is null || !instances.ContainsKey(character.Id)) return;
        starterCharacterModels.Add(character.Id);
        foreach (var template in templates.Children.ToArray())
        {
            var cloneValue = CloneInstance(template);
            if (ReadNodeArgument(cloneValue) is not { } clone || !instances.TryGetValue(character.Id, out var characterInstance)) continue;
            TrySetParent(clone, DynValue.NewTable(characterInstance));
        }
    }

    private void StartScript(GameScript gameScript, bool reportFinished)
    {
        BindCurrentScript(gameScript);
        try
        {
            var coroutine = script.CreateCoroutine(script.LoadString(gameScript.Source, CreateExecutionEnvironment(FindScriptNode(gameScript.Id)), gameScript.Name)).Coroutine;
            var execution = new ScriptExecution(gameScript, coroutine, reportFinished);
            coroutine.Resume();
            SynchronizeInstances();
            RunDeferredTasks();
            if (coroutine.State == CoroutineState.Dead)
            {
                if (reportFinished) output?.Invoke($"{gameScript.Name} finished.");
            }
            else if (project.Scripts.Any(item => item.Id == gameScript.Id)
                && (FindScriptNode(gameScript.Id) is not { } node || node.Enabled && IsScriptAttachedForExecution(node))) activeScripts.Add(execution);
        }
        catch (Exception exception)
        {
            output?.Invoke($"{gameScript.Name}: {exception.Message}");
            SynchronizeInstances();
        }
    }

    private void ResumePendingScripts(float elapsedSeconds)
    {
        foreach (var execution in activeScripts.ToArray())
        {
            if (!activeScripts.Contains(execution)) continue;
            if (execution.Coroutine.State == CoroutineState.Dead || !project.Scripts.Any(item => item.Id == execution.GameScript.Id)
                || FindScriptNode(execution.GameScript.Id) is { } node && (!node.Enabled || !IsScriptAttachedForExecution(node)))
            {
                activeScripts.Remove(execution);
                bindableInvocationScopes.Remove(execution.Coroutine);
                continue;
            }
            if (bindableInvocationScopes.TryGetValue(execution.Coroutine, out var scopes) && scopes.Count > 0)
            {
                if (!IsTaskOwnerActive(scopes.Peek().Owner))
                {
                    activeScripts.Remove(execution);
                    bindableInvocationScopes.Remove(execution.Coroutine);
                    output?.Invoke($"{execution.GameScript.Name}: BindableFunction callback stopped.");
                    continue;
                }
                BindCurrentScript(scopes.Peek().Owner);
            }
            else BindCurrentScript(execution.GameScript);
            try
            {
                execution.Coroutine.Resume(DynValue.NewNumber(elapsedSeconds));
                SynchronizeInstances();
                if (execution.Coroutine.State != CoroutineState.Dead && project.Scripts.Any(item => item.Id == execution.GameScript.Id)
                    && (FindScriptNode(execution.GameScript.Id) is not { } resumedNode || resumedNode.Enabled && IsScriptAttachedForExecution(resumedNode))) continue;
                activeScripts.Remove(execution);
                bindableInvocationScopes.Remove(execution.Coroutine);
                if (execution.ReportFinished) output?.Invoke($"{execution.GameScript.Name} finished.");
            }
            catch (Exception exception)
            {
                activeScripts.Remove(execution);
                bindableInvocationScopes.Remove(execution.Coroutine);
                output?.Invoke(execution.CallbackName is null
                    ? $"{execution.GameScript.Name}: {exception.Message}"
                    : $"{execution.GameScript.Name} {execution.CallbackName} callback: {exception.Message}");
                SynchronizeInstances();
            }
        }
        RunDeferredTasks();
    }

    public void UpdateTouched(float elapsedSeconds = .05f)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        FireFrameSignal("PostSimulation", DynValue.NewNumber(elapsed));
        RunDeferredTasks();
        ResumePendingScripts(elapsed);
        RunDelayedTasks(elapsed);
        SynchronizeInstances();
        AdvanceExplosions(elapsed);
        AdvanceDebris(elapsed);
        StartQueuedScripts();
        var touchingNow = new HashSet<string>(StringComparer.Ordinal);
        var parts = Parts.ToArray();
        for (var first = 0; first < parts.Length; first++)
        for (var second = first + 1; second < parts.Length; second++)
        {
            if (!CollisionGeometry.Intersects(parts[first], parts[second])) continue;
            var key = string.CompareOrdinal(parts[first].Id, parts[second].Id) < 0 ? $"{parts[first].Id}:{parts[second].Id}" : $"{parts[second].Id}:{parts[first].Id}";
            touchingNow.Add(key);
            if (activeContacts.Contains(key)) continue;
            if (parts[first].CanTouch) DispatchTouch(parts[first], parts[second]);
            if (parts[second].CanTouch) DispatchTouch(parts[second], parts[first]);
        }
        activeContacts.Clear();
        activeContacts.UnionWith(touchingNow);
        FireEndOfFrameSignals(elapsed);
    }

    public void SynchronizeExternalProperty(SceneNode node, string property)
    {
        if (instances.TryGetValue(node.Id, out var instance))
            WriteInstanceProperty(node, instance, property.ToLowerInvariant());
    }

    public void RegisterExternalSubtree(SceneNode root)
    {
        if (!IsAttached(root)) return;
        var nodes = new[] { root }.Concat(GameProjectStore.Flatten(root.Children)).ToArray();
        foreach (var node in nodes)
        {
            if (instances.ContainsKey(node.Id)) continue;
            instances[node.Id] = CreateInstance(node);
            if (node.Type == "Humanoid") humanoidWasAlive[node.Id] = node.Health > 0;
        }
        UpdateParentLinks();
    }

    public void RemoveExternalSubtree(SceneNode root)
    {
        if (instances.ContainsKey(root.Id)) DestroyInstance(root);
    }

    public void SetExternalPlayerCharacter(SceneNode player, SceneNode? model)
    {
        if (player.Type != "Player" || !instances.TryGetValue(player.Id, out var playerInstance)
            || model is not null && (model.Type != "Model" || !instances.ContainsKey(model.Id))) return;
        var nextCharacter = model?.Id ?? string.Empty;
        if (player.Character == nextCharacter) return;
        FirePlayerCharacterSignal(player, "CharacterRemoving", player.Character);
        player.Character = nextCharacter;
        playerInstance.Set("Character", GetCharacterValue(player));
        if (model is not null)
        {
            FirePlayerCharacterSignal(player, "CharacterAdded", model.Id);
            AddStarterCharacterScripts(model);
        }
        propertyChanged?.Invoke(player, "Character", playerInstance.Get("Character"));
    }

    public bool SetParent(SceneNode node, SceneNode parent)
    {
        if (!instances.TryGetValue(node.Id, out _) || !instances.TryGetValue(parent.Id, out var parentInstance)) return false;
        return TrySetParent(node, DynValue.NewTable(parentInstance));
    }

    private void Initialize()
    {
        script = new Script(CoreModules.Preset_SoftSandbox);
        InstallLuauMath();
        InstallVector3Metatable();
        bindableInvokeFactory = script.DoString("""
            local function pack(...)
                return { n = select('#', ...), ... }
            end
            return function(getCallback, beginCallback, endCallback)
                return function(self, ...)
                    local callback = getCallback()
                    while type(callback) ~= "function" do
                        coroutine.yield()
                        callback = getCallback()
                    end
                    local thread = coroutine.running()
                    if not beginCallback(thread) then error('BindableFunction callback is unavailable') end
                    local result = pack(pcall(callback, ...))
                    endCallback(thread)
                    if not result[1] then error(result[2]) end
                    return table.unpack(result, 2, result.n)
                end
            end
            """);
        foreach (var node in GameProjectStore.Flatten(project))
        {
            var instance = CreateInstance(node);
            instances[node.Id] = instance;
            if (node.Type == "Humanoid") humanoidWasAlive[node.Id] = node.Health > 0;
            if (node.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui")) script.Globals.Set(node.Name, DynValue.NewTable(instance));
        }
        foreach (var node in GameProjectStore.Flatten(project))
        {
            var target = instances[node.Id];
            foreach (var child in node.Children)
                if (!IsReservedBindableMember(node, child.Name) && instances.TryGetValue(child.Id, out var childTable))
                    target.Set(child.Name, DynValue.NewTable(childTable));
            var parent = FindParentNode(node);
            target.Set("Parent", parent is not null && instances.TryGetValue(parent.Id, out var parentTable) ? DynValue.NewTable(parentTable) : DynValue.Nil);
        }
        foreach (var node in GameProjectStore.Flatten(project).Where(node => node.Type == "ObjectValue"))
            instances[node.Id].Set("Value", GetValueObjectValue(node));
        foreach (var node in GameProjectStore.Flatten(project).Where(node => node.Type is "SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui"))
            instances[node.Id].Set("Adornee", GetObjectValueReference(node.Adornee));
        foreach (var node in GameProjectStore.Flatten(project).Where(node => node.Type == "Sound"))
            instances[node.Id].Set("SoundGroup", GetSoundGroupReferenceValue(node.SoundGroupId));
        foreach (var model in GameProjectStore.Flatten(project).Where(node => node.Type == "Model"))
            instances[model.Id].Set("PrimaryPart", GetModelPrimaryPartReference(model));
        var game = new Table(script);
        runtimeTypeTags.Add(game, new LuaXTypeTag("DataModel"));
        var workspace = instances[project.Workspace.Id];
        var players = instances[project.Players.Id];
        var teams = instances[project.Teams.Id];
        var lighting = instances[project.Lighting.Id];
        var soundService = instances[project.SoundService.Id];
        var replicatedStorage = instances[project.ReplicatedStorage.Id];
        var serverScriptService = instances[project.ServerScriptService.Id];
        var serverStorage = instances[project.ServerStorage.Id];
        var starterPack = instances[project.StarterPack.Id];
        var starterPlayer = instances[project.StarterPlayer.Id];
        var gui = instances[project.Gui.Id];
        var httpService = CreateHttpService();
        var pathfindingService = CreatePathfindingService();
        var debris = new Table(script);
        runtimeTypeTags.Add(debris, new LuaXTypeTag("Debris"));
        debris.Set("Name", DynValue.NewString("Debris"));
        debris.Set("ClassName", DynValue.NewString("Debris"));
        debris.Set("AddItem", DynValue.NewCallback((_, args) => AddDebrisItem(debris, args)));
        var collectionService = CreateCollectionService();
        var runService = CreateRunService();
        var userInputService = CreateUserInputService();
        var contextActionService = CreateContextActionService();
        tweenService = CreateTweenService();
        game.Set("Workspace", DynValue.NewTable(workspace));
        game.Set("Players", DynValue.NewTable(players));
        game.Set("Teams", DynValue.NewTable(teams));
        game.Set("Lighting", DynValue.NewTable(lighting));
        game.Set("SoundService", DynValue.NewTable(soundService));
        game.Set("ReplicatedStorage", DynValue.NewTable(replicatedStorage));
        game.Set("ServerScriptService", DynValue.NewTable(serverScriptService));
        game.Set("ServerStorage", DynValue.NewTable(serverStorage));
        game.Set("StarterPack", DynValue.NewTable(starterPack));
        game.Set("StarterPlayer", DynValue.NewTable(starterPlayer));
        game.Set("Gui", DynValue.NewTable(gui));
        game.Set("StarterGui", DynValue.NewTable(gui));
        game.Set("Debris", DynValue.NewTable(debris));
        game.Set("CollectionService", DynValue.NewTable(collectionService));
        game.Set("RunService", DynValue.NewTable(runService));
        game.Set("UserInputService", DynValue.NewTable(userInputService));
        game.Set("ContextActionService", DynValue.NewTable(contextActionService));
        game.Set("TweenService", DynValue.NewTable(tweenService));
        game.Set("HttpService", DynValue.NewTable(httpService));
        game.Set("PathfindingService", DynValue.NewTable(pathfindingService));
        game.Set("GetService", DynValue.NewCallback((_, args) =>
        {
            var serviceName = args.Count > 1 ? args[1].CastToString() : args.Count == 1 ? args[0].CastToString() : null;
            return serviceName switch
            {
                "Workspace" => DynValue.NewTable(workspace),
                "Players" => DynValue.NewTable(players),
                "Teams" => DynValue.NewTable(teams),
                "Lighting" => DynValue.NewTable(lighting),
                "SoundService" => DynValue.NewTable(soundService),
                "ReplicatedStorage" => DynValue.NewTable(replicatedStorage),
                "ServerScriptService" => DynValue.NewTable(serverScriptService),
                "ServerStorage" => DynValue.NewTable(serverStorage),
                "StarterPack" => DynValue.NewTable(starterPack),
                "StarterPlayer" => DynValue.NewTable(starterPlayer),
                "Gui" => DynValue.NewTable(gui),
                "StarterGui" => DynValue.NewTable(gui),
                "Debris" => DynValue.NewTable(debris),
                "CollectionService" => DynValue.NewTable(collectionService),
                "RunService" => DynValue.NewTable(runService),
                "UserInputService" => DynValue.NewTable(userInputService),
                "ContextActionService" => DynValue.NewTable(contextActionService),
                "TweenService" => DynValue.NewTable(tweenService),
                "HttpService" => DynValue.NewTable(httpService),
                "PathfindingService" => DynValue.NewTable(pathfindingService),
                _ => throw new ScriptRuntimeException(string.IsNullOrWhiteSpace(serviceName)
                    ? "GetService expects a valid service name."
                    : $"GetService does not recognize service '{serviceName}'.")
            };
        }));
        script.Globals.Set("game", DynValue.NewTable(game));
        script.Globals.Set("workspace", DynValue.NewTable(workspace));
        script.Globals.Set("Players", DynValue.NewTable(players));
        script.Globals.Set("Teams", DynValue.NewTable(teams));
        script.Globals.Set("Lighting", DynValue.NewTable(lighting));
        script.Globals.Set("SoundService", DynValue.NewTable(soundService));
        script.Globals.Set("ReplicatedStorage", DynValue.NewTable(replicatedStorage));
        script.Globals.Set("ServerScriptService", DynValue.NewTable(serverScriptService));
        script.Globals.Set("ServerStorage", DynValue.NewTable(serverStorage));
        script.Globals.Set("StarterPack", DynValue.NewTable(starterPack));
        script.Globals.Set("StarterPlayer", DynValue.NewTable(starterPlayer));
        script.Globals.Set("Gui", DynValue.NewTable(gui));
        script.Globals.Set("StarterGui", DynValue.NewTable(gui));
        script.Globals.Set("TweenService", DynValue.NewTable(tweenService));
        script.Globals.Set("UserInputService", DynValue.NewTable(userInputService));
        script.Globals.Set("ContextActionService", DynValue.NewTable(contextActionService));
        var instanceLibrary = new Table(script);
        instanceLibrary.Set("new", DynValue.NewCallback((_, args) => NewInstance(args)));
        script.Globals.Set("Instance", DynValue.NewTable(instanceLibrary));
        var cameraModeEnum = new Table(script);
        cameraModeEnum.Set("Custom", DynValue.NewString("Custom"));
        cameraModeEnum.Set("Subject", DynValue.NewString("Subject"));
        var layoutDirectionEnum = new Table(script);
        layoutDirectionEnum.Set("Vertical", DynValue.NewString("Vertical"));
        layoutDirectionEnum.Set("Horizontal", DynValue.NewString("Horizontal"));
        var scrollingDirectionEnum = new Table(script);
        foreach (var direction in new[] { "X", "Y", "XY" }) scrollingDirectionEnum.Set(direction, DynValue.NewString(direction));
        var automaticSizeEnum = new Table(script);
        foreach (var size in new[] { "None", "X", "Y", "XY" }) automaticSizeEnum.Set(size, DynValue.NewString(size));
        var enumTable = new Table(script);
        var keyCodeEnum = new Table(script);
        foreach (var keyCode in SupportedKeyCodes.Where(keyCode => keyCode != "Unknown")) keyCodeEnum.Set(keyCode, DynValue.NewString(keyCode));
        enumTable.Set("KeyCode", DynValue.NewTable(keyCodeEnum));
        var userInputTypeEnum = new Table(script);
        foreach (var inputType in new[] { "Keyboard", "MouseButton1", "MouseButton2", "MouseButton3", "MouseMovement", "Touch", "Gamepad1" })
            userInputTypeEnum.Set(inputType, DynValue.NewString(inputType));
        enumTable.Set("UserInputType", DynValue.NewTable(userInputTypeEnum));
        var userInputStateEnum = new Table(script);
        foreach (var inputState in new[] { "Begin", "Change", "End" }) userInputStateEnum.Set(inputState, DynValue.NewString(inputState));
        enumTable.Set("UserInputState", DynValue.NewTable(userInputStateEnum));
        var contextActionResultEnum = new Table(script);
        contextActionResultEnum.Set("Pass", DynValue.NewString("Pass"));
        contextActionResultEnum.Set("Sink", DynValue.NewString("Sink"));
        enumTable.Set("ContextActionResult", DynValue.NewTable(contextActionResultEnum));
        enumTable.Set("CameraMode", DynValue.NewTable(cameraModeEnum));
        var easingStyleEnum = new Table(script);
        foreach (var style in new[] { "Linear", "Sine", "Quad", "Cubic", "Quart", "Quint", "Exponential", "Circular", "Back", "Bounce", "Elastic" }) easingStyleEnum.Set(style, DynValue.NewString(style));
        enumTable.Set("EasingStyle", DynValue.NewTable(easingStyleEnum));
        var easingDirectionEnum = new Table(script);
        foreach (var direction in new[] { "In", "Out", "InOut" }) easingDirectionEnum.Set(direction, DynValue.NewString(direction));
        enumTable.Set("EasingDirection", DynValue.NewTable(easingDirectionEnum));
        var playbackStateEnum = new Table(script);
        foreach (var state in new[] { "Begin", "Delayed", "Playing", "Paused", "Completed", "Cancelled" }) playbackStateEnum.Set(state, DynValue.NewString(state));
        enumTable.Set("PlaybackState", DynValue.NewTable(playbackStateEnum));
        var pathStatusEnum = new Table(script);
        foreach (var state in new[] { "Success", "NoPath", "ClosestNoPath", "FailStartNotEmpty", "FailFinishNotEmpty", "FailFinishUnreachable", "FailTooFar" }) pathStatusEnum.Set(state, DynValue.NewString(state));
        enumTable.Set("PathStatus", DynValue.NewTable(pathStatusEnum));
        var pathWaypointActionEnum = new Table(script);
        foreach (var action in new[] { "Walk", "Jump", "Custom" }) pathWaypointActionEnum.Set(action, DynValue.NewString(action));
        enumTable.Set("PathWaypointAction", DynValue.NewTable(pathWaypointActionEnum));
        var materialEnum = new Table(script);
        foreach (var material in SceneNodeTypes.EnumMaterials) materialEnum.Set(material, DynValue.NewString(material));
        enumTable.Set("Material", DynValue.NewTable(materialEnum));
        enumTable.Set("LayoutDirection", DynValue.NewTable(layoutDirectionEnum));
        enumTable.Set("FillDirection", DynValue.NewTable(layoutDirectionEnum));
        enumTable.Set("ScrollingDirection", DynValue.NewTable(scrollingDirectionEnum));
        enumTable.Set("AutomaticSize", DynValue.NewTable(automaticSizeEnum));
        var positionAlignmentModeEnum = new Table(script);
        positionAlignmentModeEnum.Set("OneAttachment", DynValue.NewString("OneAttachment"));
        positionAlignmentModeEnum.Set("TwoAttachment", DynValue.NewString("TwoAttachment"));
        enumTable.Set("PositionAlignmentMode", DynValue.NewTable(positionAlignmentModeEnum));
        var orientationAlignmentModeEnum = new Table(script);
        orientationAlignmentModeEnum.Set("OneAttachment", DynValue.NewString("OneAttachment"));
        orientationAlignmentModeEnum.Set("TwoAttachment", DynValue.NewString("TwoAttachment"));
        enumTable.Set("OrientationAlignmentMode", DynValue.NewTable(orientationAlignmentModeEnum));
        var raycastFilterEnum = new Table(script);
        raycastFilterEnum.Set("Exclude", DynValue.NewString("Exclude"));
        raycastFilterEnum.Set("Include", DynValue.NewString("Include"));
        enumTable.Set("RaycastFilterType", DynValue.NewTable(raycastFilterEnum));
        var normalIdEnum = new Table(script);
        foreach (var face in new[] { "Top", "Bottom", "Front", "Back", "Left", "Right" })
            normalIdEnum.Set(face, DynValue.NewString(face));
        enumTable.Set("NormalId", DynValue.NewTable(normalIdEnum));
        var textureModeEnum = new Table(script);
        foreach (var mode in new[] { "Stretch", "Wrap", "Static" }) textureModeEnum.Set(mode, DynValue.NewString(mode));
        enumTable.Set("TextureMode", DynValue.NewTable(textureModeEnum));
        var actuatorRelativeToEnum = new Table(script);
        actuatorRelativeToEnum.Set("World", DynValue.NewString("World"));
        actuatorRelativeToEnum.Set("Attachment0", DynValue.NewString("Attachment0"));
        actuatorRelativeToEnum.Set("Attachment1", DynValue.NewString("Attachment1"));
        enumTable.Set("ActuatorRelativeTo", DynValue.NewTable(actuatorRelativeToEnum));
        var velocityConstraintModeEnum = new Table(script);
        foreach (var mode in new[] { "Vector", "Line", "Plane" }) velocityConstraintModeEnum.Set(mode, DynValue.NewString(mode));
        enumTable.Set("VelocityConstraintMode", DynValue.NewTable(velocityConstraintModeEnum));
        script.Globals.Set("Enum", DynValue.NewTable(enumTable));
        var tweenInfoLibrary = new Table(script);
        tweenInfoLibrary.Set("new", DynValue.NewCallback((_, args) => CreateTweenInfo(args)));
        script.Globals.Set("TweenInfo", DynValue.NewTable(tweenInfoLibrary));
        var raycastParams = new Table(script);
        raycastParams.Set("new", DynValue.NewCallback((_, _) =>
        {
            var parameters = new Table(script);
            runtimeTypeTags.Add(parameters, new LuaXTypeTag("RaycastParams"));
            parameters.Set("FilterDescendantsInstances", DynValue.NewTable(new Table(script)));
            parameters.Set("FilterType", DynValue.NewString("Exclude"));
            parameters.Set("RespectCanCollide", DynValue.NewBoolean(false));
            return DynValue.NewTable(parameters);
        }));
        script.Globals.Set("RaycastParams", DynValue.NewTable(raycastParams));
        var overlapParams = new Table(script);
        overlapParams.Set("new", DynValue.NewCallback((_, _) =>
        {
            var parameters = new Table(script);
            runtimeTypeTags.Add(parameters, new LuaXTypeTag("OverlapParams"));
            parameters.Set("FilterDescendantsInstances", DynValue.NewTable(new Table(script)));
            parameters.Set("FilterType", DynValue.NewString("Exclude"));
            parameters.Set("MaxParts", DynValue.NewNumber(0));
            parameters.Set("RespectCanCollide", DynValue.NewBoolean(false));
            return DynValue.NewTable(parameters);
        }));
        script.Globals.Set("OverlapParams", DynValue.NewTable(overlapParams));
        var vector3 = new Table(script);
        vector3.Set("new", DynValue.NewCallback((_, args) => VectorValue([NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2)])));
        vector3.Set("zero", VectorValue([0, 0, 0]));
        vector3.Set("one", VectorValue([1, 1, 1]));
        script.Globals.Set("Vector3", DynValue.NewTable(vector3));
        var vector2 = new Table(script);
        vector2.Set("new", DynValue.NewCallback((_, args) => Vector2Value([NumberArgument(args, 0), NumberArgument(args, 1)])));
        vector2.Set("zero", Vector2Value([0, 0]));
        vector2.Set("one", Vector2Value([1, 1]));
        script.Globals.Set("Vector2", DynValue.NewTable(vector2));
        var rect = new Table(script);
        rect.Set("new", DynValue.NewCallback((_, args) =>
        {
            if (args.Count >= 2 && TryReadVector2(args[0], out var min) && TryReadVector2(args[1], out var max))
                return RectValue(NormalizeSliceCenter([min[0], min[1], max[0], max[1]]));
            if (args.Count >= 4 && Enumerable.Range(0, 4).All(index => args[index].Type == DataType.Number && double.IsFinite(args[index].Number)))
                return RectValue(NormalizeSliceCenter(Enumerable.Range(0, 4).Select(index => (float)args[index].Number).ToArray()));
            return DynValue.Nil;
        }));
        script.Globals.Set("Rect", DynValue.NewTable(rect));
        script.Globals.Set("LookAtRotation", DynValue.NewCallback((_, args) => CreateLookAtRotation(args)));
        var color3 = new Table(script);
        color3.Set("new", DynValue.NewCallback((_, args) => CreateColor(NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2))));
        color3.Set("fromRGB", DynValue.NewCallback((_, args) => CreateColor(NumberArgument(args, 0) / 255f, NumberArgument(args, 1) / 255f, NumberArgument(args, 2) / 255f)));
        color3.Set("fromHSV", DynValue.NewCallback((_, args) => CreateColorFromHsv(NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2))));
        color3.Set("fromHex", DynValue.NewCallback((_, args) =>
        {
            var hex = args.Count > 0 ? args[0].CastToString() ?? string.Empty : string.Empty;
            return ColorValue(hex.Length > 0 && !hex.StartsWith('#') ? $"#{hex}" : hex);
        }));
        script.Globals.Set("Color3", DynValue.NewTable(color3));
        var numberRange = new Table(script);
        numberRange.Set("new", DynValue.NewCallback((_, args) =>
        {
            if (args.Count == 0 || args[0].Type != DataType.Number || !double.IsFinite(args[0].Number)) return DynValue.Nil;
            var minimum = (float)args[0].Number;
            var maximum = args.Count > 1 && args[1].Type == DataType.Number && double.IsFinite(args[1].Number) ? (float)args[1].Number : minimum;
            return NumberRangeValue([Math.Min(minimum, maximum), Math.Max(minimum, maximum)]);
        }));
        script.Globals.Set("NumberRange", DynValue.NewTable(numberRange));
        var numberSequenceKeypoint = new Table(script);
        numberSequenceKeypoint.Set("new", DynValue.NewCallback((_, args) =>
            args.Count >= 2 && args[0].Type == DataType.Number && args[1].Type == DataType.Number
            && float.IsFinite((float)args[0].Number) && float.IsFinite((float)args[1].Number)
            && (args.Count < 3 || args[2].Type == DataType.Number && float.IsFinite((float)args[2].Number) && args[2].Number >= 0)
                ? NumberSequenceKeypointValue(new ParticleNumberKeypoint((float)args[0].Number, (float)args[1].Number,
                    args.Count > 2 ? (float)args[2].Number : 0)) : DynValue.Nil));
        script.Globals.Set("NumberSequenceKeypoint", DynValue.NewTable(numberSequenceKeypoint));
        var numberSequence = new Table(script);
        numberSequence.Set("new", DynValue.NewCallback((_, args) => CreateNumberSequence(args)));
        script.Globals.Set("NumberSequence", DynValue.NewTable(numberSequence));
        var colorSequenceKeypoint = new Table(script);
        colorSequenceKeypoint.Set("new", DynValue.NewCallback((_, args) =>
            args.Count >= 2 && args[0].Type == DataType.Number && float.IsFinite((float)args[0].Number)
            && IsTypedValue(args[1], "Color3")
                ? ColorSequenceKeypointValue(new ParticleColorKeypoint((float)args[0].Number, ReadColor(args[1], "#FFFFFF")))
                : DynValue.Nil));
        script.Globals.Set("ColorSequenceKeypoint", DynValue.NewTable(colorSequenceKeypoint));
        var colorSequence = new Table(script);
        colorSequence.Set("new", DynValue.NewCallback((_, args) => CreateColorSequence(args)));
        script.Globals.Set("ColorSequence", DynValue.NewTable(colorSequence));
        var udim = new Table(script);
        udim.Set("new", DynValue.NewCallback((_, args) => CreateUDim(NumberArgument(args, 0), NumberArgument(args, 1))));
        script.Globals.Set("UDim", DynValue.NewTable(udim));
        var udim2 = new Table(script);
        udim2.Set("new", DynValue.NewCallback((_, args) => CreateUDim2(NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2), NumberArgument(args, 3))));
        udim2.Set("fromOffset", DynValue.NewCallback((_, args) => CreateUDim2(0, NumberArgument(args, 0), 0, NumberArgument(args, 1))));
        udim2.Set("fromScale", DynValue.NewCallback((_, args) => CreateUDim2(NumberArgument(args, 0), 0, NumberArgument(args, 1), 0)));
        udim2.Set("zero", CreateUDim2(0, 0, 0, 0));
        script.Globals.Set("UDim2", DynValue.NewTable(udim2));
        UpdateParentLinks();
        script.Globals.Set("SetProperty", DynValue.NewCallback(SetProperty));
        script.Globals.Set("GetProperty", DynValue.NewCallback(GetProperty));
        script.Globals.Set("__WaitForChildLookup", DynValue.NewCallback((_, args) => WaitForChild(args)));
        script.Globals.Set("GetAssetPath", DynValue.NewCallback((_, args) => GetAssetPath(args)));
        script.Globals.Set("ConnectTouched", DynValue.NewCallback(ConnectTouched));
        script.Globals.Set("typeof", DynValue.NewCallback((_, args) => DynValue.NewString(args.Count == 0 ? "nil" : GetLuaXTypeName(args[0]))));
        script.Globals.Set("print", DynValue.NewCallback((_, args) => { var values = new List<string>(); for (var index = 0; index < args.Count; index++) values.Add(args[index].ToPrintString()); output?.Invoke(string.Join("\t", values)); return DynValue.Nil; }));
        script.Globals.Set("warn", DynValue.NewCallback((_, args) => { var values = new List<string>(); for (var index = 0; index < args.Count; index++) values.Add(args[index].ToPrintString()); output?.Invoke($"Warning: {string.Join("\t", values)}"); return DynValue.Nil; }));
        InstallWaitForChild();
        InstallTaskLibrary();
        InstallModuleRequire();
    }

    private void InstallModuleRequire()
    {
        var requireFactory = script.DoString("""
            return function(resolveModule, loadModule)
                local cache = {}
                local loading = {}
                return function(reference, origin)
                    local module = resolveModule(reference, origin)
                    if module == nil then error('require could not find a ModuleScript') end
                    if cache[module] ~= nil then return cache[module] end
                    local caller = coroutine.running()
                    while loading[module] do
                        if loading[module] == caller then error('Requested module was required recursively') end
                        coroutine.yield()
                        if cache[module] ~= nil then return cache[module] end
                    end
                    local loader = loadModule(module)
                    if type(loader) ~= 'function' then error('require expects a ModuleScript instance') end
                    loading[module] = caller
                    local succeeded, result = pcall(loader)
                    loading[module] = nil
                    if not succeeded then error(result) end
                    if result == nil then error('ModuleScript must return one value') end
                    cache[module] = result
                    return result
                end
            end
            """);
        var resolve = DynValue.NewCallback((_, args) => ResolveModuleReference(args));
        var load = DynValue.NewCallback((_, args) => LoadModule(args));
        script.Globals.Set("require", script.Call(requireFactory, resolve, load));
        contextualRequireFactory = script.DoString("""
            return function(baseRequire, origin)
                return function(reference)
                    return baseRequire(reference, origin)
                end
            end
            """);
    }

    private Table CreateExecutionEnvironment(SceneNode? source)
    {
        var environment = new Table(script);
        if (source is not null && instances.TryGetValue(source.Id, out var instance))
        {
            var origin = DynValue.NewTable(instance);
            environment.Set("script", origin);
            environment.Set("require", script.Call(contextualRequireFactory, script.Globals.Get("require"), origin));
        }
        var metatable = new Table(script);
        metatable.Set("__index", DynValue.NewTable(script.Globals));
        environment.MetaTable = metatable;
        return environment;
    }

    private DynValue ResolveModuleReference(CallbackArguments args)
    {
        if (args.Count == 0) return DynValue.Nil;
        SceneNode? module = null;
        if (args[0].Type == DataType.Table)
        {
            var candidate = ReadNodeArgument(args[0]);
            if (candidate is not null && instances.TryGetValue(candidate.Id, out var actual)
                && ReferenceEquals(args[0].Table, actual)) module = candidate;
        }
        else if (args[0].Type == DataType.String)
        {
            SceneNode? origin = null;
            if (args.Count > 1 && args[1].Type == DataType.Table)
            {
                var candidate = ReadNodeArgument(args[1]);
                if (candidate is not null && instances.TryGetValue(candidate.Id, out var actual)
                    && ReferenceEquals(args[1].Table, actual)) origin = candidate;
            }
            module = ResolveModulePath(args[0].String, origin);
        }
        return module is { Type: "ModuleScript" } && instances.TryGetValue(module.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private SceneNode? ResolveModulePath(string path, SceneNode? origin)
    {
        SceneNode? current;
        string remaining;
        var atGameRoot = false;
        if (path.StartsWith("@game/", StringComparison.Ordinal))
        {
            current = null;
            atGameRoot = true;
            remaining = path[6..];
        }
        else if (path.StartsWith("@self/", StringComparison.Ordinal))
        {
            current = origin;
            remaining = path[6..];
        }
        else if (path.StartsWith("./", StringComparison.Ordinal))
        {
            current = origin is null ? null : FindParentNode(origin);
            remaining = path[2..];
        }
        else if (path.StartsWith("../", StringComparison.Ordinal))
        {
            var parent = origin is null ? null : FindParentNode(origin);
            current = parent is null ? null : FindParentNode(parent);
            atGameRoot = parent is not null && IsServiceRoot(parent);
            remaining = path[3..];
        }
        else return null;
        if ((current is null && !atGameRoot) || remaining.Length == 0) return null;
        foreach (var segment in remaining.Split('/'))
        {
            if (segment.Length == 0) return null;
            if (segment == "..")
            {
                if (atGameRoot || current is null) return null;
                var parent = FindParentNode(current);
                atGameRoot = parent is null && IsServiceRoot(current);
                current = parent;
            }
            else if (segment != ".")
            {
                current = atGameRoot ? GetServiceRoot(segment) : current?.Children.FirstOrDefault(child => child.Name == segment);
                atGameRoot = false;
            }
            if (current is null && !atGameRoot) return null;
        }
        return current;
    }

    private SceneNode? GetServiceRoot(string name) => name switch
    {
        "Workspace" => project.Workspace,
        "Players" => project.Players,
        "Teams" => project.Teams,
        "Lighting" => project.Lighting,
        "SoundService" => project.SoundService,
        "ReplicatedStorage" => project.ReplicatedStorage,
        "ServerScriptService" => project.ServerScriptService,
        "ServerStorage" => project.ServerStorage,
        "StarterPack" => project.StarterPack,
        "StarterPlayer" => project.StarterPlayer,
        "Gui" => project.Gui,
        "StarterGui" => project.Gui,
        _ => null
    };

    private bool IsServiceRoot(SceneNode node) =>
        ReferenceEquals(node, project.Workspace) || ReferenceEquals(node, project.Players)
        || ReferenceEquals(node, project.Teams) || ReferenceEquals(node, project.Lighting) || ReferenceEquals(node, project.SoundService) || ReferenceEquals(node, project.ReplicatedStorage)
        || ReferenceEquals(node, project.ServerScriptService) || ReferenceEquals(node, project.ServerStorage) || ReferenceEquals(node, project.StarterPack) || ReferenceEquals(node, project.StarterPlayer)
        || ReferenceEquals(node, project.Gui);

    private DynValue LoadModule(CallbackArguments args)
    {
        if (args.Count != 1 || args[0].Type != DataType.Table) return DynValue.Nil;
        var node = ReadNodeArgument(args[0]);
        if (node is not { Type: "ModuleScript" } || !instances.TryGetValue(node.Id, out var instance)
            || !ReferenceEquals(args[0].Table, instance)
            || project.Scripts.FirstOrDefault(item => item.Id == node.Id) is not { } module) return DynValue.Nil;
        return script.LoadString(module.Source, CreateExecutionEnvironment(node), module.Name);
    }

    private void InstallWaitForChild()
    {
        script.DoString("""
            function WaitForChild(parent, name, timeout)
                local waited = 0
                local maxWait = type(timeout) == "number" and timeout or nil
                while true do
                    local child = __WaitForChildLookup(parent, name)
                    if child ~= nil then return child end
                    if maxWait ~= nil and waited >= maxWait then return nil end
                    local elapsed = coroutine.yield()
                    waited = waited + (type(elapsed) == "number" and elapsed or 0.05)
                end
            end
            waitforchild = WaitForChild
            """);
        var waitForChild = script.Globals.Get("WaitForChild");
        foreach (var instance in instances.Values)
        {
            instance.Set("WaitForChild", waitForChild);
            instance.Set("waitforchild", waitForChild);
        }
    }

    private void InstallTaskLibrary()
    {
        script.DoString("""
            function wait(duration)
                local target = type(duration) == "number" and math.max(0, duration) or 0
                local elapsed = 0
                repeat
                    local step = coroutine.yield()
                    elapsed = elapsed + (type(step) == "number" and step or 0.05)
                until elapsed >= target
                return elapsed
            end
            """);
        var taskLibrary = new Table(script);
        taskLibrary.Set("wait", script.Globals.Get("wait"));
        taskLibrary.Set("spawn", DynValue.NewCallback((_, args) => ScheduleTask(args, 0, 0, deferred: false, startNow: true)));
        taskLibrary.Set("defer", DynValue.NewCallback((_, args) => ScheduleTask(args, 0, 0, deferred: true, startNow: false)));
        taskLibrary.Set("delay", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 2 || args[0].Type != DataType.Number || !double.IsFinite(args[0].Number)) return DynValue.Nil;
            return ScheduleTask(args, 1, (float)Math.Clamp(args[0].Number, 0, 86400), deferred: false, startNow: false);
        }));
        taskLibrary.Set("cancel", DynValue.NewCallback((_, args) =>
        {
            if (args.Count == 0 || args[0].Type != DataType.Thread) return DynValue.Nil;
            var coroutine = args[0].Coroutine;
            scheduledTasks.RemoveAll(item => ReferenceEquals(item.Handle.Coroutine, coroutine));
            activeScripts.RemoveAll(item => ReferenceEquals(item.Coroutine, coroutine));
            bindableInvocationScopes.Remove(coroutine);
            return DynValue.Nil;
        }));
        script.Globals.Set("task", DynValue.NewTable(taskLibrary));
    }

    private DynValue ScheduleTask(CallbackArguments args, int functionIndex, float delaySeconds, bool deferred, bool startNow)
    {
        if (currentGameScript is null || args.Count <= functionIndex) return DynValue.Nil;
        DynValue handle;
        try
        {
            handle = args[functionIndex].Type == DataType.Thread
                ? args[functionIndex] : script.CreateCoroutine(args[functionIndex]);
        }
        catch (Exception exception)
        {
            output?.Invoke($"task: {exception.Message}");
            return DynValue.Nil;
        }
        var arguments = new DynValue[args.Count - functionIndex - 1];
        for (var index = 0; index < arguments.Length; index++) arguments[index] = args[functionIndex + 1 + index];
        var scheduled = new ScheduledTask(currentGameScript, handle, arguments, delaySeconds, deferred);
        if (startNow) StartScheduledTask(scheduled);
        else scheduledTasks.Add(scheduled);
        return handle;
    }

    private bool IsTaskOwnerActive(GameScript owner)
    {
        if (!project.Scripts.Any(item => item.Id == owner.Id)) return false;
        var node = FindScriptNode(owner.Id);
        return node is null || SceneNodeTypes.IsBaseScript(node) && node.Enabled && IsScriptAttachedForExecution(node);
    }

    private void StartScheduledTask(ScheduledTask scheduled)
    {
        if (!IsTaskOwnerActive(scheduled.Owner)) return;
        var previousScript = currentGameScript;
        BindCurrentScript(scheduled.Owner);
        try
        {
            var coroutine = scheduled.Handle.Coroutine;
            coroutine.Resume(scheduled.Arguments);
            SynchronizeInstances();
            if (coroutine.State != CoroutineState.Dead && IsTaskOwnerActive(scheduled.Owner))
                activeScripts.Add(new ScriptExecution(scheduled.Owner, coroutine, false, "task"));
        }
        catch (Exception exception)
        {
            output?.Invoke($"{scheduled.Owner.Name} task: {exception.Message}");
            SynchronizeInstances();
        }
        finally { currentGameScript = previousScript; }
    }

    private void RunDeferredTasks()
    {
        foreach (var scheduled in scheduledTasks.Where(item => item.Deferred).Take(64).ToArray())
        {
            if (!scheduledTasks.Remove(scheduled)) continue;
            StartScheduledTask(scheduled);
        }
    }

    private void RunDelayedTasks(float elapsedSeconds)
    {
        var due = new List<ScheduledTask>();
        foreach (var scheduled in scheduledTasks.Where(item => !item.Deferred))
        {
            scheduled.Remaining -= elapsedSeconds;
            if (scheduled.Remaining <= 0 && due.Count < 64) due.Add(scheduled);
        }
        foreach (var scheduled in due)
        {
            if (!scheduledTasks.Remove(scheduled)) continue;
            StartScheduledTask(scheduled);
        }
        RunDeferredTasks();
    }

    private void InstallLuauMath()
    {
        var value = script.Globals.Get("math");
        if (value.Type != DataType.Table) return;
        var math = value.Table;
        math.Set("atan2", DynValue.NewCallback((_, args) => args.Count >= 2 ? DynValue.NewNumber(Math.Atan2(args[0].Number, args[1].Number)) : DynValue.Nil));
        math.Set("clamp", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 3 || args[1].Number > args[2].Number) return DynValue.Nil;
            return DynValue.NewNumber(Math.Clamp(args[0].Number, args[1].Number, args[2].Number));
        }));
        math.Set("round", DynValue.NewCallback((_, args) => args.Count > 0 ? DynValue.NewNumber(Math.Round(args[0].Number, MidpointRounding.AwayFromZero)) : DynValue.Nil));
        math.Set("sign", DynValue.NewCallback((_, args) =>
        {
            if (args.Count == 0) return DynValue.Nil;
            var number = args[0].Number;
            return DynValue.NewNumber(number > 0 ? 1 : number < 0 ? -1 : 0);
        }));
    }

    private void InstallVector3Metatable()
    {
        vector2Metatable = new Table(script);
        vector2Metatable.Set("__add", DynValue.NewCallback((_, args) => CombineVector2Values(args, (left, right) => left + right)));
        vector2Metatable.Set("__sub", DynValue.NewCallback((_, args) => CombineVector2Values(args, (left, right) => left - right)));
        vector2Metatable.Set("__mul", DynValue.NewCallback((_, args) => MultiplyVector2Values(args)));
        vector2Metatable.Set("__div", DynValue.NewCallback((_, args) => DivideVector2Values(args)));
        vector2Metatable.Set("__unm", DynValue.NewCallback((_, args) => args.Count > 0 && TryReadVector2(args[0], out var vector) ? Vector2Value(vector.Select(value => -value).ToArray()) : DynValue.Nil));
        vector2Metatable.Set("__eq", DynValue.NewCallback((_, args) =>
        {
            return DynValue.NewBoolean(args.Count >= 2 && TryReadVector2(args[0], out var left) && TryReadVector2(args[1], out var right) && left.SequenceEqual(right));
        }));
        vector2Metatable.Set("__tostring", DynValue.NewCallback((_, args) =>
            DynValue.NewString(args.Count > 0 && TryReadVector2(args[0], out var vector) ? $"{vector[0]}, {vector[1]}" : "Vector2")));
        vector3Metatable = new Table(script);
        vector3Metatable.Set("__add", DynValue.NewCallback((_, args) => args.Count >= 2 ? VectorValue(CombineVectors(args, (left, right) => left + right)) : DynValue.Nil));
        vector3Metatable.Set("__sub", DynValue.NewCallback((_, args) => args.Count >= 2 ? VectorValue(CombineVectors(args, (left, right) => left - right)) : DynValue.Nil));
        vector3Metatable.Set("__mul", DynValue.NewCallback((_, args) => MultiplyVectors(args)));
        vector3Metatable.Set("__div", DynValue.NewCallback((_, args) => DivideVectors(args)));
        vector3Metatable.Set("__unm", DynValue.NewCallback((_, args) => args.Count > 0 ? VectorValue(ReadVector(args[0], [0, 0, 0]).Select(value => -value).ToArray()) : DynValue.Nil));
        vector3Metatable.Set("__eq", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 2) return DynValue.NewBoolean(false);
            var left = ReadVector(args[0], [0, 0, 0]);
            var right = ReadVector(args[1], [0, 0, 0]);
            return DynValue.NewBoolean(left.SequenceEqual(right));
        }));
        vector3Metatable.Set("__tostring", DynValue.NewCallback((_, args) =>
        {
            if (args.Count == 0) return DynValue.NewString("Vector3");
            var vector = ReadVector(args[0], [0, 0, 0]);
            return DynValue.NewString($"{vector[0]}, {vector[1]}, {vector[2]}");
        }));
    }

    private DynValue CombineVector2Values(CallbackArguments args, Func<float, float, float> operation)
    {
        return args.Count >= 2 && TryReadVector2(args[0], out var left) && TryReadVector2(args[1], out var right)
            ? Vector2Value([operation(left[0], right[0]), operation(left[1], right[1])]) : DynValue.Nil;
    }

    private DynValue MultiplyVector2Values(CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        var hasLeft = TryReadVector2(args[0], out var left);
        var hasRight = TryReadVector2(args[1], out var right);
        if (hasLeft && hasRight)
            return Vector2Value([left[0] * right[0], left[1] * right[1]]);
        var vectorIndex = hasLeft ? 0 : hasRight ? 1 : -1;
        var scalarIndex = vectorIndex == 0 ? 1 : 0;
        if (vectorIndex < 0 || args[scalarIndex].Type != DataType.Number || !double.IsFinite(args[scalarIndex].Number)) return DynValue.Nil;
        var vector = vectorIndex == 0 ? left : right;
        var scalar = (float)args[scalarIndex].Number;
        return Vector2Value([vector[0] * scalar, vector[1] * scalar]);
    }

    private DynValue DivideVector2Values(CallbackArguments args)
    {
        if (args.Count < 2 || !TryReadVector2(args[0], out var left)) return DynValue.Nil;
        if (TryReadVector2(args[1], out var right))
            return Vector2Value([right[0] == 0 ? 0 : left[0] / right[0], right[1] == 0 ? 0 : left[1] / right[1]]);
        if (args[1].Type != DataType.Number || !double.IsFinite(args[1].Number) || args[1].Number == 0) return DynValue.Nil;
        var divisor = (float)args[1].Number;
        return Vector2Value([left[0] / divisor, left[1] / divisor]);
    }

    private float[] CombineVectors(CallbackArguments args, Func<float, float, float> operation)
    {
        var left = ReadVector(args[0], [0, 0, 0]);
        var right = ReadVector(args[1], [0, 0, 0]);
        return [operation(left[0], right[0]), operation(left[1], right[1]), operation(left[2], right[2])];
    }

    private DynValue Vector3ComponentExtremum(Table receiver, CallbackArguments args, Func<float, float, float> operation)
    {
        var index = GetVectorMethodArgumentIndex(receiver, args);
        if (args.Count <= index) return DynValue.Nil;
        var left = ReadVector(DynValue.NewTable(receiver), [0, 0, 0]);
        var right = ReadVector(args[index], [0, 0, 0]);
        return VectorValue(Enumerable.Range(0, 3).Select(axis => operation(left[axis], right[axis])).ToArray());
    }

    private DynValue MultiplyVectors(CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        if (args[0].Type == DataType.Table && args[1].Type == DataType.Table)
            return VectorValue(CombineVectors(args, (left, right) => left * right));
        var vectorArgument = args[0].Type == DataType.Table ? 0 : args[1].Type == DataType.Table ? 1 : -1;
        var scalarArgument = vectorArgument == 0 ? 1 : 0;
        if (vectorArgument < 0 || args[scalarArgument].Type != DataType.Number) return DynValue.Nil;
        var scalar = (float)args[scalarArgument].Number;
        return VectorValue(ReadVector(args[vectorArgument], [0, 0, 0]).Select(value => value * scalar).ToArray());
    }

    private DynValue DivideVectors(CallbackArguments args)
    {
        if (args.Count < 2 || args[0].Type != DataType.Table) return DynValue.Nil;
        var left = ReadVector(args[0], [0, 0, 0]);
        if (args[1].Type == DataType.Table) return VectorValue(CombineVectors(args, (first, second) => second == 0 ? 0 : first / second));
        if (args[1].Type != DataType.Number || args[1].Number == 0) return DynValue.Nil;
        var divisor = (float)args[1].Number;
        return VectorValue(left.Select(value => value / divisor).ToArray());
    }

    private Table CreateInstance(SceneNode node)
    {
        var instance = new Table(script);
        runtimeTypeTags.Add(instance, new LuaXTypeTag(node.Type));
        instance.Set("Name", DynValue.NewString(node.Name));
        instance.Set("Archivable", DynValue.NewBoolean(node.Archivable));
        instance.Set("Id", DynValue.NewString(node.Id));
        instance.Set("ClassName", DynValue.NewString(node.Type));
        if (HasPositionProperty(node)) instance.Set("Position", IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Type == "Model" ? SceneModelService.GetPivot(node) : node.Position));
        if (IsGuiObject(node)) instance.Set("AnchorPoint", Vector2Value(node.AnchorPoint));
        if (IsGuiObject(node) && node.Type != "ScrollingFrame") instance.Set("AutomaticSize", DynValue.NewString(node.AutomaticSize));
        if (IsGuiObject(node) || node.Type is "BillboardGui" or "SurfaceGui") instance.Set("ClipsDescendants", DynValue.NewBoolean(node.ClipsDescendants));
        if (IsGuiObject(node)) instance.Set("BackgroundColor3", ColorValue(node.Color));
        if (IsGuiObject(node))
        {
            instance.Set("BorderColor3", ColorValue(node.BorderColor3));
            instance.Set("BorderSizePixel", DynValue.NewNumber(node.BorderSizePixel));
        }
        if (HasRotationProperty(node)) instance.Set("Rotation", VectorValue(node.Type == "AlignOrientation" ? node.AlignOrientationRotation : node.Rotation));
        if (HasSizeProperty(node)) instance.Set("Size", SceneNodeTypes.IsSizedParticleEffect(node) || SceneNodeTypes.IsPostEffect(node) ? DynValue.NewNumber(node.EffectSize) : IsGuiObject(node) || node.Type == "BillboardGui" ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size));
        instance.Set("Color", ColorValue(node.Color));
        if (node.Type == "Sparkles") instance.Set("SparkleColor", ColorValue(node.Color));
        if (node.Type == "Fire") instance.Set("SecondaryColor", ColorValue(node.SecondaryColor));
        if (node.Type == "Highlight")
        {
            instance.Set("FillColor", ColorValue(node.FillColor));
            instance.Set("OutlineColor", ColorValue(node.OutlineColor));
        }
        if (node.Type is "SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") instance.Set("Adornee", GetObjectValueReference(node.Adornee));
        if (node.Type is "SelectionBox" or "SelectionSphere")
        {
            instance.Set("Color3", ColorValue(node.Color));
            instance.Set("SurfaceColor3", ColorValue(node.SurfaceColor3));
            instance.Set("SurfaceTransparency", DynValue.NewNumber(node.SurfaceTransparency));
            if (node.Type == "SelectionBox") instance.Set("LineThickness", DynValue.NewNumber(node.LineThickness));
        }
        if (node.Type == "UIGradient")
        {
            instance.Set("StartColor", ColorValue(node.StartColor));
            instance.Set("EndColor", ColorValue(node.EndColor));
        }
        if (node.Type == "UIPadding")
        {
            instance.Set("PaddingLeft", CreateUDim(node.PaddingLeft[0], node.PaddingLeft[1]));
            instance.Set("PaddingTop", CreateUDim(node.PaddingTop[0], node.PaddingTop[1]));
            instance.Set("PaddingRight", CreateUDim(node.PaddingRight[0], node.PaddingRight[1]));
            instance.Set("PaddingBottom", CreateUDim(node.PaddingBottom[0], node.PaddingBottom[1]));
        }
        if (node.Type == "UIScale") instance.Set("Scale", DynValue.NewNumber(node.Scale));
        if (node.Type == "UISizeConstraint")
        {
            instance.Set("MinSize", Vector2Value(node.MinSize));
            instance.Set("MaxSize", Vector2Value(node.MaxSize));
        }
        if (node.Type == "UIGridLayout")
        {
            instance.Set("CellSize", CreateUDim2Value(node.CellSizeScale, node.CellSize));
            instance.Set("CellPadding", CreateUDim2Value(node.CellPaddingScale, node.CellPadding));
            instance.Set("FillDirection", DynValue.NewString(node.FillDirection));
            instance.Set("FillDirectionMaxCells", DynValue.NewNumber(node.FillDirectionMaxCells));
        }
        if (node.Type == "ScrollingFrame")
        {
            instance.Set("CanvasPosition", Vector2Value(node.CanvasPosition));
            instance.Set("CanvasSize", CreateUDim2Value(node.CanvasSizeScale, node.CanvasSize));
            instance.Set("ScrollBarThickness", DynValue.NewNumber(node.ScrollBarThickness));
            instance.Set("ScrollingEnabled", DynValue.NewBoolean(node.ScrollingEnabled));
            instance.Set("ScrollingDirection", DynValue.NewString(node.ScrollingDirection));
            instance.Set("AutomaticCanvasSize", DynValue.NewString(node.AutomaticCanvasSize));
        }
        if (node.Type == "UIListLayout") instance.Set("FillDirection", DynValue.NewString(node.LayoutDirection));
        if (node.Type == "Highlight")
        {
            instance.Set("FillTransparency", DynValue.NewNumber(node.FillTransparency));
            instance.Set("OutlineTransparency", DynValue.NewNumber(node.OutlineTransparency));
        }
        if (SceneNodeTypes.IsBasePart(node))
        {
            instance.Set("Shape", DynValue.NewString(node.Shape));
            instance.Set("Material", DynValue.NewString(node.Material));
        }
        if (node.Type is "Decal" or "Texture" or "SurfaceLight" or "SurfaceGui") instance.Set("Face", DynValue.NewString(node.Face));
        if (node.Type == "Texture")
        {
            instance.Set("StudsPerTileU", DynValue.NewNumber(node.StudsPerTileU));
            instance.Set("StudsPerTileV", DynValue.NewNumber(node.StudsPerTileV));
            instance.Set("OffsetStudsU", DynValue.NewNumber(node.OffsetStudsU));
            instance.Set("OffsetStudsV", DynValue.NewNumber(node.OffsetStudsV));
        }
        if (IsBasePart(node) || node.Type is "Decal" or "Texture" or "Beam" or "SelectionBox" or "SelectionSphere" or "UIShadow")
            instance.Set("Transparency", DynValue.NewNumber(node.Transparency));
        if (IsGuiObject(node)) instance.Set("BackgroundTransparency", DynValue.NewNumber(node.BackgroundTransparency));
        if (node.Type == "UICanvasGroup") instance.Set("GroupTransparency", DynValue.NewNumber(node.GroupTransparency));
        if (node.Type is "TextLabel" or "TextButton" or "TextBox") instance.Set("TextTransparency", DynValue.NewNumber(node.TextTransparency));
        if (node.Type is "ImageLabel" or "ImageButton")
        {
            instance.Set("Image", DynValue.NewString(node.Asset));
            instance.Set("ImageColor3", ColorValue(node.ImageColor3));
            instance.Set("ImageTransparency", DynValue.NewNumber(node.ImageTransparency));
            instance.Set("ScaleType", DynValue.NewString(node.ScaleType));
            instance.Set("ImageRectOffset", Vector2Value(node.ImageRectOffset));
            instance.Set("ImageRectSize", Vector2Value(node.ImageRectSize));
            instance.Set("SliceCenter", RectValue(node.SliceCenter));
            instance.Set("SliceScale", DynValue.NewNumber(node.SliceScale));
        }
        if (node.Type == "ImageButton")
        {
            instance.Set("HoverImage", DynValue.NewString(node.HoverImage));
            instance.Set("PressedImage", DynValue.NewString(node.PressedImage));
        }
        if (node.Type is not ("PathfindingModifier" or "PathfindingLink")) instance.Set("Enabled", DynValue.NewBoolean(node.Enabled));
        if (node.Type == "ProximityPrompt") instance.Set("RequiresLineOfSight", DynValue.NewBoolean(node.RequiresLineOfSight));
        if (node.Type is "BillboardGui" or "SurfaceGui")
        {
            instance.Set("AlwaysOnTop", DynValue.NewBoolean(node.AlwaysOnTop));
            instance.Set("MaxDistance", DynValue.NewNumber(node.MaxDistance));
        }
        if (node.Type == "SurfaceGui") instance.Set("PixelsPerStud", DynValue.NewNumber(node.PixelsPerStud));
        if (node.Type == "Lighting" || SceneNodeTypes.IsLight(node)) instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
        if (node.Type == "ColorCorrectionEffect")
        {
            instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
            instance.Set("TintColor", ColorValue(node.TintColor));
            instance.Set("Contrast", DynValue.NewNumber(node.ColorCorrectionContrast));
            instance.Set("Saturation", DynValue.NewNumber(node.ColorCorrectionSaturation));
        }
        if (SceneNodeTypes.IsLight(node)) instance.Set("Range", DynValue.NewNumber(node.Range));
        if (node.Type == "Fire") instance.Set("Heat", DynValue.NewNumber(node.Heat));
        if (node.Type == "Smoke")
        {
            instance.Set("RiseVelocity", DynValue.NewNumber(node.RiseVelocity));
            instance.Set("Opacity", DynValue.NewNumber(node.Opacity));
        }
        if (SceneNodeTypes.IsParticleEffect(node)) instance.Set("TimeScale", DynValue.NewNumber(node.TimeScale));
        if (node.Type == "Explosion")
        {
            instance.Set("BlastRadius", DynValue.NewNumber(node.BlastRadius));
            instance.Set("BlastPressure", DynValue.NewNumber(node.BlastPressure));
            instance.Set("DestroyJointRadiusPercent", DynValue.NewNumber(node.DestroyJointRadiusPercent));
        }
        if (node.Type is "SpotLight" or "SurfaceLight") instance.Set("Angle", DynValue.NewNumber(node.Angle));
        if (node.Type == "ProximityPrompt")
        {
            instance.Set("ActionText", DynValue.NewString(node.ActionText));
            instance.Set("ObjectText", DynValue.NewString(node.ObjectText));
            instance.Set("HoldDuration", DynValue.NewNumber(node.HoldDuration));
            instance.Set("KeyboardKeyCode", DynValue.NewString(node.KeyboardKeyCode));
        }
        if (node.Type is "ProximityPrompt" or "ClickDetector") instance.Set("MaxActivationDistance", DynValue.NewNumber(node.MaxActivationDistance));
        if (node.Type is "Sound" or "SoundGroup" or "SoundService")
            instance.Set("Volume", DynValue.NewNumber(node.Type == "SoundGroup" ? node.SoundGroupVolume : node.Type == "SoundService" ? node.SoundServiceVolume : node.Volume));
        if (node.Type == "PitchShiftSoundEffect") instance.Set("Octave", DynValue.NewNumber(node.PitchShiftOctave));
        if (node.Type == "DistortionSoundEffect") instance.Set("Level", DynValue.NewNumber(node.DistortionLevel));
        if (node.Type == "CompressorSoundEffect")
        {
            instance.Set("SideChain", GetCompressorSideChainValue(node.CompressorSideChainId));
            instance.Set("Attack", DynValue.NewNumber(node.CompressorAttack));
            instance.Set("GainMakeup", DynValue.NewNumber(node.CompressorGainMakeup));
            instance.Set("Ratio", DynValue.NewNumber(node.CompressorRatio));
            instance.Set("Release", DynValue.NewNumber(node.CompressorRelease));
            instance.Set("Threshold", DynValue.NewNumber(node.CompressorThreshold));
        }
        if (node.Type == "ChorusSoundEffect")
        {
            instance.Set("Depth", DynValue.NewNumber(node.ChorusDepth));
            instance.Set("Mix", DynValue.NewNumber(node.ChorusMix));
            instance.Set("Rate", DynValue.NewNumber(node.ChorusRate));
        }
        if (node.Type == "EqualizerSoundEffect")
        {
            instance.Set("LowGain", DynValue.NewNumber(node.EqualizerLowGain));
            instance.Set("MidGain", DynValue.NewNumber(node.EqualizerMidGain));
            instance.Set("HighGain", DynValue.NewNumber(node.EqualizerHighGain));
        }
        if (node.Type == "EchoSoundEffect")
        {
            instance.Set("Delay", DynValue.NewNumber(node.EchoDelay));
            instance.Set("Feedback", DynValue.NewNumber(node.EchoFeedback));
            instance.Set("DryLevel", DynValue.NewNumber(node.EchoDryLevel));
            instance.Set("WetLevel", DynValue.NewNumber(node.EchoWetLevel));
        }
        if (node.Type == "FlangeSoundEffect")
        {
            instance.Set("Depth", DynValue.NewNumber(node.FlangeDepth));
            instance.Set("Mix", DynValue.NewNumber(node.FlangeMix));
            instance.Set("Rate", DynValue.NewNumber(node.FlangeRate));
        }
        if (node.Type == "TremoloSoundEffect")
        {
            instance.Set("Depth", DynValue.NewNumber(node.TremoloDepth));
            instance.Set("Duty", DynValue.NewNumber(node.TremoloDuty));
            instance.Set("Frequency", DynValue.NewNumber(node.TremoloFrequency));
        }
        if (node.Type == "ReverbSoundEffect")
        {
            instance.Set("DecayTime", DynValue.NewNumber(node.ReverbDecayTime));
            instance.Set("Density", DynValue.NewNumber(node.ReverbDensity));
            instance.Set("Diffusion", DynValue.NewNumber(node.ReverbDiffusion));
            instance.Set("DryLevel", DynValue.NewNumber(node.ReverbDryLevel));
            instance.Set("WetLevel", DynValue.NewNumber(node.ReverbWetLevel));
        }
        if (node.Type == "Sound" || node.Type == "VideoFrame")
        {
            if (node.Type == "VideoFrame") instance.Set("Video", DynValue.NewString(node.Video));
            instance.Set("Volume", DynValue.NewNumber(node.Volume));
            instance.Set("Looped", DynValue.NewBoolean(node.Looped));
            instance.Set("Playing", DynValue.NewBoolean(node.Playing));
            if (node.Type == "Sound")
            {
                instance.Set("PlaybackSpeed", DynValue.NewNumber(node.PlaybackSpeed));
                instance.Set("RollOffMinDistance", DynValue.NewNumber(node.RollOffMinDistance));
                instance.Set("RollOffMaxDistance", DynValue.NewNumber(node.RollOffMaxDistance));
                instance.Set("SoundGroup", GetSoundGroupReferenceValue(node.SoundGroupId));
            }
        }
        if (node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")
        {
            instance.Set("Part0", GetPartReferenceValue(node.Part0));
            instance.Set("Part1", GetPartReferenceValue(node.Part1));
        }
        if (node.Type is "Weld" or "Motor6D")
        {
            instance.Set("Part0OffsetPosition", VectorValue(node.Part0OffsetPosition));
            instance.Set("Part0OffsetRotation", VectorValue(node.Part0OffsetRotation));
            instance.Set("Part1OffsetPosition", VectorValue(node.Part1OffsetPosition));
            instance.Set("Part1OffsetRotation", VectorValue(node.Part1OffsetRotation));
        }
        if (node.Type == "Motor6D")
        {
            instance.Set("TransformPosition", VectorValue(node.MotorTransformPosition));
            instance.Set("TransformRotation", VectorValue(node.MotorTransformRotation));
        }
        if (node.Type == "Model") instance.Set("PrimaryPart", GetModelPrimaryPartReference(node));
        if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint")
        {
            instance.Set("Attachment0", DynValue.NewString(node.Attachment0));
            instance.Set("Attachment1", DynValue.NewString(node.Attachment1));
        }
        if (node.Type == "Beam")
        {
            instance.Set("Width0", DynValue.NewNumber(node.Width0));
            instance.Set("Width1", DynValue.NewNumber(node.Width1));
        }
        if (node.Type is "Part" or "ParticleEmitter" or "Trail" or "Decal" or "Texture") instance.Set("Texture", DynValue.NewString(node.Texture));
        if (node.Type is "TextLabel" or "TextButton" or "TextBox") instance.Set("Text", DynValue.NewString(node.Text));
        if (node.Type == "TextBox") instance.Set("PlaceholderText", DynValue.NewString(node.PlaceholderText));
        if (node.Type is "TextLabel" or "TextButton" or "TextBox") instance.Set("TextColor", DynValue.NewString(node.TextColor));
        if (IsGuiObject(node)) instance.Set("Visible", DynValue.NewBoolean(node.Visible));
        if (IsGuiObject(node) || node.Type == "UIShadow") instance.Set("ZIndex", DynValue.NewNumber(node.ZIndex));
        if (node.Type == "ScreenGui")
        {
            instance.Set("DisplayOrder", DynValue.NewNumber(node.DisplayOrder));
            instance.Set("ResetOnSpawn", DynValue.NewBoolean(node.ResetOnSpawn));
        }
        if (IsGuiObject(node)) instance.Set("LayoutOrder", DynValue.NewNumber(node.LayoutOrder));
        if (node.Type == "UICorner") SetCornerInstanceProperties(node, instance);
        if (node.Type == "UIShadow") SetShadowInstanceProperties(node, instance);
        if (node.Type is "TextLabel" or "TextButton" or "TextBox")
        {
            instance.Set("TextSize", DynValue.NewNumber(node.TextSize));
            instance.Set("TextScaled", DynValue.NewBoolean(node.TextScaled));
        }
        if (node.Type is "TextLabel" or "TextButton") instance.Set("TextWrapped", DynValue.NewBoolean(node.TextWrapped));
        if (node.Type == "TextBox")
        {
            instance.Set("MultiLine", DynValue.NewBoolean(node.MultiLine));
            instance.Set("TextEditable", DynValue.NewBoolean(node.TextEditable));
            instance.Set("ClearTextOnFocus", DynValue.NewBoolean(node.ClearTextOnFocus));
        }
        if (node.Type == "UITextSizeConstraint")
        {
            instance.Set("MinTextSize", DynValue.NewNumber(node.MinTextSize));
            instance.Set("MaxTextSize", DynValue.NewNumber(node.MaxTextSize));
        }
        if (node.Type is "UIListLayout" or "UITableLayout") instance.Set("LayoutSpacing", DynValue.NewNumber(node.LayoutSpacing));
        if (node.Type == "UITableLayout") instance.Set("TableColumns", DynValue.NewNumber(node.TableColumns));
        if (node.Type == "UIPageLayout")
        {
            instance.Set("CurrentPageIndex", DynValue.NewNumber(node.CurrentPageIndex));
            instance.Set("Circular", DynValue.NewBoolean(node.Circular));
            instance.Set("CurrentPage", GetPageInstanceValue(GetPageNodes(node), node.CurrentPageIndex));
        }
        if (node.Type == "UIListLayout") instance.Set("Padding", CreateUDim(node.ListPaddingScale, node.LayoutSpacing));
        if (node.Type is "UIAspectRatio" or "UIAspectRatioConstraint") instance.Set("AspectRatio", DynValue.NewNumber(node.AspectRatio));
        if (node.Type == "UIAspectRatioConstraint")
        {
            instance.Set("AspectType", DynValue.NewString(node.AspectType));
            instance.Set("DominantAxis", DynValue.NewString(node.DominantAxis));
        }
        if (node.Type == "UIListLayout")
        {
            instance.Set("LayoutDirection", DynValue.NewString(node.LayoutDirection));
            instance.Set("HorizontalAlignment", DynValue.NewString(node.HorizontalAlignment));
            instance.Set("VerticalAlignment", DynValue.NewString(node.VerticalAlignment));
            instance.Set("SortOrder", DynValue.NewString(node.SortOrder));
        }
        if (node.Type == "UIFlexItem")
        {
            instance.Set("FlexMode", DynValue.NewString(node.FlexMode));
            instance.Set("GrowRatio", DynValue.NewNumber(node.GrowRatio));
            instance.Set("ShrinkRatio", DynValue.NewNumber(node.ShrinkRatio));
            instance.Set("ItemLineAlignment", DynValue.NewString(node.ItemLineAlignment));
        }
        instance.Set("SkyboxFront", DynValue.NewString(node.SkyboxFront));
        instance.Set("SkyboxBack", DynValue.NewString(node.SkyboxBack));
        instance.Set("SkyboxLeft", DynValue.NewString(node.SkyboxLeft));
        instance.Set("SkyboxRight", DynValue.NewString(node.SkyboxRight));
        instance.Set("SkyboxTop", DynValue.NewString(node.SkyboxTop));
        instance.Set("SkyboxBottom", DynValue.NewString(node.SkyboxBottom));
        if (node.Type == "Sky")
        {
            instance.Set("SkyboxFt", DynValue.NewString(node.SkyboxFront));
            instance.Set("SkyboxBk", DynValue.NewString(node.SkyboxBack));
            instance.Set("SkyboxLf", DynValue.NewString(node.SkyboxLeft));
            instance.Set("SkyboxRt", DynValue.NewString(node.SkyboxRight));
            instance.Set("SkyboxUp", DynValue.NewString(node.SkyboxTop));
            instance.Set("SkyboxDn", DynValue.NewString(node.SkyboxBottom));
        }
        if (node.Type == "Atmosphere")
        {
            instance.Set("Decay", ColorValue(node.AtmosphereDecay));
            instance.Set("Density", DynValue.NewNumber(node.Density));
            instance.Set("Haze", DynValue.NewNumber(node.Haze));
            instance.Set("Glare", DynValue.NewNumber(node.Glare));
            instance.Set("Offset", DynValue.NewNumber(node.Offset));
        }
        if (node.Type == "Clouds")
        {
            instance.Set("Cover", DynValue.NewNumber(node.CloudCover));
            instance.Set("Density", DynValue.NewNumber(node.CloudDensity));
        }
        if (SceneNodeTypes.IsSurfaceAppearance(node))
        {
            instance.Set("Color", ColorValue(node.SurfaceAppearanceColor));
            instance.Set("ColorMap", DynValue.NewString(node.SurfaceAppearanceColorMap));
            instance.Set("NormalMap", DynValue.NewString(node.SurfaceAppearanceNormalMap));
            instance.Set("RoughnessMap", DynValue.NewString(node.SurfaceAppearanceRoughnessMap));
            instance.Set("MetalnessMap", DynValue.NewString(node.SurfaceAppearanceMetalnessMap));
            instance.Set("EmissiveMaskContent", DynValue.NewString(node.SurfaceAppearanceEmissiveMask));
            instance.Set("EmissiveTint", ColorValue(node.SurfaceAppearanceEmissiveTint));
            instance.Set("EmissiveStrength", DynValue.NewNumber(node.SurfaceAppearanceEmissiveStrength));
        }
        if (SceneNodeTypes.IsBasePart(node))
        {
            instance.Set("Anchored", DynValue.NewBoolean(node.Anchored));
            instance.Set("CanCollide", DynValue.NewBoolean(node.CanCollide));
            instance.Set("CanTouch", DynValue.NewBoolean(node.CanTouch));
            instance.Set("CanQuery", DynValue.NewBoolean(node.CanQuery));
            instance.Set("Velocity", VectorValue(node.Velocity));
            instance.Set("AssemblyLinearVelocity", VectorValue(node.Velocity));
            instance.Set("AssemblyAngularVelocity", VectorValue(node.AssemblyAngularVelocity));
            instance.Set("CastShadow", DynValue.NewBoolean(node.CastShadow));
        }
        if (node.Type == "Workspace")
        {
            instance.Set("Gravity", DynValue.NewNumber(node.Gravity));
            instance.Set("AutoManageCharacter", DynValue.NewBoolean(node.AutoManageCharacter));
            instance.Set("CurrentCamera", GetCurrentCameraReference());
        }
        if (node.Type == "ViewportFrame") instance.Set("CurrentCamera", GetObjectValueReference(node.ViewportCurrentCameraId));
        if (node.Type == "Lighting")
        {
            instance.Set("Ambient", ColorValue(node.Ambient));
            instance.Set("ClockTime", DynValue.NewNumber(node.ClockTime));
            instance.Set("GlobalShadows", DynValue.NewBoolean(project.Settings.Shadows));
        }
        if (node.Type == "Player")
        {
            instance.Set("Character", GetCharacterValue(node));
            instance.Set("CharacterAdded", DynValue.NewTable(CreateRemoteSignal(node, "CharacterAdded")));
            instance.Set("CharacterRemoving", DynValue.NewTable(CreateRemoteSignal(node, "CharacterRemoving")));
            instance.Set("Team", GetTeamValue(node));
            instance.Set("TeamColor", ColorValue(FindNode(node.Team)?.TeamColor ?? "#FFFFFF"));
        }
        if (node.Type == "Players")
        {
            instance.Set("GetPlayers", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (index != args.Count) return DynValue.Nil;
                var result = new Table(script);
                var playerIndex = 1;
                foreach (var player in project.Players.Children.Where(child => child.Type == "Player"))
                    if (instances.TryGetValue(player.Id, out var playerInstance)) result.Set(playerIndex++, DynValue.NewTable(playerInstance));
                return DynValue.NewTable(result);
            }));
            instance.Set("GetPlayerFromCharacter", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (index + 1 != args.Count || ReadNodeArgument(args[index]) is not { Type: "Model" } model) return DynValue.Nil;
                var player = project.Players.Children.FirstOrDefault(child => child.Type == "Player" && child.Character == model.Id);
                return player is not null && instances.TryGetValue(player.Id, out var playerInstance)
                    ? DynValue.NewTable(playerInstance) : DynValue.Nil;
            }));
        }
        if (node.Type == "Team")
        {
            instance.Set("TeamColor", ColorValue(node.TeamColor));
            instance.Set("AutoAssignable", DynValue.NewBoolean(node.AutoAssignable));
        }
        if (node.Type == "BodyColors")
        {
            foreach (var property in new[] { "HeadColor3", "TorsoColor3", "LeftArmColor3", "RightArmColor3", "LeftLegColor3", "RightLegColor3" })
                instance.Set(property, ColorValue(BodyColorsService.GetColor(node, property)));
        }
        if (node.Type == "SpawnLocation")
        {
            instance.Set("TeamColor", ColorValue(node.TeamColor));
            instance.Set("Neutral", DynValue.NewBoolean(node.Neutral));
            instance.Set("AllowTeamChangeOnTouch", DynValue.NewBoolean(node.AllowTeamChangeOnTouch));
            instance.Set("Duration", DynValue.NewNumber(node.Duration));
        }
        if (node.Type is "Seat" or "VehicleSeat") instance.Set("Occupant", GetOccupantValue(node));
        if (node.Type == "VehicleSeat")
        {
            instance.Set("Disabled", DynValue.NewBoolean(node.VehicleSeatDisabled));
            instance.Set("HeadsUpDisplay", DynValue.NewBoolean(node.VehicleSeatHeadsUpDisplay));
            instance.Set("MaxSpeed", DynValue.NewNumber(node.VehicleSeatMaxSpeed));
            instance.Set("Torque", DynValue.NewNumber(node.VehicleSeatTorque));
            instance.Set("TurnSpeed", DynValue.NewNumber(node.VehicleSeatTurnSpeed));
            instance.Set("ThrottleFloat", DynValue.NewNumber(node.VehicleSeatThrottleFloat));
            instance.Set("SteerFloat", DynValue.NewNumber(node.VehicleSeatSteerFloat));
            instance.Set("AreHingesDetected", DynValue.NewNumber(node.VehicleSeatAreHingesDetected));
        }
        if (node.Type == "Humanoid")
        {
            instance.Set("Health", DynValue.NewNumber(node.Health));
            instance.Set("MaxHealth", DynValue.NewNumber(node.MaxHealth));
            instance.Set("WalkSpeed", DynValue.NewNumber(node.WalkSpeed));
            instance.Set("JumpPower", DynValue.NewNumber(node.JumpPower));
        }
        if (SceneNodeTypes.IsValueObject(node)) instance.Set("Value", GetValueObjectValue(node));
        instance.Set("Asset", DynValue.NewString(node.Asset));
        instance.Set("CameraMode", DynValue.NewString(node.CameraMode));
        instance.Set("Mode", DynValue.NewString(node.Type == "AlignPosition" ? node.AlignmentMode : node.CameraMode));
        instance.Set("Subject", DynValue.NewString(node.Subject));
        instance.Set("FieldOfView", DynValue.NewNumber(node.FieldOfView));
        if (node.Type == "ParticleEmitter")
        {
            instance.Set("Size", NumberSequenceValue(node.SizeSequence.Count > 0 ? node.SizeSequence : [new(0, node.EffectSize), new(1, node.EffectSize)]));
            instance.Set("Color", ColorSequenceValue(node.ColorSequence.Count > 0 ? node.ColorSequence : [new(0, node.Color), new(1, node.Color)]));
            instance.Set("Transparency", NumberSequenceValue(node.TransparencySequence.Count > 0 ? node.TransparencySequence : [new(0, node.Transparency), new(1, node.Transparency)]));
            instance.Set("Rate", DynValue.NewNumber(node.Rate));
            instance.Set("Speed", NumberRangeValue(node.SpeedRange));
            instance.Set("Lifetime", NumberRangeValue(node.LifetimeRange));
            instance.Set("SpreadAngle", Vector2Value(node.SpreadAngle));
            instance.Set("EmissionDirection", DynValue.NewString(node.EmissionDirection));
            instance.Set("Emit", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (args.Count > index && args[index].Type == DataType.Number && double.IsFinite(args[index].Number))
                {
                    var count = (int)Math.Clamp(Math.Truncate(args[index].Number), 0, 500);
                    node.PendingParticleEmits = Math.Min(500, node.PendingParticleEmits + count);
                    if (count > 0) propertyChanged?.Invoke(node, "Emit", DynValue.NewNumber(count));
                }
                return DynValue.Nil;
            }));
            instance.Set("Clear", DynValue.NewCallback((_, _) =>
            {
                node.PendingParticleEmits = 0;
                node.ParticleClearVersion++;
                propertyChanged?.Invoke(node, "Clear", DynValue.Nil);
                return DynValue.Nil;
            }));
        }
        if (node.Type == "Trail")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("FaceCamera", DynValue.NewBoolean(node.FaceCamera));
            instance.Set("TextureMode", DynValue.NewString(node.TextureMode));
            instance.Set("TextureLength", DynValue.NewNumber(node.TextureLength));
            instance.Set("Lifetime", DynValue.NewNumber(node.TrailLifetime));
            instance.Set("MinLength", DynValue.NewNumber(node.TrailMinLength));
            instance.Set("MaxLength", DynValue.NewNumber(node.TrailMaxLength));
            instance.Set("WidthScale", NumberSequenceValue(node.TrailWidthSequence.Count > 0 ? node.TrailWidthSequence : [new(0, node.TrailWidth), new(1, node.TrailWidth)]));
            instance.Set("Color", ColorSequenceValue(node.ColorSequence.Count > 0 ? node.ColorSequence : [new(0, node.Color), new(1, node.Color)]));
            instance.Set("Transparency", NumberSequenceValue(node.TransparencySequence.Count > 0 ? node.TransparencySequence : [new(0, node.Transparency), new(1, node.Transparency)]));
            instance.Set("Clear", DynValue.NewCallback((_, _) =>
            {
                node.TrailClearVersion++;
                propertyChanged?.Invoke(node, "Clear", DynValue.Nil);
                return DynValue.Nil;
            }));
        }
        if (node.Type == "PathfindingLink")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Label", DynValue.NewString(node.PathfindingLinkLabel));
            instance.Set("IsBidirectional", DynValue.NewBoolean(node.PathfindingLinkBidirectional));
        }
        if (node.Type == "BodyForce") instance.Set("Force", VectorValue(node.Force));
        if (node.Type == "BodyThrust")
        {
            instance.Set("Force", VectorValue(node.BodyThrustForce));
            instance.Set("Location", VectorValue(node.BodyThrustLocation));
        }
        if (node.Type == "BodyVelocity")
        {
            instance.Set("Velocity", VectorValue(node.BodyVelocityTarget));
            instance.Set("MaxForce", VectorValue(node.BodyVelocityMaxForce));
            instance.Set("P", DynValue.NewNumber(node.BodyVelocityP));
        }
        if (node.Type == "BodyPosition")
        {
            instance.Set("Position", VectorValue(node.BodyPositionTarget));
            instance.Set("MaxForce", VectorValue(node.BodyPositionMaxForce));
            instance.Set("P", DynValue.NewNumber(node.BodyPositionP));
            instance.Set("D", DynValue.NewNumber(node.BodyPositionD));
        }
        if (node.Type == "BodyGyro")
        {
            instance.Set("Rotation", VectorValue(node.BodyGyroTargetRotation));
            instance.Set("MaxTorque", VectorValue(node.BodyGyroMaxTorque));
            instance.Set("P", DynValue.NewNumber(node.BodyGyroP));
            instance.Set("D", DynValue.NewNumber(node.BodyGyroD));
        }
        if (node.Type == "BodyAngularVelocity")
        {
            instance.Set("AngularVelocity", VectorValue(node.BodyAngularVelocityTarget));
            instance.Set("MaxTorque", VectorValue(node.BodyAngularVelocityMaxTorque));
            instance.Set("P", DynValue.NewNumber(node.BodyAngularVelocityP));
        }
        if (node.Type == "RocketPropulsion")
        {
            instance.Set("Target", GetPartReferenceValue(node.RocketPropulsionTarget));
            instance.Set("TargetOffset", VectorValue(node.RocketPropulsionTargetOffset));
            instance.Set("TargetRadius", DynValue.NewNumber(node.RocketPropulsionTargetRadius));
            instance.Set("CartoonFactor", DynValue.NewNumber(node.RocketPropulsionCartoonFactor));
            instance.Set("MaxSpeed", DynValue.NewNumber(node.RocketPropulsionMaxSpeed));
            instance.Set("MaxThrust", DynValue.NewNumber(node.RocketPropulsionMaxThrust));
            instance.Set("MaxTorque", VectorValue(node.RocketPropulsionMaxTorque));
            instance.Set("ThrustD", DynValue.NewNumber(node.RocketPropulsionThrustD));
            instance.Set("ThrustP", DynValue.NewNumber(node.RocketPropulsionThrustP));
            instance.Set("TurnD", DynValue.NewNumber(node.RocketPropulsionTurnD));
            instance.Set("TurnP", DynValue.NewNumber(node.RocketPropulsionTurnP));
            instance.Set("ReachedTarget", DynValue.NewTable(CreateRemoteSignal(node, "ReachedTarget")));
            instance.Set("Fire", DynValue.NewCallback((_, _) =>
            {
                node.RocketPropulsionReachedTarget = false;
                node.RocketPropulsionActive = true;
                return DynValue.Nil;
            }));
            instance.Set("Abort", DynValue.NewCallback((_, _) =>
            {
                node.RocketPropulsionActive = false;
                return DynValue.Nil;
            }));
        }
        if (node.Type == "VectorForce")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Force", VectorValue(node.Force));
            instance.Set("RelativeTo", DynValue.NewString(node.RelativeTo));
            instance.Set("ApplyAtCenterOfMass", DynValue.NewBoolean(node.ApplyAtCenterOfMass));
        }
        if (node.Type == "LineForce")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Magnitude", DynValue.NewNumber(node.LineForceMagnitude));
            instance.Set("MaxForce", DynValue.NewNumber(node.LineForceMaxForce));
            instance.Set("InverseSquareLaw", DynValue.NewBoolean(node.LineForceInverseSquareLaw));
            instance.Set("ReactionForceEnabled", DynValue.NewBoolean(node.LineForceReactionForceEnabled));
            instance.Set("ApplyAtCenterOfMass", DynValue.NewBoolean(node.ApplyAtCenterOfMass));
        }
        if (node.Type == "Torque")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Torque", VectorValue(node.TorqueTarget));
            instance.Set("RelativeTo", DynValue.NewString(node.RelativeTo));
        }
        if (node.Type == "SpringConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("CurrentLength", DynValue.NewNumber(node.SpringCurrentLength));
            instance.Set("Damping", DynValue.NewNumber(node.SpringDamping));
            instance.Set("FreeLength", DynValue.NewNumber(node.SpringFreeLength));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.SpringLimitsEnabled));
            instance.Set("MaxForce", DynValue.NewNumber(node.SpringMaxForce));
            instance.Set("MaxLength", DynValue.NewNumber(node.SpringMaxLength));
            instance.Set("MinLength", DynValue.NewNumber(node.SpringMinLength));
            instance.Set("Stiffness", DynValue.NewNumber(node.SpringStiffness));
        }
        if (node.Type == "TorsionSpringConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Coils", DynValue.NewNumber(node.TorsionSpringCoils));
            instance.Set("CurrentAngle", DynValue.NewNumber(node.TorsionSpringCurrentAngle));
            instance.Set("Damping", DynValue.NewNumber(node.TorsionSpringDamping));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.TorsionSpringLimitsEnabled));
            instance.Set("MaxAngle", DynValue.NewNumber(node.TorsionSpringMaxAngle));
            instance.Set("MaxTorque", DynValue.NewNumber(node.TorsionSpringMaxTorque));
            instance.Set("Radius", DynValue.NewNumber(node.TorsionSpringRadius));
            instance.Set("Restitution", DynValue.NewNumber(node.TorsionSpringRestitution));
            instance.Set("Stiffness", DynValue.NewNumber(node.TorsionSpringStiffness));
        }
        if (node.Type == "BallSocketConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.BallSocketLimitsEnabled));
            instance.Set("MaxFrictionTorque", DynValue.NewNumber(node.BallSocketMaxFrictionTorque));
            instance.Set("Radius", DynValue.NewNumber(node.BallSocketRadius));
            instance.Set("Restitution", DynValue.NewNumber(node.BallSocketRestitution));
            instance.Set("UpperAngle", DynValue.NewNumber(node.BallSocketUpperAngle));
            instance.Set("TwistLimitsEnabled", DynValue.NewBoolean(node.BallSocketTwistLimitsEnabled));
            instance.Set("TwistLowerAngle", DynValue.NewNumber(node.BallSocketTwistLowerAngle));
            instance.Set("TwistUpperAngle", DynValue.NewNumber(node.BallSocketTwistUpperAngle));
        }
        if (node.Type == "UniversalConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.UniversalLimitsEnabled));
            instance.Set("MaxAngle", DynValue.NewNumber(node.UniversalMaxAngle));
            instance.Set("Radius", DynValue.NewNumber(node.UniversalRadius));
            instance.Set("Restitution", DynValue.NewNumber(node.UniversalRestitution));
        }
        if (node.Type == "HingeConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("ActuatorType", DynValue.NewString(node.HingeActuatorType));
            instance.Set("AngularResponsiveness", DynValue.NewNumber(node.HingeAngularResponsiveness));
            instance.Set("AngularSpeed", DynValue.NewNumber(node.HingeAngularSpeed));
            instance.Set("AngularVelocity", DynValue.NewNumber(node.HingeAngularVelocity));
            instance.Set("CurrentAngle", DynValue.NewNumber(node.HingeCurrentAngle));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.HingeLimitsEnabled));
            instance.Set("LowerAngle", DynValue.NewNumber(node.HingeLowerAngle));
            instance.Set("MotorMaxAcceleration", DynValue.NewNumber(node.HingeMotorMaxAcceleration));
            instance.Set("MotorMaxTorque", DynValue.NewNumber(node.HingeMotorMaxTorque));
            instance.Set("Radius", DynValue.NewNumber(node.HingeRadius));
            instance.Set("Restitution", DynValue.NewNumber(node.HingeRestitution));
            instance.Set("ServoMaxTorque", DynValue.NewNumber(node.HingeServoMaxTorque));
            instance.Set("TargetAngle", DynValue.NewNumber(node.HingeTargetAngle));
            instance.Set("UpperAngle", DynValue.NewNumber(node.HingeUpperAngle));
        }
        if (node.Type is "PrismaticConstraint" or "CylindricalConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("ActuatorType", DynValue.NewString(node.PrismaticActuatorType));
            instance.Set("CurrentPosition", DynValue.NewNumber(node.PrismaticCurrentPosition));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.PrismaticLimitsEnabled));
            instance.Set("LinearResponsiveness", DynValue.NewNumber(node.PrismaticLinearResponsiveness));
            instance.Set("LowerLimit", DynValue.NewNumber(node.PrismaticLowerLimit));
            instance.Set("MotorMaxAcceleration", DynValue.NewNumber(node.PrismaticMotorMaxAcceleration));
            instance.Set("MotorMaxForce", DynValue.NewNumber(node.PrismaticMotorMaxForce));
            instance.Set("Restitution", DynValue.NewNumber(node.PrismaticRestitution));
            instance.Set("ServoMaxForce", DynValue.NewNumber(node.PrismaticServoMaxForce));
            instance.Set("Size", DynValue.NewNumber(node.PrismaticSize));
            instance.Set("Speed", DynValue.NewNumber(node.PrismaticSpeed));
            instance.Set("TargetPosition", DynValue.NewNumber(node.PrismaticTargetPosition));
            instance.Set("UpperLimit", DynValue.NewNumber(node.PrismaticUpperLimit));
            instance.Set("Velocity", DynValue.NewNumber(node.PrismaticVelocity));
        }
        if (node.Type == "CylindricalConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("ActuatorType", DynValue.NewString(node.PrismaticActuatorType));
            instance.Set("CurrentPosition", DynValue.NewNumber(node.PrismaticCurrentPosition));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.PrismaticLimitsEnabled));
            instance.Set("LinearResponsiveness", DynValue.NewNumber(node.PrismaticLinearResponsiveness));
            instance.Set("LowerLimit", DynValue.NewNumber(node.PrismaticLowerLimit));
            instance.Set("MotorMaxAcceleration", DynValue.NewNumber(node.PrismaticMotorMaxAcceleration));
            instance.Set("MotorMaxForce", DynValue.NewNumber(node.PrismaticMotorMaxForce));
            instance.Set("Restitution", DynValue.NewNumber(node.PrismaticRestitution));
            instance.Set("ServoMaxForce", DynValue.NewNumber(node.PrismaticServoMaxForce));
            instance.Set("Size", DynValue.NewNumber(node.PrismaticSize));
            instance.Set("Speed", DynValue.NewNumber(node.PrismaticSpeed));
            instance.Set("TargetPosition", DynValue.NewNumber(node.PrismaticTargetPosition));
            instance.Set("UpperLimit", DynValue.NewNumber(node.PrismaticUpperLimit));
            instance.Set("Velocity", DynValue.NewNumber(node.PrismaticVelocity));
            instance.Set("AngularActuatorType", DynValue.NewString(node.HingeActuatorType));
            instance.Set("AngularLimitsEnabled", DynValue.NewBoolean(node.HingeLimitsEnabled));
            instance.Set("AngularRestitution", DynValue.NewNumber(node.HingeRestitution));
            instance.Set("AngularResponsiveness", DynValue.NewNumber(node.HingeAngularResponsiveness));
            instance.Set("AngularSpeed", DynValue.NewNumber(node.HingeAngularSpeed));
            instance.Set("AngularVelocity", DynValue.NewNumber(node.HingeAngularVelocity));
            instance.Set("CurrentAngle", DynValue.NewNumber(node.HingeCurrentAngle));
            instance.Set("InclinationAngle", DynValue.NewNumber(node.CylindricalInclinationAngle));
            instance.Set("LowerAngle", DynValue.NewNumber(node.HingeLowerAngle));
            instance.Set("MotorMaxAngularAcceleration", DynValue.NewNumber(node.HingeMotorMaxAcceleration));
            instance.Set("MotorMaxTorque", DynValue.NewNumber(node.HingeMotorMaxTorque));
            instance.Set("RotationAxisVisible", DynValue.NewBoolean(node.CylindricalRotationAxisVisible));
            instance.Set("ServoMaxTorque", DynValue.NewNumber(node.HingeServoMaxTorque));
            instance.Set("TargetAngle", DynValue.NewNumber(node.HingeTargetAngle));
            instance.Set("UpperAngle", DynValue.NewNumber(node.HingeUpperAngle));
        }
        if (node.Type == "RopeConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("CurrentLength", DynValue.NewNumber(node.RopeCurrentLength));
            instance.Set("Length", DynValue.NewNumber(node.RopeLength));
            instance.Set("Restitution", DynValue.NewNumber(node.RopeRestitution));
            instance.Set("WinchEnabled", DynValue.NewBoolean(node.RopeWinchEnabled));
            instance.Set("WinchForce", DynValue.NewNumber(node.RopeWinchForce));
            instance.Set("WinchResponsiveness", DynValue.NewNumber(node.RopeWinchResponsiveness));
            instance.Set("WinchSpeed", DynValue.NewNumber(node.RopeWinchSpeed));
            instance.Set("WinchTarget", DynValue.NewNumber(node.RopeWinchTarget));
        }
        if (node.Type == "RodConstraint")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("CurrentDistance", DynValue.NewNumber(node.RodCurrentDistance));
            instance.Set("Length", DynValue.NewNumber(node.RodLength));
            instance.Set("LimitAngle0", DynValue.NewNumber(node.RodLimitAngle0));
            instance.Set("LimitAngle1", DynValue.NewNumber(node.RodLimitAngle1));
            instance.Set("LimitsEnabled", DynValue.NewBoolean(node.RodLimitsEnabled));
            instance.Set("Thickness", DynValue.NewNumber(node.RodThickness));
        }
        if (node.Type == "LinearVelocity")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("RelativeTo", DynValue.NewString(node.RelativeTo));
            instance.Set("VelocityConstraintMode", DynValue.NewString(node.VelocityConstraintMode));
            instance.Set("VectorVelocity", VectorValue(node.VectorVelocity));
            instance.Set("LineDirection", VectorValue(node.LineDirection));
            instance.Set("LineVelocity", DynValue.NewNumber(node.LineVelocity));
            instance.Set("PlaneVelocity", Vector2Value(node.PlaneVelocity));
            instance.Set("PrimaryTangentAxis", VectorValue(node.PrimaryTangentAxis));
            instance.Set("SecondaryTangentAxis", VectorValue(node.SecondaryTangentAxis));
            instance.Set("ForceLimitsEnabled", DynValue.NewBoolean(node.ForceLimitsEnabled));
            instance.Set("MaxForce", DynValue.NewNumber(node.MaxForce));
            instance.Set("ForceLimitMode", DynValue.NewString(node.ForceLimitMode));
            instance.Set("MaxAxesForce", VectorValue(node.MaxAxesForce));
            instance.Set("MaxPlanarAxesForce", Vector2Value(node.MaxPlanarAxesForce));
            instance.Set("ReactionForceEnabled", DynValue.NewBoolean(node.LinearVelocityReactionForceEnabled));
        }
        if (node.Type == "AlignPosition")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Responsiveness", DynValue.NewNumber(node.Responsiveness));
            instance.Set("MaxVelocity", DynValue.NewNumber(node.MaxVelocity));
            instance.Set("MaxForce", DynValue.NewNumber(node.MaxForce));
            instance.Set("RigidityEnabled", DynValue.NewBoolean(node.RigidityEnabled));
            instance.Set("ReactionForceEnabled", DynValue.NewBoolean(node.ReactionForceEnabled));
            instance.Set("ApplyAtCenterOfMass", DynValue.NewBoolean(node.ApplyAtCenterOfMass));
        }
        if (node.Type == "AlignOrientation")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("Mode", DynValue.NewString(node.AlignmentMode));
            instance.Set("Rotation", VectorValue(node.AlignOrientationRotation));
            instance.Set("Responsiveness", DynValue.NewNumber(node.Responsiveness));
            instance.Set("MaxAngularVelocity", DynValue.NewNumber(node.MaxAngularVelocity));
            instance.Set("MaxTorque", DynValue.NewNumber(node.MaxTorque));
            instance.Set("RigidityEnabled", DynValue.NewBoolean(node.RigidityEnabled));
            instance.Set("ReactionTorqueEnabled", DynValue.NewBoolean(node.ReactionTorqueEnabled));
        }
        if (node.Type == "AngularVelocity")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            instance.Set("AngularVelocity", VectorValue(node.AngularVelocityTarget));
            instance.Set("RelativeTo", DynValue.NewString(node.RelativeTo));
            instance.Set("MaxTorque", DynValue.NewNumber(node.MaxTorque));
            instance.Set("ReactionTorqueEnabled", DynValue.NewBoolean(node.ReactionTorqueEnabled));
        }
        instance.Set("SetProperty", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index + 1 ? SetPropertyValue(node, args[index].CastToString() ?? string.Empty, args[index + 1]) : DynValue.Nil;
        }));
        instance.Set("GetProperty", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index ? GetPropertyValue(node, args[index].CastToString() ?? string.Empty) : DynValue.Nil;
        }));
        if (node.Type == "UIPageLayout")
        {
            instance.Set("Next", DynValue.NewCallback((_, _) => ChangePage(node, node.CurrentPageIndex + 1)));
            instance.Set("Previous", DynValue.NewCallback((_, _) => ChangePage(node, node.CurrentPageIndex - 1)));
            instance.Set("JumpToIndex", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                return args.Count > index && args[index].Type == DataType.Number && double.IsFinite(args[index].Number)
                    && args[index].Number == Math.Truncate(args[index].Number) && args[index].Number >= 1 && args[index].Number <= 1000000
                    ? ChangePage(node, (int)args[index].Number) : DynValue.Nil;
            }));
            instance.Set("JumpTo", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                return args.Count > index && ReadNodeArgument(args[index]) is { } page
                    ? ChangePage(node, page) : DynValue.Nil;
            }));
        }
        instance.Set("WaitForChild", script.Globals.Get("WaitForChild"));
        instance.Set("waitforchild", script.Globals.Get("waitforchild"));
        instance.Set("FindFirstChild", DynValue.NewCallback((_, args) => FindFirstChild(node, args)));
        instance.Set("FindFirstChildWhichIsA", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var className = args.Count > index ? args[index].CastToString() : null;
            var recursive = args.Count > index + 1 && args[index + 1].CastToBool();
            if (className is null) return DynValue.Nil;
            var candidates = recursive ? GameProjectStore.Flatten(node.Children) : node.Children;
            var child = candidates.FirstOrDefault(candidate => IsA(candidate, className));
            return child is not null && instances.TryGetValue(child.Id, out var childInstance) ? DynValue.NewTable(childInstance) : DynValue.Nil;
        }));
        instance.Set("GetChildren", DynValue.NewCallback((_, _) => GetChildren(node)));
        instance.Set("FindFirstChildOfClass", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var className = args.Count > index ? args[index].CastToString() : null;
            var child = className is null ? null : node.Children.FirstOrDefault(candidate => candidate.Type == className);
            return child is not null && instances.TryGetValue(child.Id, out var childInstance) ? DynValue.NewTable(childInstance) : DynValue.Nil;
        }));
        instance.Set("GetDescendants", DynValue.NewCallback((_, _) =>
        {
            var descendants = new Table(script);
            var descendantIndex = 1;
            foreach (var descendant in GameProjectStore.Flatten(node.Children))
                if (instances.TryGetValue(descendant.Id, out var descendantInstance)) descendants.Set(descendantIndex++, DynValue.NewTable(descendantInstance));
            return DynValue.NewTable(descendants);
        }));
        instance.Set("FindFirstAncestor", DynValue.NewCallback((_, args) => FindFirstAncestorByName(node, args)));
        instance.Set("FindFirstAncestorOfClass", DynValue.NewCallback((_, args) => FindFirstAncestorByClass(node, args, false)));
        instance.Set("FindFirstAncestorWhichIsA", DynValue.NewCallback((_, args) => FindFirstAncestorByClass(node, args, true)));
        instance.Set("IsDescendantOf", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var ancestor = args.Count > index ? ReadNodeArgument(args[index]) : null;
            var parent = FindParentNode(node);
            while (parent is not null)
            {
                if (ancestor is not null && parent.Id == ancestor.Id) return DynValue.NewBoolean(true);
                parent = FindParentNode(parent);
            }
            return DynValue.NewBoolean(false);
        }));
        instance.Set("GetFullName", DynValue.NewCallback((_, _) =>
        {
            var names = new List<string> { node.Name };
            var parent = FindParentNode(node);
            while (parent is not null)
            {
                names.Add(parent.Name);
                parent = FindParentNode(parent);
            }
            names.Add("game");
            names.Reverse();
            return DynValue.NewString(string.Join('.', names));
        }));
        instance.Set("ClearAllChildren", DynValue.NewCallback((_, _) =>
        {
            foreach (var child in node.Children.ToArray()) DestroyInstance(child);
            return DynValue.Nil;
        }));
        instance.Set("IsA", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var className = args.Count > index ? args[index].CastToString() : null;
            return DynValue.NewBoolean(className is not null && IsA(node, className));
        }));
        instance.Set("Destroy", DynValue.NewCallback((_, _) => DestroyInstance(node)));
        instance.Set("Clone", DynValue.NewCallback((_, _) => CloneInstance(node)));
        if (node.Type == "Model")
        {
            instance.Set("MoveTo", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (index + 1 != args.Count) return DynValue.Nil;
                return SetPropertyValue(node, "Position", args[index]);
            }));
            instance.Set("RotateTo", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (index + 1 != args.Count) return DynValue.Nil;
                return SetPropertyValue(node, "Rotation", args[index]);
            }));
        }
        InstallAttributeApi(node, instance);
        InstallTagApi(node, instance);
        if (node.Type is "Workspace" or "WorldModel")
        {
            instance.Set("Raycast", DynValue.NewCallback((_, args) => Raycast(node, args)));
            instance.Set("Blockcast", DynValue.NewCallback((_, args) => Blockcast(node, args)));
            instance.Set("Spherecast", DynValue.NewCallback((_, args) => Spherecast(node, args)));
            instance.Set("Shapecast", DynValue.NewCallback((_, args) => Shapecast(node, args)));
            instance.Set("GetPartsInPart", DynValue.NewCallback((_, args) => GetPartsInPart(node, args)));
            instance.Set("GetPartBoundsInBox", DynValue.NewCallback((_, args) => GetPartBoundsInBox(node, args)));
            instance.Set("GetPartBoundsInRadius", DynValue.NewCallback((_, args) => GetPartBoundsInRadius(node, args)));
        }
        if (node.Type == "Terrain") InstallTerrainApi(node, instance);
        if (SceneNodeTypes.IsValueObject(node))
        {
            var changed = new Table(script);
            changed.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                if (currentGameScript is null || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
                if (!valueChangedCallbacks.TryGetValue(node.Id, out var callbacks)) valueChangedCallbacks[node.Id] = callbacks = [];
                callbacks.Add(new ScriptCallback(currentGameScript, args[index], "Changed"));
                return DynValue.Nil;
            }));
            instance.Set("Changed", DynValue.NewTable(changed));
        }
        if (node.Type == "BindableEvent") InstallBindableEvent(node, instance);
        if (node.Type is "RemoteEvent" or "UnreliableRemoteEvent") InstallRemoteEvent(node, instance);
        if (node.Type == "BindableFunction")
        {
            var getCallback = DynValue.NewCallback((_, _) => bindableFunctionCallbacks.TryGetValue(node.Id, out var callback) && IsTaskOwnerActive(callback.GameScript) ? callback.Function : DynValue.Nil);
            var beginCallback = DynValue.NewCallback((_, args) => BeginBindableInvocation(node, args));
            var endCallback = DynValue.NewCallback((_, args) => EndBindableInvocation(args));
            instance.Set("Invoke", script.Call(bindableInvokeFactory, getCallback, beginCallback, endCallback));
        }
        if (node.Type == "RemoteFunction") InstallRemoteFunction(node, instance);
        if (node.Type == "Sound")
        {
            instance.Set("Play", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(true))));
            instance.Set("Stop", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(false))));
        }
        if (node.Type is "Seat" or "VehicleSeat")
        {
            instance.Set("Sit", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (args.Count > index && ReadNodeArgument(args[index]) is { Type: "Humanoid" } humanoid)
                    sitOnSeat?.Invoke(node, humanoid);
                return DynValue.Nil;
            }));
        }
        if (node.Type == "Humanoid")
        {
            instance.Set("MoveTo", DynValue.NewCallback((_, args) =>
            {
                var index = GetInstanceMethodArgumentIndex(node, args);
                if (args.Count > index && TryReadVector3(args[index], out var destination))
                {
                    humanoidMoveTargets[node.Id] = new HumanoidMoveTarget(destination);
                    if (TryGetHumanoidRoot(node, out var root)) SetHumanoidHorizontalVelocity(root, Vector3.Zero);
                }
                return DynValue.Nil;
            }));
            instance.Set("TakeDamage", DynValue.NewCallback((_, args) => TakeHumanoidDamage(node, args)));
            var healthChanged = new Table(script);
            runtimeTypeTags.Add(healthChanged, new LuaXTypeTag("RBXScriptSignal"));
            healthChanged.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, healthChanged) ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? ConnectScriptCallback(humanoidHealthChangedCallbacks, node.Id, args[index], "HealthChanged") : DynValue.Nil;
            }));
            instance.Set("HealthChanged", DynValue.NewTable(healthChanged));
            var died = new Table(script);
            died.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? ConnectScriptCallback(humanoidDiedCallbacks, node.Id, args[index], "Died") : DynValue.Nil;
            }));
            instance.Set("Died", DynValue.NewTable(died));
            var moveToFinished = new Table(script);
            runtimeTypeTags.Add(moveToFinished, new LuaXTypeTag("RBXScriptSignal"));
            moveToFinished.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, moveToFinished) ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? ConnectScriptCallback(humanoidMoveToFinishedCallbacks, node.Id, args[index], "MoveToFinished") : DynValue.Nil;
            }));
            instance.Set("MoveToFinished", DynValue.NewTable(moveToFinished));
        }
        if (node.Type == "Explosion")
        {
            var hit = new Table(script);
            runtimeTypeTags.Add(hit, new LuaXTypeTag("RBXScriptSignal"));
            hit.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, hit) ? 1 : 0;
                if (currentGameScript is null || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
                if (!explosionHitCallbacks.TryGetValue(node.Id, out var callbacks)) explosionHitCallbacks[node.Id] = callbacks = [];
                callbacks.Add(new ScriptCallback(currentGameScript, args[index], "Hit"));
                return DynValue.Nil;
            }));
            instance.Set("Hit", DynValue.NewTable(hit));
        }
        var touched = new Table(script);
        touched.Set("Connect", DynValue.NewCallback((_, args) => RegisterTouch(node.Id, args[1])));
        instance.Set("Touched", DynValue.NewTable(touched));
        instance.Set("touched", DynValue.NewTable(touched));
        if (node.Type is "TextButton" or "ImageButton" or "Tool")
        {
            var activated = new Table(script);
            var connect = DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? RegisterGuiActivated(node.Id, args[index])
                    : DynValue.Nil;
            });
            activated.Set("Connect", connect);
            instance.Set("Activated", DynValue.NewTable(activated));
            if (node.Type == "Tool")
            {
                instance.Set("Equipped", DynValue.NewTable(CreateToolSignal(node.Id, "Equipped", toolEquippedCallbacks)));
                instance.Set("Unequipped", DynValue.NewTable(CreateToolSignal(node.Id, "Unequipped", toolUnequippedCallbacks)));
                instance.Set("Deactivated", DynValue.NewTable(CreateToolSignal(node.Id, "Deactivated", toolDeactivatedCallbacks)));
                instance.Set("Activate", DynValue.NewCallback((_, _) => { ActivateTool(node.Id); return DynValue.Nil; }));
                instance.Set("Deactivate", DynValue.NewCallback((_, _) => { DeactivateTool(node.Id); return DynValue.Nil; }));
                instance.Set("ToolTip", DynValue.NewString(node.ToolTip));
                instance.Set("RequiresHandle", DynValue.NewBoolean(node.RequiresHandle));
                instance.Set("CanBeDropped", DynValue.NewBoolean(node.CanBeDropped));
                instance.Set("ManualActivationOnly", DynValue.NewBoolean(node.ManualActivationOnly));
                instance.Set("GripPosition", VectorValue(node.GripPosition));
                instance.Set("GripRotation", VectorValue(node.GripRotation));
            }
            else instance.Set("MouseButton1Click", DynValue.NewTable(activated));
        }
        if (node.Type == "TextBox")
        {
            var textChanged = new Table(script);
            textChanged.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? RegisterGuiTextChanged(node.Id, args[index])
                    : DynValue.Nil;
            }));
            instance.Set("TextChanged", DynValue.NewTable(textChanged));
        }
        if (node.Type == "ProximityPrompt")
        {
            var triggered = new Table(script);
            triggered.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? RegisterPromptTriggered(node.Id, args[index])
                    : DynValue.Nil;
            }));
            instance.Set("Triggered", DynValue.NewTable(triggered));
        }
        if (node.Type == "ClickDetector")
        {
            var mouseClick = new Table(script);
            mouseClick.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                return args.Count > index && args[index].Type == DataType.Function
                    ? RegisterClickDetector(node.Id, args[index])
                    : DynValue.Nil;
            }));
            instance.Set("MouseClick", DynValue.NewTable(mouseClick));
        }
        var instanceMetatable = new Table(script);
        instanceMetatable.Set("__index", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 2 || args[1].Type != DataType.String) return DynValue.Nil;
            var name = args[1].String;
            if (node.Type == "RemoteFunction" && name is "OnServerInvoke" or "OnClientInvoke")
                return remoteFunctionCallbacks.TryGetValue((node.Id, name), out var invokeCallback) ? invokeCallback.Function : DynValue.Nil;
            foreach (var child in node.Children)
            {
                if (IsReservedBindableMember(node, child.Name)) continue;
                if (!instances.TryGetValue(child.Id, out var childInstance)) continue;
                if (string.Equals(childInstance.Get("Name").CastToString(), name, StringComparison.Ordinal))
                    return DynValue.NewTable(childInstance);
            }
            return DynValue.Nil;
        }));
        if (node.Type is "BindableFunction" or "RemoteFunction")
            instanceMetatable.Set("__newindex", DynValue.NewCallback((_, args) =>
            {
                if (args.Count < 3) return DynValue.Nil;
                if (node.Type == "BindableFunction" && args[1].Type == DataType.String && args[1].String == "OnInvoke")
                {
                    SetBindableFunctionCallback(node, args[2]);
                    return DynValue.Nil;
                }
                if (node.Type == "RemoteFunction" && args[1].Type == DataType.String && args[1].String is "OnServerInvoke" or "OnClientInvoke")
                {
                    SetRemoteFunctionCallback(node, args[1].String, args[2]);
                    return DynValue.Nil;
                }
                instance.Set(args[1], args[2]);
                return DynValue.Nil;
            }));
        instance.MetaTable = instanceMetatable;
        if (node.Type == "Humanoid")
        {
            humanoidWasAlive[node.Id] = node.Health > 0;
            humanoidLastHealth[node.Id] = node.Health;
        }
        return instance;
    }

    private void InstallTerrainApi(SceneNode terrain, Table instance)
    {
        instance.Set("FillBlock", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(terrain, args);
            if (args.Count <= index + 1 || !IsTypedValue(args[index], "Vector3") || !IsTypedValue(args[index + 1], "Vector3")) return DynValue.Nil;
            var position = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
            var size = ReadVector(args[index + 1], [float.NaN, float.NaN, float.NaN]);
            var material = ReadTerrainMaterial(args, index + 2);
            if (!IsValidTerrainRegion(position, size) || material is null) return DynValue.Nil;
            if (material == "Air") ClearTerrain(terrain, position, size);
            else FillTerrain(terrain, position, size, material, null);
            return DynValue.Nil;
        }));
        instance.Set("FillBall", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(terrain, args);
            if (args.Count <= index + 1 || !IsTypedValue(args[index], "Vector3") || args[index + 1].Type != DataType.Number
                || !double.IsFinite(args[index + 1].Number)) return DynValue.Nil;
            var position = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
            var radius = (float)Math.Clamp(args[index + 1].Number, 0, 512);
            var material = ReadTerrainMaterial(args, index + 2);
            if (!IsValidTerrainPosition(position) || radius <= 0 || material is null) return DynValue.Nil;
            if (material == "Air") ClearTerrain(terrain, position, [radius * 2, radius * 2, radius * 2], radius);
            else FillTerrain(terrain, position, [radius * 2, radius * 2, radius * 2], material, radius);
            return DynValue.Nil;
        }));
        instance.Set("FillCylinder", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(terrain, args);
            if (args.Count <= index + 2 || !IsTypedValue(args[index], "Vector3") || !IsTypedValue(args[index + 1], "Vector3")
                || !IsTypedValue(args[index + 2], "Vector3")) return DynValue.Nil;
            var position = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
            var size = ReadVector(args[index + 1], [float.NaN, float.NaN, float.NaN]);
            var rotation = ReadVector(args[index + 2], [float.NaN, float.NaN, float.NaN]);
            var material = ReadTerrainMaterial(args, index + 3);
            if (!IsValidTerrainRegion(position, size) || !IsValidTerrainRotation(rotation) || material is null) return DynValue.Nil;
            var contains = CreateTerrainCylinderPredicate(position, size, rotation);
            var bounds = GetTerrainRotatedBounds(position, size, rotation);
            if (material == "Air") ClearTerrain(terrain, bounds.Position, bounds.Size, contains: contains);
            else FillTerrain(terrain, bounds.Position, bounds.Size, material, null, contains);
            return DynValue.Nil;
        }));
        instance.Set("FillWedge", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(terrain, args);
            if (args.Count <= index + 2 || !IsTypedValue(args[index], "Vector3") || !IsTypedValue(args[index + 1], "Vector3")
                || !IsTypedValue(args[index + 2], "Vector3")) return DynValue.Nil;
            var position = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
            var size = ReadVector(args[index + 1], [float.NaN, float.NaN, float.NaN]);
            var rotation = ReadVector(args[index + 2], [float.NaN, float.NaN, float.NaN]);
            var material = ReadTerrainMaterial(args, index + 3);
            if (!IsValidTerrainRegion(position, size) || !IsValidTerrainRotation(rotation) || material is null) return DynValue.Nil;
            var contains = CreateTerrainWedgePredicate(position, size, rotation);
            var bounds = GetTerrainRotatedBounds(position, size, rotation);
            if (material == "Air") ClearTerrain(terrain, bounds.Position, bounds.Size, contains: contains);
            else FillTerrain(terrain, bounds.Position, bounds.Size, material, null, contains);
            return DynValue.Nil;
        }));
        instance.Set("ClearBlock", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(terrain, args);
            if (args.Count <= index + 1 || !IsTypedValue(args[index], "Vector3") || !IsTypedValue(args[index + 1], "Vector3")) return DynValue.Nil;
            var position = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
            var size = ReadVector(args[index + 1], [float.NaN, float.NaN, float.NaN]);
            if (!IsValidTerrainRegion(position, size)) return DynValue.Nil;
            ClearTerrain(terrain, position, size);
            return DynValue.Nil;
        }));
        instance.Set("Clear", DynValue.NewCallback((_, _) =>
        {
            ClearTerrain(terrain, null, null);
            return DynValue.Nil;
        }));
    }

    private static string? ReadTerrainMaterial(CallbackArguments args, int index)
    {
        var material = index < args.Count ? args[index].CastToString() : "Grass";
        return SceneNodeTypes.IsTerrainMaterial(material) ? material : null;
    }

    private static bool IsValidTerrainPosition(float[] position) => position.Length == 3
        && position.All(value => float.IsFinite(value) && Math.Abs(value) <= 1_000_000);

    private static bool IsValidTerrainRegion(float[] position, float[] size) => IsValidTerrainPosition(position)
        && size.All(value => float.IsFinite(value) && value > 0 && value <= 1024);

    private static bool IsValidTerrainRotation(float[] rotation) => rotation.Length == 3
        && rotation.All(value => float.IsFinite(value) && Math.Abs(value) <= 36000);

    private void FillTerrain(SceneNode terrain, float[] position, float[] size, string material, float? sphereRadius, Func<float[], bool>? contains = null)
    {
        var minX = (int)Math.Floor((position[0] - size[0] * .5f + TerrainCellSize * .5f) / TerrainCellSize);
        var minY = (int)Math.Floor((position[1] - size[1] * .5f + TerrainCellSize * .5f) / TerrainCellSize);
        var minZ = (int)Math.Floor((position[2] - size[2] * .5f + TerrainCellSize * .5f) / TerrainCellSize);
        var maxX = (int)Math.Ceiling((position[0] + size[0] * .5f + TerrainCellSize * .5f) / TerrainCellSize) - 1;
        var maxY = (int)Math.Ceiling((position[1] + size[1] * .5f + TerrainCellSize * .5f) / TerrainCellSize) - 1;
        var maxZ = (int)Math.Ceiling((position[2] + size[2] * .5f + TerrainCellSize * .5f) / TerrainCellSize) - 1;
        var cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
        if (cellCount <= 0 || cellCount > MaxTerrainCells) return;

        var targetCells = new List<(string Name, float[] Position)>();
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var cellPosition = new[] { x * TerrainCellSize, y * TerrainCellSize, z * TerrainCellSize };
            if (sphereRadius is { } radius && !TerrainCellIntersectsSphere(position, radius, cellPosition)) continue;
            if (contains is not null && !contains(cellPosition)) continue;
            targetCells.Add(($"TerrainCell_{x}_{y}_{z}", cellPosition));
        }

        var existingCells = terrain.Children.Where(child => child.Type == "Part" && child.Name.StartsWith("TerrainCell_", StringComparison.Ordinal))
            .GroupBy(child => child.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var additionalCells = targetCells.Count(target => !existingCells.ContainsKey(target.Name));
        if (existingCells.Count + additionalCells > MaxTerrainCells) return;

        SceneNode? changedNode = null;
        foreach (var (name, cellPosition) in targetCells)
        {
            existingCells.TryGetValue(name, out var cell);
            if (cell is null)
            {
                cell = new SceneNode
                {
                    Type = "Part", Name = name, Position = cellPosition,
                    Size = [TerrainCellSize, TerrainCellSize, TerrainCellSize],
                    Material = material, Color = SceneNodeTypes.TerrainColor(material),
                    Anchored = true, CanCollide = true, IsExplorerVisible = false
                };
                terrain.Children.Add(cell);
                createdNodes[cell.Id] = cell;
                instances[cell.Id] = CreateInstance(cell);
                instances[cell.Id].Set("Parent", DynValue.NewTable(instances[terrain.Id]));
            }
            else
            {
                cell.Material = material;
                cell.Color = SceneNodeTypes.TerrainColor(material);
                if (instances.TryGetValue(cell.Id, out var cellInstance))
                {
                    cellInstance.Set("Material", DynValue.NewString(material));
                    cellInstance.Set("Color", ColorValue(cell.Color));
                }
            }
            changedNode = cell;
        }
        if (changedNode is null) return;
        RefreshParentChildren(terrain);
        propertyChanged?.Invoke(changedNode, "Parent", DynValue.NewTable(instances[terrain.Id]));
    }

    private static bool TerrainCellIntersectsSphere(float[] position, float radius, float[] cellPosition)
    {
        var half = TerrainCellSize * .5f;
        var dx = position[0] - Math.Clamp(position[0], cellPosition[0] - half, cellPosition[0] + half);
        var dy = position[1] - Math.Clamp(position[1], cellPosition[1] - half, cellPosition[1] + half);
        var dz = position[2] - Math.Clamp(position[2], cellPosition[2] - half, cellPosition[2] + half);
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }

    private static Func<float[], bool> CreateTerrainCylinderPredicate(float[] position, float[] size, float[] rotation)
    {
        var inverseRotation = Matrix4x4.Transpose(RotationMath.FromDegrees(rotation));
        return cellPosition =>
        {
            var local = Vector3.TransformNormal(new Vector3(cellPosition[0] - position[0], cellPosition[1] - position[1], cellPosition[2] - position[2]), inverseRotation);
            var x = local.X / (size[0] * .5f);
            var y = local.Y / (size[1] * .5f);
            var z = local.Z / (size[2] * .5f);
            return Math.Abs(y) <= 1 && x * x + z * z <= 1;
        };
    }

    private static Func<float[], bool> CreateTerrainWedgePredicate(float[] position, float[] size, float[] rotation)
    {
        var inverseRotation = Matrix4x4.Transpose(RotationMath.FromDegrees(rotation));
        return cellPosition =>
        {
            var local = Vector3.TransformNormal(new Vector3(cellPosition[0] - position[0], cellPosition[1] - position[1], cellPosition[2] - position[2]), inverseRotation);
            var x = local.X / size[0];
            var y = local.Y / size[1];
            var z = local.Z / size[2];
            return Math.Abs(x) <= .5f && Math.Abs(z) <= .5f && y >= -.5f && y <= -x;
        };
    }

    private static (float[] Position, float[] Size) GetTerrainRotatedBounds(float[] position, float[] size, float[] rotation)
    {
        var transform = RotationMath.FromDegrees(rotation);
        var minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = Vector3.TransformNormal(new Vector3(x * size[0] * .5f, y * size[1] * .5f, z * size[2] * .5f), transform);
            for (var axis = 0; axis < 3; axis++)
            {
                var component = axis == 0 ? corner.X : axis == 1 ? corner.Y : corner.Z;
                minimum[axis] = Math.Min(minimum[axis], position[axis] + component);
                maximum[axis] = Math.Max(maximum[axis], position[axis] + component);
            }
        }
        return (
            [(minimum[0] + maximum[0]) * .5f, (minimum[1] + maximum[1]) * .5f, (minimum[2] + maximum[2]) * .5f],
            [maximum[0] - minimum[0], maximum[1] - minimum[1], maximum[2] - minimum[2]]);
    }

    private void ClearTerrain(SceneNode terrain, float[]? position, float[]? size, float? sphereRadius = null, Func<float[], bool>? contains = null)
    {
        var region = position is null || size is null ? null : new SceneNode { Type = "Part", Position = position, Size = size };
        var cells = terrain.Children.Where(node => node.Name.StartsWith("TerrainCell_", StringComparison.Ordinal)
                && node.Type == "Part" && (contains is null ? region is null || CollisionGeometry.Intersects(node, region) : contains(node.Position))
                && (sphereRadius is null || position is not null && TerrainCellIntersectsSphere(position, sphereRadius.Value, node.Position)))
            .ToArray();
        foreach (var cell in cells) DestroyInstance(cell, notifyParentChange: false, refreshParent: false);
        if (cells.Length == 0) return;
        RefreshParentChildren(terrain);
        propertyChanged?.Invoke(cells[0], "Parent", DynValue.Nil);
    }

    private static bool IsReservedBindableMember(SceneNode node, string name) =>
        node.Type == "BindableFunction" && name is ("OnInvoke" or "Invoke")
        || node.Type == "BindableEvent" && name is ("Event" or "Fire")
        || (node.Type is "RemoteEvent" or "UnreliableRemoteEvent") && name is ("OnServerEvent" or "OnClientEvent" or "FireServer" or "FireClient" or "FireAllClients")
        || node.Type == "RemoteFunction" && name is ("OnServerInvoke" or "OnClientInvoke" or "InvokeServer" or "InvokeClient");

    private void SetBindableFunctionCallback(SceneNode node, DynValue value)
    {
        if (value.Type == DataType.Function && currentGameScript is { } owner)
            bindableFunctionCallbacks[node.Id] = new ScriptCallback(owner, value, "OnInvoke");
        else if (value.Type == DataType.Nil) bindableFunctionCallbacks.Remove(node.Id);
    }

    private void SetRemoteFunctionCallback(SceneNode node, string callbackName, DynValue value)
    {
        var key = (node.Id, callbackName);
        if (value.Type == DataType.Function && currentGameScript is { } owner)
            remoteFunctionCallbacks[key] = new ScriptCallback(owner, value, callbackName);
        else if (value.Type == DataType.Nil)
            remoteFunctionCallbacks.Remove(key);
    }

    private DynValue BeginRemoteFunctionInvocation(SceneNode node, string callbackName, CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.Thread
            || !remoteFunctionCallbacks.TryGetValue((node.Id, callbackName), out var callback)
            || !IsTaskOwnerActive(callback.GameScript)) return DynValue.NewBoolean(false);
        var coroutine = args[0].Coroutine;
        if (!bindableInvocationScopes.TryGetValue(coroutine, out var scopes))
            bindableInvocationScopes[coroutine] = scopes = new Stack<BindableInvocationScope>();
        scopes.Push(new BindableInvocationScope(currentGameScript, callback.GameScript));
        BindCurrentScript(callback.GameScript);
        return DynValue.NewBoolean(true);
    }

    private void InstallRemoteFunction(SceneNode node, Table instance)
    {
        DynValue CreateInvoker(string callbackName)
        {
            var getCallback = DynValue.NewCallback((_, _) => remoteFunctionCallbacks.TryGetValue((node.Id, callbackName), out var callback) && IsTaskOwnerActive(callback.GameScript) ? callback.Function : DynValue.Nil);
            var beginCallback = DynValue.NewCallback((_, args) => BeginRemoteFunctionInvocation(node, callbackName, args));
            var endCallback = DynValue.NewCallback((_, args) => EndBindableInvocation(args));
            return script.Call(bindableInvokeFactory, getCallback, beginCallback, endCallback);
        }

        instance.Set("InvokeServer", CreateInvoker("OnServerInvoke"));
        var invokeClient = CreateInvoker("OnClientInvoke");
        var invokeClientFactory = script.DoString("""
            return function(invoke, validatePlayer)
                return function(self, player, ...)
                    if not validatePlayer(self, player) then error('InvokeClient target is not the local player') end
                    return invoke(self, ...)
                end
            end
            """);
        var validatePlayer = DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var target = args.Count > index ? ReadNodeArgument(args[index]) : null;
            var localPlayer = project.Players.Children.FirstOrDefault(player => player.Type == "Player" && player.Name == "LocalPlayer");
            return DynValue.NewBoolean(target is not null && localPlayer is not null && target.Id == localPlayer.Id);
        });
        instance.Set("InvokeClient", script.Call(invokeClientFactory, invokeClient, validatePlayer));
    }

    private DynValue BeginBindableInvocation(SceneNode node, CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.Thread
            || !bindableFunctionCallbacks.TryGetValue(node.Id, out var callback)
            || !IsTaskOwnerActive(callback.GameScript)) return DynValue.NewBoolean(false);
        var coroutine = args[0].Coroutine;
        if (!bindableInvocationScopes.TryGetValue(coroutine, out var scopes))
            bindableInvocationScopes[coroutine] = scopes = new Stack<BindableInvocationScope>();
        scopes.Push(new BindableInvocationScope(currentGameScript, callback.GameScript));
        BindCurrentScript(callback.GameScript);
        return DynValue.NewBoolean(true);
    }

    private DynValue EndBindableInvocation(CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.Thread
            || !bindableInvocationScopes.TryGetValue(args[0].Coroutine, out var scopes)
            || scopes.Count == 0) return DynValue.Nil;
        var previous = scopes.Pop().Previous;
        if (scopes.Count == 0) bindableInvocationScopes.Remove(args[0].Coroutine);
        currentGameScript = previous;
        return DynValue.Nil;
    }

    private void InstallBindableEvent(SceneNode node, Table instance)
    {
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                if (bindableEventCallbacks.TryGetValue(node.Id, out var current)) current.Remove(connection);
                return DynValue.Nil;
            }));
            if (!bindableEventCallbacks.TryGetValue(node.Id, out var callbacks)) bindableEventCallbacks[node.Id] = callbacks = [];
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        instance.Set("Event", DynValue.NewTable(signal));
        instance.Set("Fire", DynValue.NewCallback((_, args) =>
        {
            if (!bindableEventCallbacks.TryGetValue(node.Id, out var callbacks)) return DynValue.Nil;
            var index = GetInstanceMethodArgumentIndex(node, args);
            var arguments = new DynValue[args.Count - index];
            for (var argument = 0; argument < arguments.Length; argument++) arguments[argument] = args[argument + index];
            foreach (var connection in callbacks.ToArray())
            {
                if (!connection.Connected) continue;
                scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), arguments, 0, true));
            }
            return DynValue.Nil;
        }));
    }

    private void InstallRemoteEvent(SceneNode node, Table instance)
    {
        instance.Set("OnServerEvent", DynValue.NewTable(CreateRemoteSignal(node, "OnServerEvent")));
        instance.Set("OnClientEvent", DynValue.NewTable(CreateRemoteSignal(node, "OnClientEvent")));
        instance.Set("FireServer", DynValue.NewCallback((_, args) => FireRemoteEvent(node, "OnServerEvent", args, includePlayer: true)));
        instance.Set("FireClient", DynValue.NewCallback((_, args) => FireRemoteEvent(node, "OnClientEvent", args, targeted: true)));
        instance.Set("FireAllClients", DynValue.NewCallback((_, args) => FireRemoteEvent(node, "OnClientEvent", args)));
    }

    private Table CreateRemoteSignal(SceneNode node, string signalName)
    {
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var key = RemoteSignalKey(node.Id, signalName);
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                if (bindableEventCallbacks.TryGetValue(key, out var current)) current.Remove(connection);
                return DynValue.Nil;
            }));
            if (!bindableEventCallbacks.TryGetValue(key, out var callbacks)) bindableEventCallbacks[key] = callbacks = [];
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        return signal;
    }

    private DynValue FireRemoteEvent(SceneNode node, string signalName, CallbackArguments args, bool includePlayer = false, bool targeted = false)
    {
        var index = GetInstanceMethodArgumentIndex(node, args);
        if (targeted)
        {
            if (args.Count <= index || ReadNodeArgument(args[index]) is not { Type: "Player", Name: "LocalPlayer" }) return DynValue.Nil;
            index++;
        }
        var payload = new List<DynValue>();
        if (includePlayer)
        {
            var localPlayer = project.Players.Children.FirstOrDefault(player => player.Type == "Player" && player.Name == "LocalPlayer");
            if (localPlayer is null || !instances.TryGetValue(localPlayer.Id, out var playerInstance)) return DynValue.Nil;
            payload.Add(DynValue.NewTable(playerInstance));
        }
        for (var i = index; i < args.Count; i++) payload.Add(args[i]);
        var key = RemoteSignalKey(node.Id, signalName);
        if (!bindableEventCallbacks.TryGetValue(key, out var listeners)) return DynValue.Nil;
        foreach (var connection in listeners.ToArray())
        {
            if (!connection.Connected || !IsTaskOwnerActive(connection.Owner)) continue;
            scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), payload.ToArray(), 0, true));
        }
        return DynValue.Nil;
    }

    private void FirePlayerCharacterSignal(SceneNode player, string signalName, string characterId)
    {
        if (player.Type != "Player" || string.IsNullOrEmpty(characterId)
            || !instances.TryGetValue(characterId, out var characterInstance)) return;
        var key = RemoteSignalKey(player.Id, signalName);
        if (!bindableEventCallbacks.TryGetValue(key, out var listeners)) return;
        var argument = DynValue.NewTable(characterInstance);
        foreach (var connection in listeners.ToArray())
        {
            if (!connection.Connected || !IsTaskOwnerActive(connection.Owner)) continue;
            scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), [argument], 0, true));
        }
    }

    public void FireRocketReachedTarget(SceneNode node)
    {
        var key = RemoteSignalKey(node.Id, "ReachedTarget");
        if (!bindableEventCallbacks.TryGetValue(key, out var listeners)) return;
        foreach (var connection in listeners.ToArray())
        {
            if (!connection.Connected || !IsTaskOwnerActive(connection.Owner)) continue;
            scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), [], 0, true));
        }
    }

    private static string RemoteSignalKey(string nodeId, string signalName) => $"{nodeId}::remote::{signalName}";

    private void InstallAttributeApi(SceneNode node, Table instance)
    {
        instance.Set("AttributeChanged", DynValue.NewTable(GetAttributeSignal(node, null)));
        instance.Set("GetAttribute", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index && args[index].Type == DataType.String
                && node.Attributes.TryGetValue(args[index].String, out var value) ? AttributeValueToLua(value) : DynValue.Nil;
        }));
        instance.Set("GetAttributes", DynValue.NewCallback((_, _) =>
        {
            var values = new Table(script);
            foreach (var (name, value) in node.Attributes) values.Set(name, AttributeValueToLua(value));
            return DynValue.NewTable(values);
        }));
        instance.Set("SetAttribute", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            if (args.Count <= index || args[index].Type != DataType.String || string.IsNullOrWhiteSpace(args[index].String)
                || args[index].String.Length > 256 || !instances.ContainsKey(node.Id)) return DynValue.Nil;
            var name = args[index].String;
            var value = args.Count > index + 1 ? args[index + 1] : DynValue.Nil;
            if (value.Type == DataType.Nil)
            {
                if (node.Attributes.Remove(name)) FireAttributeChanged(node, name);
                return DynValue.Nil;
            }
            if (!TryReadAttributeValue(value, out var next)) return DynValue.Nil;
            if (node.Attributes.TryGetValue(name, out var previous) && AttributeValuesEqual(previous, next)) return DynValue.Nil;
            node.Attributes[name] = next;
            FireAttributeChanged(node, name);
            return DynValue.Nil;
        }));
        instance.Set("GetAttributeChangedSignal", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index && args[index].Type == DataType.String && !string.IsNullOrWhiteSpace(args[index].String)
                ? DynValue.NewTable(GetAttributeSignal(node, args[index].String)) : DynValue.Nil;
        }));
    }

    private Table GetAttributeSignal(SceneNode node, string? attribute)
    {
        var key = (node.Id, attribute);
        if (attributeSignals.TryGetValue(key, out var existing)) return existing;
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                if (attributeCallbacks.TryGetValue(key, out var current)) current.Remove(connection);
                return DynValue.Nil;
            }));
            if (!attributeCallbacks.TryGetValue(key, out var callbacks)) attributeCallbacks[key] = callbacks = [];
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        attributeSignals[key] = signal;
        return signal;
    }

    private void FireAttributeChanged(SceneNode node, string name)
    {
        foreach (var (key, arguments) in new[]
        {
            ((node.Id, (string?)null), new[] { DynValue.NewString(name) }),
            ((node.Id, (string?)name), Array.Empty<DynValue>())
        })
        {
            if (!attributeCallbacks.TryGetValue(key, out var callbacks)) continue;
            foreach (var connection in callbacks.ToArray())
                if (connection.Connected)
                    scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), arguments, 0, true));
        }
    }

    private Table CreateCollectionService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("CollectionService"));
        service.Set("Name", DynValue.NewString("CollectionService"));
        service.Set("ClassName", DynValue.NewString("CollectionService"));
        int ArgumentIndex(CallbackArguments args) => args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
        service.Set("AddTag", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count > index + 1 && ReadNodeArgument(args[index]) is { } node && args[index + 1].Type == DataType.String)
                AddTag(node, args[index + 1].String);
            return DynValue.Nil;
        }));
        service.Set("RemoveTag", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count > index + 1 && ReadNodeArgument(args[index]) is { } node && args[index + 1].Type == DataType.String)
                RemoveTag(node, args[index + 1].String);
            return DynValue.Nil;
        }));
        service.Set("HasTag", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            return DynValue.NewBoolean(args.Count > index + 1 && ReadNodeArgument(args[index]) is { } node
                && args[index + 1].Type == DataType.String && node.Tags.Contains(args[index + 1].String, StringComparer.Ordinal));
        }));
        service.Set("GetTags", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            return TagArray(args.Count > index ? ReadNodeArgument(args[index])?.Tags : null);
        }));
        service.Set("GetTagged", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            var values = new Table(script);
            if (args.Count > index && args[index].Type == DataType.String)
            {
                var item = 1;
                foreach (var node in GameProjectStore.Flatten(project).Where(node => node.Tags.Contains(args[index].String, StringComparer.Ordinal)))
                    if (instances.TryGetValue(node.Id, out var instance)) values.Set(item++, DynValue.NewTable(instance));
            }
            return DynValue.NewTable(values);
        }));
        service.Set("GetAllTags", DynValue.NewCallback((_, _) => TagArray(GameProjectStore.Flatten(project)
            .SelectMany(node => node.Tags).Distinct(StringComparer.Ordinal).OrderBy(tag => tag, StringComparer.Ordinal).ToArray())));
        service.Set("GetInstanceAddedSignal", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            return args.Count > index && args[index].Type == DataType.String
                ? DynValue.NewTable(GetTagSignal(args[index].String, true)) : DynValue.Nil;
        }));
        service.Set("GetInstanceRemovedSignal", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            return args.Count > index && args[index].Type == DataType.String
                ? DynValue.NewTable(GetTagSignal(args[index].String, false)) : DynValue.Nil;
        }));
        return service;
    }

    private Table CreateHttpService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("HttpService"));
        service.Set("Name", DynValue.NewString("HttpService"));
        service.Set("ClassName", DynValue.NewString("HttpService"));
        service.Set("IsA", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
            var className = args.Count > index ? args[index].CastToString() : null;
            return DynValue.NewBoolean(className is "Instance" or "HttpService");
        }));
        int ArgumentIndex(CallbackArguments args) => args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
        service.Set("GenerateGUID", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            var wrap = args.Count > index && args[index].Type == DataType.Boolean && args[index].Boolean;
            var guid = Guid.NewGuid().ToString("D");
            return DynValue.NewString(wrap ? $"{{{guid}}}" : guid);
        }));
        service.Set("UrlEncode", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count <= index || args[index].Type != DataType.String)
                throw new ScriptRuntimeException("HttpService.UrlEncode expects a string.");
            return DynValue.NewString(Uri.EscapeDataString(args[index].String));
        }));
        service.Set("UrlDecode", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count <= index || args[index].Type != DataType.String)
                throw new ScriptRuntimeException("HttpService.UrlDecode expects a string.");
            return DynValue.NewString(Uri.UnescapeDataString(args[index].String.Replace("+", " ", StringComparison.Ordinal)));
        }));
        service.Set("JSONEncode", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count <= index) throw new ScriptRuntimeException("HttpService.JSONEncode expects a value.");
            try
            {
                var node = ToJsonNode(args[index], new HashSet<Table>(ReferenceEqualityComparer.Instance), 0);
                return DynValue.NewString(JsonSerializer.Serialize(node));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
            {
                throw new ScriptRuntimeException($"HttpService.JSONEncode failed: {exception.Message}");
            }
        }));
        service.Set("JSONDecode", DynValue.NewCallback((_, args) =>
        {
            var index = ArgumentIndex(args);
            if (args.Count <= index || args[index].Type != DataType.String)
                throw new ScriptRuntimeException("HttpService.JSONDecode expects a JSON string.");
            try
            {
                using var document = JsonDocument.Parse(args[index].String);
                return JsonElementToDynValue(document.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
            {
                throw new ScriptRuntimeException($"HttpService.JSONDecode failed: {exception.Message}");
            }
        }));
        return service;
    }

    private static JsonNode? ToJsonNode(DynValue value, HashSet<Table> ancestors, int depth)
    {
        if (depth > 128) throw new InvalidOperationException("JSON nesting exceeds 128 levels.");
        return value.Type switch
        {
            DataType.Nil or DataType.Void => null,
            DataType.Boolean => JsonValue.Create(value.Boolean),
            DataType.Number when double.IsFinite(value.Number) => JsonValue.Create(value.Number),
            DataType.Number => throw new InvalidOperationException("JSON cannot encode NaN or infinity."),
            DataType.String => JsonValue.Create(value.String),
            DataType.Table => TableToJsonNode(value.Table, ancestors, depth),
            _ => throw new InvalidOperationException($"JSON cannot encode a value of type {value.Type}.")
        };
    }

    private static JsonNode TableToJsonNode(Table table, HashSet<Table> ancestors, int depth)
    {
        if (!ancestors.Add(table)) throw new InvalidOperationException("JSON cannot encode a cyclic table.");
        try
        {
            var pairs = table.Pairs.ToArray();
            if (pairs.Length == 0) return new JsonObject();
            var arrayKeys = pairs.Select(pair => pair.Key.Type == DataType.Number
                && pair.Key.Number >= 1 && pair.Key.Number <= 100000 && pair.Key.Number == Math.Truncate(pair.Key.Number)
                    ? (int)pair.Key.Number : 0).ToArray();
            if (arrayKeys.All(key => key > 0))
            {
                var array = new JsonArray();
                for (var index = 1; index <= arrayKeys.Max(); index++)
                {
                    var pairIndex = Array.IndexOf(arrayKeys, index);
                    array.Add(pairIndex < 0 ? null : ToJsonNode(pairs[pairIndex].Value, ancestors, depth + 1));
                }
                return array;
            }

            if (pairs.Any(pair => pair.Key.Type != DataType.String))
                throw new InvalidOperationException("JSON table keys must be strings or a dense numeric array.");
            var obj = new JsonObject();
            foreach (var pair in pairs)
            {
                var key = pair.Key.String;
                if (obj.ContainsKey(key)) throw new InvalidOperationException($"JSON table contains duplicate key '{key}'.");
                obj[key] = ToJsonNode(pair.Value, ancestors, depth + 1);
            }
            return obj;
        }
        finally
        {
            ancestors.Remove(table);
        }
    }

    private DynValue JsonElementToDynValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DynValue.Nil,
        JsonValueKind.True => DynValue.NewBoolean(true),
        JsonValueKind.False => DynValue.NewBoolean(false),
        JsonValueKind.Number => DynValue.NewNumber(element.GetDouble()),
        JsonValueKind.String => DynValue.NewString(element.GetString() ?? string.Empty),
        JsonValueKind.Array => JsonArrayToTable(element),
        JsonValueKind.Object => JsonObjectToTable(element),
        _ => DynValue.Nil
    };

    private DynValue JsonArrayToTable(JsonElement element)
    {
        var table = new Table(script);
        var index = 1;
        foreach (var item in element.EnumerateArray()) table.Set(index++, JsonElementToDynValue(item));
        return DynValue.NewTable(table);
    }

    private DynValue JsonObjectToTable(JsonElement element)
    {
        var table = new Table(script);
        foreach (var property in element.EnumerateObject()) table.Set(property.Name, JsonElementToDynValue(property.Value));
        return DynValue.NewTable(table);
    }

    private Table CreatePathfindingService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("PathfindingService"));
        service.Set("Name", DynValue.NewString("PathfindingService"));
        service.Set("ClassName", DynValue.NewString("PathfindingService"));
        service.Set("IsA", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
            var className = args.Count > index ? args[index].CastToString() : null;
            return DynValue.NewBoolean(className is "Instance" or "PathfindingService");
        }));
        service.Set("CreatePath", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
            var options = args.Count > index && args[index].Type == DataType.Table ? args[index].Table : null;
            var agentRadius = ReadOptionNumber(options, "AgentRadius", 2, .5f, 32);
            var agentHeight = ReadOptionNumber(options, "AgentHeight", 5, 1, 64);
            var waypointSpacing = ReadOptionNumber(options, "WaypointSpacing", 4, 1, 16);
            var canJumpValue = options?.Get("AgentCanJump") ?? DynValue.Nil;
            var canJump = canJumpValue.Type != DataType.Boolean || canJumpValue.Boolean;
            var traversalCosts = new Dictionary<string, float>(StringComparer.Ordinal);
            var costsValue = options?.Get("Costs") ?? DynValue.Nil;
            if (costsValue.Type == DataType.Table)
            {
                foreach (var pair in costsValue.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Number || !double.IsFinite(pair.Value.Number)) continue;
                    traversalCosts[pair.Key.String] = (float)Math.Clamp(pair.Value.Number, 0, 1000000);
                }
            }

            var path = new Table(script);
            runtimeTypeTags.Add(path, new LuaXTypeTag("Path"));
            path.Set("Name", DynValue.NewString("Path"));
            path.Set("ClassName", DynValue.NewString("Path"));
            path.Set("Status", DynValue.NewString("NoPath"));
            var pathState = new PathRuntimeState(path) { AgentRadius = agentRadius, AgentHeight = agentHeight };
            pathStates.Add(new WeakReference<PathRuntimeState>(pathState));
            path.Set("Blocked", DynValue.NewTable(CreatePathEventSignal(pathState, true)));
            path.Set("Unblocked", DynValue.NewTable(CreatePathEventSignal(pathState, false)));
            path.Set("IsA", DynValue.NewCallback((_, typeArgs) =>
            {
                var typeIndex = typeArgs.Count > 0 && typeArgs[0].Type == DataType.Table && ReferenceEquals(typeArgs[0].Table, path) ? 1 : 0;
                var requestedType = typeArgs.Count > typeIndex ? typeArgs[typeIndex].CastToString() : null;
                return DynValue.NewBoolean(requestedType is "Instance" or "Path");
            }));
            var waypoints = new Table(script);
            var waypointArray = DynValue.NewTable(waypoints);
            path.Set("GetWaypoints", DynValue.NewCallback((_, _) => waypointArray));
            path.Set("ComputeAsync", DynValue.NewCallback((_, computeArgs) =>
            {
                var computeIndex = computeArgs.Count > 0 && computeArgs[0].Type == DataType.Table && ReferenceEquals(computeArgs[0].Table, path) ? 1 : 0;
                if (computeArgs.Count <= computeIndex + 1
                    || !TryReadVector3(computeArgs[computeIndex], out var start)
                    || !TryReadVector3(computeArgs[computeIndex + 1], out var finish))
                    throw new ScriptRuntimeException("Path:ComputeAsync expects start and finish Vector3 positions.");

                var route = FindPath(start, finish, agentRadius, agentHeight, waypointSpacing, canJump, traversalCosts);
                waypoints.Clear();
                pathState.Waypoints = route.Select(waypoint => waypoint.Position).ToList();
                pathState.LinkIds = route.Select(waypoint => waypoint.LinkId).ToList();
                pathState.BlockedWaypoints.Clear();
                for (var waypointIndex = 0; waypointIndex < route.Count; waypointIndex++)
                {
                    var waypoint = new Table(script);
                    runtimeTypeTags.Add(waypoint, new LuaXTypeTag("PathWaypoint"));
                    waypoint.Set("Position", VectorValue([route[waypointIndex].Position.X, route[waypointIndex].Position.Y, route[waypointIndex].Position.Z]));
                    waypoint.Set("Action", DynValue.NewString(route[waypointIndex].Action));
                    waypoint.Set("Label", DynValue.NewString(route[waypointIndex].Label));
                    waypoints.Set(waypointIndex + 1, DynValue.NewTable(waypoint));
                }
                path.Set("Status", DynValue.NewString(route.Count > 0 ? "Success" : "NoPath"));
                return DynValue.Nil;
            }));
            path.Set("CheckOcclusionAsync", DynValue.NewCallback((_, checkArgs) =>
            {
                var checkIndex = checkArgs.Count > 0 && checkArgs[0].Type == DataType.Table && ReferenceEquals(checkArgs[0].Table, path) ? 1 : 0;
                if (checkArgs.Count <= checkIndex || checkArgs[checkIndex].Type != DataType.Number
                    || !double.IsFinite(checkArgs[checkIndex].Number) || checkArgs[checkIndex].Number != Math.Truncate(checkArgs[checkIndex].Number))
                    throw new ScriptRuntimeException("Path:CheckOcclusionAsync expects a waypoint index.");

                var startIndex = (int)checkArgs[checkIndex].Number;
                if (startIndex < 0 || startIndex > pathState.Waypoints.Count)
                    throw new ScriptRuntimeException($"Path:CheckOcclusionAsync index must be between 0 and {pathState.Waypoints.Count}.");

                var parts = Parts.Where(part => part.CanCollide && part.Size.Length >= 3).ToArray();
                var workspaceNodeIds = GameProjectStore.Flatten(project.Workspace.Children).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                return DynValue.NewNumber(FindFirstBlockedPathWaypoint(pathState, startIndex, parts, workspaceNodeIds));
            }));
            path.Set("Destroy", DynValue.NewCallback((_, _) =>
            {
                waypoints.Clear();
                path.Set("Status", DynValue.NewString("NoPath"));
                foreach (var connection in pathState.BlockedCallbacks.Concat(pathState.UnblockedCallbacks)) connection.Disconnect();
                pathState.BlockedCallbacks.Clear();
                pathState.UnblockedCallbacks.Clear();
                pathState.Waypoints.Clear();
                pathState.LinkIds.Clear();
                pathState.BlockedWaypoints.Clear();
                pathState.Destroyed = true;
                return DynValue.Nil;
            }));
            return DynValue.NewTable(path);
        }));
        return service;

        static float ReadOptionNumber(Table? options, string name, float fallback, float minimum, float maximum)
        {
            var value = options?.Get(name) ?? DynValue.Nil;
            return value.Type == DataType.Number && double.IsFinite(value.Number)
                ? Math.Clamp((float)value.Number, minimum, maximum)
                : fallback;
        }
    }

    private Table CreatePathEventSignal(PathRuntimeState state, bool blocked)
    {
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            var callbacks = blocked ? state.BlockedCallbacks : state.UnblockedCallbacks;
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                callbacks.Remove(connection);
                return DynValue.Nil;
            }));
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        return signal;
    }

    private void UpdatePathStates(float elapsed)
    {
        if (pathStates.Count == 0) return;
        foreach (var weakState in pathStates.ToArray())
        {
            if (!weakState.TryGetTarget(out var state) || state.Destroyed)
            {
                pathStates.Remove(weakState);
                continue;
            }
            if (state.Waypoints.Count == 0 || state.Path.Get("Status").CastToString() != "Success"
                || state.BlockedCallbacks.Count == 0 && state.UnblockedCallbacks.Count == 0) continue;
            state.CheckElapsed += elapsed;
            if (state.CheckElapsed < .25f) continue;
            state.CheckElapsed %= .25f;
            var parts = Parts.Where(part => part.CanCollide && part.Size.Length >= 3).ToArray();
            var workspaceNodeIds = GameProjectStore.Flatten(project.Workspace.Children).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            var currentBlocked = FindBlockedPathWaypoints(state, parts, workspaceNodeIds);
            foreach (var waypointIndex in currentBlocked.Except(state.BlockedWaypoints))
                FirePathEvent(state.BlockedCallbacks, waypointIndex);
            foreach (var waypointIndex in state.BlockedWaypoints.Except(currentBlocked))
                FirePathEvent(state.UnblockedCallbacks, waypointIndex);
            state.BlockedWaypoints.Clear();
            state.BlockedWaypoints.UnionWith(currentBlocked);
        }
    }

    private int FindFirstBlockedPathWaypoint(PathRuntimeState state, int startIndex, SceneNode[] parts, HashSet<string> workspaceNodeIds)
    {
        for (var waypointIndex = Math.Max(startIndex - 1, 0); waypointIndex < state.Waypoints.Count; waypointIndex++)
        {
            var point = state.Waypoints[waypointIndex];
            var missingLink = waypointIndex < state.LinkIds.Count && state.LinkIds[waypointIndex].Length > 0
                && (!workspaceNodeIds.Contains(state.LinkIds[waypointIndex]) || FindNode(state.LinkIds[waypointIndex])?.Type != "PathfindingLink");
            if (missingLink || !TryFindWalkableGround(point.X, point.Z, point.Y, parts, state.AgentRadius, state.AgentHeight,
                    Math.Max(state.AgentHeight + 1, 2), 2, out _, out _))
                return waypointIndex + 1;
        }
        return -1;
    }

    private HashSet<int> FindBlockedPathWaypoints(PathRuntimeState state, SceneNode[] parts, HashSet<string> workspaceNodeIds)
    {
        var blocked = new HashSet<int>();
        var next = 0;
        while ((next = FindFirstBlockedPathWaypoint(state, next + 1, parts, workspaceNodeIds)) > 0)
        {
            blocked.Add(next);
        }
        return blocked;
    }

    private void FirePathEvent(IEnumerable<BindableConnection> callbacks, int waypointIndex)
    {
        foreach (var connection in callbacks.ToArray())
        {
            if (!connection.Connected) continue;
            scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), [DynValue.NewNumber(waypointIndex)], 0, true));
        }
    }

    private List<(Vector3 Position, string Action, string Label, string LinkId)> FindPath(Vector3 start, Vector3 finish, float agentRadius, float agentHeight, float waypointSpacing, bool canJump, IReadOnlyDictionary<string, float> traversalCosts)
    {
        var workspaceNodes = GameProjectStore.Flatten(project.Workspace.Children).ToArray();
        var workspaceNodeIds = workspaceNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var parts = workspaceNodes.Where(part => IsBasePart(part) && part.CanCollide && part.Size.Length >= 3).ToArray();
        if (parts.Length == 0 || !IsFinite(start) || !IsFinite(finish)) return [];

        var spacing = Math.Clamp(waypointSpacing, Math.Min(16, Math.Max(1, agentRadius)), 16);
        var stepHeight = canJump ? 7 : 1.5f;
        var marginCells = Math.Max(4, (int)Math.Ceiling((agentRadius * 4 + spacing * 2) / spacing));
        var goalCell = new PathGridCell((int)MathF.Round((finish.X - start.X) / spacing), (int)MathF.Round((finish.Z - start.Z) / spacing));
        var minX = Math.Min(0, goalCell.X) - marginCells;
        var maxX = Math.Max(0, goalCell.X) + marginCells;
        var minZ = Math.Min(0, goalCell.Z) - marginCells;
        var maxZ = Math.Max(0, goalCell.Z) + marginCells;
        var gridWidth = maxX - minX + 1;
        var gridDepth = maxZ - minZ + 1;
        if ((long)gridWidth * gridDepth > 40000) return [];

        var originX = start.X;
        var originZ = start.Z;
        Vector3 WorldPosition(PathGridCell cell, float y) => new(originX + cell.X * spacing, y, originZ + cell.Z * spacing);
        Vector3 AttachmentWorldPosition(SceneNode attachment)
        {
            var parent = FindParentNode(attachment);
            if (parent is null || !IsBasePart(parent)) return new Vector3(float.NaN);
            return new Vector3(parent.Position[0], parent.Position[1], parent.Position[2])
                + Vector3.Transform(new Vector3(attachment.Position[0], attachment.Position[1], attachment.Position[2]), RotationMath.FromDegrees(parent.Rotation));
        }
        var linkEdges = new Dictionary<PathGridCell, List<(PathGridCell Destination, string Label, float ReferenceY, string LinkId)>>();
        foreach (var link in workspaceNodes.Where(node => node.Type == "PathfindingLink"))
        {
            if (FindNode(link.Attachment0) is not { Type: "Attachment" } attachment0 || FindNode(link.Attachment1) is not { Type: "Attachment" } attachment1) continue;
            if (!workspaceNodeIds.Contains(attachment0.Id) || !workspaceNodeIds.Contains(attachment1.Id)) continue;
            var from = AttachmentWorldPosition(attachment0);
            var to = AttachmentWorldPosition(attachment1);
            if (!IsFinite(from) || !IsFinite(to)) continue;
            var fromCell = new PathGridCell((int)MathF.Round((from.X - originX) / spacing), (int)MathF.Round((from.Z - originZ) / spacing));
            var toCell = new PathGridCell((int)MathF.Round((to.X - originX) / spacing), (int)MathF.Round((to.Z - originZ) / spacing));
            if (fromCell.X < minX || fromCell.X > maxX || fromCell.Z < minZ || fromCell.Z > maxZ || toCell.X < minX || toCell.X > maxX || toCell.Z < minZ || toCell.Z > maxZ || fromCell == toCell) continue;
            if (!linkEdges.TryGetValue(fromCell, out var forward)) linkEdges[fromCell] = forward = [];
            forward.Add((toCell, link.PathfindingLinkLabel, to.Y, link.Id));
            if (link.PathfindingLinkBidirectional)
            {
                if (!linkEdges.TryGetValue(toCell, out var backward)) linkEdges[toCell] = backward = [];
                backward.Add((fromCell, link.PathfindingLinkLabel, from.Y, link.Id));
            }
        }
        var groundCache = new Dictionary<(PathGridCell Cell, int HeightBand), float?>();
        var groundPartCache = new Dictionary<(PathGridCell Cell, int HeightBand), SceneNode?>();
        bool TryGround(PathGridCell cell, float referenceY, out float groundY)
        {
            var key = (cell, (int)MathF.Round(referenceY * 2));
            if (!groundCache.TryGetValue(key, out var cached))
            {
                var world = WorldPosition(cell, referenceY);
                cached = TryFindWalkableGround(world.X, world.Z, referenceY, parts, agentRadius, agentHeight, stepHeight, stepHeight, out var foundY, out var foundPart) ? foundY : null;
                groundPartCache[key] = cached is null ? null : foundPart;
                groundCache[key] = cached;
            }
            groundY = cached ?? 0;
            return cached is not null;
        }

        if (!TryFindWalkableGround(start.X, start.Z, start.Y, parts, agentRadius, agentHeight, stepHeight + agentHeight, 1.5f, out var startY, out _)) return [];
        var startCell = new PathGridCell(0, 0);
        var frontier = new PriorityQueue<(PathGridCell Cell, float Cost), float>();
        var previous = new Dictionary<PathGridCell, PathGridCell>();
        var previousActions = new Dictionary<PathGridCell, (string Action, string Label, string LinkId)>();
        var heights = new Dictionary<PathGridCell, float> { [startCell] = startY };
        var costs = new Dictionary<PathGridCell, float> { [startCell] = 0 };
        var minimumTraversalCost = Math.Min(1, traversalCosts.Count == 0 ? 1 : traversalCosts.Values.Min());
        frontier.Enqueue((startCell, 0), Vector2.Distance(Vector2.Zero, new Vector2(goalCell.X, goalCell.Z)) * minimumTraversalCost);
        var directions = new (int X, int Z)[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        var found = false;

        while (frontier.TryDequeue(out var entry, out _))
        {
            var current = entry.Cell;
            if (!costs.TryGetValue(current, out var currentCost) || entry.Cost > currentCost + .0001f) continue;
            if (current == goalCell) { found = true; break; }
            foreach (var direction in directions)
            {
                var next = new PathGridCell(current.X + direction.X, current.Z + direction.Z);
                if (next.X < minX || next.X > maxX || next.Z < minZ || next.Z > maxZ) continue;
                if (direction.X != 0 && direction.Z != 0
                    && (!TryGround(new PathGridCell(current.X + direction.X, current.Z), heights[current], out _)
                        || !TryGround(new PathGridCell(current.X, current.Z + direction.Z), heights[current], out _))) continue;
                if (!TryGround(next, heights[current], out var nextY)) continue;

                var groundKey = (next, (int)MathF.Round(heights[current] * 2));
                var label = groundPartCache.GetValueOrDefault(groundKey)?.Children.FirstOrDefault(child => child.Type == "PathfindingModifier")?.PathfindingModifierLabel;
                var terrainCost = !string.IsNullOrEmpty(label) && traversalCosts.TryGetValue(label, out var configuredCost) ? configuredCost : 1;
                if (terrainCost >= 1000000) continue;
                var nextCost = currentCost + (direction.X != 0 && direction.Z != 0 ? 1.4142f : 1) * terrainCost + Math.Abs(nextY - heights[current]) * .15f;
                if (costs.TryGetValue(next, out var oldCost) && nextCost >= oldCost) continue;
                costs[next] = nextCost;
                previous[next] = current;
                previousActions[next] = (nextY > heights[current] + 1.5f ? "Jump" : "Walk", string.Empty, string.Empty);
                heights[next] = nextY;
                var heuristic = Vector2.Distance(new Vector2(next.X, next.Z), new Vector2(goalCell.X, goalCell.Z)) * minimumTraversalCost;
                frontier.Enqueue((next, nextCost), nextCost + heuristic);
            }
            if (linkEdges.TryGetValue(current, out var currentLinks))
            {
                foreach (var edge in currentLinks)
                {
                    var next = edge.Destination;
                    var world = WorldPosition(next, edge.ReferenceY);
                    var linkGroundRange = Math.Max(agentHeight, stepHeight) + 1;
                    if (!TryFindWalkableGround(world.X, world.Z, edge.ReferenceY, parts, agentRadius, agentHeight, linkGroundRange, linkGroundRange, out var nextY, out _)) continue;
                    var linkCost = !string.IsNullOrEmpty(edge.Label) && traversalCosts.TryGetValue(edge.Label, out var configuredLinkCost) ? configuredLinkCost : 1;
                    if (linkCost >= 1000000) continue;
                    var nextCost = currentCost + Math.Max(1, Math.Abs(next.X - current.X) + Math.Abs(next.Z - current.Z)) * linkCost + Math.Abs(nextY - heights[current]) * .15f;
                    if (costs.TryGetValue(next, out var oldCost) && nextCost >= oldCost) continue;
                    costs[next] = nextCost;
                    previous[next] = current;
                    previousActions[next] = ("Custom", edge.Label, edge.LinkId);
                    heights[next] = nextY;
                    var heuristic = Vector2.Distance(new Vector2(next.X, next.Z), new Vector2(goalCell.X, goalCell.Z)) * minimumTraversalCost;
                    frontier.Enqueue((next, nextCost), nextCost + heuristic);
                }
            }
        }
        if (!found) return [];

        var cells = new List<PathGridCell> { goalCell };
        while (cells[^1] != startCell)
        {
            if (!previous.TryGetValue(cells[^1], out var parent)) return [];
            cells.Add(parent);
        }
        cells.Reverse();
        var route = new List<(Vector3 Position, string Action, string Label, string LinkId)>(cells.Count);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var position = WorldPosition(cell, heights[cell]);
            var action = index > 0 && previousActions.TryGetValue(cell, out var edgeAction)
                ? edgeAction.Action
                : "Walk";
            var label = index > 0 && previousActions.TryGetValue(cell, out edgeAction) ? edgeAction.Label : string.Empty;
            var linkId = index > 0 && previousActions.TryGetValue(cell, out edgeAction) ? edgeAction.LinkId : string.Empty;
            route.Add((position, action, label, linkId));
        }
        if (!TryFindWalkableGround(finish.X, finish.Z, route[^1].Position.Y, parts, agentRadius, agentHeight, stepHeight, stepHeight, out var finishY, out _)) return [];
        var finishPosition = new Vector3(finish.X, finishY, finish.Z);
        if (Vector3.Distance(route[^1].Position, finishPosition) > .1f)
            route.Add((finishPosition, finishY > route[^1].Position.Y + 1.5f ? "Jump" : "Walk", string.Empty, string.Empty));
        return route;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool TryFindWalkableGround(float x, float z, float referenceY, IReadOnlyList<SceneNode> parts, float agentRadius, float agentHeight, float verticalRange, float maxRise, out float groundY, out SceneNode? groundPart)
    {
        var candidates = new List<(float Height, SceneNode Part)>();
        foreach (var part in parts)
        {
            if (CollisionGeometry.TopYAt(part, x, z) is not { } topY || !float.IsFinite(topY)
                || topY > referenceY + maxRise || topY < referenceY - verticalRange) continue;
            candidates.Add((topY, part));
        }
        candidates.Sort((first, second) => second.Height.CompareTo(first.Height));
        var probe = new SceneNode { Type = "Part", Size = [Math.Max(.5f, agentRadius * 2), Math.Max(1, agentHeight), Math.Max(.5f, agentRadius * 2)] };
        foreach (var candidate in candidates)
        {
            probe.Position = [x, candidate.Height + Math.Max(1, agentHeight) * .5f, z];
            var blocked = parts.Any(part => !ReferenceEquals(part, candidate.Part)
                && !part.Children.Any(child => child.Type == "PathfindingModifier" && child.PathfindingPassThrough)
                && CollisionGeometry.Intersects(probe, part));
            if (blocked) continue;
            groundY = candidate.Height;
            groundPart = candidate.Part;
            return true;
        }
        groundY = 0;
        groundPart = null;
        return false;
    }

    private Table CreateRunService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("RunService"));
        service.Set("Name", DynValue.NewString("RunService"));
        service.Set("ClassName", DynValue.NewString("RunService"));
        service.Set("IsRunning", DynValue.NewCallback((_, _) => DynValue.NewBoolean(true)));
        service.Set("IsEdit", DynValue.NewCallback((_, _) => DynValue.NewBoolean(false)));
        service.Set("IsStudio", DynValue.NewCallback((_, _) => DynValue.NewBoolean(isStudio)));
        foreach (var eventName in new[] { "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped" })
        {
            var signal = new Table(script);
            runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
            signal.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
                if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
                var connectionTable = new Table(script);
                runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
                var connection = new BindableConnection(owner, args[index], connectionTable);
                connectionTable.Set("Connected", DynValue.NewBoolean(true));
                connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
                {
                    connection.Disconnect();
                    if (runServiceCallbacks.TryGetValue(eventName, out var current)) current.Remove(connection);
                    return DynValue.Nil;
                }));
                if (!runServiceCallbacks.TryGetValue(eventName, out var callbacks)) runServiceCallbacks[eventName] = callbacks = [];
                callbacks.Add(connection);
                return DynValue.NewTable(connectionTable);
            }));
            service.Set(eventName, DynValue.NewTable(signal));
        }
        return service;
    }

    private Table CreateUserInputService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("UserInputService"));
        service.Set("Name", DynValue.NewString("UserInputService"));
        service.Set("ClassName", DynValue.NewString("UserInputService"));
        service.Set("InputBegan", DynValue.NewTable(CreateUserInputSignal("InputBegan")));
        service.Set("InputEnded", DynValue.NewTable(CreateUserInputSignal("InputEnded")));
        service.Set("InputChanged", DynValue.NewTable(CreateUserInputSignal("InputChanged")));
        service.Set("MouseEnabled", DynValue.NewBoolean(true));
        service.Set("KeyboardEnabled", DynValue.NewBoolean(true));
        service.Set("TouchEnabled", DynValue.NewBoolean(false));
        service.Set("GamepadEnabled", DynValue.NewBoolean(false));
        service.Set("GetMouseLocation", DynValue.NewCallback((_, _) => Vector2Value(mouseLocation)));
        service.Set("IsKeyDown", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;
            var keyCode = args.Count > index ? args[index].CastToString() : null;
            return DynValue.NewBoolean(keyCode is not null && pressedKeyCodes.Contains(keyCode));
        }));
        service.Set("GetKeysPressed", DynValue.NewCallback((_, _) =>
        {
            var keys = new Table(script);
            var index = 1;
            foreach (var keyCode in pressedKeyCodes.OrderBy(code => code, StringComparer.Ordinal))
                keys.Set(index++, DynValue.NewTable(CreateInputObject(keyCode, "Begin")));
            return DynValue.NewTable(keys);
        }));
        return service;
    }

    private Table CreateContextActionService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("ContextActionService"));
        service.Set("Name", DynValue.NewString("ContextActionService"));
        service.Set("ClassName", DynValue.NewString("ContextActionService"));
        service.Set("BindAction", DynValue.NewCallback((_, args) => BindContextAction(service, args, false)));
        service.Set("BindActionAtPriority", DynValue.NewCallback((_, args) => BindContextAction(service, args, true)));
        service.Set("UnbindAction", DynValue.NewCallback((_, args) =>
        {
            var index = GetServiceMethodArgumentIndex(service, args);
            if (args.Count > index && args[index].CastToString() is { Length: > 0 } actionName) contextActionBindings.Remove(actionName);
            return DynValue.Nil;
        }));
        service.Set("UnbindAllActions", DynValue.NewCallback((_, _) =>
        {
            contextActionBindings.Clear();
            return DynValue.Nil;
        }));
        service.Set("GetBoundActionInfo", DynValue.NewCallback((_, args) =>
        {
            var index = GetServiceMethodArgumentIndex(service, args);
            return args.Count > index && args[index].CastToString() is { } actionName && contextActionBindings.TryGetValue(actionName, out var binding)
                ? DynValue.NewTable(CreateBoundActionInfo(binding)) : DynValue.Nil;
        }));
        service.Set("GetAllBoundActionInfo", DynValue.NewCallback((_, _) =>
        {
            var result = new Table(script);
            foreach (var binding in contextActionBindings.Values) result.Set(binding.Name, DynValue.NewTable(CreateBoundActionInfo(binding)));
            return DynValue.NewTable(result);
        }));
        return service;
    }

    private int GetServiceMethodArgumentIndex(Table service, CallbackArguments args) =>
        args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, service) ? 1 : 0;

    private DynValue BindContextAction(Table service, CallbackArguments args, bool hasPriority)
    {
        var index = GetServiceMethodArgumentIndex(service, args);
        var priorityIndex = index + 3;
        var firstInputIndex = hasPriority ? index + 4 : index + 3;
        if (args.Count <= index + 2 || args[index].CastToString() is not { Length: > 0 } actionName || args[index + 1].Type != DataType.Function
            || currentGameScript is not { } owner) return DynValue.Nil;
        var priority = hasPriority && args.Count > priorityIndex && args[priorityIndex].Type == DataType.Number && double.IsFinite(args[priorityIndex].Number)
            ? (int)Math.Clamp(Math.Truncate(args[priorityIndex].Number), int.MinValue, int.MaxValue) : 2000;
        var inputs = Enumerable.Range(firstInputIndex, Math.Max(0, args.Count - firstInputIndex))
            .Select(inputIndex => args[inputIndex].CastToString()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        contextActionBindings[actionName] = new ContextActionBinding(actionName, owner, args[index + 1], inputs, priority, args[index + 2].CastToBool());
        return DynValue.Nil;
    }

    private Table CreateBoundActionInfo(ContextActionBinding binding)
    {
        var info = new Table(script);
        runtimeTypeTags.Add(info, new LuaXTypeTag("BoundActionInfo"));
        info.Set("Name", DynValue.NewString(binding.Name));
        info.Set("PriorityLevel", DynValue.NewNumber(binding.Priority));
        info.Set("CreateTouchButton", DynValue.NewBoolean(binding.CreateTouchButton));
        var inputs = new Table(script);
        for (var index = 0; index < binding.Inputs.Length; index++) inputs.Set(index + 1, DynValue.NewString(binding.Inputs[index]));
        info.Set("InputTypes", DynValue.NewTable(inputs));
        return info;
    }

    private Table CreateUserInputSignal(string eventName)
    {
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                if (userInputCallbacks.TryGetValue(eventName, out var current)) current.Remove(connection);
                return DynValue.Nil;
            }));
            if (!userInputCallbacks.TryGetValue(eventName, out var callbacks)) userInputCallbacks[eventName] = callbacks = [];
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        return signal;
    }

    private Table CreateInputObject(string keyCode, string state, string inputType = "Keyboard", float[]? position = null, float[]? delta = null)
    {
        var input = new Table(script);
        runtimeTypeTags.Add(input, new LuaXTypeTag("InputObject"));
        input.Set("KeyCode", DynValue.NewString(keyCode));
        input.Set("UserInputType", DynValue.NewString(inputType));
        input.Set("UserInputState", DynValue.NewString(state));
        input.Set("Position", VectorValue(position is { Length: >= 3 } ? position : inputType == "Keyboard" ? [0, 0, 0] : [mouseLocation[0], mouseLocation[1], 0]));
        input.Set("Delta", VectorValue(delta is { Length: >= 3 } ? delta : [0, 0, 0]));
        return input;
    }

    public bool FireUserInput(string keyCode, bool isDown, bool gameProcessed, bool isRepeat = false, string inputType = "Keyboard", float? mouseX = null, float? mouseY = null)
    {
        if (inputType != "Keyboard" && mouseX is { } x && mouseY is { } y && float.IsFinite(x) && float.IsFinite(y)) mouseLocation = [x, y];
        if (inputType == "Keyboard")
        {
            if (!SupportedKeyCodes.Contains(keyCode, StringComparer.Ordinal)) keyCode = "Unknown";
            var wasDown = pressedKeyCodes.Contains(keyCode);
            if (isDown) pressedKeyCodes.Add(keyCode);
            else pressedKeyCodes.Remove(keyCode);
            if (isRepeat || isDown == wasDown) return false;
        }
        var eventName = isDown ? "InputBegan" : "InputEnded";
        return QueueUserInputEvent(eventName, CreateInputObject(keyCode, isDown ? "Begin" : "End", inputType), gameProcessed);
    }

    public bool FireMouseMovement(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        var delta = new[] { x - mouseLocation[0], y - mouseLocation[1], 0f };
        if (delta[0] == 0 && delta[1] == 0) return false;
        mouseLocation = [x, y];
        return QueueUserInputEvent("InputChanged", CreateInputObject("Unknown", "Change", "MouseMovement", [x, y, 0], delta), false);
    }

    private bool QueueUserInputEvent(string eventName, Table input, bool gameProcessed)
    {
        if (userInputCallbacks.TryGetValue(eventName, out var listeners))
        {
            var arguments = new DynValue[] { DynValue.NewTable(input), DynValue.NewBoolean(gameProcessed) };
            foreach (var connection in listeners.ToArray())
            {
                if (!connection.Connected || !IsTaskOwnerActive(connection.Owner)) continue;
                scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), arguments, 0, true));
            }
        }
        var keyCode = input.Get("KeyCode").CastToString() ?? "Unknown";
        var inputType = input.Get("UserInputType").CastToString() ?? "Unknown";
        var inputState = DynValue.NewString(input.Get("UserInputState").CastToString() ?? "Begin");
        if (gameProcessed) return false;
        foreach (var binding in contextActionBindings.Values.OrderByDescending(binding => binding.Priority).ToArray())
        {
            if (!IsTaskOwnerActive(binding.Owner) || !binding.Inputs.Contains(keyCode, StringComparer.Ordinal) && !binding.Inputs.Contains(inputType, StringComparer.Ordinal)) continue;
            var arguments = new DynValue[] { DynValue.NewString(binding.Name), inputState, DynValue.NewTable(input) };
            if (RunContextAction(binding, arguments)) return true;
        }
        return false;
    }

    private bool RunContextAction(ContextActionBinding binding, DynValue[] arguments)
    {
        var previousScript = currentGameScript;
        BindCurrentScript(binding.Owner);
        try
        {
            var coroutine = script.CreateCoroutine(binding.Callback).Coroutine;
            var result = coroutine.Resume(arguments);
            SynchronizeInstances();
            if (coroutine.State != CoroutineState.Dead && IsTaskOwnerActive(binding.Owner))
                activeScripts.Add(new ScriptExecution(binding.Owner, coroutine, false, $"ContextActionService.{binding.Name}"));
            return coroutine.State == CoroutineState.Dead && result.CastToString() == "Sink";
        }
        catch (Exception exception)
        {
            output?.Invoke($"{binding.Owner.Name} ContextActionService.{binding.Name} callback: {exception.Message}");
            SynchronizeInstances();
            return false;
        }
        finally { currentGameScript = previousScript; }
    }

    public void ReleaseAllPressedKeys()
    {
        foreach (var keyCode in pressedKeyCodes.ToArray()) FireUserInput(keyCode, false, false);
    }

    private Table CreateTweenService()
    {
        var service = new Table(script);
        runtimeTypeTags.Add(service, new LuaXTypeTag("TweenService"));
        service.Set("Name", DynValue.NewString("TweenService"));
        service.Set("ClassName", DynValue.NewString("TweenService"));
        service.Set("Create", DynValue.NewCallback((_, args) => CreateTween(args)));
        return service;
    }

    private DynValue CreateTweenInfo(CallbackArguments args)
    {
        var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
        var time = args.Count > index && args[index].Type == DataType.Number && double.IsFinite(args[index].Number)
            ? (float)Math.Clamp(args[index].Number, 0, 86400) : 1f;
        var style = args.Count > index + 1 ? args[index + 1].CastToString() ?? "Quad" : "Quad";
        var direction = args.Count > index + 2 ? args[index + 2].CastToString() ?? "Out" : "Out";
        var repeatCount = args.Count > index + 3 && args[index + 3].Type == DataType.Number && double.IsFinite(args[index + 3].Number)
            ? (int)Math.Clamp(Math.Truncate(args[index + 3].Number), -1, 10000) : 0;
        var reverses = args.Count > index + 4 && args[index + 4].CastToBool();
        var delay = args.Count > index + 5 && args[index + 5].Type == DataType.Number && double.IsFinite(args[index + 5].Number)
            ? (float)Math.Clamp(args[index + 5].Number, 0, 86400) : 0;
        var info = new Table(script);
        runtimeTypeTags.Add(info, new LuaXTypeTag("TweenInfo"));
        info.Set("Time", DynValue.NewNumber(time));
        info.Set("EasingStyle", DynValue.NewString(style));
        info.Set("EasingDirection", DynValue.NewString(direction));
        info.Set("RepeatCount", DynValue.NewNumber(repeatCount));
        info.Set("Reverses", DynValue.NewBoolean(reverses));
        info.Set("DelayTime", DynValue.NewNumber(delay));
        info.ReadOnly = true;
        return DynValue.NewTable(info);
    }

    private DynValue CreateTween(CallbackArguments args)
    {
        var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, tweenService) ? 1 : 0;
        if (args.Count <= index + 2 || currentGameScript is not { } owner
            || ReadNodeArgument(args[index]) is not { } target
            || !IsTypedValue(args[index + 1], "TweenInfo") || args[index + 2].Type != DataType.Table) return DynValue.Nil;
        if (!instances.TryGetValue(target.Id, out var targetInstance)) return DynValue.Nil;
        var infoTable = args[index + 1].Table;
        var info = new TweenInfoData(
            ReadFiniteNumber(infoTable.Get("Time"), 1, 0, 86400),
            infoTable.Get("EasingStyle").CastToString() ?? "Quad",
            infoTable.Get("EasingDirection").CastToString() ?? "Out",
            (int)ReadFiniteNumber(infoTable.Get("RepeatCount"), 0, -1, 10000),
            infoTable.Get("Reverses").CastToBool(),
            ReadFiniteNumber(infoTable.Get("DelayTime"), 0, 0, 86400));
        var goals = new Dictionary<string, DynValue>(StringComparer.Ordinal);
        foreach (var pair in args[index + 2].Table.Pairs)
        {
            if (pair.Key.Type != DataType.String || pair.Value.Type == DataType.Nil) continue;
            var property = pair.Key.String;
            var start = GetPropertyValue(target, property);
            if (CanInterpolateTweenValues(start, pair.Value)) goals[property] = pair.Value;
        }
        if (goals.Count == 0) return DynValue.Nil;

        var tween = new Table(script);
        runtimeTypeTags.Add(tween, new LuaXTypeTag("Tween"));
        var state = new TweenState(target, info, goals, owner, tween);
        tween.Set("Name", DynValue.NewString("Tween"));
        tween.Set("ClassName", DynValue.NewString("Tween"));
        tween.Set("Instance", DynValue.NewTable(targetInstance));
        tween.Set("PlaybackState", DynValue.NewString(state.PlaybackState));
        var completed = new Table(script);
        runtimeTypeTags.Add(completed, new LuaXTypeTag("RBXScriptSignal"));
        completed.Set("Connect", DynValue.NewCallback((_, callbackArgs) =>
        {
            var callbackIndex = callbackArgs.Count > 0 && callbackArgs[0].Type == DataType.Table && ReferenceEquals(callbackArgs[0].Table, completed) ? 1 : 0;
            if (currentGameScript is not { } callbackOwner || callbackArgs.Count <= callbackIndex || callbackArgs[callbackIndex].Type != DataType.Function) return DynValue.Nil;
            state.CompletedCallbacks.Add(new ScriptCallback(callbackOwner, callbackArgs[callbackIndex], "Tween.Completed"));
            return DynValue.Nil;
        }));
        tween.Set("Completed", DynValue.NewTable(completed));
        tween.Set("Play", DynValue.NewCallback((_, _) => { PlayTween(state); return DynValue.Nil; }));
        tween.Set("Pause", DynValue.NewCallback((_, _) => { PauseTween(state); return DynValue.Nil; }));
        tween.Set("Cancel", DynValue.NewCallback((_, _) => { CancelTween(state); return DynValue.Nil; }));
        activeTweens.Add(state);
        return DynValue.NewTable(tween);
    }

    private static float ReadFiniteNumber(DynValue value, float fallback, float minimum, float maximum) =>
        value.Type == DataType.Number && double.IsFinite(value.Number) ? (float)Math.Clamp(value.Number, minimum, maximum) : fallback;

    private bool CanInterpolateTweenValues(DynValue start, DynValue goal)
    {
        if (start.Type == DataType.Number && goal.Type == DataType.Number) return double.IsFinite(start.Number) && double.IsFinite(goal.Number);
        return TryGetTypedValueName(start, out var startType) && TryGetTypedValueName(goal, out var goalType)
            && startType == goalType && (startType is "Vector2" or "Vector3" or "Color3" or "UDim2")
            && (startType != "Vector2" || TryReadVector2(start, out _))
            && (startType != "Vector3" || ReadVector(start, [float.NaN, float.NaN, float.NaN]).All(float.IsFinite))
            && (startType != "UDim2" || TryReadUDim2(start, out _, out _) && TryReadUDim2(goal, out _, out _));
    }

    private bool TryGetTypedValueName(DynValue value, out string name)
    {
        name = string.Empty;
        return value.Type == DataType.Table && runtimeTypeTags.TryGetValue(value.Table, out var tag) && (name = tag.Name) is not null;
    }

    private void PlayTween(TweenState state)
    {
        if (state.Playing) return;
        if (state.PlaybackState is "Begin" or "Completed" or "Cancelled")
        {
            state.Elapsed = 0;
            state.Starts = state.Goals.Keys.Select(property => (property, value: GetPropertyValue(state.Target, property)))
                .Where(pair => pair.value.Type != DataType.Nil).ToDictionary(pair => pair.property, pair => pair.value, StringComparer.Ordinal);
        }
        if (state.Starts.Count == 0) return;
        state.Playing = true;
        SetTweenPlaybackState(state, "Playing");
    }

    private void PauseTween(TweenState state)
    {
        if (!state.Playing) return;
        state.Playing = false;
        SetTweenPlaybackState(state, "Paused");
    }

    private void CancelTween(TweenState state)
    {
        if (!state.Playing && state.PlaybackState is not "Paused") return;
        state.Playing = false;
        SetTweenPlaybackState(state, "Cancelled");
        FireTweenCompleted(state, "Cancelled");
    }

    private static void SetTweenPlaybackState(TweenState state, string playbackState)
    {
        state.PlaybackState = playbackState;
        state.Instance.Set("PlaybackState", DynValue.NewString(playbackState));
    }

    private void AdvanceTweens(float elapsed)
    {
        foreach (var state in activeTweens.ToArray())
        {
            if (!state.Playing) continue;
            if (!IsTaskOwnerActive(state.Owner) || !instances.ContainsKey(state.Target.Id))
            {
                CancelTween(state);
                continue;
            }
            state.Elapsed += elapsed;
            if (state.Elapsed < state.Info.DelayTime) continue;
            var activeTime = state.Elapsed - state.Info.DelayTime;
            var duration = Math.Max(.000001f, state.Info.Time);
            var cycleDuration = duration * (state.Info.Reverses ? 2 : 1);
            var totalCycles = state.Info.RepeatCount < 0 ? float.PositiveInfinity : state.Info.RepeatCount + 1;
            var totalDuration = cycleDuration * totalCycles;
            var complete = activeTime >= totalDuration;
            var cycleProgress = complete ? 1 : activeTime % cycleDuration / cycleDuration;
            var phase = state.Info.Reverses && cycleProgress > .5f ? 1 - (cycleProgress - .5f) * 2 : state.Info.Reverses ? cycleProgress * 2 : cycleProgress;
            if (complete && state.Info.Reverses) phase = 0;
            var eased = ApplyTweenEasing(Math.Clamp(phase, 0, 1), state.Info.EasingStyle, state.Info.EasingDirection);
            foreach (var (property, goal) in state.Goals)
                if (state.Starts.TryGetValue(property, out var start)) SetPropertyValue(state.Target, property, InterpolateTweenValue(start, goal, eased));
            if (!complete) continue;
            state.Playing = false;
            SetTweenPlaybackState(state, "Completed");
            FireTweenCompleted(state, "Completed");
        }
        activeTweens.RemoveAll(state => !state.Playing && state.PlaybackState is "Completed" or "Cancelled");
    }

    private DynValue InterpolateTweenValue(DynValue start, DynValue goal, float alpha)
    {
        if (start.Type == DataType.Number && goal.Type == DataType.Number)
            return DynValue.NewNumber(start.Number + (goal.Number - start.Number) * alpha);
        if (!TryGetTypedValueName(start, out var type)) return goal;
        if (type == "Vector3")
        {
            var from = ReadVector(start, [0, 0, 0]);
            var to = ReadVector(goal, from);
            return VectorValue(from.Zip(to, (a, b) => a + (b - a) * alpha).ToArray());
        }
        if (type == "Vector2")
        {
            TryReadVector2(start, out var from); TryReadVector2(goal, out var to);
            return Vector2Value([from[0] + (to[0] - from[0]) * alpha, from[1] + (to[1] - from[1]) * alpha]);
        }
        if (type == "UDim2")
        {
            TryReadUDim2(start, out var fromScale, out var fromOffset); TryReadUDim2(goal, out var toScale, out var toOffset);
            return CreateUDim2Value(
                [fromScale[0] + (toScale[0] - fromScale[0]) * alpha, fromScale[1] + (toScale[1] - fromScale[1]) * alpha],
                [fromOffset[0] + (toOffset[0] - fromOffset[0]) * alpha, fromOffset[1] + (toOffset[1] - fromOffset[1]) * alpha]);
        }
        if (type == "Color3")
        {
            var from = start.Table; var to = goal.Table;
            return CreateColor(
                ReadColorChannelFloat(from.Get("R")) + (ReadColorChannelFloat(to.Get("R")) - ReadColorChannelFloat(from.Get("R"))) * alpha,
                ReadColorChannelFloat(from.Get("G")) + (ReadColorChannelFloat(to.Get("G")) - ReadColorChannelFloat(from.Get("G"))) * alpha,
                ReadColorChannelFloat(from.Get("B")) + (ReadColorChannelFloat(to.Get("B")) - ReadColorChannelFloat(from.Get("B"))) * alpha);
        }
        return goal;
    }

    private void FireTweenCompleted(TweenState state, string playbackState)
    {
        foreach (var callback in state.CompletedCallbacks.ToArray())
            if (IsTaskOwnerActive(callback.GameScript))
                scheduledTasks.Add(new ScheduledTask(callback.GameScript, script.CreateCoroutine(callback.Function), [DynValue.NewString(playbackState)], 0, true));
    }

    private static float ApplyTweenEasing(float value, string style, string direction)
    {
        static float BounceOut(float x) => x < 1 / 2.75f ? 7.5625f * x * x
            : x < 2 / 2.75f ? 7.5625f * (x -= 1.5f / 2.75f) * x + .75f
            : x < 2.5f / 2.75f ? 7.5625f * (x -= 2.25f / 2.75f) * x + .9375f
            : 7.5625f * (x -= 2.625f / 2.75f) * x + .984375f;
        float EaseIn(float x) => style switch
        {
            "Linear" => x,
            "Sine" => 1 - MathF.Cos(x * MathF.PI / 2),
            "Quad" => x * x,
            "Cubic" => x * x * x,
            "Quart" => x * x * x * x,
            "Quint" => x * x * x * x * x,
            "Exponential" => x <= 0 ? 0 : MathF.Pow(2, 10 * x - 10),
            "Circular" => 1 - MathF.Sqrt(Math.Max(0, 1 - x * x)),
            "Back" => 2.70158f * x * x * x - 1.70158f * x * x,
            "Bounce" => 1 - BounceOut(1 - x),
            "Elastic" => x <= 0 || x >= 1 ? x : -MathF.Pow(2, 10 * x - 10) * MathF.Sin((x * 10 - 10.75f) * (2 * MathF.PI / 3)),
            _ => x * x
        };
        var eased = direction switch
        {
            "In" => EaseIn(value),
            "InOut" => value < .5f ? EaseIn(value * 2) / 2 : 1 - EaseIn((1 - value) * 2) / 2,
            _ => 1 - EaseIn(1 - value)
        };
        return float.IsFinite(eased) ? Math.Clamp(eased, 0, 1) : value;
    }

    private void FireFrameSignal(string eventName, params DynValue[] arguments)
    {
        if (!runServiceCallbacks.TryGetValue(eventName, out var callbacks)) return;
        foreach (var connection in callbacks.ToArray())
            if (connection.Connected)
                scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), arguments, 0, true));
    }

    private void FireEndOfFrameSignals(float elapsed)
    {
        var delta = DynValue.NewNumber(elapsed);
        FireFrameSignal("Heartbeat", delta);
        RunDeferredTasks();
        FireFrameSignal("PreRender", delta);
        FireFrameSignal("RenderStepped", delta);
        RunDeferredTasks();
    }

    private void InstallTagApi(SceneNode node, Table instance)
    {
        instance.Set("AddTag", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            if (args.Count > index && args[index].Type == DataType.String) AddTag(node, args[index].String);
            return DynValue.Nil;
        }));
        instance.Set("RemoveTag", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            if (args.Count > index && args[index].Type == DataType.String) RemoveTag(node, args[index].String);
            return DynValue.Nil;
        }));
        instance.Set("HasTag", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return DynValue.NewBoolean(args.Count > index && args[index].Type == DataType.String
                && node.Tags.Contains(args[index].String, StringComparer.Ordinal));
        }));
        instance.Set("GetTags", DynValue.NewCallback((_, _) => TagArray(node.Tags)));
    }

    private DynValue TagArray(IEnumerable<string>? tags)
    {
        var values = new Table(script);
        var index = 1;
        if (tags is not null)
            foreach (var tag in tags) values.Set(index++, DynValue.NewString(tag));
        return DynValue.NewTable(values);
    }

    private bool IsAttached(SceneNode node) => GameProjectStore.Flatten(project).Any(item => ReferenceEquals(item, node));

    private void AddTag(SceneNode node, string tag)
    {
        if (!instances.ContainsKey(node.Id) || string.IsNullOrWhiteSpace(tag) || tag.Length > 256 || node.Tags.Contains(tag, StringComparer.Ordinal)) return;
        node.Tags.Add(tag);
        if (IsAttached(node)) FireTagChanged(node, tag, true);
    }

    private void RemoveTag(SceneNode node, string tag)
    {
        if (!instances.ContainsKey(node.Id) || !node.Tags.Remove(tag)) return;
        if (IsAttached(node)) FireTagChanged(node, tag, false);
    }

    private Table GetTagSignal(string tag, bool added)
    {
        var key = (tag, added);
        if (tagSignals.TryGetValue(key, out var existing)) return existing;
        var signal = new Table(script);
        runtimeTypeTags.Add(signal, new LuaXTypeTag("RBXScriptSignal"));
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, signal) ? 1 : 0;
            if (currentGameScript is not { } owner || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            var connectionTable = new Table(script);
            runtimeTypeTags.Add(connectionTable, new LuaXTypeTag("RBXScriptConnection"));
            var connection = new BindableConnection(owner, args[index], connectionTable);
            connectionTable.Set("Connected", DynValue.NewBoolean(true));
            connectionTable.Set("Disconnect", DynValue.NewCallback((_, _) =>
            {
                connection.Disconnect();
                if (tagCallbacks.TryGetValue(key, out var current)) current.Remove(connection);
                return DynValue.Nil;
            }));
            if (!tagCallbacks.TryGetValue(key, out var callbacks)) tagCallbacks[key] = callbacks = [];
            callbacks.Add(connection);
            return DynValue.NewTable(connectionTable);
        }));
        tagSignals[key] = signal;
        return signal;
    }

    private void FireTagChanged(SceneNode node, string tag, bool added)
    {
        if (!instances.TryGetValue(node.Id, out var instance) || !tagCallbacks.TryGetValue((tag, added), out var callbacks)) return;
        foreach (var connection in callbacks.ToArray())
            if (connection.Connected)
                scheduledTasks.Add(new ScheduledTask(connection.Owner, script.CreateCoroutine(connection.Function), [DynValue.NewTable(instance)], 0, true));
    }

    private void FireSubtreeTagChanges(SceneNode source, bool added)
    {
        foreach (var node in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
            foreach (var tag in node.Tags) FireTagChanged(node, tag, added);
    }

    private DynValue AttributeValueToLua(SceneAttributeValue value) => value.Type switch
    {
        "String" => DynValue.NewString(value.StringValue),
        "Boolean" => DynValue.NewBoolean(value.BooleanValue),
        "Number" => DynValue.NewNumber(value.NumberValue),
        "Vector3" => VectorValue(value.Vector3Value),
        "Color3" => ColorValue(value.Color3Value),
        _ => DynValue.Nil
    };

    private bool TryReadAttributeValue(DynValue value, out SceneAttributeValue attribute)
    {
        attribute = new SceneAttributeValue();
        if (value.Type == DataType.String) { attribute.Type = "String"; attribute.StringValue = value.String; return true; }
        if (value.Type == DataType.Boolean) { attribute.Type = "Boolean"; attribute.BooleanValue = value.Boolean; return true; }
        if (value.Type == DataType.Number && double.IsFinite(value.Number)) { attribute.Type = "Number"; attribute.NumberValue = value.Number; return true; }
        if (IsTypedValue(value, "Vector3"))
        {
            var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
            if (!vector.All(float.IsFinite)) return false;
            attribute.Type = "Vector3";
            attribute.Vector3Value = vector;
            return true;
        }
        if (IsTypedValue(value, "Color3"))
        {
            attribute.Type = "Color3";
            attribute.Color3Value = ReadColor(value, "#000000");
            return true;
        }
        return false;
    }

    private static bool AttributeValuesEqual(SceneAttributeValue left, SceneAttributeValue right) =>
        left.Type == right.Type && (left.Type switch
        {
            "String" => left.StringValue == right.StringValue,
            "Boolean" => left.BooleanValue == right.BooleanValue,
            "Number" => left.NumberValue == right.NumberValue,
            "Vector3" => left.Vector3Value.SequenceEqual(right.Vector3Value),
            "Color3" => left.Color3Value == right.Color3Value,
            _ => false
        });

    public void InvokePromptTriggered(string promptId, string playerId)
    {
        if (!promptTriggeredCallbacks.TryGetValue(promptId, out var callbacks)) return;
        var player = instances.TryGetValue(playerId, out var playerInstance)
            ? DynValue.NewTable(playerInstance) : DynValue.Nil;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback, player);
    }

    public void InvokeClickDetector(string detectorId, string playerId)
    {
        if (!clickDetectorCallbacks.TryGetValue(detectorId, out var callbacks)) return;
        var player = instances.TryGetValue(playerId, out var playerInstance)
            ? DynValue.NewTable(playerInstance) : DynValue.Nil;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback, player);
    }

    public void InvokeGuiActivated(string nodeId)
    {
        if (!guiActivatedCallbacks.TryGetValue(nodeId, out var callbacks)) return;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback);
    }

    public void InvokeToolActivated(string nodeId) => ActivateTool(nodeId);

    public void DeactivateTool(string nodeId)
    {
        if (!activatedToolIds.Remove(nodeId) || !toolDeactivatedCallbacks.TryGetValue(nodeId, out var callbacks)) return;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback);
    }

    private void ActivateTool(string nodeId)
    {
        if (!equippedToolIds.Contains(nodeId) || !instances.ContainsKey(nodeId)
            || FindNode(nodeId) is not { Type: "Tool", Enabled: true } || !activatedToolIds.Add(nodeId)) return;
        InvokeGuiActivated(nodeId);
    }

    public void InvokeToolEquipped(string nodeId, bool equipped)
    {
        if (equipped) equippedToolIds.Add(nodeId);
        else
        {
            DeactivateTool(nodeId);
            equippedToolIds.Remove(nodeId);
        }
        var callbacks = equipped ? toolEquippedCallbacks : toolUnequippedCallbacks;
        if (!callbacks.TryGetValue(nodeId, out var listeners)) return;
        foreach (var callback in listeners.ToArray()) StartCallback(callback);
    }

    public void UpdateHumanoidDeaths()
    {
        foreach (var humanoid in KnownNodes().Where(node => node.Type == "Humanoid"))
        {
            var currentHealth = humanoid.Health;
            if (!humanoidLastHealth.TryGetValue(humanoid.Id, out var previousHealth))
                humanoidLastHealth[humanoid.Id] = previousHealth = currentHealth;
            if (currentHealth != previousHealth)
            {
                humanoidLastHealth[humanoid.Id] = currentHealth;
                SynchronizeExternalProperty(humanoid, "Health");
                if (humanoidHealthChangedCallbacks.TryGetValue(humanoid.Id, out var changedCallbacks))
                    foreach (var callback in changedCallbacks.ToArray()) StartCallback(callback, DynValue.NewNumber(currentHealth));
            }
            var alive = humanoid.Health > 0;
            var wasAlive = humanoidWasAlive.GetValueOrDefault(humanoid.Id, alive);
            humanoidWasAlive[humanoid.Id] = alive;
            if (alive || !wasAlive) continue;
            FinishHumanoidMoveTo(humanoid.Id, false);
            if (humanoidDiedCallbacks.TryGetValue(humanoid.Id, out var callbacks))
                foreach (var callback in callbacks.ToArray()) StartCallback(callback);
        }
    }

    private DynValue TakeHumanoidDamage(SceneNode humanoid, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(humanoid, args);
        if (args.Count <= index || args[index].Type != DataType.Number || !double.IsFinite(args[index].Number)) return DynValue.Nil;
        SynchronizeInstances();
        if (HasProtectingForceField(humanoid)) return DynValue.Nil;
        var nextHealth = Math.Clamp((double)humanoid.Health - args[index].Number, 0, humanoid.MaxHealth);
        SetPropertyValue(humanoid, "Health", DynValue.NewNumber(nextHealth));
        UpdateHumanoidDeaths();
        return DynValue.Nil;
    }

    private bool TryGetHumanoidRoot(SceneNode humanoid, out SceneNode root)
    {
        root = null!;
        if (FindParentNode(humanoid) is not { Type: "Model" } model) return false;
        var parts = GameProjectStore.Flatten(model.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        root = parts.FirstOrDefault(part => part.Name == "HumanoidRootPart")
            ?? parts.FirstOrDefault(part => part.Id == model.PrimaryPart)
            ?? parts.FirstOrDefault()!;
        return root is not null;
    }

    private void UpdateHumanoidMovement(float elapsed)
    {
        foreach (var (humanoidId, target) in humanoidMoveTargets.ToArray())
        {
            if (FindNode(humanoidId) is not { Type: "Humanoid", Health: > 0 } humanoid)
            {
                FinishHumanoidMoveTo(humanoidId, false);
                continue;
            }
            if (!TryGetHumanoidRoot(humanoid, out var root))
            {
                FinishHumanoidMoveTo(humanoidId, false);
                continue;
            }

            target.Elapsed += elapsed;
            var position = new Vector3(root.Position[0], root.Position[1], root.Position[2]);
            var displacement = target.Position - position;
            var horizontal = new Vector2(displacement.X, displacement.Z);
            var targetRootY = target.Position.Y + root.Size[1] * .5f;
            var verticalOffset = targetRootY - position.Y;
            var reached = horizontal.Length() <= 1 && Math.Abs(verticalOffset) <= Math.Max(2, root.Size[1] * .5f);
            if (reached || target.Elapsed >= 8)
            {
                FinishHumanoidMoveTo(humanoidId, reached, root);
                continue;
            }

            if (!target.JumpInitiated && verticalOffset > 1.5f && Math.Abs(root.Velocity[1]) < .5f)
            {
                root.Velocity[1] = Math.Clamp(humanoid.JumpPower, 0, 1000);
                target.JumpInitiated = true;
                if (instances.TryGetValue(root.Id, out var rootInstance))
                {
                    var velocity = VectorValue(root.Velocity);
                    rootInstance.Set("Velocity", velocity);
                    rootInstance.Set("AssemblyLinearVelocity", velocity);
                }
            }

            if (horizontal.LengthSquared() <= .0001f)
            {
                SetHumanoidHorizontalVelocity(root, Vector3.Zero);
                continue;
            }
            var direction = Vector2.Normalize(horizontal);
            var speed = Math.Clamp(humanoid.WalkSpeed, 0, 1000);
            SetHumanoidHorizontalVelocity(root, new Vector3(direction.X * speed, 0, direction.Y * speed));
        }
    }

    private void FinishHumanoidMoveTo(string humanoidId, bool reached, SceneNode? root = null)
    {
        if (!humanoidMoveTargets.Remove(humanoidId)) return;
        if (root is null && FindNode(humanoidId) is { Type: "Humanoid" } humanoid && TryGetHumanoidRoot(humanoid, out var resolvedRoot)) root = resolvedRoot;
        if (root is not null) SetHumanoidHorizontalVelocity(root, Vector3.Zero);
        if (humanoidMoveToFinishedCallbacks.TryGetValue(humanoidId, out var callbacks))
            foreach (var callback in callbacks.ToArray()) StartCallback(callback, DynValue.NewBoolean(reached));
    }

    private void SetHumanoidHorizontalVelocity(SceneNode root, Vector3 horizontalVelocity)
    {
        root.Velocity = [horizontalVelocity.X, root.Velocity[1], horizontalVelocity.Z];
        if (!instances.TryGetValue(root.Id, out var instance)) return;
        var velocity = VectorValue(root.Velocity);
        instance.Set("Velocity", velocity);
        instance.Set("AssemblyLinearVelocity", velocity);
    }

    private bool HasProtectingForceField(SceneNode humanoid)
    {
        var parent = FindParentNode(humanoid);
        if (parent is null) return false;
        var rootPart = parent.Children.FirstOrDefault(child => child.Name == "HumanoidRootPart" && SceneNodeTypes.IsBasePart(child));
        if (rootPart?.Children.Any(child => child.Type == "ForceField" && child.Enabled) == true) return true;
        for (var ancestor = parent; ancestor is not null && !ReferenceEquals(ancestor, project.Workspace); ancestor = FindParentNode(ancestor))
            if (ancestor.Children.Any(child => child.Type == "ForceField" && child.Enabled)) return true;
        return false;
    }

    private void ProcessExplosions()
    {
        foreach (var explosion in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "Explosion").ToArray())
        {
            if (!detonatedExplosions.Add(explosion.Id)) continue;
            explosion.ExplosionAge = 0;
            activeExplosions[explosion.Id] = 0;
            propertyChanged?.Invoke(explosion, "ExplosionAge", DynValue.NewNumber(0));
            var center = new Vector3(explosion.Position[0], explosion.Position[1], explosion.Position[2]);
            var hitParts = new List<(SceneNode Part, float Distance, float EdgeDistance)>();
            foreach (var part in Parts)
            {
                var position = new Vector3(part.Position[0], part.Position[1], part.Position[2]);
                var distance = Vector3.Distance(center, position);
                var halfDiagonal = new Vector3(part.Size[0], part.Size[1], part.Size[2]).Length() * .5f;
                var edgeDistance = Math.Max(0, distance - halfDiagonal);
                if (edgeDistance > explosion.BlastRadius) continue;
                hitParts.Add((part, distance, edgeDistance));
                if (explosionHitCallbacks.TryGetValue(explosion.Id, out var callbacks) && instances.TryGetValue(part.Id, out var partInstance))
                    foreach (var callback in callbacks.ToArray())
                        scheduledTasks.Add(new ScheduledTask(callback.GameScript, script.CreateCoroutine(callback.Function), [DynValue.NewTable(partInstance), DynValue.NewNumber(distance)], 0, true));
                if (part.Anchored || explosion.BlastPressure <= 0) continue;
                var direction = distance > .00001f ? Vector3.Normalize(position - center) : Vector3.UnitY;
                var strength = Math.Clamp(explosion.BlastPressure / 500000f * 60 * (1 - edgeDistance / Math.Max(explosion.BlastRadius, .001f)), 0, 120);
                part.Velocity = [part.Velocity[0] + direction.X * strength, part.Velocity[1] + direction.Y * strength, part.Velocity[2] + direction.Z * strength];
                if (instances.TryGetValue(part.Id, out var partTable))
                {
                    partTable.Set("Velocity", VectorValue(part.Velocity));
                    partTable.Set("AssemblyLinearVelocity", VectorValue(part.Velocity));
                }
                propertyChanged?.Invoke(part, "Velocity", VectorValue(part.Velocity));
            }
            var jointRadius = explosion.BlastRadius * explosion.DestroyJointRadiusPercent;
            if (jointRadius <= 0) continue;
            var breakableParts = hitParts.Where(hit => hit.EdgeDistance <= jointRadius).Select(hit => hit.Part.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var weld in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type is "WeldConstraint" or "Weld" or "Motor6D" && node.Enabled))
            {
                if (!breakableParts.Contains(weld.Part0) && !breakableParts.Contains(weld.Part1)) continue;
                var first = FindNode(weld.Part0);
                var second = FindNode(weld.Part1);
                if (first is not null && second is not null && IsPartForceFieldProtected(first) && IsPartForceFieldProtected(second)) continue;
                weld.Enabled = false;
                if (instances.TryGetValue(weld.Id, out var weldInstance)) weldInstance.Set("Enabled", DynValue.NewBoolean(false));
                propertyChanged?.Invoke(weld, "Enabled", DynValue.NewBoolean(false));
            }
            foreach (var humanoid in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "Humanoid" && node.Health > 0))
            {
                var model = FindParentNode(humanoid);
                if (model is not { Type: "Model" } || HasProtectingForceField(humanoid)) continue;
                var modelPartIds = GameProjectStore.Flatten(model.Children).Where(SceneNodeTypes.IsBasePart).Select(part => part.Id).ToHashSet(StringComparer.Ordinal);
                if (!hitParts.Any(hit => hit.EdgeDistance <= jointRadius && modelPartIds.Contains(hit.Part.Id))) continue;
                humanoid.Health = 0;
                if (instances.TryGetValue(humanoid.Id, out var humanoidInstance)) humanoidInstance.Set("Health", DynValue.NewNumber(0));
                propertyChanged?.Invoke(humanoid, "Health", DynValue.NewNumber(0));
            }
            UpdateHumanoidDeaths();
        }
    }

    private bool IsPartForceFieldProtected(SceneNode part)
    {
        if (part.Children.Any(child => child.Type == "ForceField")) return true;
        for (var ancestor = FindParentNode(part); ancestor is not null && !ReferenceEquals(ancestor, project.Workspace); ancestor = FindParentNode(ancestor))
            if (ancestor.Children.Any(child => child.Type == "ForceField")) return true;
        return false;
    }

    private void AdvanceExplosions(float elapsedSeconds)
    {
        foreach (var (id, age) in activeExplosions.ToArray())
        {
            if (FindNode(id) is not { Type: "Explosion" } explosion) { activeExplosions.Remove(id); continue; }
            var nextAge = age + elapsedSeconds;
            if (nextAge >= .5f)
            {
                activeExplosions.Remove(id);
                DestroyInstance(explosion);
                continue;
            }
            explosion.ExplosionAge = nextAge;
            activeExplosions[id] = nextAge;
            propertyChanged?.Invoke(explosion, "ExplosionAge", DynValue.NewNumber(nextAge));
        }
    }

    public void UpdateGuiTextInput(string nodeId, string value)
    {
        var node = FindNode(nodeId);
        if (node is not { Type: "TextBox" }) return;
        node.Text = value;
        if (instances.TryGetValue(node.Id, out var instance)) instance.Set("Text", DynValue.NewString(value));
        if (guiTextChangedCallbacks.TryGetValue(node.Id, out var callbacks))
        {
            foreach (var callback in callbacks.ToArray()) StartCallback(callback, DynValue.NewString(value));
        }
        SynchronizeInstances();
    }

    private void BindCurrentScript(GameScript gameScript)
    {
        currentGameScript = gameScript;
    }

    private void StartCallback(ScriptCallback callback, params DynValue[] arguments)
    {
        if (!project.Scripts.Any(item => item.Id == callback.GameScript.Id)
            || FindScriptNode(callback.GameScript.Id) is { } owner && (!owner.Enabled || !IsScriptAttachedForExecution(owner))) return;
        var previousScript = currentGameScript;
        BindCurrentScript(callback.GameScript);
        try
        {
            var coroutine = script.CreateCoroutine(callback.Function).Coroutine;
            coroutine.Resume(arguments);
            SynchronizeInstances();
            RunDeferredTasks();
            if (coroutine.State != CoroutineState.Dead && project.Scripts.Any(item => item.Id == callback.GameScript.Id)
                && (FindScriptNode(callback.GameScript.Id) is not { } resumedOwner || resumedOwner.Enabled && IsScriptAttachedForExecution(resumedOwner)))
                activeScripts.Add(new ScriptExecution(callback.GameScript, coroutine, false, callback.Name));
        }
        catch (Exception exception)
        {
            output?.Invoke($"{callback.GameScript.Name} {callback.Name} callback: {exception.Message}");
            SynchronizeInstances();
        }
        finally { currentGameScript = previousScript; }
    }

    private SceneNode? ReadNodeArgument(DynValue value)
    {
        if (value.Type == DataType.Table) return FindNode(value.Table.Get("Id").CastToString() ?? string.Empty);
        return value.Type == DataType.String ? FindNode(value.String) : null;
    }

    private bool TryReadVector3(DynValue value, out Vector3 vector)
    {
        vector = Vector3.Zero;
        if (!IsTypedValue(value, "Vector3")) return false;
        var values = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
        if (!values.All(float.IsFinite)) return false;
        vector = new Vector3(values[0], values[1], values[2]);
        return true;
    }

    private DynValue Raycast(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 2 || !IsTypedValue(args[index], "Vector3") || !IsTypedValue(args[index + 1], "Vector3")) return DynValue.Nil;
        var originValues = ReadVector(args[index], [float.NaN, float.NaN, float.NaN]);
        var directionValues = ReadVector(args[index + 1], [float.NaN, float.NaN, float.NaN]);
        if (!originValues.All(float.IsFinite) || !directionValues.All(float.IsFinite)) return DynValue.Nil;
        var origin = new Vector3(originValues[0], originValues[1], originValues[2]);
        var direction = new Vector3(directionValues[0], directionValues[1], directionValues[2]);
        if (!float.IsFinite(direction.Length()) || direction.LengthSquared() == 0) return DynValue.Nil;

        var paramsValue = args.Count > index + 2 ? args[index + 2] : DynValue.Nil;
        if (!TryReadRaycastOptions(paramsValue, out var options)) return DynValue.Nil;
        SynchronizeInstances();
        SceneNode? closestPart = null;
        CollisionGeometry.RayHit? closestHit = null;
        foreach (var part in GetWorldRootParts(workspace))
        {
            if (!(options.RespectCanCollide ? part.CanCollide : part.CanQuery)) continue;
            var matchesFilter = options.FilteredIds.Contains(part.Id);
            if (options.FilterType == "Include" ? !matchesFilter : matchesFilter) continue;
            var hit = CollisionGeometry.Raycast(part, origin, direction);
            if (hit is null || closestHit is not null && hit.Value.Distance >= closestHit.Value.Distance) continue;
            closestPart = part;
            closestHit = hit;
        }
        if (closestPart is null || closestHit is null || !instances.TryGetValue(closestPart.Id, out var hitInstance)) return DynValue.Nil;
        return CreateRaycastResult(closestPart, hitInstance, closestHit.Value);
    }

    private DynValue Blockcast(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 4 || !TryReadVector3(args[index], out var position) || !TryReadVector3(args[index + 1], out var size)
            || !TryReadVector3(args[index + 2], out var rotation) || !TryReadVector3(args[index + 3], out var direction)
            || size.X < 0 || size.Y < 0 || size.Z < 0 || !float.IsFinite(direction.Length()) || direction.LengthSquared() <= 0) return DynValue.Nil;
        var paramsValue = args.Count > index + 4 ? args[index + 4] : DynValue.Nil;
        if (!TryReadRaycastOptions(paramsValue, out var options)) return DynValue.Nil;
        var castBox = new SceneNode
        {
            Type = "Part",
            Shape = "Block",
            Name = "BlockcastQuery",
            Position = [position.X, position.Y, position.Z],
            Size = [size.X, size.Y, size.Z],
            Rotation = [rotation.X, rotation.Y, rotation.Z]
        };

        SynchronizeInstances();
        SceneNode? closestPart = null;
        CollisionGeometry.RayHit? closestHit = null;
        foreach (var part in GetWorldRootParts(workspace))
        {
            if (!(options.RespectCanCollide ? part.CanCollide : part.CanQuery)) continue;
            var matchesFilter = options.FilteredIds.Contains(part.Id);
            if (options.FilterType == "Include" ? !matchesFilter : matchesFilter) continue;
            var hit = CollisionGeometry.Boxcast(castBox, part, direction);
            if (hit is null || closestHit is not null && hit.Value.Distance >= closestHit.Value.Distance) continue;
            closestPart = part;
            closestHit = hit;
        }
        if (closestPart is null || closestHit is null || !instances.TryGetValue(closestPart.Id, out var hitInstance)) return DynValue.Nil;
        return CreateRaycastResult(closestPart, hitInstance, closestHit.Value);
    }

    private DynValue Spherecast(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 3 || !TryReadVector3(args[index], out var position)
            || args[index + 1].Type != DataType.Number || !double.IsFinite(args[index + 1].Number)
            || !TryReadVector3(args[index + 2], out var direction) || !float.IsFinite(direction.Length())
            || direction.LengthSquared() <= 0) return DynValue.Nil;
        var radius = (float)args[index + 1].Number;
        if (radius < 0 || !float.IsFinite(radius)) return DynValue.Nil;
        var paramsValue = args.Count > index + 3 ? args[index + 3] : DynValue.Nil;
        if (!TryReadRaycastOptions(paramsValue, out var options)) return DynValue.Nil;
        SynchronizeInstances();
        SceneNode? closestPart = null;
        CollisionGeometry.RayHit? closestHit = null;
        foreach (var part in GetWorldRootParts(workspace))
        {
            if (!(options.RespectCanCollide ? part.CanCollide : part.CanQuery)) continue;
            var matchesFilter = options.FilteredIds.Contains(part.Id);
            if (options.FilterType == "Include" ? !matchesFilter : matchesFilter) continue;
            var hit = CollisionGeometry.Spherecast(position, radius, direction, part);
            if (hit is null || closestHit is not null && hit.Value.Distance >= closestHit.Value.Distance) continue;
            closestPart = part;
            closestHit = hit;
        }
        if (closestPart is null || closestHit is null || !instances.TryGetValue(closestPart.Id, out var hitInstance)) return DynValue.Nil;
        return CreateRaycastResult(closestPart, hitInstance, closestHit.Value);
    }

    private DynValue Shapecast(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 2 || ReadNodeArgument(args[index]) is not { } movingPart || !IsBasePart(movingPart)
            || !TryReadVector3(args[index + 1], out var direction) || !float.IsFinite(direction.Length()) || direction.LengthSquared() <= 0) return DynValue.Nil;
        var paramsValue = args.Count > index + 2 ? args[index + 2] : DynValue.Nil;
        if (!TryReadRaycastOptions(paramsValue, out var options)) return DynValue.Nil;
        SynchronizeInstances();
        SceneNode? closestPart = null;
        CollisionGeometry.RayHit? closestHit = null;
        foreach (var part in GetWorldRootParts(workspace))
        {
            if (part.Id == movingPart.Id || !(options.RespectCanCollide ? part.CanCollide : part.CanQuery)) continue;
            var matchesFilter = options.FilteredIds.Contains(part.Id);
            if (options.FilterType == "Include" ? !matchesFilter : matchesFilter) continue;
            var hit = CollisionGeometry.Shapecast(movingPart, part, direction);
            if (hit is null || closestHit is not null && hit.Value.Distance >= closestHit.Value.Distance) continue;
            closestPart = part;
            closestHit = hit;
        }
        if (closestPart is null || closestHit is null || !instances.TryGetValue(closestPart.Id, out var hitInstance)) return DynValue.Nil;
        return CreateRaycastResult(closestPart, hitInstance, closestHit.Value);
    }

    private bool TryReadRaycastOptions(DynValue value, out RaycastQueryOptions options)
    {
        var filterType = "Exclude";
        var filteredIds = new HashSet<string>(StringComparer.Ordinal);
        var respectCanCollide = false;
        if (value.Type != DataType.Nil)
        {
            if (!IsTypedValue(value, "RaycastParams")) { options = default!; return false; }
            var parameters = value.Table;
            filterType = parameters.Get("FilterType").CastToString() ?? string.Empty;
            if (filterType is not ("Exclude" or "Include")) { options = default!; return false; }
            var roots = parameters.Get("FilterDescendantsInstances");
            if (roots.Type != DataType.Table) { options = default!; return false; }
            for (var item = 1; item <= roots.Table.Length; item++)
            {
                if (ReadNodeArgument(roots.Table.Get(item)) is not { } root) continue;
                filteredIds.Add(root.Id);
                foreach (var descendant in GameProjectStore.Flatten(root.Children)) filteredIds.Add(descendant.Id);
            }
            var respect = parameters.Get("RespectCanCollide");
            if (respect.Type == DataType.Boolean) respectCanCollide = respect.Boolean;
            else if (respect.Type != DataType.Nil) { options = default!; return false; }
        }
        options = new RaycastQueryOptions(filterType, filteredIds, respectCanCollide);
        return true;
    }

    private DynValue CreateRaycastResult(SceneNode part, Table instance, CollisionGeometry.RayHit hit)
    {
        var result = new Table(script);
        runtimeTypeTags.Add(result, new LuaXTypeTag("RaycastResult"));
        result.Set("Instance", DynValue.NewTable(instance));
        result.Set("Position", VectorValue([hit.Position.X, hit.Position.Y, hit.Position.Z]));
        result.Set("Normal", VectorValue([hit.Normal.X, hit.Normal.Y, hit.Normal.Z]));
        result.Set("Distance", DynValue.NewNumber(hit.Distance));
        result.Set("Material", DynValue.NewString(part.Material));
        result.ReadOnly = true;
        return DynValue.NewTable(result);
    }

    private sealed record RaycastQueryOptions(string FilterType, HashSet<string> FilteredIds, bool RespectCanCollide);

    private DynValue GetPartsInPart(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count <= index || ReadNodeArgument(args[index]) is not { } queryPart || !IsBasePart(queryPart)) return EmptyArray();
        var paramsValue = args.Count > index + 1 ? args[index + 1] : DynValue.Nil;
        if (!TryReadOverlapOptions(paramsValue, out var options)) return EmptyArray();
        return QueryParts(workspace, queryPart.Id, part => CollisionGeometry.Intersects(queryPart, part), options);
    }

    private DynValue GetPartBoundsInBox(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 2 || !TryReadVector3(args[index], out var position) || !TryReadVector3(args[index + 1], out var size)
            || size.X < 0 || size.Y < 0 || size.Z < 0) return EmptyArray();
        var rotation = new Vector3(0, 0, 0);
        var paramsIndex = index + 2;
        if (args.Count > index + 2)
        {
            if (args[index + 2].Type == DataType.Nil)
            {
                if (args.Count > index + 3) paramsIndex++;
            }
            else if (IsTypedValue(args[index + 2], "OverlapParams"))
            {
                // Rotation may be omitted when the third argument is OverlapParams.
            }
            else if (TryReadVector3(args[index + 2], out rotation)) paramsIndex++;
            else return EmptyArray();
        }
        var paramsValue = args.Count > paramsIndex ? args[paramsIndex] : DynValue.Nil;
        if (!TryReadOverlapOptions(paramsValue, out var options)) return EmptyArray();
        var region = new SceneNode
        {
            Type = "Part",
            Name = "BoundsQuery",
            Position = [position.X, position.Y, position.Z],
            Size = [size.X, size.Y, size.Z],
            Rotation = [rotation.X, rotation.Y, rotation.Z]
        };
        return QueryParts(workspace, null, part => CollisionGeometry.BoundsIntersect(region, part), options);
    }

    private DynValue GetPartBoundsInRadius(SceneNode workspace, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(workspace, args);
        if (args.Count < index + 2 || !TryReadVector3(args[index], out var position)
            || args[index + 1].Type != DataType.Number || !double.IsFinite(args[index + 1].Number)) return EmptyArray();
        var radius = (float)args[index + 1].Number;
        if (radius < 0 || !float.IsFinite(radius)) return EmptyArray();
        var paramsValue = args.Count > index + 2 ? args[index + 2] : DynValue.Nil;
        if (!TryReadOverlapOptions(paramsValue, out var options)) return EmptyArray();
        return QueryParts(workspace, null, part => CollisionGeometry.BoundsIntersectSphere(part, position, radius), options);
    }

    private bool TryReadOverlapOptions(DynValue value, out OverlapQueryOptions options)
    {
        var filterType = "Exclude";
        var filteredIds = new HashSet<string>(StringComparer.Ordinal);
        var maxParts = 0;
        var respectCanCollide = false;
        if (value.Type != DataType.Nil)
        {
            if (!IsTypedValue(value, "OverlapParams")) { options = default!; return false; }
            var parameters = value.Table;
            filterType = parameters.Get("FilterType").CastToString() ?? string.Empty;
            if (filterType is not ("Exclude" or "Include")) { options = default!; return false; }
            var roots = parameters.Get("FilterDescendantsInstances");
            if (roots.Type != DataType.Table) { options = default!; return false; }
            for (var item = 1; item <= roots.Table.Length; item++)
            {
                if (ReadNodeArgument(roots.Table.Get(item)) is not { } root) continue;
                filteredIds.Add(root.Id);
                foreach (var descendant in GameProjectStore.Flatten(root.Children)) filteredIds.Add(descendant.Id);
            }
            var maximum = parameters.Get("MaxParts");
            if (maximum.Type == DataType.Number && double.IsFinite(maximum.Number)) maxParts = (int)Math.Clamp(Math.Truncate(maximum.Number), 0, int.MaxValue);
            else if (maximum.Type != DataType.Nil) { options = default!; return false; }
            var respect = parameters.Get("RespectCanCollide");
            if (respect.Type == DataType.Boolean) respectCanCollide = respect.Boolean;
            else if (respect.Type != DataType.Nil) { options = default!; return false; }
        }
        options = new OverlapQueryOptions(filterType, filteredIds, maxParts, respectCanCollide);
        return true;
    }

    private DynValue QueryParts(SceneNode worldRoot, string? excludedPartId, Func<SceneNode, bool> intersectsQuery, OverlapQueryOptions options)
    {
        SynchronizeInstances();
        var result = new Table(script);
        var count = 0;
        foreach (var part in GetWorldRootParts(worldRoot))
        {
            if (part.Id == excludedPartId || !(options.RespectCanCollide ? part.CanCollide : part.CanQuery)) continue;
            var matchesFilter = options.FilteredIds.Contains(part.Id);
            if (options.FilterType == "Include" ? !matchesFilter : matchesFilter) continue;
            if (!intersectsQuery(part) || !instances.TryGetValue(part.Id, out var instance)) continue;
            result.Set(++count, DynValue.NewTable(instance));
            if (options.MaxParts > 0 && count >= options.MaxParts) break;
        }
        return DynValue.NewTable(result);
    }

    private IEnumerable<SceneNode> GetWorldRootParts(SceneNode worldRoot) => worldRoot.Type == "Workspace"
        ? Parts
        : GameProjectStore.Flatten(worldRoot.Children).Where(SceneNodeTypes.IsBasePart);

    private DynValue EmptyArray() => DynValue.NewTable(new Table(script));

    private sealed record OverlapQueryOptions(string FilterType, HashSet<string> FilteredIds, int MaxParts, bool RespectCanCollide);

    private DynValue NewInstance(CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.String || !CreatableClasses.Contains(args[0].String)) return DynValue.Nil;
        var className = args[0].String;
        var node = new SceneNode { Type = className, Name = className };
        if (SceneNodeTypes.IsBasePart(node)) node.Position = [0, 1, 0];
        if (className == "Atmosphere") { node.Color = "#C6D9FF"; node.AtmosphereDecay = "#607080"; }
        if (className == "Clouds") { node.Color = "#FFFFFF"; node.CloudCover = .5f; node.CloudDensity = .5f; }
        if (className == "BlurEffect") node.EffectSize = 0;
        if (className == "ColorCorrectionEffect") node.Brightness = 0;
        if (className == "SpawnLocation") { node.Size = [6, 1, 6]; node.Position = [0, .5f, 0]; node.Color = "#72BF9D"; node.Material = "Metal"; }
        if (className is "Seat" or "VehicleSeat") { node.Size = [2, 1, 2]; node.Position = [0, .5f, 0]; node.Color = "#596B89"; node.Material = "Metal"; }
        if (className == "Fire") { node.Color = "#FF6A00"; node.EffectSize = 5; }
        if (className == "Smoke") node.Color = "#B0B0B0";
        if (className == "Sparkles") node.Color = "#FFFFFF";
        if (className is "ParticleEmitter" or "Trail") node.Color = "#FFFFFF";
        if (className is "VectorForce" or "Torque") node.RelativeTo = "Attachment0";
        if (className is "SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui")
        {
            if (className is "SelectionBox" or "SelectionSphere") node.Color = "#FFFFFF";
            if (args.Count > 1 && ReadNodeArgument(args[1]) is { } adornmentParent
                && (IsBasePart(adornmentParent) || className is not ("BillboardGui" or "SurfaceGui") && adornmentParent.Type == "Model")) node.Adornee = adornmentParent.Id;
        }
        if (className == "BillboardGui") node.Size = [200, 50, 1];
        if (className == "SurfaceGui") node.PixelsPerStud = 50;
        if (className == "UICorner") UICornerRadiusService.Set(node, "CornerRadius", 0, 8);
        if (className == "UIShadow") { node.Color = "#000000"; node.Transparency = .5f; node.ZIndex = -1; }
        if (className is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint" && args.Count > 1 && ReadNodeArgument(args[1]) is { } constraintParent && IsBasePart(constraintParent))
            node.Part0 = constraintParent.Id;
        if (className is "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" && args.Count > 1 && ReadNodeArgument(args[1]) is { } forceParent)
            node.Attachment0 = forceParent.Type == "Attachment" ? forceParent.Id
                : IsBasePart(forceParent) ? forceParent.Children.FirstOrDefault(child => child.Type == "Attachment")?.Id ?? string.Empty : string.Empty;
        createdNodes[node.Id] = node;
        var instance = CreateInstance(node);
        instances[node.Id] = instance;
        if (className is "Script" or "ModuleScript" or "LocalScript")
            project.Scripts.Add(new GameScript
            {
                Id = node.Id,
                Name = node.Name,
                Source = className == "ModuleScript" ? "local module = {}\nreturn module" : string.Empty
            });
        if (className == "Humanoid") humanoidWasAlive[node.Id] = true;
        if (args.Count > 1 && TrySetParent(node, args[1]))
            propertyChanged?.Invoke(node, "Parent", DynValue.NewTable(instances[FindParentNode(node)!.Id]));
        return DynValue.NewTable(instance);
    }

    private DynValue CloneInstance(SceneNode source)
    {
        if (source.Type is "Workspace" or "Players" or "PlayerGui" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" || source.Type == "Camera" && project.Workspace.Children.Any(camera => camera.Type == "Camera" && camera.Id == source.Id) || !instances.ContainsKey(source.Id)) return DynValue.Nil;
        SynchronizeInstances();
        if (!instances.ContainsKey(source.Id)) return DynValue.Nil;
        var clone = GameProjectStore.CloneSubtree(source, out var renewedIds);
        if (clone is null) return DynValue.Nil;
        var clonedNodes = new[] { clone }.Concat(GameProjectStore.Flatten(clone.Children)).ToArray();
        foreach (var node in clonedNodes) createdNodes[node.Id] = node;
        foreach (var node in clonedNodes)
        {
            instances[node.Id] = CreateInstance(node);
            if (node.Type == "Humanoid") humanoidWasAlive[node.Id] = node.Health > 0;
        }
        foreach (var node in clonedNodes)
        {
            var parent = FindParentNode(node);
            instances[node.Id].Set("Parent", parent is not null ? DynValue.NewTable(instances[parent.Id]) : DynValue.Nil);
            RefreshParentChildren(node);
            if (node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")
            {
                instances[node.Id].Set("Part0", GetPartReferenceValue(node.Part0));
                instances[node.Id].Set("Part1", GetPartReferenceValue(node.Part1));
            }
            if (node.Type is "SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") instances[node.Id].Set("Adornee", GetObjectValueReference(node.Adornee));
            if (node.Type == "Model") instances[node.Id].Set("PrimaryPart", GetModelPrimaryPartReference(node));
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink")
            {
                instances[node.Id].Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink") instances[node.Id].Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            }
        }
        foreach (var node in clonedNodes.Where(node => node.Type == "ObjectValue"))
            instances[node.Id].Set("Value", GetValueObjectValue(node));
        foreach (var original in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
        {
            if (original.Type is not ("Script" or "LocalScript" or "ModuleScript") || !renewedIds.TryGetValue(original.Id, out var clonedId)) continue;
            var originalScript = project.Scripts.FirstOrDefault(item => item.Id == original.Id);
            if (originalScript is null) continue;
            var clonedScript = clonedNodes.First(node => node.Id == clonedId);
            project.Scripts.Add(new GameScript { Id = clonedId, Name = clonedScript.Name, Source = originalScript.Source });
        }
        return DynValue.NewTable(instances[clone.Id]);
    }

    private DynValue AddDebrisItem(Table debris, CallbackArguments args)
    {
        var index = args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, debris) ? 1 : 0;
        if (args.Count <= index || ReadNodeArgument(args[index]) is not { } node || !instances.ContainsKey(node.Id)
            || node.Type is "Workspace" or "Players" or "PlayerGui" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" or "Camera") return DynValue.Nil;
        var lifetime = args.Count > index + 1 && args[index + 1].Type == DataType.Number ? args[index + 1].Number : 10;
        if (!double.IsFinite(lifetime)) return DynValue.Nil;
        if (lifetime <= 0)
        {
            DestroyInstance(node);
            return DynValue.Nil;
        }
        if (debrisItems.Count >= 1000)
        {
            var oldest = debrisItems[0];
            debrisItems.RemoveAt(0);
            DestroyInstance(oldest.Node);
        }
        if (instances.ContainsKey(node.Id)) debrisItems.Add(new DebrisItem(node, (float)Math.Min(lifetime, float.MaxValue)));
        return DynValue.Nil;
    }

    private void AdvanceDebris(float elapsed)
    {
        if (debrisItems.Count == 0) return;
        foreach (var item in debrisItems) item.Remaining -= elapsed;
        var expired = debrisItems.Where(item => item.Remaining <= 0).Select(item => item.Node).ToArray();
        debrisItems.RemoveAll(item => item.Remaining <= 0);
        foreach (var node in expired)
            if (instances.ContainsKey(node.Id)) DestroyInstance(node);
    }

    private DynValue DestroyInstance(SceneNode node, bool notifyParentChange = true, bool refreshParent = true)
    {
        if (node.Type is "Workspace" or "Players" or "PlayerGui" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" || node.Type == "Camera" && project.Workspace.Children.Any(camera => camera.Type == "Camera" && camera.Id == node.Id) || !instances.ContainsKey(node.Id)) return DynValue.Nil;
        var wasAttached = IsAttached(node);
        var oldParent = FindParentNode(node);
        var sourceCollection = FindSourceCollection(node);
        sourceCollection?.Remove(node);
        if (wasAttached) FireSubtreeTagChanges(node, false);
        if (oldParent is not null && refreshParent) RefreshParentChildren(oldParent);
        if (instances.TryGetValue(node.Id, out var removedInstance)) removedInstance.Set("Parent", DynValue.Nil);
        var removedNodes = new[] { node }.Concat(GameProjectStore.Flatten(node.Children)).ToArray();
        var removedIds = removedNodes.Select(removed => removed.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var player in KnownNodes().Where(item => item.Type == "Player" && removedIds.Contains(item.Character)))
        {
            var oldCharacter = player.Character;
            FirePlayerCharacterSignal(player, "CharacterRemoving", oldCharacter);
            player.Character = string.Empty;
            if (instances.TryGetValue(player.Id, out var playerInstance)) playerInstance.Set("Character", DynValue.Nil);
            propertyChanged?.Invoke(player, "Character", DynValue.Nil);
        }
        var removedScriptIds = removedNodes.Where(SceneNodeTypes.IsBaseScript).Select(removed => removed.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in removedNodes)
        {
            instances.Remove(removed.Id);
            createdNodes.Remove(removed.Id);
            linkedChildKeys.Remove(removed.Id);
            humanoidWasAlive.Remove(removed.Id);
            touchedCallbacks.Remove(removed.Id);
            guiActivatedCallbacks.Remove(removed.Id);
            toolEquippedCallbacks.Remove(removed.Id);
            toolUnequippedCallbacks.Remove(removed.Id);
            toolDeactivatedCallbacks.Remove(removed.Id);
            equippedToolIds.Remove(removed.Id);
            activatedToolIds.Remove(removed.Id);
            guiTextChangedCallbacks.Remove(removed.Id);
            promptTriggeredCallbacks.Remove(removed.Id);
            clickDetectorCallbacks.Remove(removed.Id);
            DisconnectCallbacksForNode(humanoidDiedCallbacks, removed.Id);
            DisconnectCallbacksForNode(humanoidHealthChangedCallbacks, removed.Id);
            DisconnectCallbacksForNode(humanoidMoveToFinishedCallbacks, removed.Id);
            humanoidMoveTargets.Remove(removed.Id);
            humanoidLastHealth.Remove(removed.Id);
            explosionHitCallbacks.Remove(removed.Id);
            activeExplosions.Remove(removed.Id);
            valueChangedCallbacks.Remove(removed.Id);
            foreach (var key in bindableEventCallbacks.Keys
                         .Where(key => key == removed.Id || key.StartsWith($"{removed.Id}::remote::", StringComparison.Ordinal)).ToArray())
                if (bindableEventCallbacks.Remove(key, out var bindableConnections))
                    foreach (var connection in bindableConnections) connection.Disconnect();
            bindableFunctionCallbacks.Remove(removed.Id);
            foreach (var key in remoteFunctionCallbacks.Keys.Where(key => key.NodeId == removed.Id).ToArray()) remoteFunctionCallbacks.Remove(key);
            queuedScriptIds.Remove(removed.Id);
            startedScriptIds.Remove(removed.Id);
            project.Scripts.RemoveAll(script => script.Id == removed.Id);
        }
        foreach (var player in KnownNodes().Where(item => item.Type == "Player" && removedIds.Contains(item.Team)))
        {
            player.Team = string.Empty;
            if (instances.TryGetValue(player.Id, out var playerInstance))
            {
                playerInstance.Set("Team", DynValue.Nil);
                playerInstance.Set("TeamColor", ColorValue("#FFFFFF"));
            }
            propertyChanged?.Invoke(player, "Team", DynValue.Nil);
        }
        foreach (var key in attributeCallbacks.Keys.Where(key => removedIds.Contains(key.NodeId)).ToArray())
        {
            foreach (var connection in attributeCallbacks[key]) connection.Disconnect();
            attributeCallbacks.Remove(key);
        }
        foreach (var key in attributeSignals.Keys.Where(key => removedIds.Contains(key.NodeId)).ToArray()) attributeSignals.Remove(key);
        debrisItems.RemoveAll(item => removedIds.Contains(item.Node.Id));
        foreach (var objectValue in KnownNodes().Where(item => item.Type == "ObjectValue" && removedIds.Contains(item.ObjectValueId)))
        {
            objectValue.ObjectValueId = string.Empty;
            if (instances.TryGetValue(objectValue.Id, out var valueInstance)) valueInstance.Set("Value", DynValue.Nil);
            propertyChanged?.Invoke(objectValue, "Value", DynValue.Nil);
            FireValueChanged(objectValue, DynValue.Nil);
        }
        foreach (var viewportFrame in KnownNodes().Where(item => item.Type == "ViewportFrame" && removedIds.Contains(item.ViewportCurrentCameraId)))
        {
            viewportFrame.ViewportCurrentCameraId = string.Empty;
            if (instances.TryGetValue(viewportFrame.Id, out var viewportInstance)) viewportInstance.Set("CurrentCamera", DynValue.Nil);
            propertyChanged?.Invoke(viewportFrame, "CurrentCamera", DynValue.Nil);
        }
        foreach (var sound in KnownNodes().Where(item => item.Type == "Sound" && removedIds.Contains(item.SoundGroupId)))
        {
            sound.SoundGroupId = string.Empty;
            if (instances.TryGetValue(sound.Id, out var soundInstance)) soundInstance.Set("SoundGroup", DynValue.Nil);
            propertyChanged?.Invoke(sound, "SoundGroup", DynValue.Nil);
        }
        foreach (var effect in KnownNodes().Where(item => item.Type == "CompressorSoundEffect" && removedIds.Contains(item.CompressorSideChainId)))
        {
            effect.CompressorSideChainId = string.Empty;
            if (instances.TryGetValue(effect.Id, out var effectInstance)) effectInstance.Set("SideChain", DynValue.Nil);
            propertyChanged?.Invoke(effect, "SideChain", DynValue.Nil);
        }
        foreach (var box in KnownNodes().Where(item => item.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") && removedIds.Contains(item.Adornee)))
        {
            box.Adornee = string.Empty;
            if (instances.TryGetValue(box.Id, out var instance)) instance.Set("Adornee", DynValue.Nil);
            propertyChanged?.Invoke(box, "Adornee", DynValue.Nil);
        }
        foreach (var model in KnownNodes().Where(item => item.Type == "Model" && removedIds.Contains(item.PrimaryPart)))
        {
            if (removedNodes.FirstOrDefault(item => item.Id == model.PrimaryPart) is { } deletedPrimaryPart)
            {
                model.Position = (float[])deletedPrimaryPart.Position.Clone();
                model.Rotation = (float[])deletedPrimaryPart.Rotation.Clone();
                model.HasModelPivot = true;
            }
            model.PrimaryPart = string.Empty;
            if (instances.TryGetValue(model.Id, out var instance)) instance.Set("PrimaryPart", DynValue.Nil);
            propertyChanged?.Invoke(model, "PrimaryPart", DynValue.Nil);
        }
        foreach (var constraint in KnownNodes().Where(item => item.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint"))
        {
            if (removedIds.Contains(constraint.Part0))
            {
                constraint.Part0 = string.Empty;
                if (instances.TryGetValue(constraint.Id, out var instance)) instance.Set("Part0", DynValue.Nil);
                propertyChanged?.Invoke(constraint, "Part0", DynValue.Nil);
            }
            if (removedIds.Contains(constraint.Part1))
            {
                constraint.Part1 = string.Empty;
                if (instances.TryGetValue(constraint.Id, out var instance)) instance.Set("Part1", DynValue.Nil);
                propertyChanged?.Invoke(constraint, "Part1", DynValue.Nil);
            }
        }
        foreach (var constraint in KnownNodes().Where(item => item.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink"))
        {
            if (removedIds.Contains(constraint.Attachment0))
            {
                constraint.Attachment0 = string.Empty;
                if (instances.TryGetValue(constraint.Id, out var instance)) instance.Set("Attachment0", DynValue.Nil);
                propertyChanged?.Invoke(constraint, "Attachment0", DynValue.Nil);
            }
            if (removedIds.Contains(constraint.Attachment1))
            {
                constraint.Attachment1 = string.Empty;
                if (instances.TryGetValue(constraint.Id, out var instance)) instance.Set("Attachment1", DynValue.Nil);
                propertyChanged?.Invoke(constraint, "Attachment1", DynValue.Nil);
            }
        }
        StopScripts(removedScriptIds);
        if (notifyParentChange) propertyChanged?.Invoke(node, "Parent", DynValue.Nil);
        return DynValue.Nil;
    }

    private void StopScripts(HashSet<string> scriptIds)
    {
        if (scriptIds.Count == 0) return;
        foreach (var id in scriptIds)
        {
            queuedScriptIds.Remove(id);
            startedScriptIds.Remove(id);
        }
        foreach (var execution in activeScripts.Where(item => scriptIds.Contains(item.GameScript.Id)))
            bindableInvocationScopes.Remove(execution.Coroutine);
        activeScripts.RemoveAll(execution => scriptIds.Contains(execution.GameScript.Id));
        scheduledTasks.RemoveAll(item => scriptIds.Contains(item.Owner.Id));
        RemoveCallbacksOwnedBy(scriptIds, touchedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, guiActivatedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, toolEquippedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, toolUnequippedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, toolDeactivatedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, guiTextChangedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, promptTriggeredCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, clickDetectorCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, humanoidDiedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, humanoidHealthChangedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, valueChangedCallbacks);
        foreach (var callbacks in bindableEventCallbacks.Values)
            foreach (var connection in callbacks.Where(item => scriptIds.Contains(item.Owner.Id)).ToArray())
            {
                connection.Disconnect();
                callbacks.Remove(connection);
            }
        foreach (var key in remoteFunctionCallbacks.Where(pair => scriptIds.Contains(pair.Value.GameScript.Id)).Select(pair => pair.Key).ToArray())
            remoteFunctionCallbacks.Remove(key);
        foreach (var callbacks in attributeCallbacks.Values)
            foreach (var connection in callbacks.Where(item => scriptIds.Contains(item.Owner.Id)).ToArray())
            {
                connection.Disconnect();
                callbacks.Remove(connection);
            }
        foreach (var callbacks in tagCallbacks.Values)
            foreach (var connection in callbacks.Where(item => scriptIds.Contains(item.Owner.Id)).ToArray())
            {
                connection.Disconnect();
                callbacks.Remove(connection);
            }
        foreach (var callbacks in runServiceCallbacks.Values)
            foreach (var connection in callbacks.Where(item => scriptIds.Contains(item.Owner.Id)).ToArray())
            {
                connection.Disconnect();
                callbacks.Remove(connection);
            }
        foreach (var id in bindableFunctionCallbacks.Where(item => scriptIds.Contains(item.Value.GameScript.Id)).Select(item => item.Key).ToArray())
            bindableFunctionCallbacks.Remove(id);
    }

    private void StopInactiveScripts(SceneNode source)
    {
        var inactive = new[] { source }.Concat(GameProjectStore.Flatten(source.Children))
            .Where(node => SceneNodeTypes.IsBaseScript(node) && (!node.Enabled || !IsScriptAttachedForExecution(node)))
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        StopScripts(inactive);
    }

    private static void RemoveCallbacksOwnedBy(HashSet<string> scriptIds, Dictionary<string, List<ScriptCallback>> registry)
    {
        foreach (var (nodeId, callbacks) in registry.ToArray())
        {
            foreach (var callback in callbacks.Where(callback => scriptIds.Contains(callback.GameScript.Id)).ToArray())
            {
                callback.Connection?.Set("Connected", DynValue.NewBoolean(false));
                callbacks.Remove(callback);
            }
            if (callbacks.Count == 0) registry.Remove(nodeId);
        }
    }

    private static void DisconnectCallbacksForNode(Dictionary<string, List<ScriptCallback>> registry, string nodeId)
    {
        if (!registry.Remove(nodeId, out var callbacks)) return;
        foreach (var callback in callbacks)
            callback.Connection?.Set("Connected", DynValue.NewBoolean(false));
    }

    private static void DisconnectCallbacksInRegistry(Dictionary<string, List<ScriptCallback>> registry)
    {
        foreach (var callbacks in registry.Values)
            foreach (var callback in callbacks)
                callback.Connection?.Set("Connected", DynValue.NewBoolean(false));
    }

    private SceneNode[] KnownNodes()
    {
        var tree = GameProjectStore.Flatten(project);
        var treeIds = tree.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return tree.Concat(createdNodes.Values.Where(node => !treeIds.Contains(node.Id))).ToArray();
    }

    private bool TrySetParent(SceneNode source, DynValue value, bool updateLinks = true)
    {
        if (source.Type is "Workspace" or "Players" or "PlayerGui" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui"
            || source.Type == "Camera" && project.Workspace.Children.Any(camera => camera.Type == "Camera" && camera.Id == source.Id)) return false;
        var wasAttached = IsAttached(source);
        if (value.Type == DataType.Nil)
        {
            var detachedOldParent = FindParentNode(source);
            var oldCollection = FindSourceCollection(source);
            if (oldCollection is null) return createdNodes.ContainsKey(source.Id);
            oldCollection.Remove(source);
            createdNodes[source.Id] = source;
            if (wasAttached) FireSubtreeTagChanges(source, false);
            if (updateLinks) RefreshLinksForMove(source, detachedOldParent, null);
            StopInactiveScripts(source);
            return true;
        }
        var parent = ReadNodeArgument(value);
        if (parent is null) return false;
        var parentIsAttached = GameProjectStore.Flatten(project).Any(node => node.Id == parent.Id);
        if (parentIsAttached ? !GameProjectStore.CanParentNode(project, source, parent)
            : !createdNodes.ContainsKey(parent.Id) || !GameProjectStore.CanParentDetachedNode(source, parent)) return false;
        var oldParent = FindParentNode(source);
        if (ReferenceEquals(oldParent, parent)) return true;
        var sourceCollection = FindSourceCollection(source);
        if (sourceCollection is null && !createdNodes.ContainsKey(source.Id)) return false;
        sourceCollection?.Remove(source);
        parent.Children.Add(source);
        var isAttached = IsAttached(source);
        if (wasAttached != isAttached) FireSubtreeTagChanges(source, isAttached);
        if (updateLinks) RefreshLinksForMove(source, oldParent, parent);
        StopInactiveScripts(source);
        QueueRunnableScripts(source);
        return true;
    }

    private DynValue ChangePage(SceneNode layout, int requestedIndex)
    {
        if (layout.Type != "UIPageLayout" || FindParentNode(layout) is not { } parent) return DynValue.Nil;
        var pages = parent.Children.Where(IsGuiObject).ToArray();
        if (layout.SortOrder == "Name") pages = pages.OrderBy(page => page.Name, StringComparer.Ordinal).ToArray();
        else pages = pages.OrderBy(page => page.LayoutOrder).ToArray();
        if (pages.Length == 0) return DynValue.Nil;
        var next = layout.Circular
            ? ((requestedIndex - 1) % pages.Length + pages.Length) % pages.Length + 1
            : Math.Clamp(requestedIndex, 1, pages.Length);
        if (next == layout.CurrentPageIndex) return DynValue.Nil;
        SelectPage(layout, pages, next);
        return DynValue.Nil;
    }

    private DynValue ChangePage(SceneNode layout, SceneNode page)
    {
        if (layout.Type != "UIPageLayout" || FindParentNode(layout) is not { } parent) return DynValue.Nil;
        var pages = parent.Children.Where(IsGuiObject).ToArray();
        if (layout.SortOrder == "Name") pages = pages.OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray();
        else pages = pages.OrderBy(candidate => candidate.LayoutOrder).ToArray();
        var index = Array.FindIndex(pages, candidate => candidate.Id == page.Id);
        if (index < 0) return DynValue.Nil;
        var next = index + 1;
        if (next == layout.CurrentPageIndex) return DynValue.Nil;
        SelectPage(layout, pages, next);
        return DynValue.Nil;
    }

    private void SelectPage(SceneNode layout, SceneNode[] pages, int index)
    {
        layout.CurrentPageIndex = index;
        if (instances.TryGetValue(layout.Id, out var instance))
        {
            instance.Set("CurrentPageIndex", DynValue.NewNumber(index));
            instance.Set("CurrentPage", GetPageInstanceValue(pages, index));
        }
        propertyChanged?.Invoke(layout, "CurrentPageIndex", DynValue.NewNumber(index));
    }

    private DynValue GetPageInstanceValue(SceneNode[] pages, int index)
    {
        var page = index > 0 && index <= pages.Length ? pages[index - 1] : null;
        return page is not null && instances.TryGetValue(page.Id, out var pageInstance)
            ? DynValue.NewTable(pageInstance) : DynValue.Nil;
    }

    private SceneNode[] GetPageNodes(SceneNode layout)
    {
        if (FindParentNode(layout) is not { } parent) return [];
        var pages = parent.Children.Where(IsGuiObject).ToArray();
        return layout.SortOrder == "Name"
            ? pages.OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray()
            : pages.OrderBy(candidate => candidate.LayoutOrder).ToArray();
    }

    private SceneNode? GetCurrentPage(SceneNode layout)
    {
        var pages = GetPageNodes(layout);
        return layout.CurrentPageIndex > 0 && layout.CurrentPageIndex <= pages.Length
            ? pages[layout.CurrentPageIndex - 1] : null;
    }

    private bool SetCurrentPage(SceneNode layout, SceneNode? page)
    {
        if (layout.Type != "UIPageLayout" || page is null) return false;
        var pages = GetPageNodes(layout);
        var index = Array.FindIndex(pages, candidate => candidate.Id == page.Id);
        if (index < 0) return false;
        layout.CurrentPageIndex = index + 1;
        if (instances.TryGetValue(layout.Id, out var instance))
        {
            instance.Set("CurrentPageIndex", DynValue.NewNumber(index + 1));
            instance.Set("CurrentPage", GetPageInstanceValue(pages, index + 1));
        }
        return true;
    }

    private SceneNode? FindParentNode(SceneNode target)
    {
        if (ReferenceEquals(target, project.Workspace) || ReferenceEquals(target, project.Players) || ReferenceEquals(target, project.Teams) || ReferenceEquals(target, project.Lighting) || ReferenceEquals(target, project.SoundService) || ReferenceEquals(target, project.ReplicatedStorage) || ReferenceEquals(target, project.ServerScriptService) || ReferenceEquals(target, project.ServerStorage) || ReferenceEquals(target, project.StarterPack) || ReferenceEquals(target, project.StarterPlayer) || ReferenceEquals(target, project.Gui)) return null;
        return FindParentNode(project.Workspace, target) ?? FindParentNode(project.Players, target) ?? FindParentNode(project.Teams, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.SoundService, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.StarterPack, target) ?? FindParentNode(project.StarterPlayer, target) ?? FindParentNode(project.Gui, target)
            ?? createdNodes.Values.Select(node => FindParentNode(node, target)).FirstOrDefault(parent => parent is not null);
    }

    private static SceneNode? FindParentNode(SceneNode parent, SceneNode target)
    {
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child, target) || child.Id == target.Id) return parent;
            var nested = FindParentNode(child, target);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static List<SceneNode>? FindParentCollection(SceneNode parent, SceneNode target)
    {
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child, target) || child.Id == target.Id) return parent.Children;
            var nested = FindParentCollection(child, target);
            if (nested is not null) return nested;
        }
        return null;
    }

    private List<SceneNode>? FindSourceCollection(SceneNode target) =>
        FindParentCollection(project.Workspace, target) ?? FindParentCollection(project.Players, target) ?? FindParentCollection(project.Teams, target) ?? FindParentCollection(project.Lighting, target) ?? FindParentCollection(project.SoundService, target) ?? FindParentCollection(project.ReplicatedStorage, target) ?? FindParentCollection(project.ServerScriptService, target) ?? FindParentCollection(project.ServerStorage, target) ?? FindParentCollection(project.StarterPack, target) ?? FindParentCollection(project.StarterPlayer, target) ?? FindParentCollection(project.Gui, target)
        ?? createdNodes.Values.Select(node => FindParentCollection(node, target)).FirstOrDefault(collection => collection is not null);

    private void RefreshLinksForMove(SceneNode source, SceneNode? oldParent, SceneNode? newParent)
    {
        if (instances.TryGetValue(source.Id, out var instance))
            instance.Set("Parent", newParent is not null && instances.TryGetValue(newParent.Id, out var newParentTable)
                ? DynValue.NewTable(newParentTable) : DynValue.Nil);
        if (oldParent is not null) RefreshParentChildren(oldParent);
        if (newParent is not null && !ReferenceEquals(oldParent, newParent)) RefreshParentChildren(newParent);
        foreach (var model in KnownNodes().Where(item => item.Type == "Model" && item.PrimaryPart.Length > 0))
        {
            if (SceneModelService.ContainsPrimaryPart(model, model.PrimaryPart)) continue;
            model.PrimaryPart = string.Empty;
            model.HasModelPivot = true;
            if (instances.TryGetValue(model.Id, out var modelInstance)) modelInstance.Set("PrimaryPart", DynValue.Nil);
            propertyChanged?.Invoke(model, "PrimaryPart", DynValue.Nil);
        }
    }

    private void RefreshParentChildren(SceneNode parent)
    {
        if (!instances.TryGetValue(parent.Id, out var table)) return;
        if (linkedChildKeys.TryGetValue(parent.Id, out var oldKeys))
            foreach (var key in oldKeys) table.Set(key, DynValue.Nil);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var children = new Table(script);
        var index = 1;
        foreach (var child in parent.Children)
        {
            if (!instances.TryGetValue(child.Id, out var childTable)) continue;
            var childValue = DynValue.NewTable(childTable);
            children.Set(index++, childValue);
            children.Set(child.Name, childValue);
            children.Set(child.Id, childValue);
            if (!IsReservedBindableMember(parent, child.Name))
            {
                table.Set(child.Name, childValue);
                keys.Add(child.Name);
            }
        }
        table.Set("Children", DynValue.NewTable(children));
        linkedChildKeys[parent.Id] = keys;
    }

    private void UpdateParentLinks()
    {
        var nodes = KnownNodes();
        foreach (var (parentId, keys) in linkedChildKeys)
            if (instances.TryGetValue(parentId, out var oldParent))
                foreach (var key in keys) oldParent.Set(key, DynValue.Nil);
        linkedChildKeys.Clear();
        foreach (var node in nodes)
        {
            if (!instances.TryGetValue(node.Id, out var instance)) continue;
            instance.Set("Children", DynValue.NewTable(new Table(script)));
            var parent = FindParentNode(node);
            instance.Set("Parent", parent is not null && instances.TryGetValue(parent.Id, out var parentTable) ? DynValue.NewTable(parentTable) : DynValue.Nil);
            if (node.Type == "Player") instance.Set("Character", GetCharacterValue(node));
            if (node.Type == "Player") { instance.Set("Team", GetTeamValue(node)); instance.Set("TeamColor", ColorValue(FindNode(node.Team)?.TeamColor ?? "#FFFFFF")); }
            if (node.Type is "Seat" or "VehicleSeat") instance.Set("Occupant", GetOccupantValue(node));
            if (node.Type == "VehicleSeat")
            {
                instance.Set("ThrottleFloat", DynValue.NewNumber(node.VehicleSeatThrottleFloat));
                instance.Set("SteerFloat", DynValue.NewNumber(node.VehicleSeatSteerFloat));
                instance.Set("AreHingesDetected", DynValue.NewNumber(node.VehicleSeatAreHingesDetected));
            }
            if (node.Type == "Model") instance.Set("PrimaryPart", GetModelPrimaryPartReference(node));
            if (FindNode(node.Subject) is { } subjectNode && IsBasePart(subjectNode) && instances.TryGetValue(subjectNode.Id, out var subject)) instance.Set("Subject", DynValue.NewTable(subject));
            else if (!IsBasePart(ReadNodeArgument(instance.Get("Subject")))) instance.Set("Subject", DynValue.Nil);
            if (node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")
            {
                instance.Set("Part0", GetPartReferenceValue(node.Part0));
                instance.Set("Part1", GetPartReferenceValue(node.Part1));
            }
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink")
            {
                instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink") instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            }
        }
        foreach (var node in nodes)
        {
            if (!instances.TryGetValue(node.Id, out var instance) || instance.Get("Children").Type != DataType.Table) continue;
            var childTable = instance.Get("Children").Table;
            var index = 1;
            foreach (var child in node.Children)
            {
                if (!instances.TryGetValue(child.Id, out var childInstance)) continue;
                var childValue = DynValue.NewTable(childInstance);
                childTable.Set(index++, childValue);
                childTable.Set(child.Name, childValue);
                childTable.Set(child.Id, childValue);
                if (!IsReservedBindableMember(node, child.Name))
                {
                    instance.Set(child.Name, childValue);
                    if (!linkedChildKeys.TryGetValue(node.Id, out var keys)) linkedChildKeys[node.Id] = keys = new HashSet<string>(StringComparer.Ordinal);
                    keys.Add(child.Name);
                }
            }
        }
    }

    private void WriteInstanceProperty(SceneNode node, Table instance, string normalizedProperty)
    {
        if (node.Type == "Sparkles" && normalizedProperty is "color" or "sparklecolor")
        {
            instance.Set("Color", ColorValue(node.Color));
            instance.Set("SparkleColor", ColorValue(node.Color));
            return;
        }
        if (normalizedProperty is "velocity" or "assemblylinearvelocity")
        {
            instance.Set("Velocity", VectorValue(node.Velocity));
            instance.Set("AssemblyLinearVelocity", VectorValue(node.Velocity));
            return;
        }
        if (normalizedProperty == "assemblyangularvelocity")
        {
            instance.Set("AssemblyAngularVelocity", VectorValue(node.AssemblyAngularVelocity));
            return;
        }
        if (normalizedProperty == "mode" && node.Type is "AlignPosition" or "AlignOrientation")
        {
            instance.Set("Mode", DynValue.NewString(node.AlignmentMode));
            return;
        }
        if (node.Type == "UIShadow" && normalizedProperty is ("blurradius" or "offset" or "spread" or "inset" or "showbehindparent" or "mode" or "zindex"))
        {
            SetShadowInstanceProperties(node, instance);
            return;
        }
        if (normalizedProperty is "mode" or "cameramode")
        {
            var cameraMode = DynValue.NewString(node.CameraMode);
            instance.Set("CameraMode", cameraMode);
            instance.Set("Mode", cameraMode);
            return;
        }
        var propertyName = normalizedProperty switch
        {
            "name" => "Name",
            "archivable" => "Archivable",
            "position" => "Position",
            "rotation" => "Rotation",
            "tooltip" => "ToolTip",
            "requireshandle" => "RequiresHandle",
            "canbedropped" => "CanBeDropped",
            "manualactivationonly" => "ManualActivationOnly",
            "gripposition" => "GripPosition",
            "griprotation" => "GripRotation",
            "size" => "Size",
            "color" => "Color",
            "tintcolor" => "TintColor",
            "contrast" => "Contrast",
            "saturation" => "Saturation",
            "backgroundcolor3" => "BackgroundColor3",
            "color3" => "Color3",
            "surfacecolor3" => "SurfaceColor3",
            "surfacetransparency" => "SurfaceTransparency",
            "linethickness" => "LineThickness",
            "adornee" => "Adornee",
            "sparklecolor" => "SparkleColor",
            "secondarycolor" => "SecondaryColor",
            "decay" => "Decay",
            "density" => "Density",
            "haze" => "Haze",
            "glare" => "Glare",
            "offset" => "Offset",
            "fillcolor" => "FillColor",
            "outlinecolor" => "OutlineColor",
            "startcolor" => "StartColor",
            "endcolor" => "EndColor",
            "paddingleft" => "PaddingLeft",
            "paddingtop" => "PaddingTop",
            "paddingright" => "PaddingRight",
            "paddingbottom" => "PaddingBottom",
            "padding" => "Padding",
            "scale" => "Scale",
            "cellsize" => "CellSize",
            "cellpadding" => "CellPadding",
            "canvasposition" => "CanvasPosition",
            "canvassize" => "CanvasSize",
            "scrollbarthickness" => "ScrollBarThickness",
            "scrollingenabled" => "ScrollingEnabled",
            "scrollingdirection" => "ScrollingDirection",
            "automaticcanvassize" => "AutomaticCanvasSize",
            "automaticsize" => "AutomaticSize",
            "clipsdescendants" => "ClipsDescendants",
            "anchorpoint" => "AnchorPoint",
            "filldirection" => "FillDirection",
            "filldirectionmaxcells" => "FillDirectionMaxCells",
            "filltransparency" => "FillTransparency",
            "outlinetransparency" => "OutlineTransparency",
            "shape" => "Shape",
            "face" => "Face",
            "studspertileu" => "StudsPerTileU",
            "studspertilev" => "StudsPerTileV",
            "offsetstudsu" => "OffsetStudsU",
            "offsetstudsv" => "OffsetStudsV",
            "material" => "Material",
            "transparency" => "Transparency",
            "backgroundtransparency" => "BackgroundTransparency",
            "bordercolor3" => "BorderColor3",
            "bordersizepixel" => "BorderSizePixel",
            "grouptransparency" => "GroupTransparency",
            "texttransparency" => "TextTransparency",
            "imagetransparency" => "ImageTransparency",
            "imagecolor3" => "ImageColor3",
            "scaletype" => "ScaleType",
            "imagerectoffset" => "ImageRectOffset",
            "imagerectsize" => "ImageRectSize",
            "slicecenter" => "SliceCenter",
            "slicescale" => "SliceScale",
            "minsize" => "MinSize",
            "maxsize" => "MaxSize",
            "textscaled" => "TextScaled",
            "textwrapped" => "TextWrapped",
            "multiline" => "MultiLine",
            "texteditable" => "TextEditable",
            "cleartextonfocus" => "ClearTextOnFocus",
            "mintextsize" => "MinTextSize",
            "maxtextsize" => "MaxTextSize",
            "enabled" => "Enabled",
            "brightness" => "Brightness",
            "range" => "Range",
            "heat" => "Heat",
            "risevelocity" => "RiseVelocity",
            "opacity" => "Opacity",
            "timescale" => "TimeScale",
            "blastradius" => "BlastRadius",
            "blastpressure" => "BlastPressure",
            "destroyjointradiuspercent" => "DestroyJointRadiusPercent",
            "angle" => "Angle",
            "actiontext" => "ActionText",
            "objecttext" => "ObjectText",
            "holdduration" => "HoldDuration",
            "keyboardkeycode" => "KeyboardKeyCode",
            "maxactivationdistance" => "MaxActivationDistance",
            "maxdistance" => "MaxDistance",
            "alwaystop" => "AlwaysOnTop",
            "pixelsperstud" => "PixelsPerStud",
            "video" => "Video",
            "volume" => "Volume",
            "playbackspeed" => "PlaybackSpeed",
            "octave" => "Octave",
            "delay" => "Delay",
            "feedback" => "Feedback",
            "drylevel" => "DryLevel",
            "wetlevel" => "WetLevel",
            "depth" => "Depth",
            "mix" => "Mix",
            "rate" => "Rate",
            "duty" => "Duty",
            "frequency" => "Frequency",
            "decaytime" => "DecayTime",
            "diffusion" => "Diffusion",
            "soundgroup" => "SoundGroup",
            "sidechain" => "SideChain",
            "looped" => "Looped",
            "playing" => "Playing",
            "rolloffmindistance" => "RollOffMinDistance",
            "rolloffmaxdistance" => "RollOffMaxDistance",
            "texture" => "Texture",
            "colormap" => "ColorMap",
            "normalmap" => "NormalMap",
            "roughnessmap" => "RoughnessMap",
            "metalnessmap" => "MetalnessMap",
            "emissivemaskcontent" => "EmissiveMaskContent",
            "emissivetint" => "EmissiveTint",
            "emissivestrength" => "EmissiveStrength",
            "text" => "Text",
            "placeholdertext" => "PlaceholderText",
            "textcolor" => "TextColor",
            "visible" => "Visible",
            "zindex" => "ZIndex",
            "displayorder" => "DisplayOrder",
            "resetonspawn" => "ResetOnSpawn",
            "layoutorder" => "LayoutOrder",
            "anchored" => "Anchored",
            "cancollide" => "CanCollide",
            "cantouch" => "CanTouch",
            "canquery" => "CanQuery",
            "castshadow" => "CastShadow",
            "gravity" => "Gravity",
            "ambient" => "Ambient",
            "clocktime" => "ClockTime",
            "globalshadows" => "GlobalShadows",
            "automanagecharacter" => "AutoManageCharacter",
            "character" => "Character",
            "team" => "Team",
            "teamcolor" => "TeamColor",
            "autoassignable" => "AutoAssignable",
            "headcolor3" => "HeadColor3",
            "torsocolor3" => "TorsoColor3",
            "leftarmcolor3" => "LeftArmColor3",
            "rightarmcolor3" => "RightArmColor3",
            "leftlegcolor3" => "LeftLegColor3",
            "rightlegcolor3" => "RightLegColor3",
            "neutral" => "Neutral",
            "allowteamchangeontouch" => "AllowTeamChangeOnTouch",
            "duration" => "Duration",
            "occupant" => "Occupant",
            "disabled" => "Disabled",
            "headsupdisplay" => "HeadsUpDisplay",
            "maxspeed" => "MaxSpeed",
            "turnspeed" => "TurnSpeed",
            "throttlefloat" => "ThrottleFloat",
            "steerfloat" => "SteerFloat",
            "arehingesdetected" => "AreHingesDetected",
            "health" => "Health",
            "maxhealth" => "MaxHealth",
            "walkspeed" => "WalkSpeed",
            "jumppower" => "JumpPower",
            "value" => "Value",
            "asset" => "Asset",
            "image" => "Image",
            "hoverimage" => "HoverImage",
            "pressedimage" => "PressedImage",
            "currentcamera" => "CurrentCamera",
            "subject" => "Subject",
            "part0" => "Part0",
            "part1" => "Part1",
            "part0offsetposition" => "Part0OffsetPosition",
            "part0offsetrotation" => "Part0OffsetRotation",
            "part1offsetposition" => "Part1OffsetPosition",
            "part1offsetrotation" => "Part1OffsetRotation",
            "primarypart" => "PrimaryPart",
            "attachment0" => "Attachment0",
            "attachment1" => "Attachment1",
            "force" => "Force",
            "torque" => "Torque",
            "relativeto" => "RelativeTo",
            "vectorvelocity" => "VectorVelocity",
            "linedirection" => "LineDirection",
            "linevelocity" => "LineVelocity",
            "planevelocity" => "PlaneVelocity",
            "primarytangentaxis" => "PrimaryTangentAxis",
            "secondarytangentaxis" => "SecondaryTangentAxis",
            "velocityconstraintmode" => "VelocityConstraintMode",
            "forcelimitsenabled" => "ForceLimitsEnabled",
            "reactionforceenabled" => "ReactionForceEnabled",
            "maxforce" => "MaxForce",
            "forcelimitmode" => "ForceLimitMode",
            "maxaxesforce" => "MaxAxesForce",
            "maxplanaraxesforce" => "MaxPlanarAxesForce",
            "angularvelocity" => "AngularVelocity",
            "assemblyangularvelocity" => "AssemblyAngularVelocity",
            "maxtorque" => "MaxTorque",
            "maxangularvelocity" => "MaxAngularVelocity",
            "reactiontorqueenabled" => "ReactionTorqueEnabled",
            "maxvelocity" => "MaxVelocity",
            "responsiveness" => "Responsiveness",
            "rigidityenabled" => "RigidityEnabled",
            "width0" => "Width0",
            "width1" => "Width1",
            "fieldofview" => "FieldOfView",
            "cornerradius" => "CornerRadius",
            "topleftradius" => "TopLeftRadius",
            "toprightradius" => "TopRightRadius",
            "bottomrightradius" => "BottomRightRadius",
            "bottomleftradius" => "BottomLeftRadius",
            "strokecolor" => "StrokeColor",
            "strokethickness" => "StrokeThickness",
            "textsize" => "TextSize",
            "layoutspacing" => "LayoutSpacing",
            "tablecolumns" => "TableColumns",
            "aspectratio" => "AspectRatio",
            "aspecttype" => "AspectType",
            "dominantaxis" => "DominantAxis",
            "layoutdirection" => "LayoutDirection",
            "horizontalalignment" => "HorizontalAlignment",
            "verticalalignment" => "VerticalAlignment",
            "sortorder" => "SortOrder",
            "currentpageindex" => "CurrentPageIndex",
            "currentpage" => "CurrentPage",
            "circular" => "Circular",
            "flexmode" => "FlexMode",
            "growratio" => "GrowRatio",
            "shrinkratio" => "ShrinkRatio",
            "itemlinealignment" => "ItemLineAlignment",
            "skyboxfront" => "SkyboxFront",
            "skyboxback" => "SkyboxBack",
            "skyboxleft" => "SkyboxLeft",
            "skyboxright" => "SkyboxRight",
            "skyboxtop" => "SkyboxTop",
            "skyboxbottom" => "SkyboxBottom",
            "skyboxft" => "SkyboxFt",
            "skyboxbk" => "SkyboxBk",
            "skyboxlf" => "SkyboxLf",
            "skyboxrt" => "SkyboxRt",
            "skyboxup" => "SkyboxUp",
            "skyboxdn" => "SkyboxDn",
            _ => null
        };
        if (propertyName is not null) instance.Set(propertyName, GetPropertyValue(node, propertyName));
    }

    private DynValue SetProperty(ScriptExecutionContext context, CallbackArguments args)
    {
        if (args.Count < 3) return DynValue.Nil;
        var node = ReadNodeArgument(args[0]);
        return node is null ? DynValue.Nil : SetPropertyValue(node, args[1].CastToString() ?? string.Empty, args[2]);
    }

    private DynValue GetProperty(ScriptExecutionContext context, CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        var node = ReadNodeArgument(args[0]);
        return node is null ? DynValue.Nil : GetPropertyValue(node, args[1].CastToString() ?? string.Empty);
    }

    private DynValue SetPropertyValue(SceneNode node, string property, DynValue value)
    {
        var normalized = property.ToLowerInvariant();
        if (node.Type == "PathfindingLink" && normalized is "label" or "isbidirectional")
        {
            if (normalized == "label")
            {
                if (value.Type != DataType.String) return DynValue.Nil;
                node.PathfindingLinkLabel = value.String;
            }
            else
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.PathfindingLinkBidirectional = value.Boolean;
            }
            var canonical = normalized == "label" ? "Label" : "IsBidirectional";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var linkInstance)) linkInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "PathfindingModifier" && normalized is "label" or "passthrough")
        {
            if (normalized == "label")
            {
                if (value.Type != DataType.String) return DynValue.Nil;
                node.PathfindingModifierLabel = value.String;
            }
            else
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.PathfindingPassThrough = value.Boolean;
            }
            var canonical = normalized == "label" ? "Label" : "PassThrough";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var modifierInstance)) modifierInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type is ("VectorForce" or "AlignPosition") && normalized == "applyatcenterofmass")
        {
            if (value.Type != DataType.Boolean) return DynValue.Nil;
            node.ApplyAtCenterOfMass = value.Boolean;
            var current = DynValue.NewBoolean(node.ApplyAtCenterOfMass);
            if (instances.TryGetValue(node.Id, out var vectorForceInstance)) vectorForceInstance.Set("ApplyAtCenterOfMass", current);
            propertyChanged?.Invoke(node, "ApplyAtCenterOfMass", current);
            return DynValue.Nil;
        }
        if (node.Type == "LineForce" && normalized is ("magnitude" or "maxforce" or "inversesquarelaw" or "reactionforceenabled" or "applyatcenterofmass"))
        {
            if (normalized is "magnitude" or "maxforce")
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)Math.Clamp(value.Number, 0, 1000000);
                if (normalized == "magnitude") node.LineForceMagnitude = number;
                else node.LineForceMaxForce = number;
            }
            else
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                if (normalized == "inversesquarelaw") node.LineForceInverseSquareLaw = value.Boolean;
                else if (normalized == "reactionforceenabled") node.LineForceReactionForceEnabled = value.Boolean;
                else node.ApplyAtCenterOfMass = value.Boolean;
            }
            var canonical = normalized switch
            {
                "magnitude" => "Magnitude",
                "maxforce" => "MaxForce",
                "inversesquarelaw" => "InverseSquareLaw",
                "reactionforceenabled" => "ReactionForceEnabled",
                _ => "ApplyAtCenterOfMass"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var lineForceInstance)) lineForceInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BodyVelocity" && normalized is ("velocity" or "maxforce" or "p"))
        {
            if (normalized is "velocity" or "maxforce")
            {
                var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
                vector = normalized == "velocity"
                    ? vector.Select(component => Math.Clamp(component, -10000, 10000)).ToArray()
                    : vector.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                if (normalized == "velocity") node.BodyVelocityTarget = vector; else node.BodyVelocityMaxForce = vector;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                node.BodyVelocityP = (float)Math.Clamp(value.Number, 0, 1000000);
            }
            var canonical = normalized switch { "velocity" => "Velocity", "maxforce" => "MaxForce", _ => "P" };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var bodyVelocityInstance)) bodyVelocityInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BodyPosition" && normalized is ("position" or "maxforce" or "p" or "d"))
        {
            if (normalized is "position" or "maxforce")
            {
                var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
                vector = normalized == "position"
                    ? vector.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray()
                    : vector.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                if (normalized == "position") node.BodyPositionTarget = vector; else node.BodyPositionMaxForce = vector;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                if (normalized == "p") node.BodyPositionP = (float)Math.Clamp(value.Number, 0, 1000000);
                else node.BodyPositionD = (float)Math.Clamp(value.Number, 0, 1000000);
            }
            var canonical = normalized switch { "position" => "Position", "maxforce" => "MaxForce", "p" => "P", _ => "D" };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var bodyPositionInstance)) bodyPositionInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BodyGyro" && normalized is ("rotation" or "maxtorque" or "p" or "d"))
        {
            if (normalized is "rotation" or "maxtorque")
            {
                var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
                vector = normalized == "rotation"
                    ? vector.Select(component => Math.Clamp(component, -360000, 360000)).ToArray()
                    : vector.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                if (normalized == "rotation") node.BodyGyroTargetRotation = vector; else node.BodyGyroMaxTorque = vector;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                if (normalized == "p") node.BodyGyroP = (float)Math.Clamp(value.Number, 0, 1000000);
                else node.BodyGyroD = (float)Math.Clamp(value.Number, 0, 1000000);
            }
            var canonical = normalized switch { "rotation" => "Rotation", "maxtorque" => "MaxTorque", "p" => "P", _ => "D" };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var bodyGyroInstance)) bodyGyroInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BodyAngularVelocity" && normalized is ("angularvelocity" or "maxtorque" or "p"))
        {
            if (normalized is "angularvelocity" or "maxtorque")
            {
                var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
                vector = normalized == "angularvelocity"
                    ? vector.Select(component => Math.Clamp(component, -10000, 10000)).ToArray()
                    : vector.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                if (normalized == "angularvelocity") node.BodyAngularVelocityTarget = vector; else node.BodyAngularVelocityMaxTorque = vector;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                node.BodyAngularVelocityP = (float)Math.Clamp(value.Number, 0, 1000000);
            }
            var canonical = normalized switch { "angularvelocity" => "AngularVelocity", "maxtorque" => "MaxTorque", _ => "P" };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var bodyAngularVelocityInstance)) bodyAngularVelocityInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "RocketPropulsion" && normalized is ("target" or "targetoffset" or "targetradius" or "cartoonfactor" or "maxspeed" or "maxthrust" or "maxtorque" or "thrustd" or "thrustp" or "turnd" or "turnp"))
        {
            if (normalized == "target")
            {
                var target = ReadNodeArgument(value);
                if (value.Type == DataType.Nil) target = null;
                else if (target is null || !SceneNodeTypes.IsBasePart(target)) return DynValue.Nil;
                var targetId = target?.Id ?? string.Empty;
                if (node.RocketPropulsionTarget != targetId) node.RocketPropulsionReachedTarget = false;
                node.RocketPropulsionTarget = targetId;
            }
            else if (normalized is "targetoffset" or "maxtorque")
            {
                var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
                vector = vector.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                if (normalized == "targetoffset")
                {
                    if (!node.RocketPropulsionTargetOffset.SequenceEqual(vector)) node.RocketPropulsionReachedTarget = false;
                    node.RocketPropulsionTargetOffset = vector;
                }
                else node.RocketPropulsionMaxTorque = vector.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)Math.Clamp(value.Number, 0, 1000000);
                switch (normalized)
                {
                    case "targetradius": node.RocketPropulsionTargetRadius = number; break;
                    case "cartoonfactor": node.RocketPropulsionCartoonFactor = Math.Clamp(number, 0, 1); break;
                    case "maxspeed": node.RocketPropulsionMaxSpeed = Math.Min(number, 10000); break;
                    case "maxthrust": node.RocketPropulsionMaxThrust = number; break;
                    case "thrustd": node.RocketPropulsionThrustD = number; break;
                    case "thrustp": node.RocketPropulsionThrustP = number; break;
                    case "turnd": node.RocketPropulsionTurnD = number; break;
                    case "turnp": node.RocketPropulsionTurnP = number; break;
                }
            }
            var canonical = normalized switch
            {
                "target" => "Target", "targetoffset" => "TargetOffset", "targetradius" => "TargetRadius", "cartoonfactor" => "CartoonFactor",
                "maxspeed" => "MaxSpeed", "maxthrust" => "MaxThrust", "maxtorque" => "MaxTorque", "thrustd" => "ThrustD",
                "thrustp" => "ThrustP", "turnd" => "TurnD", _ => "TurnP"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var rocketInstance)) rocketInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type is "VectorForce" or "BodyForce" && normalized is ("force" or "relativeto"))
        {
            if (normalized == "force")
            {
                var force = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || !force.All(float.IsFinite)) return DynValue.Nil;
                node.Force = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
            }
            else
            {
                if (node.Type != "VectorForce") return DynValue.Nil;
                if (value.Type != DataType.String || value.String is not ("World" or "Attachment0" or "Attachment1")) return DynValue.Nil;
                node.RelativeTo = value.String;
            }
            var canonical = normalized == "force" ? "Force" : "RelativeTo";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var forceInstance)) forceInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BodyThrust" && normalized is ("force" or "location"))
        {
            var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
            if (value.Type != DataType.Table || vector.Length != 3 || !vector.All(float.IsFinite)) return DynValue.Nil;
            vector = vector.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
            if (normalized == "force") node.BodyThrustForce = vector; else node.BodyThrustLocation = vector;
            var canonical = normalized == "force" ? "Force" : "Location";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var thrustInstance)) thrustInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "VehicleSeat" && normalized == "torque")
        {
            if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
            node.VehicleSeatTorque = (float)Math.Clamp(value.Number, 0, 1000000);
            var current = GetPropertyValue(node, "Torque");
            if (instances.TryGetValue(node.Id, out var vehicleSeatInstance)) vehicleSeatInstance.Set("Torque", current);
            propertyChanged?.Invoke(node, "Torque", current);
            return DynValue.Nil;
        }
        if (node.Type == "Torque" && normalized is ("torque" or "relativeto"))
        {
            if (normalized == "torque")
            {
                var torque = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || !torque.All(float.IsFinite)) return DynValue.Nil;
                node.TorqueTarget = torque.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
            }
            else
            {
                if (value.Type != DataType.String || value.String is not ("World" or "Attachment0" or "Attachment1")) return DynValue.Nil;
                node.RelativeTo = value.String;
            }
            var canonical = normalized == "torque" ? "Torque" : "RelativeTo";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var torqueInstance)) torqueInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "SpringConstraint" && normalized is ("damping" or "freelength" or "limitsenabled" or "maxforce" or "maxlength" or "minlength" or "stiffness"))
        {
            if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.SpringLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "damping": node.SpringDamping = Math.Clamp(number, 0, 10000); break;
                    case "freelength": node.SpringFreeLength = Math.Clamp(number, 0, 10000); break;
                    case "maxforce": node.SpringMaxForce = Math.Clamp(number, 0, 1000000); break;
                    case "maxlength": node.SpringMaxLength = Math.Clamp(number, node.SpringMinLength, 10000); break;
                    case "minlength": node.SpringMinLength = Math.Clamp(number, 0, node.SpringMaxLength); break;
                    case "stiffness": node.SpringStiffness = Math.Clamp(number, 0, 100000); break;
                }
            }
            var canonical = normalized switch
            {
                "limitsenabled" => "LimitsEnabled",
                "freelength" => "FreeLength",
                "maxlength" => "MaxLength",
                "minlength" => "MinLength",
                _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var springInstance)) springInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "TorsionSpringConstraint" && normalized is ("coils" or "damping" or "limitsenabled" or "maxangle" or "maxtorque" or "radius" or "restitution" or "stiffness"))
        {
            if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.TorsionSpringLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "coils": node.TorsionSpringCoils = Math.Clamp(number, 0, 8); break;
                    case "damping": node.TorsionSpringDamping = Math.Clamp(number, 0, 100000); break;
                    case "maxangle": node.TorsionSpringMaxAngle = Math.Clamp(number, 0, 180); break;
                    case "maxtorque": node.TorsionSpringMaxTorque = Math.Clamp(number, 0, 1000000); break;
                    case "radius": node.TorsionSpringRadius = Math.Clamp(number, 0, 100); break;
                    case "restitution": node.TorsionSpringRestitution = Math.Clamp(number, 0, 1); break;
                    case "stiffness": node.TorsionSpringStiffness = Math.Clamp(number, 0, 100000); break;
                }
            }
            var canonical = normalized == "limitsenabled" ? "LimitsEnabled" : char.ToUpperInvariant(normalized[0]) + normalized[1..];
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var torsionInstance)) torsionInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "RopeConstraint" && normalized is ("length" or "restitution" or "winchenabled" or "winchforce" or "winchresponsiveness" or "winchspeed" or "winchtarget"))
        {
            if (normalized == "winchenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.RopeWinchEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "length": node.RopeLength = Math.Clamp(number, 0, 10000); break;
                    case "restitution": node.RopeRestitution = Math.Clamp(number, 0, 1); break;
                    case "winchforce": node.RopeWinchForce = Math.Clamp(number, 0, 1000000); break;
                    case "winchresponsiveness": node.RopeWinchResponsiveness = Math.Clamp(number, 0, 200); break;
                    case "winchspeed": node.RopeWinchSpeed = Math.Clamp(number, 0, 10000); break;
                    case "winchtarget": node.RopeWinchTarget = Math.Clamp(number, 0, 10000); break;
                }
            }
            var canonical = normalized switch
            {
                "length" => "Length",
                "restitution" => "Restitution",
                "winchenabled" => "WinchEnabled",
                "winchforce" => "WinchForce",
                "winchresponsiveness" => "WinchResponsiveness",
                "winchspeed" => "WinchSpeed",
                _ => "WinchTarget"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var ropeInstance)) ropeInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "RodConstraint" && normalized is ("length" or "limitangle0" or "limitangle1" or "limitsenabled" or "thickness"))
        {
            if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.RodLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "length": node.RodLength = Math.Clamp(number, 0, 10000); break;
                    case "limitangle0": node.RodLimitAngle0 = Math.Clamp(number, 0, 180); break;
                    case "limitangle1": node.RodLimitAngle1 = Math.Clamp(number, 0, 180); break;
                    case "thickness": node.RodThickness = Math.Clamp(number, 0, 100); break;
                }
            }
            var canonical = normalized switch
            {
                "length" => "Length",
                "limitangle0" => "LimitAngle0",
                "limitangle1" => "LimitAngle1",
                "limitsenabled" => "LimitsEnabled",
                _ => "Thickness"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var rodInstance)) rodInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "BallSocketConstraint" && normalized is ("limitsenabled" or "maxfrictiontorque" or "radius" or "restitution" or "upperangle" or "twistlimitsenabled" or "twistlowerangle" or "twistupperangle"))
        {
            if (normalized is "limitsenabled" or "twistlimitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                if (normalized == "limitsenabled") node.BallSocketLimitsEnabled = value.Boolean;
                else node.BallSocketTwistLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "maxfrictiontorque": node.BallSocketMaxFrictionTorque = Math.Clamp(number, 0, 1000000); break;
                    case "radius": node.BallSocketRadius = Math.Clamp(number, 0, 100); break;
                    case "restitution": node.BallSocketRestitution = Math.Clamp(number, 0, 1); break;
                    case "upperangle": node.BallSocketUpperAngle = Math.Clamp(number, 0, 180); break;
                    case "twistlowerangle": node.BallSocketTwistLowerAngle = Math.Clamp(number, -180, node.BallSocketTwistUpperAngle); break;
                    case "twistupperangle": node.BallSocketTwistUpperAngle = Math.Clamp(number, node.BallSocketTwistLowerAngle, 180); break;
                }
            }
            var canonical = normalized switch
            {
                "limitsenabled" => "LimitsEnabled",
                "maxfrictiontorque" => "MaxFrictionTorque",
                "radius" => "Radius",
                "restitution" => "Restitution",
                "upperangle" => "UpperAngle",
                "twistlimitsenabled" => "TwistLimitsEnabled",
                "twistlowerangle" => "TwistLowerAngle",
                _ => "TwistUpperAngle"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var ballSocketInstance)) ballSocketInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "UniversalConstraint" && normalized is ("limitsenabled" or "maxangle" or "radius" or "restitution"))
        {
            if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.UniversalLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "maxangle": node.UniversalMaxAngle = Math.Clamp(number, 0, 180); break;
                    case "radius": node.UniversalRadius = Math.Clamp(number, 0, 100); break;
                    case "restitution": node.UniversalRestitution = Math.Clamp(number, 0, 1); break;
                }
            }
            var canonical = normalized == "limitsenabled" ? "LimitsEnabled" : char.ToUpperInvariant(normalized[0]) + normalized[1..];
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var universalInstance)) universalInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "HingeConstraint" && normalized is ("actuatortype" or "angularresponsiveness" or "angularspeed" or "angularvelocity" or "limitsenabled" or "lowerangle" or "motormaxacceleration" or "motormaxtorque" or "radius" or "restitution" or "servomaxtorque" or "targetangle" or "upperangle"))
        {
            if (normalized == "actuatortype")
            {
                if (value.Type != DataType.String || value.String is not ("None" or "Motor" or "Servo")) return DynValue.Nil;
                node.HingeActuatorType = value.String;
            }
            else if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.HingeLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "angularresponsiveness": node.HingeAngularResponsiveness = Math.Clamp(number, 0, 200); break;
                    case "angularspeed": node.HingeAngularSpeed = Math.Clamp(number, 0, 1000); break;
                    case "angularvelocity": node.HingeAngularVelocity = Math.Clamp(number, -1000, 1000); break;
                    case "lowerangle": node.HingeLowerAngle = Math.Clamp(number, -180, node.HingeUpperAngle); break;
                    case "motormaxacceleration": node.HingeMotorMaxAcceleration = Math.Clamp(number, 0, 100000); break;
                    case "motormaxtorque": node.HingeMotorMaxTorque = Math.Clamp(number, 0, 1000000); break;
                    case "radius": node.HingeRadius = Math.Clamp(number, 0, 100); break;
                    case "restitution": node.HingeRestitution = Math.Clamp(number, 0, 1); break;
                    case "servomaxtorque": node.HingeServoMaxTorque = Math.Clamp(number, 0, 1000000); break;
                    case "targetangle": node.HingeTargetAngle = Math.Clamp(number, -360000, 360000); break;
                    case "upperangle": node.HingeUpperAngle = Math.Clamp(number, node.HingeLowerAngle, 180); break;
                }
            }
            var canonical = normalized switch
            {
                "actuatortype" => "ActuatorType",
                "angularresponsiveness" => "AngularResponsiveness",
                "angularspeed" => "AngularSpeed",
                "angularvelocity" => "AngularVelocity",
                "limitsenabled" => "LimitsEnabled",
                "lowerangle" => "LowerAngle",
                "motormaxacceleration" => "MotorMaxAcceleration",
                "motormaxtorque" => "MotorMaxTorque",
                "servomaxtorque" => "ServoMaxTorque",
                "targetangle" => "TargetAngle",
                "upperangle" => "UpperAngle",
                _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var hingeInstance)) hingeInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type is ("PrismaticConstraint" or "CylindricalConstraint") && normalized is ("actuatortype" or "limitsenabled" or "linearresponsiveness" or "lowerlimit" or "motormaxacceleration" or "motormaxforce" or "restitution" or "servomaxforce" or "size" or "speed" or "targetposition" or "upperlimit" or "velocity"))
        {
            if (normalized == "actuatortype")
            {
                if (value.Type != DataType.String || value.String is not ("None" or "Motor" or "Servo")) return DynValue.Nil;
                node.PrismaticActuatorType = value.String;
            }
            else if (normalized == "limitsenabled")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                node.PrismaticLimitsEnabled = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "linearresponsiveness": node.PrismaticLinearResponsiveness = Math.Clamp(number, 0, 200); break;
                    case "lowerlimit":
                        node.PrismaticLowerLimit = Math.Clamp(number, -10000, 10000);
                        if (node.PrismaticLowerLimit > node.PrismaticUpperLimit)
                            node.PrismaticUpperLimit = node.PrismaticLowerLimit;
                        break;
                    case "motormaxacceleration": node.PrismaticMotorMaxAcceleration = Math.Clamp(number, 0, 100000); break;
                    case "motormaxforce": node.PrismaticMotorMaxForce = Math.Clamp(number, 0, 1000000); break;
                    case "restitution": node.PrismaticRestitution = Math.Clamp(number, 0, 1); break;
                    case "servomaxforce": node.PrismaticServoMaxForce = Math.Clamp(number, 0, 1000000); break;
                    case "size": node.PrismaticSize = Math.Clamp(number, 0, 100); break;
                    case "speed": node.PrismaticSpeed = Math.Clamp(number, 0, 10000); break;
                    case "targetposition": node.PrismaticTargetPosition = Math.Clamp(number, -10000, 10000); break;
                    case "upperlimit":
                        node.PrismaticUpperLimit = Math.Clamp(number, -10000, 10000);
                        if (node.PrismaticUpperLimit < node.PrismaticLowerLimit)
                            node.PrismaticLowerLimit = node.PrismaticUpperLimit;
                        break;
                    case "velocity": node.PrismaticVelocity = Math.Clamp(number, -10000, 10000); break;
                }
            }
            var canonical = normalized switch
            {
                "actuatortype" => "ActuatorType",
                "limitsenabled" => "LimitsEnabled",
                "linearresponsiveness" => "LinearResponsiveness",
                "lowerlimit" => "LowerLimit",
                "motormaxacceleration" => "MotorMaxAcceleration",
                "motormaxforce" => "MotorMaxForce",
                "servomaxforce" => "ServoMaxForce",
                "targetposition" => "TargetPosition",
                "upperlimit" => "UpperLimit",
                _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var prismaticInstance)) prismaticInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "CylindricalConstraint" && normalized is ("angularactuatortype" or "angularlimitsenabled" or "angularrestitution" or "angularresponsiveness" or "angularspeed" or "angularvelocity" or "inclinationangle" or "lowerangle" or "motormaxangularacceleration" or "motormaxtorque" or "rotationaxisvisible" or "servomaxtorque" or "targetangle" or "upperangle"))
        {
            if (normalized is "angularactuatortype")
            {
                if (value.Type != DataType.String || value.String is not ("None" or "Motor" or "Servo")) return DynValue.Nil;
                node.HingeActuatorType = value.String;
            }
            else if (normalized is "angularlimitsenabled" or "rotationaxisvisible")
            {
                if (value.Type != DataType.Boolean) return DynValue.Nil;
                if (normalized == "angularlimitsenabled") node.HingeLimitsEnabled = value.Boolean;
                else node.CylindricalRotationAxisVisible = value.Boolean;
            }
            else
            {
                if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                var number = (float)value.Number;
                switch (normalized)
                {
                    case "angularrestitution": node.HingeRestitution = Math.Clamp(number, 0, 1); break;
                    case "angularresponsiveness": node.HingeAngularResponsiveness = Math.Clamp(number, 0, 200); break;
                    case "angularspeed": node.HingeAngularSpeed = Math.Clamp(number, 0, 1000); break;
                    case "angularvelocity": node.HingeAngularVelocity = Math.Clamp(number, -1000, 1000); break;
                    case "inclinationangle": node.CylindricalInclinationAngle = Math.Clamp(number, -180, 180); break;
                    case "lowerangle": node.HingeLowerAngle = Math.Clamp(number, -180, node.HingeUpperAngle); break;
                    case "motormaxangularacceleration": node.HingeMotorMaxAcceleration = Math.Clamp(number, 0, 100000); break;
                    case "motormaxtorque": node.HingeMotorMaxTorque = Math.Clamp(number, 0, 1000000); break;
                    case "servomaxtorque": node.HingeServoMaxTorque = Math.Clamp(number, 0, 1000000); break;
                    case "targetangle": node.HingeTargetAngle = Math.Clamp(number, -180, 180); break;
                    case "upperangle": node.HingeUpperAngle = Math.Clamp(number, node.HingeLowerAngle, 180); break;
                }
            }
            var canonical = normalized switch
            {
                "angularactuatortype" => "AngularActuatorType",
                "angularlimitsenabled" => "AngularLimitsEnabled",
                "angularrestitution" => "AngularRestitution",
                "angularresponsiveness" => "AngularResponsiveness",
                "angularspeed" => "AngularSpeed",
                "angularvelocity" => "AngularVelocity",
                "inclinationangle" => "InclinationAngle",
                "lowerangle" => "LowerAngle",
                "motormaxangularacceleration" => "MotorMaxAngularAcceleration",
                "motormaxtorque" => "MotorMaxTorque",
                "rotationaxisvisible" => "RotationAxisVisible",
                "servomaxtorque" => "ServoMaxTorque",
                "targetangle" => "TargetAngle",
                _ => "UpperAngle"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var cylindricalInstance)) cylindricalInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "AngularVelocity" && normalized is ("angularvelocity" or "relativeto" or "maxtorque" or "reactiontorqueenabled"))
        {
            switch (normalized)
            {
                case "angularvelocity":
                    var angularVelocity = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                    if (value.Type != DataType.Table || !angularVelocity.All(float.IsFinite)) return DynValue.Nil;
                    node.AngularVelocityTarget = angularVelocity.Select(component => Math.Clamp(component, -1000, 1000)).ToArray();
                    break;
                case "relativeto":
                    if (value.Type != DataType.String || value.String is not ("World" or "Attachment0" or "Attachment1")) return DynValue.Nil;
                    node.RelativeTo = value.String;
                    break;
                case "maxtorque":
                    if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxTorque = (float)Math.Clamp(value.Number, 0, 1000000);
                    break;
                case "reactiontorqueenabled":
                    if (value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ReactionTorqueEnabled = value.Boolean;
                    break;
            }
            var canonical = normalized switch
            {
                "angularvelocity" => "AngularVelocity",
                "relativeto" => "RelativeTo",
                "maxtorque" => "MaxTorque",
                _ => "ReactionTorqueEnabled"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var angularInstance)) angularInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "LinearVelocity" && normalized is ("vectorvelocity" or "linedirection" or "linevelocity" or "planevelocity" or "primarytangentaxis" or "secondarytangentaxis" or "velocityconstraintmode" or "forcelimitsenabled" or "maxforce" or "forcelimitmode" or "maxaxesforce" or "maxplanaraxesforce" or "relativeto" or "reactionforceenabled"))
        {
            if (!TrySetLinearVelocityProperty(node, normalized, value)) return DynValue.Nil;
            var canonical = normalized switch
            {
                "vectorvelocity" => "VectorVelocity",
                "linedirection" => "LineDirection",
                "linevelocity" => "LineVelocity",
                "planevelocity" => "PlaneVelocity",
                "primarytangentaxis" => "PrimaryTangentAxis",
                "secondarytangentaxis" => "SecondaryTangentAxis",
                "velocityconstraintmode" => "VelocityConstraintMode",
                "forcelimitsenabled" => "ForceLimitsEnabled",
                "maxforce" => "MaxForce",
                "forcelimitmode" => "ForceLimitMode",
                "maxaxesforce" => "MaxAxesForce",
                "maxplanaraxesforce" => "MaxPlanarAxesForce",
                "reactionforceenabled" => "ReactionForceEnabled",
                _ => "RelativeTo"
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var velocityInstance)) velocityInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "Trail" && normalized is ("color" or "transparency" or "widthscale" or "lifetime" or "minlength" or "maxlength" or "facecamera" or "texturemode" or "texturelength"))
        {
            if (!TrySetTrailProperty(node, normalized, value)) return DynValue.Nil;
            var canonical = normalized switch
            {
                "widthscale" => "WidthScale",
                "minlength" => "MinLength",
                "maxlength" => "MaxLength",
                "facecamera" => "FaceCamera",
                "texturemode" => "TextureMode",
                "texturelength" => "TextureLength",
                _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
            };
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var trailInstance)) trailInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "ParticleEmitter" && normalized is ("size" or "color" or "transparency" or "rate" or "speed" or "lifetime" or "spreadangle" or "emissiondirection"))
        {
            if (!TrySetParticleEmitterProperty(node, normalized, value)) return DynValue.Nil;
            var canonical = char.ToUpperInvariant(normalized[0]) + normalized[1..];
            if (instances.TryGetValue(node.Id, out var emitterInstance))
                emitterInstance.Set(canonical, GetPropertyValue(node, canonical));
            propertyChanged?.Invoke(node, canonical, GetPropertyValue(node, canonical));
            return DynValue.Nil;
        }
        if (normalized == "oninvoke" && node.Type == "BindableFunction")
        {
            SetBindableFunctionCallback(node, value);
            return DynValue.Nil;
        }
        if (node.Type == "RemoteFunction" && normalized is "onserverinvoke" or "onclientinvoke")
        {
            SetRemoteFunctionCallback(node, normalized == "onserverinvoke" ? "OnServerInvoke" : "OnClientInvoke", value);
            return DynValue.Nil;
        }
        if (normalized == "value")
        {
            if (!TrySetValueObjectValue(node, value, out var changed)) return DynValue.Nil;
            if (instances.TryGetValue(node.Id, out var valueInstance)) valueInstance.Set("Value", GetValueObjectValue(node));
            if (changed)
            {
                var currentValue = GetValueObjectValue(node);
                propertyChanged?.Invoke(node, "Value", currentValue);
                FireValueChanged(node, currentValue);
            }
            return DynValue.Nil;
        }
        if (normalized == "parent")
        {
            if (!TrySetParent(node, value)) return DynValue.Nil;
        }
        else
        {
            switch (normalized)
            {
                case "position":
                    if (!HasPositionProperty(node)) return DynValue.Nil;
                    SetPositionProperty(node, value);
                    break;
                case "transformposition":
                    if (node.Type is not ("Weld" or "Motor6D") || value.Type != DataType.Table) return DynValue.Nil;
                    node.MotorTransformPosition = LimitVector(ReadVector(value, node.MotorTransformPosition), -1000000, 1000000);
                    break;
                case "part0offsetposition":
                case "part0offsetrotation":
                case "part1offsetposition":
                case "part1offsetrotation":
                    if (node.Type != "Motor6D" || value.Type != DataType.Table) return DynValue.Nil;
                    var isPositionOffset = normalized.EndsWith("position", StringComparison.Ordinal);
                    var isPart0Offset = normalized.StartsWith("part0", StringComparison.Ordinal);
                    var priorOffset = (isPart0Offset, isPositionOffset) switch
                    {
                        (true, true) => node.Part0OffsetPosition,
                        (true, false) => node.Part0OffsetRotation,
                        (false, true) => node.Part1OffsetPosition,
                        _ => node.Part1OffsetRotation
                    };
                    var motorOffset = LimitVector(ReadVector(value, priorOffset), isPositionOffset ? -1000000 : -360000, isPositionOffset ? 1000000 : 360000);
                    if (isPart0Offset && isPositionOffset) node.Part0OffsetPosition = motorOffset;
                    else if (isPart0Offset) node.Part0OffsetRotation = motorOffset;
                    else if (isPositionOffset) node.Part1OffsetPosition = motorOffset;
                    else node.Part1OffsetRotation = motorOffset;
                    break;
                case "transformrotation":
                    if (node.Type != "Motor6D" || value.Type != DataType.Table) return DynValue.Nil;
                    node.MotorTransformRotation = LimitVector(ReadVector(value, node.MotorTransformRotation), -360000, 360000);
                    break;
                case "tooltip":
                    if (node.Type != "Tool" || value.Type != DataType.String) return DynValue.Nil;
                    node.ToolTip = value.String;
                    break;
                case "requireshandle": if (node.Type != "Tool" || value.Type != DataType.Boolean) return DynValue.Nil; node.RequiresHandle = value.Boolean; break;
                case "canbedropped": if (node.Type != "Tool" || value.Type != DataType.Boolean) return DynValue.Nil; node.CanBeDropped = value.Boolean; break;
                case "manualactivationonly": if (node.Type != "Tool" || value.Type != DataType.Boolean) return DynValue.Nil; node.ManualActivationOnly = value.Boolean; break;
                case "gripposition": if (node.Type != "Tool" || value.Type != DataType.Table) return DynValue.Nil; node.GripPosition = ReadVector(value, node.GripPosition); break;
                case "griprotation": if (node.Type != "Tool" || value.Type != DataType.Table) return DynValue.Nil; node.GripRotation = ReadVector(value, node.GripRotation).Select(component => Math.Clamp(component, -360000, 360000)).ToArray(); break;
                case "rotation":
                    if (!HasRotationProperty(node)) return DynValue.Nil;
                    if (node.Type == "AlignOrientation") node.AlignOrientationRotation = LimitVector(ReadVector(value, node.AlignOrientationRotation), -360000, 360000);
                    else if (node.Type == "Model")
                    {
                        var pending = CapturePendingModelDescendantWrites(node);
                        if (SceneModelService.RotateTo(node, LimitVector(ReadVector(value, node.Rotation), -360000, 360000))) UpdateModelDescendantInstances(node, true, pending, (child, propertyName) => propertyChanged?.Invoke(child, propertyName, GetPropertyValue(child, propertyName)));
                    }
                    else node.Rotation = LimitVector(ReadVector(value, node.Rotation), -360000, 360000);
                    break;
                case "size":
                    if (!HasSizeProperty(node)) return DynValue.Nil;
                    SetSizeProperty(node, value);
                    break;
                case "color":
                    if (SceneNodeTypes.IsSurfaceAppearance(node)) node.SurfaceAppearanceColor = ReadColor(value, node.SurfaceAppearanceColor);
                    else node.Color = ReadColor(value, node.Color);
                    break;
                case "tintcolor": if (node.Type != "ColorCorrectionEffect") return DynValue.Nil; node.TintColor = ReadColor(value, node.TintColor); break;
                case "contrast": if (node.Type != "ColorCorrectionEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.ColorCorrectionContrast = (float)Math.Clamp(value.Number, -1, 1); break;
                case "saturation": if (node.Type != "ColorCorrectionEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.ColorCorrectionSaturation = (float)Math.Clamp(value.Number, -1, 1); break;
                case "colormap": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.String) return DynValue.Nil; node.SurfaceAppearanceColorMap = value.String; break;
                case "normalmap": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.String) return DynValue.Nil; node.SurfaceAppearanceNormalMap = value.String; break;
                case "roughnessmap": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.String) return DynValue.Nil; node.SurfaceAppearanceRoughnessMap = value.String; break;
                case "metalnessmap": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.String) return DynValue.Nil; node.SurfaceAppearanceMetalnessMap = value.String; break;
                case "emissivemaskcontent": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.String) return DynValue.Nil; node.SurfaceAppearanceEmissiveMask = value.String; break;
                case "emissivetint": if (!SceneNodeTypes.IsSurfaceAppearance(node)) return DynValue.Nil; node.SurfaceAppearanceEmissiveTint = ReadColor(value, node.SurfaceAppearanceEmissiveTint); break;
                case "emissivestrength": if (!SceneNodeTypes.IsSurfaceAppearance(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.SurfaceAppearanceEmissiveStrength = (float)Math.Clamp(value.Number, 0, 100); break;
                case "backgroundcolor3":
                    if (!IsGuiObject(node) || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.Color = ReadColor(value, node.Color);
                    break;
                case "color3":
                    if (node.Type is not ("SelectionBox" or "SelectionSphere") || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.Color = ReadColor(value, node.Color);
                    break;
                case "surfacecolor3":
                    if (node.Type is not ("SelectionBox" or "SelectionSphere") || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.SurfaceColor3 = ReadColor(value, node.SurfaceColor3);
                    break;
                case "surfacetransparency":
                    if (node.Type is not ("SelectionBox" or "SelectionSphere") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.SurfaceTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "linethickness":
                    if (node.Type != "SelectionBox" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.LineThickness = (float)Math.Clamp(value.Number, 0, 10);
                    break;
                case "adornee":
                    if (node.Type is not ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui")) return DynValue.Nil;
                    var adornment = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && adornment is null || adornment is not null && !IsBasePart(adornment) && ((node.Type is "BillboardGui" or "SurfaceGui") || adornment.Type != "Model")) return DynValue.Nil;
                    node.Adornee = adornment?.Id ?? string.Empty;
                    break;
                case "sparklecolor": if (node.Type != "Sparkles" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.Color = ReadColor(value, node.Color); break;
                case "secondarycolor": if (node.Type != "Fire" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.SecondaryColor = ReadColor(value, node.SecondaryColor); break;
                case "decay": if (node.Type != "Atmosphere" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.AtmosphereDecay = ReadColor(value, node.AtmosphereDecay); break;
                case "density":
                    if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    if (node.Type == "Atmosphere") node.Density = (float)Math.Clamp(value.Number, 0, 1);
                    else if (node.Type == "Clouds") node.CloudDensity = (float)Math.Clamp(value.Number, 0, 1);
                    else if (node.Type == "ReverbSoundEffect") node.ReverbDensity = (float)Math.Clamp(value.Number, 0, 1);
                    else return DynValue.Nil;
                    break;
                case "cover": if (node.Type != "Clouds" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CloudCover = (float)Math.Clamp(value.Number, 0, 1); break;
                case "haze": if (node.Type != "Atmosphere" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Haze = (float)Math.Clamp(value.Number, 0, 10); break;
                case "glare": if (node.Type != "Atmosphere" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Glare = (float)Math.Clamp(value.Number, 0, 10); break;
                case "offset":
                    if (node.Type == "Atmosphere" && value.Type == DataType.Number && double.IsFinite(value.Number)) node.Offset = (float)Math.Clamp(value.Number, -1, 1);
                    else if (node.Type == "UIShadow" && TryReadUDim2(value, out var shadowOffsetScale, out var shadowOffsetValue))
                    {
                        node.ShadowOffsetScale = UIShadowService.NormalizePair(shadowOffsetScale);
                        node.ShadowOffset = UIShadowService.NormalizePair(shadowOffsetValue);
                    }
                    else return DynValue.Nil;
                    break;
                case "fillcolor": if (node.Type != "Highlight") return DynValue.Nil; node.FillColor = ReadColor(value, node.FillColor); break;
                case "outlinecolor": if (node.Type != "Highlight") return DynValue.Nil; node.OutlineColor = ReadColor(value, node.OutlineColor); break;
                case "startcolor": if (node.Type != "UIGradient" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.StartColor = ReadColor(value, node.StartColor); break;
                case "endcolor": if (node.Type != "UIGradient" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.EndColor = ReadColor(value, node.EndColor); break;
                case "paddingleft":
                case "paddingtop":
                case "paddingright":
                case "paddingbottom":
                    if (node.Type != "UIPadding" || !TryReadUDim(value, out var paddingValue)) return DynValue.Nil;
                    if (normalized == "paddingleft") node.PaddingLeft = paddingValue;
                    else if (normalized == "paddingtop") node.PaddingTop = paddingValue;
                    else if (normalized == "paddingright") node.PaddingRight = paddingValue;
                    else node.PaddingBottom = paddingValue;
                    break;
                case "scale":
                    if (node.Type != "UIScale" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Scale = (float)Math.Clamp(value.Number, .01, 100);
                    break;
                case "cellsize":
                case "cellpadding":
                    if (node.Type != "UIGridLayout" || !TryReadUDim2(value, out var cellScale, out var cellOffset)) return DynValue.Nil;
                    if (normalized == "cellsize") { node.CellSizeScale = cellScale; node.CellSize = cellOffset; }
                    else { node.CellPaddingScale = cellScale; node.CellPadding = cellOffset; }
                    break;
                case "canvasposition":
                    if (node.Type != "ScrollingFrame" || !TryReadVector2(value, out var canvasPosition)) return DynValue.Nil;
                    node.CanvasPosition = canvasPosition.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 1000000) : 0).ToArray();
                    break;
                case "canvassize":
                    if (node.Type != "ScrollingFrame" || !TryReadUDim2(value, out var canvasSizeScale, out var canvasSize)) return DynValue.Nil;
                    node.CanvasSizeScale = canvasSizeScale.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 100) : 0).ToArray();
                    node.CanvasSize = canvasSize.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 1000000) : 0).ToArray();
                    break;
                case "scrollbarthickness":
                    if (node.Type != "ScrollingFrame" || value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number != Math.Truncate(value.Number)) return DynValue.Nil;
                    node.ScrollBarThickness = (int)Math.Clamp(value.Number, 0, 100);
                    break;
                case "scrollingenabled":
                    if (node.Type != "ScrollingFrame" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ScrollingEnabled = value.Boolean;
                    break;
                case "scrollingdirection":
                    if (node.Type != "ScrollingFrame" || value.CastToString() is not ("X" or "Y" or "XY")) return DynValue.Nil;
                    node.ScrollingDirection = value.String;
                    break;
                case "automaticcanvassize":
                    if (node.Type != "ScrollingFrame" || value.CastToString() is not ("None" or "X" or "Y" or "XY")) return DynValue.Nil;
                    node.AutomaticCanvasSize = value.String;
                    break;
                case "automaticsize":
                    if (!IsGuiObject(node) || node.Type == "ScrollingFrame" || value.CastToString() is not ("None" or "X" or "Y" or "XY")) return DynValue.Nil;
                    node.AutomaticSize = value.String;
                    break;
                case "clipsdescendants":
                    if (!(IsGuiObject(node) || node.Type is "BillboardGui" or "SurfaceGui") || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ClipsDescendants = value.Boolean;
                    break;
                case "anchorpoint":
                    if (!IsGuiObject(node) || !TryReadVector2(value, out var anchorPoint)) return DynValue.Nil;
                    node.AnchorPoint = [Math.Clamp(anchorPoint[0], 0, 1), Math.Clamp(anchorPoint[1], 0, 1)];
                    break;
                case "filldirection":
                    if (node.Type is not ("UIGridLayout" or "UIListLayout") || value.CastToString() is not ("Horizontal" or "Vertical")) return DynValue.Nil;
                    if (node.Type == "UIListLayout") node.LayoutDirection = value.CastToString()!;
                    else node.FillDirection = value.CastToString()!;
                    break;
                case "padding":
                    if (node.Type != "UIListLayout" || !TryReadUDim(value, out var listPadding)) return DynValue.Nil;
                    node.ListPaddingScale = Math.Clamp(listPadding[0], 0, 100);
                    node.LayoutSpacing = Math.Clamp(listPadding[1], 0, 1000000);
                    break;
                case "filldirectionmaxcells":
                    if (node.Type != "UIGridLayout" || value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number != Math.Truncate(value.Number)) return DynValue.Nil;
                    node.FillDirectionMaxCells = (int)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "filltransparency":
                    if (node.Type != "Highlight" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.FillTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "outlinetransparency":
                    if (node.Type != "Highlight" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.OutlineTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "shape":
                    if (node.Type != "Part" || value.CastToString() is not { } shape || shape is not ("Block" or "Ball" or "Cylinder")) return DynValue.Nil;
                    node.Shape = shape;
                    break;
                case "face":
                    if (node.Type is not ("Decal" or "Texture" or "SurfaceLight" or "SurfaceGui") || !SceneNodeTypes.IsFace(value.CastToString())) return DynValue.Nil;
                    node.Face = value.String;
                    break;
                case "studspertileu":
                    if (node.Type != "Texture" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.StudsPerTileU = (float)Math.Clamp(value.Number, .05, 1000);
                    break;
                case "studspertilev":
                    if (node.Type != "Texture" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.StudsPerTileV = (float)Math.Clamp(value.Number, .05, 1000);
                    break;
                case "offsetstudsu":
                    if (node.Type != "Texture" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.OffsetStudsU = (float)Math.Clamp(value.Number, -10000, 10000);
                    break;
                case "offsetstudsv":
                    if (node.Type != "Texture" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.OffsetStudsV = (float)Math.Clamp(value.Number, -10000, 10000);
                    break;
                case "material":
                    if (!IsBasePart(node) || value.CastToString() is not { } material || !SceneNodeTypes.IsMaterial(material)) return DynValue.Nil;
                    node.Material = material;
                    break;
                case "transparency":
                    if ((!IsBasePart(node) && node.Type is not ("Decal" or "Texture" or "Beam" or "SelectionBox" or "SelectionSphere" or "UIShadow")) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Transparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "backgroundtransparency":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.BackgroundTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "bordercolor3":
                    if (!IsGuiObject(node) || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.BorderColor3 = ReadColor(value, node.BorderColor3);
                    break;
                case "bordersizepixel":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.BorderSizePixel = (int)Math.Clamp(Math.Round(value.Number), 0, 64);
                    break;
                case "grouptransparency":
                    if (node.Type != "UICanvasGroup" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.GroupTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "texttransparency":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.TextTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "imagetransparency":
                    if (!IsGuiObject(node) || node.Type is not ("ImageLabel" or "ImageButton") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.ImageTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "imagecolor3":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.ImageColor3 = ReadColor(value, node.ImageColor3);
                    break;
                case "scaletype":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || value.Type != DataType.String || value.String is not ("Stretch" or "Fit" or "Crop" or "Tile" or "Slice")) return DynValue.Nil;
                    node.ScaleType = value.String;
                    break;
                case "imagerectoffset":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || !TryReadVector2(value, out var imageRectOffset)) return DynValue.Nil;
                    node.ImageRectOffset = imageRectOffset.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    break;
                case "imagerectsize":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || !TryReadVector2(value, out var imageRectSize)) return DynValue.Nil;
                    node.ImageRectSize = imageRectSize.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    break;
                case "slicecenter":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || !TryReadRect(value, out var sliceCenter)) return DynValue.Nil;
                    node.SliceCenter = NormalizeSliceCenter(sliceCenter);
                    break;
                case "slicescale":
                    if (node.Type is not ("ImageLabel" or "ImageButton") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.SliceScale = (float)Math.Clamp(value.Number, .01, 100);
                    break;
                case "hoverimage": if (node.Type != "ImageButton") return DynValue.Nil; node.HoverImage = value.CastToString() ?? string.Empty; break;
                case "pressedimage": if (node.Type != "ImageButton") return DynValue.Nil; node.PressedImage = value.CastToString() ?? string.Empty; break;
                case "minsize":
                    if (node.Type != "UISizeConstraint" || !TryReadVector2(value, out var minSize)) return DynValue.Nil;
                    var priorMaxSize = instances.TryGetValue(node.Id, out var minSizeInstance)
                        && TryReadVector2(minSizeInstance.Get("MaxSize"), out var pendingMaxSize) ? pendingMaxSize : node.MaxSize;
                    node.MinSize = minSize.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    node.MaxSize = [Math.Max(priorMaxSize[0], node.MinSize[0]), Math.Max(priorMaxSize[1], node.MinSize[1])];
                    break;
                case "maxsize":
                    if (node.Type != "UISizeConstraint" || !TryReadVector2(value, out var maxSize)) return DynValue.Nil;
                    var priorMinSize = instances.TryGetValue(node.Id, out var maxSizeInstance)
                        && TryReadVector2(maxSizeInstance.Get("MinSize"), out var pendingMinSize) ? pendingMinSize : node.MinSize;
                    node.MaxSize = [Math.Clamp(maxSize[0], Math.Clamp(priorMinSize[0], 0, 1000000), 1000000), Math.Clamp(maxSize[1], Math.Clamp(priorMinSize[1], 0, 1000000), 1000000)];
                    break;
                case "textscaled":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.TextScaled = value.Boolean;
                    break;
                case "textwrapped":
                    if (node.Type is not ("TextLabel" or "TextButton") || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.TextWrapped = value.Boolean;
                    break;
                case "multiline":
                    if (node.Type != "TextBox" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.MultiLine = value.Boolean;
                    break;
                case "texteditable":
                    if (node.Type != "TextBox" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.TextEditable = value.Boolean;
                    break;
                case "cleartextonfocus":
                    if (node.Type != "TextBox" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ClearTextOnFocus = value.Boolean;
                    break;
                case "mintextsize":
                    if (node.Type != "UITextSizeConstraint" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    var priorMaxTextSize = instances.TryGetValue(node.Id, out var minTextSizeInstance)
                        && minTextSizeInstance.Get("MaxTextSize") is { Type: DataType.Number } pendingMaxTextSize && double.IsFinite(pendingMaxTextSize.Number)
                        ? (float)Math.Clamp(pendingMaxTextSize.Number, 1, 1000) : node.MaxTextSize;
                    node.MinTextSize = (float)Math.Clamp(value.Number, 1, 1000);
                    node.MaxTextSize = Math.Max(priorMaxTextSize, node.MinTextSize);
                    break;
                case "maxtextsize":
                    if (node.Type != "UITextSizeConstraint" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    var priorMinTextSize = instances.TryGetValue(node.Id, out var maxTextSizeInstance)
                        && maxTextSizeInstance.Get("MinTextSize") is { Type: DataType.Number } pendingMinTextSize && double.IsFinite(pendingMinTextSize.Number)
                        ? (float)Math.Clamp(pendingMinTextSize.Number, 1, 1000) : node.MinTextSize;
                    node.MaxTextSize = (float)Math.Clamp(value.Number, priorMinTextSize, 1000);
                    break;
                case "aspecttype":
                    if (node.Type != "UIAspectRatioConstraint" || value.Type != DataType.String || value.String is not ("FitWithinMaxSize" or "ScaleWithParentSize")) return DynValue.Nil;
                    node.AspectType = value.String;
                    break;
                case "dominantaxis":
                    if (node.Type != "UIAspectRatioConstraint" || value.Type != DataType.String || value.String is not ("Width" or "Height")) return DynValue.Nil;
                    node.DominantAxis = value.String;
                    break;
                case "enabled":
                    if (node.Type is "PathfindingModifier" or "PathfindingLink" || value.Type != DataType.Boolean) return DynValue.Nil;
                    var wasEnabled = node.Enabled;
                    node.Enabled = value.Boolean;
                    if (SceneNodeTypes.IsBaseScript(node) && wasEnabled != node.Enabled)
                    {
                        if (node.Enabled) QueueRunnableScripts(node);
                        else StopScripts([node.Id]);
                    }
                    break;
                case "requireslineofsight":
                    if (node.Type != "ProximityPrompt" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.RequiresLineOfSight = value.Boolean;
                    break;
                case "keyboardkeycode":
                    if (node.Type != "ProximityPrompt" || value.Type != DataType.String
                        || !SupportedKeyCodes.Contains(value.String, StringComparer.Ordinal) || value.String is "Unknown" or "None") return DynValue.Nil;
                    node.KeyboardKeyCode = value.String;
                    break;
                case "disabled": if (node.Type != "VehicleSeat" || value.Type != DataType.Boolean) return DynValue.Nil; node.VehicleSeatDisabled = value.Boolean; break;
                case "headsupdisplay": if (node.Type != "VehicleSeat" || value.Type != DataType.Boolean) return DynValue.Nil; node.VehicleSeatHeadsUpDisplay = value.Boolean; break;
                case "maxspeed": if (node.Type != "VehicleSeat" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.VehicleSeatMaxSpeed = (float)Math.Clamp(value.Number, 0, 10000); break;
                case "turnspeed": if (node.Type != "VehicleSeat" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.VehicleSeatTurnSpeed = (float)Math.Clamp(value.Number, 0, 1000); break;
                case "alwaystop":
                    if (node.Type is not ("BillboardGui" or "SurfaceGui") || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.AlwaysOnTop = value.Boolean;
                    break;
                case "brightness": if ((node.Type is not ("Lighting" or "ColorCorrectionEffect") && !SceneNodeTypes.IsLight(node)) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Brightness = node.Type == "ColorCorrectionEffect" ? (float)Math.Clamp(value.Number, -1, 1) : (float)Math.Clamp(value.Number, 0, 8); break;
                case "range": if (!SceneNodeTypes.IsLight(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Range = (float)Math.Clamp(value.Number, 1, 1000); break;
                case "heat": if (node.Type != "Fire" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Heat = (float)Math.Clamp(value.Number, -25, 25); break;
                case "risevelocity": if (node.Type != "Smoke" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.RiseVelocity = (float)Math.Clamp(value.Number, -25, 25); break;
                case "opacity": if (node.Type != "Smoke" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Opacity = (float)Math.Clamp(value.Number, 0, 1); break;
                case "timescale": if (!SceneNodeTypes.IsParticleEffect(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.TimeScale = (float)Math.Clamp(value.Number, 0, 1); break;
                case "blastradius": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.BlastRadius = (float)Math.Clamp(value.Number, 0, 100); break;
                case "blastpressure": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.BlastPressure = (float)Math.Clamp(value.Number, 0, 1000000); break;
                case "destroyjointradiuspercent": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.DestroyJointRadiusPercent = (float)Math.Clamp(value.Number, 0, 1); break;
                case "angle": if (node.Type is not ("SpotLight" or "SurfaceLight") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Angle = (float)Math.Clamp(value.Number, 1, 175); break;
                case "actiontext": if (node.Type != "ProximityPrompt") return DynValue.Nil; node.ActionText = value.CastToString() ?? node.ActionText; break;
                case "objecttext": if (node.Type != "ProximityPrompt") return DynValue.Nil; node.ObjectText = value.CastToString() ?? node.ObjectText; break;
                case "holdduration":
                    if (node.Type != "ProximityPrompt" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.HoldDuration = (float)Math.Clamp(value.Number, 0, 60);
                    break;
                case "maxactivationdistance":
                    if (node.Type is not ("ProximityPrompt" or "ClickDetector") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxActivationDistance = (float)Math.Clamp(value.Number, 1, 100);
                    break;
                case "maxdistance":
                    if (node.Type is not ("BillboardGui" or "SurfaceGui") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxDistance = (float)Math.Clamp(value.Number, 0, 100000);
                    break;
                case "pixelsperstud":
                    if (node.Type != "SurfaceGui" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.PixelsPerStud = (float)Math.Clamp(value.Number, 1, 1000);
                    break;
                case "volume":
                    if (node.Type is not ("Sound" or "SoundGroup" or "SoundService" or "VideoFrame") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    if (node.Type == "SoundGroup") node.SoundGroupVolume = (float)Math.Clamp(value.Number, 0, 1);
                    else if (node.Type == "SoundService") node.SoundServiceVolume = (float)Math.Clamp(value.Number, 0, 1);
                    else node.Volume = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "playbackspeed":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.PlaybackSpeed = (float)Math.Clamp(value.Number, 0, 20);
                    break;
                case "octave":
                    if (node.Type != "PitchShiftSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.PitchShiftOctave = (float)Math.Clamp(value.Number, .5, 2);
                    break;
                case "sidechain":
                {
                    if (node.Type != "CompressorSoundEffect") return DynValue.Nil;
                    var sideChain = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && sideChain?.Type is not ("Sound" or "SoundGroup")) return DynValue.Nil;
                    node.CompressorSideChainId = sideChain?.Id ?? string.Empty;
                    break;
                }
                case "attack": if (node.Type != "CompressorSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CompressorAttack = (float)Math.Clamp(value.Number, .1, 1); break;
                case "gainmakeup": if (node.Type != "CompressorSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CompressorGainMakeup = (float)Math.Clamp(value.Number, -30, 30); break;
                case "ratio": if (node.Type != "CompressorSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CompressorRatio = (float)Math.Clamp(value.Number, 1, 50); break;
                case "release": if (node.Type != "CompressorSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CompressorRelease = (float)Math.Clamp(value.Number, 0, 5); break;
                case "threshold": if (node.Type != "CompressorSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.CompressorThreshold = (float)Math.Clamp(value.Number, -80, 0); break;
                case "lowgain": if (node.Type != "EqualizerSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.EqualizerLowGain = (float)Math.Clamp(value.Number, -80, 10); break;
                case "midgain": if (node.Type != "EqualizerSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.EqualizerMidGain = (float)Math.Clamp(value.Number, -80, 10); break;
                case "highgain": if (node.Type != "EqualizerSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.EqualizerHighGain = (float)Math.Clamp(value.Number, -80, 10); break;
                case "level": if (node.Type != "DistortionSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.DistortionLevel = (float)Math.Clamp(value.Number, 0, 1); break;
                case "delay": if (node.Type != "EchoSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.EchoDelay = (float)Math.Clamp(value.Number, .01, 5); break;
                case "feedback": if (node.Type != "EchoSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.EchoFeedback = (float)Math.Clamp(value.Number, 0, 1); break;
                case "drylevel": if (node.Type == "EchoSoundEffect" && value.Type == DataType.Number && double.IsFinite(value.Number)) node.EchoDryLevel = (float)Math.Clamp(value.Number, -80, 10); else if (node.Type == "ReverbSoundEffect" && value.Type == DataType.Number && double.IsFinite(value.Number)) node.ReverbDryLevel = (float)Math.Clamp(value.Number, -80, 10); else return DynValue.Nil; break;
                case "wetlevel": if (node.Type == "EchoSoundEffect" && value.Type == DataType.Number && double.IsFinite(value.Number)) node.EchoWetLevel = (float)Math.Clamp(value.Number, -80, 10); else if (node.Type == "ReverbSoundEffect" && value.Type == DataType.Number && double.IsFinite(value.Number)) node.ReverbWetLevel = (float)Math.Clamp(value.Number, -80, 10); else return DynValue.Nil; break;
                case "decaytime": if (node.Type != "ReverbSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.ReverbDecayTime = (float)Math.Clamp(value.Number, .1, 20); break;
                case "diffusion": if (node.Type != "ReverbSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.ReverbDiffusion = (float)Math.Clamp(value.Number, 0, 1); break;
                case "depth":
                    if (node.Type is not ("FlangeSoundEffect" or "TremoloSoundEffect" or "ChorusSoundEffect") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    if (node.Type == "FlangeSoundEffect") node.FlangeDepth = (float)Math.Clamp(value.Number, .01, 1);
                    else if (node.Type == "TremoloSoundEffect") node.TremoloDepth = (float)Math.Clamp(value.Number, 0, 1);
                    else node.ChorusDepth = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "mix": if (node.Type is not ("FlangeSoundEffect" or "ChorusSoundEffect") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; if (node.Type == "FlangeSoundEffect") node.FlangeMix = (float)Math.Clamp(value.Number, 0, 1); else node.ChorusMix = (float)Math.Clamp(value.Number, 0, 1); break;
                case "rate": if (node.Type is not ("FlangeSoundEffect" or "ChorusSoundEffect") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; if (node.Type == "FlangeSoundEffect") node.FlangeRate = (float)Math.Clamp(value.Number, 0, 20); else node.ChorusRate = (float)Math.Clamp(value.Number, 0, 20); break;
                case "duty": if (node.Type != "TremoloSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.TremoloDuty = (float)Math.Clamp(value.Number, 0, 1); break;
                case "frequency": if (node.Type != "TremoloSoundEffect" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.TremoloFrequency = (float)Math.Clamp(value.Number, .1, 20); break;
                case "looped": if (node.Type is not ("Sound" or "VideoFrame")) return DynValue.Nil; node.Looped = value.CastToBool(); break;
                case "playing": if (node.Type is not ("Sound" or "VideoFrame")) return DynValue.Nil; node.Playing = value.CastToBool(); break;
                case "rolloffmindistance":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.RollOffMinDistance = (float)Math.Clamp(value.Number, 0, 1000);
                    node.RollOffMaxDistance = Math.Max(node.RollOffMaxDistance, node.RollOffMinDistance + .01f);
                    break;
                case "rolloffmaxdistance":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.RollOffMaxDistance = (float)Math.Clamp(value.Number, node.RollOffMinDistance + .01f, 10000);
                    break;
                case "soundgroup":
                {
                    if (node.Type != "Sound") return DynValue.Nil;
                    var requestedSoundGroup = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && requestedSoundGroup?.Type != "SoundGroup") return DynValue.Nil;
                    node.SoundGroupId = requestedSoundGroup?.Id ?? string.Empty;
                    break;
                }
                case "texture":
                    if (node.Type is not ("Part" or "ParticleEmitter" or "Trail" or "Decal" or "Texture")) return DynValue.Nil;
                    node.Texture = value.CastToString() ?? string.Empty;
                    break;
                case "text":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox")) return DynValue.Nil;
                    node.Text = value.CastToString() ?? node.Text;
                    break;
                case "placeholdertext": if (node.Type != "TextBox") return DynValue.Nil; node.PlaceholderText = value.CastToString() ?? string.Empty; break;
                case "textcolor":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox")) return DynValue.Nil;
                    node.TextColor = value.CastToString() ?? node.TextColor;
                    break;
                case "visible":
                    if (!IsGuiObject(node) || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.Visible = value.Boolean;
                    break;
                case "zindex":
                    if (!(IsGuiObject(node) || node.Type == "UIShadow") || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue) return DynValue.Nil;
                    node.ZIndex = node.Type == "UIShadow" ? Math.Min(0, (int)value.Number) : (int)value.Number;
                    break;
                case "layoutorder":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue) return DynValue.Nil;
                    node.LayoutOrder = (int)value.Number;
                    break;
                case "displayorder":
                    if (node.Type != "ScreenGui" || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue) return DynValue.Nil;
                    node.DisplayOrder = (int)value.Number;
                    break;
                case "resetonspawn":
                    if (node.Type != "ScreenGui" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ResetOnSpawn = value.Boolean;
                    break;
                case "anchored": if (!IsBasePart(node)) return DynValue.Nil; node.Anchored = value.CastToBool(); break;
                case "cancollide": if (!IsBasePart(node)) return DynValue.Nil; node.CanCollide = value.CastToBool(); break;
                case "cantouch": if (!IsBasePart(node)) return DynValue.Nil; node.CanTouch = value.CastToBool(); break;
                case "canquery": if (!IsBasePart(node) || value.Type != DataType.Boolean) return DynValue.Nil; node.CanQuery = value.Boolean; break;
                case "velocity":
                case "assemblylinearvelocity":
                    if (!IsBasePart(node) || value.Type != DataType.Table) return DynValue.Nil;
                    node.Velocity = ReadVector(value, node.Velocity);
                    break;
                case "assemblyangularvelocity":
                    if (!IsBasePart(node) || value.Type != DataType.Table) return DynValue.Nil;
                    node.AssemblyAngularVelocity = ReadVector(value, node.AssemblyAngularVelocity).Select(component => Math.Clamp(component, -1000, 1000)).ToArray();
                    break;
                case "castshadow": if (!IsBasePart(node)) return DynValue.Nil; node.CastShadow = value.CastToBool(); break;
                case "gravity":
                    if (node.Type != "Workspace" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Gravity = (float)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "ambient":
                    if (node.Type != "Lighting" || value.Type != DataType.Table || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.Ambient = ReadColor(value, node.Ambient);
                    break;
                case "clocktime":
                    if (node.Type != "Lighting" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.ClockTime = (float)Math.Clamp(value.Number, 0, 24);
                    break;
                case "globalshadows":
                    if (node.Type != "Lighting" || value.Type != DataType.Boolean) return DynValue.Nil;
                    project.Settings.Shadows = value.Boolean;
                    break;
                case "automanagecharacter":
                    if (node.Type != "Workspace" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.AutoManageCharacter = value.Boolean;
                    break;
                case "character":
                    if (node.Type != "Player") return DynValue.Nil;
                    var assignedCharacter = ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && assignedCharacter?.Type != "Model") return DynValue.Nil;
                    var previousCharacter = node.Character;
                    node.Character = assignedCharacter?.Id ?? string.Empty;
                    if (previousCharacter != node.Character)
                    {
                        FirePlayerCharacterSignal(node, "CharacterRemoving", previousCharacter);
                        FirePlayerCharacterSignal(node, "CharacterAdded", node.Character);
                    }
                    break;
                case "team":
                    if (node.Type != "Player") return DynValue.Nil;
                    var assignedTeam = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && (assignedTeam?.Type != "Team" || !project.Teams.Children.Contains(assignedTeam))) return DynValue.Nil;
                    node.Team = assignedTeam?.Id ?? string.Empty;
                    break;
                case "teamcolor":
                    if (node.Type is not ("Team" or "SpawnLocation") || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    node.TeamColor = ReadColor(value, node.TeamColor);
                    break;
                case "headcolor3":
                case "torsocolor3":
                case "leftarmcolor3":
                case "rightarmcolor3":
                case "leftlegcolor3":
                case "rightlegcolor3":
                    if (node.Type != "BodyColors" || !IsTypedValue(value, "Color3")) return DynValue.Nil;
                    BodyColorsService.SetColor(node, normalized, ReadColor(value, BodyColorsService.GetColor(node, normalized)));
                    break;
                case "autoassignable":
                    if (node.Type != "Team" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.AutoAssignable = value.Boolean;
                    break;
                case "neutral":
                    if (node.Type != "SpawnLocation" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.Neutral = value.Boolean;
                    break;
                case "allowteamchangeontouch":
                    if (node.Type != "SpawnLocation" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.AllowTeamChangeOnTouch = value.Boolean;
                    break;
                case "duration":
                    if (node.Type != "SpawnLocation" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Duration = (float)Math.Clamp(value.Number, 0, 10000);
                    break;
                case "health":
                    if (node.Type != "Humanoid" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Health = (float)Math.Clamp(value.Number, 0, node.MaxHealth);
                    break;
                case "maxhealth":
                    if (node.Type != "Humanoid" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxHealth = (float)Math.Clamp(value.Number, 1, 100000);
                    node.Health = Math.Min(node.Health, node.MaxHealth);
                    break;
                case "walkspeed":
                    if (node.Type != "Humanoid" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.WalkSpeed = (float)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "jumppower":
                    if (node.Type != "Humanoid" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.JumpPower = (float)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "asset": node.Asset = value.CastToString() ?? string.Empty; break;
                case "image": if (node.Type is not ("ImageLabel" or "ImageButton")) return DynValue.Nil; node.Asset = value.CastToString() ?? string.Empty; break;
                case "video": if (node.Type != "VideoFrame") return DynValue.Nil; node.Video = value.CastToString() ?? string.Empty; break;
                case "archivable":
                    if (value.Type != DataType.Boolean) return DynValue.Nil;
                    node.Archivable = value.Boolean;
                    break;
                case "name":
                    if (node.Type is "Workspace" or "Players" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") return DynValue.Nil;
                    SetNodeName(node, value.CastToString() ?? node.Name);
                    break;
                case "cameramode":
                case "mode":
                    if (normalized == "mode" && node.Type == "UIShadow")
                    {
                        if (value.Type != DataType.String || value.String is not ("Shape" or "Text")) return DynValue.Nil;
                        node.ShadowMode = value.String;
                        break;
                    }
                    if (normalized == "mode" && node.Type == "AlignPosition")
                    {
                        if (value.Type != DataType.String || value.String is not ("OneAttachment" or "TwoAttachment")) return DynValue.Nil;
                        node.AlignmentMode = value.String;
                        break;
                    }
                    if (normalized == "mode" && node.Type == "AlignOrientation")
                    {
                        if (value.Type != DataType.String || value.String is not ("OneAttachment" or "TwoAttachment")) return DynValue.Nil;
                        node.AlignmentMode = value.String;
                        break;
                    }
                    if (node.Type != "Camera") return DynValue.Nil;
                    var cameraMode = value.CastToString() is "Subject" ? "Subject" : "Custom";
                    if (cameraMode == "Subject" && !IsBasePart(FindNode(node.Subject))) return DynValue.Nil;
                    node.CameraMode = cameraMode;
                    break;
                case "subject":
                    if (value.Type == DataType.Nil)
                    {
                        if (node.CameraMode == "Subject") return DynValue.Nil;
                        node.Subject = string.Empty;
                        break;
                    }
                    var cameraSubject = ReadNodeArgument(value);
                    if (cameraSubject is null || !IsBasePart(cameraSubject)) return DynValue.Nil;
                    node.Subject = cameraSubject.Id;
                    break;
                case "currentcamera":
                    if (node.Type != "ViewportFrame") return DynValue.Nil;
                    var viewportCamera = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && viewportCamera?.Type != "Camera") return DynValue.Nil;
                    node.ViewportCurrentCameraId = viewportCamera?.Id ?? string.Empty;
                    break;
                case "primarypart":
                    if (node.Type != "Model") return DynValue.Nil;
                    var requestedPrimaryPart = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && (requestedPrimaryPart is null || !SceneModelService.ContainsPrimaryPart(node, requestedPrimaryPart.Id))) return DynValue.Nil;
                    SceneModelService.SetPrimaryPart(node, requestedPrimaryPart);
                    if (instances.TryGetValue(node.Id, out var modelInstance))
                    {
                        modelInstance.Set("Position", VectorValue(SceneModelService.GetPivot(node)));
                        modelInstance.Set("Rotation", VectorValue(node.Rotation));
                    }
                    break;
                case "part0":
                case "part1":
                    if (node.Type is not ("WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")) return DynValue.Nil;
                    var endpoint = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && !IsBasePart(endpoint)) return DynValue.Nil;
                    var endpointId = endpoint?.Id ?? string.Empty;
                    if (endpointId.Length > 0 && endpointId == (normalized == "part0" ? node.Part1 : node.Part0)) return DynValue.Nil;
                    if (normalized == "part0") node.Part0 = endpointId;
                    else node.Part1 = endpointId;
                    break;
                case "attachment0":
                case "attachment1":
                    if (node.Type is not ("Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink")) return DynValue.Nil;
                    var attachment = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && attachment?.Type != "Attachment") return DynValue.Nil;
                    var attachmentId = attachment?.Id ?? string.Empty;
                    if (node.Type is "Beam" or "Trail" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink" && attachmentId.Length > 0 && attachmentId == (normalized == "attachment0" ? node.Attachment1 : node.Attachment0)) return DynValue.Nil;
                    if (normalized == "attachment0") node.Attachment0 = attachmentId;
                    else node.Attachment1 = attachmentId;
                    break;
                case "width0":
                case "width1":
                    if (node.Type != "Beam" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    if (normalized == "width0") node.Width0 = (float)Math.Clamp(value.Number, 0, 100);
                    else node.Width1 = (float)Math.Clamp(value.Number, 0, 100);
                    break;
                case "fieldofview":
                    if (node.Type != "Camera" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.FieldOfView = (float)Math.Clamp(value.Number, 10, 120);
                    break;
                case "cornerradius":
                case "topleftradius":
                case "toprightradius":
                case "bottomrightradius":
                case "bottomleftradius":
                    if (node.Type != "UICorner" || !TryReadCornerUDim(value, out var requestedCorner)) return DynValue.Nil;
                    UICornerRadiusService.Set(node, normalized switch
                    {
                        "topleftradius" => "TopLeftRadius",
                        "toprightradius" => "TopRightRadius",
                        "bottomrightradius" => "BottomRightRadius",
                        "bottomleftradius" => "BottomLeftRadius",
                        _ => "CornerRadius"
                    }, requestedCorner[0], requestedCorner[1]);
                    break;
                case "blurradius":
                    if (node.Type != "UIShadow" || !TryReadUDim(value, out var requestedBlur)) return DynValue.Nil;
                    node.ShadowBlurRadius = UIShadowService.NormalizeUDim(requestedBlur, false);
                    break;
                case "spread":
                    if (node.Type != "UIShadow" || !TryReadUDim2(value, out var spreadScale, out var spreadOffset)) return DynValue.Nil;
                    node.ShadowSpreadScale = UIShadowService.NormalizePair(spreadScale);
                    node.ShadowSpread = UIShadowService.NormalizePair(spreadOffset);
                    break;
                case "inset":
                    if (node.Type != "UIShadow" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ShadowInset = value.Boolean;
                    break;
                case "showbehindparent":
                    if (node.Type != "UIShadow" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ShadowShowBehindParent = value.Boolean;
                    break;
                case "strokecolor": node.StrokeColor = value.CastToString() ?? node.StrokeColor; break;
                case "strokethickness": node.StrokeThickness = (float)Math.Max(0, value.Number); break;
                case "textsize":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.TextSize = (float)Math.Clamp(value.Number, 1, 200);
                    break;
                case "layoutspacing":
                    if (node.Type is not ("UIListLayout" or "UITableLayout") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.LayoutSpacing = (float)Math.Clamp(value.Number, 0, 1000000);
                    break;
                case "tablecolumns":
                    if (node.Type != "UITableLayout" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.TableColumns = (int)Math.Clamp(value.Number, 1, 24);
                    break;
                case "aspectratio":
                    if (node.Type is not ("UIAspectRatio" or "UIAspectRatioConstraint") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.AspectRatio = (float)Math.Clamp(value.Number, .001, 1000);
                    break;
                case "layoutdirection":
                    if (node.Type != "UIListLayout" || value.CastToString() is not ("Horizontal" or "Vertical")) return DynValue.Nil;
                    node.LayoutDirection = value.CastToString()!;
                    break;
                case "horizontalalignment":
                    if (node.Type != "UIListLayout" || value.Type != DataType.String || value.String is not ("Left" or "Center" or "Right")) return DynValue.Nil;
                    node.HorizontalAlignment = value.String;
                    break;
                case "verticalalignment":
                    if (node.Type != "UIListLayout" || value.Type != DataType.String || value.String is not ("Top" or "Center" or "Bottom")) return DynValue.Nil;
                    node.VerticalAlignment = value.String;
                    break;
                case "sortorder":
                    if (node.Type is not ("UIListLayout" or "UIPageLayout") || value.Type != DataType.String || value.String is not ("LayoutOrder" or "Name")) return DynValue.Nil;
                    node.SortOrder = value.String;
                    break;
                case "currentpageindex":
                    if (node.Type != "UIPageLayout" || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < 1 || value.Number > 1000000) return DynValue.Nil;
                    node.CurrentPageIndex = (int)value.Number;
                    break;
                case "currentpage":
                    if (!SetCurrentPage(node, ReadNodeArgument(value))) return DynValue.Nil;
                    break;
                case "circular":
                    if (node.Type != "UIPageLayout" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.Circular = value.Boolean;
                    break;
                case "flexmode":
                    if (node.Type != "UIFlexItem" || value.Type != DataType.String || value.String is not ("None" or "Grow" or "Shrink" or "Fill" or "Custom")) return DynValue.Nil;
                    node.FlexMode = value.String;
                    break;
                case "growratio":
                    if (node.Type != "UIFlexItem" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.GrowRatio = (float)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "shrinkratio":
                    if (node.Type != "UIFlexItem" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.ShrinkRatio = (float)Math.Clamp(value.Number, 0, 1000);
                    break;
                case "itemlinealignment":
                    if (node.Type != "UIFlexItem" || value.Type != DataType.String || value.String is not ("Automatic" or "Start" or "Center" or "End" or "Stretch")) return DynValue.Nil;
                    node.ItemLineAlignment = value.String;
                    break;
                case "responsiveness":
                    if (node.Type != "AlignPosition" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Responsiveness = (float)Math.Clamp(value.Number, 0, 200);
                    break;
                case "maxvelocity":
                    if (node.Type != "AlignPosition" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxVelocity = (float)Math.Clamp(value.Number, 0, 10000);
                    break;
                case "maxforce":
                    if (node.Type != "AlignPosition" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxForce = (float)Math.Clamp(value.Number, 0, 1000000);
                    break;
                case "rigidityenabled":
                    if (node.Type != "AlignPosition" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.RigidityEnabled = value.Boolean;
                    break;
                case "reactionforceenabled":
                    if (node.Type != "AlignPosition" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ReactionForceEnabled = value.Boolean;
                    break;
                case "maxangularvelocity":
                    if (node.Type != "AlignOrientation" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxAngularVelocity = (float)Math.Clamp(value.Number, 0, 10000);
                    break;
                case "maxtorque":
                    if (node.Type != "AlignOrientation" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxTorque = (float)Math.Clamp(value.Number, 0, 1000000);
                    break;
                case "reactiontorqueenabled":
                    if (node.Type != "AlignOrientation" || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.ReactionTorqueEnabled = value.Boolean;
                    break;
                case "skyboxfront" or "skyboxft": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxFront = value.CastToString() ?? string.Empty; break;
                case "skyboxback" or "skyboxbk": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxBack = value.CastToString() ?? string.Empty; break;
                case "skyboxleft" or "skyboxlf": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxLeft = value.CastToString() ?? string.Empty; break;
                case "skyboxright" or "skyboxrt": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxRight = value.CastToString() ?? string.Empty; break;
                case "skyboxtop" or "skyboxup": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxTop = value.CastToString() ?? string.Empty; break;
                case "skyboxbottom" or "skyboxdn": if (node.Type is not ("Workspace" or "Sky")) return DynValue.Nil; node.SkyboxBottom = value.CastToString() ?? string.Empty; break;
                default: return DynValue.Nil;
            }
        }
        if (instances.TryGetValue(node.Id, out var instance))
        {
            if (node.Type == "Lighting" && normalized == "ambient") instance.Set("Ambient", ColorValue(node.Ambient));
            else if (node.Type == "Lighting" && normalized == "brightness") instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
            else if (node.Type == "Lighting" && normalized == "clocktime") instance.Set("ClockTime", DynValue.NewNumber(node.ClockTime));
            else if (node.Type == "Lighting" && normalized == "globalshadows") instance.Set("GlobalShadows", DynValue.NewBoolean(project.Settings.Shadows));
            else WriteInstanceProperty(node, instance, normalized);
            if (node.Type == "UICorner" && normalized is ("cornerradius" or "topleftradius" or "toprightradius" or "bottomrightradius" or "bottomleftradius"))
                SetCornerInstanceProperties(node, instance);
            if (node.Type is ("SelectionBox" or "SelectionSphere") && normalized is ("color" or "color3"))
            {
                instance.Set("Color", ColorValue(node.Color));
                instance.Set("Color3", ColorValue(node.Color));
            }
            if (node.Type == "UIListLayout" && normalized is ("filldirection" or "layoutdirection"))
            {
                instance.Set("FillDirection", DynValue.NewString(node.LayoutDirection));
                instance.Set("LayoutDirection", DynValue.NewString(node.LayoutDirection));
            }
            if (node.Type == "UIListLayout" && normalized is ("padding" or "layoutspacing"))
            {
                instance.Set("Padding", CreateUDim(node.ListPaddingScale, node.LayoutSpacing));
                instance.Set("LayoutSpacing", DynValue.NewNumber(node.LayoutSpacing));
            }
        }
        if (normalized == "maxhealth" && instances.TryGetValue(node.Id, out var humanoidInstance))
            humanoidInstance.Set("Health", DynValue.NewNumber(node.Health));
        if (normalized == "rolloffmindistance" && instances.TryGetValue(node.Id, out var soundInstance))
            soundInstance.Set("RollOffMaxDistance", DynValue.NewNumber(node.RollOffMaxDistance));
        if (normalized == "minsize" && node.Type == "UISizeConstraint" && instances.TryGetValue(node.Id, out var sizeConstraintInstance))
            sizeConstraintInstance.Set("MaxSize", Vector2Value(node.MaxSize));
        if (normalized == "mintextsize" && node.Type == "UITextSizeConstraint" && instances.TryGetValue(node.Id, out var textConstraintInstance))
            textConstraintInstance.Set("MaxTextSize", DynValue.NewNumber(node.MaxTextSize));
        if (normalized is "team" or "teamcolor") SyncTeamPropertyInstances(node);
        if (normalized == "name" && FindParentNode(node) is { } parent) RefreshParentChildren(parent);
        if (node.Type == "Player" && normalized == "character" && FindNode(node.Character) is { Type: "Model" } character)
            AddStarterCharacterScripts(character);
        propertyChanged?.Invoke(node, property, value);
        return DynValue.Nil;
    }

    private void SyncTeamPropertyInstances(SceneNode node)
    {
        if (node.Type == "Player" && instances.TryGetValue(node.Id, out var playerInstance))
        {
            playerInstance.Set("Team", GetTeamValue(node));
            playerInstance.Set("TeamColor", ColorValue(FindNode(node.Team)?.TeamColor ?? "#FFFFFF"));
        }
        if (node.Type == "Team")
            foreach (var player in KnownNodes().Where(candidate => candidate.Type == "Player" && candidate.Team == node.Id))
                if (instances.TryGetValue(player.Id, out var memberInstance)) memberInstance.Set("TeamColor", ColorValue(node.TeamColor));
    }

    private void SetPositionProperty(SceneNode node, DynValue value)
    {
        if (node.Type == "Model")
        {
            var pending = CapturePendingModelDescendantWrites(node);
            if (SceneModelService.MoveTo(node, LimitVector(ReadVector(value, SceneModelService.GetPivot(node)), -1000000, 1000000)))
                UpdateModelDescendantInstances(node, false, pending, (child, propertyName) => propertyChanged?.Invoke(child, propertyName, GetPropertyValue(child, propertyName)));
            return;
        }
        if (IsGuiObject(node) && TryReadUDim2(value, out var scale, out var offset))
        {
            node.PositionScale = scale;
            node.Position = [offset[0], offset[1], node.Position[2]];
            return;
        }
        node.Position = LimitVector(ReadVector(value, node.Position), -1000000, 1000000);
        if (IsGuiObject(node)) node.PositionScale = [0, 0];
    }

    private Dictionary<string, (bool Position, bool Rotation)> CapturePendingModelDescendantWrites(SceneNode model)
    {
        var pending = new Dictionary<string, (bool Position, bool Rotation)>(StringComparer.Ordinal);
        foreach (var child in GameProjectStore.Flatten(model.Children))
        {
            if (!instances.TryGetValue(child.Id, out var instance)) continue;
            var positionValue = instance.Get("Position");
            var positionChanged = IsGuiObject(child)
                ? TryReadUDim2(positionValue, out var scale, out var offset)
                    && (!child.PositionScale.SequenceEqual(scale) || !child.Position.Take(2).SequenceEqual(offset))
                : positionValue.Type == DataType.Table
                    && !ReadVector(positionValue, child.Type == "Model" ? SceneModelService.GetPivot(child) : child.Position)
                        .SequenceEqual(child.Type == "Model" ? SceneModelService.GetPivot(child) : child.Position);
            var rotationValue = instance.Get("Rotation");
            var rotationChanged = rotationValue.Type == DataType.Table && !ReadVector(rotationValue, child.Rotation).SequenceEqual(child.Rotation);
            pending[child.Id] = (positionChanged, rotationChanged);
        }
        return pending;
    }

    private void UpdateModelDescendantInstances(SceneNode model, bool rotated, Dictionary<string, (bool Position, bool Rotation)> pending, Action<SceneNode, string> changed)
    {
        foreach (var child in GameProjectStore.Flatten(model.Children))
        {
            if (child.Type != "Model" && !SceneNodeTypes.IsBasePart(child) && child.Type != "ImageLabel") continue;
            if (instances.TryGetValue(child.Id, out var childInstance))
            {
                pending.TryGetValue(child.Id, out var pendingWrite);
                if (!pendingWrite.Position) childInstance.Set("Position", IsGuiObject(child) ? CreateUDim2Value(child.PositionScale, child.Position) : VectorValue(child.Type == "Model" ? SceneModelService.GetPivot(child) : child.Position));
                if (rotated && !pendingWrite.Rotation) childInstance.Set("Rotation", VectorValue(child.Rotation));
            }
            changed(child, "Position");
            if (rotated) changed(child, "Rotation");
        }
    }

    private void SetSizeProperty(SceneNode node, DynValue value)
    {
        if (node.Type == "Sparkles") return;
        if (SceneNodeTypes.IsSizedParticleEffect(node) || SceneNodeTypes.IsPostEffect(node))
        {
            if (value.Type == DataType.Number && double.IsFinite(value.Number))
                node.EffectSize = (float)Math.Clamp(value.Number, node.Type == "Fire" ? 2 : SceneNodeTypes.IsPostEffect(node) ? 0 : .1f, node.Type == "Fire" ? 30 : SceneNodeTypes.IsPostEffect(node) ? 56 : 100);
            return;
        }
        if (IsGuiObject(node) && TryReadUDim2(value, out var scale, out var offset))
        {
            node.SizeScale = scale;
            node.Size = [offset[0], offset[1], node.Size[2]];
            return;
        }
        if (node.Type == "BillboardGui" && TryReadUDim2(value, out var billboardScale, out var billboardOffset))
        {
            node.SizeScale = billboardScale;
            node.Size = [billboardOffset[0], billboardOffset[1], node.Size[2]];
            return;
        }
        node.Size = LimitVector(ReadVector(value, node.Size), .001f, 1000000);
        if (IsGuiObject(node)) node.SizeScale = [0, 0];
    }

    private void SetNodeName(SceneNode node, string name)
    {
        if (node.Type is "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") return;
        node.Name = name;
        if ((node.Type is "Script" or "LocalScript" or "ModuleScript") && project.Scripts.FirstOrDefault(item => item.Id == node.Id) is { } gameScript)
            gameScript.Name = name;
    }

    private DynValue GetPartReferenceValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { } part && IsBasePart(part) && instances.TryGetValue(part.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetModelPrimaryPartReference(SceneNode model) =>
        SceneModelService.ContainsPrimaryPart(model, model.PrimaryPart) ? GetPartReferenceValue(model.PrimaryPart) : DynValue.Nil;

    private DynValue GetCurrentCameraReference()
    {
        var camera = project.Workspace.Children.FirstOrDefault(child => child.Type == "Camera");
        return camera is not null && instances.TryGetValue(camera.Id, out var cameraInstance) ? DynValue.NewTable(cameraInstance) : DynValue.Nil;
    }

    private DynValue GetAttachmentReferenceValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { Type: "Attachment" } attachment && instances.TryGetValue(attachment.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetSoundGroupReferenceValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { Type: "SoundGroup" } group && instances.TryGetValue(group.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetCompressorSideChainValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { Type: "Sound" or "SoundGroup" } sideChain && instances.TryGetValue(sideChain.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetCharacterValue(SceneNode player) =>
        player.Type == "Player" && !string.IsNullOrEmpty(player.Character)
        && FindNode(player.Character) is { Type: "Model" } model
        && instances.TryGetValue(model.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetTeamValue(SceneNode player) =>
        player.Type == "Player" && !string.IsNullOrEmpty(player.Team)
        && FindNode(player.Team) is { Type: "Team" } team
        && instances.TryGetValue(team.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetOccupantValue(SceneNode seat) =>
        seat.Type is "Seat" or "VehicleSeat" && !string.IsNullOrEmpty(seat.Occupant)
        && FindNode(seat.Occupant) is { Type: "Humanoid" } humanoid
        && instances.TryGetValue(humanoid.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private bool TrySetLinearVelocityProperty(SceneNode node, string property, DynValue value)
    {
        if (property is "vectorvelocity" or "linedirection" or "primarytangentaxis" or "secondarytangentaxis" or "maxaxesforce")
        {
            var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
            if (value.Type != DataType.Table || !vector.All(float.IsFinite)) return false;
            vector = vector.Select(component => Math.Clamp(component, property == "maxaxesforce" ? 0 : -10000, property == "maxaxesforce" ? 1000000 : 10000)).ToArray();
            if (property == "vectorvelocity") node.VectorVelocity = vector;
            if (property == "linedirection") node.LineDirection = vector;
            if (property == "primarytangentaxis") node.PrimaryTangentAxis = vector;
            if (property == "secondarytangentaxis") node.SecondaryTangentAxis = vector;
            if (property == "maxaxesforce") node.MaxAxesForce = vector;
            return true;
        }
        switch (property)
        {
            case "planevelocity" when TryReadVector2(value, out var plane):
                node.PlaneVelocity = plane.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                return true;
            case "maxplanaraxesforce" when TryReadVector2(value, out var maxPlanarForce):
                node.MaxPlanarAxesForce = maxPlanarForce.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                return true;
            case "linevelocity" when value.Type == DataType.Number && double.IsFinite(value.Number):
                node.LineVelocity = (float)Math.Clamp(value.Number, -10000, 10000);
                return true;
            case "maxforce" when value.Type == DataType.Number && double.IsFinite(value.Number):
                node.MaxForce = (float)Math.Clamp(value.Number, 0, 1000000);
                return true;
            case "forcelimitmode" when value.Type == DataType.String && value.String is "Magnitude" or "PerAxis":
                node.ForceLimitMode = value.String;
                return true;
            case "forcelimitsenabled" when value.Type == DataType.Boolean:
                node.ForceLimitsEnabled = value.Boolean;
                return true;
            case "velocityconstraintmode" when value.Type == DataType.String && value.String is "Vector" or "Line" or "Plane":
                node.VelocityConstraintMode = value.String;
                return true;
            case "relativeto" when value.Type == DataType.String && value.String is "World" or "Attachment0" or "Attachment1":
                node.RelativeTo = value.String;
                return true;
            case "reactionforceenabled" when value.Type == DataType.Boolean:
                node.LinearVelocityReactionForceEnabled = value.Boolean;
                return true;
            default:
                return false;
        }
    }

    private bool TrySetTrailProperty(SceneNode node, string property, DynValue value)
    {
        switch (property)
        {
            case "color" when TryReadColorSequence(value, out var color):
                node.ColorSequence = ParticleSequenceService.NormalizeColor(color);
                node.Color = node.ColorSequence[0].Value;
                return true;
            case "transparency" when TryReadNumberSequence(value, out var transparency):
                node.TransparencySequence = ParticleSequenceService.NormalizeNumber(transparency, 0, 1);
                node.Transparency = node.TransparencySequence[0].Value;
                return true;
            case "widthscale" when TryReadNumberSequence(value, out var width):
                node.TrailWidthSequence = ParticleSequenceService.NormalizeNumber(width, 0, 1);
                node.TrailWidth = node.TrailWidthSequence[0].Value;
                return true;
            case "lifetime" when value.Type == DataType.Number && float.IsFinite((float)value.Number):
                node.TrailLifetime = (float)Math.Clamp(value.Number, .01, 20);
                return true;
            case "minlength" when value.Type == DataType.Number && float.IsFinite((float)value.Number):
                node.TrailMinLength = (float)Math.Clamp(value.Number, 0, 100);
                return true;
            case "maxlength" when value.Type == DataType.Number && float.IsFinite((float)value.Number):
                node.TrailMaxLength = (float)Math.Clamp(value.Number, 0, 10000);
                return true;
            case "facecamera" when value.Type == DataType.Boolean:
                node.FaceCamera = value.Boolean;
                return true;
            case "texturemode" when value.Type == DataType.String && value.String is "Stretch" or "Wrap" or "Static":
                node.TextureMode = value.String;
                return true;
            case "texturelength" when value.Type == DataType.Number && float.IsFinite((float)value.Number):
                node.TextureLength = (float)Math.Clamp(value.Number, .01, 1000);
                return true;
            default:
                return false;
        }
    }

    private bool TrySetParticleEmitterProperty(SceneNode node, string property, DynValue value)
    {
        switch (property)
        {
            case "size" when TryReadNumberSequence(value, out var size):
                node.SizeSequence = ParticleSequenceService.NormalizeNumber(size, 0, 100);
                node.EffectSize = node.SizeSequence[0].Value;
                return true;
            case "color" when TryReadColorSequence(value, out var color):
                node.ColorSequence = ParticleSequenceService.NormalizeColor(color);
                node.Color = node.ColorSequence[0].Value;
                return true;
            case "transparency" when TryReadNumberSequence(value, out var transparency):
                node.TransparencySequence = ParticleSequenceService.NormalizeNumber(transparency, 0, 1);
                node.Transparency = node.TransparencySequence[0].Value;
                return true;
            case "rate" when value.Type == DataType.Number && double.IsFinite(value.Number):
                node.Rate = (float)Math.Clamp(value.Number, 0, 200);
                return true;
            case "speed" when TryReadNumberRange(value, out var speed):
                node.SpeedRange = [Math.Clamp(speed[0], -100, 100), Math.Clamp(speed[1], Math.Clamp(speed[0], -100, 100), 100)];
                return true;
            case "lifetime" when TryReadNumberRange(value, out var lifetime):
                node.LifetimeRange = [Math.Clamp(lifetime[0], .05f, 30), Math.Clamp(lifetime[1], Math.Clamp(lifetime[0], .05f, 30), 30)];
                return true;
            case "spreadangle" when TryReadVector2(value, out var spread):
                node.SpreadAngle = [Math.Clamp(spread[0], 0, 360), Math.Clamp(spread[1], 0, 360)];
                return true;
            case "emissiondirection" when SceneNodeTypes.IsFace(value.CastToString()):
                node.EmissionDirection = value.String;
                return true;
            default:
                return false;
        }
    }

    private DynValue GetPropertyValue(SceneNode node, string property)
    {
        if (node.Type == "BindableFunction" && property.Equals("OnInvoke", StringComparison.OrdinalIgnoreCase))
            return DynValue.Nil;
        if (node.Type == "RemoteFunction" && (property.Equals("OnServerInvoke", StringComparison.OrdinalIgnoreCase) || property.Equals("OnClientInvoke", StringComparison.OrdinalIgnoreCase)))
        {
            var callbackName = property.Equals("OnServerInvoke", StringComparison.OrdinalIgnoreCase) ? "OnServerInvoke" : "OnClientInvoke";
            return remoteFunctionCallbacks.TryGetValue((node.Id, callbackName), out var callback) ? callback.Function : DynValue.Nil;
        }
        if (property.Equals("Volume", StringComparison.OrdinalIgnoreCase) && node.Type == "SoundGroup")
            return DynValue.NewNumber(node.SoundGroupVolume);
        if (property.Equals("Volume", StringComparison.OrdinalIgnoreCase) && node.Type == "SoundService")
            return DynValue.NewNumber(node.SoundServiceVolume);
        if (node.Type == "Trail")
        {
            var trailValue = property.ToLowerInvariant() switch
            {
                "color" => ColorSequenceValue(node.ColorSequence.Count > 0 ? node.ColorSequence : [new(0, node.Color), new(1, node.Color)]),
                "transparency" => NumberSequenceValue(node.TransparencySequence.Count > 0 ? node.TransparencySequence : [new(0, node.Transparency), new(1, node.Transparency)]),
                "widthscale" => NumberSequenceValue(node.TrailWidthSequence.Count > 0 ? node.TrailWidthSequence : [new(0, node.TrailWidth), new(1, node.TrailWidth)]),
                "lifetime" => DynValue.NewNumber(node.TrailLifetime),
                "minlength" => DynValue.NewNumber(node.TrailMinLength),
                "maxlength" => DynValue.NewNumber(node.TrailMaxLength),
                "facecamera" => DynValue.NewBoolean(node.FaceCamera),
                "texturemode" => DynValue.NewString(node.TextureMode),
                "texturelength" => DynValue.NewNumber(node.TextureLength),
                _ => DynValue.Nil
            };
            if (trailValue.Type != DataType.Nil) return trailValue;
        }
        if (node.Type is "VectorForce" or "BodyForce")
        {
            var forceValue = property.ToLowerInvariant() switch
            {
                "force" => VectorValue(node.Force),
                "relativeto" => node.Type == "VectorForce" ? DynValue.NewString(node.RelativeTo) : DynValue.Nil,
                "applyatcenterofmass" => node.Type == "VectorForce" ? DynValue.NewBoolean(node.ApplyAtCenterOfMass) : DynValue.Nil,
                _ => DynValue.Nil
            };
            if (forceValue.Type != DataType.Nil) return forceValue;
        }
        if (node.Type == "BodyThrust")
        {
            var bodyThrustValue = property.ToLowerInvariant() switch
            {
                "force" => VectorValue(node.BodyThrustForce),
                "location" => VectorValue(node.BodyThrustLocation),
                _ => DynValue.Nil
            };
            if (bodyThrustValue.Type != DataType.Nil) return bodyThrustValue;
        }
        if (node.Type == "RocketPropulsion")
        {
            var rocketValue = property.ToLowerInvariant() switch
            {
                "target" => GetPartReferenceValue(node.RocketPropulsionTarget),
                "targetoffset" => VectorValue(node.RocketPropulsionTargetOffset),
                "targetradius" => DynValue.NewNumber(node.RocketPropulsionTargetRadius),
                "cartoonfactor" => DynValue.NewNumber(node.RocketPropulsionCartoonFactor),
                "maxspeed" => DynValue.NewNumber(node.RocketPropulsionMaxSpeed),
                "maxthrust" => DynValue.NewNumber(node.RocketPropulsionMaxThrust),
                "maxtorque" => VectorValue(node.RocketPropulsionMaxTorque),
                "thrustd" => DynValue.NewNumber(node.RocketPropulsionThrustD),
                "thrustp" => DynValue.NewNumber(node.RocketPropulsionThrustP),
                "turnd" => DynValue.NewNumber(node.RocketPropulsionTurnD),
                "turnp" => DynValue.NewNumber(node.RocketPropulsionTurnP),
                _ => DynValue.Nil
            };
            if (property.Equals("Target", StringComparison.OrdinalIgnoreCase) || rocketValue.Type != DataType.Nil) return rocketValue;
        }
        if (node.Type == "BodyVelocity")
        {
            var bodyVelocityValue = property.ToLowerInvariant() switch
            {
                "velocity" => VectorValue(node.BodyVelocityTarget),
                "maxforce" => VectorValue(node.BodyVelocityMaxForce),
                "p" => DynValue.NewNumber(node.BodyVelocityP),
                _ => DynValue.Nil
            };
            if (bodyVelocityValue.Type != DataType.Nil) return bodyVelocityValue;
        }
        if (node.Type == "BodyPosition")
        {
            var bodyPositionValue = property.ToLowerInvariant() switch
            {
                "position" => VectorValue(node.BodyPositionTarget),
                "maxforce" => VectorValue(node.BodyPositionMaxForce),
                "p" => DynValue.NewNumber(node.BodyPositionP),
                "d" => DynValue.NewNumber(node.BodyPositionD),
                _ => DynValue.Nil
            };
            if (bodyPositionValue.Type != DataType.Nil) return bodyPositionValue;
        }
        if (node.Type == "BodyGyro")
        {
            var bodyGyroValue = property.ToLowerInvariant() switch
            {
                "rotation" => VectorValue(node.BodyGyroTargetRotation),
                "maxtorque" => VectorValue(node.BodyGyroMaxTorque),
                "p" => DynValue.NewNumber(node.BodyGyroP),
                "d" => DynValue.NewNumber(node.BodyGyroD),
                _ => DynValue.Nil
            };
            if (bodyGyroValue.Type != DataType.Nil) return bodyGyroValue;
        }
        if (node.Type == "BodyAngularVelocity")
        {
            var bodyAngularVelocityValue = property.ToLowerInvariant() switch
            {
                "angularvelocity" => VectorValue(node.BodyAngularVelocityTarget),
                "maxtorque" => VectorValue(node.BodyAngularVelocityMaxTorque),
                "p" => DynValue.NewNumber(node.BodyAngularVelocityP),
                _ => DynValue.Nil
            };
            if (bodyAngularVelocityValue.Type != DataType.Nil) return bodyAngularVelocityValue;
        }
        if (node.Type == "LineForce")
        {
            var lineForceValue = property.ToLowerInvariant() switch
            {
                "magnitude" => DynValue.NewNumber(node.LineForceMagnitude),
                "maxforce" => DynValue.NewNumber(node.LineForceMaxForce),
                "inversesquarelaw" => DynValue.NewBoolean(node.LineForceInverseSquareLaw),
                "reactionforceenabled" => DynValue.NewBoolean(node.LineForceReactionForceEnabled),
                "applyatcenterofmass" => DynValue.NewBoolean(node.ApplyAtCenterOfMass),
                _ => DynValue.Nil
            };
            if (lineForceValue.Type != DataType.Nil) return lineForceValue;
        }
        if (node.Type == "Torque")
        {
            var torqueValue = property.ToLowerInvariant() switch
            {
                "torque" => VectorValue(node.TorqueTarget),
                "relativeto" => DynValue.NewString(node.RelativeTo),
                _ => DynValue.Nil
            };
            if (torqueValue.Type != DataType.Nil) return torqueValue;
        }
        if (node.Type == "SpringConstraint")
        {
            var springValue = property.ToLowerInvariant() switch
            {
                "currentlength" => DynValue.NewNumber(node.SpringCurrentLength),
                "damping" => DynValue.NewNumber(node.SpringDamping),
                "freelength" => DynValue.NewNumber(node.SpringFreeLength),
                "limitsenabled" => DynValue.NewBoolean(node.SpringLimitsEnabled),
                "maxforce" => DynValue.NewNumber(node.SpringMaxForce),
                "maxlength" => DynValue.NewNumber(node.SpringMaxLength),
                "minlength" => DynValue.NewNumber(node.SpringMinLength),
                "stiffness" => DynValue.NewNumber(node.SpringStiffness),
                _ => DynValue.Nil
            };
            if (springValue.Type != DataType.Nil) return springValue;
        }
        if (node.Type == "TorsionSpringConstraint")
        {
            var torsionSpringValue = property.ToLowerInvariant() switch
            {
                "coils" => DynValue.NewNumber(node.TorsionSpringCoils),
                "currentangle" => DynValue.NewNumber(node.TorsionSpringCurrentAngle),
                "damping" => DynValue.NewNumber(node.TorsionSpringDamping),
                "limitsenabled" => DynValue.NewBoolean(node.TorsionSpringLimitsEnabled),
                "maxangle" => DynValue.NewNumber(node.TorsionSpringMaxAngle),
                "maxtorque" => DynValue.NewNumber(node.TorsionSpringMaxTorque),
                "radius" => DynValue.NewNumber(node.TorsionSpringRadius),
                "restitution" => DynValue.NewNumber(node.TorsionSpringRestitution),
                "stiffness" => DynValue.NewNumber(node.TorsionSpringStiffness),
                _ => DynValue.Nil
            };
            if (torsionSpringValue.Type != DataType.Nil) return torsionSpringValue;
        }
        if (node.Type == "BallSocketConstraint")
        {
            var ballSocketValue = property.ToLowerInvariant() switch
            {
                "limitsenabled" => DynValue.NewBoolean(node.BallSocketLimitsEnabled),
                "maxfrictiontorque" => DynValue.NewNumber(node.BallSocketMaxFrictionTorque),
                "radius" => DynValue.NewNumber(node.BallSocketRadius),
                "restitution" => DynValue.NewNumber(node.BallSocketRestitution),
                "upperangle" => DynValue.NewNumber(node.BallSocketUpperAngle),
                "twistlimitsenabled" => DynValue.NewBoolean(node.BallSocketTwistLimitsEnabled),
                "twistlowerangle" => DynValue.NewNumber(node.BallSocketTwistLowerAngle),
                "twistupperangle" => DynValue.NewNumber(node.BallSocketTwistUpperAngle),
                _ => DynValue.Nil
            };
            if (ballSocketValue.Type != DataType.Nil) return ballSocketValue;
        }
        if (node.Type == "UniversalConstraint")
        {
            var universalValue = property.ToLowerInvariant() switch
            {
                "limitsenabled" => DynValue.NewBoolean(node.UniversalLimitsEnabled),
                "maxangle" => DynValue.NewNumber(node.UniversalMaxAngle),
                "radius" => DynValue.NewNumber(node.UniversalRadius),
                "restitution" => DynValue.NewNumber(node.UniversalRestitution),
                _ => DynValue.Nil
            };
            if (universalValue.Type != DataType.Nil) return universalValue;
        }
        if (node.Type == "HingeConstraint")
        {
            var hingeValue = property.ToLowerInvariant() switch
            {
                "actuatortype" => DynValue.NewString(node.HingeActuatorType),
                "angularresponsiveness" => DynValue.NewNumber(node.HingeAngularResponsiveness),
                "angularspeed" => DynValue.NewNumber(node.HingeAngularSpeed),
                "angularvelocity" => DynValue.NewNumber(node.HingeAngularVelocity),
                "currentangle" => DynValue.NewNumber(node.HingeCurrentAngle),
                "limitsenabled" => DynValue.NewBoolean(node.HingeLimitsEnabled),
                "lowerangle" => DynValue.NewNumber(node.HingeLowerAngle),
                "motormaxacceleration" => DynValue.NewNumber(node.HingeMotorMaxAcceleration),
                "motormaxtorque" => DynValue.NewNumber(node.HingeMotorMaxTorque),
                "radius" => DynValue.NewNumber(node.HingeRadius),
                "restitution" => DynValue.NewNumber(node.HingeRestitution),
                "servomaxtorque" => DynValue.NewNumber(node.HingeServoMaxTorque),
                "targetangle" => DynValue.NewNumber(node.HingeTargetAngle),
                "upperangle" => DynValue.NewNumber(node.HingeUpperAngle),
                _ => DynValue.Nil
            };
            if (hingeValue.Type != DataType.Nil) return hingeValue;
        }
        if (node.Type is "PrismaticConstraint" or "CylindricalConstraint")
        {
            var prismaticValue = property.ToLowerInvariant() switch
            {
                "actuatortype" => DynValue.NewString(node.PrismaticActuatorType),
                "currentposition" => DynValue.NewNumber(node.PrismaticCurrentPosition),
                "limitsenabled" => DynValue.NewBoolean(node.PrismaticLimitsEnabled),
                "linearresponsiveness" => DynValue.NewNumber(node.PrismaticLinearResponsiveness),
                "lowerlimit" => DynValue.NewNumber(node.PrismaticLowerLimit),
                "motormaxacceleration" => DynValue.NewNumber(node.PrismaticMotorMaxAcceleration),
                "motormaxforce" => DynValue.NewNumber(node.PrismaticMotorMaxForce),
                "restitution" => DynValue.NewNumber(node.PrismaticRestitution),
                "servomaxforce" => DynValue.NewNumber(node.PrismaticServoMaxForce),
                "size" => DynValue.NewNumber(node.PrismaticSize),
                "speed" => DynValue.NewNumber(node.PrismaticSpeed),
                "targetposition" => DynValue.NewNumber(node.PrismaticTargetPosition),
                "upperlimit" => DynValue.NewNumber(node.PrismaticUpperLimit),
                "velocity" => DynValue.NewNumber(node.PrismaticVelocity),
                _ => DynValue.Nil
            };
            if (prismaticValue.Type != DataType.Nil) return prismaticValue;
        }
        if (node.Type == "CylindricalConstraint")
        {
            var inclination = node.CylindricalInclinationAngle * (MathF.PI / 180);
            var localAxis = new Vector3(MathF.Cos(inclination), MathF.Sin(inclination), 0);
            var worldAxis = localAxis;
            if (FindNode(node.Attachment0) is { Type: "Attachment" } attachment && FindParentNode(attachment) is { } part)
            {
                var frame = RotationMath.FromDegrees(attachment.Rotation) * RotationMath.FromDegrees(part.Rotation);
                worldAxis = Vector3.Normalize(Vector3.TransformNormal(localAxis, frame));
            }
            var cylindricalValue = property.ToLowerInvariant() switch
            {
                "angularactuatortype" => DynValue.NewString(node.HingeActuatorType),
                "angularlimitsenabled" => DynValue.NewBoolean(node.HingeLimitsEnabled),
                "angularrestitution" => DynValue.NewNumber(node.HingeRestitution),
                "angularresponsiveness" => DynValue.NewNumber(node.HingeAngularResponsiveness),
                "angularspeed" => DynValue.NewNumber(node.HingeAngularSpeed),
                "angularvelocity" => DynValue.NewNumber(node.HingeAngularVelocity),
                "currentangle" => DynValue.NewNumber(node.HingeCurrentAngle),
                "inclinationangle" => DynValue.NewNumber(node.CylindricalInclinationAngle),
                "lowerangle" => DynValue.NewNumber(node.HingeLowerAngle),
                "motormaxangularacceleration" => DynValue.NewNumber(node.HingeMotorMaxAcceleration),
                "motormaxtorque" => DynValue.NewNumber(node.HingeMotorMaxTorque),
                "rotationaxisvisible" => DynValue.NewBoolean(node.CylindricalRotationAxisVisible),
                "servomaxtorque" => DynValue.NewNumber(node.HingeServoMaxTorque),
                "targetangle" => DynValue.NewNumber(node.HingeTargetAngle),
                "upperangle" => DynValue.NewNumber(node.HingeUpperAngle),
                "worldrotationaxis" => VectorValue([worldAxis.X, worldAxis.Y, worldAxis.Z]),
                _ => DynValue.Nil
            };
            if (cylindricalValue.Type != DataType.Nil) return cylindricalValue;
        }
        if (node.Type == "RopeConstraint")
        {
            var ropeValue = property.ToLowerInvariant() switch
            {
                "currentlength" => DynValue.NewNumber(node.RopeCurrentLength),
                "length" => DynValue.NewNumber(node.RopeLength),
                "restitution" => DynValue.NewNumber(node.RopeRestitution),
                "winchenabled" => DynValue.NewBoolean(node.RopeWinchEnabled),
                "winchforce" => DynValue.NewNumber(node.RopeWinchForce),
                "winchresponsiveness" => DynValue.NewNumber(node.RopeWinchResponsiveness),
                "winchspeed" => DynValue.NewNumber(node.RopeWinchSpeed),
                "winchtarget" => DynValue.NewNumber(node.RopeWinchTarget),
                _ => DynValue.Nil
            };
            if (ropeValue.Type != DataType.Nil) return ropeValue;
        }
        if (node.Type == "RodConstraint")
        {
            var rodValue = property.ToLowerInvariant() switch
            {
                "currentdistance" => DynValue.NewNumber(node.RodCurrentDistance),
                "length" => DynValue.NewNumber(node.RodLength),
                "limitangle0" => DynValue.NewNumber(node.RodLimitAngle0),
                "limitangle1" => DynValue.NewNumber(node.RodLimitAngle1),
                "limitsenabled" => DynValue.NewBoolean(node.RodLimitsEnabled),
                "thickness" => DynValue.NewNumber(node.RodThickness),
                _ => DynValue.Nil
            };
            if (rodValue.Type != DataType.Nil) return rodValue;
        }
        if (node.Type == "LinearVelocity")
        {
            var velocityValue = property.ToLowerInvariant() switch
            {
                "vectorvelocity" => VectorValue(node.VectorVelocity),
                "linedirection" => VectorValue(node.LineDirection),
                "linevelocity" => DynValue.NewNumber(node.LineVelocity),
                "planevelocity" => Vector2Value(node.PlaneVelocity),
                "primarytangentaxis" => VectorValue(node.PrimaryTangentAxis),
                "secondarytangentaxis" => VectorValue(node.SecondaryTangentAxis),
                "velocityconstraintmode" => DynValue.NewString(node.VelocityConstraintMode),
                "forcelimitsenabled" => DynValue.NewBoolean(node.ForceLimitsEnabled),
                "reactionforceenabled" => DynValue.NewBoolean(node.LinearVelocityReactionForceEnabled),
                "maxforce" => DynValue.NewNumber(node.MaxForce),
                "forcelimitmode" => DynValue.NewString(node.ForceLimitMode),
                "maxaxesforce" => VectorValue(node.MaxAxesForce),
                "maxplanaraxesforce" => Vector2Value(node.MaxPlanarAxesForce),
                "relativeto" => DynValue.NewString(node.RelativeTo),
                _ => DynValue.Nil
            };
            if (velocityValue.Type != DataType.Nil) return velocityValue;
        }
        if (node.Type == "AlignPosition")
        {
            var alignmentValue = property.ToLowerInvariant() switch
            {
                "mode" => DynValue.NewString(node.AlignmentMode),
                "responsiveness" => DynValue.NewNumber(node.Responsiveness),
                "maxvelocity" => DynValue.NewNumber(node.MaxVelocity),
                "maxforce" => DynValue.NewNumber(node.MaxForce),
                "rigidityenabled" => DynValue.NewBoolean(node.RigidityEnabled),
                "reactionforceenabled" => DynValue.NewBoolean(node.ReactionForceEnabled),
                "applyatcenterofmass" => DynValue.NewBoolean(node.ApplyAtCenterOfMass),
                _ => DynValue.Nil
            };
            if (alignmentValue.Type != DataType.Nil) return alignmentValue;
        }
        if (node.Type == "AngularVelocity")
        {
            var angularValue = property.ToLowerInvariant() switch
            {
                "angularvelocity" => VectorValue(node.AngularVelocityTarget),
                "relativeto" => DynValue.NewString(node.RelativeTo),
                "maxtorque" => DynValue.NewNumber(node.MaxTorque),
                "reactiontorqueenabled" => DynValue.NewBoolean(node.ReactionTorqueEnabled),
                _ => DynValue.Nil
            };
            if (angularValue.Type != DataType.Nil) return angularValue;
        }
        if (node.Type == "AlignOrientation")
        {
            var alignmentValue = property.ToLowerInvariant() switch
            {
                "mode" => DynValue.NewString(node.AlignmentMode),
                "rotation" => VectorValue(node.AlignOrientationRotation),
                "responsiveness" => DynValue.NewNumber(node.Responsiveness),
                "maxangularvelocity" => DynValue.NewNumber(node.MaxAngularVelocity),
                "maxtorque" => DynValue.NewNumber(node.MaxTorque),
                "rigidityenabled" => DynValue.NewBoolean(node.RigidityEnabled),
                "reactiontorqueenabled" => DynValue.NewBoolean(node.ReactionTorqueEnabled),
                _ => DynValue.Nil
            };
            if (alignmentValue.Type != DataType.Nil) return alignmentValue;
        }
        if (node.Type == "ParticleEmitter")
        {
            var emitterValue = property.ToLowerInvariant() switch
            {
                "size" => NumberSequenceValue(node.SizeSequence.Count > 0 ? node.SizeSequence : [new(0, node.EffectSize), new(1, node.EffectSize)]),
                "color" => ColorSequenceValue(node.ColorSequence.Count > 0 ? node.ColorSequence : [new(0, node.Color), new(1, node.Color)]),
                "transparency" => NumberSequenceValue(node.TransparencySequence.Count > 0 ? node.TransparencySequence : [new(0, node.Transparency), new(1, node.Transparency)]),
                "rate" => DynValue.NewNumber(node.Rate),
                "speed" => NumberRangeValue(node.SpeedRange),
                "lifetime" => NumberRangeValue(node.LifetimeRange),
                "spreadangle" => Vector2Value(node.SpreadAngle),
                "emissiondirection" => DynValue.NewString(node.EmissionDirection),
                _ => DynValue.Nil
            };
            if (emitterValue.Type != DataType.Nil) return emitterValue;
        }
        if (node.Type == "Lighting" && instances.TryGetValue(node.Id, out var lightingInstance))
        {
            var pendingLighting = lightingInstance.Get(property.ToLowerInvariant() switch
            {
                "ambient" => "Ambient",
                "brightness" => "Brightness",
                "clocktime" => "ClockTime",
                "globalshadows" => "GlobalShadows",
                _ => property
            });
            if (property.Equals("Ambient", StringComparison.OrdinalIgnoreCase) && IsTypedValue(pendingLighting, "Color3")) return pendingLighting;
            if (property.Equals("Brightness", StringComparison.OrdinalIgnoreCase) && pendingLighting.Type == DataType.Number && double.IsFinite(pendingLighting.Number)) return pendingLighting;
            if (property.Equals("ClockTime", StringComparison.OrdinalIgnoreCase) && pendingLighting.Type == DataType.Number && double.IsFinite(pendingLighting.Number)) return pendingLighting;
            if (property.Equals("GlobalShadows", StringComparison.OrdinalIgnoreCase) && pendingLighting.Type == DataType.Boolean) return pendingLighting;
        }
        if (property.Equals("Value", StringComparison.OrdinalIgnoreCase) && instances.TryGetValue(node.Id, out var valueInstance))
        {
            var pending = valueInstance.Get("Value");
            if (IsValidValueObjectValue(node, pending)) return pending;
        }
        var normalizedProperty = property.ToLowerInvariant();
        return normalizedProperty switch
        {
        "label" => node.Type == "PathfindingModifier" ? DynValue.NewString(node.PathfindingModifierLabel) : node.Type == "PathfindingLink" ? DynValue.NewString(node.PathfindingLinkLabel) : DynValue.Nil,
        "passthrough" => node.Type == "PathfindingModifier" ? DynValue.NewBoolean(node.PathfindingPassThrough) : DynValue.Nil,
        "isbidirectional" => node.Type == "PathfindingLink" ? DynValue.NewBoolean(node.PathfindingLinkBidirectional) : DynValue.Nil,
        "id" => DynValue.NewString(node.Id),
        "classname" or "type" => DynValue.NewString(node.Type),
        "position" => HasPositionProperty(node) ? IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Type == "Model" ? SceneModelService.GetPivot(node) : node.Position) : DynValue.Nil,
        "tooltip" => node.Type == "Tool" ? DynValue.NewString(node.ToolTip) : DynValue.Nil,
        "requireshandle" => node.Type == "Tool" ? DynValue.NewBoolean(node.RequiresHandle) : DynValue.Nil,
        "canbedropped" => node.Type == "Tool" ? DynValue.NewBoolean(node.CanBeDropped) : DynValue.Nil,
        "manualactivationonly" => node.Type == "Tool" ? DynValue.NewBoolean(node.ManualActivationOnly) : DynValue.Nil,
        "gripposition" => node.Type == "Tool" ? VectorValue(node.GripPosition) : DynValue.Nil,
        "griprotation" => node.Type == "Tool" ? VectorValue(node.GripRotation) : DynValue.Nil,
        "anchorpoint" => IsGuiObject(node) ? Vector2Value(node.AnchorPoint) : DynValue.Nil,
        "rotation" => HasRotationProperty(node) ? VectorValue(node.Type == "AlignOrientation" ? node.AlignOrientationRotation : node.Rotation) : DynValue.Nil,
        "worldposition" => GetAttachmentWorldTransformValue(node, false),
        "worldrotation" => GetAttachmentWorldTransformValue(node, true),
        "size" => !HasSizeProperty(node) ? DynValue.Nil : SceneNodeTypes.IsSizedParticleEffect(node) || SceneNodeTypes.IsPostEffect(node) ? DynValue.NewNumber(node.EffectSize) : IsGuiObject(node) || node.Type == "BillboardGui" ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size),
        "color" => ColorValue(SceneNodeTypes.IsSurfaceAppearance(node) ? node.SurfaceAppearanceColor : node.Color),
        "tintcolor" => node.Type == "ColorCorrectionEffect" ? ColorValue(node.TintColor) : DynValue.Nil,
        "contrast" => node.Type == "ColorCorrectionEffect" ? DynValue.NewNumber(node.ColorCorrectionContrast) : DynValue.Nil,
        "saturation" => node.Type == "ColorCorrectionEffect" ? DynValue.NewNumber(node.ColorCorrectionSaturation) : DynValue.Nil,
        "colormap" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewString(node.SurfaceAppearanceColorMap) : DynValue.Nil,
        "normalmap" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewString(node.SurfaceAppearanceNormalMap) : DynValue.Nil,
        "roughnessmap" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewString(node.SurfaceAppearanceRoughnessMap) : DynValue.Nil,
        "metalnessmap" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewString(node.SurfaceAppearanceMetalnessMap) : DynValue.Nil,
        "emissivemaskcontent" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewString(node.SurfaceAppearanceEmissiveMask) : DynValue.Nil,
        "emissivetint" => SceneNodeTypes.IsSurfaceAppearance(node) ? ColorValue(node.SurfaceAppearanceEmissiveTint) : DynValue.Nil,
        "emissivestrength" => SceneNodeTypes.IsSurfaceAppearance(node) ? DynValue.NewNumber(node.SurfaceAppearanceEmissiveStrength) : DynValue.Nil,
        "backgroundcolor3" => IsGuiObject(node) ? ColorValue(node.Color) : DynValue.Nil,
        "color3" => node.Type is ("SelectionBox" or "SelectionSphere") ? ColorValue(node.Color) : DynValue.Nil,
        "surfacecolor3" => node.Type is ("SelectionBox" or "SelectionSphere") ? ColorValue(node.SurfaceColor3) : DynValue.Nil,
        "surfacetransparency" => node.Type is ("SelectionBox" or "SelectionSphere") ? DynValue.NewNumber(node.SurfaceTransparency) : DynValue.Nil,
        "linethickness" => node.Type == "SelectionBox" ? DynValue.NewNumber(node.LineThickness) : DynValue.Nil,
        "adornee" => node.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") ? GetObjectValueReference(node.Adornee) : DynValue.Nil,
        "sparklecolor" => node.Type == "Sparkles" ? ColorValue(node.Color) : DynValue.Nil,
        "secondarycolor" => node.Type == "Fire" ? ColorValue(node.SecondaryColor) : DynValue.Nil,
        "decay" => node.Type == "Atmosphere" ? ColorValue(node.AtmosphereDecay) : DynValue.Nil,
        "density" => node.Type == "Atmosphere" ? DynValue.NewNumber(node.Density) : node.Type == "Clouds" ? DynValue.NewNumber(node.CloudDensity) : node.Type == "ReverbSoundEffect" ? DynValue.NewNumber(node.ReverbDensity) : DynValue.Nil,
        "cover" => node.Type == "Clouds" ? DynValue.NewNumber(node.CloudCover) : DynValue.Nil,
        "haze" => node.Type == "Atmosphere" ? DynValue.NewNumber(node.Haze) : DynValue.Nil,
        "glare" => node.Type == "Atmosphere" ? DynValue.NewNumber(node.Glare) : DynValue.Nil,
        "fillcolor" => node.Type == "Highlight" ? ColorValue(node.FillColor) : DynValue.Nil,
        "outlinecolor" => node.Type == "Highlight" ? ColorValue(node.OutlineColor) : DynValue.Nil,
        "startcolor" => node.Type == "UIGradient" ? ColorValue(node.StartColor) : DynValue.Nil,
        "endcolor" => node.Type == "UIGradient" ? ColorValue(node.EndColor) : DynValue.Nil,
        "paddingleft" => node.Type == "UIPadding" ? CreateUDim(node.PaddingLeft[0], node.PaddingLeft[1]) : DynValue.Nil,
        "paddingtop" => node.Type == "UIPadding" ? CreateUDim(node.PaddingTop[0], node.PaddingTop[1]) : DynValue.Nil,
        "paddingright" => node.Type == "UIPadding" ? CreateUDim(node.PaddingRight[0], node.PaddingRight[1]) : DynValue.Nil,
        "paddingbottom" => node.Type == "UIPadding" ? CreateUDim(node.PaddingBottom[0], node.PaddingBottom[1]) : DynValue.Nil,
        "padding" => node.Type == "UIListLayout" ? CreateUDim(node.ListPaddingScale, node.LayoutSpacing) : DynValue.Nil,
        "scale" => node.Type == "UIScale" ? DynValue.NewNumber(node.Scale) : DynValue.Nil,
        "cellsize" => node.Type == "UIGridLayout" ? CreateUDim2Value(node.CellSizeScale, node.CellSize) : DynValue.Nil,
        "cellpadding" => node.Type == "UIGridLayout" ? CreateUDim2Value(node.CellPaddingScale, node.CellPadding) : DynValue.Nil,
        "canvasposition" => node.Type == "ScrollingFrame" ? Vector2Value(node.CanvasPosition) : DynValue.Nil,
        "canvassize" => node.Type == "ScrollingFrame" ? CreateUDim2Value(node.CanvasSizeScale, node.CanvasSize) : DynValue.Nil,
        "scrollbarthickness" => node.Type == "ScrollingFrame" ? DynValue.NewNumber(node.ScrollBarThickness) : DynValue.Nil,
        "scrollingenabled" => node.Type == "ScrollingFrame" ? DynValue.NewBoolean(node.ScrollingEnabled) : DynValue.Nil,
        "scrollingdirection" => node.Type == "ScrollingFrame" ? DynValue.NewString(node.ScrollingDirection) : DynValue.Nil,
        "automaticcanvassize" => node.Type == "ScrollingFrame" ? DynValue.NewString(node.AutomaticCanvasSize) : DynValue.Nil,
        "automaticsize" => IsGuiObject(node) && node.Type != "ScrollingFrame" ? DynValue.NewString(node.AutomaticSize) : DynValue.Nil,
        "clipsdescendants" => IsGuiObject(node) || node.Type is "BillboardGui" or "SurfaceGui" ? DynValue.NewBoolean(node.ClipsDescendants) : DynValue.Nil,
        "filldirection" => node.Type == "UIListLayout" ? DynValue.NewString(node.LayoutDirection) : node.Type == "UIGridLayout" ? DynValue.NewString(node.FillDirection) : DynValue.Nil,
        "filldirectionmaxcells" => node.Type == "UIGridLayout" ? DynValue.NewNumber(node.FillDirectionMaxCells) : DynValue.Nil,
        "filltransparency" or "outlinetransparency" => node.Type == "Highlight" ? DynValue.NewNumber(normalizedProperty == "filltransparency" ? node.FillTransparency : node.OutlineTransparency) : DynValue.Nil,
        "shape" => node.Type == "Part" ? DynValue.NewString(node.Shape) : DynValue.Nil,
        "face" => node.Type is "Decal" or "Texture" or "SurfaceLight" or "SurfaceGui" ? DynValue.NewString(node.Face) : DynValue.Nil,
        "studspertileu" => node.Type == "Texture" ? DynValue.NewNumber(node.StudsPerTileU) : DynValue.Nil,
        "studspertilev" => node.Type == "Texture" ? DynValue.NewNumber(node.StudsPerTileV) : DynValue.Nil,
        "offsetstudsu" => node.Type == "Texture" ? DynValue.NewNumber(node.OffsetStudsU) : DynValue.Nil,
        "offsetstudsv" => node.Type == "Texture" ? DynValue.NewNumber(node.OffsetStudsV) : DynValue.Nil,
        "material" => IsBasePart(node) ? DynValue.NewString(node.Material) : DynValue.Nil,
        "transparency" => IsBasePart(node) || node.Type is "Decal" or "Texture" or "Beam" or "SelectionBox" or "SelectionSphere" or "UIShadow" ? DynValue.NewNumber(node.Transparency) : DynValue.Nil,
        "backgroundtransparency" => IsGuiObject(node) ? DynValue.NewNumber(node.BackgroundTransparency) : DynValue.Nil,
        "bordercolor3" => IsGuiObject(node) ? ColorValue(node.BorderColor3) : DynValue.Nil,
        "bordersizepixel" => IsGuiObject(node) ? DynValue.NewNumber(node.BorderSizePixel) : DynValue.Nil,
        "grouptransparency" => node.Type == "UICanvasGroup" ? DynValue.NewNumber(node.GroupTransparency) : DynValue.Nil,
        "texttransparency" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewNumber(node.TextTransparency) : DynValue.Nil,
        "imagetransparency" => IsGuiObject(node) && node.Type is "ImageLabel" or "ImageButton" ? DynValue.NewNumber(node.ImageTransparency) : DynValue.Nil,
        "imagecolor3" => node.Type is "ImageLabel" or "ImageButton" ? ColorValue(node.ImageColor3) : DynValue.Nil,
        "scaletype" => node.Type is "ImageLabel" or "ImageButton" ? DynValue.NewString(node.ScaleType) : DynValue.Nil,
        "imagerectoffset" => node.Type is "ImageLabel" or "ImageButton" ? Vector2Value(node.ImageRectOffset) : DynValue.Nil,
        "imagerectsize" => node.Type is "ImageLabel" or "ImageButton" ? Vector2Value(node.ImageRectSize) : DynValue.Nil,
        "slicecenter" => node.Type is "ImageLabel" or "ImageButton" ? RectValue(node.SliceCenter) : DynValue.Nil,
        "slicescale" => node.Type is "ImageLabel" or "ImageButton" ? DynValue.NewNumber(node.SliceScale) : DynValue.Nil,
        "hoverimage" => node.Type == "ImageButton" ? DynValue.NewString(node.HoverImage) : DynValue.Nil,
        "pressedimage" => node.Type == "ImageButton" ? DynValue.NewString(node.PressedImage) : DynValue.Nil,
        "minsize" => node.Type == "UISizeConstraint" ? Vector2Value(node.MinSize) : DynValue.Nil,
        "maxsize" => node.Type == "UISizeConstraint" ? Vector2Value(node.MaxSize) : DynValue.Nil,
        "textscaled" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewBoolean(node.TextScaled) : DynValue.Nil,
        "textwrapped" => node.Type is "TextLabel" or "TextButton" ? DynValue.NewBoolean(node.TextWrapped) : DynValue.Nil,
        "multiline" => node.Type == "TextBox" ? DynValue.NewBoolean(node.MultiLine) : DynValue.Nil,
        "texteditable" => node.Type == "TextBox" ? DynValue.NewBoolean(node.TextEditable) : DynValue.Nil,
        "cleartextonfocus" => node.Type == "TextBox" ? DynValue.NewBoolean(node.ClearTextOnFocus) : DynValue.Nil,
        "mintextsize" => node.Type == "UITextSizeConstraint" ? DynValue.NewNumber(node.MinTextSize) : DynValue.Nil,
        "maxtextsize" => node.Type == "UITextSizeConstraint" ? DynValue.NewNumber(node.MaxTextSize) : DynValue.Nil,
        "aspecttype" => node.Type == "UIAspectRatioConstraint" ? DynValue.NewString(node.AspectType) : DynValue.Nil,
        "dominantaxis" => node.Type == "UIAspectRatioConstraint" ? DynValue.NewString(node.DominantAxis) : DynValue.Nil,
        "enabled" => node.Type is not ("PathfindingModifier" or "PathfindingLink") ? DynValue.NewBoolean(node.Enabled) : DynValue.Nil,
        "alwaystop" => node.Type is "BillboardGui" or "SurfaceGui" ? DynValue.NewBoolean(node.AlwaysOnTop) : DynValue.Nil,
        "maxdistance" => node.Type is "BillboardGui" or "SurfaceGui" ? DynValue.NewNumber(node.MaxDistance) : DynValue.Nil,
        "pixelsperstud" => node.Type == "SurfaceGui" ? DynValue.NewNumber(node.PixelsPerStud) : DynValue.Nil,
        "brightness" => node.Type is "Lighting" or "ColorCorrectionEffect" || SceneNodeTypes.IsLight(node) ? DynValue.NewNumber(node.Brightness) : DynValue.Nil,
        "range" => SceneNodeTypes.IsLight(node) ? DynValue.NewNumber(node.Range) : DynValue.Nil,
        "heat" => node.Type == "Fire" ? DynValue.NewNumber(node.Heat) : DynValue.Nil,
        "risevelocity" => node.Type == "Smoke" ? DynValue.NewNumber(node.RiseVelocity) : DynValue.Nil,
        "opacity" => node.Type == "Smoke" ? DynValue.NewNumber(node.Opacity) : DynValue.Nil,
        "timescale" => SceneNodeTypes.IsParticleEffect(node) ? DynValue.NewNumber(node.TimeScale) : DynValue.Nil,
        "blastradius" => node.Type == "Explosion" ? DynValue.NewNumber(node.BlastRadius) : DynValue.Nil,
        "blastpressure" => node.Type == "Explosion" ? DynValue.NewNumber(node.BlastPressure) : DynValue.Nil,
        "destroyjointradiuspercent" => node.Type == "Explosion" ? DynValue.NewNumber(node.DestroyJointRadiusPercent) : DynValue.Nil,
        "angle" => node.Type is "SpotLight" or "SurfaceLight" ? DynValue.NewNumber(node.Angle) : DynValue.Nil,
        "actiontext" => node.Type == "ProximityPrompt" ? DynValue.NewString(node.ActionText) : DynValue.Nil,
        "objecttext" => node.Type == "ProximityPrompt" ? DynValue.NewString(node.ObjectText) : DynValue.Nil,
        "holdduration" => node.Type == "ProximityPrompt" ? DynValue.NewNumber(node.HoldDuration) : DynValue.Nil,
        "keyboardkeycode" => node.Type == "ProximityPrompt" ? DynValue.NewString(node.KeyboardKeyCode) : DynValue.Nil,
        "requireslineofsight" => node.Type == "ProximityPrompt" ? DynValue.NewBoolean(node.RequiresLineOfSight) : DynValue.Nil,
        "maxactivationdistance" => node.Type is "ProximityPrompt" or "ClickDetector" ? DynValue.NewNumber(node.MaxActivationDistance) : DynValue.Nil,
        "volume" => node.Type is "Sound" or "SoundGroup" or "SoundService" or "VideoFrame" ? DynValue.NewNumber(node.Type == "SoundGroup" ? node.SoundGroupVolume : node.Type == "SoundService" ? node.SoundServiceVolume : node.Volume) : DynValue.Nil,
        "playbackspeed" => node.Type == "Sound" ? DynValue.NewNumber(node.PlaybackSpeed) : DynValue.Nil,
        "octave" => node.Type == "PitchShiftSoundEffect" ? DynValue.NewNumber(node.PitchShiftOctave) : DynValue.Nil,
        "level" => node.Type == "DistortionSoundEffect" ? DynValue.NewNumber(node.DistortionLevel) : DynValue.Nil,
        "sidechain" => node.Type == "CompressorSoundEffect" ? GetCompressorSideChainValue(node.CompressorSideChainId) : DynValue.Nil,
        "attack" => node.Type == "CompressorSoundEffect" ? DynValue.NewNumber(node.CompressorAttack) : DynValue.Nil,
        "gainmakeup" => node.Type == "CompressorSoundEffect" ? DynValue.NewNumber(node.CompressorGainMakeup) : DynValue.Nil,
        "ratio" => node.Type == "CompressorSoundEffect" ? DynValue.NewNumber(node.CompressorRatio) : DynValue.Nil,
        "release" => node.Type == "CompressorSoundEffect" ? DynValue.NewNumber(node.CompressorRelease) : DynValue.Nil,
        "threshold" => node.Type == "CompressorSoundEffect" ? DynValue.NewNumber(node.CompressorThreshold) : DynValue.Nil,
        "lowgain" => node.Type == "EqualizerSoundEffect" ? DynValue.NewNumber(node.EqualizerLowGain) : DynValue.Nil,
        "midgain" => node.Type == "EqualizerSoundEffect" ? DynValue.NewNumber(node.EqualizerMidGain) : DynValue.Nil,
        "highgain" => node.Type == "EqualizerSoundEffect" ? DynValue.NewNumber(node.EqualizerHighGain) : DynValue.Nil,
        "delay" => node.Type == "EchoSoundEffect" ? DynValue.NewNumber(node.EchoDelay) : DynValue.Nil,
        "feedback" => node.Type == "EchoSoundEffect" ? DynValue.NewNumber(node.EchoFeedback) : DynValue.Nil,
        "drylevel" => node.Type == "EchoSoundEffect" ? DynValue.NewNumber(node.EchoDryLevel) : node.Type == "ReverbSoundEffect" ? DynValue.NewNumber(node.ReverbDryLevel) : DynValue.Nil,
        "wetlevel" => node.Type == "EchoSoundEffect" ? DynValue.NewNumber(node.EchoWetLevel) : node.Type == "ReverbSoundEffect" ? DynValue.NewNumber(node.ReverbWetLevel) : DynValue.Nil,
        "depth" => node.Type == "FlangeSoundEffect" ? DynValue.NewNumber(node.FlangeDepth) : node.Type == "TremoloSoundEffect" ? DynValue.NewNumber(node.TremoloDepth) : node.Type == "ChorusSoundEffect" ? DynValue.NewNumber(node.ChorusDepth) : DynValue.Nil,
        "mix" => node.Type == "FlangeSoundEffect" ? DynValue.NewNumber(node.FlangeMix) : node.Type == "ChorusSoundEffect" ? DynValue.NewNumber(node.ChorusMix) : DynValue.Nil,
        "rate" => node.Type == "FlangeSoundEffect" ? DynValue.NewNumber(node.FlangeRate) : node.Type == "ChorusSoundEffect" ? DynValue.NewNumber(node.ChorusRate) : DynValue.Nil,
        "duty" => node.Type == "TremoloSoundEffect" ? DynValue.NewNumber(node.TremoloDuty) : DynValue.Nil,
        "frequency" => node.Type == "TremoloSoundEffect" ? DynValue.NewNumber(node.TremoloFrequency) : DynValue.Nil,
        "decaytime" => node.Type == "ReverbSoundEffect" ? DynValue.NewNumber(node.ReverbDecayTime) : DynValue.Nil,
        "diffusion" => node.Type == "ReverbSoundEffect" ? DynValue.NewNumber(node.ReverbDiffusion) : DynValue.Nil,
        "soundgroup" => node.Type == "Sound" ? GetSoundGroupReferenceValue(node.SoundGroupId) : DynValue.Nil,
        "looped" => node.Type is "Sound" or "VideoFrame" ? DynValue.NewBoolean(node.Looped) : DynValue.Nil,
        "playing" => node.Type is "Sound" or "VideoFrame" ? DynValue.NewBoolean(node.Playing) : DynValue.Nil,
        "rolloffmindistance" => node.Type == "Sound" ? DynValue.NewNumber(node.RollOffMinDistance) : DynValue.Nil,
        "rolloffmaxdistance" => node.Type == "Sound" ? DynValue.NewNumber(node.RollOffMaxDistance) : DynValue.Nil,
        "texture" => node.Type is "Part" or "ParticleEmitter" or "Trail" or "Decal" or "Texture" ? DynValue.NewString(node.Texture) : DynValue.Nil,
        "text" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewString(node.Text) : DynValue.Nil,
        "placeholdertext" => node.Type == "TextBox" ? DynValue.NewString(node.PlaceholderText) : DynValue.Nil,
        "textcolor" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewString(node.TextColor) : DynValue.Nil,
        "visible" => IsGuiObject(node) ? DynValue.NewBoolean(node.Visible) : DynValue.Nil,
        "displayorder" => node.Type == "ScreenGui" ? DynValue.NewNumber(node.DisplayOrder) : DynValue.Nil,
        "resetonspawn" => node.Type == "ScreenGui" ? DynValue.NewBoolean(node.ResetOnSpawn) : DynValue.Nil,
        "zindex" => IsGuiObject(node) || node.Type == "UIShadow" ? DynValue.NewNumber(node.ZIndex) : DynValue.Nil,
        "layoutorder" => IsGuiObject(node) ? DynValue.NewNumber(node.LayoutOrder) : DynValue.Nil,
        "anchored" => IsBasePart(node) ? DynValue.NewBoolean(node.Anchored) : DynValue.Nil,
        "cancollide" => IsBasePart(node) ? DynValue.NewBoolean(node.CanCollide) : DynValue.Nil,
        "cantouch" => IsBasePart(node) ? DynValue.NewBoolean(node.CanTouch) : DynValue.Nil,
        "canquery" => IsBasePart(node) ? DynValue.NewBoolean(node.CanQuery) : DynValue.Nil,
        "velocity" or "assemblylinearvelocity" => IsBasePart(node) ? VectorValue(node.Velocity) : DynValue.Nil,
        "assemblyangularvelocity" => IsBasePart(node) ? VectorValue(node.AssemblyAngularVelocity) : DynValue.Nil,
        "castshadow" => IsBasePart(node) ? DynValue.NewBoolean(node.CastShadow) : DynValue.Nil,
        "gravity" => node.Type == "Workspace" ? DynValue.NewNumber(node.Gravity) : DynValue.Nil,
        "currentcamera" => node.Type == "Workspace" ? GetCurrentCameraReference() : node.Type == "ViewportFrame" ? GetObjectValueReference(node.ViewportCurrentCameraId) : DynValue.Nil,
        "ambient" => node.Type == "Lighting" ? ColorValue(node.Ambient) : DynValue.Nil,
        "clocktime" => node.Type == "Lighting" ? DynValue.NewNumber(node.ClockTime) : DynValue.Nil,
        "globalshadows" => node.Type == "Lighting" ? DynValue.NewBoolean(project.Settings.Shadows) : DynValue.Nil,
        "automanagecharacter" => node.Type == "Workspace" ? DynValue.NewBoolean(node.AutoManageCharacter) : DynValue.Nil,
        "character" => GetCharacterValue(node),
        "team" => GetTeamValue(node),
        "teamcolor" => node.Type is "Team" or "SpawnLocation" ? ColorValue(node.TeamColor) : node.Type == "Player" ? ColorValue(FindNode(node.Team)?.TeamColor ?? "#FFFFFF") : DynValue.Nil,
        "headcolor3" or "torsocolor3" or "leftarmcolor3" or "rightarmcolor3" or "leftlegcolor3" or "rightlegcolor3"
            => node.Type == "BodyColors" ? ColorValue(BodyColorsService.GetColor(node, normalizedProperty)) : DynValue.Nil,
        "autoassignable" => node.Type == "Team" ? DynValue.NewBoolean(node.AutoAssignable) : DynValue.Nil,
        "neutral" => node.Type == "SpawnLocation" ? DynValue.NewBoolean(node.Neutral) : DynValue.Nil,
        "allowteamchangeontouch" => node.Type == "SpawnLocation" ? DynValue.NewBoolean(node.AllowTeamChangeOnTouch) : DynValue.Nil,
        "duration" => node.Type == "SpawnLocation" ? DynValue.NewNumber(node.Duration) : DynValue.Nil,
        "occupant" => node.Type is "Seat" or "VehicleSeat" ? GetOccupantValue(node) : DynValue.Nil,
        "disabled" => node.Type == "VehicleSeat" ? DynValue.NewBoolean(node.VehicleSeatDisabled) : DynValue.Nil,
        "headsupdisplay" => node.Type == "VehicleSeat" ? DynValue.NewBoolean(node.VehicleSeatHeadsUpDisplay) : DynValue.Nil,
        "maxspeed" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatMaxSpeed) : DynValue.Nil,
        "torque" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatTorque) : node.Type == "Torque" ? VectorValue(node.TorqueTarget) : DynValue.Nil,
        "turnspeed" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatTurnSpeed) : DynValue.Nil,
        "throttlefloat" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatThrottleFloat) : DynValue.Nil,
        "steerfloat" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatSteerFloat) : DynValue.Nil,
        "arehingesdetected" => node.Type == "VehicleSeat" ? DynValue.NewNumber(node.VehicleSeatAreHingesDetected) : DynValue.Nil,
        "health" => node.Type == "Humanoid" ? DynValue.NewNumber(node.Health) : DynValue.Nil,
        "maxhealth" => node.Type == "Humanoid" ? DynValue.NewNumber(node.MaxHealth) : DynValue.Nil,
        "walkspeed" => node.Type == "Humanoid" ? DynValue.NewNumber(node.WalkSpeed) : DynValue.Nil,
        "jumppower" => node.Type == "Humanoid" ? DynValue.NewNumber(node.JumpPower) : DynValue.Nil,
        "value" => SceneNodeTypes.IsValueObject(node) ? GetValueObjectValue(node) : DynValue.Nil,
        "asset" => DynValue.NewString(node.Asset),
        "image" => node.Type is "ImageLabel" or "ImageButton" ? DynValue.NewString(node.Asset) : DynValue.Nil,
        "video" => node.Type == "VideoFrame" ? DynValue.NewString(node.Video) : DynValue.Nil,
        "archivable" => DynValue.NewBoolean(node.Archivable),
        "name" => DynValue.NewString(node.Name),
        "children" => GetChildren(node),
        "parent" => FindParentNode(node) is { } parent && instances.TryGetValue(parent.Id, out var parentInstance) ? DynValue.NewTable(parentInstance) : DynValue.Nil,
        "cameramode" or "mode" => DynValue.NewString(node.Type == "UIShadow" ? node.ShadowMode : node.CameraMode),
        "subject" => FindNode(node.Subject) is { } subjectNode && IsBasePart(subjectNode) && instances.TryGetValue(subjectNode.Id, out var subject) ? DynValue.NewTable(subject) : DynValue.Nil,
        "primarypart" => node.Type == "Model" ? GetModelPrimaryPartReference(node) : DynValue.Nil,
        "part0" or "part1" => node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint" ? GetPartReferenceValue(normalizedProperty == "part0" ? node.Part0 : node.Part1) : DynValue.Nil,
        "part0offsetposition" => node.Type is "Weld" or "Motor6D" ? VectorValue(node.Part0OffsetPosition) : DynValue.Nil,
        "part0offsetrotation" => node.Type is "Weld" or "Motor6D" ? VectorValue(node.Part0OffsetRotation) : DynValue.Nil,
        "part1offsetposition" => node.Type is "Weld" or "Motor6D" ? VectorValue(node.Part1OffsetPosition) : DynValue.Nil,
        "part1offsetrotation" => node.Type is "Weld" or "Motor6D" ? VectorValue(node.Part1OffsetRotation) : DynValue.Nil,
        "transformposition" => node.Type == "Motor6D" ? VectorValue(node.MotorTransformPosition) : DynValue.Nil,
        "transformrotation" => node.Type == "Motor6D" ? VectorValue(node.MotorTransformRotation) : DynValue.Nil,
        "attachment0" or "attachment1" => node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "PathfindingLink" ? GetAttachmentReferenceValue(normalizedProperty == "attachment0" ? node.Attachment0 : node.Attachment1) : DynValue.Nil,
        "width0" or "width1" => node.Type == "Beam" ? DynValue.NewNumber(normalizedProperty == "width0" ? node.Width0 : node.Width1) : DynValue.Nil,
        "fieldofview" => node.Type == "Camera" ? DynValue.NewNumber(node.FieldOfView) : DynValue.Nil,
        "cornerradius" => node.Type == "UICorner" ? CornerUDim(node, "CornerRadius") : DynValue.Nil,
        "topleftradius" => node.Type == "UICorner" ? CornerUDim(node, "TopLeftRadius") : DynValue.Nil,
        "toprightradius" => node.Type == "UICorner" ? CornerUDim(node, "TopRightRadius") : DynValue.Nil,
        "bottomrightradius" => node.Type == "UICorner" ? CornerUDim(node, "BottomRightRadius") : DynValue.Nil,
        "bottomleftradius" => node.Type == "UICorner" ? CornerUDim(node, "BottomLeftRadius") : DynValue.Nil,
        "blurradius" => node.Type == "UIShadow" ? CreateUDim(node.ShadowBlurRadius[0], node.ShadowBlurRadius[1]) : DynValue.Nil,
        "offset" => node.Type == "UIShadow" ? CreateUDim2Value(node.ShadowOffsetScale, node.ShadowOffset) : node.Type == "Atmosphere" ? DynValue.NewNumber(node.Offset) : DynValue.Nil,
        "spread" => node.Type == "UIShadow" ? CreateUDim2Value(node.ShadowSpreadScale, node.ShadowSpread) : DynValue.Nil,
        "inset" => node.Type == "UIShadow" ? DynValue.NewBoolean(node.ShadowInset) : DynValue.Nil,
        "showbehindparent" => node.Type == "UIShadow" ? DynValue.NewBoolean(node.ShadowShowBehindParent) : DynValue.Nil,
        "strokecolor" => DynValue.NewString(node.StrokeColor),
        "strokethickness" => DynValue.NewNumber(node.StrokeThickness),
        "textsize" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewNumber(node.TextSize) : DynValue.Nil,
        "layoutspacing" => node.Type is "UIListLayout" or "UITableLayout" ? DynValue.NewNumber(node.LayoutSpacing) : DynValue.Nil,
        "tablecolumns" => node.Type == "UITableLayout" ? DynValue.NewNumber(node.TableColumns) : DynValue.Nil,
        "aspectratio" => node.Type is "UIAspectRatio" or "UIAspectRatioConstraint" ? DynValue.NewNumber(node.AspectRatio) : DynValue.Nil,
        "layoutdirection" => node.Type == "UIListLayout" ? DynValue.NewString(node.LayoutDirection) : DynValue.Nil,
        "horizontalalignment" => node.Type == "UIListLayout" ? DynValue.NewString(node.HorizontalAlignment) : DynValue.Nil,
        "verticalalignment" => node.Type == "UIListLayout" ? DynValue.NewString(node.VerticalAlignment) : DynValue.Nil,
        "sortorder" => node.Type is "UIListLayout" or "UIPageLayout" ? DynValue.NewString(node.SortOrder) : DynValue.Nil,
        "currentpageindex" => node.Type == "UIPageLayout" ? DynValue.NewNumber(node.CurrentPageIndex) : DynValue.Nil,
        "currentpage" => node.Type == "UIPageLayout" && GetCurrentPage(node) is { } page && instances.TryGetValue(page.Id, out var pageInstance) ? DynValue.NewTable(pageInstance) : DynValue.Nil,
        "circular" => node.Type == "UIPageLayout" ? DynValue.NewBoolean(node.Circular) : DynValue.Nil,
        "flexmode" => node.Type == "UIFlexItem" ? DynValue.NewString(node.FlexMode) : DynValue.Nil,
        "growratio" => node.Type == "UIFlexItem" ? DynValue.NewNumber(node.GrowRatio) : DynValue.Nil,
        "shrinkratio" => node.Type == "UIFlexItem" ? DynValue.NewNumber(node.ShrinkRatio) : DynValue.Nil,
        "itemlinealignment" => node.Type == "UIFlexItem" ? DynValue.NewString(node.ItemLineAlignment) : DynValue.Nil,
        "skyboxfront" or "skyboxft" => DynValue.NewString(node.SkyboxFront),
        "skyboxback" or "skyboxbk" => DynValue.NewString(node.SkyboxBack),
        "skyboxleft" or "skyboxlf" => DynValue.NewString(node.SkyboxLeft),
        "skyboxright" or "skyboxrt" => DynValue.NewString(node.SkyboxRight),
        "skyboxtop" or "skyboxup" => DynValue.NewString(node.SkyboxTop),
        "skyboxbottom" or "skyboxdn" => DynValue.NewString(node.SkyboxBottom),
        _ => DynValue.Nil
        };
    }

    private DynValue GetValueObjectValue(SceneNode node) => node.Type switch
    {
        "BoolValue" => DynValue.NewBoolean(node.BooleanValue),
        "IntValue" => DynValue.NewNumber(node.IntegerValue),
        "NumberValue" => DynValue.NewNumber(node.NumberValue),
        "StringValue" => DynValue.NewString(node.StringValueText),
        "Vector2Value" => Vector2Value(node.Vector2Value),
        "Vector3Value" => VectorValue(node.Vector3Value),
        "Color3Value" => ColorValue(node.Color3Value),
        "ObjectValue" => GetObjectValueReference(node.ObjectValueId),
        _ => DynValue.Nil
    };

    private DynValue GetObjectValueReference(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { } referenced && instances.TryGetValue(referenced.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private bool IsValidValueObjectValue(SceneNode node, DynValue value) => node.Type switch
    {
        "BoolValue" => value.Type == DataType.Boolean,
        "IntValue" => value.Type == DataType.Number && double.IsFinite(value.Number)
            && value.Number == Math.Truncate(value.Number) && Math.Abs(value.Number) <= 9007199254740991d,
        "NumberValue" => value.Type == DataType.Number && double.IsFinite(value.Number),
        "StringValue" => value.Type == DataType.String,
        "Vector2Value" => IsTypedValue(value, "Vector2") && TryReadVector2(value, out var vector2) && vector2.All(float.IsFinite),
        "Vector3Value" => IsTypedValue(value, "Vector3") && ReadVector(value, [float.NaN, float.NaN, float.NaN]).All(float.IsFinite),
        "Color3Value" => IsTypedValue(value, "Color3") && new[] { "R", "G", "B" }.All(channel => value.Table.Get(channel).Type == DataType.Number && double.IsFinite(value.Table.Get(channel).Number)),
        "ObjectValue" => value.Type == DataType.Nil || value.Type == DataType.Table && ReadNodeArgument(value) is { } referenced
            && instances.TryGetValue(referenced.Id, out var instance) && ReferenceEquals(value.Table, instance),
        _ => false
    };

    private bool IsTypedValue(DynValue value, string type) =>
        value.Type == DataType.Table && runtimeTypeTags.TryGetValue(value.Table, out var tag) && tag.Name == type;

    private bool TrySetValueObjectValue(SceneNode node, DynValue value, out bool changed)
    {
        changed = false;
        switch (node.Type)
        {
            case "BoolValue" when value.Type == DataType.Boolean:
                changed = node.BooleanValue != value.Boolean;
                node.BooleanValue = value.Boolean;
                return true;
            case "IntValue" when value.Type == DataType.Number && double.IsFinite(value.Number)
                && value.Number == Math.Truncate(value.Number) && Math.Abs(value.Number) <= 9007199254740991d:
                var integer = (long)value.Number;
                changed = node.IntegerValue != integer;
                node.IntegerValue = integer;
                return true;
            case "NumberValue" when value.Type == DataType.Number && double.IsFinite(value.Number):
                changed = node.NumberValue != value.Number;
                node.NumberValue = value.Number;
                return true;
            case "StringValue" when value.Type == DataType.String:
                changed = node.StringValueText != value.String;
                node.StringValueText = value.String;
                return true;
            case "Vector2Value" when IsValidValueObjectValue(node, value):
                TryReadVector2(value, out var vector2);
                changed = !node.Vector2Value.SequenceEqual(vector2);
                node.Vector2Value = vector2;
                return true;
            case "Vector3Value" when IsValidValueObjectValue(node, value):
                var vector = ReadVector(value, node.Vector3Value);
                changed = !node.Vector3Value.SequenceEqual(vector);
                node.Vector3Value = vector;
                return true;
            case "Color3Value" when IsValidValueObjectValue(node, value):
                var color = ReadColor(value, node.Color3Value);
                changed = !string.Equals(node.Color3Value, color, StringComparison.OrdinalIgnoreCase);
                node.Color3Value = color;
                return true;
            case "ObjectValue" when IsValidValueObjectValue(node, value):
                var referencedId = value.Type == DataType.Nil ? string.Empty : ReadNodeArgument(value)!.Id;
                changed = node.ObjectValueId != referencedId;
                node.ObjectValueId = referencedId;
                return true;
            default:
                return false;
        }
    }

    private void FireValueChanged(SceneNode node, DynValue value)
    {
        if (!valueChangedCallbacks.TryGetValue(node.Id, out var callbacks)) return;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback, value);
    }

    private DynValue ConnectScriptCallback(Dictionary<string, List<ScriptCallback>> registry, string nodeId, DynValue function, string signalName)
    {
        if (currentGameScript is null || function.Type != DataType.Function) return DynValue.Nil;
        if (!registry.TryGetValue(nodeId, out var callbacks)) registry[nodeId] = callbacks = [];
        var connection = new Table(script);
        runtimeTypeTags.Add(connection, new LuaXTypeTag("RBXScriptConnection"));
        var callback = new ScriptCallback(currentGameScript, function, signalName, connection);
        callbacks.Add(callback);
        connection.Set("Connected", DynValue.NewBoolean(true));
        connection.Set("Disconnect", DynValue.NewCallback((_, _) =>
        {
            if (!connection.Get("Connected").CastToBool()) return DynValue.Nil;
            connection.Set("Connected", DynValue.NewBoolean(false));
            callbacks.Remove(callback);
            if (callbacks.Count == 0) registry.Remove(nodeId);
            return DynValue.Nil;
        }));
        return DynValue.NewTable(connection);
    }

    private DynValue WaitForChild(CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        var parent = ReadNodeArgument(args[0]);
        if (parent is null && args[0].Type == DataType.String)
            parent = args[0].String switch
            {
                "Gui" or "StarterGui" => project.Gui,
                "Lighting" => project.Lighting,
                "ReplicatedStorage" => project.ReplicatedStorage,
                "ServerScriptService" => project.ServerScriptService,
                "ServerStorage" => project.ServerStorage,
                _ => project.Workspace
            };
        var name = args[1].CastToString();
        if (string.IsNullOrEmpty(name)) return DynValue.Nil;
        var node = (parent?.Children ?? project.Workspace.Children).FirstOrDefault(child => child.Name == name || child.Id == name);
        return node is not null && instances.TryGetValue(node.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private int GetInstanceMethodArgumentIndex(SceneNode node, CallbackArguments args) =>
        args.Count > 0 && ReadNodeArgument(args[0])?.Id == node.Id ? 1 : 0;

    private DynValue FindFirstChild(SceneNode parent, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(parent, args);
        if (args.Count <= index || args[index].CastToString() is not { } name) return DynValue.Nil;
        var recursive = args.Count > index + 1 && args[index + 1].Type == DataType.Boolean && args[index + 1].Boolean;
        var candidates = recursive ? GameProjectStore.Flatten(parent.Children) : parent.Children;
        var child = candidates.FirstOrDefault(node => node.Name == name || node.Id == name);
        return child is not null && instances.TryGetValue(child.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private DynValue FindFirstAncestorByName(SceneNode node, CallbackArguments args)
    {
        var index = GetInstanceMethodArgumentIndex(node, args);
        var name = args.Count > index ? args[index].CastToString() : null;
        if (name is null) return DynValue.Nil;
        for (var parent = FindParentNode(node); parent is not null; parent = FindParentNode(parent))
            if (parent.Name == name && instances.TryGetValue(parent.Id, out var instance)) return DynValue.NewTable(instance);
        return DynValue.Nil;
    }

    private DynValue FindFirstAncestorByClass(SceneNode node, CallbackArguments args, bool whichIsA)
    {
        var index = GetInstanceMethodArgumentIndex(node, args);
        var className = args.Count > index ? args[index].CastToString() : null;
        if (className is null) return DynValue.Nil;
        for (var parent = FindParentNode(node); parent is not null; parent = FindParentNode(parent))
            if ((whichIsA ? IsA(parent, className) : parent.Type == className) && instances.TryGetValue(parent.Id, out var instance))
                return DynValue.NewTable(instance);
        return DynValue.Nil;
    }

    private DynValue GetChildren(SceneNode node)
    {
        var children = new Table(script);
        var index = 1;
        foreach (var child in node.Children)
        {
            if (!instances.TryGetValue(child.Id, out var instance)) continue;
            children.Set(index++, DynValue.NewTable(instance));
        }
        return DynValue.NewTable(children);
    }

    private static bool IsA(SceneNode node, string className) => className switch
    {
        "Instance" => true,
        "WorldRoot" => node.Type is "Workspace" or "WorldModel",
        "Model" => node.Type is "Workspace" or "Model" or "WorldModel",
        "PVInstance" => node.Type is "Workspace" or "Model" or "WorldModel" or "Camera" or "Terrain" || IsBasePart(node),
        "BaseRemoteEvent" => node.Type is "RemoteEvent" or "UnreliableRemoteEvent",
        "RemoteEvent" => node.Type == "RemoteEvent",
        "UnreliableRemoteEvent" => node.Type == "UnreliableRemoteEvent",
        "Accoutrement" => node.Type == "Accessory",
        "BodyMover" => node.Type is "BodyForce" or "BodyThrust" or "BodyVelocity" or "BodyPosition" or "BodyGyro" or "BodyAngularVelocity" or "RocketPropulsion",
        "JointInstance" => node.Type is "Weld" or "Motor6D",
        "Constraint" => node.Type is "WeldConstraint" or "NoCollisionConstraint" or "VectorForce" or "LineForce" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity",
        "SlidingBallConstraint" => node.Type is "PrismaticConstraint" or "CylindricalConstraint",
        "BasePart" => IsBasePart(node) || node.Type == "Terrain",
        "Light" => SceneNodeTypes.IsLight(node),
        "PostEffect" => SceneNodeTypes.IsPostEffectClass(node),
        "SoundEffect" => node.Type is "PitchShiftSoundEffect" or "DistortionSoundEffect" or "EqualizerSoundEffect" or "ChorusSoundEffect" or "CompressorSoundEffect" or "EchoSoundEffect" or "FlangeSoundEffect" or "TremoloSoundEffect" or "ReverbSoundEffect",
        "ValueBase" => SceneNodeTypes.IsValueObject(node),
        "GuiBase3d" => node.Type is "SelectionBox" or "SelectionSphere",
        "BasePlayerGui" => node.Type == "PlayerGui",
        "BasePlayerScripts" => node.Type == "PlayerScripts",
        "InstanceAdornment" => node.Type == "SelectionBox",
        "PVAdornment" => node.Type == "SelectionSphere",
        "UIComponent" => node.Type is "UICorner" or "UIOutline" or "UIStroke" or "UIGradient" or "UIShadow" or "UIPadding" or "UIScale" or "UIListLayout" or "UIGridLayout" or "UITableLayout" or "UIPageLayout" or "UIAspectRatio" or "UIAspectRatioConstraint" or "UISizeConstraint" or "UITextSizeConstraint" or "UIFlexItem",
        "LuaSourceContainer" => node.Type is "Script" or "LocalScript" or "ModuleScript",
        "BaseScript" => node.Type is "Script" or "LocalScript",
        "FaceInstance" or "Decal" => node.Type is "Decal" or "Texture",
        "GuiObject" => node.Type is "Frame" or "UICanvasGroup" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox",
        "GuiBase2d" => node.Type is "ScreenGui" or "BillboardGui" or "SurfaceGui" or "Frame" or "UICanvasGroup" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox",
        "LayerCollector" => node.Type is "ScreenGui" or "BillboardGui" or "SurfaceGui",
        _ => node.Type == className
    };

    private static bool IsBasePart(SceneNode? node) => SceneNodeTypes.IsBasePart(node);

    private bool HasPositionProperty(SceneNode node) =>
        IsBasePart(node) || node.Type is "Attachment" or "Model" or "Camera" || IsGuiObject(node);

    private bool HasRotationProperty(SceneNode node) =>
        IsBasePart(node) || node.Type is "Attachment" or "Model" or "Camera" or "AlignOrientation" || IsGuiObject(node);

    private bool TryGetAttachmentWorldTransform(SceneNode attachment, out Vector3 position, out float[] rotation)
    {
        position = default;
        rotation = [0, 0, 0];
        var parent = FindParentNode(attachment);
        if (attachment.Type != "Attachment" || parent is null || !IsBasePart(parent)) return false;
        var parentRotation = RotationMath.FromDegrees(parent.Rotation);
        position = new Vector3(parent.Position[0], parent.Position[1], parent.Position[2])
            + Vector3.Transform(new Vector3(attachment.Position[0], attachment.Position[1], attachment.Position[2]), parentRotation);
        rotation = RotationMath.ToDegrees(RotationMath.FromDegrees(attachment.Rotation) * parentRotation);
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) && rotation.All(float.IsFinite);
    }

    private DynValue GetAttachmentWorldTransformValue(SceneNode attachment, bool getRotation)
    {
        if (!TryGetAttachmentWorldTransform(attachment, out var position, out var rotation)) return DynValue.Nil;
        return VectorValue(getRotation ? rotation : [position.X, position.Y, position.Z]);
    }

    private bool HasSizeProperty(SceneNode node) =>
        IsBasePart(node) || node.Type is "BillboardGui" or "ParticleEmitter" or "PrismaticConstraint" or "CylindricalConstraint"
        || IsGuiObject(node) || SceneNodeTypes.IsSizedParticleEffect(node) || SceneNodeTypes.IsPostEffect(node);

    private static float[] LimitVector(float[] vector, float minimum, float maximum) =>
        vector.Select(component => Math.Clamp(float.IsFinite(component) ? component : 0, minimum, maximum)).ToArray();

    private DynValue GetAssetPath(CallbackArguments args)
    {
        if (args.Count == 0 || args[0].CastToString() is not { } key) return DynValue.Nil;
        var normalizedKey = NormalizeAssetKey(key);
        var asset = project.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeAssetKey(item.RelativePath), normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return DynValue.Nil;
        try { return DynValue.NewString(GameProjectStore.GetSafeAssetFilePath(project, asset.RelativePath)); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DynValue.Nil; }
    }

    private static string NormalizeAssetKey(string value)
    {
        var normalized = value.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? normalized[7..] : normalized;
    }

    private DynValue ConnectTouched(ScriptExecutionContext context, CallbackArguments args)
    {
        if (args.Count > 1 && args[1].Type == DataType.Function && ReadNodeArgument(args[0]) is { } node) return RegisterTouch(node.Id, args[1]);
        return DynValue.Nil;
    }

    private DynValue RegisterTouch(string id, DynValue callback)
    {
        if (currentGameScript is null) return DynValue.Nil;
        if (!touchedCallbacks.TryGetValue(id, out var callbacks)) touchedCallbacks[id] = callbacks = [];
        callbacks.Add(new ScriptCallback(currentGameScript, callback, "Touched"));
        return DynValue.Nil;
    }

    private DynValue RegisterGuiActivated(string id, DynValue callback)
    {
        if (currentGameScript is null) return DynValue.Nil;
        if (!guiActivatedCallbacks.TryGetValue(id, out var callbacks)) guiActivatedCallbacks[id] = callbacks = [];
        callbacks.Add(new ScriptCallback(currentGameScript, callback, "Activated"));
        return DynValue.Nil;
    }

    private Table CreateToolSignal(string id, string eventName, Dictionary<string, List<ScriptCallback>> registry)
    {
        var signal = new Table(script);
        signal.Set("Connect", DynValue.NewCallback((_, args) =>
        {
            var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
            if (currentGameScript is null || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
            if (!registry.TryGetValue(id, out var callbacks)) registry[id] = callbacks = [];
            callbacks.Add(new ScriptCallback(currentGameScript, args[index], eventName));
            return DynValue.Nil;
        }));
        return signal;
    }

    private DynValue RegisterGuiTextChanged(string id, DynValue callback)
    {
        if (currentGameScript is null) return DynValue.Nil;
        if (!guiTextChangedCallbacks.TryGetValue(id, out var callbacks)) guiTextChangedCallbacks[id] = callbacks = [];
        callbacks.Add(new ScriptCallback(currentGameScript, callback, "TextChanged"));
        return DynValue.Nil;
    }

    private DynValue RegisterPromptTriggered(string id, DynValue callback)
    {
        if (currentGameScript is null) return DynValue.Nil;
        if (!promptTriggeredCallbacks.TryGetValue(id, out var callbacks)) promptTriggeredCallbacks[id] = callbacks = [];
        callbacks.Add(new ScriptCallback(currentGameScript, callback, "Triggered"));
        return DynValue.Nil;
    }

    private DynValue RegisterClickDetector(string id, DynValue callback)
    {
        if (currentGameScript is null) return DynValue.Nil;
        if (!clickDetectorCallbacks.TryGetValue(id, out var callbacks)) clickDetectorCallbacks[id] = callbacks = [];
        callbacks.Add(new ScriptCallback(currentGameScript, callback, "MouseClick"));
        return DynValue.Nil;
    }

    private void DispatchTouch(SceneNode receiver, SceneNode other)
    {
        if (!touchedCallbacks.TryGetValue(receiver.Id, out var callbacks) || !instances.TryGetValue(other.Id, out var otherInstance)) return;
        foreach (var callback in callbacks.ToArray()) StartCallback(callback, DynValue.NewTable(otherInstance));
    }

    private void SynchronizeInstances()
    {
        var changed = new Dictionary<(string NodeId, string Property), SceneNode>();
        void MarkChanged(SceneNode node, string property) => changed.TryAdd((node.Id, property), node);
        void Assign<T>(SceneNode node, string property, T current, T next, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, next)) return;
            assign(next);
            MarkChanged(node, property);
        }

        foreach (var node in KnownNodes())
        {
            if (!instances.TryGetValue(node.Id, out var instance)) continue;
            if (SceneNodeTypes.IsValueObject(node))
            {
                if (TrySetValueObjectValue(node, instance.Get("Value"), out var valueChanged) && valueChanged)
                    MarkChanged(node, "Value");
                instance.Set("Value", GetValueObjectValue(node));
            }
            if (node.Type == "Model")
            {
                var requestedPrimary = instance.Get("PrimaryPart");
                var requestedPart = requestedPrimary.Type == DataType.Nil ? null : ReadNodeArgument(requestedPrimary);
                if ((requestedPrimary.Type == DataType.Nil || requestedPart is not null && SceneModelService.ContainsPrimaryPart(node, requestedPart.Id))
                    && node.PrimaryPart != (requestedPart?.Id ?? string.Empty))
                {
                    var priorPivot = SceneModelService.GetPivot(node);
                    var priorRotation = (float[])node.Rotation.Clone();
                    var requestedPosition = LimitVector(ReadVector(instance.Get("Position"), priorPivot), -1000000, 1000000);
                    var requestedRotation = LimitVector(ReadVector(instance.Get("Rotation"), priorRotation), -360000, 360000);
                    var positionWasSet = !requestedPosition.SequenceEqual(priorPivot);
                    var rotationWasSet = !requestedRotation.SequenceEqual(priorRotation);
                    var pending = CapturePendingModelDescendantWrites(node);
                    var priorModelPosition = (float[])node.Position.Clone();
                    SceneModelService.SetPrimaryPart(node, requestedPart);
                    MarkChanged(node, "PrimaryPart");
                    if (!node.Position.SequenceEqual(priorModelPosition)) MarkChanged(node, "Position");
                    if (!node.Rotation.SequenceEqual(priorRotation)) MarkChanged(node, "Rotation");
                    if (positionWasSet && SceneModelService.MoveTo(node, requestedPosition))
                    {
                        MarkChanged(node, "Position");
                        UpdateModelDescendantInstances(node, false, pending, MarkChanged);
                    }
                    else instance.Set("Position", VectorValue(SceneModelService.GetPivot(node)));
                    if (rotationWasSet && SceneModelService.RotateTo(node, requestedRotation))
                    {
                        MarkChanged(node, "Rotation");
                        UpdateModelDescendantInstances(node, true, pending, MarkChanged);
                    }
                    else instance.Set("Rotation", VectorValue(node.Rotation));
                }
                instance.Set("PrimaryPart", GetModelPrimaryPartReference(node));
            }
            if (IsGuiObject(node) && TryReadVector2(instance.Get("AnchorPoint"), out var anchorPoint))
            {
                anchorPoint = [Math.Clamp(anchorPoint[0], 0, 1), Math.Clamp(anchorPoint[1], 0, 1)];
                if (!node.AnchorPoint.SequenceEqual(anchorPoint)) { node.AnchorPoint = anchorPoint; MarkChanged(node, "AnchorPoint"); }
                instance.Set("AnchorPoint", Vector2Value(node.AnchorPoint));
            }
            if ((IsGuiObject(node) || node.Type is "BillboardGui" or "SurfaceGui") && instance.Get("ClipsDescendants").Type == DataType.Boolean)
                Assign(node, "ClipsDescendants", node.ClipsDescendants, instance.Get("ClipsDescendants").Boolean, value => node.ClipsDescendants = value);
            if (IsGuiObject(node) && IsTypedValue(instance.Get("BorderColor3"), "Color3"))
                Assign(node, "BorderColor3", node.BorderColor3, ReadColor(instance.Get("BorderColor3"), node.BorderColor3), value => node.BorderColor3 = value);
            if (IsGuiObject(node) && instance.Get("BorderSizePixel").Type == DataType.Number
                && double.IsFinite(instance.Get("BorderSizePixel").Number))
                Assign(node, "BorderSizePixel", node.BorderSizePixel, (int)Math.Clamp(Math.Round(instance.Get("BorderSizePixel").Number), 0, 64), value => node.BorderSizePixel = value);
            if (IsGuiObject(node) && node.Type != "ScrollingFrame"
                && instance.Get("AutomaticSize").CastToString() is { } automaticSize && automaticSize is ("None" or "X" or "Y" or "XY"))
                Assign(node, "AutomaticSize", node.AutomaticSize, automaticSize, value => node.AutomaticSize = value);
            if (node.Type == "ScrollingFrame")
            {
                if (TryReadVector2(instance.Get("CanvasPosition"), out var canvasPosition))
                {
                    var nextCanvasPosition = canvasPosition.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 1000000) : 0).ToArray();
                    if (!node.CanvasPosition.SequenceEqual(nextCanvasPosition)) { node.CanvasPosition = nextCanvasPosition; MarkChanged(node, "CanvasPosition"); }
                }
                if (TryReadUDim2(instance.Get("CanvasSize"), out var canvasSizeScale, out var canvasSize))
                {
                    var nextCanvasSizeScale = canvasSizeScale.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 100) : 0).ToArray();
                    var nextCanvasSize = canvasSize.Select(component => float.IsFinite(component) ? Math.Clamp(component, 0, 1000000) : 0).ToArray();
                    if (!node.CanvasSizeScale.SequenceEqual(nextCanvasSizeScale)) { node.CanvasSizeScale = nextCanvasSizeScale; MarkChanged(node, "CanvasSize"); }
                    if (!node.CanvasSize.SequenceEqual(nextCanvasSize)) { node.CanvasSize = nextCanvasSize; MarkChanged(node, "CanvasSize"); }
                }
                if (instance.Get("ScrollBarThickness") is { Type: DataType.Number } scrollBarThickness
                    && double.IsFinite(scrollBarThickness.Number) && scrollBarThickness.Number == Math.Truncate(scrollBarThickness.Number))
                    Assign(node, "ScrollBarThickness", node.ScrollBarThickness, (int)Math.Clamp(scrollBarThickness.Number, 0, 100), value => node.ScrollBarThickness = value);
                if (instance.Get("ScrollingEnabled").Type == DataType.Boolean)
                    Assign(node, "ScrollingEnabled", node.ScrollingEnabled, instance.Get("ScrollingEnabled").Boolean, value => node.ScrollingEnabled = value);
                if (instance.Get("AutomaticCanvasSize").CastToString() is { } automaticCanvasSize && automaticCanvasSize is ("None" or "X" or "Y" or "XY"))
                    Assign(node, "AutomaticCanvasSize", node.AutomaticCanvasSize, automaticCanvasSize, value => node.AutomaticCanvasSize = value);
                if (instance.Get("ScrollingDirection").CastToString() is { } scrollingDirection && scrollingDirection is ("X" or "Y" or "XY"))
                    Assign(node, "ScrollingDirection", node.ScrollingDirection, scrollingDirection, value => node.ScrollingDirection = value);
            }
            if (HasPositionProperty(node) && instance.Get("Position").Type == DataType.Table)
            {
                if (node.Type == "Model")
                {
                    var position = LimitVector(ReadVector(instance.Get("Position"), SceneModelService.GetPivot(node)), -1000000, 1000000);
                    var pending = CapturePendingModelDescendantWrites(node);
                    if (SceneModelService.MoveTo(node, position))
                    {
                        MarkChanged(node, "Position");
                        UpdateModelDescendantInstances(node, false, pending, MarkChanged);
                    }
                }
                else if (IsGuiObject(node) && TryReadUDim2(instance.Get("Position"), out var positionScale, out var positionOffset))
                {
                    if (!node.PositionScale.SequenceEqual(positionScale) || !node.Position.Take(2).SequenceEqual(positionOffset))
                    {
                        node.PositionScale = positionScale;
                        node.Position = [positionOffset[0], positionOffset[1], node.Position[2]];
                        MarkChanged(node, "Position");
                    }
                }
                else
                {
                    var position = LimitVector(ReadVector(instance.Get("Position"), node.Position), -1000000, 1000000);
                    if (!node.Position.SequenceEqual(position)) { node.Position = position; MarkChanged(node, "Position"); }
                }
            }
            if (HasRotationProperty(node) && instance.Get("Rotation").Type == DataType.Table)
            {
                var rotation = LimitVector(ReadVector(instance.Get("Rotation"), node.Rotation), -360000, 360000);
                if (node.Type == "AlignOrientation")
                {
                    rotation = rotation.Select(component => Math.Clamp(component, -360000, 360000)).ToArray();
                    if (!node.AlignOrientationRotation.SequenceEqual(rotation)) { node.AlignOrientationRotation = rotation; MarkChanged(node, "Rotation"); }
                }
                else if (node.Type == "Model")
                {
                    var pending = CapturePendingModelDescendantWrites(node);
                    if (SceneModelService.RotateTo(node, rotation))
                    {
                        MarkChanged(node, "Rotation");
                        UpdateModelDescendantInstances(node, true, pending, MarkChanged);
                    }
                }
                else if (!node.Rotation.SequenceEqual(rotation)) { node.Rotation = rotation; MarkChanged(node, "Rotation"); }
            }
            if (node.Type == "ParticleEmitter" && TryReadNumberSequence(instance.Get("Size"), out var particleSize))
            {
                var normalized = ParticleSequenceService.NormalizeNumber(particleSize, 0, 100);
                if (!node.SizeSequence.SequenceEqual(normalized))
                {
                    node.SizeSequence = normalized;
                    node.EffectSize = normalized[0].Value;
                    MarkChanged(node, "Size");
                }
            }
            else if ((SceneNodeTypes.IsSizedParticleEffect(node) || SceneNodeTypes.IsPostEffect(node)) && instance.Get("Size").Type == DataType.Number && double.IsFinite(instance.Get("Size").Number))
                Assign(node, "Size", node.EffectSize, (float)Math.Clamp(instance.Get("Size").Number, node.Type == "Fire" ? 2 : SceneNodeTypes.IsPostEffect(node) ? 0 : .1f, node.Type == "Fire" ? 30 : SceneNodeTypes.IsPostEffect(node) ? 56 : 100), value => node.EffectSize = value);
            else if (HasSizeProperty(node) && instance.Get("Size").Type == DataType.Table && !SceneNodeTypes.IsSizedParticleEffect(node) && node.Type != "ParticleEmitter")
            {
                if ((IsGuiObject(node) || node.Type == "BillboardGui") && TryReadUDim2(instance.Get("Size"), out var sizeScale, out var sizeOffset))
                {
                    if (!node.SizeScale.SequenceEqual(sizeScale) || !node.Size.Take(2).SequenceEqual(sizeOffset))
                    {
                        node.SizeScale = sizeScale;
                        node.Size = [sizeOffset[0], sizeOffset[1], node.Size[2]];
                        MarkChanged(node, "Size");
                    }
                }
                else
                {
                    var size = LimitVector(ReadVector(instance.Get("Size"), node.Size), .001f, 1000000);
                    if (!node.Size.SequenceEqual(size)) { node.Size = size; MarkChanged(node, "Size"); }
                }
            }
            if (node.Type == "Sparkles")
            {
                var sparkleColor = ReadColor(instance.Get("SparkleColor"), node.Color);
                var colorAlias = ReadColor(instance.Get("Color"), node.Color);
                Assign(node, "SparkleColor", node.Color, sparkleColor != node.Color ? sparkleColor : colorAlias, value => node.Color = value);
                instance.Set("Color", ColorValue(node.Color));
                instance.Set("SparkleColor", ColorValue(node.Color));
            }
            else if (node.Type is "ParticleEmitter" or "Trail")
            {
                if (TryReadColorSequence(instance.Get("Color"), out var particleColor))
                {
                    var normalized = ParticleSequenceService.NormalizeColor(particleColor);
                    if (!node.ColorSequence.SequenceEqual(normalized))
                    {
                        node.ColorSequence = normalized;
                        node.Color = normalized[0].Value;
                        MarkChanged(node, "Color");
                    }
                }
            }
            else if (SceneNodeTypes.IsSurfaceAppearance(node)) Assign(node, "Color", node.SurfaceAppearanceColor, ReadColor(instance.Get("Color"), node.SurfaceAppearanceColor), value => node.SurfaceAppearanceColor = value);
            else if (node.Type is not ("SelectionBox" or "SelectionSphere")) Assign(node, "Color", node.Color, ReadColor(instance.Get("Color"), node.Color), value => node.Color = value);
            if (SceneNodeTypes.IsSurfaceAppearance(node))
            {
                Assign(node, "ColorMap", node.SurfaceAppearanceColorMap, instance.Get("ColorMap").CastToString() ?? string.Empty, value => node.SurfaceAppearanceColorMap = value);
                Assign(node, "NormalMap", node.SurfaceAppearanceNormalMap, instance.Get("NormalMap").CastToString() ?? string.Empty, value => node.SurfaceAppearanceNormalMap = value);
                Assign(node, "RoughnessMap", node.SurfaceAppearanceRoughnessMap, instance.Get("RoughnessMap").CastToString() ?? string.Empty, value => node.SurfaceAppearanceRoughnessMap = value);
                Assign(node, "MetalnessMap", node.SurfaceAppearanceMetalnessMap, instance.Get("MetalnessMap").CastToString() ?? string.Empty, value => node.SurfaceAppearanceMetalnessMap = value);
                Assign(node, "EmissiveMaskContent", node.SurfaceAppearanceEmissiveMask, instance.Get("EmissiveMaskContent").CastToString() ?? string.Empty, value => node.SurfaceAppearanceEmissiveMask = value);
                Assign(node, "EmissiveTint", node.SurfaceAppearanceEmissiveTint, ReadColor(instance.Get("EmissiveTint"), node.SurfaceAppearanceEmissiveTint), value => node.SurfaceAppearanceEmissiveTint = value);
                if (instance.Get("EmissiveStrength").Type == DataType.Number && double.IsFinite(instance.Get("EmissiveStrength").Number))
                    Assign(node, "EmissiveStrength", node.SurfaceAppearanceEmissiveStrength, (float)Math.Clamp(instance.Get("EmissiveStrength").Number, 0, 100), value => node.SurfaceAppearanceEmissiveStrength = value);
            }
            if (node.Type == "Fire") Assign(node, "SecondaryColor", node.SecondaryColor, ReadColor(instance.Get("SecondaryColor"), node.SecondaryColor), value => node.SecondaryColor = value);
            if (node.Type == "UIGradient")
            {
                Assign(node, "StartColor", node.StartColor, ReadColor(instance.Get("StartColor"), node.StartColor), value => node.StartColor = value);
                Assign(node, "EndColor", node.EndColor, ReadColor(instance.Get("EndColor"), node.EndColor), value => node.EndColor = value);
            }
            if (node.Type == "UIPadding")
            {
                if (TryReadUDim(instance.Get("PaddingLeft"), out var left) && !node.PaddingLeft.SequenceEqual(left)) { node.PaddingLeft = left; MarkChanged(node, "PaddingLeft"); }
                if (TryReadUDim(instance.Get("PaddingTop"), out var top) && !node.PaddingTop.SequenceEqual(top)) { node.PaddingTop = top; MarkChanged(node, "PaddingTop"); }
                if (TryReadUDim(instance.Get("PaddingRight"), out var right) && !node.PaddingRight.SequenceEqual(right)) { node.PaddingRight = right; MarkChanged(node, "PaddingRight"); }
                if (TryReadUDim(instance.Get("PaddingBottom"), out var bottom) && !node.PaddingBottom.SequenceEqual(bottom)) { node.PaddingBottom = bottom; MarkChanged(node, "PaddingBottom"); }
            }
            if (node.Type == "UIScale" && instance.Get("Scale").Type == DataType.Number && double.IsFinite(instance.Get("Scale").Number))
                Assign(node, "Scale", node.Scale, (float)Math.Clamp(instance.Get("Scale").Number, .01, 100), value => node.Scale = value);
            if (node.Type == "Highlight")
            {
                Assign(node, "FillColor", node.FillColor, ReadColor(instance.Get("FillColor"), node.FillColor), value => node.FillColor = value);
                Assign(node, "OutlineColor", node.OutlineColor, ReadColor(instance.Get("OutlineColor"), node.OutlineColor), value => node.OutlineColor = value);
                if (instance.Get("FillTransparency").Type == DataType.Number && double.IsFinite(instance.Get("FillTransparency").Number))
                    Assign(node, "FillTransparency", node.FillTransparency, (float)Math.Clamp(instance.Get("FillTransparency").Number, 0, 1), value => node.FillTransparency = value);
                if (instance.Get("OutlineTransparency").Type == DataType.Number && double.IsFinite(instance.Get("OutlineTransparency").Number))
                    Assign(node, "OutlineTransparency", node.OutlineTransparency, (float)Math.Clamp(instance.Get("OutlineTransparency").Number, 0, 1), value => node.OutlineTransparency = value);
            }
            if (node.Type is "SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui")
            {
                var requestedAdornee = ReadNodeArgument(instance.Get("Adornee"));
                if (requestedAdornee is not null && (IsBasePart(requestedAdornee) || node.Type is not ("BillboardGui" or "SurfaceGui") && requestedAdornee.Type == "Model"))
                    Assign(node, "Adornee", node.Adornee, requestedAdornee.Id, value => node.Adornee = value);
                else if (instance.Get("Adornee").Type == DataType.Nil)
                    Assign(node, "Adornee", node.Adornee, string.Empty, value => node.Adornee = value);
            }
            if (node.Type is "SelectionBox" or "SelectionSphere")
            {
                var color3 = ReadColor(instance.Get("Color3"), node.Color);
                var colorAlias = ReadColor(instance.Get("Color"), node.Color);
                if (color3 != node.Color) Assign(node, "Color3", node.Color, color3, value => node.Color = value);
                else Assign(node, "Color", node.Color, colorAlias, value => node.Color = value);
                instance.Set("Color3", ColorValue(node.Color));
                instance.Set("Color", ColorValue(node.Color));
                Assign(node, "SurfaceColor3", node.SurfaceColor3, ReadColor(instance.Get("SurfaceColor3"), node.SurfaceColor3), value => node.SurfaceColor3 = value);
                if (instance.Get("SurfaceTransparency") is { Type: DataType.Number } surfaceTransparency && double.IsFinite(surfaceTransparency.Number))
                    Assign(node, "SurfaceTransparency", node.SurfaceTransparency, (float)Math.Clamp(surfaceTransparency.Number, 0, 1), value => node.SurfaceTransparency = value);
                if (node.Type == "SelectionBox" && instance.Get("LineThickness") is { Type: DataType.Number } lineThickness && double.IsFinite(lineThickness.Number))
                    Assign(node, "LineThickness", node.LineThickness, (float)Math.Clamp(lineThickness.Number, 0, 10), value => node.LineThickness = value);
            }
            if (node.Type == "Part" && instance.Get("Shape").CastToString() is "Block" or "Ball" or "Cylinder")
                Assign(node, "Shape", node.Shape, instance.Get("Shape").String, value => node.Shape = value);
            if (node.Type is ("Decal" or "Texture" or "SurfaceLight" or "SurfaceGui") && SceneNodeTypes.IsFace(instance.Get("Face").CastToString()))
                Assign(node, "Face", node.Face, instance.Get("Face").String, value => node.Face = value);
            if (node.Type == "Texture")
            {
                if (instance.Get("StudsPerTileU").Type == DataType.Number && double.IsFinite(instance.Get("StudsPerTileU").Number))
                    Assign(node, "StudsPerTileU", node.StudsPerTileU, (float)Math.Clamp(instance.Get("StudsPerTileU").Number, .05, 1000), value => node.StudsPerTileU = value);
                if (instance.Get("StudsPerTileV").Type == DataType.Number && double.IsFinite(instance.Get("StudsPerTileV").Number))
                    Assign(node, "StudsPerTileV", node.StudsPerTileV, (float)Math.Clamp(instance.Get("StudsPerTileV").Number, .05, 1000), value => node.StudsPerTileV = value);
                if (instance.Get("OffsetStudsU").Type == DataType.Number && double.IsFinite(instance.Get("OffsetStudsU").Number))
                    Assign(node, "OffsetStudsU", node.OffsetStudsU, (float)Math.Clamp(instance.Get("OffsetStudsU").Number, -10000, 10000), value => node.OffsetStudsU = value);
                if (instance.Get("OffsetStudsV").Type == DataType.Number && double.IsFinite(instance.Get("OffsetStudsV").Number))
                    Assign(node, "OffsetStudsV", node.OffsetStudsV, (float)Math.Clamp(instance.Get("OffsetStudsV").Number, -10000, 10000), value => node.OffsetStudsV = value);
            }
            if (IsBasePart(node) && SceneNodeTypes.IsMaterial(instance.Get("Material").CastToString()))
                Assign(node, "Material", node.Material, instance.Get("Material").String, value => node.Material = value);
            if (node.Type is "ParticleEmitter" or "Trail" && TryReadNumberSequence(instance.Get("Transparency"), out var particleTransparency))
            {
                var normalized = ParticleSequenceService.NormalizeNumber(particleTransparency, 0, 1);
                if (!node.TransparencySequence.SequenceEqual(normalized))
                {
                    node.TransparencySequence = normalized;
                    node.Transparency = normalized[0].Value;
                    MarkChanged(node, "Transparency");
                }
            }
            else if ((IsBasePart(node) || node.Type is "Decal" or "Texture" or "Beam" or "SelectionBox" or "SelectionSphere" or "UIShadow") && instance.Get("Transparency").Type == DataType.Number && double.IsFinite(instance.Get("Transparency").Number))
                Assign(node, "Transparency", node.Transparency, (float)Math.Clamp(instance.Get("Transparency").Number, 0, 1), value => node.Transparency = value);
            if (IsGuiObject(node) && instance.Get("BackgroundTransparency") is { Type: DataType.Number } backgroundTransparency && double.IsFinite(backgroundTransparency.Number))
                Assign(node, "BackgroundTransparency", node.BackgroundTransparency, (float)Math.Clamp(backgroundTransparency.Number, 0, 1), value => node.BackgroundTransparency = value);
            if (node.Type == "UICanvasGroup" && instance.Get("GroupTransparency") is { Type: DataType.Number } groupTransparency && double.IsFinite(groupTransparency.Number))
                Assign(node, "GroupTransparency", node.GroupTransparency, (float)Math.Clamp(groupTransparency.Number, 0, 1), value => node.GroupTransparency = value);
            if (node.Type is "TextLabel" or "TextButton" or "TextBox" && instance.Get("TextTransparency") is { Type: DataType.Number } textTransparency && double.IsFinite(textTransparency.Number))
                Assign(node, "TextTransparency", node.TextTransparency, (float)Math.Clamp(textTransparency.Number, 0, 1), value => node.TextTransparency = value);
            if (IsGuiObject(node) && node.Type is "ImageLabel" or "ImageButton" && instance.Get("ImageTransparency") is { Type: DataType.Number } imageTransparency && double.IsFinite(imageTransparency.Number))
                Assign(node, "ImageTransparency", node.ImageTransparency, (float)Math.Clamp(imageTransparency.Number, 0, 1), value => node.ImageTransparency = value);
            if (node.Type is "ImageLabel" or "ImageButton")
            {
                Assign(node, "ImageColor3", node.ImageColor3, ReadColor(instance.Get("ImageColor3"), node.ImageColor3), value => node.ImageColor3 = value);
                if (instance.Get("ScaleType").Type == DataType.String && instance.Get("ScaleType").String is "Stretch" or "Fit" or "Crop" or "Tile" or "Slice")
                    Assign(node, "ScaleType", node.ScaleType, instance.Get("ScaleType").String, value => node.ScaleType = value);
                if (TryReadRect(instance.Get("SliceCenter"), out var sliceCenter) && !node.SliceCenter.SequenceEqual(sliceCenter))
                    Assign(node, "SliceCenter", node.SliceCenter, NormalizeSliceCenter(sliceCenter), value => node.SliceCenter = value);
                if (instance.Get("SliceScale").Type == DataType.Number && double.IsFinite(instance.Get("SliceScale").Number))
                    Assign(node, "SliceScale", node.SliceScale, (float)Math.Clamp(instance.Get("SliceScale").Number, .01, 100), value => node.SliceScale = value);
                if (TryReadVector2(instance.Get("ImageRectOffset"), out var imageRectOffset) && !node.ImageRectOffset.SequenceEqual(imageRectOffset))
                    Assign(node, "ImageRectOffset", node.ImageRectOffset, imageRectOffset.Select(component => Math.Clamp(component, 0, 1000000)).ToArray(), value => node.ImageRectOffset = value);
                if (TryReadVector2(instance.Get("ImageRectSize"), out var imageRectSize) && !node.ImageRectSize.SequenceEqual(imageRectSize))
                    Assign(node, "ImageRectSize", node.ImageRectSize, imageRectSize.Select(component => Math.Clamp(component, 0, 1000000)).ToArray(), value => node.ImageRectSize = value);
            }
            if (node.Type == "BodyColors")
            {
                foreach (var property in new[] { "HeadColor3", "TorsoColor3", "LeftArmColor3", "RightArmColor3", "LeftLegColor3", "RightLegColor3" })
                    if (IsTypedValue(instance.Get(property), "Color3"))
                        Assign(node, property, BodyColorsService.GetColor(node, property), ReadColor(instance.Get(property), BodyColorsService.GetColor(node, property)), value => BodyColorsService.SetColor(node, property, value));
            }
            if (node.Type == "ImageButton")
            {
                if (instance.Get("HoverImage").Type == DataType.String)
                    Assign(node, "HoverImage", node.HoverImage, instance.Get("HoverImage").String, value => node.HoverImage = value);
                if (instance.Get("PressedImage").Type == DataType.String)
                    Assign(node, "PressedImage", node.PressedImage, instance.Get("PressedImage").String, value => node.PressedImage = value);
            }
            if (instance.Get("Enabled").Type == DataType.Boolean)
            {
                var wasEnabled = node.Enabled;
                Assign(node, "Enabled", node.Enabled, instance.Get("Enabled").Boolean, value => node.Enabled = value);
                if (SceneNodeTypes.IsBaseScript(node) && wasEnabled != node.Enabled)
                {
                    if (node.Enabled) QueueRunnableScripts(node);
                    else StopScripts([node.Id]);
                }
            }
            if (node.Type == "ColorCorrectionEffect")
            {
                if (IsTypedValue(instance.Get("TintColor"), "Color3")) Assign(node, "TintColor", node.TintColor, ReadColor(instance.Get("TintColor"), node.TintColor), value => node.TintColor = value);
                if (instance.Get("Brightness").Type == DataType.Number && double.IsFinite(instance.Get("Brightness").Number)) Assign(node, "Brightness", node.Brightness, (float)Math.Clamp(instance.Get("Brightness").Number, -1, 1), value => node.Brightness = value);
                if (instance.Get("Contrast").Type == DataType.Number && double.IsFinite(instance.Get("Contrast").Number)) Assign(node, "Contrast", node.ColorCorrectionContrast, (float)Math.Clamp(instance.Get("Contrast").Number, -1, 1), value => node.ColorCorrectionContrast = value);
                if (instance.Get("Saturation").Type == DataType.Number && double.IsFinite(instance.Get("Saturation").Number)) Assign(node, "Saturation", node.ColorCorrectionSaturation, (float)Math.Clamp(instance.Get("Saturation").Number, -1, 1), value => node.ColorCorrectionSaturation = value);
                instance.Set("TintColor", ColorValue(node.TintColor));
                instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
                instance.Set("Contrast", DynValue.NewNumber(node.ColorCorrectionContrast));
                instance.Set("Saturation", DynValue.NewNumber(node.ColorCorrectionSaturation));
            }
            if (node.Type == "Clouds")
            {
                if (instance.Get("Cover").Type == DataType.Number && double.IsFinite(instance.Get("Cover").Number)) Assign(node, "Cover", node.CloudCover, (float)Math.Clamp(instance.Get("Cover").Number, 0, 1), value => node.CloudCover = value);
                if (instance.Get("Density").Type == DataType.Number && double.IsFinite(instance.Get("Density").Number)) Assign(node, "Density", node.CloudDensity, (float)Math.Clamp(instance.Get("Density").Number, 0, 1), value => node.CloudDensity = value);
                if (IsTypedValue(instance.Get("Color"), "Color3")) Assign(node, "Color", node.Color, ReadColor(instance.Get("Color"), node.Color), value => node.Color = value);
                instance.Set("Cover", DynValue.NewNumber(node.CloudCover));
                instance.Set("Density", DynValue.NewNumber(node.CloudDensity));
                instance.Set("Color", ColorValue(node.Color));
            }
            if ((node.Type == "Lighting" || SceneNodeTypes.IsLight(node)) && instance.Get("Brightness").Type == DataType.Number && double.IsFinite(instance.Get("Brightness").Number)) Assign(node, "Brightness", node.Brightness, (float)Math.Clamp(instance.Get("Brightness").Number, 0, 8), value => node.Brightness = value);
            if (node.Type == "Lighting")
            {
                Assign(node, "Ambient", node.Ambient, ReadColor(instance.Get("Ambient"), node.Ambient), value => node.Ambient = value);
                if (instance.Get("ClockTime").Type == DataType.Number && double.IsFinite(instance.Get("ClockTime").Number))
                    Assign(node, "ClockTime", node.ClockTime, (float)Math.Clamp(instance.Get("ClockTime").Number, 0, 24), value => node.ClockTime = value);
                if (instance.Get("GlobalShadows").Type == DataType.Boolean)
                    Assign(node, "GlobalShadows", project.Settings.Shadows, instance.Get("GlobalShadows").Boolean, value => project.Settings.Shadows = value);
            }
            if (SceneNodeTypes.IsLight(node) && instance.Get("Range").Type == DataType.Number && double.IsFinite(instance.Get("Range").Number)) Assign(node, "Range", node.Range, (float)Math.Clamp(instance.Get("Range").Number, 1, 1000), value => node.Range = value);
            if (SceneNodeTypes.IsParticleEffect(node))
            {
                if (instance.Get("TimeScale").Type == DataType.Number && double.IsFinite(instance.Get("TimeScale").Number))
                    Assign(node, "TimeScale", node.TimeScale, (float)Math.Clamp(instance.Get("TimeScale").Number, 0, 1), value => node.TimeScale = value);
                if (node.Type == "ParticleEmitter")
                {
                    if (instance.Get("Rate").Type == DataType.Number && double.IsFinite(instance.Get("Rate").Number))
                        Assign(node, "Rate", node.Rate, (float)Math.Clamp(instance.Get("Rate").Number, 0, 200), value => node.Rate = value);
                    if (TryReadNumberRange(instance.Get("Speed"), out var speedRange))
                    {
                        var next = new[] { Math.Clamp(speedRange[0], -100, 100), Math.Clamp(speedRange[1], Math.Clamp(speedRange[0], -100, 100), 100) };
                        if (!node.SpeedRange.SequenceEqual(next)) { node.SpeedRange = next; MarkChanged(node, "Speed"); }
                    }
                    if (TryReadNumberRange(instance.Get("Lifetime"), out var lifetimeRange))
                    {
                        var next = new[] { Math.Clamp(lifetimeRange[0], .05f, 30), Math.Clamp(lifetimeRange[1], Math.Clamp(lifetimeRange[0], .05f, 30), 30) };
                        if (!node.LifetimeRange.SequenceEqual(next)) { node.LifetimeRange = next; MarkChanged(node, "Lifetime"); }
                    }
                    if (TryReadVector2(instance.Get("SpreadAngle"), out var spreadAngle))
                    {
                        var next = new[] { Math.Clamp(spreadAngle[0], 0, 360), Math.Clamp(spreadAngle[1], 0, 360) };
                        if (!node.SpreadAngle.SequenceEqual(next)) { node.SpreadAngle = next; MarkChanged(node, "SpreadAngle"); }
                    }
                    if (SceneNodeTypes.IsFace(instance.Get("EmissionDirection").CastToString()))
                        Assign(node, "EmissionDirection", node.EmissionDirection, instance.Get("EmissionDirection").String, value => node.EmissionDirection = value);
                }
                if (node.Type == "Fire" && instance.Get("Heat").Type == DataType.Number && double.IsFinite(instance.Get("Heat").Number))
                    Assign(node, "Heat", node.Heat, (float)Math.Clamp(instance.Get("Heat").Number, -25, 25), value => node.Heat = value);
                if (node.Type == "Smoke")
                {
                    if (instance.Get("RiseVelocity").Type == DataType.Number && double.IsFinite(instance.Get("RiseVelocity").Number))
                        Assign(node, "RiseVelocity", node.RiseVelocity, (float)Math.Clamp(instance.Get("RiseVelocity").Number, -25, 25), value => node.RiseVelocity = value);
                    if (instance.Get("Opacity").Type == DataType.Number && double.IsFinite(instance.Get("Opacity").Number))
                        Assign(node, "Opacity", node.Opacity, (float)Math.Clamp(instance.Get("Opacity").Number, 0, 1), value => node.Opacity = value);
                }
            }
            if (node.Type == "Explosion")
            {
                if (instance.Get("BlastRadius").Type == DataType.Number && double.IsFinite(instance.Get("BlastRadius").Number)) Assign(node, "BlastRadius", node.BlastRadius, (float)Math.Clamp(instance.Get("BlastRadius").Number, 0, 100), value => node.BlastRadius = value);
                if (instance.Get("BlastPressure").Type == DataType.Number && double.IsFinite(instance.Get("BlastPressure").Number)) Assign(node, "BlastPressure", node.BlastPressure, (float)Math.Clamp(instance.Get("BlastPressure").Number, 0, 1000000), value => node.BlastPressure = value);
                if (instance.Get("DestroyJointRadiusPercent").Type == DataType.Number && double.IsFinite(instance.Get("DestroyJointRadiusPercent").Number)) Assign(node, "DestroyJointRadiusPercent", node.DestroyJointRadiusPercent, (float)Math.Clamp(instance.Get("DestroyJointRadiusPercent").Number, 0, 1), value => node.DestroyJointRadiusPercent = value);
            }
            if ((node.Type is "SpotLight" or "SurfaceLight") && instance.Get("Angle").Type == DataType.Number && double.IsFinite(instance.Get("Angle").Number)) Assign(node, "Angle", node.Angle, (float)Math.Clamp(instance.Get("Angle").Number, 1, 175), value => node.Angle = value);
            if (node.Type == "ProximityPrompt")
            {
                if (instance.Get("ActionText").CastToString() is { } actionText) Assign(node, "ActionText", node.ActionText, actionText, value => node.ActionText = value);
                if (instance.Get("ObjectText").CastToString() is { } objectText) Assign(node, "ObjectText", node.ObjectText, objectText, value => node.ObjectText = value);
                if (instance.Get("HoldDuration").Type == DataType.Number && double.IsFinite(instance.Get("HoldDuration").Number))
                    Assign(node, "HoldDuration", node.HoldDuration, (float)Math.Clamp(instance.Get("HoldDuration").Number, 0, 60), value => node.HoldDuration = value);
                if (instance.Get("RequiresLineOfSight").Type == DataType.Boolean)
                    Assign(node, "RequiresLineOfSight", node.RequiresLineOfSight, instance.Get("RequiresLineOfSight").Boolean, value => node.RequiresLineOfSight = value);
                if (instance.Get("KeyboardKeyCode").Type == DataType.String
                    && SupportedKeyCodes.Contains(instance.Get("KeyboardKeyCode").String, StringComparer.Ordinal)
                    && instance.Get("KeyboardKeyCode").String is not ("Unknown" or "None"))
                    Assign(node, "KeyboardKeyCode", node.KeyboardKeyCode, instance.Get("KeyboardKeyCode").String, value => node.KeyboardKeyCode = value);
            }
            if (node.Type is "ProximityPrompt" or "ClickDetector"
                && instance.Get("MaxActivationDistance").Type == DataType.Number && double.IsFinite(instance.Get("MaxActivationDistance").Number))
                Assign(node, "MaxActivationDistance", node.MaxActivationDistance, (float)Math.Clamp(instance.Get("MaxActivationDistance").Number, 1, 100), value => node.MaxActivationDistance = value);
            if (node.Type is "BillboardGui" or "SurfaceGui")
            {
                if (instance.Get("AlwaysOnTop").Type == DataType.Boolean)
                    Assign(node, "AlwaysOnTop", node.AlwaysOnTop, instance.Get("AlwaysOnTop").Boolean, value => node.AlwaysOnTop = value);
                if (instance.Get("MaxDistance").Type == DataType.Number && double.IsFinite(instance.Get("MaxDistance").Number))
                    Assign(node, "MaxDistance", node.MaxDistance, (float)Math.Clamp(instance.Get("MaxDistance").Number, 0, 100000), value => node.MaxDistance = value);
                if (node.Type == "SurfaceGui" && instance.Get("PixelsPerStud").Type == DataType.Number && double.IsFinite(instance.Get("PixelsPerStud").Number))
                    Assign(node, "PixelsPerStud", node.PixelsPerStud, (float)Math.Clamp(instance.Get("PixelsPerStud").Number, 1, 1000), value => node.PixelsPerStud = value);
            }
            if (node.Type is "Sound" or "VideoFrame")
            {
                if (node.Type == "VideoFrame" && instance.Get("Video").Type == DataType.String)
                    Assign(node, "Video", node.Video, instance.Get("Video").String, value => node.Video = value);
                if (instance.Get("Volume").Type == DataType.Number && double.IsFinite(instance.Get("Volume").Number))
                    Assign(node, "Volume", node.Volume, (float)Math.Clamp(instance.Get("Volume").Number, 0, 1), value => node.Volume = value);
                if (instance.Get("Looped").Type == DataType.Boolean)
                    Assign(node, "Looped", node.Looped, instance.Get("Looped").Boolean, value => node.Looped = value);
                if (instance.Get("Playing").Type == DataType.Boolean)
                    Assign(node, "Playing", node.Playing, instance.Get("Playing").Boolean, value => node.Playing = value);
                if (node.Type == "Sound")
                {
                    if (instance.Get("PlaybackSpeed").Type == DataType.Number && double.IsFinite(instance.Get("PlaybackSpeed").Number))
                        Assign(node, "PlaybackSpeed", node.PlaybackSpeed, (float)Math.Clamp(instance.Get("PlaybackSpeed").Number, 0, 20), value => node.PlaybackSpeed = value);
                    if (instance.Get("RollOffMinDistance").Type == DataType.Number && double.IsFinite(instance.Get("RollOffMinDistance").Number))
                        Assign(node, "RollOffMinDistance", node.RollOffMinDistance, (float)Math.Clamp(instance.Get("RollOffMinDistance").Number, 0, 1000), value => node.RollOffMinDistance = value);
                    if (instance.Get("RollOffMaxDistance").Type == DataType.Number && double.IsFinite(instance.Get("RollOffMaxDistance").Number))
                        Assign(node, "RollOffMaxDistance", node.RollOffMaxDistance, (float)Math.Clamp(instance.Get("RollOffMaxDistance").Number, node.RollOffMinDistance + .01f, 10000), value => node.RollOffMaxDistance = value);
                    var soundGroupValue = instance.Get("SoundGroup");
                    var requestedSoundGroup = ReadNodeArgument(soundGroupValue);
                    if (requestedSoundGroup?.Type == "SoundGroup")
                        Assign(node, "SoundGroup", node.SoundGroupId, requestedSoundGroup.Id, value => node.SoundGroupId = value);
                    else if (soundGroupValue.Type == DataType.Nil)
                        Assign(node, "SoundGroup", node.SoundGroupId, string.Empty, value => node.SoundGroupId = value);
                }
            }
            if (node.Type == "SoundGroup" && instance.Get("Volume").Type == DataType.Number && double.IsFinite(instance.Get("Volume").Number))
            {
                Assign(node, "Volume", node.SoundGroupVolume, (float)Math.Clamp(instance.Get("Volume").Number, 0, 1), value => node.SoundGroupVolume = value);
            }
            if (node.Type == "SoundService" && instance.Get("Volume").Type == DataType.Number && double.IsFinite(instance.Get("Volume").Number))
            {
                Assign(node, "Volume", node.SoundServiceVolume, (float)Math.Clamp(instance.Get("Volume").Number, 0, 1), value => node.SoundServiceVolume = value);
            }
            if (node.Type == "CompressorSoundEffect")
            {
                var sideChainValue = instance.Get("SideChain");
                var sideChain = ReadNodeArgument(sideChainValue);
                if (sideChain?.Type is "Sound" or "SoundGroup")
                    Assign(node, "SideChain", node.CompressorSideChainId, sideChain.Id, value => node.CompressorSideChainId = value);
                else if (sideChainValue.Type == DataType.Nil)
                    Assign(node, "SideChain", node.CompressorSideChainId, string.Empty, value => node.CompressorSideChainId = value);
                if (instance.Get("Attack").Type == DataType.Number && double.IsFinite(instance.Get("Attack").Number)) Assign(node, "Attack", node.CompressorAttack, (float)Math.Clamp(instance.Get("Attack").Number, .1, 1), value => node.CompressorAttack = value);
                if (instance.Get("GainMakeup").Type == DataType.Number && double.IsFinite(instance.Get("GainMakeup").Number)) Assign(node, "GainMakeup", node.CompressorGainMakeup, (float)Math.Clamp(instance.Get("GainMakeup").Number, -30, 30), value => node.CompressorGainMakeup = value);
                if (instance.Get("Ratio").Type == DataType.Number && double.IsFinite(instance.Get("Ratio").Number)) Assign(node, "Ratio", node.CompressorRatio, (float)Math.Clamp(instance.Get("Ratio").Number, 1, 50), value => node.CompressorRatio = value);
                if (instance.Get("Release").Type == DataType.Number && double.IsFinite(instance.Get("Release").Number)) Assign(node, "Release", node.CompressorRelease, (float)Math.Clamp(instance.Get("Release").Number, 0, 5), value => node.CompressorRelease = value);
                if (instance.Get("Threshold").Type == DataType.Number && double.IsFinite(instance.Get("Threshold").Number)) Assign(node, "Threshold", node.CompressorThreshold, (float)Math.Clamp(instance.Get("Threshold").Number, -80, 0), value => node.CompressorThreshold = value);
                instance.Set("Attack", DynValue.NewNumber(node.CompressorAttack));
                instance.Set("GainMakeup", DynValue.NewNumber(node.CompressorGainMakeup));
                instance.Set("Ratio", DynValue.NewNumber(node.CompressorRatio));
                instance.Set("Release", DynValue.NewNumber(node.CompressorRelease));
                instance.Set("Threshold", DynValue.NewNumber(node.CompressorThreshold));
            }
            if (node.Type == "ChorusSoundEffect")
            {
                if (instance.Get("Depth").Type == DataType.Number && double.IsFinite(instance.Get("Depth").Number)) Assign(node, "Depth", node.ChorusDepth, (float)Math.Clamp(instance.Get("Depth").Number, 0, 1), value => node.ChorusDepth = value);
                if (instance.Get("Mix").Type == DataType.Number && double.IsFinite(instance.Get("Mix").Number)) Assign(node, "Mix", node.ChorusMix, (float)Math.Clamp(instance.Get("Mix").Number, 0, 1), value => node.ChorusMix = value);
                if (instance.Get("Rate").Type == DataType.Number && double.IsFinite(instance.Get("Rate").Number)) Assign(node, "Rate", node.ChorusRate, (float)Math.Clamp(instance.Get("Rate").Number, 0, 20), value => node.ChorusRate = value);
                instance.Set("Depth", DynValue.NewNumber(node.ChorusDepth));
                instance.Set("Mix", DynValue.NewNumber(node.ChorusMix));
                instance.Set("Rate", DynValue.NewNumber(node.ChorusRate));
            }
            if (node.Type == "EqualizerSoundEffect")
            {
                if (instance.Get("LowGain").Type == DataType.Number && double.IsFinite(instance.Get("LowGain").Number)) Assign(node, "LowGain", node.EqualizerLowGain, (float)Math.Clamp(instance.Get("LowGain").Number, -80, 10), value => node.EqualizerLowGain = value);
                if (instance.Get("MidGain").Type == DataType.Number && double.IsFinite(instance.Get("MidGain").Number)) Assign(node, "MidGain", node.EqualizerMidGain, (float)Math.Clamp(instance.Get("MidGain").Number, -80, 10), value => node.EqualizerMidGain = value);
                if (instance.Get("HighGain").Type == DataType.Number && double.IsFinite(instance.Get("HighGain").Number)) Assign(node, "HighGain", node.EqualizerHighGain, (float)Math.Clamp(instance.Get("HighGain").Number, -80, 10), value => node.EqualizerHighGain = value);
                instance.Set("LowGain", DynValue.NewNumber(node.EqualizerLowGain));
                instance.Set("MidGain", DynValue.NewNumber(node.EqualizerMidGain));
                instance.Set("HighGain", DynValue.NewNumber(node.EqualizerHighGain));
            }
            if (node.Type == "DistortionSoundEffect" && instance.Get("Level").Type == DataType.Number && double.IsFinite(instance.Get("Level").Number))
            {
                Assign(node, "Level", node.DistortionLevel, (float)Math.Clamp(instance.Get("Level").Number, 0, 1), value => node.DistortionLevel = value);
                instance.Set("Level", DynValue.NewNumber(node.DistortionLevel));
            }
            if (node.Type == "PitchShiftSoundEffect" && instance.Get("Octave").Type == DataType.Number && double.IsFinite(instance.Get("Octave").Number))
            {
                Assign(node, "Octave", node.PitchShiftOctave, (float)Math.Clamp(instance.Get("Octave").Number, .5, 2), value => node.PitchShiftOctave = value);
            }
            if (node.Type == "EchoSoundEffect")
            {
                if (instance.Get("Delay").Type == DataType.Number && double.IsFinite(instance.Get("Delay").Number)) Assign(node, "Delay", node.EchoDelay, (float)Math.Clamp(instance.Get("Delay").Number, .01, 5), value => node.EchoDelay = value);
                if (instance.Get("Feedback").Type == DataType.Number && double.IsFinite(instance.Get("Feedback").Number)) Assign(node, "Feedback", node.EchoFeedback, (float)Math.Clamp(instance.Get("Feedback").Number, 0, 1), value => node.EchoFeedback = value);
                if (instance.Get("DryLevel").Type == DataType.Number && double.IsFinite(instance.Get("DryLevel").Number)) Assign(node, "DryLevel", node.EchoDryLevel, (float)Math.Clamp(instance.Get("DryLevel").Number, -80, 10), value => node.EchoDryLevel = value);
                if (instance.Get("WetLevel").Type == DataType.Number && double.IsFinite(instance.Get("WetLevel").Number)) Assign(node, "WetLevel", node.EchoWetLevel, (float)Math.Clamp(instance.Get("WetLevel").Number, -80, 10), value => node.EchoWetLevel = value);
                instance.Set("Delay", DynValue.NewNumber(node.EchoDelay));
                instance.Set("Feedback", DynValue.NewNumber(node.EchoFeedback));
                instance.Set("DryLevel", DynValue.NewNumber(node.EchoDryLevel));
                instance.Set("WetLevel", DynValue.NewNumber(node.EchoWetLevel));
            }
            if (node.Type == "FlangeSoundEffect")
            {
                if (instance.Get("Depth").Type == DataType.Number && double.IsFinite(instance.Get("Depth").Number)) Assign(node, "Depth", node.FlangeDepth, (float)Math.Clamp(instance.Get("Depth").Number, .01, 1), value => node.FlangeDepth = value);
                if (instance.Get("Mix").Type == DataType.Number && double.IsFinite(instance.Get("Mix").Number)) Assign(node, "Mix", node.FlangeMix, (float)Math.Clamp(instance.Get("Mix").Number, 0, 1), value => node.FlangeMix = value);
                if (instance.Get("Rate").Type == DataType.Number && double.IsFinite(instance.Get("Rate").Number)) Assign(node, "Rate", node.FlangeRate, (float)Math.Clamp(instance.Get("Rate").Number, 0, 20), value => node.FlangeRate = value);
                instance.Set("Depth", DynValue.NewNumber(node.FlangeDepth));
                instance.Set("Mix", DynValue.NewNumber(node.FlangeMix));
                instance.Set("Rate", DynValue.NewNumber(node.FlangeRate));
            }
            if (node.Type == "TremoloSoundEffect")
            {
                if (instance.Get("Depth").Type == DataType.Number && double.IsFinite(instance.Get("Depth").Number)) Assign(node, "Depth", node.TremoloDepth, (float)Math.Clamp(instance.Get("Depth").Number, 0, 1), value => node.TremoloDepth = value);
                if (instance.Get("Duty").Type == DataType.Number && double.IsFinite(instance.Get("Duty").Number)) Assign(node, "Duty", node.TremoloDuty, (float)Math.Clamp(instance.Get("Duty").Number, 0, 1), value => node.TremoloDuty = value);
                if (instance.Get("Frequency").Type == DataType.Number && double.IsFinite(instance.Get("Frequency").Number)) Assign(node, "Frequency", node.TremoloFrequency, (float)Math.Clamp(instance.Get("Frequency").Number, .1, 20), value => node.TremoloFrequency = value);
                instance.Set("Depth", DynValue.NewNumber(node.TremoloDepth));
                instance.Set("Duty", DynValue.NewNumber(node.TremoloDuty));
                instance.Set("Frequency", DynValue.NewNumber(node.TremoloFrequency));
            }
            if (node.Type == "ReverbSoundEffect")
            {
                if (instance.Get("DecayTime").Type == DataType.Number && double.IsFinite(instance.Get("DecayTime").Number)) Assign(node, "DecayTime", node.ReverbDecayTime, (float)Math.Clamp(instance.Get("DecayTime").Number, .1, 20), value => node.ReverbDecayTime = value);
                if (instance.Get("Density").Type == DataType.Number && double.IsFinite(instance.Get("Density").Number)) Assign(node, "Density", node.ReverbDensity, (float)Math.Clamp(instance.Get("Density").Number, 0, 1), value => node.ReverbDensity = value);
                if (instance.Get("Diffusion").Type == DataType.Number && double.IsFinite(instance.Get("Diffusion").Number)) Assign(node, "Diffusion", node.ReverbDiffusion, (float)Math.Clamp(instance.Get("Diffusion").Number, 0, 1), value => node.ReverbDiffusion = value);
                if (instance.Get("DryLevel").Type == DataType.Number && double.IsFinite(instance.Get("DryLevel").Number)) Assign(node, "DryLevel", node.ReverbDryLevel, (float)Math.Clamp(instance.Get("DryLevel").Number, -80, 10), value => node.ReverbDryLevel = value);
                if (instance.Get("WetLevel").Type == DataType.Number && double.IsFinite(instance.Get("WetLevel").Number)) Assign(node, "WetLevel", node.ReverbWetLevel, (float)Math.Clamp(instance.Get("WetLevel").Number, -80, 10), value => node.ReverbWetLevel = value);
                instance.Set("DecayTime", DynValue.NewNumber(node.ReverbDecayTime));
                instance.Set("Density", DynValue.NewNumber(node.ReverbDensity));
                instance.Set("Diffusion", DynValue.NewNumber(node.ReverbDiffusion));
                instance.Set("DryLevel", DynValue.NewNumber(node.ReverbDryLevel));
                instance.Set("WetLevel", DynValue.NewNumber(node.ReverbWetLevel));
            }
            if (node.Type == "ReverbSoundEffect")
            {
                if (instance.Get("DecayTime").Type == DataType.Number && double.IsFinite(instance.Get("DecayTime").Number)) Assign(node, "DecayTime", node.ReverbDecayTime, (float)Math.Clamp(instance.Get("DecayTime").Number, .1, 20), value => node.ReverbDecayTime = value);
                if (instance.Get("Density").Type == DataType.Number && double.IsFinite(instance.Get("Density").Number)) Assign(node, "Density", node.ReverbDensity, (float)Math.Clamp(instance.Get("Density").Number, 0, 1), value => node.ReverbDensity = value);
                if (instance.Get("Diffusion").Type == DataType.Number && double.IsFinite(instance.Get("Diffusion").Number)) Assign(node, "Diffusion", node.ReverbDiffusion, (float)Math.Clamp(instance.Get("Diffusion").Number, 0, 1), value => node.ReverbDiffusion = value);
                if (instance.Get("DryLevel").Type == DataType.Number && double.IsFinite(instance.Get("DryLevel").Number)) Assign(node, "DryLevel", node.ReverbDryLevel, (float)Math.Clamp(instance.Get("DryLevel").Number, -80, 10), value => node.ReverbDryLevel = value);
                if (instance.Get("WetLevel").Type == DataType.Number && double.IsFinite(instance.Get("WetLevel").Number)) Assign(node, "WetLevel", node.ReverbWetLevel, (float)Math.Clamp(instance.Get("WetLevel").Number, -80, 10), value => node.ReverbWetLevel = value);
                instance.Set("DecayTime", DynValue.NewNumber(node.ReverbDecayTime));
                instance.Set("Density", DynValue.NewNumber(node.ReverbDensity));
                instance.Set("Diffusion", DynValue.NewNumber(node.ReverbDiffusion));
                instance.Set("DryLevel", DynValue.NewNumber(node.ReverbDryLevel));
                instance.Set("WetLevel", DynValue.NewNumber(node.ReverbWetLevel));
            }
            if (node.Type == "BodyForce")
            {
                var force = ReadVector(instance.Get("Force"), [float.NaN, float.NaN, float.NaN]);
                if (force.All(float.IsFinite))
                {
                    var requestedForce = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                    if (!node.Force.SequenceEqual(requestedForce)) { node.Force = requestedForce; MarkChanged(node, "Force"); }
                }
            }
            if (node.Type == "BodyThrust")
            {
                var force = ReadVector(instance.Get("Force"), [float.NaN, float.NaN, float.NaN]);
                if (force.Length == 3 && force.All(float.IsFinite))
                {
                    var requested = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                    if (!node.BodyThrustForce.SequenceEqual(requested)) { node.BodyThrustForce = requested; MarkChanged(node, "Force"); }
                }
                var location = ReadVector(instance.Get("Location"), [float.NaN, float.NaN, float.NaN]);
                if (location.Length == 3 && location.All(float.IsFinite))
                {
                    var requested = location.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                    if (!node.BodyThrustLocation.SequenceEqual(requested)) { node.BodyThrustLocation = requested; MarkChanged(node, "Location"); }
                }
            }
            if (node.Type == "RocketPropulsion")
            {
                var target = ReadNodeArgument(instance.Get("Target"));
                var targetId = target is { } targetPart && SceneNodeTypes.IsBasePart(targetPart) ? targetPart.Id : string.Empty;
                if (node.RocketPropulsionTarget != targetId)
                {
                    node.RocketPropulsionTarget = targetId;
                    node.RocketPropulsionReachedTarget = false;
                    MarkChanged(node, "Target");
                }
                var targetOffset = ReadVector(instance.Get("TargetOffset"), [float.NaN, float.NaN, float.NaN]);
                if (targetOffset.Length == 3 && targetOffset.All(float.IsFinite))
                {
                    var requested = targetOffset.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                    if (!node.RocketPropulsionTargetOffset.SequenceEqual(requested))
                    {
                        node.RocketPropulsionTargetOffset = requested;
                        node.RocketPropulsionReachedTarget = false;
                        MarkChanged(node, "TargetOffset");
                    }
                }
                var maxTorque = ReadVector(instance.Get("MaxTorque"), [float.NaN, float.NaN, float.NaN]);
                if (maxTorque.Length == 3 && maxTorque.All(float.IsFinite))
                {
                    var requested = maxTorque.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.RocketPropulsionMaxTorque.SequenceEqual(requested)) { node.RocketPropulsionMaxTorque = requested; MarkChanged(node, "MaxTorque"); }
                }
                if (instance.Get("TargetRadius").Type == DataType.Number && double.IsFinite(instance.Get("TargetRadius").Number)) Assign(node, "TargetRadius", node.RocketPropulsionTargetRadius, (float)Math.Clamp(instance.Get("TargetRadius").Number, 0, 1000000), value => node.RocketPropulsionTargetRadius = value);
                if (instance.Get("CartoonFactor").Type == DataType.Number && double.IsFinite(instance.Get("CartoonFactor").Number)) Assign(node, "CartoonFactor", node.RocketPropulsionCartoonFactor, (float)Math.Clamp(instance.Get("CartoonFactor").Number, 0, 1), value => node.RocketPropulsionCartoonFactor = value);
                if (instance.Get("MaxSpeed").Type == DataType.Number && double.IsFinite(instance.Get("MaxSpeed").Number)) Assign(node, "MaxSpeed", node.RocketPropulsionMaxSpeed, (float)Math.Clamp(instance.Get("MaxSpeed").Number, 0, 10000), value => node.RocketPropulsionMaxSpeed = value);
                if (instance.Get("MaxThrust").Type == DataType.Number && double.IsFinite(instance.Get("MaxThrust").Number)) Assign(node, "MaxThrust", node.RocketPropulsionMaxThrust, (float)Math.Clamp(instance.Get("MaxThrust").Number, 0, 1000000), value => node.RocketPropulsionMaxThrust = value);
                if (instance.Get("ThrustD").Type == DataType.Number && double.IsFinite(instance.Get("ThrustD").Number)) Assign(node, "ThrustD", node.RocketPropulsionThrustD, (float)Math.Clamp(instance.Get("ThrustD").Number, 0, 1000000), value => node.RocketPropulsionThrustD = value);
                if (instance.Get("ThrustP").Type == DataType.Number && double.IsFinite(instance.Get("ThrustP").Number)) Assign(node, "ThrustP", node.RocketPropulsionThrustP, (float)Math.Clamp(instance.Get("ThrustP").Number, 0, 1000000), value => node.RocketPropulsionThrustP = value);
                if (instance.Get("TurnD").Type == DataType.Number && double.IsFinite(instance.Get("TurnD").Number)) Assign(node, "TurnD", node.RocketPropulsionTurnD, (float)Math.Clamp(instance.Get("TurnD").Number, 0, 1000000), value => node.RocketPropulsionTurnD = value);
                if (instance.Get("TurnP").Type == DataType.Number && double.IsFinite(instance.Get("TurnP").Number)) Assign(node, "TurnP", node.RocketPropulsionTurnP, (float)Math.Clamp(instance.Get("TurnP").Number, 0, 1000000), value => node.RocketPropulsionTurnP = value);
            }
            if (node.Type == "BodyVelocity")
            {
                var targetVelocity = ReadVector(instance.Get("Velocity"), [float.NaN, float.NaN, float.NaN]);
                if (targetVelocity.All(float.IsFinite))
                {
                    var requested = targetVelocity.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                    if (!node.BodyVelocityTarget.SequenceEqual(requested)) { node.BodyVelocityTarget = requested; MarkChanged(node, "Velocity"); }
                }
                var maxForce = ReadVector(instance.Get("MaxForce"), [float.NaN, float.NaN, float.NaN]);
                if (maxForce.All(float.IsFinite))
                {
                    var requested = maxForce.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.BodyVelocityMaxForce.SequenceEqual(requested)) { node.BodyVelocityMaxForce = requested; MarkChanged(node, "MaxForce"); }
                }
                if (instance.Get("P").Type == DataType.Number && double.IsFinite(instance.Get("P").Number))
                    Assign(node, "P", node.BodyVelocityP, (float)Math.Clamp(instance.Get("P").Number, 0, 1000000), value => node.BodyVelocityP = value);
            }
            if (node.Type == "BodyPosition")
            {
                var targetPosition = ReadVector(instance.Get("Position"), [float.NaN, float.NaN, float.NaN]);
                if (targetPosition.All(float.IsFinite))
                {
                    var requested = targetPosition.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                    if (!node.BodyPositionTarget.SequenceEqual(requested)) { node.BodyPositionTarget = requested; MarkChanged(node, "Position"); }
                }
                var maxForce = ReadVector(instance.Get("MaxForce"), [float.NaN, float.NaN, float.NaN]);
                if (maxForce.All(float.IsFinite))
                {
                    var requested = maxForce.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.BodyPositionMaxForce.SequenceEqual(requested)) { node.BodyPositionMaxForce = requested; MarkChanged(node, "MaxForce"); }
                }
                if (instance.Get("P").Type == DataType.Number && double.IsFinite(instance.Get("P").Number))
                    Assign(node, "P", node.BodyPositionP, (float)Math.Clamp(instance.Get("P").Number, 0, 1000000), value => node.BodyPositionP = value);
                if (instance.Get("D").Type == DataType.Number && double.IsFinite(instance.Get("D").Number))
                    Assign(node, "D", node.BodyPositionD, (float)Math.Clamp(instance.Get("D").Number, 0, 1000000), value => node.BodyPositionD = value);
            }
            if (node.Type == "BodyGyro")
            {
                var rotation = ReadVector(instance.Get("Rotation"), [float.NaN, float.NaN, float.NaN]);
                if (rotation.All(float.IsFinite))
                {
                    var requested = rotation.Select(component => Math.Clamp(component, -360000, 360000)).ToArray();
                    if (!node.BodyGyroTargetRotation.SequenceEqual(requested)) { node.BodyGyroTargetRotation = requested; MarkChanged(node, "Rotation"); }
                }
                var maxTorque = ReadVector(instance.Get("MaxTorque"), [float.NaN, float.NaN, float.NaN]);
                if (maxTorque.All(float.IsFinite))
                {
                    var requested = maxTorque.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.BodyGyroMaxTorque.SequenceEqual(requested)) { node.BodyGyroMaxTorque = requested; MarkChanged(node, "MaxTorque"); }
                }
                if (instance.Get("P").Type == DataType.Number && double.IsFinite(instance.Get("P").Number))
                    Assign(node, "P", node.BodyGyroP, (float)Math.Clamp(instance.Get("P").Number, 0, 1000000), value => node.BodyGyroP = value);
                if (instance.Get("D").Type == DataType.Number && double.IsFinite(instance.Get("D").Number))
                    Assign(node, "D", node.BodyGyroD, (float)Math.Clamp(instance.Get("D").Number, 0, 1000000), value => node.BodyGyroD = value);
            }
            if (node.Type == "BodyAngularVelocity")
            {
                var angularVelocity = ReadVector(instance.Get("AngularVelocity"), [float.NaN, float.NaN, float.NaN]);
                if (angularVelocity.All(float.IsFinite))
                {
                    var requested = angularVelocity.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                    if (!node.BodyAngularVelocityTarget.SequenceEqual(requested)) { node.BodyAngularVelocityTarget = requested; MarkChanged(node, "AngularVelocity"); }
                }
                var maxTorque = ReadVector(instance.Get("MaxTorque"), [float.NaN, float.NaN, float.NaN]);
                if (maxTorque.All(float.IsFinite))
                {
                    var requested = maxTorque.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.BodyAngularVelocityMaxTorque.SequenceEqual(requested)) { node.BodyAngularVelocityMaxTorque = requested; MarkChanged(node, "MaxTorque"); }
                }
                if (instance.Get("P").Type == DataType.Number && double.IsFinite(instance.Get("P").Number))
                    Assign(node, "P", node.BodyAngularVelocityP, (float)Math.Clamp(instance.Get("P").Number, 0, 1000000), value => node.BodyAngularVelocityP = value);
            }
            if (node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")
            {
                var part0Value = instance.Get("Part0");
                var requestedPart0 = ReadNodeArgument(part0Value);
                if (IsBasePart(requestedPart0) && requestedPart0!.Id != node.Part1)
                    Assign(node, "Part0", node.Part0, requestedPart0.Id, value => node.Part0 = value);
                else if (part0Value.Type == DataType.Nil) Assign(node, "Part0", node.Part0, string.Empty, value => node.Part0 = value);
                var part1Value = instance.Get("Part1");
                var requestedPart1 = ReadNodeArgument(part1Value);
                if (IsBasePart(requestedPart1) && requestedPart1!.Id != node.Part0)
                    Assign(node, "Part1", node.Part1, requestedPart1.Id, value => node.Part1 = value);
                else if (part1Value.Type == DataType.Nil) Assign(node, "Part1", node.Part1, string.Empty, value => node.Part1 = value);
            }
            if (node.Type is "Weld" or "Motor6D")
            {
                var part0OffsetPosition = LimitVector(ReadVector(instance.Get("Part0OffsetPosition"), node.Part0OffsetPosition), -1000000, 1000000);
                if (instance.Get("Part0OffsetPosition").Type == DataType.Table && !node.Part0OffsetPosition.SequenceEqual(part0OffsetPosition)) { node.Part0OffsetPosition = part0OffsetPosition; MarkChanged(node, "Part0OffsetPosition"); }
                var part0OffsetRotation = LimitVector(ReadVector(instance.Get("Part0OffsetRotation"), node.Part0OffsetRotation), -360000, 360000);
                if (instance.Get("Part0OffsetRotation").Type == DataType.Table && !node.Part0OffsetRotation.SequenceEqual(part0OffsetRotation)) { node.Part0OffsetRotation = part0OffsetRotation; MarkChanged(node, "Part0OffsetRotation"); }
                var part1OffsetPosition = LimitVector(ReadVector(instance.Get("Part1OffsetPosition"), node.Part1OffsetPosition), -1000000, 1000000);
                if (instance.Get("Part1OffsetPosition").Type == DataType.Table && !node.Part1OffsetPosition.SequenceEqual(part1OffsetPosition)) { node.Part1OffsetPosition = part1OffsetPosition; MarkChanged(node, "Part1OffsetPosition"); }
                var part1OffsetRotation = LimitVector(ReadVector(instance.Get("Part1OffsetRotation"), node.Part1OffsetRotation), -360000, 360000);
                if (instance.Get("Part1OffsetRotation").Type == DataType.Table && !node.Part1OffsetRotation.SequenceEqual(part1OffsetRotation)) { node.Part1OffsetRotation = part1OffsetRotation; MarkChanged(node, "Part1OffsetRotation"); }
                if (node.Type == "Motor6D" && instance.Get("TransformPosition").Type == DataType.Table)
                {
                    var transformPosition = LimitVector(ReadVector(instance.Get("TransformPosition"), node.MotorTransformPosition), -1000000, 1000000);
                    if (!node.MotorTransformPosition.SequenceEqual(transformPosition)) { node.MotorTransformPosition = transformPosition; MarkChanged(node, "TransformPosition"); }
                }
                if (node.Type == "Motor6D" && instance.Get("TransformRotation").Type == DataType.Table)
                {
                    var transformRotation = LimitVector(ReadVector(instance.Get("TransformRotation"), node.MotorTransformRotation), -360000, 360000);
                    if (!node.MotorTransformRotation.SequenceEqual(transformRotation)) { node.MotorTransformRotation = transformRotation; MarkChanged(node, "TransformRotation"); }
                }
            }
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint")
            {
                var attachment0Value = instance.Get("Attachment0");
                var requestedAttachment0 = ReadNodeArgument(attachment0Value);
                if (requestedAttachment0 is { Type: "Attachment" } && (node.Type is "VectorForce" or "LinearVelocity" || requestedAttachment0.Id != node.Attachment1))
                    Assign(node, "Attachment0", node.Attachment0, requestedAttachment0.Id, value => node.Attachment0 = value);
                else if (attachment0Value.Type == DataType.Nil) Assign(node, "Attachment0", node.Attachment0, string.Empty, value => node.Attachment0 = value);
                if (node.Type == "VectorForce")
                {
                    var referenceValue = instance.Get("Attachment1");
                    var referenceAttachment = ReadNodeArgument(referenceValue);
                    if (referenceAttachment is { Type: "Attachment" })
                        Assign(node, "Attachment1", node.Attachment1, referenceAttachment.Id, value => node.Attachment1 = value);
                    else if (referenceValue.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    var force = ReadVector(instance.Get("Force"), [float.NaN, float.NaN, float.NaN]);
                    if (force.All(float.IsFinite))
                    {
                        var requestedForce = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                        if (!node.Force.SequenceEqual(requestedForce)) { node.Force = requestedForce; MarkChanged(node, "Force"); }
                    }
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0" or "Attachment1")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
                    if (instance.Get("ApplyAtCenterOfMass").Type == DataType.Boolean)
                        Assign(node, "ApplyAtCenterOfMass", node.ApplyAtCenterOfMass, instance.Get("ApplyAtCenterOfMass").Boolean, value => node.ApplyAtCenterOfMass = value);
                }
                else if (node.Type == "LineForce")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignLineForceNumber("Magnitude", node.LineForceMagnitude, 0, 1000000, value => node.LineForceMagnitude = value);
                    AssignLineForceNumber("MaxForce", node.LineForceMaxForce, 0, 1000000, value => node.LineForceMaxForce = value);
                    if (instance.Get("InverseSquareLaw").Type == DataType.Boolean)
                        Assign(node, "InverseSquareLaw", node.LineForceInverseSquareLaw, instance.Get("InverseSquareLaw").Boolean, value => node.LineForceInverseSquareLaw = value);
                    if (instance.Get("ReactionForceEnabled").Type == DataType.Boolean)
                        Assign(node, "ReactionForceEnabled", node.LineForceReactionForceEnabled, instance.Get("ReactionForceEnabled").Boolean, value => node.LineForceReactionForceEnabled = value);
                    if (instance.Get("ApplyAtCenterOfMass").Type == DataType.Boolean)
                        Assign(node, "ApplyAtCenterOfMass", node.ApplyAtCenterOfMass, instance.Get("ApplyAtCenterOfMass").Boolean, value => node.ApplyAtCenterOfMass = value);

                    void AssignLineForceNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        var requested = instance.Get(propertyName);
                        if (requested.Type == DataType.Number && double.IsFinite(requested.Number))
                            Assign(node, propertyName, current, (float)Math.Clamp(requested.Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "Torque")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    var torque = ReadVector(instance.Get("Torque"), [float.NaN, float.NaN, float.NaN]);
                    if (torque.All(float.IsFinite))
                    {
                        var requestedTorque = torque.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                        if (!node.TorqueTarget.SequenceEqual(requestedTorque)) { node.TorqueTarget = requestedTorque; MarkChanged(node, "Torque"); }
                    }
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0" or "Attachment1")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
                }
                else if (node.Type == "SpringConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignSpringNumber("Damping", node.SpringDamping, 0, 10000, value => node.SpringDamping = value);
                    AssignSpringNumber("FreeLength", node.SpringFreeLength, 0, 10000, value => node.SpringFreeLength = value);
                    AssignSpringNumber("MaxForce", node.SpringMaxForce, 0, 1000000, value => node.SpringMaxForce = value);
                    AssignSpringNumber("MaxLength", node.SpringMaxLength, node.SpringMinLength, 10000, value => node.SpringMaxLength = value);
                    AssignSpringNumber("MinLength", node.SpringMinLength, 0, node.SpringMaxLength, value => node.SpringMinLength = value);
                    AssignSpringNumber("Stiffness", node.SpringStiffness, 0, 100000, value => node.SpringStiffness = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.SpringLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.SpringLimitsEnabled = value);
                    instance.Set("CurrentLength", DynValue.NewNumber(node.SpringCurrentLength));

                    void AssignSpringNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "TorsionSpringConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignTorsionNumber("Coils", node.TorsionSpringCoils, 0, 8, value => node.TorsionSpringCoils = value);
                    AssignTorsionNumber("Damping", node.TorsionSpringDamping, 0, 100000, value => node.TorsionSpringDamping = value);
                    AssignTorsionNumber("MaxAngle", node.TorsionSpringMaxAngle, 0, 180, value => node.TorsionSpringMaxAngle = value);
                    AssignTorsionNumber("MaxTorque", node.TorsionSpringMaxTorque, 0, 1000000, value => node.TorsionSpringMaxTorque = value);
                    AssignTorsionNumber("Radius", node.TorsionSpringRadius, 0, 100, value => node.TorsionSpringRadius = value);
                    AssignTorsionNumber("Restitution", node.TorsionSpringRestitution, 0, 1, value => node.TorsionSpringRestitution = value);
                    AssignTorsionNumber("Stiffness", node.TorsionSpringStiffness, 0, 100000, value => node.TorsionSpringStiffness = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.TorsionSpringLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.TorsionSpringLimitsEnabled = value);
                    instance.Set("CurrentAngle", DynValue.NewNumber(node.TorsionSpringCurrentAngle));

                    void AssignTorsionNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "RopeConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignRopeNumber("Length", node.RopeLength, 0, 10000, value => node.RopeLength = value);
                    AssignRopeNumber("Restitution", node.RopeRestitution, 0, 1, value => node.RopeRestitution = value);
                    AssignRopeNumber("WinchForce", node.RopeWinchForce, 0, 1000000, value => node.RopeWinchForce = value);
                    AssignRopeNumber("WinchResponsiveness", node.RopeWinchResponsiveness, 0, 200, value => node.RopeWinchResponsiveness = value);
                    AssignRopeNumber("WinchSpeed", node.RopeWinchSpeed, 0, 10000, value => node.RopeWinchSpeed = value);
                    AssignRopeNumber("WinchTarget", node.RopeWinchTarget, 0, 10000, value => node.RopeWinchTarget = value);
                    if (instance.Get("WinchEnabled").Type == DataType.Boolean)
                        Assign(node, "WinchEnabled", node.RopeWinchEnabled, instance.Get("WinchEnabled").Boolean, value => node.RopeWinchEnabled = value);
                    instance.Set("CurrentLength", DynValue.NewNumber(node.RopeCurrentLength));

                    void AssignRopeNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "RodConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignRodNumber("Length", node.RodLength, 0, 10000, value => node.RodLength = value);
                    AssignRodNumber("LimitAngle0", node.RodLimitAngle0, 0, 180, value => node.RodLimitAngle0 = value);
                    AssignRodNumber("LimitAngle1", node.RodLimitAngle1, 0, 180, value => node.RodLimitAngle1 = value);
                    AssignRodNumber("Thickness", node.RodThickness, 0, 100, value => node.RodThickness = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.RodLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.RodLimitsEnabled = value);
                    instance.Set("CurrentDistance", DynValue.NewNumber(node.RodCurrentDistance));

                    void AssignRodNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "UniversalConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignUniversalNumber("MaxAngle", node.UniversalMaxAngle, 0, 180, value => node.UniversalMaxAngle = value);
                    AssignUniversalNumber("Radius", node.UniversalRadius, 0, 100, value => node.UniversalRadius = value);
                    AssignUniversalNumber("Restitution", node.UniversalRestitution, 0, 1, value => node.UniversalRestitution = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.UniversalLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.UniversalLimitsEnabled = value);

                    void AssignUniversalNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "BallSocketConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    AssignBallSocketNumber("UpperAngle", node.BallSocketUpperAngle, 0, 180, value => node.BallSocketUpperAngle = value);
                    AssignBallSocketNumber("MaxFrictionTorque", node.BallSocketMaxFrictionTorque, 0, 1000000, value => node.BallSocketMaxFrictionTorque = value);
                    AssignBallSocketNumber("Radius", node.BallSocketRadius, 0, 100, value => node.BallSocketRadius = value);
                    AssignBallSocketNumber("Restitution", node.BallSocketRestitution, 0, 1, value => node.BallSocketRestitution = value);
                    AssignBallSocketNumber("TwistLowerAngle", node.BallSocketTwistLowerAngle, -180, node.BallSocketTwistUpperAngle, value => node.BallSocketTwistLowerAngle = value);
                    AssignBallSocketNumber("TwistUpperAngle", node.BallSocketTwistUpperAngle, node.BallSocketTwistLowerAngle, 180, value => node.BallSocketTwistUpperAngle = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.BallSocketLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.BallSocketLimitsEnabled = value);
                    if (instance.Get("TwistLimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "TwistLimitsEnabled", node.BallSocketTwistLimitsEnabled, instance.Get("TwistLimitsEnabled").Boolean, value => node.BallSocketTwistLimitsEnabled = value);

                    void AssignBallSocketNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "HingeConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    if (instance.Get("ActuatorType").Type == DataType.String && instance.Get("ActuatorType").String is "None" or "Motor" or "Servo")
                        Assign(node, "ActuatorType", node.HingeActuatorType, instance.Get("ActuatorType").String, value => node.HingeActuatorType = value);
                    AssignHingeNumber("AngularResponsiveness", node.HingeAngularResponsiveness, 0, 200, value => node.HingeAngularResponsiveness = value);
                    AssignHingeNumber("AngularSpeed", node.HingeAngularSpeed, 0, 1000, value => node.HingeAngularSpeed = value);
                    AssignHingeNumber("AngularVelocity", node.HingeAngularVelocity, -1000, 1000, value => node.HingeAngularVelocity = value);
                    AssignHingeNumber("LowerAngle", node.HingeLowerAngle, -180, node.HingeUpperAngle, value => node.HingeLowerAngle = value);
                    AssignHingeNumber("MotorMaxAcceleration", node.HingeMotorMaxAcceleration, 0, 100000, value => node.HingeMotorMaxAcceleration = value);
                    AssignHingeNumber("MotorMaxTorque", node.HingeMotorMaxTorque, 0, 1000000, value => node.HingeMotorMaxTorque = value);
                    AssignHingeNumber("Radius", node.HingeRadius, 0, 100, value => node.HingeRadius = value);
                    AssignHingeNumber("Restitution", node.HingeRestitution, 0, 1, value => node.HingeRestitution = value);
                    AssignHingeNumber("ServoMaxTorque", node.HingeServoMaxTorque, 0, 1000000, value => node.HingeServoMaxTorque = value);
                    AssignHingeNumber("TargetAngle", node.HingeTargetAngle, -360000, 360000, value => node.HingeTargetAngle = value);
                    AssignHingeNumber("UpperAngle", node.HingeUpperAngle, node.HingeLowerAngle, 180, value => node.HingeUpperAngle = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.HingeLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.HingeLimitsEnabled = value);
                    instance.Set("CurrentAngle", DynValue.NewNumber(node.HingeCurrentAngle));

                    void AssignHingeNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type is "PrismaticConstraint" or "CylindricalConstraint")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    if (instance.Get("ActuatorType").Type == DataType.String && instance.Get("ActuatorType").String is "None" or "Motor" or "Servo")
                        Assign(node, "ActuatorType", node.PrismaticActuatorType, instance.Get("ActuatorType").String, value => node.PrismaticActuatorType = value);
                    AssignPrismaticNumber("LinearResponsiveness", node.PrismaticLinearResponsiveness, 0, 200, value => node.PrismaticLinearResponsiveness = value);
                    AssignPrismaticNumber("LowerLimit", node.PrismaticLowerLimit, -10000, 10000, value =>
                    {
                        node.PrismaticLowerLimit = value;
                        if (node.PrismaticLowerLimit > node.PrismaticUpperLimit)
                            node.PrismaticUpperLimit = node.PrismaticLowerLimit;
                    });
                    AssignPrismaticNumber("MotorMaxAcceleration", node.PrismaticMotorMaxAcceleration, 0, 100000, value => node.PrismaticMotorMaxAcceleration = value);
                    AssignPrismaticNumber("MotorMaxForce", node.PrismaticMotorMaxForce, 0, 1000000, value => node.PrismaticMotorMaxForce = value);
                    AssignPrismaticNumber("Restitution", node.PrismaticRestitution, 0, 1, value => node.PrismaticRestitution = value);
                    AssignPrismaticNumber("ServoMaxForce", node.PrismaticServoMaxForce, 0, 1000000, value => node.PrismaticServoMaxForce = value);
                    AssignPrismaticNumber("Size", node.PrismaticSize, 0, 100, value => node.PrismaticSize = value);
                    AssignPrismaticNumber("Speed", node.PrismaticSpeed, 0, 10000, value => node.PrismaticSpeed = value);
                    AssignPrismaticNumber("TargetPosition", node.PrismaticTargetPosition, -10000, 10000, value => node.PrismaticTargetPosition = value);
                    AssignPrismaticNumber("UpperLimit", node.PrismaticUpperLimit, -10000, 10000, value =>
                    {
                        node.PrismaticUpperLimit = value;
                        if (node.PrismaticUpperLimit < node.PrismaticLowerLimit)
                            node.PrismaticLowerLimit = node.PrismaticUpperLimit;
                    });
                    AssignPrismaticNumber("Velocity", node.PrismaticVelocity, -10000, 10000, value => node.PrismaticVelocity = value);
                    if (instance.Get("LimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "LimitsEnabled", node.PrismaticLimitsEnabled, instance.Get("LimitsEnabled").Boolean, value => node.PrismaticLimitsEnabled = value);
                    instance.Set("CurrentPosition", DynValue.NewNumber(node.PrismaticCurrentPosition));
                    if (node.Type == "CylindricalConstraint")
                    {
                        if (instance.Get("AngularActuatorType").Type == DataType.String && instance.Get("AngularActuatorType").String is "None" or "Motor" or "Servo")
                            Assign(node, "AngularActuatorType", node.HingeActuatorType, instance.Get("AngularActuatorType").String, value => node.HingeActuatorType = value);
                        AssignPrismaticNumber("AngularRestitution", node.HingeRestitution, 0, 1, value => node.HingeRestitution = value);
                        AssignPrismaticNumber("AngularResponsiveness", node.HingeAngularResponsiveness, 0, 200, value => node.HingeAngularResponsiveness = value);
                        AssignPrismaticNumber("AngularSpeed", node.HingeAngularSpeed, 0, 1000, value => node.HingeAngularSpeed = value);
                        AssignPrismaticNumber("AngularVelocity", node.HingeAngularVelocity, -1000, 1000, value => node.HingeAngularVelocity = value);
                        AssignPrismaticNumber("InclinationAngle", node.CylindricalInclinationAngle, -180, 180, value => node.CylindricalInclinationAngle = value);
                        AssignPrismaticNumber("LowerAngle", node.HingeLowerAngle, -180, node.HingeUpperAngle, value => node.HingeLowerAngle = value);
                        AssignPrismaticNumber("MotorMaxAngularAcceleration", node.HingeMotorMaxAcceleration, 0, 100000, value => node.HingeMotorMaxAcceleration = value);
                        AssignPrismaticNumber("MotorMaxTorque", node.HingeMotorMaxTorque, 0, 1000000, value => node.HingeMotorMaxTorque = value);
                        AssignPrismaticNumber("ServoMaxTorque", node.HingeServoMaxTorque, 0, 1000000, value => node.HingeServoMaxTorque = value);
                        AssignPrismaticNumber("TargetAngle", node.HingeTargetAngle, -180, 180, value => node.HingeTargetAngle = value);
                        AssignPrismaticNumber("UpperAngle", node.HingeUpperAngle, node.HingeLowerAngle, 180, value => node.HingeUpperAngle = value);
                        if (instance.Get("AngularLimitsEnabled").Type == DataType.Boolean)
                            Assign(node, "AngularLimitsEnabled", node.HingeLimitsEnabled, instance.Get("AngularLimitsEnabled").Boolean, value => node.HingeLimitsEnabled = value);
                        if (instance.Get("RotationAxisVisible").Type == DataType.Boolean)
                            Assign(node, "RotationAxisVisible", node.CylindricalRotationAxisVisible, instance.Get("RotationAxisVisible").Boolean, value => node.CylindricalRotationAxisVisible = value);
                        instance.Set("CurrentAngle", DynValue.NewNumber(node.HingeCurrentAngle));
                    }

                    void AssignPrismaticNumber(string propertyName, float current, float minimum, float maximum, Action<float> setter)
                    {
                        if (instance.Get(propertyName).Type != DataType.Number || !double.IsFinite(instance.Get(propertyName).Number)) return;
                        Assign(node, propertyName, current, (float)Math.Clamp(instance.Get(propertyName).Number, minimum, maximum), setter);
                    }
                }
                else if (node.Type == "LinearVelocity")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    void SyncVector(string property, float[] current, Action<float[]> assign)
                    {
                        var requested = ReadVector(instance.Get(property), [float.NaN, float.NaN, float.NaN]);
                        if (!requested.All(float.IsFinite)) return;
                        requested = requested.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                        if (current.SequenceEqual(requested)) return;
                        assign(requested);
                        MarkChanged(node, property);
                    }
                    SyncVector("VectorVelocity", node.VectorVelocity, value => node.VectorVelocity = value);
                    SyncVector("LineDirection", node.LineDirection, value => node.LineDirection = value);
                    SyncVector("PrimaryTangentAxis", node.PrimaryTangentAxis, value => node.PrimaryTangentAxis = value);
                    SyncVector("SecondaryTangentAxis", node.SecondaryTangentAxis, value => node.SecondaryTangentAxis = value);
                    var maxAxesForce = ReadVector(instance.Get("MaxAxesForce"), [float.NaN, float.NaN, float.NaN]);
                    if (maxAxesForce.All(float.IsFinite))
                    {
                        var requested = maxAxesForce.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                        if (!node.MaxAxesForce.SequenceEqual(requested)) { node.MaxAxesForce = requested; MarkChanged(node, "MaxAxesForce"); }
                    }
                    if (TryReadVector2(instance.Get("PlaneVelocity"), out var plane))
                    {
                        var requested = plane.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                        if (!node.PlaneVelocity.SequenceEqual(requested)) { node.PlaneVelocity = requested; MarkChanged(node, "PlaneVelocity"); }
                    }
                    if (TryReadVector2(instance.Get("MaxPlanarAxesForce"), out var maxPlanarForce))
                    {
                        var requested = maxPlanarForce.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                        if (!node.MaxPlanarAxesForce.SequenceEqual(requested)) { node.MaxPlanarAxesForce = requested; MarkChanged(node, "MaxPlanarAxesForce"); }
                    }
                    if (instance.Get("LineVelocity").Type == DataType.Number && double.IsFinite(instance.Get("LineVelocity").Number))
                        Assign(node, "LineVelocity", node.LineVelocity, (float)Math.Clamp(instance.Get("LineVelocity").Number, -10000, 10000), value => node.LineVelocity = value);
                    if (instance.Get("MaxForce").Type == DataType.Number && double.IsFinite(instance.Get("MaxForce").Number))
                        Assign(node, "MaxForce", node.MaxForce, (float)Math.Clamp(instance.Get("MaxForce").Number, 0, 1000000), value => node.MaxForce = value);
                    if (instance.Get("ForceLimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "ForceLimitsEnabled", node.ForceLimitsEnabled, instance.Get("ForceLimitsEnabled").Boolean, value => node.ForceLimitsEnabled = value);
                    if (instance.Get("ForceLimitMode").Type == DataType.String && instance.Get("ForceLimitMode").String is "Magnitude" or "PerAxis")
                        Assign(node, "ForceLimitMode", node.ForceLimitMode, instance.Get("ForceLimitMode").String, value => node.ForceLimitMode = value);
                    if (instance.Get("ReactionForceEnabled").Type == DataType.Boolean)
                        Assign(node, "ReactionForceEnabled", node.LinearVelocityReactionForceEnabled, instance.Get("ReactionForceEnabled").Boolean, value => node.LinearVelocityReactionForceEnabled = value);
                    if (instance.Get("VelocityConstraintMode").Type == DataType.String && instance.Get("VelocityConstraintMode").String is "Vector" or "Line" or "Plane")
                        Assign(node, "VelocityConstraintMode", node.VelocityConstraintMode, instance.Get("VelocityConstraintMode").String, value => node.VelocityConstraintMode = value);
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0" or "Attachment1")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
                }
                else if (node.Type == "AlignPosition")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    if (instance.Get("Mode").CastToString() is { } mode && mode is ("OneAttachment" or "TwoAttachment"))
                        Assign(node, "Mode", node.AlignmentMode, mode, value => node.AlignmentMode = value);
                    if (instance.Get("Responsiveness") is { Type: DataType.Number } responsiveness && double.IsFinite(responsiveness.Number))
                        Assign(node, "Responsiveness", node.Responsiveness, (float)Math.Clamp(responsiveness.Number, 0, 200), value => node.Responsiveness = value);
                    if (instance.Get("MaxVelocity") is { Type: DataType.Number } maxVelocity && double.IsFinite(maxVelocity.Number))
                        Assign(node, "MaxVelocity", node.MaxVelocity, (float)Math.Clamp(maxVelocity.Number, 0, 10000), value => node.MaxVelocity = value);
                    if (instance.Get("MaxForce") is { Type: DataType.Number } maxForce && double.IsFinite(maxForce.Number))
                        Assign(node, "MaxForce", node.MaxForce, (float)Math.Clamp(maxForce.Number, 0, 1000000), value => node.MaxForce = value);
                    if (instance.Get("RigidityEnabled").Type == DataType.Boolean)
                        Assign(node, "RigidityEnabled", node.RigidityEnabled, instance.Get("RigidityEnabled").Boolean, value => node.RigidityEnabled = value);
                    if (instance.Get("ReactionForceEnabled").Type == DataType.Boolean)
                        Assign(node, "ReactionForceEnabled", node.ReactionForceEnabled, instance.Get("ReactionForceEnabled").Boolean, value => node.ReactionForceEnabled = value);
                    if (instance.Get("ApplyAtCenterOfMass").Type == DataType.Boolean)
                        Assign(node, "ApplyAtCenterOfMass", node.ApplyAtCenterOfMass, instance.Get("ApplyAtCenterOfMass").Boolean, value => node.ApplyAtCenterOfMass = value);
                }
                else if (node.Type == "AlignOrientation")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    if (instance.Get("Mode").CastToString() is { } mode && mode is ("OneAttachment" or "TwoAttachment"))
                        Assign(node, "Mode", node.AlignmentMode, mode, value => node.AlignmentMode = value);
                    var targetRotation = ReadVector(instance.Get("Rotation"), node.AlignOrientationRotation).Select(component => Math.Clamp(component, -360000, 360000)).ToArray();
                    if (!node.AlignOrientationRotation.SequenceEqual(targetRotation)) { node.AlignOrientationRotation = targetRotation; MarkChanged(node, "Rotation"); }
                    if (instance.Get("Responsiveness") is { Type: DataType.Number } responsiveness && double.IsFinite(responsiveness.Number))
                        Assign(node, "Responsiveness", node.Responsiveness, (float)Math.Clamp(responsiveness.Number, 0, 200), value => node.Responsiveness = value);
                    if (instance.Get("MaxAngularVelocity") is { Type: DataType.Number } maxAngularVelocity && double.IsFinite(maxAngularVelocity.Number))
                        Assign(node, "MaxAngularVelocity", node.MaxAngularVelocity, (float)Math.Clamp(maxAngularVelocity.Number, 0, 10000), value => node.MaxAngularVelocity = value);
                    if (instance.Get("MaxTorque") is { Type: DataType.Number } maxTorque && double.IsFinite(maxTorque.Number))
                        Assign(node, "MaxTorque", node.MaxTorque, (float)Math.Clamp(maxTorque.Number, 0, 1000000), value => node.MaxTorque = value);
                    if (instance.Get("RigidityEnabled").Type == DataType.Boolean)
                        Assign(node, "RigidityEnabled", node.RigidityEnabled, instance.Get("RigidityEnabled").Boolean, value => node.RigidityEnabled = value);
                    if (instance.Get("ReactionTorqueEnabled").Type == DataType.Boolean)
                        Assign(node, "ReactionTorqueEnabled", node.ReactionTorqueEnabled, instance.Get("ReactionTorqueEnabled").Boolean, value => node.ReactionTorqueEnabled = value);
                }
                else if (node.Type == "AngularVelocity")
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil)
                        Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    var targetAngularVelocity = ReadVector(instance.Get("AngularVelocity"), node.AngularVelocityTarget).Select(component => Math.Clamp(component, -1000, 1000)).ToArray();
                    if (!node.AngularVelocityTarget.SequenceEqual(targetAngularVelocity)) { node.AngularVelocityTarget = targetAngularVelocity; MarkChanged(node, "AngularVelocity"); }
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0" or "Attachment1")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
                    if (instance.Get("MaxTorque").Type == DataType.Number && double.IsFinite(instance.Get("MaxTorque").Number))
                        Assign(node, "MaxTorque", node.MaxTorque, (float)Math.Clamp(instance.Get("MaxTorque").Number, 0, 1000000), value => node.MaxTorque = value);
                    if (instance.Get("ReactionTorqueEnabled").Type == DataType.Boolean)
                        Assign(node, "ReactionTorqueEnabled", node.ReactionTorqueEnabled, instance.Get("ReactionTorqueEnabled").Boolean, value => node.ReactionTorqueEnabled = value);
                }
                else
                {
                    var attachment1Value = instance.Get("Attachment1");
                    var requestedAttachment1 = ReadNodeArgument(attachment1Value);
                    if (requestedAttachment1 is { Type: "Attachment" } && requestedAttachment1.Id != node.Attachment0)
                        Assign(node, "Attachment1", node.Attachment1, requestedAttachment1.Id, value => node.Attachment1 = value);
                    else if (attachment1Value.Type == DataType.Nil) Assign(node, "Attachment1", node.Attachment1, string.Empty, value => node.Attachment1 = value);
                    if (instance.Get("Width0").Type == DataType.Number && double.IsFinite(instance.Get("Width0").Number))
                        Assign(node, "Width0", node.Width0, (float)Math.Clamp(instance.Get("Width0").Number, 0, 100), value => node.Width0 = value);
                    if (instance.Get("Width1").Type == DataType.Number && double.IsFinite(instance.Get("Width1").Number))
                        Assign(node, "Width1", node.Width1, (float)Math.Clamp(instance.Get("Width1").Number, 0, 100), value => node.Width1 = value);
                }
            }
            if (node.Type == "Trail")
            {
                if (instance.Get("FaceCamera").Type == DataType.Boolean)
                    Assign(node, "FaceCamera", node.FaceCamera, instance.Get("FaceCamera").Boolean, value => node.FaceCamera = value);
                if (instance.Get("TextureMode").Type == DataType.String && instance.Get("TextureMode").String is "Stretch" or "Wrap" or "Static")
                    Assign(node, "TextureMode", node.TextureMode, instance.Get("TextureMode").String, value => node.TextureMode = value);
                if (instance.Get("TextureLength").Type == DataType.Number && float.IsFinite((float)instance.Get("TextureLength").Number))
                    Assign(node, "TextureLength", node.TextureLength, (float)Math.Clamp(instance.Get("TextureLength").Number, .01, 1000), value => node.TextureLength = value);
                if (TryReadNumberSequence(instance.Get("WidthScale"), out var requestedWidth))
                {
                    var normalizedWidth = ParticleSequenceService.NormalizeNumber(requestedWidth, 0, 1);
                    if (!node.TrailWidthSequence.SequenceEqual(normalizedWidth))
                    {
                        node.TrailWidthSequence = normalizedWidth;
                        node.TrailWidth = normalizedWidth[0].Value;
                        MarkChanged(node, "WidthScale");
                    }
                }
                if (instance.Get("Lifetime").Type == DataType.Number && float.IsFinite((float)instance.Get("Lifetime").Number))
                    Assign(node, "Lifetime", node.TrailLifetime, (float)Math.Clamp(instance.Get("Lifetime").Number, .01, 20), value => node.TrailLifetime = value);
                if (instance.Get("MinLength").Type == DataType.Number && float.IsFinite((float)instance.Get("MinLength").Number))
                    Assign(node, "MinLength", node.TrailMinLength, (float)Math.Clamp(instance.Get("MinLength").Number, 0, 100), value => node.TrailMinLength = value);
                if (instance.Get("MaxLength").Type == DataType.Number && float.IsFinite((float)instance.Get("MaxLength").Number))
                    Assign(node, "MaxLength", node.TrailMaxLength, (float)Math.Clamp(instance.Get("MaxLength").Number, 0, 10000), value => node.TrailMaxLength = value);
            }
            if (instance.Get("Texture").CastToString() is { } texture) Assign(node, "Texture", node.Texture, texture, value => node.Texture = value);
            if (instance.Get("Text").CastToString() is { } text) Assign(node, "Text", node.Text, text, value => node.Text = value);
            if (node.Type == "TextBox" && instance.Get("PlaceholderText").CastToString() is { } placeholderText)
                Assign(node, "PlaceholderText", node.PlaceholderText, placeholderText, value => node.PlaceholderText = value);
            if (instance.Get("TextColor").CastToString() is { } textColor) Assign(node, "TextColor", node.TextColor, textColor, value => node.TextColor = value);
            if (node.Type == "TextBox")
            {
                if (instance.Get("MultiLine").Type == DataType.Boolean) Assign(node, "MultiLine", node.MultiLine, instance.Get("MultiLine").Boolean, value => node.MultiLine = value);
                if (instance.Get("TextEditable").Type == DataType.Boolean) Assign(node, "TextEditable", node.TextEditable, instance.Get("TextEditable").Boolean, value => node.TextEditable = value);
                if (instance.Get("ClearTextOnFocus").Type == DataType.Boolean) Assign(node, "ClearTextOnFocus", node.ClearTextOnFocus, instance.Get("ClearTextOnFocus").Boolean, value => node.ClearTextOnFocus = value);
                instance.Set("MultiLine", DynValue.NewBoolean(node.MultiLine));
                instance.Set("TextEditable", DynValue.NewBoolean(node.TextEditable));
                instance.Set("ClearTextOnFocus", DynValue.NewBoolean(node.ClearTextOnFocus));
            }
            if (node.Type is ("TextLabel" or "TextButton") && instance.Get("TextWrapped").Type == DataType.Boolean)
                Assign(node, "TextWrapped", node.TextWrapped, instance.Get("TextWrapped").Boolean, value => node.TextWrapped = value);
            if (instance.Get("Visible").Type == DataType.Boolean) Assign(node, "Visible", node.Visible, instance.Get("Visible").Boolean, value => node.Visible = value);
            if (node.Type == "ScreenGui" && instance.Get("DisplayOrder").Type == DataType.Number
                && double.IsFinite(instance.Get("DisplayOrder").Number) && instance.Get("DisplayOrder").Number == Math.Truncate(instance.Get("DisplayOrder").Number)
                && instance.Get("DisplayOrder").Number >= int.MinValue && instance.Get("DisplayOrder").Number <= int.MaxValue)
                Assign(node, "DisplayOrder", node.DisplayOrder, (int)instance.Get("DisplayOrder").Number, value => node.DisplayOrder = value);
            if (node.Type == "ScreenGui" && instance.Get("ResetOnSpawn").Type == DataType.Boolean)
                Assign(node, "ResetOnSpawn", node.ResetOnSpawn, instance.Get("ResetOnSpawn").Boolean, value => node.ResetOnSpawn = value);
            if (IsGuiObject(node) || node.Type == "UIShadow")
            {
                var requestedZIndex = instance.Get("ZIndex");
                if (requestedZIndex.Type == DataType.Number && double.IsFinite(requestedZIndex.Number)
                    && requestedZIndex.Number == Math.Truncate(requestedZIndex.Number)
                    && requestedZIndex.Number >= int.MinValue && requestedZIndex.Number <= int.MaxValue)
                    Assign(node, "ZIndex", node.ZIndex, node.Type == "UIShadow" ? Math.Min(0, (int)requestedZIndex.Number) : (int)requestedZIndex.Number, value => node.ZIndex = value);
                var requestedLayoutOrder = instance.Get("LayoutOrder");
                if (IsGuiObject(node) && requestedLayoutOrder.Type == DataType.Number && double.IsFinite(requestedLayoutOrder.Number)
                    && requestedLayoutOrder.Number == Math.Truncate(requestedLayoutOrder.Number)
                    && requestedLayoutOrder.Number >= int.MinValue && requestedLayoutOrder.Number <= int.MaxValue)
                    Assign(node, "LayoutOrder", node.LayoutOrder, (int)requestedLayoutOrder.Number, value => node.LayoutOrder = value);
            }
            if (node.Type == "UICorner")
            {
                var current = UICornerRadiusService.Get(node, "CornerRadius");
                var individualProperties = new[] { "TopLeftRadius", "TopRightRadius", "BottomRightRadius", "BottomLeftRadius" };
                var previous = individualProperties.ToDictionary(property => property, property => UICornerRadiusService.Get(node, property).ToArray(), StringComparer.Ordinal);
                var changedAll = TryReadCornerUDim(instance.Get("CornerRadius"), out var requested)
                    && !current.SequenceEqual(requested);
                if (changedAll)
                {
                    UICornerRadiusService.Set(node, "CornerRadius", requested[0], requested[1]);
                    MarkChanged(node, "CornerRadius");
                }
                foreach (var cornerProperty in individualProperties)
                {
                    if (!TryReadCornerUDim(instance.Get(cornerProperty), out requested)
                        || (changedAll ? previous[cornerProperty] : UICornerRadiusService.Get(node, cornerProperty)).SequenceEqual(requested)) continue;
                    UICornerRadiusService.Set(node, cornerProperty, requested[0], requested[1]);
                    MarkChanged(node, cornerProperty);
                }
                SetCornerInstanceProperties(node, instance);
            }
            if (node.Type == "UIShadow")
            {
                if (TryReadUDim(instance.Get("BlurRadius"), out var blur))
                {
                    blur = UIShadowService.NormalizeUDim(blur, false);
                    if (!node.ShadowBlurRadius.SequenceEqual(blur)) { node.ShadowBlurRadius = blur; MarkChanged(node, "BlurRadius"); }
                }
                if (TryReadUDim2(instance.Get("Offset"), out var offsetScale, out var offset))
                {
                    offsetScale = UIShadowService.NormalizePair(offsetScale);
                    offset = UIShadowService.NormalizePair(offset);
                    if (!node.ShadowOffsetScale.SequenceEqual(offsetScale) || !node.ShadowOffset.SequenceEqual(offset))
                    { node.ShadowOffsetScale = offsetScale; node.ShadowOffset = offset; MarkChanged(node, "Offset"); }
                }
                if (TryReadUDim2(instance.Get("Spread"), out var spreadScale, out var spread))
                {
                    spreadScale = UIShadowService.NormalizePair(spreadScale);
                    spread = UIShadowService.NormalizePair(spread);
                    if (!node.ShadowSpreadScale.SequenceEqual(spreadScale) || !node.ShadowSpread.SequenceEqual(spread))
                    { node.ShadowSpreadScale = spreadScale; node.ShadowSpread = spread; MarkChanged(node, "Spread"); }
                }
                if (instance.Get("Inset").Type == DataType.Boolean)
                    Assign(node, "Inset", node.ShadowInset, instance.Get("Inset").Boolean, value => node.ShadowInset = value);
                if (instance.Get("ShowBehindParent").Type == DataType.Boolean)
                    Assign(node, "ShowBehindParent", node.ShadowShowBehindParent, instance.Get("ShowBehindParent").Boolean, value => node.ShadowShowBehindParent = value);
                if (instance.Get("Mode") is { Type: DataType.String, String: "Shape" or "Text" } mode)
                    Assign(node, "Mode", node.ShadowMode, mode.String, value => node.ShadowMode = value);
                SetShadowInstanceProperties(node, instance);
            }
            if (instance.Get("StrokeColor").CastToString() is { } strokeColor) Assign(node, "StrokeColor", node.StrokeColor, strokeColor, value => node.StrokeColor = value);
            if (instance.Get("StrokeThickness").Type == DataType.Number) Assign(node, "StrokeThickness", node.StrokeThickness, (float)Math.Max(0, instance.Get("StrokeThickness").Number), value => node.StrokeThickness = value);
            if (node.Type is "TextLabel" or "TextButton" or "TextBox" && instance.Get("TextSize") is { Type: DataType.Number } requestedTextSize && double.IsFinite(requestedTextSize.Number))
                Assign(node, "TextSize", node.TextSize, (float)Math.Clamp(requestedTextSize.Number, 1, 200), value => node.TextSize = value);
            if (node.Type is "TextLabel" or "TextButton" or "TextBox" && instance.Get("TextScaled").Type == DataType.Boolean)
                Assign(node, "TextScaled", node.TextScaled, instance.Get("TextScaled").Boolean, value => node.TextScaled = value);
            if (node.Type == "UITextSizeConstraint")
            {
                if (instance.Get("MinTextSize") is { Type: DataType.Number } minTextSize && double.IsFinite(minTextSize.Number))
                    Assign(node, "MinTextSize", node.MinTextSize, (float)Math.Clamp(minTextSize.Number, 1, 1000), value => node.MinTextSize = value);
                if (instance.Get("MaxTextSize") is { Type: DataType.Number } maxTextSize && double.IsFinite(maxTextSize.Number))
                    Assign(node, "MaxTextSize", node.MaxTextSize, (float)Math.Clamp(maxTextSize.Number, node.MinTextSize, 1000), value => node.MaxTextSize = value);
                instance.Set("MinTextSize", DynValue.NewNumber(node.MinTextSize));
                instance.Set("MaxTextSize", DynValue.NewNumber(node.MaxTextSize));
            }
            if (node.Type == "UITableLayout" && instance.Get("LayoutSpacing") is { Type: DataType.Number } requestedSpacing && double.IsFinite(requestedSpacing.Number))
                Assign(node, "LayoutSpacing", node.LayoutSpacing, (float)Math.Clamp(requestedSpacing.Number, 0, 1000000), value => node.LayoutSpacing = value);
            if (instance.Get("TableColumns").Type == DataType.Number) Assign(node, "TableColumns", node.TableColumns, (int)Math.Clamp(instance.Get("TableColumns").Number, 1, 24), value => node.TableColumns = value);
            if (node.Type is "UIAspectRatio" or "UIAspectRatioConstraint" && instance.Get("AspectRatio") is { Type: DataType.Number } requestedAspectRatio && double.IsFinite(requestedAspectRatio.Number))
                Assign(node, "AspectRatio", node.AspectRatio, (float)Math.Clamp(requestedAspectRatio.Number, .001, 1000), value => node.AspectRatio = value);
            if (node.Type == "UIAspectRatioConstraint")
            {
                var aspectType = instance.Get("AspectType").CastToString();
                if (aspectType is "FitWithinMaxSize" or "ScaleWithParentSize")
                    Assign(node, "AspectType", node.AspectType, aspectType, value => node.AspectType = value);
                var dominantAxis = instance.Get("DominantAxis").CastToString();
                if (dominantAxis is "Width" or "Height")
                    Assign(node, "DominantAxis", node.DominantAxis, dominantAxis, value => node.DominantAxis = value);
            }
            if (node.Type == "UISizeConstraint")
            {
                if (TryReadVector2(instance.Get("MinSize"), out var requestedMin))
                {
                    var nextMin = requestedMin.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    if (!node.MinSize.SequenceEqual(nextMin)) { node.MinSize = nextMin; MarkChanged(node, "MinSize"); }
                }
                if (TryReadVector2(instance.Get("MaxSize"), out var requestedMax))
                {
                    var nextMax = new[] { Math.Clamp(requestedMax[0], node.MinSize[0], 1000000), Math.Clamp(requestedMax[1], node.MinSize[1], 1000000) };
                    if (!node.MaxSize.SequenceEqual(nextMax)) { node.MaxSize = nextMax; MarkChanged(node, "MaxSize"); }
                }
                instance.Set("MinSize", Vector2Value(node.MinSize));
                instance.Set("MaxSize", Vector2Value(node.MaxSize));
            }
            if (node.Type == "UIListLayout")
            {
                var fillDirection = instance.Get("FillDirection").CastToString();
                var legacyDirection = instance.Get("LayoutDirection").CastToString();
                if (fillDirection is ("Horizontal" or "Vertical") && fillDirection != node.LayoutDirection)
                    Assign(node, "FillDirection", node.LayoutDirection, fillDirection, value => node.LayoutDirection = value);
                else if (legacyDirection is ("Horizontal" or "Vertical"))
                    Assign(node, "LayoutDirection", node.LayoutDirection, legacyDirection, value => node.LayoutDirection = value);
                instance.Set("FillDirection", DynValue.NewString(node.LayoutDirection));
                instance.Set("LayoutDirection", DynValue.NewString(node.LayoutDirection));
                if (TryReadUDim(instance.Get("Padding"), out var listPadding)
                    && (listPadding[0] != node.ListPaddingScale || listPadding[1] != node.LayoutSpacing))
                {
                    node.ListPaddingScale = Math.Clamp(listPadding[0], 0, 100);
                    node.LayoutSpacing = Math.Clamp(listPadding[1], 0, 1000000);
                    MarkChanged(node, "Padding");
                }
                else if (instance.Get("LayoutSpacing") is { Type: DataType.Number } listSpacing && double.IsFinite(listSpacing.Number))
                    Assign(node, "LayoutSpacing", node.LayoutSpacing, (float)Math.Clamp(listSpacing.Number, 0, 1000000), value => node.LayoutSpacing = value);
                instance.Set("Padding", CreateUDim(node.ListPaddingScale, node.LayoutSpacing));
                instance.Set("LayoutSpacing", DynValue.NewNumber(node.LayoutSpacing));
                if (instance.Get("HorizontalAlignment").CastToString() is { } horizontalAlignment && horizontalAlignment is ("Left" or "Center" or "Right"))
                    Assign(node, "HorizontalAlignment", node.HorizontalAlignment, horizontalAlignment, value => node.HorizontalAlignment = value);
                if (instance.Get("VerticalAlignment").CastToString() is { } verticalAlignment && verticalAlignment is ("Top" or "Center" or "Bottom"))
                    Assign(node, "VerticalAlignment", node.VerticalAlignment, verticalAlignment, value => node.VerticalAlignment = value);
                if (instance.Get("SortOrder").CastToString() is { } sortOrder && sortOrder is ("LayoutOrder" or "Name"))
                    Assign(node, "SortOrder", node.SortOrder, sortOrder, value => node.SortOrder = value);
            }
            if (node.Type == "UIPageLayout")
            {
                var requestedPageIndex = instance.Get("CurrentPageIndex");
                var pageIndexChanged = requestedPageIndex.Type == DataType.Number && double.IsFinite(requestedPageIndex.Number)
                    && requestedPageIndex.Number == Math.Truncate(requestedPageIndex.Number) && requestedPageIndex.Number >= 1
                    && requestedPageIndex.Number <= 1000000 && (int)requestedPageIndex.Number != node.CurrentPageIndex;
                if (pageIndexChanged)
                {
                    Assign(node, "CurrentPageIndex", node.CurrentPageIndex, (int)requestedPageIndex.Number, value => node.CurrentPageIndex = value);
                }
                else if (ReadNodeArgument(instance.Get("CurrentPage")) is { } requestedPage)
                {
                    var pages = GetPageNodes(node);
                    var currentPageIndex = Array.FindIndex(pages, page => page.Id == requestedPage.Id) + 1;
                    if (currentPageIndex > 0)
                        Assign(node, "CurrentPageIndex", node.CurrentPageIndex, currentPageIndex, value => node.CurrentPageIndex = value);
                }
                if (instance.Get("Circular").Type == DataType.Boolean)
                    Assign(node, "Circular", node.Circular, instance.Get("Circular").Boolean, value => node.Circular = value);
                if (instance.Get("SortOrder").CastToString() is { } pageSortOrder && pageSortOrder is ("LayoutOrder" or "Name"))
                    Assign(node, "SortOrder", node.SortOrder, pageSortOrder, value => node.SortOrder = value);
                instance.Set("CurrentPageIndex", DynValue.NewNumber(node.CurrentPageIndex));
                instance.Set("Circular", DynValue.NewBoolean(node.Circular));
                instance.Set("SortOrder", DynValue.NewString(node.SortOrder));
                instance.Set("CurrentPage", GetPageInstanceValue(GetPageNodes(node), node.CurrentPageIndex));
            }
            if (node.Type == "UIFlexItem")
            {
                if (instance.Get("FlexMode").CastToString() is { } flexMode && flexMode is ("None" or "Grow" or "Shrink" or "Fill" or "Custom"))
                    Assign(node, "FlexMode", node.FlexMode, flexMode, value => node.FlexMode = value);
                if (instance.Get("GrowRatio") is { Type: DataType.Number } growRatio && double.IsFinite(growRatio.Number))
                    Assign(node, "GrowRatio", node.GrowRatio, (float)Math.Clamp(growRatio.Number, 0, 1000), value => node.GrowRatio = value);
                if (instance.Get("ShrinkRatio") is { Type: DataType.Number } shrinkRatio && double.IsFinite(shrinkRatio.Number))
                    Assign(node, "ShrinkRatio", node.ShrinkRatio, (float)Math.Clamp(shrinkRatio.Number, 0, 1000), value => node.ShrinkRatio = value);
                if (instance.Get("ItemLineAlignment").CastToString() is { } itemAlignment && itemAlignment is ("Automatic" or "Start" or "Center" or "End" or "Stretch"))
                    Assign(node, "ItemLineAlignment", node.ItemLineAlignment, itemAlignment, value => node.ItemLineAlignment = value);
            }
            if (node.Type == "UIGridLayout")
            {
                if (TryReadUDim2(instance.Get("CellSize"), out var sizeScale, out var sizeOffset)
                    && (!node.CellSizeScale.SequenceEqual(sizeScale) || !node.CellSize.SequenceEqual(sizeOffset)))
                {
                    node.CellSizeScale = sizeScale;
                    node.CellSize = sizeOffset;
                    MarkChanged(node, "CellSize");
                }
                if (TryReadUDim2(instance.Get("CellPadding"), out var paddingScale, out var paddingOffset)
                    && (!node.CellPaddingScale.SequenceEqual(paddingScale) || !node.CellPadding.SequenceEqual(paddingOffset)))
                {
                    node.CellPaddingScale = paddingScale;
                    node.CellPadding = paddingOffset;
                    MarkChanged(node, "CellPadding");
                }
                if (instance.Get("FillDirection").CastToString() is { } direction && direction is ("Horizontal" or "Vertical"))
                    Assign(node, "FillDirection", node.FillDirection, direction, value => node.FillDirection = value);
                if (instance.Get("FillDirectionMaxCells").Type == DataType.Number && double.IsFinite(instance.Get("FillDirectionMaxCells").Number)
                    && instance.Get("FillDirectionMaxCells").Number == Math.Truncate(instance.Get("FillDirectionMaxCells").Number))
                    Assign(node, "FillDirectionMaxCells", node.FillDirectionMaxCells, (int)Math.Clamp(instance.Get("FillDirectionMaxCells").Number, 0, 1000), value => node.FillDirectionMaxCells = value);
            }
            if (instance.Get("CastShadow").Type == DataType.Boolean) Assign(node, "CastShadow", node.CastShadow, instance.Get("CastShadow").Boolean, value => node.CastShadow = value);
            if (instance.Get("Anchored").Type == DataType.Boolean) Assign(node, "Anchored", node.Anchored, instance.Get("Anchored").Boolean, value => node.Anchored = value);
            if (instance.Get("CanCollide").Type == DataType.Boolean) Assign(node, "CanCollide", node.CanCollide, instance.Get("CanCollide").Boolean, value => node.CanCollide = value);
            if (instance.Get("CanTouch").Type == DataType.Boolean) Assign(node, "CanTouch", node.CanTouch, instance.Get("CanTouch").Boolean, value => node.CanTouch = value);
            if (IsBasePart(node) && instance.Get("CanQuery").Type == DataType.Boolean) Assign(node, "CanQuery", node.CanQuery, instance.Get("CanQuery").Boolean, value => node.CanQuery = value);
            if (IsBasePart(node))
            {
                var assemblyVelocity = ReadVector(instance.Get("AssemblyLinearVelocity"), node.Velocity);
                var directVelocity = ReadVector(instance.Get("Velocity"), node.Velocity);
                var requestedVelocity = node.Velocity.SequenceEqual(assemblyVelocity) ? directVelocity : assemblyVelocity;
                if (!node.Velocity.SequenceEqual(requestedVelocity))
                {
                    node.Velocity = requestedVelocity;
                    instance.Set("Velocity", VectorValue(requestedVelocity));
                    instance.Set("AssemblyLinearVelocity", VectorValue(requestedVelocity));
                    MarkChanged(node, "Velocity");
                }
                var angularVelocity = ReadVector(instance.Get("AssemblyAngularVelocity"), node.AssemblyAngularVelocity);
                angularVelocity = angularVelocity.Select(component => Math.Clamp(component, -1000, 1000)).ToArray();
                if (!node.AssemblyAngularVelocity.SequenceEqual(angularVelocity))
                {
                    node.AssemblyAngularVelocity = angularVelocity;
                    instance.Set("AssemblyAngularVelocity", VectorValue(angularVelocity));
                    MarkChanged(node, "AssemblyAngularVelocity");
                }
            }
            if (node.Type == "Workspace" && instance.Get("Gravity").Type == DataType.Number && double.IsFinite(instance.Get("Gravity").Number))
                Assign(node, "Gravity", node.Gravity, (float)Math.Clamp(instance.Get("Gravity").Number, 0, 1000), value => node.Gravity = value);
            if (node.Type == "Workspace" && instance.Get("AutoManageCharacter").Type == DataType.Boolean)
                Assign(node, "AutoManageCharacter", node.AutoManageCharacter, instance.Get("AutoManageCharacter").Boolean, value => node.AutoManageCharacter = value);
            if (node.Type == "Tool")
            {
                if (instance.Get("ToolTip").Type == DataType.String)
                    Assign(node, "ToolTip", node.ToolTip, instance.Get("ToolTip").String, value => node.ToolTip = value);
                if (instance.Get("RequiresHandle").Type == DataType.Boolean)
                    Assign(node, "RequiresHandle", node.RequiresHandle, instance.Get("RequiresHandle").Boolean, value => node.RequiresHandle = value);
                if (instance.Get("CanBeDropped").Type == DataType.Boolean)
                    Assign(node, "CanBeDropped", node.CanBeDropped, instance.Get("CanBeDropped").Boolean, value => node.CanBeDropped = value);
                if (instance.Get("ManualActivationOnly").Type == DataType.Boolean)
                    Assign(node, "ManualActivationOnly", node.ManualActivationOnly, instance.Get("ManualActivationOnly").Boolean, value => node.ManualActivationOnly = value);
                if (instance.Get("GripPosition").Type == DataType.Table)
                {
                    var gripPosition = ReadVector(instance.Get("GripPosition"), node.GripPosition);
                    if (!node.GripPosition.SequenceEqual(gripPosition)) { node.GripPosition = gripPosition; MarkChanged(node, "GripPosition"); }
                }
                if (instance.Get("GripRotation").Type == DataType.Table)
                {
                    var gripRotation = ReadVector(instance.Get("GripRotation"), node.GripRotation).Select(component => Math.Clamp(component, -360000, 360000)).ToArray();
                    if (!node.GripRotation.SequenceEqual(gripRotation)) { node.GripRotation = gripRotation; MarkChanged(node, "GripRotation"); }
                }
                instance.Set("ToolTip", DynValue.NewString(node.ToolTip));
                instance.Set("RequiresHandle", DynValue.NewBoolean(node.RequiresHandle));
                instance.Set("CanBeDropped", DynValue.NewBoolean(node.CanBeDropped));
                instance.Set("ManualActivationOnly", DynValue.NewBoolean(node.ManualActivationOnly));
                instance.Set("GripPosition", VectorValue(node.GripPosition));
                instance.Set("GripRotation", VectorValue(node.GripRotation));
            }
            if (node.Type == "VehicleSeat")
            {
                if (instance.Get("Disabled").Type == DataType.Boolean)
                    Assign(node, "Disabled", node.VehicleSeatDisabled, instance.Get("Disabled").Boolean, value => node.VehicleSeatDisabled = value);
                if (instance.Get("HeadsUpDisplay").Type == DataType.Boolean)
                    Assign(node, "HeadsUpDisplay", node.VehicleSeatHeadsUpDisplay, instance.Get("HeadsUpDisplay").Boolean, value => node.VehicleSeatHeadsUpDisplay = value);
                if (instance.Get("MaxSpeed").Type == DataType.Number && double.IsFinite(instance.Get("MaxSpeed").Number))
                    Assign(node, "MaxSpeed", node.VehicleSeatMaxSpeed, (float)Math.Clamp(instance.Get("MaxSpeed").Number, 0, 10000), value => node.VehicleSeatMaxSpeed = value);
                if (instance.Get("Torque").Type == DataType.Number && double.IsFinite(instance.Get("Torque").Number))
                    Assign(node, "Torque", node.VehicleSeatTorque, (float)Math.Clamp(instance.Get("Torque").Number, 0, 1000000), value => node.VehicleSeatTorque = value);
                if (instance.Get("TurnSpeed").Type == DataType.Number && double.IsFinite(instance.Get("TurnSpeed").Number))
                    Assign(node, "TurnSpeed", node.VehicleSeatTurnSpeed, (float)Math.Clamp(instance.Get("TurnSpeed").Number, 0, 1000), value => node.VehicleSeatTurnSpeed = value);
                instance.Set("Disabled", DynValue.NewBoolean(node.VehicleSeatDisabled));
                instance.Set("HeadsUpDisplay", DynValue.NewBoolean(node.VehicleSeatHeadsUpDisplay));
                instance.Set("MaxSpeed", DynValue.NewNumber(node.VehicleSeatMaxSpeed));
                instance.Set("Torque", DynValue.NewNumber(node.VehicleSeatTorque));
                instance.Set("TurnSpeed", DynValue.NewNumber(node.VehicleSeatTurnSpeed));
            }
            if (node.Type == "Player")
            {
                var characterValue = instance.Get("Character");
                var characterNode = ReadNodeArgument(characterValue);
                if (characterValue.Type == DataType.Nil || characterNode?.Type == "Model")
                {
                    var previousCharacter = node.Character;
                    var nextCharacter = characterNode?.Id ?? string.Empty;
                    if (previousCharacter != nextCharacter)
                    {
                        FirePlayerCharacterSignal(node, "CharacterRemoving", previousCharacter);
                        Assign(node, "Character", previousCharacter, nextCharacter, value => node.Character = value);
                        FirePlayerCharacterSignal(node, "CharacterAdded", nextCharacter);
                    }
                }
                var teamValue = instance.Get("Team");
                var teamNode = ReadNodeArgument(teamValue);
                if (teamValue.Type == DataType.Nil || teamNode?.Type == "Team" && project.Teams.Children.Contains(teamNode))
                    Assign(node, "Team", node.Team, teamNode?.Id ?? string.Empty, value => node.Team = value);
                instance.Set("TeamColor", ColorValue(FindNode(node.Team)?.TeamColor ?? "#FFFFFF"));
            }
            if (node.Type == "Team")
            {
                if (IsTypedValue(instance.Get("TeamColor"), "Color3"))
                    Assign(node, "TeamColor", node.TeamColor, ReadColor(instance.Get("TeamColor"), node.TeamColor), value => node.TeamColor = value);
                if (instance.Get("AutoAssignable").Type == DataType.Boolean)
                    Assign(node, "AutoAssignable", node.AutoAssignable, instance.Get("AutoAssignable").Boolean, value => node.AutoAssignable = value);
                instance.Set("TeamColor", ColorValue(node.TeamColor));
                instance.Set("AutoAssignable", DynValue.NewBoolean(node.AutoAssignable));
                SyncTeamPropertyInstances(node);
            }
            if (node.Type == "SpawnLocation")
            {
                if (instance.Get("TeamColor") is { } teamColor && IsTypedValue(teamColor, "Color3"))
                    Assign(node, "TeamColor", node.TeamColor, ReadColor(teamColor, node.TeamColor), value => node.TeamColor = value);
                if (instance.Get("Neutral").Type == DataType.Boolean)
                    Assign(node, "Neutral", node.Neutral, instance.Get("Neutral").Boolean, value => node.Neutral = value);
                if (instance.Get("AllowTeamChangeOnTouch").Type == DataType.Boolean)
                    Assign(node, "AllowTeamChangeOnTouch", node.AllowTeamChangeOnTouch, instance.Get("AllowTeamChangeOnTouch").Boolean, value => node.AllowTeamChangeOnTouch = value);
                if (instance.Get("Duration").Type == DataType.Number && double.IsFinite(instance.Get("Duration").Number))
                    Assign(node, "Duration", node.Duration, (float)Math.Clamp(instance.Get("Duration").Number, 0, 10000), value => node.Duration = value);
                instance.Set("TeamColor", ColorValue(node.TeamColor));
                instance.Set("Neutral", DynValue.NewBoolean(node.Neutral));
                instance.Set("AllowTeamChangeOnTouch", DynValue.NewBoolean(node.AllowTeamChangeOnTouch));
                instance.Set("Duration", DynValue.NewNumber(node.Duration));
            }
            if (node.Type == "Humanoid")
            {
                if (instance.Get("MaxHealth").Type == DataType.Number && double.IsFinite(instance.Get("MaxHealth").Number))
                    Assign(node, "MaxHealth", node.MaxHealth, (float)Math.Clamp(instance.Get("MaxHealth").Number, 1, 100000), value => node.MaxHealth = value);
                if (instance.Get("Health").Type == DataType.Number && double.IsFinite(instance.Get("Health").Number))
                    Assign(node, "Health", node.Health, (float)Math.Clamp(instance.Get("Health").Number, 0, node.MaxHealth), value => node.Health = value);
                if (node.Health > node.MaxHealth) Assign(node, "Health", node.Health, node.MaxHealth, value => node.Health = value);
                if (instance.Get("WalkSpeed").Type == DataType.Number && double.IsFinite(instance.Get("WalkSpeed").Number))
                    Assign(node, "WalkSpeed", node.WalkSpeed, (float)Math.Clamp(instance.Get("WalkSpeed").Number, 0, 1000), value => node.WalkSpeed = value);
                if (instance.Get("JumpPower").Type == DataType.Number && double.IsFinite(instance.Get("JumpPower").Number))
                    Assign(node, "JumpPower", node.JumpPower, (float)Math.Clamp(instance.Get("JumpPower").Number, 0, 1000), value => node.JumpPower = value);
                instance.Set("MaxHealth", DynValue.NewNumber(node.MaxHealth));
                instance.Set("Health", DynValue.NewNumber(node.Health));
                instance.Set("WalkSpeed", DynValue.NewNumber(node.WalkSpeed));
                instance.Set("JumpPower", DynValue.NewNumber(node.JumpPower));
            }
            if (node.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Terrain" or "Gui" or "StarterGui") && instance.Get("Name").CastToString() is { } name)
                Assign(node, "Name", node.Name, name, value => SetNodeName(node, value));
            else if (node.Type is "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Terrain" or "Gui" or "StarterGui")
                instance.Set("Name", DynValue.NewString(node.Name));
            if (instance.Get("Archivable").Type == DataType.Boolean)
                Assign(node, "Archivable", node.Archivable, instance.Get("Archivable").Boolean, value => node.Archivable = value);
            else instance.Set("Archivable", DynValue.NewBoolean(node.Archivable));
            if (node.Type == "Workspace")
            {
                instance.Set("CurrentCamera", GetCurrentCameraReference());
                if (instance.Get("SkyboxFront").CastToString() is { } skyboxFront) Assign(node, "SkyboxFront", node.SkyboxFront, skyboxFront, value => node.SkyboxFront = value);
                if (instance.Get("SkyboxBack").CastToString() is { } skyboxBack) Assign(node, "SkyboxBack", node.SkyboxBack, skyboxBack, value => node.SkyboxBack = value);
                if (instance.Get("SkyboxLeft").CastToString() is { } skyboxLeft) Assign(node, "SkyboxLeft", node.SkyboxLeft, skyboxLeft, value => node.SkyboxLeft = value);
                if (instance.Get("SkyboxRight").CastToString() is { } skyboxRight) Assign(node, "SkyboxRight", node.SkyboxRight, skyboxRight, value => node.SkyboxRight = value);
                if (instance.Get("SkyboxTop").CastToString() is { } skyboxTop) Assign(node, "SkyboxTop", node.SkyboxTop, skyboxTop, value => node.SkyboxTop = value);
                if (instance.Get("SkyboxBottom").CastToString() is { } skyboxBottom) Assign(node, "SkyboxBottom", node.SkyboxBottom, skyboxBottom, value => node.SkyboxBottom = value);
            }
            if (node.Type == "ViewportFrame")
            {
                var cameraValue = instance.Get("CurrentCamera");
                var currentCamera = cameraValue.Type == DataType.Nil ? null : ReadNodeArgument(cameraValue);
                if (cameraValue.Type == DataType.Nil || currentCamera?.Type == "Camera")
                    Assign(node, "CurrentCamera", node.ViewportCurrentCameraId, currentCamera?.Id ?? string.Empty, value => node.ViewportCurrentCameraId = value);
                else instance.Set("CurrentCamera", GetObjectValueReference(node.ViewportCurrentCameraId));
            }
            if (node.Type == "Sky")
            {
                if (instance.Get("SkyboxFt").CastToString() is { } skyboxFt) Assign(node, "SkyboxFt", node.SkyboxFront, skyboxFt, value => node.SkyboxFront = value);
                if (instance.Get("SkyboxBk").CastToString() is { } skyboxBk) Assign(node, "SkyboxBk", node.SkyboxBack, skyboxBk, value => node.SkyboxBack = value);
                if (instance.Get("SkyboxLf").CastToString() is { } skyboxLf) Assign(node, "SkyboxLf", node.SkyboxLeft, skyboxLf, value => node.SkyboxLeft = value);
                if (instance.Get("SkyboxRt").CastToString() is { } skyboxRt) Assign(node, "SkyboxRt", node.SkyboxRight, skyboxRt, value => node.SkyboxRight = value);
                if (instance.Get("SkyboxUp").CastToString() is { } skyboxUp) Assign(node, "SkyboxUp", node.SkyboxTop, skyboxUp, value => node.SkyboxTop = value);
                if (instance.Get("SkyboxDn").CastToString() is { } skyboxDn) Assign(node, "SkyboxDn", node.SkyboxBottom, skyboxDn, value => node.SkyboxBottom = value);
            }
            var parentValue = instance.Get("Parent");
            if (ReadNodeArgument(parentValue) is { } newParent && FindParentNode(node)?.Id != newParent.Id)
            {
                if (TrySetParent(node, parentValue, false)) MarkChanged(node, "Parent");
            }
            else if (parentValue.Type == DataType.Nil && FindParentNode(node) is not null && TrySetParent(node, DynValue.Nil, false))
                MarkChanged(node, "Parent");
            if (node.Type == "Camera")
            {
                var modeValue = instance.Get("Mode").CastToString();
                var cameraModeValue = instance.Get("CameraMode").CastToString();
                var mode = (modeValue is "Custom" or "Subject") && modeValue != node.CameraMode
                    ? modeValue
                    : (cameraModeValue is "Custom" or "Subject") && cameraModeValue != node.CameraMode
                        ? cameraModeValue
                        : node.CameraMode;
                var subjectValue = instance.Get("Subject");
                var requestedSubject = ReadNodeArgument(subjectValue);
                var subjectForMode = IsBasePart(requestedSubject) ? requestedSubject : FindNode(node.Subject);
                if (mode == "Subject" && !IsBasePart(subjectForMode)) mode = node.CameraMode;
                if (mode is "Custom" or "Subject") Assign(node, "Mode", node.CameraMode, mode, value => node.CameraMode = value);
                if (IsBasePart(requestedSubject)) Assign(node, "Subject", node.Subject, requestedSubject!.Id, value => node.Subject = value);
                else if (subjectValue.Type == DataType.Nil && mode != "Subject") Assign(node, "Subject", node.Subject, string.Empty, value => node.Subject = value);
                instance.Set("Mode", DynValue.NewString(node.CameraMode));
                instance.Set("CameraMode", DynValue.NewString(node.CameraMode));
                instance.Set("Subject", GetPropertyValue(node, "Subject"));
                if (instance.Get("FieldOfView").Type == DataType.Number) Assign(node, "FieldOfView", node.FieldOfView, (float)Math.Clamp(instance.Get("FieldOfView").Number, 10, 120), value => node.FieldOfView = value);
            }
        }
        UpdateParentLinks();
        foreach (var (key, node) in changed)
        {
            var currentValue = GetPropertyValue(node, key.Property);
            propertyChanged?.Invoke(node, key.Property, currentValue);
            if (key.Property == "Value") FireValueChanged(node, currentValue);
        }
        UpdateHumanoidDeaths();
        ProcessExplosions();
    }

    private DynValue CreateLookAtRotation(CallbackArguments args)
    {
        if (args.Count < 2 || !IsTypedValue(args[0], "Vector3") || !IsTypedValue(args[1], "Vector3")) return DynValue.Nil;
        var from = ReadVector(args[0], [float.NaN, float.NaN, float.NaN]);
        var to = ReadVector(args[1], [float.NaN, float.NaN, float.NaN]);
        if (!from.All(float.IsFinite) || !to.All(float.IsFinite)) return DynValue.Nil;
        var x = (double)to[0] - from[0];
        var y = (double)to[1] - from[1];
        var z = (double)to[2] - from[2];
        var distance = Math.Sqrt(x * x + y * y + z * z);
        if (distance < 0.000001) return VectorValue([0, 0, 0]);
        var pitch = Math.Asin(Math.Clamp(y / distance, -1, 1)) * 180 / Math.PI;
        var yaw = x * x + z * z < 0.000000000001 ? 0 : Math.Atan2(-x, -z) * 180 / Math.PI;
        return VectorValue([(float)pitch, (float)yaw, 0]);
    }

    private DynValue Vector2Value(float[] values)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("Vector2"));
        table.Set("X", DynValue.NewNumber(values[0]));
        table.Set("Y", DynValue.NewNumber(values[1]));
        table.Set(1, DynValue.NewNumber(values[0]));
        table.Set(2, DynValue.NewNumber(values[1]));
        var magnitude = (float)Math.Sqrt(values[0] * values[0] + values[1] * values[1]);
        table.Set("Magnitude", DynValue.NewNumber(magnitude));
        var divisor = magnitude > 0 ? magnitude : 1;
        table.Set("Unit", Vector2ValueWithoutDerivedProperties([values[0] / divisor, values[1] / divisor]));
        table.Set("Perpendicular", Vector2ValueWithoutDerivedProperties([-values[1], values[0]]));
        table.Set("Dot", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index || !TryReadVector2(args[index], out var other)) return DynValue.Nil;
            return DynValue.NewNumber(values[0] * other[0] + values[1] * other[1]);
        }));
        table.Set("Lerp", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index + 1 || args[index + 1].Type != DataType.Number || !double.IsFinite(args[index + 1].Number)
                || !TryReadVector2(args[index], out var other)) return DynValue.Nil;
            var alpha = (float)args[index + 1].Number;
            return Vector2Value([values[0] + (other[0] - values[0]) * alpha, values[1] + (other[1] - values[1]) * alpha]);
        }));
        table.Set("Cross", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            return args.Count > index && TryReadVector2(args[index], out var other)
                ? DynValue.NewNumber(values[0] * other[1] - values[1] * other[0]) : DynValue.Nil;
        }));
        table.Set("Angle", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index || !TryReadVector2(args[index], out var other)) return DynValue.Nil;
            var otherMagnitude = Math.Sqrt(other[0] * other[0] + other[1] * other[1]);
            return DynValue.NewNumber(magnitude == 0 || otherMagnitude == 0 ? 0
                : Math.Acos(Math.Clamp((values[0] * other[0] + values[1] * other[1]) / (magnitude * otherMagnitude), -1, 1)));
        }));
        table.Set("FuzzyEq", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index || !TryReadVector2(args[index], out var other)) return DynValue.NewBoolean(false);
            var epsilon = args.Count > index + 1 && args[index + 1].Type == DataType.Number && double.IsFinite(args[index + 1].Number)
                ? (float)Math.Clamp(args[index + 1].Number, 0, 1000000) : 0.00001f;
            return DynValue.NewBoolean(Math.Abs(values[0] - other[0]) <= epsilon && Math.Abs(values[1] - other[1]) <= epsilon);
        }));
        table.Set("Abs", DynValue.NewCallback((_, _) => Vector2Value([Math.Abs(values[0]), Math.Abs(values[1])])));
        table.Set("Ceil", DynValue.NewCallback((_, _) => Vector2Value([MathF.Ceiling(values[0]), MathF.Ceiling(values[1])])));
        table.Set("Floor", DynValue.NewCallback((_, _) => Vector2Value([MathF.Floor(values[0]), MathF.Floor(values[1])])));
        table.Set("Sign", DynValue.NewCallback((_, _) => Vector2Value([MathF.Sign(values[0]), MathF.Sign(values[1])])));
        table.Set("Min", DynValue.NewCallback((_, args) => Vector2ComponentExtremum(table, args, Math.Min)));
        table.Set("Max", DynValue.NewCallback((_, args) => Vector2ComponentExtremum(table, args, Math.Max)));
        table.Set("ClampMagnitude", DynValue.NewCallback((_, args) =>
        {
            var maxMagnitude = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= maxMagnitude || args[maxMagnitude].Type != DataType.Number || !double.IsFinite(args[maxMagnitude].Number)) return DynValue.Nil;
            var limit = Math.Max(0, args[maxMagnitude].Number);
            return magnitude <= limit || magnitude == 0 ? Vector2Value(values)
                : Vector2Value([values[0] / magnitude * (float)limit, values[1] / magnitude * (float)limit]);
        }));
        table.MetaTable = vector2Metatable;
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue RectValue(float[] values)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("Rect"));
        table.Set("Min", Vector2Value([values[0], values[1]]));
        table.Set("Max", Vector2Value([values[2], values[3]]));
        table.Set("Width", DynValue.NewNumber(Math.Max(0, values[2] - values[0])));
        table.Set("Height", DynValue.NewNumber(Math.Max(0, values[3] - values[1])));
        return DynValue.NewTable(table);
    }

    private DynValue Vector2ComponentExtremum(Table receiver, CallbackArguments args, Func<float, float, float> operation)
    {
        var index = GetVectorMethodArgumentIndex(receiver, args);
        if (args.Count <= index || !TryReadVector2(args[index], out var other)) return DynValue.Nil;
        var values = ReadVector2(receiver);
        return Vector2Value([operation(values[0], other[0]), operation(values[1], other[1])]);
    }

    private static float[] ReadVector2(Table value) => [(float)value.Get("X").Number, (float)value.Get("Y").Number];

    private DynValue Vector2ValueWithoutDerivedProperties(float[] values)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("Vector2"));
        table.Set("X", DynValue.NewNumber(values[0]));
        table.Set("Y", DynValue.NewNumber(values[1]));
        table.Set(1, DynValue.NewNumber(values[0]));
        table.Set(2, DynValue.NewNumber(values[1]));
        table.MetaTable = vector2Metatable;
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue NumberRangeValue(float[] values)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("NumberRange"));
        table.Set("Min", DynValue.NewNumber(values[0]));
        table.Set("Max", DynValue.NewNumber(values[1]));
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue CreateNumberSequence(CallbackArguments args)
    {
        if (args.Count == 0) return DynValue.Nil;
        if (args[0].Type == DataType.Number && float.IsFinite((float)args[0].Number))
        {
            if (args.Count > 1 && (args[1].Type != DataType.Number || !float.IsFinite((float)args[1].Number))) return DynValue.Nil;
            var first = (float)args[0].Number;
            var last = args.Count > 1 ? (float)args[1].Number : first;
            return NumberSequenceValue([new(0, first), new(1, last)]);
        }
        return TryReadNumberKeypoints(args[0], out var points) ? NumberSequenceValue(points) : DynValue.Nil;
    }

    private DynValue CreateColorSequence(CallbackArguments args)
    {
        if (args.Count == 0) return DynValue.Nil;
        if (IsTypedValue(args[0], "Color3"))
        {
            if (args.Count > 1 && !IsTypedValue(args[1], "Color3")) return DynValue.Nil;
            var first = ReadColor(args[0], "#FFFFFF");
            var last = args.Count > 1 ? ReadColor(args[1], first) : first;
            return ColorSequenceValue([new(0, first), new(1, last)]);
        }
        return TryReadColorKeypoints(args[0], out var points) ? ColorSequenceValue(points) : DynValue.Nil;
    }

    private DynValue NumberSequenceKeypointValue(ParticleNumberKeypoint point)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("NumberSequenceKeypoint"));
        table.Set("Time", DynValue.NewNumber(point.Time));
        table.Set("Value", DynValue.NewNumber(point.Value));
        table.Set("Envelope", DynValue.NewNumber(point.Envelope));
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue ColorSequenceKeypointValue(ParticleColorKeypoint point)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("ColorSequenceKeypoint"));
        table.Set("Time", DynValue.NewNumber(point.Time));
        table.Set("Value", ColorValue(point.Value));
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue NumberSequenceValue(float value) => NumberSequenceValue([new(0, value), new(1, value)]);

    private DynValue NumberSequenceValue(IReadOnlyList<ParticleNumberKeypoint> points)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("NumberSequence"));
        var keypoints = new Table(script);
        for (var index = 0; index < points.Count; index++) keypoints.Set(index + 1, NumberSequenceKeypointValue(points[index]));
        keypoints.ReadOnly = true;
        table.Set("Keypoints", DynValue.NewTable(keypoints));
        table.Set("Value", DynValue.NewNumber(points[0].Value));
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private DynValue ColorSequenceValue(string color) => ColorSequenceValue([new(0, color), new(1, color)]);

    private DynValue ColorSequenceValue(IReadOnlyList<ParticleColorKeypoint> points)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("ColorSequence"));
        var keypoints = new Table(script);
        for (var index = 0; index < points.Count; index++) keypoints.Set(index + 1, ColorSequenceKeypointValue(points[index]));
        keypoints.ReadOnly = true;
        table.Set("Keypoints", DynValue.NewTable(keypoints));
        table.Set("Value", ColorValue(points[0].Value));
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private bool TryReadNumberRange(DynValue value, out float[] range)
    {
        range = [0, 0];
        if (!IsTypedValue(value, "NumberRange")) return false;
        var min = value.Table.Get("Min");
        var max = value.Table.Get("Max");
        if (min.Type != DataType.Number || max.Type != DataType.Number || !double.IsFinite(min.Number) || !double.IsFinite(max.Number)) return false;
        range = [(float)min.Number, (float)max.Number];
        return range.All(float.IsFinite);
    }

    private bool TryReadNumberKeypoints(DynValue value, out List<ParticleNumberKeypoint> points)
    {
        points = [];
        if (value.Type != DataType.Table) return false;
        for (var index = 1; index <= 20; index++)
        {
            var item = value.Table.Get(index);
            if (item.Type == DataType.Nil) break;
            if (!IsTypedValue(item, "NumberSequenceKeypoint")) return false;
            var time = item.Table.Get("Time");
            var number = item.Table.Get("Value");
            var envelope = item.Table.Get("Envelope");
            if (time.Type != DataType.Number || number.Type != DataType.Number || envelope.Type != DataType.Number
                || !float.IsFinite((float)time.Number) || !float.IsFinite((float)number.Number)
                || !float.IsFinite((float)envelope.Number)) return false;
            points.Add(new ParticleNumberKeypoint((float)time.Number, (float)number.Number, (float)envelope.Number));
        }
        if (value.Table.Get(points.Count + 1).Type != DataType.Nil) return false;
        return (points = ParticleSequenceService.NormalizeNumber(points, -float.MaxValue, float.MaxValue)).Count > 0;
    }

    private bool TryReadColorKeypoints(DynValue value, out List<ParticleColorKeypoint> points)
    {
        points = [];
        if (value.Type != DataType.Table) return false;
        for (var index = 1; index <= 20; index++)
        {
            var item = value.Table.Get(index);
            if (item.Type == DataType.Nil) break;
            if (!IsTypedValue(item, "ColorSequenceKeypoint")) return false;
            var time = item.Table.Get("Time");
            var color = item.Table.Get("Value");
            if (time.Type != DataType.Number || !float.IsFinite((float)time.Number) || !IsTypedValue(color, "Color3")) return false;
            points.Add(new ParticleColorKeypoint((float)time.Number, ReadColor(color, "#FFFFFF")));
        }
        if (value.Table.Get(points.Count + 1).Type != DataType.Nil) return false;
        return (points = ParticleSequenceService.NormalizeColor(points)).Count > 0;
    }

    private bool TryReadNumberSequence(DynValue value, out List<ParticleNumberKeypoint> points)
    {
        points = [];
        if (value.Type == DataType.Number && float.IsFinite((float)value.Number))
        {
            points = [new(0, (float)value.Number), new(1, (float)value.Number)];
            return true;
        }
        return IsTypedValue(value, "NumberSequence") && TryReadNumberKeypoints(value.Table.Get("Keypoints"), out points);
    }

    private bool TryReadColorSequence(DynValue value, out List<ParticleColorKeypoint> points)
    {
        points = [];
        if (IsTypedValue(value, "Color3"))
        {
            var color = ReadColor(value, "#FFFFFF");
            points = [new(0, color), new(1, color)];
            return true;
        }
        return IsTypedValue(value, "ColorSequence") && TryReadColorKeypoints(value.Table.Get("Keypoints"), out points);
    }

    private bool TryReadVector2(DynValue value, out float[] vector)
    {
        vector = [0, 0];
        if (!IsTypedValue(value, "Vector2")) return false;
        var x = value.Table.Get("X");
        var y = value.Table.Get("Y");
        if (x.Type != DataType.Number || y.Type != DataType.Number || !double.IsFinite(x.Number) || !double.IsFinite(y.Number)) return false;
        vector = [(float)x.Number, (float)y.Number];
        return vector.All(float.IsFinite);
    }

    private bool TryReadRect(DynValue value, out float[] rectangle)
    {
        rectangle = [0, 0, 0, 0];
        if (!IsTypedValue(value, "Rect") || !TryReadVector2(value.Table.Get("Min"), out var min) || !TryReadVector2(value.Table.Get("Max"), out var max)) return false;
        rectangle = NormalizeSliceCenter([min[0], min[1], max[0], max[1]]);
        return true;
    }

    private static float[] NormalizeSliceCenter(float[]? values)
    {
        var rectangle = new float[4];
        if (values is not null) Array.Copy(values, rectangle, Math.Min(values.Length, rectangle.Length));
        for (var index = 0; index < rectangle.Length; index++)
            rectangle[index] = float.IsFinite(rectangle[index]) ? Math.Clamp(rectangle[index], 0, 1000000) : 0;
        rectangle[2] = Math.Max(rectangle[0], rectangle[2]);
        rectangle[3] = Math.Max(rectangle[1], rectangle[3]);
        return rectangle;
    }

    private DynValue VectorValue(float[] values)
    {
        var table = new Table(script);
        runtimeTypeTags.Add(table, new LuaXTypeTag("Vector3"));
        table.Set("X", DynValue.NewNumber(values[0])); table.Set("Y", DynValue.NewNumber(values[1])); table.Set("Z", DynValue.NewNumber(values[2]));
        table.Set(1, DynValue.NewNumber(values[0])); table.Set(2, DynValue.NewNumber(values[1])); table.Set(3, DynValue.NewNumber(values[2]));
        var magnitude = (float)Math.Sqrt(values[0] * values[0] + values[1] * values[1] + values[2] * values[2]);
        table.Set("Magnitude", DynValue.NewNumber(magnitude));
        var unit = new Table(script);
        runtimeTypeTags.Add(unit, new LuaXTypeTag("Vector3"));
        var divisor = magnitude > 0 ? magnitude : 1;
        unit.Set("X", DynValue.NewNumber(values[0] / divisor)); unit.Set("Y", DynValue.NewNumber(values[1] / divisor)); unit.Set("Z", DynValue.NewNumber(values[2] / divisor));
        unit.Set(1, DynValue.NewNumber(values[0] / divisor)); unit.Set(2, DynValue.NewNumber(values[1] / divisor)); unit.Set(3, DynValue.NewNumber(values[2] / divisor));
        unit.ReadOnly = true;
        table.Set("Unit", DynValue.NewTable(unit));
        table.Set("Dot", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index) return DynValue.Nil;
            var other = ReadVector(args[index], [0, 0, 0]);
            return DynValue.NewNumber(values[0] * other[0] + values[1] * other[1] + values[2] * other[2]);
        }));
        table.Set("Cross", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index) return DynValue.Nil;
            var other = ReadVector(args[index], [0, 0, 0]);
            return VectorValue([values[1] * other[2] - values[2] * other[1], values[2] * other[0] - values[0] * other[2], values[0] * other[1] - values[1] * other[0]]);
        }));
        table.Set("Lerp", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index + 1 || args[index + 1].Type != DataType.Number) return DynValue.Nil;
            var other = ReadVector(args[index], [0, 0, 0]);
            var alpha = (float)args[index + 1].Number;
            return VectorValue([values[0] + (other[0] - values[0]) * alpha, values[1] + (other[1] - values[1]) * alpha, values[2] + (other[2] - values[2]) * alpha]);
        }));
        table.Set("Angle", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index) return DynValue.Nil;
            var other = ReadVector(args[index], [0, 0, 0]);
            var otherMagnitude = Math.Sqrt(other.Sum(value => value * value));
            return DynValue.NewNumber(magnitude == 0 || otherMagnitude == 0 ? 0
                : Math.Acos(Math.Clamp((values[0] * other[0] + values[1] * other[1] + values[2] * other[2]) / (magnitude * otherMagnitude), -1, 1)));
        }));
        table.Set("FuzzyEq", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index) return DynValue.NewBoolean(false);
            var other = ReadVector(args[index], [0, 0, 0]);
            var epsilon = args.Count > index + 1 && args[index + 1].Type == DataType.Number && double.IsFinite(args[index + 1].Number)
                ? (float)Math.Clamp(args[index + 1].Number, 0, 1000000) : 0.00001f;
            return DynValue.NewBoolean(Enumerable.Range(0, 3).All(axis => Math.Abs(values[axis] - other[axis]) <= epsilon));
        }));
        table.Set("Abs", DynValue.NewCallback((_, _) => VectorValue(values.Select(Math.Abs).ToArray())));
        table.Set("Ceil", DynValue.NewCallback((_, _) => VectorValue(values.Select(MathF.Ceiling).ToArray())));
        table.Set("Floor", DynValue.NewCallback((_, _) => VectorValue(values.Select(MathF.Floor).ToArray())));
        table.Set("Sign", DynValue.NewCallback((_, _) => VectorValue(values.Select(value => (float)MathF.Sign(value)).ToArray())));
        table.Set("Min", DynValue.NewCallback((_, args) => Vector3ComponentExtremum(table, args, Math.Min)));
        table.Set("Max", DynValue.NewCallback((_, args) => Vector3ComponentExtremum(table, args, Math.Max)));
        table.Set("ClampMagnitude", DynValue.NewCallback((_, args) =>
        {
            var index = GetVectorMethodArgumentIndex(table, args);
            if (args.Count <= index || args[index].Type != DataType.Number || !double.IsFinite(args[index].Number)) return DynValue.Nil;
            var limit = Math.Max(0, args[index].Number);
            return magnitude <= limit || magnitude == 0 ? VectorValue(values)
                : VectorValue(values.Select(value => value / magnitude * (float)limit).ToArray());
        }));
        table.MetaTable = vector3Metatable;
        table.ReadOnly = true;
        return DynValue.NewTable(table);
    }

    private static int GetVectorMethodArgumentIndex(Table receiver, CallbackArguments args) =>
        args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, receiver) ? 1 : 0;

    private DynValue CreateColor(float red, float green, float blue)
    {
        var color = new Table(script);
        runtimeTypeTags.Add(color, new LuaXTypeTag("Color3"));
        var channels = new[] { Math.Clamp(red, 0, 1), Math.Clamp(green, 0, 1), Math.Clamp(blue, 0, 1) };
        color.Set("R", DynValue.NewNumber(channels[0]));
        color.Set("G", DynValue.NewNumber(channels[1]));
        color.Set("B", DynValue.NewNumber(channels[2]));
        color.Set("Lerp", DynValue.NewCallback((_, args) =>
        {
            var index = GetColorMethodArgumentIndex(color, args);
            if (args.Count <= index + 1 || args[index + 1].Type != DataType.Number) return DynValue.Nil;
            var other = ReadColorChannels(args[index]);
            var alpha = (float)args[index + 1].Number;
            return CreateColor(channels[0] + (other[0] - channels[0]) * alpha, channels[1] + (other[1] - channels[1]) * alpha, channels[2] + (other[2] - channels[2]) * alpha);
        }));
        var hex = string.Concat(channels.Select(channel => ((byte)Math.Round(channel * 255, MidpointRounding.AwayFromZero)).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        color.Set("ToHex", DynValue.NewCallback((_, _) => DynValue.NewString(hex)));
        color.Set("ToHSV", DynValue.NewCallback((_, _) =>
        {
            var hsv = RgbToHsv(channels[0], channels[1], channels[2]);
            return DynValue.NewTuple(DynValue.NewNumber(hsv[0]), DynValue.NewNumber(hsv[1]), DynValue.NewNumber(hsv[2]));
        }));
        color.ReadOnly = true;
        return DynValue.NewTable(color);
    }

    private DynValue CreateUDim(float scale, float offset)
    {
        var value = new Table(script);
        runtimeTypeTags.Add(value, new LuaXTypeTag("UDim"));
        value.Set("Scale", DynValue.NewNumber(scale));
        value.Set("Offset", DynValue.NewNumber(offset));
        value.ReadOnly = true;
        return DynValue.NewTable(value);
    }

    private DynValue CreateUDim2(float xScale, float xOffset, float yScale, float yOffset)
    {
        var value = new Table(script);
        runtimeTypeTags.Add(value, new LuaXTypeTag("UDim2"));
        value.Set("X", CreateUDim(xScale, xOffset));
        value.Set("Y", CreateUDim(yScale, yOffset));
        value.ReadOnly = true;
        return DynValue.NewTable(value);
    }

    private string GetLuaXTypeName(DynValue value) => value.Type switch
    {
        DataType.Nil or DataType.Void => "nil",
        DataType.Boolean => "boolean",
        DataType.Number => "number",
        DataType.String => "string",
        DataType.Table => runtimeTypeTags.TryGetValue(value.Table, out var typeTag) ? typeTag.Name : "table",
        DataType.Function or DataType.ClrFunction or DataType.TailCallRequest => "function",
        DataType.Thread => "thread",
        DataType.UserData => "userdata",
        DataType.Tuple => "tuple",
        _ => "unknown"
    };

    private DynValue CreateUDim2Value(float[] scale, float[] offset) => CreateUDim2(scale[0], offset[0], scale[1], offset[1]);

    private DynValue CornerUDim(SceneNode node, string property)
    {
        var value = UICornerRadiusService.Get(node, property);
        return CreateUDim(value[0], value[1]);
    }

    private void SetCornerInstanceProperties(SceneNode node, Table instance)
    {
        foreach (var property in new[] { "CornerRadius", "TopLeftRadius", "TopRightRadius", "BottomRightRadius", "BottomLeftRadius" })
            instance.Set(property, CornerUDim(node, property));
    }

    private void SetShadowInstanceProperties(SceneNode node, Table instance)
    {
        instance.Set("BlurRadius", CreateUDim(node.ShadowBlurRadius[0], node.ShadowBlurRadius[1]));
        instance.Set("Offset", CreateUDim2Value(node.ShadowOffsetScale, node.ShadowOffset));
        instance.Set("Spread", CreateUDim2Value(node.ShadowSpreadScale, node.ShadowSpread));
        instance.Set("Inset", DynValue.NewBoolean(node.ShadowInset));
        instance.Set("ShowBehindParent", DynValue.NewBoolean(node.ShadowShowBehindParent));
        instance.Set("Mode", DynValue.NewString(node.ShadowMode));
        instance.Set("ZIndex", DynValue.NewNumber(node.ZIndex));
    }

    private bool TryReadCornerUDim(DynValue value, out float[] result)
    {
        if (TryReadUDim(value, out result)) return true;
        result = [0, 0];
        if (value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number < float.MinValue || value.Number > float.MaxValue) return false;
        result[1] = (float)value.Number;
        return true;
    }

    private bool TryReadUDim(DynValue value, out float[] result)
    {
        result = [0, 0];
        if (!IsTypedValue(value, "UDim")) return false;
        var scale = value.Table.Get("Scale");
        var offset = value.Table.Get("Offset");
        if (scale.Type != DataType.Number || offset.Type != DataType.Number
            || !double.IsFinite(scale.Number) || !double.IsFinite(offset.Number)) return false;
        result = [(float)scale.Number, (float)offset.Number];
        return result.All(float.IsFinite);
    }

    private static bool TryReadUDim2(DynValue value, out float[] scale, out float[] offset)
    {
        scale = [0, 0];
        offset = [0, 0];
        if (value.Type != DataType.Table) return false;
        var x = value.Table.Get("X");
        var y = value.Table.Get("Y");
        if (x.Type != DataType.Table || y.Type != DataType.Table) return false;
        var xScale = x.Table.Get("Scale");
        var xOffset = x.Table.Get("Offset");
        var yScale = y.Table.Get("Scale");
        var yOffset = y.Table.Get("Offset");
        if (xScale.Type != DataType.Number || xOffset.Type != DataType.Number || yScale.Type != DataType.Number || yOffset.Type != DataType.Number
            || !double.IsFinite(xScale.Number) || !double.IsFinite(xOffset.Number) || !double.IsFinite(yScale.Number) || !double.IsFinite(yOffset.Number)) return false;
        scale = [(float)xScale.Number, (float)yScale.Number];
        offset = [(float)xOffset.Number, (float)yOffset.Number];
        return scale.All(float.IsFinite) && offset.All(float.IsFinite);
    }

    private bool IsGuiObject(SceneNode node)
    {
        if (node.Type is not ("Frame" or "UICanvasGroup" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")) return false;
        if (node.Type != "ImageLabel") return true;
        var parent = FindParentNode(node);
        while (parent is not null)
        {
            if (parent.Id == project.Gui.Id || parent.Type is "ScreenGui" or "BillboardGui" or "SurfaceGui") return true;
            if (parent.Id == project.Workspace.Id || parent.Id == project.Lighting.Id || parent.Id == project.ReplicatedStorage.Id || parent.Id == project.ServerScriptService.Id || parent.Id == project.ServerStorage.Id || parent.Type == "Model" || SceneNodeTypes.IsBasePart(parent)) return false;
            parent = FindParentNode(parent);
        }
        return true;
    }

    private DynValue CreateColorFromHsv(float hue, float saturation, float value)
    {
        hue = hue - (float)Math.Floor(hue);
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var sector = hue * 6;
        var index = (int)Math.Floor(sector);
        var fraction = sector - index;
        var low = value * (1 - saturation);
        var descending = value * (1 - fraction * saturation);
        var ascending = value * (1 - (1 - fraction) * saturation);
        return (index % 6) switch
        {
            0 => CreateColor(value, ascending, low),
            1 => CreateColor(descending, value, low),
            2 => CreateColor(low, value, ascending),
            3 => CreateColor(low, descending, value),
            4 => CreateColor(ascending, low, value),
            _ => CreateColor(value, low, descending)
        };
    }

    private static float[] RgbToHsv(float red, float green, float blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = 0f;
        if (delta > 0)
        {
            if (maximum == red) hue = (green - blue) / delta % 6;
            else if (maximum == green) hue = (blue - red) / delta + 2;
            else hue = (red - green) / delta + 4;
            hue /= 6;
            if (hue < 0) hue += 1;
        }
        var saturation = maximum <= 0 ? 0 : delta / maximum;
        return [hue, saturation, maximum];
    }

    private static int GetColorMethodArgumentIndex(Table receiver, CallbackArguments args) =>
        args.Count > 0 && args[0].Type == DataType.Table && ReferenceEquals(args[0].Table, receiver) ? 1 : 0;

    private static float[] ReadColorChannels(DynValue value)
    {
        if (value.Type != DataType.Table) return [0, 0, 0];
        return [ReadColorChannelFloat(value.Table.Get("R")), ReadColorChannelFloat(value.Table.Get("G")), ReadColorChannelFloat(value.Table.Get("B"))];
    }

    private static float ReadColorChannelFloat(DynValue value) => value.Type == DataType.Number ? (float)Math.Clamp(value.Number, 0, 1) : 0;

    private DynValue ColorValue(string value)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            return CreateColor(color.R / 255f, color.G / 255f, color.B / 255f);
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return CreateColor(78 / 255f, 157 / 255f, 1);
        }
    }

    private static string ReadColor(DynValue value, string fallback)
    {
        if (value.Type == DataType.Table)
        {
            var color = value.Table;
            var red = ReadColorChannel(color.Get("R"));
            var green = ReadColorChannel(color.Get("G"));
            var blue = ReadColorChannel(color.Get("B"));
            return $"#{red:X2}{green:X2}{blue:X2}";
        }
        if (value.CastToString() is not { } text) return fallback;
        try
        {
            _ = ColorConverter.ConvertFromString(text);
            return text;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return fallback;
        }
    }

    private static byte ReadColorChannel(DynValue value) => value.Type == DataType.Number
        ? (byte)Math.Round(Math.Clamp(value.Number, 0, 1) * 255)
        : (byte)0;

    private static float NumberArgument(CallbackArguments args, int index) =>
        index < args.Count && args[index].Type == DataType.Number ? (float)args[index].Number : 0;

    private static float[] ReadVector(DynValue value, float[] fallback)
    {
        if (value.Type != DataType.Table) return fallback;
        var table = value.Table;
        return [ReadAxis(table, "X", 1, fallback[0]), ReadAxis(table, "Y", 2, fallback[1]), ReadAxis(table, "Z", 3, fallback[2])];
    }

    private static float ReadAxis(Table table, string key, int index, float fallback)
    {
        var value = table.Get(key);
        if (value.Type != DataType.Number) value = table.Get(index);
        return value.Type == DataType.Number && double.IsFinite(value.Number) && Math.Abs(value.Number) <= float.MaxValue
            ? (float)value.Number : fallback;
    }

    private SceneNode? FindNode(string id) =>
        GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id || node.Name == id)
        ?? createdNodes.Values.FirstOrDefault(node => node.Id == id || node.Name == id);
}
