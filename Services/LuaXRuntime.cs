using BullerStudio.Models;
using System.IO;
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
    private readonly Dictionary<string, List<ScriptCallback>> valueChangedCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> humanoidWasAlive = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeContacts = new(StringComparer.Ordinal);
    private readonly List<ScriptExecution> activeScripts = [];
    private readonly List<ScheduledTask> scheduledTasks = [];
    private readonly Queue<string> pendingScriptStarts = new();
    private readonly HashSet<string> queuedScriptIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> startedScriptIds = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Table, LuaXTypeTag> runtimeTypeTags = new();
    private Script script = null!;
    private GameScript? currentGameScript;
    private Table vector3Metatable = null!;

    private sealed record LuaXTypeTag(string Name);
    private sealed record ScriptCallback(GameScript GameScript, DynValue Function, string Name);
    private sealed record ScriptExecution(GameScript GameScript, Coroutine Coroutine, bool ReportFinished, string? CallbackName = null);
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
        "Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "Model", "Folder", "Humanoid",
        "BoolValue", "IntValue", "NumberValue", "StringValue",
        "PointLight", "SpotLight", "SurfaceLight", "Decal", "Texture", "Sound", "ProximityPrompt", "ClickDetector", "Highlight", "WeldConstraint", "Attachment", "Beam",
        "ScreenGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox",
        "UICorner", "UIOutline", "UIStroke", "UIListLayout", "UITableLayout", "UIAspectRatio"
    };

    public LuaXRuntime(GameProject project, Action<SceneNode, string, DynValue>? propertyChanged = null, Action<string>? output = null)
    {
        this.project = project;
        this.propertyChanged = propertyChanged;
        this.output = output;
        Initialize();
    }

    public IReadOnlyCollection<SceneNode> Parts => GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
    public bool HasPendingScripts => activeScripts.Count > 0 || pendingScriptStarts.Count > 0 || scheduledTasks.Count > 0;

    public void RunAll()
    {
        activeScripts.Clear();
        scheduledTasks.Clear();
        pendingScriptStarts.Clear();
        queuedScriptIds.Clear();
        startedScriptIds.Clear();
        foreach (var gameScript in project.Scripts.ToArray())
        {
            var node = FindScriptNode(gameScript.Id);
            if (node is not null && (node.Type != "Script" || !node.Enabled || !IsScriptAttachedForExecution(node))) continue;
            startedScriptIds.Add(gameScript.Id);
            StartScript(gameScript, false);
        }
        StartQueuedScripts();
        ResumePendingScripts(.05f);
    }

    public void Run(string scriptId)
    {
        var gameScript = project.Scripts.FirstOrDefault(item => item.Id == scriptId);
        if (gameScript is null) return;
        startedScriptIds.Add(scriptId);
        StartScript(gameScript, true);
        StartQueuedScripts();
    }

    public void Update(float elapsedSeconds = .05f)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .001f, .25f);
        ResumePendingScripts(elapsed);
        RunDelayedTasks(elapsed);
        StartQueuedScripts();
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
            var coroutine = script.CreateCoroutine(script.LoadString(gameScript.Source, null, gameScript.Name)).Coroutine;
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
                continue;
            }
            BindCurrentScript(execution.GameScript);
            try
            {
                execution.Coroutine.Resume(DynValue.NewNumber(elapsedSeconds));
                SynchronizeInstances();
                if (execution.Coroutine.State != CoroutineState.Dead && project.Scripts.Any(item => item.Id == execution.GameScript.Id)
                    && (FindScriptNode(execution.GameScript.Id) is not { } resumedNode || resumedNode.Enabled && IsScriptAttachedForExecution(resumedNode))) continue;
                activeScripts.Remove(execution);
                if (execution.ReportFinished) output?.Invoke($"{execution.GameScript.Name} finished.");
            }
            catch (Exception exception)
            {
                activeScripts.Remove(execution);
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
        ResumePendingScripts(elapsed);
        RunDelayedTasks(elapsed);
        SynchronizeInstances();
        StartQueuedScripts();
        var nodes = Parts.Where(node => node.CanTouch).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var touchingNow = new HashSet<string>(StringComparer.Ordinal);
        var parts = nodes.Values.ToArray();
        for (var first = 0; first < parts.Length; first++)
        for (var second = first + 1; second < parts.Length; second++)
        {
            if (!Overlaps(parts[first], parts[second])) continue;
            var key = string.CompareOrdinal(parts[first].Id, parts[second].Id) < 0 ? $"{parts[first].Id}:{parts[second].Id}" : $"{parts[second].Id}:{parts[first].Id}";
            touchingNow.Add(key);
            if (activeContacts.Contains(key)) continue;
            DispatchTouch(parts[first], parts[second]);
            DispatchTouch(parts[second], parts[first]);
        }
        activeContacts.Clear();
        activeContacts.UnionWith(touchingNow);
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
        foreach (var node in GameProjectStore.Flatten(project))
        {
            var instance = CreateInstance(node);
            instances[node.Id] = instance;
            if (node.Type == "Humanoid") humanoidWasAlive[node.Id] = node.Health > 0;
            if (node.Type is not ("Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui")) script.Globals.Set(node.Name, DynValue.NewTable(instance));
        }
        foreach (var node in GameProjectStore.Flatten(project))
        {
            var target = instances[node.Id];
            foreach (var child in node.Children) if (instances.TryGetValue(child.Id, out var childTable)) target.Set(child.Name, DynValue.NewTable(childTable));
            var parent = FindParentNode(node);
            target.Set("Parent", parent is not null && instances.TryGetValue(parent.Id, out var parentTable) ? DynValue.NewTable(parentTable) : DynValue.Nil);
        }
        var game = new Table(script);
        runtimeTypeTags.Add(game, new LuaXTypeTag("DataModel"));
        var workspace = instances[project.Workspace.Id];
        var players = instances[project.Players.Id];
        var replicatedStorage = instances[project.ReplicatedStorage.Id];
        var serverScriptService = instances[project.ServerScriptService.Id];
        var serverStorage = instances[project.ServerStorage.Id];
        var gui = instances[project.Gui.Id];
        game.Set("Workspace", DynValue.NewTable(workspace));
        game.Set("Players", DynValue.NewTable(players));
        game.Set("ReplicatedStorage", DynValue.NewTable(replicatedStorage));
        game.Set("ServerScriptService", DynValue.NewTable(serverScriptService));
        game.Set("ServerStorage", DynValue.NewTable(serverStorage));
        game.Set("Gui", DynValue.NewTable(gui));
        game.Set("GetService", DynValue.NewCallback((_, args) =>
        {
            var serviceName = args.Count > 1 ? args[1].CastToString() : args.Count == 1 ? args[0].CastToString() : null;
            return serviceName switch
            {
                "Workspace" => DynValue.NewTable(workspace),
                "Players" => DynValue.NewTable(players),
                "ReplicatedStorage" => DynValue.NewTable(replicatedStorage),
                "ServerScriptService" => DynValue.NewTable(serverScriptService),
                "ServerStorage" => DynValue.NewTable(serverStorage),
                "Gui" => DynValue.NewTable(gui),
                _ => DynValue.Nil
            };
        }));
        script.Globals.Set("game", DynValue.NewTable(game));
        script.Globals.Set("workspace", DynValue.NewTable(workspace));
        script.Globals.Set("Players", DynValue.NewTable(players));
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
        script.Globals.Set("Enum", DynValue.NewTable(enumTable));
        var vector3 = new Table(script);
        vector3.Set("new", DynValue.NewCallback((_, args) => VectorValue([NumberArgument(args, 0), NumberArgument(args, 1), NumberArgument(args, 2)])));
        vector3.Set("zero", VectorValue([0, 0, 0]));
        vector3.Set("one", VectorValue([1, 1, 1]));
        script.Globals.Set("Vector3", DynValue.NewTable(vector3));
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
        instance.Set("Position", IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Position));
        instance.Set("Rotation", VectorValue(node.Rotation));
        instance.Set("Size", IsGuiObject(node) ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size));
        instance.Set("Color", ColorValue(node.Color));
        instance.Set("FillColor", ColorValue(node.FillColor));
        instance.Set("OutlineColor", ColorValue(node.OutlineColor));
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
        instance.Set("Enabled", DynValue.NewBoolean(node.Enabled));
        instance.Set("Brightness", DynValue.NewNumber(node.Brightness));
        instance.Set("Range", DynValue.NewNumber(node.Range));
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
        instance.Set("CornerRadius", DynValue.NewNumber(node.CornerRadius));
        instance.Set("StrokeColor", DynValue.NewString(node.StrokeColor));
        instance.Set("StrokeThickness", DynValue.NewNumber(node.StrokeThickness));
        instance.Set("TextSize", DynValue.NewNumber(node.TextSize));
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
        instance.Set("Anchored", DynValue.NewBoolean(node.Anchored));
        instance.Set("CanCollide", DynValue.NewBoolean(node.CanCollide));
        instance.Set("CanTouch", DynValue.NewBoolean(node.CanTouch));
        instance.Set("Velocity", VectorValue(node.Velocity));
        instance.Set("AssemblyLinearVelocity", VectorValue(node.Velocity));
        instance.Set("CastShadow", DynValue.NewBoolean(node.CastShadow));
        instance.Set("Gravity", DynValue.NewNumber(node.Gravity));
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
        if (node.Type == "Sound")
        {
            instance.Set("Play", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(true))));
            instance.Set("Stop", DynValue.NewCallback((_, _) => SetPropertyValue(node, "Playing", DynValue.NewBoolean(false))));
        }
        if (node.Type == "Humanoid")
        {
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
        return instance;
    }

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
        script.Globals.Set("script", instances.TryGetValue(gameScript.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil);
    }

    private void StartCallback(ScriptCallback callback, params DynValue[] arguments)
    {
        if (!project.Scripts.Any(item => item.Id == callback.GameScript.Id)
            || FindScriptNode(callback.GameScript.Id) is { } owner && (!owner.Enabled || !IsScriptAttachedForExecution(owner))) return;
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
    }

    private SceneNode? ReadNodeArgument(DynValue value)
    {
        if (value.Type == DataType.Table) return FindNode(value.Table.Get("Id").CastToString() ?? string.Empty);
        return value.Type == DataType.String ? FindNode(value.String) : null;
    }

    private DynValue NewInstance(CallbackArguments args)
    {
        if (args.Count == 0 || args[0].Type != DataType.String || !CreatableClasses.Contains(args[0].String)) return DynValue.Nil;
        var className = args[0].String;
        var node = new SceneNode { Type = className, Name = className };
        if (SceneNodeTypes.IsBasePart(node)) node.Position = [0, 1, 0];
        if (className == "SpawnLocation") { node.Size = [6, 1, 6]; node.Position = [0, .5f, 0]; node.Color = "#72BF9D"; node.Material = "Metal"; }
        if (className == "Seat") { node.Size = [2, 1, 2]; node.Position = [0, .5f, 0]; node.Color = "#596B89"; node.Material = "Metal"; }
        createdNodes[node.Id] = node;
        var instance = CreateInstance(node);
        instances[node.Id] = instance;
        if (className == "Humanoid") humanoidWasAlive[node.Id] = true;
        if (args.Count > 1 && TrySetParent(node, args[1]))
            propertyChanged?.Invoke(node, "Parent", DynValue.NewTable(instances[FindParentNode(node)!.Id]));
        return DynValue.NewTable(instance);
    }

    private DynValue CloneInstance(SceneNode source)
    {
        if (source.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera" || !instances.ContainsKey(source.Id)) return DynValue.Nil;
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
            if (node.Type == "Beam")
            {
                instances[node.Id].Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                instances[node.Id].Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
            }
        }
        foreach (var original in new[] { source }.Concat(GameProjectStore.Flatten(source.Children)))
        {
            if (original.Type != "Script" || !renewedIds.TryGetValue(original.Id, out var clonedId)) continue;
            var originalScript = project.Scripts.FirstOrDefault(item => item.Id == original.Id);
            if (originalScript is null) continue;
            var clonedScript = clonedNodes.First(node => node.Id == clonedId);
            project.Scripts.Add(new GameScript { Id = clonedId, Name = clonedScript.Name, Source = originalScript.Source });
        }
        return DynValue.NewTable(instances[clone.Id]);
    }

    private DynValue DestroyInstance(SceneNode node)
    {
        if (node.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera" || !instances.ContainsKey(node.Id)) return DynValue.Nil;
        var oldParent = FindParentNode(node);
        var sourceCollection = FindSourceCollection(node);
        sourceCollection?.Remove(node);
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
            valueChangedCallbacks.Remove(removed.Id);
            queuedScriptIds.Remove(removed.Id);
            startedScriptIds.Remove(removed.Id);
            project.Scripts.RemoveAll(script => script.Id == removed.Id);
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
        activeScripts.RemoveAll(execution => scriptIds.Contains(execution.GameScript.Id));
        scheduledTasks.RemoveAll(item => scriptIds.Contains(item.Owner.Id));
        RemoveCallbacksOwnedBy(scriptIds, touchedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, guiActivatedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, guiTextChangedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, promptTriggeredCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, clickDetectorCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, humanoidDiedCallbacks);
        RemoveCallbacksOwnedBy(scriptIds, valueChangedCallbacks);
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
        if (source.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera") return false;
        if (value.Type == DataType.Nil)
        {
            var detachedOldParent = FindParentNode(source);
            var oldCollection = FindSourceCollection(source);
            if (oldCollection is null) return createdNodes.ContainsKey(source.Id);
            oldCollection.Remove(source);
            createdNodes[source.Id] = source;
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
        if (updateLinks) RefreshLinksForMove(source, oldParent, parent);
        StopInactiveScripts(source);
        QueueRunnableScripts(source);
        return true;
    }

    private SceneNode? FindParentNode(SceneNode target)
    {
        if (ReferenceEquals(target, project.Workspace) || ReferenceEquals(target, project.Players) || ReferenceEquals(target, project.ReplicatedStorage) || ReferenceEquals(target, project.ServerScriptService) || ReferenceEquals(target, project.ServerStorage) || ReferenceEquals(target, project.Gui)) return null;
        return FindParentNode(project.Workspace, target) ?? FindParentNode(project.Players, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.Gui, target)
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
        FindParentCollection(project.Workspace, target) ?? FindParentCollection(project.ReplicatedStorage, target) ?? FindParentCollection(project.ServerScriptService, target) ?? FindParentCollection(project.ServerStorage, target) ?? FindParentCollection(project.Gui, target)
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
            table.Set(child.Name, childValue);
            keys.Add(child.Name);
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
            if (node.Type == "Beam")
            {
                instance.Set("Attachment0", GetAttachmentReferenceValue(node.Attachment0));
                instance.Set("Attachment1", GetAttachmentReferenceValue(node.Attachment1));
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
                instance.Set(child.Name, childValue);
                if (!linkedChildKeys.TryGetValue(node.Id, out var keys)) linkedChildKeys[node.Id] = keys = new HashSet<string>(StringComparer.Ordinal);
                keys.Add(child.Name);
            }
        }
    }

    private void WriteInstanceProperty(SceneNode node, Table instance, string normalizedProperty)
    {
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
            "fillcolor" => "FillColor",
            "outlinecolor" => "OutlineColor",
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
            "enabled" => "Enabled",
            "brightness" => "Brightness",
            "range" => "Range",
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
            "anchored" => "Anchored",
            "cancollide" => "CanCollide",
            "cantouch" => "CanTouch",
            "castshadow" => "CastShadow",
            "gravity" => "Gravity",
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
                case "rotation": node.Rotation = ReadVector(value, node.Rotation); break;
                case "size": SetSizeProperty(node, value); break;
                case "color": node.Color = ReadColor(value, node.Color); break;
                case "fillcolor": if (node.Type != "Highlight") return DynValue.Nil; node.FillColor = ReadColor(value, node.FillColor); break;
                case "outlinecolor": if (node.Type != "Highlight") return DynValue.Nil; node.OutlineColor = ReadColor(value, node.OutlineColor); break;
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
                case "anchored": node.Anchored = value.CastToBool(); break;
                case "cancollide": node.CanCollide = value.CastToBool(); break;
                case "cantouch": node.CanTouch = value.CastToBool(); break;
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
                    if (node.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return DynValue.Nil;
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
                    if (node.Type != "Beam") return DynValue.Nil;
                    var attachment = value.Type == DataType.Nil ? null : ReadNodeArgument(value);
                    if (value.Type != DataType.Nil && attachment?.Type != "Attachment") return DynValue.Nil;
                    var attachmentId = attachment?.Id ?? string.Empty;
                    if (attachmentId.Length > 0 && attachmentId == (normalized == "attachment0" ? node.Attachment1 : node.Attachment0)) return DynValue.Nil;
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
                case "skyboxfront": node.SkyboxFront = value.CastToString() ?? string.Empty; break;
                case "skyboxback": node.SkyboxBack = value.CastToString() ?? string.Empty; break;
                case "skyboxleft": node.SkyboxLeft = value.CastToString() ?? string.Empty; break;
                case "skyboxright": node.SkyboxRight = value.CastToString() ?? string.Empty; break;
                case "skyboxtop": node.SkyboxTop = value.CastToString() ?? string.Empty; break;
                case "skyboxbottom": node.SkyboxBottom = value.CastToString() ?? string.Empty; break;
                default: return DynValue.Nil;
            }
        }
        if (instances.TryGetValue(node.Id, out var instance)) WriteInstanceProperty(node, instance, normalized);
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
        if (IsGuiObject(node) && TryReadUDim2(value, out var scale, out var offset))
        {
            node.PositionScale = scale;
            node.Position = [offset[0], offset[1], node.Position[2]];
            return;
        }
        node.Position = ReadVector(value, node.Position);
        if (IsGuiObject(node)) node.PositionScale = [0, 0];
    }

    private void SetSizeProperty(SceneNode node, DynValue value)
    {
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
        if (node.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return;
        node.Name = name;
        if (node.Type == "Script" && project.Scripts.FirstOrDefault(item => item.Id == node.Id) is { } gameScript)
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

    private DynValue GetPropertyValue(SceneNode node, string property)
    {
        if (property.Equals("Value", StringComparison.OrdinalIgnoreCase) && instances.TryGetValue(node.Id, out var valueInstance))
        {
            var pending = valueInstance.Get("Value");
            if (IsValidValueObjectValue(node, pending)) return pending;
        }
        return property.ToLowerInvariant() switch
        {
        "id" => DynValue.NewString(node.Id),
        "classname" or "type" => DynValue.NewString(node.Type),
        "position" => IsGuiObject(node) ? CreateUDim2Value(node.PositionScale, node.Position) : VectorValue(node.Position),
        "rotation" => VectorValue(node.Rotation),
        "size" => IsGuiObject(node) ? CreateUDim2Value(node.SizeScale, node.Size) : VectorValue(node.Size),
        "color" => ColorValue(node.Color),
        "fillcolor" => ColorValue(node.FillColor),
        "outlinecolor" => ColorValue(node.OutlineColor),
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
        "enabled" => DynValue.NewBoolean(node.Enabled),
        "brightness" => DynValue.NewNumber(node.Brightness),
        "range" => DynValue.NewNumber(node.Range),
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
        "anchored" => DynValue.NewBoolean(node.Anchored),
        "cancollide" => DynValue.NewBoolean(node.CanCollide),
        "cantouch" => DynValue.NewBoolean(node.CanTouch),
        "velocity" or "assemblylinearvelocity" => VectorValue(node.Velocity),
        "castshadow" => DynValue.NewBoolean(node.CastShadow),
        "gravity" => DynValue.NewNumber(node.Gravity),
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
        "skyboxfront" => DynValue.NewString(node.SkyboxFront),
        "skyboxback" => DynValue.NewString(node.SkyboxBack),
        "skyboxleft" => DynValue.NewString(node.SkyboxLeft),
        "skyboxright" => DynValue.NewString(node.SkyboxRight),
        "skyboxtop" => DynValue.NewString(node.SkyboxTop),
        "skyboxbottom" => DynValue.NewString(node.SkyboxBottom),
        _ => DynValue.Nil
        };
    }

    private static DynValue GetValueObjectValue(SceneNode node) => node.Type switch
    {
        "BoolValue" => DynValue.NewBoolean(node.BooleanValue),
        "IntValue" => DynValue.NewNumber(node.IntegerValue),
        "NumberValue" => DynValue.NewNumber(node.NumberValue),
        "StringValue" => DynValue.NewString(node.StringValueText),
        _ => DynValue.Nil
    };

    private static bool IsValidValueObjectValue(SceneNode node, DynValue value) => node.Type switch
    {
        "BoolValue" => value.Type == DataType.Boolean,
        "IntValue" => value.Type == DataType.Number && double.IsFinite(value.Number)
            && value.Number == Math.Truncate(value.Number) && Math.Abs(value.Number) <= 9007199254740991d,
        "NumberValue" => value.Type == DataType.Number && double.IsFinite(value.Number),
        "StringValue" => value.Type == DataType.String,
        _ => false
    };

    private static bool TrySetValueObjectValue(SceneNode node, DynValue value, out bool changed)
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
            parent = args[0].String switch { "Gui" => project.Gui, "ReplicatedStorage" => project.ReplicatedStorage, "ServerScriptService" => project.ServerScriptService, "ServerStorage" => project.ServerStorage, _ => project.Workspace };
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
                if (IsGuiObject(node) && TryReadUDim2(instance.Get("Position"), out var positionScale, out var positionOffset))
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
                if (!node.Rotation.SequenceEqual(rotation)) { node.Rotation = rotation; MarkChanged(node, "Rotation"); }
            }
            if (instance.Get("Size").Type == DataType.Table)
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
            Assign(node, "Color", node.Color, ReadColor(instance.Get("Color"), node.Color), value => node.Color = value);
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
            if ((IsBasePart(node) || node.Type is "Decal" or "Texture" or "Beam") && instance.Get("Transparency").Type == DataType.Number && double.IsFinite(instance.Get("Transparency").Number))
                Assign(node, "Transparency", node.Transparency, (float)Math.Clamp(instance.Get("Transparency").Number, 0, 1), value => node.Transparency = value);
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
            if (instance.Get("Range").Type == DataType.Number && double.IsFinite(instance.Get("Range").Number)) Assign(node, "Range", node.Range, (float)Math.Clamp(instance.Get("Range").Number, 1, 1000), value => node.Range = value);
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
            if (node.Type == "Beam")
            {
                var attachment0Value = instance.Get("Attachment0");
                var requestedAttachment0 = ReadNodeArgument(attachment0Value);
                if (requestedAttachment0 is { Type: "Attachment" } && requestedAttachment0.Id != node.Attachment1)
                    Assign(node, "Attachment0", node.Attachment0, requestedAttachment0.Id, value => node.Attachment0 = value);
                else if (attachment0Value.Type == DataType.Nil) Assign(node, "Attachment0", node.Attachment0, string.Empty, value => node.Attachment0 = value);
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
            if (instance.Get("Texture").CastToString() is { } texture) Assign(node, "Texture", node.Texture, texture, value => node.Texture = value);
            if (instance.Get("Text").CastToString() is { } text) Assign(node, "Text", node.Text, text, value => node.Text = value);
            if (instance.Get("TextColor").CastToString() is { } textColor) Assign(node, "TextColor", node.TextColor, textColor, value => node.TextColor = value);
            if (instance.Get("Visible").Type == DataType.Boolean) Assign(node, "Visible", node.Visible, instance.Get("Visible").Boolean, value => node.Visible = value);
            if (instance.Get("CornerRadius").Type == DataType.Number) Assign(node, "CornerRadius", node.CornerRadius, (float)Math.Max(0, instance.Get("CornerRadius").Number), value => node.CornerRadius = value);
            if (instance.Get("StrokeColor").CastToString() is { } strokeColor) Assign(node, "StrokeColor", node.StrokeColor, strokeColor, value => node.StrokeColor = value);
            if (instance.Get("StrokeThickness").Type == DataType.Number) Assign(node, "StrokeThickness", node.StrokeThickness, (float)Math.Max(0, instance.Get("StrokeThickness").Number), value => node.StrokeThickness = value);
            if (instance.Get("TextSize").Type == DataType.Number) Assign(node, "TextSize", node.TextSize, (float)Math.Clamp(instance.Get("TextSize").Number, 1, 200), value => node.TextSize = value);
            if (instance.Get("LayoutSpacing").Type == DataType.Number) Assign(node, "LayoutSpacing", node.LayoutSpacing, (float)Math.Max(0, instance.Get("LayoutSpacing").Number), value => node.LayoutSpacing = value);
            if (instance.Get("TableColumns").Type == DataType.Number) Assign(node, "TableColumns", node.TableColumns, (int)Math.Clamp(instance.Get("TableColumns").Number, 1, 24), value => node.TableColumns = value);
            if (instance.Get("AspectRatio").Type == DataType.Number) Assign(node, "AspectRatio", node.AspectRatio, (float)Math.Max(.001, instance.Get("AspectRatio").Number), value => node.AspectRatio = value);
            if (instance.Get("LayoutDirection").CastToString() is { } layoutDirection) Assign(node, "LayoutDirection", node.LayoutDirection, layoutDirection == "Horizontal" ? "Horizontal" : "Vertical", value => node.LayoutDirection = value);
            if (instance.Get("CastShadow").Type == DataType.Boolean) Assign(node, "CastShadow", node.CastShadow, instance.Get("CastShadow").Boolean, value => node.CastShadow = value);
            if (instance.Get("Anchored").Type == DataType.Boolean) Assign(node, "Anchored", node.Anchored, instance.Get("Anchored").Boolean, value => node.Anchored = value);
            if (instance.Get("CanCollide").Type == DataType.Boolean) Assign(node, "CanCollide", node.CanCollide, instance.Get("CanCollide").Boolean, value => node.CanCollide = value);
            if (instance.Get("CanTouch").Type == DataType.Boolean) Assign(node, "CanTouch", node.CanTouch, instance.Get("CanTouch").Boolean, value => node.CanTouch = value);
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
            if (node.Type is not ("Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") && instance.Get("Name").CastToString() is { } name)
                Assign(node, "Name", node.Name, name, value => SetNodeName(node, value));
            else if (node.Type is "Workspace" or "Players" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui")
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
    }

    private static bool Overlaps(SceneNode first, SceneNode second)
    {
        for (var axis = 0; axis < 3; axis++) if (Math.Abs(first.Position[axis] - second.Position[axis]) > (Math.Abs(first.Size[axis]) + Math.Abs(second.Size[axis])) * .5f) return false;
        return true;
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
        if (xScale.Type != DataType.Number || xOffset.Type != DataType.Number || yScale.Type != DataType.Number || yOffset.Type != DataType.Number) return false;
        scale = [(float)xScale.Number, (float)yScale.Number];
        offset = [(float)xOffset.Number, (float)yOffset.Number];
        return true;
    }

    private bool IsGuiObject(SceneNode node)
    {
        if (node.Type is not ("Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")) return false;
        if (node.Type != "ImageLabel") return true;
        var parent = FindParentNode(node);
        while (parent is not null)
        {
            if (parent.Id == project.Gui.Id || parent.Type == "ScreenGui") return true;
            if (parent.Id == project.Workspace.Id || parent.Id == project.ReplicatedStorage.Id || parent.Id == project.ServerScriptService.Id || parent.Id == project.ServerStorage.Id || parent.Type == "Model" || SceneNodeTypes.IsBasePart(parent)) return false;
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
