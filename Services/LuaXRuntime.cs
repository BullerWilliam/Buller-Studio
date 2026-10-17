using BullerStudio.Models;
using System.IO;
using WattleScript.Interpreter;

namespace BullerStudio.Services;

public sealed class LuaXRuntime
{
    private readonly GameProject project;
    private readonly Action<SceneNode, string, DynValue>? propertyChanged;
    private readonly Action<string>? output;
    private readonly Dictionary<string, Table> instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DynValue>> touchedCallbacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeContacts = new(StringComparer.Ordinal);
    private Script script = null!;

    public LuaXRuntime(GameProject project, Action<SceneNode, string, DynValue>? propertyChanged = null, Action<string>? output = null)
    {
        this.project = project;
        this.propertyChanged = propertyChanged;
        this.output = output;
        Initialize();
    }

    public IReadOnlyCollection<SceneNode> Parts => GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type is "Part" or "MeshPart").ToArray();

    public void RunAll()
    {
        foreach (var gameScript in project.Scripts)
        {
            try { script.DoString(gameScript.Source, null, gameScript.Name); }
            catch (Exception exception) { output?.Invoke($"{gameScript.Name}: {exception.Message}"); }
        }
        SynchronizeInstances();
    }

    public void Run(string scriptId)
    {
        var gameScript = project.Scripts.FirstOrDefault(item => item.Id == scriptId);
        if (gameScript is null) return;
        try { script.DoString(gameScript.Source, null, gameScript.Name); output?.Invoke($"{gameScript.Name} finished."); }
        catch (Exception exception) { output?.Invoke($"{gameScript.Name}: {exception.Message}"); }
        SynchronizeInstances();
    }

    public void UpdateTouched()
    {
        SynchronizeInstances();
        var nodes = Parts.ToDictionary(node => node.Id, StringComparer.Ordinal);
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

    private void Initialize()
    {
        script = new Script(CoreModules.Preset_SoftSandbox);
        var workspace = new Table(script);
        workspace.Set("Name", DynValue.NewString(project.Workspace.Name));
        workspace.Set("WaitForChild", DynValue.NewCallback((_, args) => FindChild(args[0].Table, args[1].String)));
        workspace.Set("waitforchild", DynValue.NewCallback((_, args) => FindChild(args[0].Table, args[1].String)));
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children))
        {
            var instance = CreateInstance(node);
            instances[node.Id] = instance;
            script.Globals.Set(node.Name, DynValue.NewTable(instance));
        }
        foreach (var node in project.Workspace.Children) if (instances.TryGetValue(node.Id, out var instance)) workspace.Set(node.Name, DynValue.NewTable(instance));
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children))
        {
            var target = instances[node.Id];
            foreach (var child in node.Children) if (instances.TryGetValue(child.Id, out var childTable)) target.Set(child.Name, DynValue.NewTable(childTable));
        }
        var game = new Table(script);
        game.Set("Workspace", DynValue.NewTable(workspace));
        script.Globals.Set("game", DynValue.NewTable(game));
        script.Globals.Set("workspace", DynValue.NewTable(workspace));
        script.Globals.Set("SetProperty", DynValue.NewCallback(SetProperty));
        script.Globals.Set("GetProperty", DynValue.NewCallback(GetProperty));
        script.Globals.Set("WaitForChild", DynValue.NewCallback((_, args) => WaitForChild(args)));
        script.Globals.Set("GetAssetPath", DynValue.NewCallback((_, args) => GetAssetPath(args)));
        script.Globals.Set("ConnectTouched", DynValue.NewCallback(ConnectTouched));
        script.Globals.Set("print", DynValue.NewCallback((_, args) => { var values = new List<string>(); for (var index = 0; index < args.Count; index++) values.Add(args[index].ToPrintString()); output?.Invoke(string.Join("\t", values)); return DynValue.Nil; }));
    }

    private Table CreateInstance(SceneNode node)
    {
        var instance = new Table(script);
        instance.Set("Name", DynValue.NewString(node.Name));
        instance.Set("Id", DynValue.NewString(node.Id));
        instance.Set("ClassName", DynValue.NewString(node.Type));
        instance.Set("Position", VectorValue(node.Position));
        instance.Set("Rotation", VectorValue(node.Rotation));
        instance.Set("Size", VectorValue(node.Size));
        instance.Set("Color", DynValue.NewString(node.Color));
        instance.Set("Anchored", DynValue.NewBoolean(node.Anchored));
        instance.Set("Asset", DynValue.NewString(node.Asset));
        instance.Set("CameraMode", DynValue.NewString(node.CameraMode));
        instance.Set("Mode", DynValue.NewString(node.CameraMode));
        instance.Set("Subject", DynValue.NewString(node.Subject));
        instance.Set("FieldOfView", DynValue.NewNumber(node.FieldOfView));
        instance.Set("SetProperty", DynValue.NewCallback((_, args) => SetPropertyValue(node, args[0].String, args[1])));
        instance.Set("GetProperty", DynValue.NewCallback((_, args) => GetPropertyValue(node, args[0].String)));
        instance.Set("WaitForChild", DynValue.NewCallback((_, args) => FindChild(instance, args[1].String)));
        instance.Set("waitforchild", DynValue.NewCallback((_, args) => FindChild(instance, args[1].String)));
        var touched = new Table(script);
        touched.Set("Connect", DynValue.NewCallback((_, args) => RegisterTouch(node.Id, args[1])));
        instance.Set("Touched", DynValue.NewTable(touched));
        instance.Set("touched", DynValue.NewTable(touched));
        return instance;
    }

    private DynValue SetProperty(ScriptExecutionContext context, CallbackArguments args)
    {
        var node = FindNode(args[0].String);
        return node is null ? DynValue.Nil : SetPropertyValue(node, args[1].String, args[2]);
    }

    private DynValue GetProperty(ScriptExecutionContext context, CallbackArguments args)
    {
        var node = FindNode(args[0].String);
        return node is null ? DynValue.Nil : GetPropertyValue(node, args[1].String);
    }

    private DynValue SetPropertyValue(SceneNode node, string property, DynValue value)
    {
        switch (property.ToLowerInvariant())
        {
            case "position": node.Position = ReadVector(value, node.Position); break;
            case "rotation": node.Rotation = ReadVector(value, node.Rotation); break;
            case "size": node.Size = ReadVector(value, node.Size); break;
            case "color": node.Color = value.CastToString() ?? node.Color; break;
            case "anchored": node.Anchored = value.CastToBool(); break;
            case "asset": node.Asset = value.CastToString() ?? string.Empty; break;
            case "name": node.Name = value.CastToString() ?? node.Name; break;
            case "cameramode":
            case "mode": node.CameraMode = value.CastToString() is "Subject" ? "Subject" : "Custom"; break;
            case "subject": node.Subject = value.CastToString() ?? string.Empty; break;
            case "fieldofview": node.FieldOfView = (float)Math.Clamp(value.Number, 10, 120); break;
            default: return DynValue.Nil;
        }
        if (instances.TryGetValue(node.Id, out var instance))
        {
            instance.Set("Name", DynValue.NewString(node.Name));
            instance.Set("Position", VectorValue(node.Position));
            instance.Set("Rotation", VectorValue(node.Rotation));
            instance.Set("Size", VectorValue(node.Size));
            instance.Set("Color", DynValue.NewString(node.Color));
            instance.Set("Anchored", DynValue.NewBoolean(node.Anchored));
            instance.Set("Asset", DynValue.NewString(node.Asset));
            instance.Set("CameraMode", DynValue.NewString(node.CameraMode));
            instance.Set("Mode", DynValue.NewString(node.CameraMode));
            instance.Set("Subject", DynValue.NewString(node.Subject));
            instance.Set("FieldOfView", DynValue.NewNumber(node.FieldOfView));
        }
        propertyChanged?.Invoke(node, property, value);
        return DynValue.Nil;
    }

    private DynValue GetPropertyValue(SceneNode node, string property) => property.ToLowerInvariant() switch
    {
        "position" => VectorValue(node.Position),
        "rotation" => VectorValue(node.Rotation),
        "size" => VectorValue(node.Size),
        "color" => DynValue.NewString(node.Color),
        "anchored" => DynValue.NewBoolean(node.Anchored),
        "asset" => DynValue.NewString(node.Asset),
        "name" => DynValue.NewString(node.Name),
        "cameramode" or "mode" => DynValue.NewString(node.CameraMode),
        "subject" => DynValue.NewString(node.Subject),
        "fieldofview" => DynValue.NewNumber(node.FieldOfView),
        _ => DynValue.Nil
    };

    private DynValue WaitForChild(CallbackArguments args)
    {
        if (args.Count < 2) return DynValue.Nil;
        var parent = args[0].Type == DataType.Table
            ? FindNode(args[0].Table.Get("Id").CastToString() ?? string.Empty)
            : args[0].Type == DataType.String && args[0].String != "Workspace" ? FindNode(args[0].String) : null;
        var name = args[1].CastToString();
        var node = (parent?.Children ?? project.Workspace.Children).FirstOrDefault(child => child.Name == name || child.Id == name);
        return node is not null && instances.TryGetValue(node.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private DynValue FindChild(Table parent, string name)
    {
        var node = parent.Get("Id").CastToString() is { } id ? FindNode(id) : project.Workspace;
        var child = (node?.Children ?? project.Workspace.Children).FirstOrDefault(item => item.Name == name || item.Id == name);
        return child is not null && instances.TryGetValue(child.Id, out var instance) ? DynValue.NewTable(instance) : DynValue.Nil;
    }

    private DynValue GetAssetPath(CallbackArguments args)
    {
        var key = args[0].CastToString();
        var asset = project.Assets.FirstOrDefault(item => item.Name == key || item.RelativePath == key);
        return asset is null ? DynValue.Nil : DynValue.NewString(Path.GetFullPath(Path.Combine(project.ProjectPath, asset.RelativePath)));
    }

    private DynValue ConnectTouched(ScriptExecutionContext context, CallbackArguments args)
    {
        if (args.Count > 1 && args[1].Type == DataType.Function) return RegisterTouch(args[0].String, args[1]);
        return DynValue.Nil;
    }

    private DynValue RegisterTouch(string id, DynValue callback)
    {
        if (!touchedCallbacks.TryGetValue(id, out var callbacks)) touchedCallbacks[id] = callbacks = [];
        callbacks.Add(callback);
        return DynValue.Nil;
    }

    private void DispatchTouch(SceneNode receiver, SceneNode other)
    {
        if (!touchedCallbacks.TryGetValue(receiver.Id, out var callbacks) || !instances.TryGetValue(other.Id, out var otherInstance)) return;
        foreach (var callback in callbacks.ToArray())
        {
            try { callback.Function.Call(DynValue.NewTable(otherInstance)); }
            catch (Exception exception) { output?.Invoke($"Touched callback: {exception.Message}"); }
        }
    }

    private void SynchronizeInstances()
    {
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children))
        {
            if (!instances.TryGetValue(node.Id, out var instance)) continue;
            if (instance.Get("Position").Type == DataType.Table) node.Position = ReadVector(instance.Get("Position"), node.Position);
            if (instance.Get("Rotation").Type == DataType.Table) node.Rotation = ReadVector(instance.Get("Rotation"), node.Rotation);
            if (instance.Get("Size").Type == DataType.Table) node.Size = ReadVector(instance.Get("Size"), node.Size);
            if (instance.Get("Color").CastToString() is { } color) node.Color = color;
            if (node.Type == "Camera")
            {
                var mode = instance.Get("Mode").CastToString() ?? instance.Get("CameraMode").CastToString();
                if (mode is "Custom" or "Subject") node.CameraMode = mode;
                node.Subject = instance.Get("Subject").CastToString() ?? node.Subject;
                if (instance.Get("FieldOfView").Type == DataType.Number) node.FieldOfView = (float)Math.Clamp(instance.Get("FieldOfView").Number, 10, 120);
            }
        }
    }

    private static bool Overlaps(SceneNode first, SceneNode second)
    {
        for (var axis = 0; axis < 3; axis++) if (Math.Abs(first.Position[axis] - second.Position[axis]) > (Math.Abs(first.Size[axis]) + Math.Abs(second.Size[axis])) * .5f) return false;
        return true;
    }

    private DynValue VectorValue(float[] values)
    {
        var table = new Table(script);
        table.Set("X", DynValue.NewNumber(values[0])); table.Set("Y", DynValue.NewNumber(values[1])); table.Set("Z", DynValue.NewNumber(values[2]));
        table.Set(1, DynValue.NewNumber(values[0])); table.Set(2, DynValue.NewNumber(values[1])); table.Set(3, DynValue.NewNumber(values[2]));
        return DynValue.NewTable(table);
    }

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
        return value.Type == DataType.Number ? (float)value.Number : fallback;
    }

    private SceneNode? FindNode(string id) => GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == id || node.Name == id);
}
