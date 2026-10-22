using BullerStudio.Models;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WattleScript.Interpreter;

namespace BullerStudio.Services;

public sealed class LuaXRuntime
{
    private readonly GameProject project;
    private readonly Action<SceneNode, string, DynValue>? propertyChanged;
    private readonly Action<string>? output;
    private readonly Dictionary<string, Table> instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneNode> createdNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> linkedChildKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> touchedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> guiActivatedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> guiTextChangedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> promptTriggeredCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> clickDetectorCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> humanoidDiedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> explosionHitCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScriptCallback>> valueChangedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BindableConnection>> bindableEventCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string NodeId, string? Attribute), List<BindableConnection>> attributeCallbacks = [];
    private readonly Dictionary<(string NodeId, string? Attribute), Table> attributeSignals = [];
    private readonly Dictionary<(string Tag, bool Added), List<BindableConnection>> tagCallbacks = [];
    private readonly Dictionary<(string Tag, bool Added), Table> tagSignals = [];
    private readonly Dictionary<string, List<BindableConnection>> runServiceCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptCallback> bindableFunctionCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<Coroutine, Stack<BindableInvocationScope>> bindableInvocationScopes = new();
    private readonly Dictionary<string, bool> humanoidWasAlive = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeContacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> detonatedExplosions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> activeExplosions = new(StringComparer.Ordinal);
    private readonly List<DebrisItem> debrisItems = [];
    private readonly List<ScriptExecution> activeScripts = [];
    private readonly List<ScheduledTask> scheduledTasks = [];
    private readonly Queue<string> pendingScriptStarts = new();
    private readonly HashSet<string> queuedScriptIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> startedScriptIds = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Table, LuaXTypeTag> runtimeTypeTags = new();
    private readonly bool isStudio;
    private double simulationTime;
    private Script script = null!;
    private DynValue bindableInvokeFactory = DynValue.Nil;
    private DynValue contextualRequireFactory = DynValue.Nil;
    private GameScript? currentGameScript;
    private Table vector3Metatable = null!;

    private sealed record LuaXTypeTag(string Name);
    private sealed record ScriptCallback(GameScript GameScript, DynValue Function, string Name);
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
    private sealed record ScriptExecution(GameScript GameScript, Coroutine Coroutine, bool ReportFinished, string? CallbackName = null);
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
    private static readonly HashSet<string> CreatableClasses = new(StringComparer.Ordinal)
    {
        "Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "Model", "Folder", "Configuration", "BindableEvent", "BindableFunction", "ModuleScript", "Humanoid",
        "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector3Value", "Color3Value", "ObjectValue",
        "PointLight", "SpotLight", "SurfaceLight", "Decal", "Texture", "Sound", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "Trail", "VectorForce", "LinearVelocity", "ProximityPrompt", "ClickDetector", "Highlight", "ForceField", "Explosion", "WeldConstraint", "Attachment", "Beam",
        "Sky", "ScreenGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox",
        "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UISizeConstraint", "UITextSizeConstraint"
    };

    public LuaXRuntime(GameProject project, Action<SceneNode, string, DynValue>? propertyChanged = null, Action<string>? output = null, bool isStudio = true)
    {
        this.project = project;
        this.propertyChanged = propertyChanged;
        this.output = output;
        this.isStudio = isStudio;
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
        bindableFunctionCallbacks.Clear();
        explosionHitCallbacks.Clear();
        detonatedExplosions.Clear();
        activeExplosions.Clear();
        debrisItems.Clear();
        bindableInvocationScopes.Clear();
        activeScripts.Clear();
        scheduledTasks.Clear();
        pendingScriptStarts.Clear();
        queuedScriptIds.Clear();
        startedScriptIds.Clear();
        simulationTime = 0;
        foreach (var gameScript in project.Scripts.ToArray())
        {
            var node = FindScriptNode(gameScript.Id);
            if (node is not null && (node.Type != "Script" || !node.Enabled || !IsScriptAttachedForExecution(node))) continue;
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
        startedScriptIds.Add(scriptId);
        StartScript(gameScript, true);
        StartQueuedScripts();
    }

    public void Update(float elapsedSeconds = .05f)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        UpdateBeforePhysics(elapsed);
        FireFrameSignal("PostSimulation", DynValue.NewNumber(elapsed));
        RunDeferredTasks();
        ResumePendingScripts(elapsed);
        RunDelayedTasks(elapsed);
        StartQueuedScripts();
        AdvanceExplosions(elapsed);
        AdvanceDebris(elapsed);
        FireEndOfFrameSignals(elapsed);
    }

    public void UpdateBeforePhysics(float elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        FireFrameSignal("Stepped", DynValue.NewNumber(simulationTime), DynValue.NewNumber(elapsed));
        FireFrameSignal("PreSimulation", DynValue.NewNumber(elapsed));
        RunDeferredTasks();
        simulationTime += elapsed;
    }

    private SceneNode? FindScriptNode(string id) =>
        GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id)
        ?? (createdNodes.TryGetValue(id, out var created) ? created : null);

    private bool IsScriptAttachedForExecution(SceneNode node)
    {
        var current = node;
        while (FindParentNode(current) is { } parent)
        {
            if (ReferenceEquals(parent, project.Workspace) || ReferenceEquals(parent, project.ServerScriptService) || ReferenceEquals(parent, project.Gui)) return true;
            if (ReferenceEquals(parent, project.ReplicatedStorage) || ReferenceEquals(parent, project.ServerStorage) || ReferenceEquals(parent, project.Players)) return false;
            current = parent;
        }
        return false;
    }

    private void QueueRunnableScripts(SceneNode source)
    {
        foreach (var node in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
            if (node.Type == "Script" && node.Enabled && IsScriptAttachedForExecution(node) && !startedScriptIds.Contains(node.Id)
                && project.Scripts.Any(item => item.Id == node.Id) && queuedScriptIds.Add(node.Id))
                pendingScriptStarts.Enqueue(node.Id);
    }

    private void StartQueuedScripts()
    {
        for (var count = 0; count < 64 && pendingScriptStarts.Count > 0; count++)
        {
            var id = pendingScriptStarts.Dequeue();
            queuedScriptIds.Remove(id);
            if (startedScriptIds.Contains(id) || FindScriptNode(id) is not { Type: "Script", Enabled: true } node || !IsScriptAttachedForExecution(node)) continue;
            var gameScript = project.Scripts.FirstOrDefault(item => item.Id == id);
            if (gameScript is null) continue;
            startedScriptIds.Add(id);
            StartScript(gameScript, false);
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
        var nodes = Parts.Where(node => node.CanTouch).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var touchingNow = new HashSet<string>(StringComparer.Ordinal);
        var parts = nodes.Values.ToArray();
        for (var first = 0; first < parts.Length; first++)
        for (var second = first + 1; second < parts.Length; second++)
        {
            if (!CollisionGeometry.Intersects(parts[first], parts[second])) continue;
            var key = string.CompareOrdinal(parts[first].Id, parts[second].Id) < 0 ? $"{parts[first].Id}:{parts[second].Id}" : $"{parts[second].Id}:{parts[first].Id}";
            touchingNow.Add(key);
            if (activeContacts.Contains(key)) continue;
            DispatchTouch(parts[first], parts[second]);
            DispatchTouch(parts[second], parts[first]);
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
            if (node.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui")) script.Globals.Set(node.Name, DynValue.NewTable(instance));
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
        var game = new Table(script);
        runtimeTypeTags.Add(game, new LuaXTypeTag("DataModel"));
        var workspace = instances[project.Workspace.Id];
        var players = instances[project.Players.Id];
        var lighting = instances[project.Lighting.Id];
        var replicatedStorage = instances[project.ReplicatedStorage.Id];
        var serverScriptService = instances[project.ServerScriptService.Id];
        var serverStorage = instances[project.ServerStorage.Id];
        var gui = instances[project.Gui.Id];
        var debris = new Table(script);
        runtimeTypeTags.Add(debris, new LuaXTypeTag("Debris"));
        debris.Set("Name", DynValue.NewString("Debris"));
        debris.Set("ClassName", DynValue.NewString("Debris"));
        debris.Set("AddItem", DynValue.NewCallback((_, args) => AddDebrisItem(debris, args)));
        var collectionService = CreateCollectionService();
        var runService = CreateRunService();
        game.Set("Workspace", DynValue.NewTable(workspace));
        game.Set("Players", DynValue.NewTable(players));
        game.Set("Lighting", DynValue.NewTable(lighting));
        game.Set("ReplicatedStorage", DynValue.NewTable(replicatedStorage));
        game.Set("ServerScriptService", DynValue.NewTable(serverScriptService));
        game.Set("ServerStorage", DynValue.NewTable(serverStorage));
        game.Set("Gui", DynValue.NewTable(gui));
        game.Set("Debris", DynValue.NewTable(debris));
        game.Set("CollectionService", DynValue.NewTable(collectionService));
        game.Set("RunService", DynValue.NewTable(runService));
        game.Set("GetService", DynValue.NewCallback((_, args) =>
        {
            var serviceName = args.Count > 1 ? args[1].CastToString() : args.Count == 1 ? args[0].CastToString() : null;
            return serviceName switch
            {
                "Workspace" => DynValue.NewTable(workspace),
                "Players" => DynValue.NewTable(players),
                "Lighting" => DynValue.NewTable(lighting),
                "ReplicatedStorage" => DynValue.NewTable(replicatedStorage),
                "ServerScriptService" => DynValue.NewTable(serverScriptService),
                "ServerStorage" => DynValue.NewTable(serverStorage),
                "Gui" => DynValue.NewTable(gui),
                "Debris" => DynValue.NewTable(debris),
                "CollectionService" => DynValue.NewTable(collectionService),
                "RunService" => DynValue.NewTable(runService),
                _ => DynValue.Nil
            };
        }));
        script.Globals.Set("game", DynValue.NewTable(game));
        script.Globals.Set("workspace", DynValue.NewTable(workspace));
        script.Globals.Set("Players", DynValue.NewTable(players));
        script.Globals.Set("Lighting", DynValue.NewTable(lighting));
        script.Globals.Set("ReplicatedStorage", DynValue.NewTable(replicatedStorage));
        script.Globals.Set("ServerScriptService", DynValue.NewTable(serverScriptService));
        script.Globals.Set("ServerStorage", DynValue.NewTable(serverStorage));
        script.Globals.Set("Gui", DynValue.NewTable(gui));
        var instanceLibrary = new Table(script);
        instanceLibrary.Set("new", DynValue.NewCallback((_, args) => NewInstance(args)));
        script.Globals.Set("Instance", DynValue.NewTable(instanceLibrary));
        var cameraModeEnum = new Table(script);
        cameraModeEnum.Set("Custom", DynValue.NewString("Custom"));
        cameraModeEnum.Set("Subject", DynValue.NewString("Subject"));
        var layoutDirectionEnum = new Table(script);
        layoutDirectionEnum.Set("Vertical", DynValue.NewString("Vertical"));
        layoutDirectionEnum.Set("Horizontal", DynValue.NewString("Horizontal"));
        var enumTable = new Table(script);
        enumTable.Set("CameraMode", DynValue.NewTable(cameraModeEnum));
        enumTable.Set("LayoutDirection", DynValue.NewTable(layoutDirectionEnum));
        enumTable.Set("FillDirection", DynValue.NewTable(layoutDirectionEnum));
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
        enumTable.Set("ActuatorRelativeTo", DynValue.NewTable(actuatorRelativeToEnum));
        var velocityConstraintModeEnum = new Table(script);
        foreach (var mode in new[] { "Vector", "Line", "Plane" }) velocityConstraintModeEnum.Set(mode, DynValue.NewString(mode));
        enumTable.Set("VelocityConstraintMode", DynValue.NewTable(velocityConstraintModeEnum));
        script.Globals.Set("Enum", DynValue.NewTable(enumTable));
        var raycastParams = new Table(script);
        raycastParams.Set("new", DynValue.NewCallback((_, _) =>
        {
            var parameters = new Table(script);
            runtimeTypeTags.Add(parameters, new LuaXTypeTag("RaycastParams"));
            parameters.Set("FilterDescendantsInstances", DynValue.NewTable(new Table(script)));
            parameters.Set("FilterType", DynValue.NewString("Exclude"));
            return DynValue.NewTable(parameters);
        }));
        script.Globals.Set("RaycastParams", DynValue.NewTable(raycastParams));
        var vector3 = new Table(script);
        vector3.Set("new", DynValue.NewCallback((_, args) => VectorValue([NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2)])));
        vector3.Set("zero", VectorValue([0, 0, 0]));
        vector3.Set("one", VectorValue([1, 1, 1]));
        script.Globals.Set("Vector3", DynValue.NewTable(vector3));
        var vector2 = new Table(script);
        vector2.Set("new", DynValue.NewCallback((_, args) => Vector2Value([NumberArgument(args, 0), NumberArgument(args, 1)])));
        script.Globals.Set("Vector2", DynValue.NewTable(vector2));
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
        "Lighting" => project.Lighting,
        "ReplicatedStorage" => project.ReplicatedStorage,
        "ServerScriptService" => project.ServerScriptService,
        "ServerStorage" => project.ServerStorage,
        "Gui" => project.Gui,
        _ => null
    };

    private bool IsServiceRoot(SceneNode node) =>
        ReferenceEquals(node, project.Workspace) || ReferenceEquals(node, project.Players)
        || ReferenceEquals(node, project.Lighting) || ReferenceEquals(node, project.ReplicatedStorage)
        || ReferenceEquals(node, project.ServerScriptService) || ReferenceEquals(node, project.ServerStorage)
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
        return node is null || node.Type == "Script" && node.Enabled && IsScriptAttachedForExecution(node);
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

    private float[] CombineVectors(CallbackArguments args, Func<float, float, float> operation)
    {
        var left = ReadVector(args[0], [0, 0, 0]);
        var right = ReadVector(args[1], [0, 0, 0]);
        return [operation(left[0], right[0]), operation(left[1], right[1]), operation(left[2], right[2])];
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
        instance.Set("Position", IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Type == "Model" ? SceneModelService.GetPivot(node) : node.Position));
        instance.Set("Rotation", VectorValue(node.Rotation));
        instance.Set("Size", node.Type == "Sparkles" ? DynValue.Nil : SceneNodeTypes.IsSizedParticleEffect(node) ? DynValue.NewNumber(node.EffectSize) : IsGuiObject(node) ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size));
        instance.Set("Color", ColorValue(node.Color));
        instance.Set("SparkleColor", ColorValue(node.Color));
        instance.Set("SecondaryColor", ColorValue(node.SecondaryColor));
        instance.Set("FillColor", ColorValue(node.FillColor));
        instance.Set("OutlineColor", ColorValue(node.OutlineColor));
        instance.Set("StartColor", ColorValue(node.StartColor));
        instance.Set("EndColor", ColorValue(node.EndColor));
        instance.Set("PaddingLeft", CreateUDim(node.PaddingLeft[0], node.PaddingLeft[1]));
        instance.Set("PaddingTop", CreateUDim(node.PaddingTop[0], node.PaddingTop[1]));
        instance.Set("PaddingRight", CreateUDim(node.PaddingRight[0], node.PaddingRight[1]));
        instance.Set("PaddingBottom", CreateUDim(node.PaddingBottom[0], node.PaddingBottom[1]));
        instance.Set("Scale", DynValue.NewNumber(node.Scale));
        instance.Set("MinSize", Vector2Value(node.MinSize));
        instance.Set("MaxSize", Vector2Value(node.MaxSize));
        instance.Set("CellSize", CreateUDim2Value(node.CellSizeScale, node.CellSize));
        instance.Set("CellPadding", CreateUDim2Value(node.CellPaddingScale, node.CellPadding));
        instance.Set("FillDirection", DynValue.NewString(node.FillDirection));
        instance.Set("FillDirectionMaxCells", DynValue.NewNumber(node.FillDirectionMaxCells));
        instance.Set("FillTransparency", DynValue.NewNumber(node.FillTransparency));
        instance.Set("OutlineTransparency", DynValue.NewNumber(node.OutlineTransparency));
        instance.Set("Shape", DynValue.NewString(node.Shape));
        instance.Set("Face", DynValue.NewString(node.Face));
        instance.Set("StudsPerTileU", DynValue.NewNumber(node.StudsPerTileU));
        instance.Set("StudsPerTileV", DynValue.NewNumber(node.StudsPerTileV));
        instance.Set("OffsetStudsU", DynValue.NewNumber(node.OffsetStudsU));
        instance.Set("OffsetStudsV", DynValue.NewNumber(node.OffsetStudsV));
        instance.Set("Material", DynValue.NewString(node.Material));
        instance.Set("Transparency", DynValue.NewNumber(node.Transparency));
        instance.Set("BackgroundTransparency", DynValue.NewNumber(node.BackgroundTransparency));
        instance.Set("TextTransparency", DynValue.NewNumber(node.TextTransparency));
        instance.Set("ImageTransparency", DynValue.NewNumber(node.ImageTransparency));
        instance.Set("Enabled", DynValue.NewBoolean(node.Enabled));
        instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
        instance.Set("Range", DynValue.NewNumber(node.Range));
        instance.Set("Heat", DynValue.NewNumber(node.Heat));
        instance.Set("RiseVelocity", DynValue.NewNumber(node.RiseVelocity));
        instance.Set("Opacity", DynValue.NewNumber(node.Opacity));
        instance.Set("TimeScale", DynValue.NewNumber(node.TimeScale));
        instance.Set("BlastRadius", DynValue.NewNumber(node.BlastRadius));
        instance.Set("BlastPressure", DynValue.NewNumber(node.BlastPressure));
        instance.Set("DestroyJointRadiusPercent", DynValue.NewNumber(node.DestroyJointRadiusPercent));
        instance.Set("Angle", DynValue.NewNumber(node.Angle));
        instance.Set("ActionText", DynValue.NewString(node.ActionText));
        instance.Set("ObjectText", DynValue.NewString(node.ObjectText));
        instance.Set("MaxActivationDistance", DynValue.NewNumber(node.MaxActivationDistance));
        instance.Set("Volume", DynValue.NewNumber(node.Volume));
        instance.Set("Looped", DynValue.NewBoolean(node.Looped));
        instance.Set("Playing", DynValue.NewBoolean(node.Playing));
        instance.Set("RollOffMinDistance", DynValue.NewNumber(node.RollOffMinDistance));
        instance.Set("RollOffMaxDistance", DynValue.NewNumber(node.RollOffMaxDistance));
        instance.Set("Part0", DynValue.NewString(node.Part0));
        instance.Set("Part1", DynValue.NewString(node.Part1));
        instance.Set("Attachment0", DynValue.NewString(node.Attachment0));
        instance.Set("Attachment1", DynValue.NewString(node.Attachment1));
        instance.Set("Width0", DynValue.NewNumber(node.Width0));
        instance.Set("Width1", DynValue.NewNumber(node.Width1));
        instance.Set("Texture", DynValue.NewString(node.Texture));
        instance.Set("Text", DynValue.NewString(node.Text));
        instance.Set("TextColor", DynValue.NewString(node.TextColor));
        instance.Set("Visible", DynValue.NewBoolean(node.Visible));
        instance.Set("ZIndex", DynValue.NewNumber(node.ZIndex));
        instance.Set("LayoutOrder", DynValue.NewNumber(node.LayoutOrder));
        instance.Set("CornerRadius", DynValue.NewNumber(node.CornerRadius));
        instance.Set("StrokeColor", DynValue.NewString(node.StrokeColor));
        instance.Set("StrokeThickness", DynValue.NewNumber(node.StrokeThickness));
        instance.Set("TextSize", DynValue.NewNumber(node.TextSize));
        instance.Set("TextScaled", DynValue.NewBoolean(node.TextScaled));
        instance.Set("MinTextSize", DynValue.NewNumber(node.MinTextSize));
        instance.Set("MaxTextSize", DynValue.NewNumber(node.MaxTextSize));
        instance.Set("LayoutSpacing", DynValue.NewNumber(node.LayoutSpacing));
        instance.Set("TableColumns", DynValue.NewNumber(node.TableColumns));
        instance.Set("AspectRatio", DynValue.NewNumber(node.AspectRatio));
        instance.Set("LayoutDirection", DynValue.NewString(node.LayoutDirection));
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
        instance.Set("Anchored", DynValue.NewBoolean(node.Anchored));
        instance.Set("CanCollide", DynValue.NewBoolean(node.CanCollide));
        instance.Set("CanTouch", DynValue.NewBoolean(node.CanTouch));
        instance.Set("CanQuery", DynValue.NewBoolean(node.CanQuery));
        instance.Set("Velocity", VectorValue(node.Velocity));
        instance.Set("AssemblyLinearVelocity", VectorValue(node.Velocity));
        instance.Set("CastShadow", DynValue.NewBoolean(node.CastShadow));
        instance.Set("Gravity", DynValue.NewNumber(node.Gravity));
        instance.Set("Ambient", ColorValue(node.Ambient));
        instance.Set("ClockTime", DynValue.NewNumber(node.ClockTime));
        instance.Set("GlobalShadows", DynValue.NewBoolean(project.Settings.Shadows));
        instance.Set("AutoManageCharacter", DynValue.NewBoolean(node.AutoManageCharacter));
        instance.Set("Character", GetCharacterValue(node));
        instance.Set("Occupant", GetOccupantValue(node));
        instance.Set("Health", DynValue.NewNumber(node.Health));
        instance.Set("MaxHealth", DynValue.NewNumber(node.MaxHealth));
        instance.Set("WalkSpeed", DynValue.NewNumber(node.WalkSpeed));
        instance.Set("JumpPower", DynValue.NewNumber(node.JumpPower));
        if (SceneNodeTypes.IsValueObject(node)) instance.Set("Value", GetValueObjectValue(node));
        instance.Set("Asset", DynValue.NewString(node.Asset));
        instance.Set("CameraMode", DynValue.NewString(node.CameraMode));
        instance.Set("Mode", DynValue.NewString(node.CameraMode));
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
        if (node.Type == "VectorForce")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
            instance.Set("Force", VectorValue(node.Force));
            instance.Set("RelativeTo", DynValue.NewString(node.RelativeTo));
        }
        if (node.Type == "LinearVelocity")
        {
            instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
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
        instance.Set("WaitForChild", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index && args[index].CastToString() is { } name ? FindChild(instance, name) : DynValue.Nil;
        }));
        instance.Set("waitforchild", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            return args.Count > index && args[index].CastToString() is { } name ? FindChild(instance, name) : DynValue.Nil;
        }));
        instance.Set("FindFirstChild", DynValue.NewCallback((_, args) => FindFirstChild(node, args)));
        instance.Set("GetChildren", DynValue.NewCallback((_, _) => GetChildren(node)));
        instance.Set("IsA", DynValue.NewCallback((_, args) =>
        {
            var index = GetInstanceMethodArgumentIndex(node, args);
            var className = args.Count > index ? args[index].CastToString() : null;
            return DynValue.NewBoolean(className is not null && IsA(node, className));
        }));
        instance.Set("Destroy", DynValue.NewCallback((_, _) => DestroyInstance(node)));
        instance.Set("Clone", DynValue.NewCallback((_, _) => CloneInstance(node)));
        InstallAttributeApi(node, instance);
        InstallTagApi(node, instance);
        if (node.Type == "Workspace") instance.Set("Raycast", DynValue.NewCallback((_, args) => Raycast(node, args)));
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
        if (node.Type == "BindableFunction")
        {
            var getCallback = DynValue.NewCallback((_, _) => bindableFunctionCallbacks.TryGetValue(node.Id, out var callback) && IsTaskOwnerActive(callback.GameScript) ? callback.Function : DynValue.Nil);
            var beginCallback = DynValue.NewCallback((_, args) => BeginBindableInvocation(node, args));
            var endCallback = DynValue.NewCallback((_, args) => EndBindableInvocation(args));
            instance.Set("Invoke", script.Call(bindableInvokeFactory, getCallback, beginCallback, endCallback));
        }
        if (node.Type == "Sound")
        {
            instance.Set("Play", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(true))));
            instance.Set("Stop", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(false))));
        }
        if (node.Type == "Humanoid")
        {
            instance.Set("TakeDamage", DynValue.NewCallback((_, args) => TakeHumanoidDamage(node, args)));
            var died = new Table(script);
            died.Set("Connect", DynValue.NewCallback((_, args) =>
            {
                var index = args.Count > 0 && args[0].Type == DataType.Table ? 1 : 0;
                if (currentGameScript is null || args.Count <= index || args[index].Type != DataType.Function) return DynValue.Nil;
                if (!humanoidDiedCallbacks.TryGetValue(node.Id, out var callbacks)) humanoidDiedCallbacks[node.Id] = callbacks = [];
                callbacks.Add(new ScriptCallback(currentGameScript, args[index], "Died"));
                return DynValue.Nil;
            }));
            instance.Set("Died", DynValue.NewTable(died));
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
        if (node.Type is "TextButton" or "ImageButton")
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
            instance.Set("MouseButton1Click", DynValue.NewTable(activated));
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
            foreach (var child in node.Children)
            {
                if (IsReservedBindableMember(node, child.Name)) continue;
                if (!instances.TryGetValue(child.Id, out var childInstance)) continue;
                if (string.Equals(childInstance.Get("Name").CastToString(), name, StringComparison.Ordinal))
                    return DynValue.NewTable(childInstance);
            }
            return DynValue.Nil;
        }));
        if (node.Type == "BindableFunction")
            instanceMetatable.Set("__newindex", DynValue.NewCallback((_, args) =>
            {
                if (args.Count < 3) return DynValue.Nil;
                if (args[1].Type == DataType.String && args[1].String == "OnInvoke")
                {
                    SetBindableFunctionCallback(node, args[2]);
                    return DynValue.Nil;
                }
                instance.Set(args[1], args[2]);
                return DynValue.Nil;
            }));
        instance.MetaTable = instanceMetatable;
        return instance;
    }

    private static bool IsReservedBindableMember(SceneNode node, string name) =>
        node.Type == "BindableFunction" && name is ("OnInvoke" or "Invoke")
        || node.Type == "BindableEvent" && name is ("Event" or "Fire");

    private void SetBindableFunctionCallback(SceneNode node, DynValue value)
    {
        if (value.Type == DataType.Function && currentGameScript is { } owner)
            bindableFunctionCallbacks[node.Id] = new ScriptCallback(owner, value, "OnInvoke");
        else if (value.Type == DataType.Nil) bindableFunctionCallbacks.Remove(node.Id);
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

    public void UpdateHumanoidDeaths()
    {
        foreach (var humanoid in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "Humanoid"))
        {
            var alive = humanoid.Health > 0;
            var wasAlive = humanoidWasAlive.GetValueOrDefault(humanoid.Id, alive);
            humanoidWasAlive[humanoid.Id] = alive;
            if (alive || !wasAlive) continue;
            SynchronizeExternalProperty(humanoid, "Health");
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

    private bool HasProtectingForceField(SceneNode humanoid)
    {
        var parent = FindParentNode(humanoid);
        if (parent is null) return false;
        var rootPart = parent.Children.FirstOrDefault(child => child.Name == "HumanoidRootPart" && SceneNodeTypes.IsBasePart(child));
        if (rootPart?.Children.Any(child => child.Type == "ForceField") == true) return true;
        for (var ancestor = parent; ancestor is not null && !ReferenceEquals(ancestor, project.Workspace); ancestor = FindParentNode(ancestor))
            if (ancestor.Children.Any(child => child.Type == "ForceField")) return true;
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
            foreach (var weld in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "WeldConstraint" && node.Enabled))
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

        var filterType = "Exclude";
        var filterRoots = new List<SceneNode>();
        if (args.Count > index + 2 && args[index + 2].Type != DataType.Nil)
        {
            if (!IsTypedValue(args[index + 2], "RaycastParams")) return DynValue.Nil;
            var parameters = args[index + 2].Table;
            var requestedType = parameters.Get("FilterType").CastToString();
            if (requestedType is not ("Exclude" or "Include")) return DynValue.Nil;
            filterType = requestedType;
            var descendants = parameters.Get("FilterDescendantsInstances");
            if (descendants.Type != DataType.Table) return DynValue.Nil;
            for (var item = 1; item <= descendants.Table.Length; item++)
                if (ReadNodeArgument(descendants.Table.Get(item)) is { } root) filterRoots.Add(root);
        }

        SynchronizeInstances();
        var filteredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in filterRoots)
        {
            filteredIds.Add(root.Id);
            foreach (var descendant in GameProjectStore.Flatten(root.Children)) filteredIds.Add(descendant.Id);
        }
        SceneNode? closestPart = null;
        CollisionGeometry.RayHit? closestHit = null;
        foreach (var part in Parts)
        {
            if (!part.CanQuery) continue;
            var matchesFilter = filteredIds.Contains(part.Id);
            if (filterType == "Include" ? !matchesFilter : matchesFilter) continue;
            var hit = CollisionGeometry.Raycast(part, origin, direction);
            if (hit is null || closestHit is not null && hit.Value.Distance >= closestHit.Value.Distance) continue;
            closestPart = part;
            closestHit = hit;
        }
        if (closestPart is null || closestHit is null || !instances.TryGetValue(closestPart.Id, out var hitInstance)) return DynValue.Nil;
        var result = new Table(script);
        runtimeTypeTags.Add(result, new LuaXTypeTag("RaycastResult"));
        result.Set("Instance", DynValue.NewTable(hitInstance));
        result.Set("Position", VectorValue([closestHit.Value.Position.X, closestHit.Value.Position.Y, closestHit.Value.Position.Z]));
        result.Set("Normal", VectorValue([closestHit.Value.Normal.X, closestHit.Value.Normal.Y, closestHit.Value.Normal.Z]));
        result.Set("Distance", DynValue.NewNumber(closestHit.Value.Distance));
        result.Set("Material", DynValue.NewString(closestPart.Material));
        result.ReadOnly = true;
        return DynValue.NewTable(result);
    }

    private DynValue NewInstance(CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.String || !CreatableClasses.Contains(args[0].String)) return DynValue.Nil;
        var className = args[0].String;
        var node = new SceneNode { Type = className, Name = className };
        if (SceneNodeTypes.IsBasePart(node)) node.Position = [0, 1, 0];
        if (className == "SpawnLocation") { node.Size = [6, 1, 6]; node.Position = [0, .5f, 0]; node.Color = "#72BF9D"; node.Material = "Metal"; }
        if (className == "Seat") { node.Size = [2, 1, 2]; node.Position = [0, .5f, 0]; node.Color = "#596B89"; node.Material = "Metal"; }
        if (className == "Fire") { node.Color = "#FF6A00"; node.EffectSize = 5; }
        if (className == "Smoke") node.Color = "#B0B0B0";
        if (className == "Sparkles") node.Color = "#FFFFFF";
        if (className is "ParticleEmitter" or "Trail") node.Color = "#FFFFFF";
        if (className == "VectorForce") node.RelativeTo = "Attachment0";
        if (className is "VectorForce" or "LinearVelocity" && args.Count > 1 && ReadNodeArgument(args[1]) is { } forceParent)
            node.Attachment0 = forceParent.Type == "Attachment" ? forceParent.Id
                : IsBasePart(forceParent) ? forceParent.Children.FirstOrDefault(child => child.Type == "Attachment")?.Id ?? string.Empty : string.Empty;
        createdNodes[node.Id] = node;
        var instance = CreateInstance(node);
        instances[node.Id] = instance;
        if (className == "ModuleScript")
            project.Scripts.Add(new GameScript { Id = node.Id, Name = node.Name, Source = "local module = {}\nreturn module" });
        if (className == "Humanoid") humanoidWasAlive[node.Id] = true;
        if (args.Count > 1 && TrySetParent(node, args[1]))
            propertyChanged?.Invoke(node, "Parent", DynValue.NewTable(instances[FindParentNode(node)!.Id]));
        return DynValue.NewTable(instance);
    }

    private DynValue CloneInstance(SceneNode source)
    {
        if (source.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera" || !instances.ContainsKey(source.Id)) return DynValue.Nil;
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
            if (node.Type == "WeldConstraint")
            {
                instances[node.Id].Set("Part0", GetPartReferenceValue(node.Part0));
                instances[node.Id].Set("Part1", GetPartReferenceValue(node.Part1));
            }
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LinearVelocity")
            {
                instances[node.Id].Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                if (node.Type is "Beam" or "Trail") instances[node.Id].Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            }
        }
        foreach (var node in clonedNodes.Where(node => node.Type == "ObjectValue"))
            instances[node.Id].Set("Value", GetValueObjectValue(node));
        foreach (var original in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
        {
            if (original.Type is not ("Script" or "ModuleScript") || !renewedIds.TryGetValue(original.Id, out var clonedId)) continue;
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
            || node.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera") return DynValue.Nil;
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

    private DynValue DestroyInstance(SceneNode node)
    {
        if (node.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera" || !instances.ContainsKey(node.Id)) return DynValue.Nil;
        var wasAttached = IsAttached(node);
        var oldParent = FindParentNode(node);
        var sourceCollection = FindSourceCollection(node);
        sourceCollection?.Remove(node);
        if (wasAttached) FireSubtreeTagChanges(node, false);
        if (oldParent is not null) RefreshParentChildren(oldParent);
        if (instances.TryGetValue(node.Id, out var removedInstance)) removedInstance.Set("Parent", DynValue.Nil);
        var removedNodes = new[] { node }.Concat(GameProjectStore.Flatten(node.Children)).ToArray();
        var removedScriptIds = removedNodes.Where(removed => removed.Type == "Script").Select(removed => removed.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in removedNodes)
        {
            instances.Remove(removed.Id);
            createdNodes.Remove(removed.Id);
            linkedChildKeys.Remove(removed.Id);
            humanoidWasAlive.Remove(removed.Id);
            touchedCallbacks.Remove(removed.Id);
            guiActivatedCallbacks.Remove(removed.Id);
            guiTextChangedCallbacks.Remove(removed.Id);
            promptTriggeredCallbacks.Remove(removed.Id);
            clickDetectorCallbacks.Remove(removed.Id);
            humanoidDiedCallbacks.Remove(removed.Id);
            explosionHitCallbacks.Remove(removed.Id);
            activeExplosions.Remove(removed.Id);
            valueChangedCallbacks.Remove(removed.Id);
            if (bindableEventCallbacks.Remove(removed.Id, out var bindableConnections))
                foreach (var connection in bindableConnections) connection.Disconnect();
            bindableFunctionCallbacks.Remove(removed.Id);
            queuedScriptIds.Remove(removed.Id);
            startedScriptIds.Remove(removed.Id);
            project.Scripts.RemoveAll(script => script.Id == removed.Id);
        }
        var removedIds = removedNodes.Select(removed => removed.Id).ToHashSet(StringComparer.Ordinal);
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
        StopScripts(removedScriptIds);
        propertyChanged?.Invoke(node, "Parent", DynValue.Nil);
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
        RemoveCallbacksOwnedBy(scriptIds, guiTextChangedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, promptTriggeredCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, clickDetectorCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, humanoidDiedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, valueChangedCallbacks);
        foreach (var callbacks in bindableEventCallbacks.Values)
            foreach (var connection in callbacks.Where(item => scriptIds.Contains(item.Owner.Id)).ToArray())
            {
                connection.Disconnect();
                callbacks.Remove(connection);
            }
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
            .Where(node => node.Type == "Script" && (!node.Enabled || !IsScriptAttachedForExecution(node)))
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        StopScripts(inactive);
    }

    private static void RemoveCallbacksOwnedBy(HashSet<string> scriptIds, Dictionary<string, List<ScriptCallback>> registry)
    {
        foreach (var (nodeId, callbacks) in registry.ToArray())
        {
            callbacks.RemoveAll(callback => scriptIds.Contains(callback.GameScript.Id));
            if (callbacks.Count == 0) registry.Remove(nodeId);
        }
    }

    private SceneNode[] KnownNodes()
    {
        var tree = GameProjectStore.Flatten(project);
        var treeIds = tree.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return tree.Concat(createdNodes.Values.Where(node => !treeIds.Contains(node.Id))).ToArray();
    }

    private bool TrySetParent(SceneNode source, DynValue value, bool updateLinks = true)
    {
        if (source.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera") return false;
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

    private SceneNode? FindParentNode(SceneNode target)
    {
        if (ReferenceEquals(target, project.Workspace) || ReferenceEquals(target, project.Players) || ReferenceEquals(target, project.Lighting) || ReferenceEquals(target, project.ReplicatedStorage) || ReferenceEquals(target, project.ServerScriptService) || ReferenceEquals(target, project.ServerStorage) || ReferenceEquals(target, project.Gui)) return null;
        return FindParentNode(project.Workspace, target) ?? FindParentNode(project.Players, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.Gui, target)
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
        FindParentCollection(project.Workspace, target) ?? FindParentCollection(project.Lighting, target) ?? FindParentCollection(project.ReplicatedStorage, target) ?? FindParentCollection(project.ServerScriptService, target) ?? FindParentCollection(project.ServerStorage, target) ?? FindParentCollection(project.Gui, target)
        ?? createdNodes.Values.Select(node => FindParentCollection(node, target)).FirstOrDefault(collection => collection is not null);

    private void RefreshLinksForMove(SceneNode source, SceneNode? oldParent, SceneNode? newParent)
    {
        if (instances.TryGetValue(source.Id, out var instance))
            instance.Set("Parent", newParent is not null && instances.TryGetValue(newParent.Id, out var newParentTable)
                ? DynValue.NewTable(newParentTable) : DynValue.Nil);
        if (oldParent is not null) RefreshParentChildren(oldParent);
        if (newParent is not null && !ReferenceEquals(oldParent, newParent)) RefreshParentChildren(newParent);
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
            if (node.Type == "Seat") instance.Set("Occupant", GetOccupantValue(node));
            if (FindNode(node.Subject) is { } subjectNode && IsBasePart(subjectNode) && instances.TryGetValue(subjectNode.Id, out var subject)) instance.Set("Subject", DynValue.NewTable(subject));
            else if (!IsBasePart(ReadNodeArgument(instance.Get("Subject")))) instance.Set("Subject", DynValue.Nil);
            if (node.Type == "WeldConstraint")
            {
                instance.Set("Part0", GetPartReferenceValue(node.Part0));
                instance.Set("Part1", GetPartReferenceValue(node.Part1));
            }
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LinearVelocity")
            {
                instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                if (node.Type is "Beam" or "Trail") instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
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
            "size" => "Size",
            "color" => "Color",
            "sparklecolor" => "SparkleColor",
            "secondarycolor" => "SecondaryColor",
            "fillcolor" => "FillColor",
            "outlinecolor" => "OutlineColor",
            "startcolor" => "StartColor",
            "endcolor" => "EndColor",
            "paddingleft" => "PaddingLeft",
            "paddingtop" => "PaddingTop",
            "paddingright" => "PaddingRight",
            "paddingbottom" => "PaddingBottom",
            "scale" => "Scale",
            "cellsize" => "CellSize",
            "cellpadding" => "CellPadding",
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
            "texttransparency" => "TextTransparency",
            "imagetransparency" => "ImageTransparency",
            "minsize" => "MinSize",
            "maxsize" => "MaxSize",
            "textscaled" => "TextScaled",
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
            "maxactivationdistance" => "MaxActivationDistance",
            "volume" => "Volume",
            "looped" => "Looped",
            "playing" => "Playing",
            "rolloffmindistance" => "RollOffMinDistance",
            "rolloffmaxdistance" => "RollOffMaxDistance",
            "texture" => "Texture",
            "text" => "Text",
            "textcolor" => "TextColor",
            "visible" => "Visible",
            "zindex" => "ZIndex",
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
            "occupant" => "Occupant",
            "health" => "Health",
            "maxhealth" => "MaxHealth",
            "walkspeed" => "WalkSpeed",
            "jumppower" => "JumpPower",
            "value" => "Value",
            "asset" => "Asset",
            "subject" => "Subject",
            "part0" => "Part0",
            "part1" => "Part1",
            "attachment0" => "Attachment0",
            "attachment1" => "Attachment1",
            "force" => "Force",
            "relativeto" => "RelativeTo",
            "vectorvelocity" => "VectorVelocity",
            "linedirection" => "LineDirection",
            "linevelocity" => "LineVelocity",
            "planevelocity" => "PlaneVelocity",
            "primarytangentaxis" => "PrimaryTangentAxis",
            "secondarytangentaxis" => "SecondaryTangentAxis",
            "velocityconstraintmode" => "VelocityConstraintMode",
            "forcelimitsenabled" => "ForceLimitsEnabled",
            "maxforce" => "MaxForce",
            "width0" => "Width0",
            "width1" => "Width1",
            "fieldofview" => "FieldOfView",
            "cornerradius" => "CornerRadius",
            "strokecolor" => "StrokeColor",
            "strokethickness" => "StrokeThickness",
            "textsize" => "TextSize",
            "layoutspacing" => "LayoutSpacing",
            "tablecolumns" => "TableColumns",
            "aspectratio" => "AspectRatio",
            "layoutdirection" => "LayoutDirection",
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
        if (node.Type == "VectorForce" && normalized is ("force" or "relativeto"))
        {
            if (normalized == "force")
            {
                var force = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
                if (value.Type != DataType.Table || !force.All(float.IsFinite)) return DynValue.Nil;
                node.Force = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
            }
            else
            {
                if (value.Type != DataType.String || value.String is not ("World" or "Attachment0")) return DynValue.Nil;
                node.RelativeTo = value.String;
            }
            var canonical = normalized == "force" ? "Force" : "RelativeTo";
            var current = GetPropertyValue(node, canonical);
            if (instances.TryGetValue(node.Id, out var forceInstance)) forceInstance.Set(canonical, current);
            propertyChanged?.Invoke(node, canonical, current);
            return DynValue.Nil;
        }
        if (node.Type == "LinearVelocity" && normalized is ("vectorvelocity" or "linedirection" or "linevelocity" or "planevelocity" or "primarytangentaxis" or "secondarytangentaxis" or "velocityconstraintmode" or "forcelimitsenabled" or "maxforce" or "relativeto"))
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
                case "position": SetPositionProperty(node, value); break;
                case "rotation":
                    if (node.Type == "Model")
                    {
                        var pending = CapturePendingModelDescendantWrites(node);
                        if (SceneModelService.RotateTo(node, ReadVector(value, node.Rotation))) UpdateModelDescendantInstances(node, true, pending, (child, propertyName) => propertyChanged?.Invoke(child, propertyName, GetPropertyValue(child, propertyName)));
                    }
                    else node.Rotation = ReadVector(value, node.Rotation);
                    break;
                case "size": SetSizeProperty(node, value); break;
                case "color": node.Color = ReadColor(value, node.Color); break;
                case "sparklecolor": if (node.Type != "Sparkles" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.Color = ReadColor(value, node.Color); break;
                case "secondarycolor": if (node.Type != "Fire" || !IsTypedValue(value, "Color3")) return DynValue.Nil; node.SecondaryColor = ReadColor(value, node.SecondaryColor); break;
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
                case "filldirection":
                    if (node.Type != "UIGridLayout" || value.CastToString() is not ("Horizontal" or "Vertical")) return DynValue.Nil;
                    node.FillDirection = value.CastToString()!;
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
                    if (node.Type is not ("Decal" or "Texture" or "SurfaceLight") || !SceneNodeTypes.IsFace(value.CastToString())) return DynValue.Nil;
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
                    if (!IsBasePart(node) || value.CastToString() is not { } material || material is not ("Plastic" or "Metal" or "Neon" or "Glass" or "Wood" or "Concrete")) return DynValue.Nil;
                    node.Material = material;
                    break;
                case "transparency":
                    if ((!IsBasePart(node) && node.Type is not ("Decal" or "Texture" or "Beam")) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Transparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "backgroundtransparency":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.BackgroundTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "texttransparency":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.TextTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "imagetransparency":
                    if (!IsGuiObject(node) || node.Type is not ("ImageLabel" or "ImageButton") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.ImageTransparency = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "minsize":
                    if (node.Type != "UISizeConstraint" || !TryReadVector2(value, out var minSize)) return DynValue.Nil;
                    node.MinSize = minSize.Select(component => Math.Clamp(component, 0, 1000000)).ToArray();
                    node.MaxSize = [Math.Max(node.MaxSize[0], node.MinSize[0]), Math.Max(node.MaxSize[1], node.MinSize[1])];
                    break;
                case "maxsize":
                    if (node.Type != "UISizeConstraint" || !TryReadVector2(value, out var maxSize)) return DynValue.Nil;
                    node.MaxSize = [Math.Clamp(maxSize[0], node.MinSize[0], 1000000), Math.Clamp(maxSize[1], node.MinSize[1], 1000000)];
                    break;
                case "textscaled":
                    if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || value.Type != DataType.Boolean) return DynValue.Nil;
                    node.TextScaled = value.Boolean;
                    break;
                case "mintextsize":
                    if (node.Type != "UITextSizeConstraint" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MinTextSize = (float)Math.Clamp(value.Number, 1, 1000);
                    node.MaxTextSize = Math.Max(node.MaxTextSize, node.MinTextSize);
                    break;
                case "maxtextsize":
                    if (node.Type != "UITextSizeConstraint" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxTextSize = (float)Math.Clamp(value.Number, node.MinTextSize, 1000);
                    break;
                case "enabled":
                    if (value.Type != DataType.Boolean) return DynValue.Nil;
                    var wasEnabled = node.Enabled;
                    node.Enabled = value.Boolean;
                    if (node.Type == "Script" && wasEnabled != node.Enabled)
                    {
                        if (node.Enabled) QueueRunnableScripts(node);
                        else StopScripts([node.Id]);
                    }
                    break;
                case "brightness": if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Brightness = (float)Math.Clamp(value.Number, 0, 8); break;
                case "range": if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Range = (float)Math.Clamp(value.Number, 1, 1000); break;
                case "heat": if (node.Type != "Fire" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Heat = (float)Math.Clamp(value.Number, -25, 25); break;
                case "risevelocity": if (node.Type != "Smoke" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.RiseVelocity = (float)Math.Clamp(value.Number, -25, 25); break;
                case "opacity": if (node.Type != "Smoke" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Opacity = (float)Math.Clamp(value.Number, 0, 1); break;
                case "timescale": if (!SceneNodeTypes.IsParticleEffect(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.TimeScale = (float)Math.Clamp(value.Number, 0, 1); break;
                case "blastradius": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.BlastRadius = (float)Math.Clamp(value.Number, 0, 100); break;
                case "blastpressure": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.BlastPressure = (float)Math.Clamp(value.Number, 0, 1000000); break;
                case "destroyjointradiuspercent": if (node.Type != "Explosion" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.DestroyJointRadiusPercent = (float)Math.Clamp(value.Number, 0, 1); break;
                case "angle": if (value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil; node.Angle = (float)Math.Clamp(value.Number, 1, 175); break;
                case "actiontext": if (node.Type != "ProximityPrompt") return DynValue.Nil; node.ActionText = value.CastToString() ?? node.ActionText; break;
                case "objecttext": if (node.Type != "ProximityPrompt") return DynValue.Nil; node.ObjectText = value.CastToString() ?? node.ObjectText; break;
                case "maxactivationdistance":
                    if (node.Type is not ("ProximityPrompt" or "ClickDetector") || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.MaxActivationDistance = (float)Math.Clamp(value.Number, 1, 100);
                    break;
                case "volume":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.Volume = (float)Math.Clamp(value.Number, 0, 1);
                    break;
                case "looped": if (node.Type != "Sound") return DynValue.Nil; node.Looped = value.CastToBool(); break;
                case "playing": if (node.Type != "Sound") return DynValue.Nil; node.Playing = value.CastToBool(); break;
                case "rolloffmindistance":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.RollOffMinDistance = (float)Math.Clamp(value.Number, 0, 1000);
                    node.RollOffMaxDistance = Math.Max(node.RollOffMaxDistance, node.RollOffMinDistance + .01f);
                    break;
                case "rolloffmaxdistance":
                    if (node.Type != "Sound" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    node.RollOffMaxDistance = (float)Math.Clamp(value.Number, node.RollOffMinDistance + .01f, 10000);
                    break;
                case "texture": node.Texture = value.CastToString() ?? string.Empty; break;
                case "text": node.Text = value.CastToString() ?? node.Text; break;
                case "textcolor": node.TextColor = value.CastToString() ?? node.TextColor; break;
                case "visible": node.Visible = value.CastToBool(); break;
                case "zindex":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue) return DynValue.Nil;
                    node.ZIndex = (int)value.Number;
                    break;
                case "layoutorder":
                    if (!IsGuiObject(node) || value.Type != DataType.Number || !double.IsFinite(value.Number)
                        || value.Number != Math.Truncate(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue) return DynValue.Nil;
                    node.LayoutOrder = (int)value.Number;
                    break;
                case "anchored": node.Anchored = value.CastToBool(); break;
                case "cancollide": node.CanCollide = value.CastToBool(); break;
                case "cantouch": node.CanTouch = value.CastToBool(); break;
                case "canquery": if (!IsBasePart(node) || value.Type != DataType.Boolean) return DynValue.Nil; node.CanQuery = value.Boolean; break;
                case "velocity":
                case "assemblylinearvelocity":
                    if (!IsBasePart(node) || value.Type != DataType.Table) return DynValue.Nil;
                    node.Velocity = ReadVector(value, node.Velocity);
                    break;
                case "castshadow": node.CastShadow = value.CastToBool(); break;
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
                    node.Character = assignedCharacter?.Id ?? string.Empty;
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
                case "archivable":
                    if (value.Type != DataType.Boolean) return DynValue.Nil;
                    node.Archivable = value.Boolean;
                    break;
                case "name":
                    if (node.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return DynValue.Nil;
                    SetNodeName(node, value.CastToString() ?? node.Name);
                    break;
                case "cameramode":
                case "mode":
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
                case "part0":
                case "part1":
                    if (node.Type != "WeldConstraint") return DynValue.Nil;
                    var endpoint = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && !IsBasePart(endpoint)) return DynValue.Nil;
                    var endpointId = endpoint?.Id ?? string.Empty;
                    if (endpointId.Length > 0 && endpointId == (normalized == "part0" ? node.Part1 : node.Part0)) return DynValue.Nil;
                    if (normalized == "part0") node.Part0 = endpointId;
                    else node.Part1 = endpointId;
                    break;
                case "attachment0":
                case "attachment1":
                    if (node.Type is not ("Beam" or "Trail" or "VectorForce" or "LinearVelocity") || normalized == "attachment1" && node.Type is ("VectorForce" or "LinearVelocity")) return DynValue.Nil;
                    var attachment = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && attachment?.Type != "Attachment") return DynValue.Nil;
                    var attachmentId = attachment?.Id ?? string.Empty;
                    if (node.Type is "Beam" or "Trail" && attachmentId.Length > 0 && attachmentId == (normalized == "attachment0" ? node.Attachment1 : node.Attachment0)) return DynValue.Nil;
                    if (normalized == "attachment0") node.Attachment0 = attachmentId;
                    else node.Attachment1 = attachmentId;
                    break;
                case "width0":
                case "width1":
                    if (node.Type != "Beam" || value.Type != DataType.Number || !double.IsFinite(value.Number)) return DynValue.Nil;
                    if (normalized == "width0") node.Width0 = (float)Math.Clamp(value.Number, 0, 100);
                    else node.Width1 = (float)Math.Clamp(value.Number, 0, 100);
                    break;
                case "fieldofview": node.FieldOfView = (float)Math.Clamp(value.Number, 10, 120); break;
                case "cornerradius": node.CornerRadius = (float)Math.Max(0, value.Number); break;
                case "strokecolor": node.StrokeColor = value.CastToString() ?? node.StrokeColor; break;
                case "strokethickness": node.StrokeThickness = (float)Math.Max(0, value.Number); break;
                case "textsize": node.TextSize = (float)Math.Clamp(value.Number, 1, 200); break;
                case "layoutspacing": node.LayoutSpacing = (float)Math.Max(0, value.Number); break;
                case "tablecolumns": node.TableColumns = (int)Math.Clamp(value.Number, 1, 24); break;
                case "aspectratio": node.AspectRatio = (float)Math.Max(.001, value.Number); break;
                case "layoutdirection": node.LayoutDirection = value.CastToString() == "Horizontal" ? "Horizontal" : "Vertical"; break;
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
        }
        if (normalized == "maxhealth" && instances.TryGetValue(node.Id, out var humanoidInstance))
            humanoidInstance.Set("Health", DynValue.NewNumber(node.Health));
        if (normalized == "rolloffmindistance" && instances.TryGetValue(node.Id, out var soundInstance))
            soundInstance.Set("RollOffMaxDistance", DynValue.NewNumber(node.RollOffMaxDistance));
        if (normalized == "name" && FindParentNode(node) is { } parent) RefreshParentChildren(parent);
        propertyChanged?.Invoke(node, property, value);
        return DynValue.Nil;
    }

    private void SetPositionProperty(SceneNode node, DynValue value)
    {
        if (node.Type == "Model")
        {
            var pending = CapturePendingModelDescendantWrites(node);
            if (SceneModelService.MoveTo(node, ReadVector(value, SceneModelService.GetPivot(node))))
                UpdateModelDescendantInstances(node, false, pending, (child, propertyName) => propertyChanged?.Invoke(child, propertyName, GetPropertyValue(child, propertyName)));
            return;
        }
        if (IsGuiObject(node) && TryReadUDim2(value, out var scale, out var offset))
        {
            node.PositionScale = scale;
            node.Position = [offset[0], offset[1], node.Position[2]];
            return;
        }
        node.Position = ReadVector(value, node.Position);
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
        if (SceneNodeTypes.IsSizedParticleEffect(node))
        {
            if (value.Type == DataType.Number && double.IsFinite(value.Number))
                node.EffectSize = (float)Math.Clamp(value.Number, node.Type == "Fire" ? 2 : .1f, node.Type == "Fire" ? 30 : 100);
            return;
        }
        if (IsGuiObject(node) && TryReadUDim2(value, out var scale, out var offset))
        {
            node.SizeScale = scale;
            node.Size = [offset[0], offset[1], node.Size[2]];
            return;
        }
        node.Size = ReadVector(value, node.Size);
        if (IsGuiObject(node)) node.SizeScale = [0, 0];
    }

    private void SetNodeName(SceneNode node, string name)
    {
        if (node.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return;
        node.Name = name;
        if ((node.Type is "Script" or "ModuleScript") && project.Scripts.FirstOrDefault(item => item.Id == node.Id) is { } gameScript)
            gameScript.Name = name;
    }

    private DynValue GetPartReferenceValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { } part && IsBasePart(part) && instances.TryGetValue(part.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetAttachmentReferenceValue(string id) =>
        !string.IsNullOrEmpty(id) && FindNode(id) is { Type: "Attachment" } attachment && instances.TryGetValue(attachment.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetCharacterValue(SceneNode player) =>
        player.Type == "Player" && !string.IsNullOrEmpty(player.Character)
        && FindNode(player.Character) is { Type: "Model" } model
        && instances.TryGetValue(model.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private DynValue GetOccupantValue(SceneNode seat) =>
        seat.Type == "Seat" && !string.IsNullOrEmpty(seat.Occupant)
        && FindNode(seat.Occupant) is { Type: "Humanoid" } humanoid
        && instances.TryGetValue(humanoid.Id, out var instance)
            ? DynValue.NewTable(instance) : DynValue.Nil;

    private bool TrySetLinearVelocityProperty(SceneNode node, string property, DynValue value)
    {
        if (property is "vectorvelocity" or "linedirection" or "primarytangentaxis" or "secondarytangentaxis")
        {
            var vector = ReadVector(value, [float.NaN, float.NaN, float.NaN]);
            if (value.Type != DataType.Table || !vector.All(float.IsFinite)) return false;
            vector = vector.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
            if (property == "vectorvelocity") node.VectorVelocity = vector;
            if (property == "linedirection") node.LineDirection = vector;
            if (property == "primarytangentaxis") node.PrimaryTangentAxis = vector;
            if (property == "secondarytangentaxis") node.SecondaryTangentAxis = vector;
            return true;
        }
        switch (property)
        {
            case "planevelocity" when TryReadVector2(value, out var plane):
                node.PlaneVelocity = plane.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                return true;
            case "linevelocity" when value.Type == DataType.Number && double.IsFinite(value.Number):
                node.LineVelocity = (float)Math.Clamp(value.Number, -10000, 10000);
                return true;
            case "maxforce" when value.Type == DataType.Number && double.IsFinite(value.Number):
                node.MaxForce = (float)Math.Clamp(value.Number, 0, 1000000);
                return true;
            case "forcelimitsenabled" when value.Type == DataType.Boolean:
                node.ForceLimitsEnabled = value.Boolean;
                return true;
            case "velocityconstraintmode" when value.Type == DataType.String && value.String is "Vector" or "Line" or "Plane":
                node.VelocityConstraintMode = value.String;
                return true;
            case "relativeto" when value.Type == DataType.String && value.String is "World" or "Attachment0":
                node.RelativeTo = value.String;
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
        if (node.Type == "VectorForce")
        {
            var forceValue = property.ToLowerInvariant() switch
            {
                "force" => VectorValue(node.Force),
                "relativeto" => DynValue.NewString(node.RelativeTo),
                _ => DynValue.Nil
            };
            if (forceValue.Type != DataType.Nil) return forceValue;
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
                "maxforce" => DynValue.NewNumber(node.MaxForce),
                "relativeto" => DynValue.NewString(node.RelativeTo),
                _ => DynValue.Nil
            };
            if (velocityValue.Type != DataType.Nil) return velocityValue;
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
        return property.ToLowerInvariant() switch
        {
        "id" => DynValue.NewString(node.Id),
        "classname" or "type" => DynValue.NewString(node.Type),
        "position" => IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Type == "Model" ? SceneModelService.GetPivot(node) : node.Position),
        "rotation" => VectorValue(node.Rotation),
        "size" => node.Type == "Sparkles" ? DynValue.Nil : SceneNodeTypes.IsSizedParticleEffect(node) ? DynValue.NewNumber(node.EffectSize) : IsGuiObject(node) ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size),
        "color" => ColorValue(node.Color),
        "sparklecolor" => node.Type == "Sparkles" ? ColorValue(node.Color) : DynValue.Nil,
        "secondarycolor" => node.Type == "Fire" ? ColorValue(node.SecondaryColor) : DynValue.Nil,
        "fillcolor" => ColorValue(node.FillColor),
        "outlinecolor" => ColorValue(node.OutlineColor),
        "startcolor" => node.Type == "UIGradient" ? ColorValue(node.StartColor) : DynValue.Nil,
        "endcolor" => node.Type == "UIGradient" ? ColorValue(node.EndColor) : DynValue.Nil,
        "paddingleft" => node.Type == "UIPadding" ? CreateUDim(node.PaddingLeft[0], node.PaddingLeft[1]) : DynValue.Nil,
        "paddingtop" => node.Type == "UIPadding" ? CreateUDim(node.PaddingTop[0], node.PaddingTop[1]) : DynValue.Nil,
        "paddingright" => node.Type == "UIPadding" ? CreateUDim(node.PaddingRight[0], node.PaddingRight[1]) : DynValue.Nil,
        "paddingbottom" => node.Type == "UIPadding" ? CreateUDim(node.PaddingBottom[0], node.PaddingBottom[1]) : DynValue.Nil,
        "scale" => node.Type == "UIScale" ? DynValue.NewNumber(node.Scale) : DynValue.Nil,
        "cellsize" => node.Type == "UIGridLayout" ? CreateUDim2Value(node.CellSizeScale, node.CellSize) : DynValue.Nil,
        "cellpadding" => node.Type == "UIGridLayout" ? CreateUDim2Value(node.CellPaddingScale, node.CellPadding) : DynValue.Nil,
        "filldirection" => node.Type == "UIGridLayout" ? DynValue.NewString(node.FillDirection) : DynValue.Nil,
        "filldirectionmaxcells" => node.Type == "UIGridLayout" ? DynValue.NewNumber(node.FillDirectionMaxCells) : DynValue.Nil,
        "filltransparency" => DynValue.NewNumber(node.FillTransparency),
        "outlinetransparency" => DynValue.NewNumber(node.OutlineTransparency),
        "shape" => DynValue.NewString(node.Shape),
        "face" => DynValue.NewString(node.Face),
        "studspertileu" => DynValue.NewNumber(node.StudsPerTileU),
        "studspertilev" => DynValue.NewNumber(node.StudsPerTileV),
        "offsetstudsu" => DynValue.NewNumber(node.OffsetStudsU),
        "offsetstudsv" => DynValue.NewNumber(node.OffsetStudsV),
        "material" => DynValue.NewString(node.Material),
        "transparency" => DynValue.NewNumber(node.Transparency),
        "backgroundtransparency" => IsGuiObject(node) ? DynValue.NewNumber(node.BackgroundTransparency) : DynValue.Nil,
        "texttransparency" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewNumber(node.TextTransparency) : DynValue.Nil,
        "imagetransparency" => IsGuiObject(node) && node.Type is "ImageLabel" or "ImageButton" ? DynValue.NewNumber(node.ImageTransparency) : DynValue.Nil,
        "minsize" => node.Type == "UISizeConstraint" ? Vector2Value(node.MinSize) : DynValue.Nil,
        "maxsize" => node.Type == "UISizeConstraint" ? Vector2Value(node.MaxSize) : DynValue.Nil,
        "textscaled" => node.Type is "TextLabel" or "TextButton" or "TextBox" ? DynValue.NewBoolean(node.TextScaled) : DynValue.Nil,
        "mintextsize" => node.Type == "UITextSizeConstraint" ? DynValue.NewNumber(node.MinTextSize) : DynValue.Nil,
        "maxtextsize" => node.Type == "UITextSizeConstraint" ? DynValue.NewNumber(node.MaxTextSize) : DynValue.Nil,
        "enabled" => DynValue.NewBoolean(node.Enabled),
        "brightness" => DynValue.NewNumber(node.Brightness),
        "range" => DynValue.NewNumber(node.Range),
        "heat" => node.Type == "Fire" ? DynValue.NewNumber(node.Heat) : DynValue.Nil,
        "risevelocity" => node.Type == "Smoke" ? DynValue.NewNumber(node.RiseVelocity) : DynValue.Nil,
        "opacity" => node.Type == "Smoke" ? DynValue.NewNumber(node.Opacity) : DynValue.Nil,
        "timescale" => SceneNodeTypes.IsParticleEffect(node) ? DynValue.NewNumber(node.TimeScale) : DynValue.Nil,
        "blastradius" => node.Type == "Explosion" ? DynValue.NewNumber(node.BlastRadius) : DynValue.Nil,
        "blastpressure" => node.Type == "Explosion" ? DynValue.NewNumber(node.BlastPressure) : DynValue.Nil,
        "destroyjointradiuspercent" => node.Type == "Explosion" ? DynValue.NewNumber(node.DestroyJointRadiusPercent) : DynValue.Nil,
        "angle" => DynValue.NewNumber(node.Angle),
        "actiontext" => DynValue.NewString(node.ActionText),
        "objecttext" => DynValue.NewString(node.ObjectText),
        "maxactivationdistance" => DynValue.NewNumber(node.MaxActivationDistance),
        "volume" => DynValue.NewNumber(node.Volume),
        "looped" => DynValue.NewBoolean(node.Looped),
        "playing" => DynValue.NewBoolean(node.Playing),
        "rolloffmindistance" => DynValue.NewNumber(node.RollOffMinDistance),
        "rolloffmaxdistance" => DynValue.NewNumber(node.RollOffMaxDistance),
        "texture" => DynValue.NewString(node.Texture),
        "text" => DynValue.NewString(node.Text),
        "textcolor" => DynValue.NewString(node.TextColor),
        "visible" => DynValue.NewBoolean(node.Visible),
        "zindex" => IsGuiObject(node) ? DynValue.NewNumber(node.ZIndex) : DynValue.Nil,
        "layoutorder" => IsGuiObject(node) ? DynValue.NewNumber(node.LayoutOrder) : DynValue.Nil,
        "anchored" => DynValue.NewBoolean(node.Anchored),
        "cancollide" => DynValue.NewBoolean(node.CanCollide),
        "cantouch" => DynValue.NewBoolean(node.CanTouch),
        "canquery" => IsBasePart(node) ? DynValue.NewBoolean(node.CanQuery) : DynValue.Nil,
        "velocity" or "assemblylinearvelocity" => VectorValue(node.Velocity),
        "castshadow" => DynValue.NewBoolean(node.CastShadow),
        "gravity" => DynValue.NewNumber(node.Gravity),
        "ambient" => ColorValue(node.Ambient),
        "clocktime" => DynValue.NewNumber(node.ClockTime),
        "globalshadows" => DynValue.NewBoolean(project.Settings.Shadows),
        "automanagecharacter" => DynValue.NewBoolean(node.AutoManageCharacter),
        "character" => GetCharacterValue(node),
        "occupant" => GetOccupantValue(node),
        "health" => DynValue.NewNumber(node.Health),
        "maxhealth" => DynValue.NewNumber(node.MaxHealth),
        "walkspeed" => DynValue.NewNumber(node.WalkSpeed),
        "jumppower" => DynValue.NewNumber(node.JumpPower),
        "value" => SceneNodeTypes.IsValueObject(node) ? GetValueObjectValue(node) : DynValue.Nil,
        "asset" => DynValue.NewString(node.Asset),
        "archivable" => DynValue.NewBoolean(node.Archivable),
        "name" => DynValue.NewString(node.Name),
        "children" => GetChildren(node),
        "parent" => FindParentNode(node) is { } parent && instances.TryGetValue(parent.Id, out var parentInstance) ? DynValue.NewTable(parentInstance) : DynValue.Nil,
        "cameramode" or "mode" => DynValue.NewString(node.CameraMode),
        "subject" => FindNode(node.Subject) is { } subjectNode && IsBasePart(subjectNode) && instances.TryGetValue(subjectNode.Id, out var subject) ? DynValue.NewTable(subject) : DynValue.Nil,
        "part0" => GetPartReferenceValue(node.Part0),
        "part1" => GetPartReferenceValue(node.Part1),
        "attachment0" => GetAttachmentReferenceValue(node.Attachment0),
        "attachment1" => GetAttachmentReferenceValue(node.Attachment1),
        "width0" => DynValue.NewNumber(node.Width0),
        "width1" => DynValue.NewNumber(node.Width1),
        "fieldofview" => DynValue.NewNumber(node.FieldOfView),
        "cornerradius" => DynValue.NewNumber(node.CornerRadius),
        "strokecolor" => DynValue.NewString(node.StrokeColor),
        "strokethickness" => DynValue.NewNumber(node.StrokeThickness),
        "textsize" => DynValue.NewNumber(node.TextSize),
        "layoutspacing" => DynValue.NewNumber(node.LayoutSpacing),
        "tablecolumns" => DynValue.NewNumber(node.TableColumns),
        "aspectratio" => DynValue.NewNumber(node.AspectRatio),
        "layoutdirection" => DynValue.NewString(node.LayoutDirection),
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

    private DynValue WaitForChild(CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        var parent = ReadNodeArgument(args[0]);
        if (parent is null && args[0].Type == DataType.String)
            parent = args[0].String switch { "Gui" => project.Gui, "Lighting" => project.Lighting, "ReplicatedStorage" => project.ReplicatedStorage, "ServerScriptService" => project.ServerScriptService, "ServerStorage" => project.ServerStorage, _ => project.Workspace };
        var name = args[1].CastToString();
        if (string.IsNullOrEmpty(name)) return DynValue.Nil;
        var node = (parent?.Children ?? project.Workspace.Children).FirstOrDefault(child => child.Name == name || child.Id == name);
        return node is not null && instances.TryGetValue(node.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private DynValue FindChild(Table parent, string name)
    {
        var node = FindNode(parent.Get("Id").CastToString() ?? string.Empty) ?? project.Workspace;
        var child = (node?.Children ?? project.Workspace.Children).FirstOrDefault(item => item.Name == name || item.Id == name);
        return child is not null && instances.TryGetValue(child.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
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
        "BasePart" => IsBasePart(node),
        "Light" => SceneNodeTypes.IsLight(node),
        "ValueBase" => SceneNodeTypes.IsValueObject(node),
        "UIComponent" => node.Type is "UICorner" or "UIOutline" or "UIStroke" or "UIGradient" or "UIPadding" or "UIScale" or "UIListLayout" or "UIGridLayout" or "UITableLayout" or "UIAspectRatio" or "UISizeConstraint" or "UITextSizeConstraint",
        "LuaSourceContainer" => node.Type is "Script" or "ModuleScript",
        "BaseScript" => node.Type == "Script",
        "FaceInstance" or "Decal" => node.Type is "Decal" or "Texture",
        "GuiObject" => node.Type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox",
        "GuiBase2d" => node.Type is "ScreenGui" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox",
        "LayerCollector" => node.Type == "ScreenGui",
        _ => node.Type == className
    };

    private static bool IsBasePart(SceneNode? node) => SceneNodeTypes.IsBasePart(node);

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
            if (instance.Get("Position").Type == DataType.Table)
            {
                if (node.Type == "Model")
                {
                    var position = ReadVector(instance.Get("Position"), SceneModelService.GetPivot(node));
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
                    var position = ReadVector(instance.Get("Position"), node.Position);
                    if (!node.Position.SequenceEqual(position)) { node.Position = position; MarkChanged(node, "Position"); }
                }
            }
            if (instance.Get("Rotation").Type == DataType.Table)
            {
                var rotation = ReadVector(instance.Get("Rotation"), node.Rotation);
                if (node.Type == "Model")
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
            else if (SceneNodeTypes.IsSizedParticleEffect(node) && instance.Get("Size").Type == DataType.Number && double.IsFinite(instance.Get("Size").Number))
                Assign(node, "Size", node.EffectSize, (float)Math.Clamp(instance.Get("Size").Number, node.Type == "Fire" ? 2 : .1f, node.Type == "Fire" ? 30 : 100), value => node.EffectSize = value);
            else if (instance.Get("Size").Type == DataType.Table && !SceneNodeTypes.IsSizedParticleEffect(node) && node.Type != "ParticleEmitter")
            {
                if (IsGuiObject(node) && TryReadUDim2(instance.Get("Size"), out var sizeScale, out var sizeOffset))
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
                    var size = ReadVector(instance.Get("Size"), node.Size);
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
            else Assign(node, "Color", node.Color, ReadColor(instance.Get("Color"), node.Color), value => node.Color = value);
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
            if (node.Type == "Part" && instance.Get("Shape").CastToString() is "Block" or "Ball" or "Cylinder")
                Assign(node, "Shape", node.Shape, instance.Get("Shape").String, value => node.Shape = value);
            if (node.Type is ("Decal" or "Texture" or "SurfaceLight") && SceneNodeTypes.IsFace(instance.Get("Face").CastToString()))
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
            if (IsBasePart(node) && instance.Get("Material").CastToString() is "Plastic" or "Metal" or "Neon" or "Glass" or "Wood" or "Concrete")
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
            else if ((IsBasePart(node) || node.Type is "Decal" or "Texture" or "Beam") && instance.Get("Transparency").Type == DataType.Number && double.IsFinite(instance.Get("Transparency").Number))
                Assign(node, "Transparency", node.Transparency, (float)Math.Clamp(instance.Get("Transparency").Number, 0, 1), value => node.Transparency = value);
            if (IsGuiObject(node) && instance.Get("BackgroundTransparency") is { Type: DataType.Number } backgroundTransparency && double.IsFinite(backgroundTransparency.Number))
                Assign(node, "BackgroundTransparency", node.BackgroundTransparency, (float)Math.Clamp(backgroundTransparency.Number, 0, 1), value => node.BackgroundTransparency = value);
            if (node.Type is "TextLabel" or "TextButton" or "TextBox" && instance.Get("TextTransparency") is { Type: DataType.Number } textTransparency && double.IsFinite(textTransparency.Number))
                Assign(node, "TextTransparency", node.TextTransparency, (float)Math.Clamp(textTransparency.Number, 0, 1), value => node.TextTransparency = value);
            if (IsGuiObject(node) && node.Type is "ImageLabel" or "ImageButton" && instance.Get("ImageTransparency") is { Type: DataType.Number } imageTransparency && double.IsFinite(imageTransparency.Number))
                Assign(node, "ImageTransparency", node.ImageTransparency, (float)Math.Clamp(imageTransparency.Number, 0, 1), value => node.ImageTransparency = value);
            if (instance.Get("Enabled").Type == DataType.Boolean)
            {
                var wasEnabled = node.Enabled;
                Assign(node, "Enabled", node.Enabled, instance.Get("Enabled").Boolean, value => node.Enabled = value);
                if (node.Type == "Script" && wasEnabled != node.Enabled)
                {
                    if (node.Enabled) QueueRunnableScripts(node);
                    else StopScripts([node.Id]);
                }
            }
            if (instance.Get("Brightness").Type == DataType.Number && double.IsFinite(instance.Get("Brightness").Number)) Assign(node, "Brightness", node.Brightness, (float)Math.Clamp(instance.Get("Brightness").Number, 0, 8), value => node.Brightness = value);
            if (node.Type == "Lighting")
            {
                Assign(node, "Ambient", node.Ambient, ReadColor(instance.Get("Ambient"), node.Ambient), value => node.Ambient = value);
                if (instance.Get("ClockTime").Type == DataType.Number && double.IsFinite(instance.Get("ClockTime").Number))
                    Assign(node, "ClockTime", node.ClockTime, (float)Math.Clamp(instance.Get("ClockTime").Number, 0, 24), value => node.ClockTime = value);
                if (instance.Get("GlobalShadows").Type == DataType.Boolean)
                    Assign(node, "GlobalShadows", project.Settings.Shadows, instance.Get("GlobalShadows").Boolean, value => project.Settings.Shadows = value);
            }
            if (instance.Get("Range").Type == DataType.Number && double.IsFinite(instance.Get("Range").Number)) Assign(node, "Range", node.Range, (float)Math.Clamp(instance.Get("Range").Number, 1, 1000), value => node.Range = value);
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
            if (instance.Get("Angle").Type == DataType.Number && double.IsFinite(instance.Get("Angle").Number)) Assign(node, "Angle", node.Angle, (float)Math.Clamp(instance.Get("Angle").Number, 1, 175), value => node.Angle = value);
            if (node.Type == "ProximityPrompt")
            {
                if (instance.Get("ActionText").CastToString() is { } actionText) Assign(node, "ActionText", node.ActionText, actionText, value => node.ActionText = value);
                if (instance.Get("ObjectText").CastToString() is { } objectText) Assign(node, "ObjectText", node.ObjectText, objectText, value => node.ObjectText = value);
            }
            if (node.Type is "ProximityPrompt" or "ClickDetector"
                && instance.Get("MaxActivationDistance").Type == DataType.Number && double.IsFinite(instance.Get("MaxActivationDistance").Number))
                Assign(node, "MaxActivationDistance", node.MaxActivationDistance, (float)Math.Clamp(instance.Get("MaxActivationDistance").Number, 1, 100), value => node.MaxActivationDistance = value);
            if (node.Type == "Sound")
            {
                if (instance.Get("Volume").Type == DataType.Number && double.IsFinite(instance.Get("Volume").Number))
                    Assign(node, "Volume", node.Volume, (float)Math.Clamp(instance.Get("Volume").Number, 0, 1), value => node.Volume = value);
                if (instance.Get("Looped").Type == DataType.Boolean)
                    Assign(node, "Looped", node.Looped, instance.Get("Looped").Boolean, value => node.Looped = value);
                if (instance.Get("Playing").Type == DataType.Boolean)
                    Assign(node, "Playing", node.Playing, instance.Get("Playing").Boolean, value => node.Playing = value);
                if (instance.Get("RollOffMinDistance").Type == DataType.Number && double.IsFinite(instance.Get("RollOffMinDistance").Number))
                    Assign(node, "RollOffMinDistance", node.RollOffMinDistance, (float)Math.Clamp(instance.Get("RollOffMinDistance").Number, 0, 1000), value => node.RollOffMinDistance = value);
                if (instance.Get("RollOffMaxDistance").Type == DataType.Number && double.IsFinite(instance.Get("RollOffMaxDistance").Number))
                    Assign(node, "RollOffMaxDistance", node.RollOffMaxDistance, (float)Math.Clamp(instance.Get("RollOffMaxDistance").Number, node.RollOffMinDistance + .01f, 10000), value => node.RollOffMaxDistance = value);
            }
            if (node.Type == "WeldConstraint")
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
            if (node.Type is "Beam" or "Trail" or "VectorForce" or "LinearVelocity")
            {
                var attachment0Value = instance.Get("Attachment0");
                var requestedAttachment0 = ReadNodeArgument(attachment0Value);
                if (requestedAttachment0 is { Type: "Attachment" } && (node.Type is "VectorForce" or "LinearVelocity" || requestedAttachment0.Id != node.Attachment1))
                    Assign(node, "Attachment0", node.Attachment0, requestedAttachment0.Id, value => node.Attachment0 = value);
                else if (attachment0Value.Type == DataType.Nil) Assign(node, "Attachment0", node.Attachment0, string.Empty, value => node.Attachment0 = value);
                if (node.Type == "VectorForce")
                {
                    var force = ReadVector(instance.Get("Force"), [float.NaN, float.NaN, float.NaN]);
                    if (force.All(float.IsFinite))
                    {
                        var requestedForce = force.Select(component => Math.Clamp(component, -1000000, 1000000)).ToArray();
                        if (!node.Force.SequenceEqual(requestedForce)) { node.Force = requestedForce; MarkChanged(node, "Force"); }
                    }
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
                }
                else if (node.Type == "LinearVelocity")
                {
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
                    if (TryReadVector2(instance.Get("PlaneVelocity"), out var plane))
                    {
                        var requested = plane.Select(component => Math.Clamp(component, -10000, 10000)).ToArray();
                        if (!node.PlaneVelocity.SequenceEqual(requested)) { node.PlaneVelocity = requested; MarkChanged(node, "PlaneVelocity"); }
                    }
                    if (instance.Get("LineVelocity").Type == DataType.Number && double.IsFinite(instance.Get("LineVelocity").Number))
                        Assign(node, "LineVelocity", node.LineVelocity, (float)Math.Clamp(instance.Get("LineVelocity").Number, -10000, 10000), value => node.LineVelocity = value);
                    if (instance.Get("MaxForce").Type == DataType.Number && double.IsFinite(instance.Get("MaxForce").Number))
                        Assign(node, "MaxForce", node.MaxForce, (float)Math.Clamp(instance.Get("MaxForce").Number, 0, 1000000), value => node.MaxForce = value);
                    if (instance.Get("ForceLimitsEnabled").Type == DataType.Boolean)
                        Assign(node, "ForceLimitsEnabled", node.ForceLimitsEnabled, instance.Get("ForceLimitsEnabled").Boolean, value => node.ForceLimitsEnabled = value);
                    if (instance.Get("VelocityConstraintMode").Type == DataType.String && instance.Get("VelocityConstraintMode").String is "Vector" or "Line" or "Plane")
                        Assign(node, "VelocityConstraintMode", node.VelocityConstraintMode, instance.Get("VelocityConstraintMode").String, value => node.VelocityConstraintMode = value);
                    if (instance.Get("RelativeTo").Type == DataType.String && instance.Get("RelativeTo").String is "World" or "Attachment0")
                        Assign(node, "RelativeTo", node.RelativeTo, instance.Get("RelativeTo").String, value => node.RelativeTo = value);
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
            if (instance.Get("TextColor").CastToString() is { } textColor) Assign(node, "TextColor", node.TextColor, textColor, value => node.TextColor = value);
            if (instance.Get("Visible").Type == DataType.Boolean) Assign(node, "Visible", node.Visible, instance.Get("Visible").Boolean, value => node.Visible = value);
            if (IsGuiObject(node))
            {
                var requestedZIndex = instance.Get("ZIndex");
                if (requestedZIndex.Type == DataType.Number && double.IsFinite(requestedZIndex.Number)
                    && requestedZIndex.Number == Math.Truncate(requestedZIndex.Number)
                    && requestedZIndex.Number >= int.MinValue && requestedZIndex.Number <= int.MaxValue)
                    Assign(node, "ZIndex", node.ZIndex, (int)requestedZIndex.Number, value => node.ZIndex = value);
                var requestedLayoutOrder = instance.Get("LayoutOrder");
                if (requestedLayoutOrder.Type == DataType.Number && double.IsFinite(requestedLayoutOrder.Number)
                    && requestedLayoutOrder.Number == Math.Truncate(requestedLayoutOrder.Number)
                    && requestedLayoutOrder.Number >= int.MinValue && requestedLayoutOrder.Number <= int.MaxValue)
                    Assign(node, "LayoutOrder", node.LayoutOrder, (int)requestedLayoutOrder.Number, value => node.LayoutOrder = value);
            }
            if (instance.Get("CornerRadius").Type == DataType.Number) Assign(node, "CornerRadius", node.CornerRadius, (float)Math.Max(0, instance.Get("CornerRadius").Number), value => node.CornerRadius = value);
            if (instance.Get("StrokeColor").CastToString() is { } strokeColor) Assign(node, "StrokeColor", node.StrokeColor, strokeColor, value => node.StrokeColor = value);
            if (instance.Get("StrokeThickness").Type == DataType.Number) Assign(node, "StrokeThickness", node.StrokeThickness, (float)Math.Max(0, instance.Get("StrokeThickness").Number), value => node.StrokeThickness = value);
            if (instance.Get("TextSize").Type == DataType.Number) Assign(node, "TextSize", node.TextSize, (float)Math.Clamp(instance.Get("TextSize").Number, 1, 200), value => node.TextSize = value);
            if (node.Type is "TextLabel" or "TextButton" or "TextBox" && instance.Get("TextScaled").Type == DataType.Boolean)
                Assign(node, "TextScaled", node.TextScaled, instance.Get("TextScaled").Boolean, value => node.TextScaled = value);
            if (node.Type == "UITextSizeConstraint")
            {
                if (instance.Get("MinTextSize") is { Type: DataType.Number } minTextSize && double.IsFinite(minTextSize.Number))
                    Assign(node, "MinTextSize", node.MinTextSize, (float)Math.Clamp(minTextSize.Number, 1, 1000), value => node.MinTextSize = value);
                if (instance.Get("MaxTextSize") is { Type: DataType.Number } maxTextSize && double.IsFinite(maxTextSize.Number))
                    Assign(node, "MaxTextSize", node.MaxTextSize, (float)Math.Clamp(maxTextSize.Number, node.MinTextSize, 1000), value => node.MaxTextSize = value);
            }
            if (instance.Get("LayoutSpacing").Type == DataType.Number) Assign(node, "LayoutSpacing", node.LayoutSpacing, (float)Math.Max(0, instance.Get("LayoutSpacing").Number), value => node.LayoutSpacing = value);
            if (instance.Get("TableColumns").Type == DataType.Number) Assign(node, "TableColumns", node.TableColumns, (int)Math.Clamp(instance.Get("TableColumns").Number, 1, 24), value => node.TableColumns = value);
            if (instance.Get("AspectRatio").Type == DataType.Number) Assign(node, "AspectRatio", node.AspectRatio, (float)Math.Max(.001, instance.Get("AspectRatio").Number), value => node.AspectRatio = value);
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
            }
            if (instance.Get("LayoutDirection").CastToString() is { } layoutDirection) Assign(node, "LayoutDirection", node.LayoutDirection, layoutDirection == "Horizontal" ? "Horizontal" : "Vertical", value => node.LayoutDirection = value);
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
            }
            if (node.Type == "Workspace" && instance.Get("Gravity").Type == DataType.Number && double.IsFinite(instance.Get("Gravity").Number))
                Assign(node, "Gravity", node.Gravity, (float)Math.Clamp(instance.Get("Gravity").Number, 0, 1000), value => node.Gravity = value);
            if (node.Type == "Workspace" && instance.Get("AutoManageCharacter").Type == DataType.Boolean)
                Assign(node, "AutoManageCharacter", node.AutoManageCharacter, instance.Get("AutoManageCharacter").Boolean, value => node.AutoManageCharacter = value);
            if (node.Type == "Player")
            {
                var characterValue = instance.Get("Character");
                var characterNode = ReadNodeArgument(characterValue);
                if (characterValue.Type == DataType.Nil || characterNode?.Type == "Model")
                    Assign(node, "Character", node.Character, characterNode?.Id ?? string.Empty, value => node.Character = value);
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
            if (node.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") && instance.Get("Name").CastToString() is { } name)
                Assign(node, "Name", node.Name, name, value => SetNodeName(node, value));
            else if (node.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui")
                instance.Set("Name", DynValue.NewString(node.Name));
            if (instance.Get("Archivable").Type == DataType.Boolean)
                Assign(node, "Archivable", node.Archivable, instance.Get("Archivable").Boolean, value => node.Archivable = value);
            else instance.Set("Archivable", DynValue.NewBoolean(node.Archivable));
            if (node.Type == "Workspace")
            {
                if (instance.Get("SkyboxFront").CastToString() is { } skyboxFront) Assign(node, "SkyboxFront", node.SkyboxFront, skyboxFront, value => node.SkyboxFront = value);
                if (instance.Get("SkyboxBack").CastToString() is { } skyboxBack) Assign(node, "SkyboxBack", node.SkyboxBack, skyboxBack, value => node.SkyboxBack = value);
                if (instance.Get("SkyboxLeft").CastToString() is { } skyboxLeft) Assign(node, "SkyboxLeft", node.SkyboxLeft, skyboxLeft, value => node.SkyboxLeft = value);
                if (instance.Get("SkyboxRight").CastToString() is { } skyboxRight) Assign(node, "SkyboxRight", node.SkyboxRight, skyboxRight, value => node.SkyboxRight = value);
                if (instance.Get("SkyboxTop").CastToString() is { } skyboxTop) Assign(node, "SkyboxTop", node.SkyboxTop, skyboxTop, value => node.SkyboxTop = value);
                if (instance.Get("SkyboxBottom").CastToString() is { } skyboxBottom) Assign(node, "SkyboxBottom", node.SkyboxBottom, skyboxBottom, value => node.SkyboxBottom = value);
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
        if (node.Type is not ("Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")) return false;
        if (node.Type != "ImageLabel") return true;
        var parent = FindParentNode(node);
        while (parent is not null)
        {
            if (parent.Id == project.Gui.Id || parent.Type == "ScreenGui") return true;
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
