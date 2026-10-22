using System.IO;
using System.Text.Json;
using BullerStudio.Models;
using BullerStudio.Services;

var project = GameProjectStore.NewProject("Event Smoke", string.Empty);
var signal = new SceneNode { Type = "BindableEvent", Name = "RoundStarted" };
if (!GameProjectStore.CanParentNode(project, signal, project.ReplicatedStorage))
    throw new Exception("BindableEvent cannot be inserted under ReplicatedStorage.");
project.ReplicatedStorage.Children.Add(signal);

var nestedValue = new SceneNode { Type = "NumberValue", Name = "Count", NumberValue = 3 };
if (!GameProjectStore.CanParentNode(project, nestedValue, signal))
    throw new Exception("BindableEvent cannot contain a value object.");
signal.Children.Add(nestedValue);

var guiSignal = new SceneNode { Type = "BindableEvent", Name = "GuiSignal" };
var screen = project.Gui.Children.Single(child => child.Type == "ScreenGui");
if (!GameProjectStore.CanParentNode(project, guiSignal, screen))
    throw new Exception("BindableEvent cannot be inserted under ScreenGui.");
screen.Children.Add(guiSignal);
var guiFunction = new SceneNode { Type = "BindableFunction", Name = "GuiFunction" };
if (!GameProjectStore.CanParentNode(project, guiFunction, screen))
    throw new Exception("BindableFunction cannot be inserted under ScreenGui.");
screen.Children.Add(guiFunction);

var responder = new SceneNode { Type = "BindableFunction", Name = "AskServer" };
if (!GameProjectStore.CanParentNode(project, responder, project.ReplicatedStorage))
    throw new Exception("BindableFunction cannot be inserted under ReplicatedStorage.");
project.ReplicatedStorage.Children.Add(responder);
if (!GameProjectStore.CanParentNode(project, new SceneNode { Type = "StringValue" }, responder))
    throw new Exception("BindableFunction cannot contain a value object.");
responder.Children.Add(new SceneNode { Type = "StringValue", Name = "OnInvoke", StringValueText = "child" });

var moduleNode = new SceneNode { Type = "ModuleScript", Name = "SharedModule" };
if (!GameProjectStore.CanParentNode(project, moduleNode, project.ReplicatedStorage))
    throw new Exception("ModuleScript cannot be inserted under ReplicatedStorage.");
project.ReplicatedStorage.Children.Add(moduleNode);
project.Scripts.Add(new GameScript
{
    Id = moduleNode.Id,
    Name = moduleNode.Name,
    Source = "print('module-ran'); local shared = { Count = 1, ParentName = script.Parent.Name }; return shared"
});
var yieldingModule = new SceneNode { Type = "ModuleScript", Name = "YieldingModule" };
project.ReplicatedStorage.Children.Add(yieldingModule);
project.Scripts.Add(new GameScript { Id = yieldingModule.Id, Name = yieldingModule.Name, Source = "task.wait(0.1); return 42" });
var recursiveModule = new SceneNode { Type = "ModuleScript", Name = "RecursiveModule" };
project.ReplicatedStorage.Children.Add(recursiveModule);
project.Scripts.Add(new GameScript { Id = recursiveModule.Id, Name = recursiveModule.Name, Source = "return require(script)" });
var concurrentModule = new SceneNode { Type = "ModuleScript", Name = "ConcurrentModule" };
project.ReplicatedStorage.Children.Add(concurrentModule);
project.Scripts.Add(new GameScript { Id = concurrentModule.Id, Name = concurrentModule.Name, Source = "task.wait(0.1); print('concurrent-ran'); return { Value = 9 }" });
var systemFolder = new SceneNode { Type = "Folder", Name = "Systems" };
project.ServerScriptService.Children.Add(systemFolder);
var siblingModule = new SceneNode { Type = "ModuleScript", Name = "Sibling" };
systemFolder.Children.Add(siblingModule);
project.Scripts.Add(new GameScript { Id = siblingModule.Id, Name = siblingModule.Name, Source = "print('relative-ran'); return { Number = 17 }" });
var relativeScript = new SceneNode { Type = "Script", Name = "RelativeCaller" };
systemFolder.Children.Add(relativeScript);
project.Scripts.Add(new GameScript { Id = relativeScript.Id, Name = relativeScript.Name, Source = "print('relative-result', require('./Sibling').Number, script.Name)" });
var nestedFolder = new SceneNode { Type = "Folder", Name = "Nested" };
systemFolder.Children.Add(nestedFolder);
var parentPathScript = new SceneNode { Type = "Script", Name = "ParentCaller" };
nestedFolder.Children.Add(parentPathScript);
project.Scripts.Add(new GameScript { Id = parentPathScript.Id, Name = parentPathScript.Name, Source = "print('parent-result', require('../Sibling').Number, script.Name)" });
var servicePathScript = new SceneNode { Type = "Script", Name = "ServiceCaller" };
project.ServerScriptService.Children.Add(servicePathScript);
project.Scripts.Add(new GameScript { Id = servicePathScript.Id, Name = servicePathScript.Name, Source = "print('service-result', require('../ReplicatedStorage/SharedModule').Count)" });
var callbackOwnerScript = new SceneNode { Type = "Script", Name = "CallbackOwner" };
project.ServerScriptService.Children.Add(callbackOwnerScript);
project.Scripts.Add(new GameScript { Id = callbackOwnerScript.Id, Name = callbackOwnerScript.Name, Source = "game.ReplicatedStorage.AskServer.OnInvoke = function() return script.Name end" });
var callbackCallerScript = new SceneNode { Type = "Script", Name = "CallbackCaller" };
project.ServerScriptService.Children.Add(callbackCallerScript);
project.Scripts.Add(new GameScript { Id = callbackCallerScript.Id, Name = callbackCallerScript.Name, Source = "print('callback-script', game.ReplicatedStorage.AskServer:Invoke(), script.Name)" });
var ownershipSignal = new SceneNode { Type = "BindableEvent", Name = "OwnershipSignal" };
project.ReplicatedStorage.Children.Add(ownershipSignal);
var ownershipValue = new SceneNode { Type = "NumberValue", Name = "OwnershipValue" };
project.ReplicatedStorage.Children.Add(ownershipValue);
var valueListenerScript = new SceneNode { Type = "Script", Name = "ValueListener" };
project.ServerScriptService.Children.Add(valueListenerScript);
project.Scripts.Add(new GameScript { Id = valueListenerScript.Id, Name = valueListenerScript.Name, Source = "game.ReplicatedStorage.OwnershipValue.Changed:Connect(function() print('value-listener') end)" });
var ownershipScript = new SceneNode { Type = "Script", Name = "OwnershipScript" };
project.ServerScriptService.Children.Add(ownershipScript);
project.Scripts.Add(new GameScript
{
    Id = ownershipScript.Id,
    Name = ownershipScript.Name,
    Source = "game.ReplicatedStorage.AskServer.OnInvoke = function() task.wait(0.05); SetProperty(game.ReplicatedStorage.OwnershipValue, 'Value', 1); game.ReplicatedStorage.OwnershipSignal.Event:Connect(function() print('stale-owned-event') end); task.delay(0.2, function() print('stale-owned-task') end); return 'owned' end"
});
var ownershipCaller = new SceneNode { Type = "Script", Name = "OwnershipCaller" };
project.ServerScriptService.Children.Add(ownershipCaller);
project.Scripts.Add(new GameScript { Id = ownershipCaller.Id, Name = ownershipCaller.Name, Source = "print('ownership-result', game.ReplicatedStorage.AskServer:Invoke())" });
var ownershipDisabler = new SceneNode { Type = "Script", Name = "OwnershipDisabler" };
project.ServerScriptService.Children.Add(ownershipDisabler);
project.Scripts.Add(new GameScript { Id = ownershipDisabler.Id, Name = ownershipDisabler.Name, Source = "game.ServerScriptService.OwnershipScript.Enabled = false" });
var errorOwner = new SceneNode { Type = "Script", Name = "ErrorOwner" };
project.ServerScriptService.Children.Add(errorOwner);
project.Scripts.Add(new GameScript { Id = errorOwner.Id, Name = errorOwner.Name, Source = "game.ReplicatedStorage.AskServer.OnInvoke = function() error('intentional callback failure') end" });
var errorCaller = new SceneNode { Type = "Script", Name = "ErrorCaller" };
project.ServerScriptService.Children.Add(errorCaller);
project.Scripts.Add(new GameScript
{
    Id = errorCaller.Id,
    Name = errorCaller.Name,
    Source = "local ok = pcall(function() game.ReplicatedStorage.AskServer:Invoke() end); print('invoke-error-caught', ok); task.delay(0.05, function() print('post-error-task') end); game.ServerScriptService.ErrorOwner.Enabled = false"
});
var parentModule = new SceneNode { Type = "ModuleScript", Name = "ParentModule" };
project.ReplicatedStorage.Children.Add(parentModule);
var childModule = new SceneNode { Type = "ModuleScript", Name = "ChildModule" };
parentModule.Children.Add(childModule);
project.Scripts.Add(new GameScript { Id = parentModule.Id, Name = parentModule.Name, Source = "return require('@self/ChildModule')" });
project.Scripts.Add(new GameScript { Id = childModule.Id, Name = childModule.Name, Source = "return { Answer = 99 }" });

var clone = GameProjectStore.CloneSubtree(signal, out _)
    ?? throw new Exception("BindableEvent did not clone.");
if (clone.Type != "BindableEvent" || clone.Id == signal.Id || clone.Children.Single().Id == nestedValue.Id)
    throw new Exception("BindableEvent clone did not copy children with new IDs.");

var json = JsonSerializer.Serialize(project, GameProjectStore.JsonOptions);
var restored = JsonSerializer.Deserialize<GameProject>(json, GameProjectStore.JsonOptions)
    ?? throw new Exception("BindableEvent project did not deserialize.");
GameProjectStore.EnsureProject(restored);
if (restored.ReplicatedStorage.Children.Single(node => node.Name == "RoundStarted").Children.Single().NumberValue != 3
    || restored.ReplicatedStorage.Children.Single(node => node.Type == "BindableFunction").Name != "AskServer"
    || restored.Scripts.Single(script => script.Id == moduleNode.Id).Source.Contains("module-ran", StringComparison.Ordinal) == false
    || restored.Gui.Children.Single().Children.Count != 2)
    throw new Exception("BindableEvent did not survive a project round trip.");

var output = new List<string>();
var runtime = new LuaXRuntime(project, output: output.Add);
runtime.Run(moduleNode.Id);
if (output.Any(line => line.Contains("module-ran", StringComparison.Ordinal)))
    throw new Exception("ModuleScript ran without require.");

GameScript Run(string source)
{
    var gameScript = new GameScript { Source = source };
    project.Scripts.Add(gameScript);
    runtime.Run(gameScript.Id);
    return gameScript;
}

Run("""
    local signal = game.ReplicatedStorage.RoundStarted
    local connection = signal.Event:Connect(function(number, text)
        print('heard', number, text)
    end)
    print('class', signal:IsA('BindableEvent'), typeof(signal.Event), typeof(connection), connection.Connected)
    signal:Fire(4, 'go')
    print('after-fire')
    """);
var afterFire = output.FindIndex(line => line.Contains("after-fire", StringComparison.Ordinal));
var heard = output.FindIndex(line => line.Contains("heard\t4\tgo", StringComparison.Ordinal));
if (afterFire < 0 || heard <= afterFire || !output.Any(line => line.Contains("class\ttrue\tRBXScriptSignal\tRBXScriptConnection\ttrue", StringComparison.Ordinal)))
    throw new Exception("Fire blocked the caller or lost its arguments: " + string.Join(" | ", output));

Run("""
    local signal = game.ReplicatedStorage.RoundStarted
    local connection = signal.Event:Connect(function() print('disconnected-callback-ran') end)
    connection:Disconnect()
    print('connection-state', connection.Connected)
    signal:Fire(8, 'next')
    """);
if (output.Any(line => line.Contains("disconnected-callback-ran", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("connection-state\tfalse", StringComparison.Ordinal))
    || output.Count(line => line.Contains("heard\t8\tnext", StringComparison.Ordinal)) != 1)
    throw new Exception("Disconnect failed: " + string.Join(" | ", output));

Run("""
    local signal = game.ReplicatedStorage.RoundStarted
    signal.Event:Connect(function() error('bad listener') end)
    signal.Event:Connect(function() print('survived-error') end)
    signal:Fire()
    print('after-error-fire')
    """);
if (!output.Any(line => line.Contains("survived-error", StringComparison.Ordinal))
    || output.FindIndex(line => line.Contains("after-error-fire", StringComparison.Ordinal))
        >= output.FindIndex(line => line.Contains("survived-error", StringComparison.Ordinal)))
    throw new Exception("One failing listener stopped another: " + string.Join(" | ", output));

Run("""
    local signal = Instance.new('BindableEvent', workspace)
    signal.Name = 'DynamicSignal'
    signal.Event:Connect(function(value) print('dynamic-event', value) end)
    signal:Fire(12)
    """);
if (!output.Any(line => line.Contains("dynamic-event\t12", StringComparison.Ordinal))
    || project.Workspace.Children.All(node => node.Type != "BindableEvent" || node.Name != "DynamicSignal"))
    throw new Exception("Instance.new BindableEvent failed: " + string.Join(" | ", output));

Run("""
    local callback = game.ReplicatedStorage.AskServer
    callback.OnInvoke = function(a, b)
        task.wait(0.1)
        return a + b, 'ready'
    end
    """);
Run("""
    local callback = game.ReplicatedStorage.AskServer
    local result, state = callback:Invoke(2, 5)
    print('invoke-result', result, state)
    """);
if (output.Any(line => line.Contains("invoke-result", StringComparison.Ordinal)))
    throw new Exception("BindableFunction did not yield for its callback.");
runtime.Update(0.05f);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("invoke-result\t7\tready", StringComparison.Ordinal)))
    throw new Exception("BindableFunction did not return its callback values: " + string.Join(" | ", output));

Run("""
    local callback = Instance.new('BindableFunction', workspace)
    callback.Name = 'LateCallback'
    task.defer(function()
        callback.OnInvoke = function(value) return value * 3 end
    end)
    print('late-invoke', callback:Invoke(4))
    """);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("late-invoke\t12", StringComparison.Ordinal)))
    throw new Exception("BindableFunction did not wait for a callback: " + string.Join(" | ", output));

Run("""
    local callback = game.ReplicatedStorage.AskServer
    SetProperty(callback, 'OnInvoke', function(value) return value + 10 end)
    print('replaced-invoke', GetProperty(callback, 'OnInvoke') == nil, callback.OnInvoke == nil, callback:FindFirstChild('OnInvoke') ~= nil, callback:Invoke(3))
    """);
if (!output.Any(line => line.Contains("replaced-invoke\ttrue\ttrue\ttrue\t13", StringComparison.Ordinal)))
    throw new Exception("BindableFunction callback replacement failed: " + string.Join(" | ", output));

Run("""
    local callback = game.ReplicatedStorage.AskServer
    callback.OnInvoke = function(value) return value * 2 end
    callback.OnInvoke = function(value) return value * 4 end
    callback.CustomField = 'kept'
    print('direct-replacement', callback.OnInvoke == nil, callback:Invoke(3), callback.CustomField)
    """);
if (!output.Any(line => line.Contains("direct-replacement\ttrue\t12\tkept", StringComparison.Ordinal)))
    throw new Exception("BindableFunction direct callback assignment failed: " + string.Join(" | ", output));

var retiredOwner = Run("""
    local callback = Instance.new('BindableFunction', workspace)
    callback.Name = 'RetiredCallback'
    callback.OnInvoke = function() return 'stale' end
    """);
project.Scripts.Remove(retiredOwner);
Run("""
    print('retired-result', workspace.RetiredCallback:Invoke())
    """);
runtime.Update(0.05f);
if (output.Any(line => line.Contains("retired-result", StringComparison.Ordinal)))
    throw new Exception("A removed script left its BindableFunction callback active.");
Run("""
    workspace.RetiredCallback.OnInvoke = function() return 'fresh' end
    """);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("retired-result\tfresh", StringComparison.Ordinal)))
    throw new Exception("A replacement BindableFunction callback did not resume the caller: " + string.Join(" | ", output));

Run("""
    local shared = require(game.ReplicatedStorage.SharedModule)
    print('module-first', shared.Count, shared.ParentName, game.ReplicatedStorage.SharedModule:IsA('LuaSourceContainer'))
    shared.Count = 7
    """);
Run("""
    local shared = require(game.ReplicatedStorage.SharedModule)
    print('module-second', shared.Count)
    """);
if (output.Count(line => line.Contains("module-ran", StringComparison.Ordinal)) != 1
    || !output.Any(line => line.Contains("module-first\t1\tReplicatedStorage\ttrue", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("module-second\t7", StringComparison.Ordinal)))
    throw new Exception("ModuleScript execution or caching failed: " + string.Join(" | ", output));

Run("""
    local clone = game.ReplicatedStorage.SharedModule:Clone()
    clone.Parent = workspace
    print('module-clone', require(clone).ParentName)
    """);
if (!output.Any(line => line.Contains("module-clone\tWorkspace", StringComparison.Ordinal)))
    throw new Exception("Cloned ModuleScript lost its source or parent context: " + string.Join(" | ", output));

Run("""
    print('module-yielded', require(game.ReplicatedStorage.YieldingModule))
    """);
if (output.Any(line => line.Contains("module-yielded", StringComparison.Ordinal)))
    throw new Exception("ModuleScript require did not yield.");
runtime.Update(0.05f);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("module-yielded\t42", StringComparison.Ordinal)))
    throw new Exception("Yielding ModuleScript did not return: " + string.Join(" | ", output));

Run("require(game.ReplicatedStorage.RecursiveModule)");
if (!output.Any(line => line.Contains("required recursively", StringComparison.Ordinal)))
    throw new Exception("Recursive ModuleScript require was not rejected: " + string.Join(" | ", output));

Run("print('concurrent-first', require(game.ReplicatedStorage.ConcurrentModule).Value)");
Run("print('concurrent-second', require(game.ReplicatedStorage.ConcurrentModule).Value)");
runtime.Update(0.05f);
runtime.Update(0.05f);
if (output.Count(line => line.Contains("concurrent-ran", StringComparison.Ordinal)) != 1
    || !output.Any(line => line.Contains("concurrent-first\t9", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("concurrent-second\t9", StringComparison.Ordinal)))
    throw new Exception("Concurrent ModuleScript callers did not share one execution: " + string.Join(" | ", output));

Run("""
    local module = Instance.new('ModuleScript', workspace)
    print('dynamic-module', typeof(module), type(require(module)))
    """);
if (!output.Any(line => line.Contains("dynamic-module\tModuleScript\ttable", StringComparison.Ordinal)))
    throw new Exception("Instance.new ModuleScript failed: " + string.Join(" | ", output));

runtime.Run(relativeScript.Id);
runtime.Run(parentPathScript.Id);
runtime.Run(servicePathScript.Id);
Run("print('self-result', require(game.ReplicatedStorage.ParentModule).Answer)");
Run("print('game-path-result', require('@game/ServerScriptService/Systems/Sibling').Number)");
Run("print('game-parent-result', require('@game/ServerScriptService/../ReplicatedStorage/SharedModule').Count)");
if (output.Count(line => line.Contains("relative-ran", StringComparison.Ordinal)) != 1
    || !output.Any(line => line.Contains("relative-result\t17\tRelativeCaller", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("parent-result\t17\tParentCaller", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("service-result\t7", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("self-result\t99", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("game-path-result\t17", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("game-parent-result\t7", StringComparison.Ordinal)))
    throw new Exception("ModuleScript path resolution failed: " + string.Join(" | ", output));

runtime.Run(callbackOwnerScript.Id);
runtime.Run(callbackCallerScript.Id);
if (!output.Any(line => line.Contains("callback-script\tCallbackOwner\tCallbackCaller", StringComparison.Ordinal)))
    throw new Exception("Scripts shared the wrong script global during a callback: " + string.Join(" | ", output));

runtime.Run(valueListenerScript.Id);
runtime.Run(ownershipScript.Id);
runtime.Run(ownershipCaller.Id);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("ownership-result\towned", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("value-listener", StringComparison.Ordinal)))
    throw new Exception("Yielding BindableFunction callback did not complete: " + string.Join(" | ", output));
runtime.Run(ownershipDisabler.Id);
Run("game.ReplicatedStorage.OwnershipSignal:Fire()");
runtime.Update(0.25f);
if (output.Any(line => line.Contains("stale-owned-event", StringComparison.Ordinal)
    || line.Contains("stale-owned-task", StringComparison.Ordinal)))
    throw new Exception("A stopped callback owner left events or tasks active: " + string.Join(" | ", output));

runtime.Run(errorOwner.Id);
runtime.Run(errorCaller.Id);
runtime.Update(0.05f);
if (!output.Any(line => line.Contains("invoke-error-caught\tfalse", StringComparison.Ordinal))
    || !output.Any(line => line.Contains("post-error-task", StringComparison.Ordinal)))
    throw new Exception("Callback errors did not restore the caller's task ownership: " + string.Join(" | ", output));

Run("""
    local part = Instance.new('Part', workspace)
    part.Position = Vector3.new(1, 2, 3)
    part.Rotation = LookAtRotation(part.Position, Vector3.new(2, 2, 3))
    print('rotation-only', CFrame == nil, part.CFrame == nil, part.Position.X, part.Rotation.Y)
    """);
if (!output.Any(line => line.Contains("rotation-only\ttrue\ttrue\t1\t-90", StringComparison.Ordinal)))
    throw new Exception("Position, Rotation, or LookAtRotation regressed: " + string.Join(" | ", output));

static SceneNode RotatedBarrier() => new()
{
    Type = "Part", Name = "RotatedBarrier", Anchored = true,
    Position = [0, 0, 0], Size = [10, 1, 1], Rotation = [0, 45, 0]
};

var clearProject = new GameProject();
clearProject.Workspace.Gravity = 0;
clearProject.Workspace.Children.Add(RotatedBarrier());
var clearProbe = new SceneNode
{
    Type = "Part", Name = "ClearProbe", Anchored = false,
    Position = [3, 0, 0], Size = [.5f, .5f, .5f], Velocity = [50, 0, 0]
};
clearProject.Workspace.Children.Add(clearProbe);
new PhysicsSimulationService(clearProject, _ => { }).Step(.02f);
if (clearProbe.Position[0] < 3.99f)
    throw new Exception($"Rotated part blocked a clear path: {clearProbe.Position[0]}");

var hitProject = new GameProject();
hitProject.Workspace.Gravity = 0;
hitProject.Workspace.Children.Add(RotatedBarrier());
var hitProbe = new SceneNode
{
    Type = "Part", Name = "HitProbe", Anchored = false,
    Position = [3, 0, -5], Size = [.5f, .5f, .5f], Velocity = [0, 0, 200]
};
hitProject.Workspace.Children.Add(hitProbe);
new PhysicsSimulationService(hitProject, _ => { }).Step(.02f);
if (hitProbe.Position[2] > -3.5f || hitProbe.Velocity[2] != 0)
    throw new Exception($"Rotated part failed to stop a crossing probe: {hitProbe.Position[2]}");

var touchProject = new GameProject();
touchProject.Workspace.Children.Add(RotatedBarrier());
var touchProbe = new SceneNode
{
    Type = "Part", Name = "TouchProbe", Position = [3, 0, 0], Size = [.5f, .5f, .5f]
};
touchProject.Workspace.Children.Add(touchProbe);
var touchOutput = new List<string>();
var touchRuntime = new LuaXRuntime(touchProject, output: touchOutput.Add);
var touchScript = new GameScript { Source = "workspace.RotatedBarrier.Touched:Connect(function(hit) print('rotated-touch', hit.Name) end)" };
touchProject.Scripts.Add(touchScript);
touchRuntime.Run(touchScript.Id);
touchRuntime.UpdateTouched();
if (touchOutput.Any(line => line.Contains("rotated-touch", StringComparison.Ordinal)))
    throw new Exception("Rotated Touched reported a false contact.");
var moveTouchScript = new GameScript { Source = "workspace.TouchProbe.Position = Vector3.new(3, 0, -3)" };
touchProject.Scripts.Add(moveTouchScript);
touchRuntime.Run(moveTouchScript.Id);
touchRuntime.UpdateTouched();
if (!touchOutput.Any(line => line.Contains("rotated-touch\tTouchProbe", StringComparison.Ordinal)))
    throw new Exception("Rotated Touched missed a real contact: " + string.Join(" | ", touchOutput));

var floorProject = new GameProject();
floorProject.Workspace.Gravity = 0;
floorProject.Workspace.Children.Add(new SceneNode
{
    Type = "Part", Name = "Floor", Position = [0, 0, 0], Size = [10, 1, 10]
});
var falling = new SceneNode
{
    Type = "Part", Name = "Falling", Anchored = false,
    Position = [0, 2, 0], Size = [1, 1, 1], Velocity = [0, -100, 0]
};
floorProject.Workspace.Children.Add(falling);
var floorPhysics = new PhysicsSimulationService(floorProject, _ => { });
floorPhysics.Step(.02f);
if (Math.Abs(falling.Position[1] - 1) > .001f || falling.Velocity[1] != 0 || !floorPhysics.IsGrounded(falling))
    throw new Exception($"Ordinary floor collision regressed: {falling.Position[1]}");

var weldProject = new GameProject();
weldProject.Workspace.Gravity = 0;
var weldAnchor = new SceneNode { Type = "Part", Name = "WeldAnchor", Anchored = true, CanCollide = false, Position = [0, 0, 0] };
var weldChild = new SceneNode { Type = "Part", Name = "WeldChild", Anchored = false, CanCollide = false, Position = [2, 0, 0] };
var weldGrandchild = new SceneNode { Type = "Part", Name = "WeldGrandchild", Anchored = false, CanCollide = false, Position = [2, 1, 2], Rotation = [0, 30, 0] };
weldProject.Workspace.Children.Add(weldAnchor);
weldProject.Workspace.Children.Add(weldChild);
weldProject.Workspace.Children.Add(weldGrandchild);
weldProject.Workspace.Children.Add(new SceneNode { Type = "WeldConstraint", Part0 = weldAnchor.Id, Part1 = weldChild.Id });
weldProject.Workspace.Children.Add(new SceneNode { Type = "WeldConstraint", Part0 = weldChild.Id, Part1 = weldGrandchild.Id });
var weldPhysics = new PhysicsSimulationService(weldProject, _ => { });
weldPhysics.Step(.02f);
weldAnchor.Position = [3, 0, 0];
weldAnchor.Rotation = [0, 90, 0];
weldPhysics.Step(.02f);
if (Math.Abs(weldChild.Position[0] - 3) > .001f || Math.Abs(weldChild.Position[2] + 2) > .001f
    || Math.Abs(weldChild.Rotation[1] - 90) > .001f || Math.Abs(weldGrandchild.Position[0] - 5) > .001f
    || Math.Abs(weldGrandchild.Position[1] - 1) > .001f || Math.Abs(weldGrandchild.Position[2] + 2) > .001f
    || Math.Abs(weldGrandchild.Rotation[1] - 120) > .001f)
    throw new Exception("Welded parts did not follow their rotated anchor.");
weldChild.Position = [99, 99, 99];
weldChild.Rotation = [0, 0, 0];
weldPhysics.Step(.02f);
if (Math.Abs(weldChild.Position[0] - 3) > .001f || Math.Abs(weldChild.Position[2] + 2) > .001f
    || Math.Abs(weldChild.Rotation[1] - 90) > .001f)
    throw new Exception($"An anchored weld did not restore an edited child transform: ({string.Join(',', weldChild.Position)}) ({string.Join(',', weldChild.Rotation)}).");
weldAnchor.Rotation = [90, 90, 0];
weldPhysics.Step(.02f);
if (Math.Abs(weldGrandchild.Position[0] - 4) > .001f || Math.Abs(weldGrandchild.Position[1] + 2) > .001f
    || Math.Abs(weldGrandchild.Position[2] + 2) > .001f || Math.Abs(weldChild.Rotation[0] - 90) > .001f)
    throw new Exception("Welded parts did not follow a pitch rotation.");
var looseWeldProject = new GameProject();
looseWeldProject.Workspace.Gravity = 0;
var looseFirst = new SceneNode { Type = "Part", Anchored = false, CanCollide = false, Position = [0, 0, 0] };
var looseSecond = new SceneNode { Type = "Part", Anchored = false, CanCollide = false, Position = [2, 0, 0] };
looseWeldProject.Workspace.Children.Add(looseFirst);
looseWeldProject.Workspace.Children.Add(looseSecond);
looseWeldProject.Workspace.Children.Add(new SceneNode { Type = "WeldConstraint", Part0 = looseFirst.Id, Part1 = looseSecond.Id });
var looseWeldPhysics = new PhysicsSimulationService(looseWeldProject, _ => { });
looseWeldPhysics.Step(.02f);
looseSecond.Rotation = [0, 90, 0];
looseWeldPhysics.Step(.02f);
if (Math.Abs(looseFirst.Position[0] - 2) > .001f || Math.Abs(looseFirst.Position[2] - 2) > .001f
    || Math.Abs(looseFirst.Rotation[1] - 90) > .001f)
    throw new Exception("A free weld assembly did not rotate around the edited member.");

var seatProject = new GameProject();
var rotatedSeat = new SceneNode
{
    Type = "Seat", Name = "RotatedSeat", Position = [0, 0, 0],
    Size = [4, .5f, 1], Rotation = [0, 45, 0]
};
seatProject.Workspace.Children.Add(rotatedSeat);
var seatedCharacter = new PlayerCharacterService(seatProject);
var seatInteraction = new SeatInteractionService(seatProject, seatedCharacter);
seatedCharacter.Root.Position = [2.3f, 2.05f, 0];
seatInteraction.AfterPhysics();
if (seatInteraction.IsSeated)
    throw new Exception("Rotated Seat claimed a character outside its footprint.");
seatedCharacter.Root.Position = [0, 2.05f, 0];
seatInteraction.AfterPhysics();
if (!seatInteraction.IsSeated || rotatedSeat.Occupant != seatedCharacter.Humanoid.Id)
    throw new Exception("Rotated Seat failed to accept a character on its surface.");
rotatedSeat.Rotation = [30, 0, 0];
seatInteraction.AfterPhysics();
if (seatedCharacter.Root.Position[1] <= 1.52f || seatedCharacter.Root.Position[1] >= 1.6f)
    throw new Exception("Character sank into a tilted Seat.");
seatInteraction.BeforePhysics(true);
if (seatInteraction.IsSeated || seatedCharacter.Root.Position[1] <= 2.08f)
    throw new Exception("Jumping failed to leave the rotated Seat.");

var emptySpawnProject = new GameProject();
emptySpawnProject.Workspace.Children.Add(new SceneNode
{
    Type = "Part", Name = "RotatedDistantGround", Position = [2, 0, 0],
    Size = [6, 1, 1], Rotation = [0, 45, 0]
});
var emptySpawnCharacter = new PlayerCharacterService(emptySpawnProject);
if (Math.Abs(emptySpawnCharacter.Root.Position[1] - 1.801f) > .001f)
    throw new Exception("Character spawned above empty space beside a rotated part.");

var tiltedSpawnProject = new GameProject();
tiltedSpawnProject.Workspace.Children.Add(new SceneNode
{
    Type = "Part", Name = "TiltedGround", Position = [0, 0, 0],
    Size = [4, 1, 4], Rotation = [30, 0, 0]
});
var tiltedSpawnCharacter = new PlayerCharacterService(tiltedSpawnProject);
if (tiltedSpawnCharacter.Root.Position[1] < 2.37f || tiltedSpawnCharacter.Root.Position[1] > 2.39f)
    throw new Exception($"Character spawned at the wrong height over tilted ground: {tiltedSpawnCharacter.Root.Position[1]}");

var gradientProject = new GameProject();
var gradientScreen = gradientProject.Gui.Children.Single(node => node.Type == "ScreenGui");
var gradientFrame = new SceneNode { Type = "Frame", Name = "GradientFrame", Size = [100, 40, 2], Color = "#224466" };
gradientScreen.Children.Add(gradientFrame);
if (!GameProjectStore.CanParentNode(gradientProject, new SceneNode { Type = "UIGradient" }, gradientFrame)
    || GameProjectStore.CanParentNode(gradientProject, new SceneNode { Type = "UIGradient" }, gradientProject.Workspace))
    throw new Exception("UIGradient parenting rules are wrong.");
var gradientOutput = new List<string>();
var gradientRuntime = new LuaXRuntime(gradientProject, output: gradientOutput.Add);
var gradientScript = new GameScript
{
    Source = """
        local frame = game.Gui.ScreenGui.GradientFrame
        local g = Instance.new('UIGradient', frame)
        g.StartColor = Color3.fromRGB(255, 0, 0)
        g.EndColor = Color3.fromRGB(0, 0, 255)
        g.Rotation = Vector3.new(0, 0, 90)
        frame.BackgroundTransparency = 0.5
        local label = Instance.new('TextLabel', frame)
        label.TextTransparency = 0.25
        local image = Instance.new('ImageLabel', frame)
        image.ImageTransparency = 0.75
        label.TextScaled = true
        local textLimit = Instance.new('UITextSizeConstraint', label)
        textLimit.MinTextSize = 12
        textLimit.MaxTextSize = 60
        local sizeLimit = Instance.new('UISizeConstraint', frame)
        sizeLimit.MinSize = Vector2.new(20, 20)
        sizeLimit.MaxSize = Vector2.new(200, 200)
        print('gradient-values', g.StartColor:ToHex(), g.EndColor:ToHex(), g.Rotation.Z)
        print('gui-alpha', frame.BackgroundTransparency, label.TextTransparency, image.ImageTransparency)
        print('size-limit', sizeLimit:IsA('UIComponent'), sizeLimit.MinSize.X, sizeLimit.MaxSize.Y)
        print('text-limit', label.TextScaled, textLimit:IsA('UIComponent'), textLimit.MinTextSize, textLimit.MaxTextSize)
        """
};
gradientProject.Scripts.Add(gradientScript);
gradientRuntime.Run(gradientScript.Id);
var gradientNode = gradientFrame.Children.SingleOrDefault(node => node.Type == "UIGradient");
if (gradientNode is null || gradientNode.StartColor != "#FF0000" || gradientNode.EndColor != "#0000FF" || gradientNode.Rotation[2] != 90
    || gradientFrame.BackgroundTransparency != .5f
    || !gradientOutput.Any(line => line.Contains("gradient-values\tFF0000\t0000FF\t90", StringComparison.Ordinal))
    || !gradientOutput.Any(line => line.Contains("gui-alpha\t0.5\t0.25\t0.75", StringComparison.Ordinal))
    || !gradientOutput.Any(line => line.Contains("size-limit\ttrue\t20\t200", StringComparison.Ordinal))
    || !gradientOutput.Any(line => line.Contains("text-limit\ttrue\ttrue\t12\t60", StringComparison.Ordinal)))
    throw new Exception("UIGradient Lua X properties failed: " + string.Join(" | ", gradientOutput));
var gradientJson = JsonSerializer.Serialize(gradientProject, GameProjectStore.JsonOptions);
var restoredGradientProject = JsonSerializer.Deserialize<GameProject>(gradientJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("UIGradient project did not deserialize.");
GameProjectStore.EnsureProject(restoredGradientProject);
var restoredSizeLimit = restoredGradientProject.Gui.Children.Single(node => node.Type == "ScreenGui")
    .Children.Single(node => node.Name == "GradientFrame").Children.Single(node => node.Type == "UISizeConstraint");
if (!restoredSizeLimit.MinSize.SequenceEqual(new float[] { 20, 20 })
    || !restoredSizeLimit.MaxSize.SequenceEqual(new float[] { 200, 200 }))
    throw new Exception("UISizeConstraint did not survive project serialization.");
var restoredTextLabel = restoredGradientProject.Gui.Children.Single(node => node.Type == "ScreenGui")
    .Children.Single(node => node.Name == "GradientFrame").Children.Single(node => node.Type == "TextLabel");
var restoredTextLimit = restoredTextLabel.Children.Single(node => node.Type == "UITextSizeConstraint");
if (!restoredTextLabel.TextScaled || restoredTextLimit.MinTextSize != 12 || restoredTextLimit.MaxTextSize != 60)
    throw new Exception("UITextSizeConstraint and TextScaled did not survive project serialization.");
var gradientAssetRoot = Path.Combine(Path.GetTempPath(), "BullerStudioGradientSmoke_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(gradientAssetRoot, "assets"));
restoredGradientProject.ProjectPath = gradientAssetRoot;
Exception? gradientRenderError = null;
var renderThread = new Thread(() =>
{
    try
    {
        var canvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        var frameBorder = (System.Windows.Controls.Border)canvas.Children[0];
        var content = (System.Windows.Controls.Grid)frameBorder.Child;
        var visual = (System.Windows.Controls.Border)content.Children[0];
        var brush = visual.Background as System.Windows.Media.LinearGradientBrush;
        if (brush is null || brush.GradientStops[0].Color != System.Windows.Media.Color.FromArgb(128, 0x22, 0, 0)
            || brush.GradientStops[1].Color != System.Windows.Media.Color.FromArgb(128, 0, 0, 0x66)
            || brush.GradientStops[0].Color.A != 128 || brush.GradientStops[1].Color.A != 128
            || Math.Abs(brush.StartPoint.X - .5) > .001 || Math.Abs(brush.StartPoint.Y) > .001)
            throw new Exception("UIGradient did not render the saved colors and rotation.");
        var restoredGradient = restoredGradientProject.Gui.Children.Single(node => node.Type == "ScreenGui")
            .Children.Single(node => node.Name == "GradientFrame").Children.Single(node => node.Type == "UIGradient");
        restoredGradient.Enabled = false;
        var plainCanvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        var plainFrame = (System.Windows.Controls.Border)plainCanvas.Children[0];
        var plainVisual = (System.Windows.Controls.Border)((System.Windows.Controls.Grid)plainFrame.Child).Children[0];
        if (plainVisual.Background is not System.Windows.Media.SolidColorBrush plainBrush
            || plainBrush.Color != System.Windows.Media.Color.FromArgb(128, 0x22, 0x44, 0x66))
            throw new Exception("Disabling UIGradient did not restore the frame background.");

        var assets = Path.Combine(gradientAssetRoot, "assets");
        var pixels = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 };
        var source = System.Windows.Media.Imaging.BitmapSource.Create(2, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using (var file = File.Create(Path.Combine(assets, "gradient-test.png"))) encoder.Save(file);
        var screenNode = restoredGradientProject.Gui.Children.Single(node => node.Type == "ScreenGui");
        static SceneNode Gradient(string start, string end, float rotation = 0) => new()
            { Type = "UIGradient", StartColor = start, EndColor = end, Rotation = [0, 0, rotation] };
        var textLabel = new SceneNode { Type = "TextLabel", Name = "GradientText", Text = "Color", TextColor = "#80FFFF", Color = "#FFFFFF", TextTransparency = .5f, BackgroundTransparency = .25f };
        textLabel.Children.Add(Gradient("#FF0000", "#0000FF", 90));
        var textButton = new SceneNode { Type = "TextButton", Name = "GradientButton", Text = "Click", TextColor = "#FFFFFF", Color = "#FFFFFF" };
        textButton.Children.Add(Gradient("#FF0000", "#0000FF"));
        var imageLabel = new SceneNode { Type = "ImageLabel", Name = "GradientImage", Asset = "assets/gradient-test.png", Color = "#FFFFFF", ImageTransparency = .25f, BackgroundTransparency = .5f };
        var imageGradient = Gradient("#FF0000", "#0000FF");
        imageLabel.Children.Add(imageGradient);
        var imageButton = new SceneNode { Type = "ImageButton", Name = "GradientImageButton", Asset = "assets/gradient-test.png", Color = "#FFFFFF" };
        imageButton.Children.Add(Gradient("#FF0000", "#0000FF"));
        var unsupportedTextBox = new SceneNode { Type = "TextBox", Name = "GradientTextBox", TextColor = "#FFFFFF", TextTransparency = .5f };
        unsupportedTextBox.Children.Add(Gradient("#FF0000", "#0000FF"));
        screenNode.Children.Add(textLabel); screenNode.Children.Add(textButton); screenNode.Children.Add(imageLabel);
        screenNode.Children.Add(imageButton); screenNode.Children.Add(unsupportedTextBox);
        var gradientCanvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        static System.Windows.FrameworkElement OwnVisual(System.Windows.Controls.Canvas root, string name)
        {
            var border = root.Children.OfType<System.Windows.Controls.Border>()
                .Single(element => (element.Tag as SceneNode)?.Name == name);
            return (System.Windows.FrameworkElement)((System.Windows.Controls.Grid)border.Child).Children[0];
        }
        var labelVisual = (System.Windows.Controls.TextBlock)OwnVisual(gradientCanvas, "GradientText");
        if (labelVisual.Foreground is not System.Windows.Media.LinearGradientBrush textBrush
            || textBrush.GradientStops[0].Color != System.Windows.Media.Color.FromArgb(128, 0x80, 0, 0)
            || textBrush.GradientStops[1].Color != System.Windows.Media.Color.FromArgb(128, 0, 0, 0xFF)
            || textBrush.GradientStops[0].Color.A != 128
            || labelVisual.Background is not System.Windows.Media.LinearGradientBrush labelBackground
            || labelBackground.GradientStops[0].Color.A != 191)
            throw new Exception("UIGradient did not tint the TextLabel foreground with TextColor.");
        var buttonVisual = (System.Windows.Controls.Button)OwnVisual(gradientCanvas, "GradientButton");
        if (buttonVisual.Foreground is not System.Windows.Media.LinearGradientBrush)
            throw new Exception("UIGradient did not tint the TextButton foreground.");
        if (OwnVisual(gradientCanvas, "GradientTextBox") is not System.Windows.Controls.TextBox textBoxVisual
            || textBoxVisual.Foreground is not System.Windows.Media.SolidColorBrush textBoxBrush || textBoxBrush.Color.A != 128)
            throw new Exception("UIGradient unexpectedly tinted an unsupported TextBox.");
        var imageGrid = (System.Windows.Controls.Grid)OwnVisual(gradientCanvas, "GradientImage");
        var imageVisual = (System.Windows.Controls.Image)imageGrid.Children[0];
        var imageBitmap = (System.Windows.Media.Imaging.BitmapSource)imageVisual.Source;
        var tintedPixels = new byte[8];
        imageBitmap.CopyPixels(tintedPixels, 8, 0);
        if (tintedPixels[2] <= tintedPixels[0] || tintedPixels[4] <= tintedPixels[6]
            || tintedPixels[3] != 191 || tintedPixels[7] != 191
            || imageGrid.Background is not System.Windows.Media.LinearGradientBrush imageBackground
            || imageBackground.GradientStops[0].Color.A != 128)
            throw new Exception("UIGradient did not tint image pixels across their width.");
        var imageButtonVisual = (System.Windows.Controls.Button)OwnVisual(gradientCanvas, "GradientImageButton");
        var buttonImage = (System.Windows.Controls.Image)imageButtonVisual.Content;
        if (buttonImage.Source is not System.Windows.Media.Imaging.BitmapSource buttonBitmap
            || buttonBitmap.PixelWidth != 2 || imageButtonVisual.Background is not System.Windows.Media.LinearGradientBrush)
            throw new Exception("UIGradient did not render on ImageButton image and background.");
        imageGradient.Enabled = false;
        var disabledImage = (System.Windows.Controls.Image)((System.Windows.Controls.Grid)OwnVisual(
            GuiRenderService.Render(restoredGradientProject, false, 200, 100), "GradientImage")).Children[0];
        var disabledPixels = new byte[8];
        ((System.Windows.Media.Imaging.BitmapSource)disabledImage.Source).CopyPixels(disabledPixels, 8, 0);
        if (disabledPixels[0] != 255 || disabledPixels[2] != 255 || disabledPixels[4] != 255 || disabledPixels[6] != 255)
            throw new Exception("Disabling UIGradient did not restore image pixels.");

        var constrainedFrame = new SceneNode { Type = "Frame", Name = "ConstrainedFrame", Size = [20, 20, 2], SizeScale = [.5f, .5f] };
        var sizeConstraint = new SceneNode { Type = "UISizeConstraint", Name = "SizeLimit", MinSize = [120, 80], MaxSize = [180, 140] };
        screenNode.Children.Add(constrainedFrame);
        if (!GameProjectStore.CanParentNode(restoredGradientProject, sizeConstraint, constrainedFrame)
            || GameProjectStore.CanParentNode(restoredGradientProject, sizeConstraint, screenNode))
            throw new Exception("UISizeConstraint parenting rules are wrong.");
        constrainedFrame.Children.Add(sizeConstraint);
        var smallCanvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        var smallFrame = smallCanvas.Children.OfType<System.Windows.Controls.Border>()
            .Single(element => (element.Tag as SceneNode)?.Name == "ConstrainedFrame");
        var largeCanvas = GuiRenderService.Render(restoredGradientProject, false, 1000, 1000);
        var largeFrame = largeCanvas.Children.OfType<System.Windows.Controls.Border>()
            .Single(element => (element.Tag as SceneNode)?.Name == "ConstrainedFrame");
        if (smallFrame.Width != 120 || smallFrame.Height != 80 || largeFrame.Width != 180 || largeFrame.Height != 140)
            throw new Exception("UISizeConstraint did not clamp the GUI size at different viewport dimensions.");

        var scrolling = new SceneNode { Type = "ScrollingFrame", Name = "ConstrainedScroll", Size = [100, 100, 2] };
        scrolling.Children.Add(new SceneNode { Type = "UIGridLayout", CellSize = [40, 40] });
        scrolling.Children.Add(new SceneNode { Type = "Frame", Name = "GridFirst" });
        var gridSecond = new SceneNode { Type = "Frame", Name = "GridSecond" };
        gridSecond.Children.Add(new SceneNode { Type = "UISizeConstraint", MinSize = [120, 80] });
        scrolling.Children.Add(gridSecond);
        screenNode.Children.Add(scrolling);
        var scrollCanvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        var scrollRoot = scrollCanvas.Children.OfType<System.Windows.Controls.Border>()
            .Single(element => (element.Tag as SceneNode)?.Name == "ConstrainedScroll");
        var scrollView = (System.Windows.Controls.ScrollViewer)((System.Windows.Controls.Grid)scrollRoot.Child).Children[1];
        var scrollContent = (System.Windows.Controls.Canvas)scrollView.Content;
        var secondVisual = scrollContent.Children.OfType<System.Windows.Controls.Border>()
            .Single(element => (element.Tag as SceneNode)?.Name == "GridSecond");
        if (secondVisual.Width != 120 || secondVisual.Height != 80 || scrollContent.Width < 160)
            throw new Exception("UISizeConstraint was clipped by grid scrolling bounds.");

        var fittedLabel = new SceneNode { Type = "TextLabel", Name = "FittedText", Text = "Hello", TextScaled = true, Size = [0, 0, 2], SizeScale = [.5f, .5f] };
        var textLimit = new SceneNode { Type = "UITextSizeConstraint", MinTextSize = 12, MaxTextSize = 60 };
        screenNode.Children.Add(fittedLabel);
        if (!GameProjectStore.CanParentNode(restoredGradientProject, textLimit, fittedLabel)
            || GameProjectStore.CanParentNode(restoredGradientProject, textLimit, constrainedFrame))
            throw new Exception("UITextSizeConstraint parenting rules are wrong.");
        fittedLabel.Children.Add(textLimit);
        var fittedSmall = (System.Windows.Controls.TextBlock)OwnVisual(
            GuiRenderService.Render(restoredGradientProject, false, 200, 100), "FittedText");
        var fittedLarge = (System.Windows.Controls.TextBlock)OwnVisual(
            GuiRenderService.Render(restoredGradientProject, false, 1000, 1000), "FittedText");
        if (fittedSmall.FontSize < 12 || fittedSmall.FontSize >= 60 || fittedLarge.FontSize < 59.9 || fittedLarge.FontSize > 60)
            throw new Exception($"TextScaled did not fit text within UITextSizeConstraint bounds: small={fittedSmall.FontSize}, large={fittedLarge.FontSize}.");
        fittedLabel.TextScaled = false;
        fittedLabel.TextSize = 90;
        var fixedText = (System.Windows.Controls.TextBlock)OwnVisual(
            GuiRenderService.Render(restoredGradientProject, false, 200, 100), "FittedText");
        if (fixedText.FontSize != 60)
            throw new Exception("UITextSizeConstraint did not clamp fixed TextSize.");
    }
    catch (Exception exception) { gradientRenderError = exception; }
});
renderThread.SetApartmentState(ApartmentState.STA);
renderThread.Start();
renderThread.Join();
if (Directory.Exists(gradientAssetRoot) && Path.GetFullPath(gradientAssetRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
    Directory.Delete(gradientAssetRoot, true);
if (gradientRenderError is not null) throw gradientRenderError;

var paddingProject = new GameProject();
var paddingScreen = paddingProject.Gui.Children.Single(node => node.Type == "ScreenGui");
var paddingFrame = new SceneNode { Type = "Frame", Name = "PaddingFrame", Size = [200, 100, 2] };
paddingScreen.Children.Add(paddingFrame);
paddingFrame.Children.Add(new SceneNode
{
    Type = "TextLabel", Name = "ScaledChild", Size = [0, 0, 2], SizeScale = [1, 1]
});
if (!GameProjectStore.CanParentNode(paddingProject, new SceneNode { Type = "UIPadding" }, paddingFrame)
    || GameProjectStore.CanParentNode(paddingProject, new SceneNode { Type = "UIPadding" }, paddingScreen))
    throw new Exception("UIPadding parenting rules are wrong.");
var paddingOutput = new List<string>();
var paddingRuntime = new LuaXRuntime(paddingProject, output: paddingOutput.Add);
var paddingScript = new GameScript
{
    Source = "local p = Instance.new('UIPadding', game.Gui.ScreenGui.PaddingFrame); p.PaddingLeft = UDim.new(0, 10); p.PaddingTop = UDim.new(0.1, 0); p.PaddingRight = UDim.new(0.1, 0); p.PaddingBottom = UDim.new(0, 20); print('padding-values', p.PaddingLeft.Offset, p.PaddingTop.Scale, p:IsA('UIComponent'))"
};
paddingProject.Scripts.Add(paddingScript);
paddingRuntime.Run(paddingScript.Id);
var paddingNode = paddingFrame.Children.SingleOrDefault(node => node.Type == "UIPadding");
if (paddingNode is null || !paddingNode.PaddingLeft.SequenceEqual(new float[] { 0, 10 })
    || !paddingNode.PaddingTop.SequenceEqual(new float[] { .1f, 0 })
    || !paddingOutput.Any(line => line.StartsWith("padding-values\t10\t0.1", StringComparison.Ordinal)
        && line.EndsWith("\ttrue", StringComparison.Ordinal)))
    throw new Exception("UIPadding Lua X properties failed: " + string.Join(" | ", paddingOutput));
var paddingJson = JsonSerializer.Serialize(paddingProject, GameProjectStore.JsonOptions);
var restoredPaddingProject = JsonSerializer.Deserialize<GameProject>(paddingJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("UIPadding project did not deserialize.");
GameProjectStore.EnsureProject(restoredPaddingProject);
Exception? paddingRenderError = null;
var paddingRenderThread = new Thread(() =>
{
    try
    {
        var canvas = GuiRenderService.Render(restoredPaddingProject, false, 300, 200);
        var frameBorder = (System.Windows.Controls.Border)canvas.Children[0];
        var content = (System.Windows.Controls.Grid)frameBorder.Child;
        var childCanvas = (System.Windows.Controls.Canvas)content.Children[1];
        var child = (System.Windows.Controls.Border)childCanvas.Children[0];
        if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(child) - 10) > .001
            || Math.Abs(System.Windows.Controls.Canvas.GetTop(child) - 10) > .001
            || Math.Abs(child.Width - 170) > .001 || Math.Abs(child.Height - 70) > .001)
            throw new Exception("UIPadding did not adjust child position and scale size.");
        var restoredFrame = restoredPaddingProject.Gui.Children.Single(node => node.Type == "ScreenGui")
            .Children.Single(node => node.Name == "PaddingFrame");
        restoredFrame.Children.Add(new SceneNode { Type = "TextLabel", Name = "SecondChild", Size = [30, 20, 2] });
        var list = new SceneNode { Type = "UIListLayout", LayoutDirection = "Horizontal" };
        restoredFrame.Children.Add(list);
        var listCanvas = GuiRenderService.Render(restoredPaddingProject, false, 300, 200);
        var listFrame = (System.Windows.Controls.Border)listCanvas.Children[0];
        var listChildren = (System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)listFrame.Child).Children[1];
        if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(listChildren.Children[1]) - 180) > .001)
            throw new Exception("UIPadding did not offset horizontal list layout.");
        restoredFrame.Children.Remove(list);
        restoredFrame.Children.Add(new SceneNode { Type = "UITableLayout", TableColumns = 2 });
        var tableCanvas = GuiRenderService.Render(restoredPaddingProject, false, 300, 200);
        var tableFrame = (System.Windows.Controls.Border)tableCanvas.Children[0];
        var tableChildren = (System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)tableFrame.Child).Children[1];
        var secondCell = (System.Windows.Controls.Border)tableChildren.Children[1];
        if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(secondCell) - 95) > .001 || Math.Abs(secondCell.Width - 85) > .001)
            throw new Exception("UIPadding did not reduce table layout columns.");
    }
    catch (Exception exception) { paddingRenderError = exception; }
});
paddingRenderThread.SetApartmentState(ApartmentState.STA);
paddingRenderThread.Start();
paddingRenderThread.Join();
if (paddingRenderError is not null) throw paddingRenderError;

var scaleProject = new GameProject();
var scaleScreen = scaleProject.Gui.Children.Single(node => node.Type == "ScreenGui");
var scaleContainer = new SceneNode { Type = "Frame", Name = "ScaleContainer", Size = [300, 100, 2] };
scaleScreen.Children.Add(scaleContainer);
var scaledLabel = new SceneNode { Type = "TextLabel", Name = "ScaledLabel", Size = [100, 40, 2], Rotation = [0, 0, 30] };
scaleContainer.Children.Add(scaledLabel);
scaleContainer.Children.Add(new SceneNode { Type = "TextLabel", Name = "FollowingLabel", Size = [40, 40, 2] });
scaleContainer.Children.Add(new SceneNode { Type = "UIListLayout", LayoutDirection = "Horizontal" });
if (!GameProjectStore.CanParentNode(scaleProject, new SceneNode { Type = "UIScale" }, scaledLabel)
    || GameProjectStore.CanParentNode(scaleProject, new SceneNode { Type = "UIScale" }, scaleScreen))
    throw new Exception("UIScale parenting rules are wrong.");
var scaleOutput = new List<string>();
var scaleRuntime = new LuaXRuntime(scaleProject, output: scaleOutput.Add);
var scaleScript = new GameScript
{
    Source = "local s = Instance.new('UIScale', game.Gui.ScreenGui.ScaleContainer.ScaledLabel); s.Scale = 2; print('scale-values', s.Scale, s:IsA('UIComponent'))"
};
scaleProject.Scripts.Add(scaleScript);
scaleRuntime.Run(scaleScript.Id);
if (scaledLabel.Children.SingleOrDefault(node => node.Type == "UIScale") is not { Scale: 2 }
    || !scaleOutput.Any(line => line.Contains("scale-values\t2\ttrue", StringComparison.Ordinal)))
    throw new Exception("UIScale Lua X properties failed: " + string.Join(" | ", scaleOutput));
var scaleJson = JsonSerializer.Serialize(scaleProject, GameProjectStore.JsonOptions);
var restoredScaleProject = JsonSerializer.Deserialize<GameProject>(scaleJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("UIScale project did not deserialize.");
GameProjectStore.EnsureProject(restoredScaleProject);
Exception? scaleRenderError = null;
var scaleRenderThread = new Thread(() =>
{
    try
    {
        var canvas = GuiRenderService.Render(restoredScaleProject, false, 400, 200);
        var container = (System.Windows.Controls.Border)canvas.Children[0];
        var children = (System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)container.Child).Children[1];
        var label = (System.Windows.Controls.Border)children.Children[0];
        var transform = label.RenderTransform as System.Windows.Media.TransformGroup;
        if (transform?.Children[0] is not System.Windows.Media.ScaleTransform visualScale
            || Math.Abs(visualScale.ScaleX - 2) > .001 || Math.Abs(visualScale.ScaleY - 2) > .001
            || transform.Children[1] is not System.Windows.Media.RotateTransform rotation
            || Math.Abs(rotation.Angle - 30) > .001 || Math.Abs(rotation.CenterX - 100) > .001
            || Math.Abs(System.Windows.Controls.Canvas.GetLeft(children.Children[1]) - 200) > .001)
            throw new Exception("UIScale did not scale and rotate the label or reserve list space.");
    }
    catch (Exception exception) { scaleRenderError = exception; }
});
scaleRenderThread.SetApartmentState(ApartmentState.STA);
scaleRenderThread.Start();
scaleRenderThread.Join();
if (scaleRenderError is not null) throw scaleRenderError;

var gridProject = new GameProject();
var gridScreen = gridProject.Gui.Children.Single(node => node.Type == "ScreenGui");
var gridFrame = new SceneNode { Type = "Frame", Name = "GridFrame", Size = [260, 180, 2] };
gridScreen.Children.Add(gridFrame);
for (var index = 0; index < 5; index++)
    gridFrame.Children.Add(new SceneNode { Type = "TextLabel", Name = $"Cell{index + 1}" });
gridFrame.Children.Add(new SceneNode
{
    Type = "UIPadding", PaddingLeft = [0, 10], PaddingTop = [0, 10],
    PaddingRight = [0, 10], PaddingBottom = [0, 10]
});
if (!GameProjectStore.CanParentNode(gridProject, new SceneNode { Type = "UIGridLayout" }, gridFrame)
    || GameProjectStore.CanParentNode(gridProject, new SceneNode { Type = "UIGridLayout" }, gridScreen))
    throw new Exception("UIGridLayout parenting rules are wrong.");
var gridOutput = new List<string>();
var gridRuntime = new LuaXRuntime(gridProject, output: gridOutput.Add);
var gridScript = new GameScript
{
    Source = "local g = Instance.new('UIGridLayout', game.Gui.ScreenGui.GridFrame); g.CellSize = UDim2.fromOffset(70, 40); g.CellPadding = UDim2.fromOffset(10, 5); g.FillDirection = Enum.FillDirection.Horizontal; g.FillDirectionMaxCells = 2; print('grid-values', g.CellSize.X.Offset, g.CellPadding.Y.Offset, g.FillDirection, g.FillDirectionMaxCells, g:IsA('UIComponent'))"
};
gridProject.Scripts.Add(gridScript);
gridRuntime.Run(gridScript.Id);
var gridNode = gridFrame.Children.SingleOrDefault(node => node.Type == "UIGridLayout");
if (gridNode is null || !gridNode.CellSize.SequenceEqual(new float[] { 70, 40 })
    || !gridNode.CellPadding.SequenceEqual(new float[] { 10, 5 })
    || gridNode.FillDirectionMaxCells != 2
    || !gridOutput.Any(line => line.Contains("grid-values\t70\t5\tHorizontal\t2\ttrue", StringComparison.Ordinal)))
    throw new Exception("UIGridLayout Lua X properties failed: " + string.Join(" | ", gridOutput));
var gridJson = JsonSerializer.Serialize(gridProject, GameProjectStore.JsonOptions);
var restoredGridProject = JsonSerializer.Deserialize<GameProject>(gridJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("UIGridLayout project did not deserialize.");
GameProjectStore.EnsureProject(restoredGridProject);
Exception? gridRenderError = null;
var gridRenderThread = new Thread(() =>
{
    try
    {
        static System.Windows.Controls.Canvas GridChildren(GameProject game)
        {
            var canvas = GuiRenderService.Render(game, false, 400, 300);
            var frame = (System.Windows.Controls.Border)canvas.Children[0];
            return (System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)frame.Child).Children[1];
        }
        static void CheckCell(System.Windows.Controls.Canvas canvas, int index, double x, double y)
        {
            var child = (System.Windows.Controls.Border)canvas.Children[index];
            if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(child) - x) > .001
                || Math.Abs(System.Windows.Controls.Canvas.GetTop(child) - y) > .001
                || Math.Abs(child.Width - 70) > .001 || Math.Abs(child.Height - 40) > .001)
                throw new Exception($"UIGridLayout cell {index + 1} has the wrong bounds.");
        }
        var horizontal = GridChildren(restoredGridProject);
        CheckCell(horizontal, 0, 10, 10);
        CheckCell(horizontal, 1, 90, 10);
        CheckCell(horizontal, 2, 10, 55);
        CheckCell(horizontal, 4, 10, 100);
        var restoredGrid = restoredGridProject.Gui.Children.Single(node => node.Type == "ScreenGui")
            .Children.Single(node => node.Name == "GridFrame").Children.Single(node => node.Type == "UIGridLayout");
        restoredGrid.FillDirection = "Vertical";
        var vertical = GridChildren(restoredGridProject);
        CheckCell(vertical, 1, 10, 55);
        CheckCell(vertical, 2, 90, 10);
        CheckCell(vertical, 4, 170, 10);
    }
    catch (Exception exception) { gridRenderError = exception; }
});
gridRenderThread.SetApartmentState(ApartmentState.STA);
gridRenderThread.Start();
gridRenderThread.Join();
if (gridRenderError is not null) throw gridRenderError;

var orderProject = new GameProject();
var orderScreen = orderProject.Gui.Children.Single(node => node.Type == "ScreenGui");
var backFrame = new SceneNode { Type = "Frame", Name = "BackFrame", Size = [100, 100, 2] };
var frontFrame = new SceneNode { Type = "Frame", Name = "FrontFrame", Size = [100, 100, 2] };
orderScreen.Children.Add(backFrame);
orderScreen.Children.Add(frontFrame);
var orderContainer = new SceneNode { Type = "Frame", Name = "OrderContainer", Position = [0, 120, 0], Size = [200, 50, 2] };
orderScreen.Children.Add(orderContainer);
var firstOrderCell = new SceneNode { Type = "TextLabel", Name = "OrderA" };
var secondOrderCell = new SceneNode { Type = "TextLabel", Name = "OrderB" };
var thirdOrderCell = new SceneNode { Type = "TextLabel", Name = "OrderC" };
orderContainer.Children.Add(firstOrderCell);
orderContainer.Children.Add(secondOrderCell);
orderContainer.Children.Add(thirdOrderCell);
orderContainer.Children.Add(new SceneNode { Type = "UIGridLayout", CellSize = [50, 30] });
var orderOutput = new List<string>();
var orderRuntime = new LuaXRuntime(orderProject, output: orderOutput.Add);
var orderScript = new GameScript
{
    Source = "game.Gui.ScreenGui.BackFrame.ZIndex = 7; game.Gui.ScreenGui.FrontFrame.ZIndex = 1; local c = game.Gui.ScreenGui.OrderContainer; c.OrderA.LayoutOrder = 2; c.OrderB.LayoutOrder = 0; c.OrderC.LayoutOrder = 1; print('gui-order', game.Gui.ScreenGui.BackFrame.ZIndex, c.OrderA.LayoutOrder, c.OrderB.LayoutOrder)"
};
orderProject.Scripts.Add(orderScript);
orderRuntime.Run(orderScript.Id);
if (backFrame.ZIndex != 7 || firstOrderCell.LayoutOrder != 2 || secondOrderCell.LayoutOrder != 0
    || !orderOutput.Any(line => line.Contains("gui-order\t7\t2\t0", StringComparison.Ordinal)))
    throw new Exception("ZIndex or LayoutOrder Lua X assignment failed: " + string.Join(" | ", orderOutput));
var orderJson = JsonSerializer.Serialize(orderProject, GameProjectStore.JsonOptions);
var restoredOrderProject = JsonSerializer.Deserialize<GameProject>(orderJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("GUI order project did not deserialize.");
GameProjectStore.EnsureProject(restoredOrderProject);
Exception? orderRenderError = null;
var orderRenderThread = new Thread(() =>
{
    try
    {
        var canvas = GuiRenderService.Render(restoredOrderProject, false, 300, 200);
        var back = canvas.Children.OfType<System.Windows.Controls.Border>().Single(element => (element.Tag as SceneNode)?.Name == "BackFrame");
        var front = canvas.Children.OfType<System.Windows.Controls.Border>().Single(element => (element.Tag as SceneNode)?.Name == "FrontFrame");
        if (System.Windows.Controls.Panel.GetZIndex(back) != 7 || System.Windows.Controls.Panel.GetZIndex(front) != 1)
            throw new Exception("ZIndex did not control visual layering.");
        var container = canvas.Children.OfType<System.Windows.Controls.Border>().Single(element => (element.Tag as SceneNode)?.Name == "OrderContainer");
        var children = (System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)container.Child).Children[1];
        var names = children.Children.OfType<System.Windows.Controls.Border>().Select(element => ((SceneNode)element.Tag).Name).ToArray();
        if (!names.SequenceEqual(["OrderB", "OrderC", "OrderA"]))
            throw new Exception("LayoutOrder did not arrange grid cells independently of Explorer order.");
        for (var index = 0; index < names.Length; index++)
            if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(children.Children[index]) - index * 50) > .001)
                throw new Exception("LayoutOrder produced the wrong grid position.");
    }
    catch (Exception exception) { orderRenderError = exception; }
});
orderRenderThread.SetApartmentState(ApartmentState.STA);
orderRenderThread.Start();
orderRenderThread.Join();
if (orderRenderError is not null) throw orderRenderError;

var rayProject = new GameProject();
var rayGroup = new SceneNode { Type = "Model", Name = "RayGroup" };
var nearRayPart = new SceneNode { Type = "Part", Name = "NearRayPart", Position = [0, 0, 0], Size = [10, 1, 1], Rotation = [0, 45, 0], CanCollide = false };
var farRayPart = new SceneNode { Type = "Part", Name = "FarRayPart", Position = [0, 0, 7], Size = [10, 1, 1] };
rayGroup.Children.Add(nearRayPart);
rayProject.Workspace.Children.Add(rayGroup);
rayProject.Workspace.Children.Add(farRayPart);
var rayOutput = new List<string>();
var rayRuntime = new LuaXRuntime(rayProject, output: rayOutput.Add);
var rayScript = new GameScript { Source = """
    local hit = workspace:Raycast(Vector3.new(3, 0, -5), Vector3.new(0, 0, 20))
    assert(hit and hit.Instance == workspace.RayGroup.NearRayPart and typeof(hit) == 'RaycastResult')
    assert(hit.Distance > 0 and hit.Distance < 10 and hit.Normal.Magnitude > 0.99)
    assert(workspace:Raycast(Vector3.new(3, 0, 0), Vector3.new(1, 0, 2)) == nil)
    local params = RaycastParams.new()
    assert(typeof(params) == 'RaycastParams')
    params.FilterDescendantsInstances = {workspace.RayGroup}
    local other = workspace:Raycast(Vector3.new(3, 0, -5), Vector3.new(0, 0, 20), params)
    assert(other and other.Instance == workspace.FarRayPart)
    params.FilterType = Enum.RaycastFilterType.Include
    assert(workspace:Raycast(Vector3.new(3, 0, -5), Vector3.new(0, 0, 20), params).Instance == workspace.RayGroup.NearRayPart)
    workspace.RayGroup.NearRayPart.CanQuery = false
    assert(workspace.RayGroup.NearRayPart.CanQuery == false)
    assert(workspace:Raycast(Vector3.new(3, 0, -5), Vector3.new(0, 0, 20)).Instance == workspace.FarRayPart)
    assert(workspace:Raycast(Vector3.new(0, 0, 0), Vector3.zero) == nil)
    print('raycast-ok')
    """ };
rayProject.Scripts.Add(rayScript);
rayRuntime.Run(rayScript.Id);
if (!rayOutput.Contains("raycast-ok") || rayOutput.Any(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)))
    throw new Exception("Workspace Raycast failed: " + string.Join(" | ", rayOutput));
var rayJson = JsonSerializer.Serialize(rayProject, GameProjectStore.JsonOptions);
var restoredRayProject = JsonSerializer.Deserialize<GameProject>(rayJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Raycast project did not deserialize.");
GameProjectStore.EnsureProject(restoredRayProject);
if (restoredRayProject.Workspace.Children.Single(node => node.Name == "RayGroup").Children.Single().CanQuery)
    throw new Exception("CanQuery did not persist.");

var modelProject = new GameProject();
var spinModel = new SceneNode { Type = "Model", Name = "SpinModel" };
var spinA = new SceneNode { Type = "Part", Name = "SpinA", Position = [1, 0, 0] };
var spinB = new SceneNode { Type = "Part", Name = "SpinB", Position = [3, 0, 0] };
spinModel.Children.Add(spinA);
spinModel.Children.Add(spinB);
modelProject.Workspace.Children.Add(spinModel);
if (Math.Abs(SceneModelService.GetPivot(spinModel)[0] - 2) > .001f
    || !SceneModelService.RotateTo(spinModel, [0, 90, 0])
    || Math.Abs(spinA.Position[0] - 2) > .001f || Math.Abs(spinA.Position[2] - 1) > .001f
    || Math.Abs(spinB.Position[2] + 1) > .001f || !spinModel.HasModelPivot)
    throw new Exception("Model rotation did not rotate parts around their shared pivot.");
SceneModelService.RotateTo(spinModel, [0, 180, 0]);
SceneModelService.MoveTo(spinModel, [5, 0, 0]);
if (Math.Abs(SceneModelService.GetPivot(spinModel)[0] - 5) > .001f
    || Math.Abs(spinA.Position[0] - 6) > .001f || Math.Abs(spinB.Position[0] - 4) > .001f)
    throw new Exception("Repeated Model transforms changed its pivot or child layout.");
var modelJson = JsonSerializer.Serialize(modelProject, GameProjectStore.JsonOptions);
var restoredModelProject = JsonSerializer.Deserialize<GameProject>(modelJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Model transform project did not deserialize.");
GameProjectStore.EnsureProject(restoredModelProject);
var restoredSpinModel = restoredModelProject.Workspace.Children.Single(node => node.Name == "SpinModel");
if (Math.Abs(SceneModelService.GetPivot(restoredSpinModel)[0] - 5) > .001f || !restoredSpinModel.HasModelPivot)
    throw new Exception("The Model pivot did not persist.");
var modelOutput = new List<string>();
var modelRuntime = new LuaXRuntime(restoredModelProject, output: modelOutput.Add);
var modelScript = new GameScript
{
    Source = "local m = workspace.SpinModel; m.Position = Vector3.new(8, 0, 0); m.Rotation = Vector3.new(0, 90, 0); print('model-direct')"
};
restoredModelProject.Scripts.Add(modelScript);
modelRuntime.Run(modelScript.Id);
var luaSpinA = restoredSpinModel.Children.Single(node => node.Name == "SpinA");
var luaSpinB = restoredSpinModel.Children.Single(node => node.Name == "SpinB");
if (!modelOutput.Contains("model-direct") || Math.Abs(SceneModelService.GetPivot(restoredSpinModel)[0] - 8) > .001f
    || Math.Abs(luaSpinA.Position[0] - 8) > .001f || Math.Abs(luaSpinA.Position[2] - 1) > .001f
    || Math.Abs(luaSpinB.Position[2] + 1) > .001f)
    throw new Exception("Direct Lua X Model transforms did not update children: " + string.Join(" | ", modelOutput));
var modelSetterScript = new GameScript
{
    Source = "local m = workspace.SpinModel; m:SetProperty('Rotation', Vector3.new(0, 180, 0)); m:SetProperty('Position', Vector3.new(10, 0, 0)); print('model-setter', m.SpinA.Position.X, m.SpinB.Position.X)"
};
restoredModelProject.Scripts.Add(modelSetterScript);
modelRuntime.Run(modelSetterScript.Id);
if (!modelOutput.Any(line => line.Contains("model-setter\t11\t9", StringComparison.Ordinal))
    || Math.Abs(luaSpinA.Position[0] - 11) > .001f || Math.Abs(luaSpinB.Position[0] - 9) > .001f)
    throw new Exception("SetProperty Model transforms did not update children immediately: " + string.Join(" | ", modelOutput));
var modelMixedScript = new GameScript
{
    Source = "local m = workspace.SpinModel; m.Position = Vector3.new(12, 0, 0); m.SpinA.Position = Vector3.new(20, 0, 0); print('model-mixed')"
};
restoredModelProject.Scripts.Add(modelMixedScript);
modelRuntime.Run(modelMixedScript.Id);
if (!modelOutput.Contains("model-mixed") || Math.Abs(SceneModelService.GetPivot(restoredSpinModel)[0] - 12) > .001f
    || Math.Abs(luaSpinA.Position[0] - 20) > .001f || Math.Abs(luaSpinB.Position[0] - 11) > .001f)
    throw new Exception("A child write was lost when its Model moved in the same script: " + string.Join(" | ", modelOutput));

var forceProject = new GameProject();
var guardedModel = new SceneNode { Type = "Model", Name = "Guarded" };
var guardedRoot = new SceneNode { Type = "Part", Name = "HumanoidRootPart" };
var guardedHumanoid = new SceneNode { Type = "Humanoid", Name = "Humanoid" };
var forceField = new SceneNode { Type = "ForceField", Name = "ForceField" };
forceProject.Workspace.Children.Add(guardedModel);
if (!GameProjectStore.CanParentNode(forceProject, forceField, guardedModel)
    || GameProjectStore.CanParentNode(forceProject, forceField, forceProject.Workspace))
    throw new Exception("ForceField insertion hierarchy is incorrect.");
guardedModel.Children.Add(guardedRoot);
guardedModel.Children.Add(guardedHumanoid);
guardedModel.Children.Add(forceField);
var forceOutput = new List<string>();
var forceRuntime = new LuaXRuntime(forceProject, output: forceOutput.Add);
var forceScript = new GameScript
{
    Source = """
        local model = workspace.Guarded
        local humanoid = model.Humanoid
        humanoid:TakeDamage(25)
        assert(humanoid.Health == 100)
        model.ForceField.Visible = false
        humanoid:TakeDamage(25)
        assert(humanoid.Health == 100)
        humanoid.Health = 70
        humanoid:TakeDamage(10)
        assert(humanoid.Health == 70)
        model.ForceField:Destroy()
        humanoid:TakeDamage(20)
        assert(humanoid.Health == 50)
        humanoid:TakeDamage(-10)
        assert(humanoid.Health == 60)
        local rootShield = Instance.new('ForceField', model.HumanoidRootPart)
        assert(rootShield and rootShield:IsA('ForceField'))
        humanoid:TakeDamage(10)
        assert(humanoid.Health == 60)
        rootShield:Destroy()
        local outer = Instance.new('Model', workspace)
        model:SetProperty('Parent', outer)
        local ancestorShield = Instance.new('ForceField', outer)
        humanoid:TakeDamage(10)
        assert(humanoid.Health == 60)
        ancestorShield:Destroy()
        model:SetProperty('Parent', workspace)
        outer:Destroy()
        print('forcefield-ok')
        """
};
forceProject.Scripts.Add(forceScript);
forceRuntime.Run(forceScript.Id);
if (!forceOutput.Contains("forcefield-ok") || guardedHumanoid.Health != 60)
    throw new Exception("ForceField protection or Humanoid.TakeDamage failed: " + string.Join(" | ", forceOutput));
var persistedForceField = new SceneNode { Type = "ForceField", Visible = false };
guardedModel.Children.Add(persistedForceField);
var forceJson = JsonSerializer.Serialize(forceProject, GameProjectStore.JsonOptions);
var restoredForceProject = JsonSerializer.Deserialize<GameProject>(forceJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("ForceField project did not deserialize.");
GameProjectStore.EnsureProject(restoredForceProject);
if (restoredForceProject.Workspace.Children.Single(node => node.Name == "Guarded").Children.Single(node => node.Type == "ForceField").Visible)
    throw new Exception("ForceField.Visible did not persist.");

var explosionProject = new GameProject();
explosionProject.Workspace.Gravity = 0;
var blastTarget = new SceneNode { Type = "Part", Name = "BlastTarget", Anchored = false, CanCollide = false, Position = [4, 0, 0] };
var blastMate = new SceneNode { Type = "Part", Name = "BlastMate", Anchored = false, CanCollide = false, Position = [5, 0, 0] };
var distantPart = new SceneNode { Type = "Part", Name = "DistantPart", Anchored = false, Position = [30, 0, 0] };
var blastWeld = new SceneNode { Type = "WeldConstraint", Part0 = blastTarget.Id, Part1 = blastMate.Id };
explosionProject.Workspace.Children.Add(blastTarget);
explosionProject.Workspace.Children.Add(blastMate);
explosionProject.Workspace.Children.Add(distantPart);
explosionProject.Workspace.Children.Add(blastWeld);
var unguarded = new SceneNode { Type = "Model", Name = "Unguarded" };
var unguardedHumanoid = new SceneNode { Type = "Humanoid", Name = "Humanoid" };
unguarded.Children.Add(new SceneNode { Type = "Part", Name = "Body", Position = [1, 0, 0] });
unguarded.Children.Add(unguardedHumanoid);
explosionProject.Workspace.Children.Add(unguarded);
var blastGuarded = new SceneNode { Type = "Model", Name = "BlastGuarded" };
var blastGuardedHumanoid = new SceneNode { Type = "Humanoid", Name = "Humanoid" };
blastGuarded.Children.Add(new SceneNode { Type = "Part", Name = "Body", Position = [-1, 0, 0] });
blastGuarded.Children.Add(blastGuardedHumanoid);
blastGuarded.Children.Add(new SceneNode { Type = "ForceField" });
explosionProject.Workspace.Children.Add(blastGuarded);
var explosionOutput = new List<string>();
var explosionRuntime = new LuaXRuntime(explosionProject, output: explosionOutput.Add);
var explosionScript = new GameScript
{
    Source = """
        local blast = Instance.new('Explosion')
        blast.Position = Vector3.new(0, 0, 0)
        blast.BlastRadius = 8
        blast.BlastPressure = 500000
        blast.DestroyJointRadiusPercent = 1
        blast.Hit:Connect(function(part, distance)
            print('explosion-hit', part.Name, distance >= 0)
        end)
        blast.Parent = workspace
        print('explosion-created')
        """
};
explosionProject.Scripts.Add(explosionScript);
explosionRuntime.Run(explosionScript.Id);
explosionRuntime.UpdateTouched(.02f);
if (!explosionOutput.Contains("explosion-created")
    || !explosionOutput.Any(line => line.Contains("explosion-hit\tBlastTarget\ttrue", StringComparison.Ordinal))
    || explosionOutput.Any(line => line.Contains("explosion-hit\tDistantPart", StringComparison.Ordinal))
    || blastTarget.Velocity[0] <= 0 || distantPart.Velocity.Any(value => value != 0)
    || blastWeld.Enabled || unguardedHumanoid.Health != 0 || blastGuardedHumanoid.Health != 100)
    throw new Exception("Explosion radius, pressure, hit signal, weld damage, or ForceField protection failed: " + string.Join(" | ", explosionOutput));
var hitCount = explosionOutput.Count(line => line.StartsWith("explosion-hit", StringComparison.Ordinal));
explosionRuntime.UpdateTouched(.02f);
if (explosionOutput.Count(line => line.StartsWith("explosion-hit", StringComparison.Ordinal)) != hitCount)
    throw new Exception("Explosion detonated more than once.");
var harmlessScript = new GameScript
{
    Source = """
        local blast = Instance.new('Explosion')
        blast.Position = Vector3.new(30, 0, 0)
        blast.BlastRadius = 4
        blast.BlastPressure = 0
        blast.DestroyJointRadiusPercent = 0
        blast.Hit:Connect(function(part) print('harmless-hit', part.Name) end)
        blast.Parent = workspace
        """
};
explosionProject.Scripts.Add(harmlessScript);
explosionRuntime.Run(harmlessScript.Id);
explosionRuntime.UpdateTouched(.02f);
if (!explosionOutput.Any(line => line.Contains("harmless-hit\tDistantPart", StringComparison.Ordinal))
    || distantPart.Velocity.Any(value => value != 0))
    throw new Exception("Zero-pressure Explosion failed to emit Hit without applying force: " + string.Join(" | ", explosionOutput));
var placedExplosionProject = new GameProject();
placedExplosionProject.Workspace.Gravity = 0;
var placedBlastTarget = new SceneNode { Type = "Part", Anchored = false, CanCollide = false, Position = [2, 0, 0] };
var placedExplosion = new SceneNode { Type = "Explosion", BlastRadius = 4, BlastPressure = 500000, DestroyJointRadiusPercent = 0 };
placedExplosionProject.Workspace.Children.Add(placedBlastTarget);
placedExplosionProject.Workspace.Children.Add(placedExplosion);
var placedExplosionRuntime = new LuaXRuntime(placedExplosionProject);
placedExplosionRuntime.RunAll();
if (placedBlastTarget.Velocity[0] <= 0 || !placedExplosionRuntime.HasPendingScripts)
    throw new Exception("An Explosion placed in the editor did not detonate at play startup.");
placedExplosionRuntime.UpdateTouched(.25f);
placedExplosionRuntime.UpdateTouched(.25f);
if (placedExplosionRuntime.HasPendingScripts || GameProjectStore.Flatten(placedExplosionProject.Workspace.Children).Any(node => node.Id == placedExplosion.Id))
    throw new Exception("A play-mode Explosion did not expire after its visual pulse.");
var savedExplosion = new SceneNode { Type = "Explosion", BlastRadius = 6, BlastPressure = 1234, DestroyJointRadiusPercent = .25f, Visible = false };
if (!GameProjectStore.CanParentNode(explosionProject, savedExplosion, explosionProject.Workspace))
    throw new Exception("Explosion cannot be inserted under Workspace.");
explosionProject.Workspace.Children.Add(savedExplosion);
var explosionJson = JsonSerializer.Serialize(explosionProject, GameProjectStore.JsonOptions);
var restoredExplosionProject = JsonSerializer.Deserialize<GameProject>(explosionJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Explosion project did not deserialize.");
GameProjectStore.EnsureProject(restoredExplosionProject);
var restoredExplosion = restoredExplosionProject.Workspace.Children.Single(node => node.Id == savedExplosion.Id);
if (restoredExplosion.BlastRadius != 6 || restoredExplosion.BlastPressure != 1234
    || Math.Abs(restoredExplosion.DestroyJointRadiusPercent - .25f) > .001f || restoredExplosion.Visible)
    throw new Exception("Explosion properties did not persist.");

var debrisProject = new GameProject();
var debrisScriptNode = new SceneNode { Type = "Script", Name = "DebrisTest" };
debrisProject.ServerScriptService.Children.Add(debrisScriptNode);
debrisProject.Scripts.Add(new GameScript
{
    Id = debrisScriptNode.Id,
    Name = debrisScriptNode.Name,
    Source = """
        local debris = game:GetService('Debris')
        print('debris-service', debris == game.Debris, typeof(debris), debris.ClassName)
        local part = Instance.new('Part', workspace)
        part.Name = 'TemporaryPart'
        debris:AddItem(part, 0.2)
        local immediate = Instance.new('Part', workspace)
        immediate.Name = 'ImmediatePart'
        debris:AddItem(immediate, 0)
        print('debris-immediate', workspace:FindFirstChild('ImmediatePart') == nil)
        script.Enabled = false
        """
});
var debrisOutput = new List<string>();
var debrisRuntime = new LuaXRuntime(debrisProject, output: debrisOutput.Add);
debrisRuntime.RunAll();
if (!debrisOutput.Contains("debris-service\ttrue\tDebris\tDebris")
    || !debrisOutput.Contains("debris-immediate\ttrue")
    || !debrisRuntime.HasPendingScripts
    || !GameProjectStore.Flatten(debrisProject.Workspace.Children).Any(node => node.Name == "TemporaryPart"))
    throw new Exception("Debris service lookup or scheduling failed: " + string.Join(" | ", debrisOutput));
debrisRuntime.Update(.1f);
if (!GameProjectStore.Flatten(debrisProject.Workspace.Children).Any(node => node.Name == "TemporaryPart"))
    throw new Exception("Debris destroyed an item before its lifetime elapsed.");
debrisRuntime.Update(.1f);
if (debrisRuntime.HasPendingScripts || GameProjectStore.Flatten(debrisProject.Workspace.Children).Any(node => node.Name == "TemporaryPart"))
    throw new Exception("Debris did not destroy its item after the scheduling script stopped.");

var effectProject = new GameProject();
var effectPart = new SceneNode { Type = "Part", Name = "EffectPart" };
var effectAttachment = new SceneNode { Type = "Attachment", Name = "EffectAttachment" };
effectProject.Workspace.Children.Add(effectPart);
effectPart.Children.Add(effectAttachment);
if (!GameProjectStore.CanParentNode(effectProject, new SceneNode { Type = "Fire" }, effectPart)
    || !GameProjectStore.CanParentNode(effectProject, new SceneNode { Type = "Smoke" }, effectAttachment)
    || !GameProjectStore.CanParentNode(effectProject, new SceneNode { Type = "Sparkles" }, effectPart)
    || GameProjectStore.CanParentNode(effectProject, new SceneNode { Type = "Fire" }, effectProject.Workspace))
    throw new Exception("Fire, Smoke, or Sparkles parent rules failed.");
var effectOutput = new List<string>();
var effectRuntime = new LuaXRuntime(effectProject, output: effectOutput.Add);
var effectScript = new GameScript
{
    Source = """
        local part = workspace.EffectPart
        local fire = Instance.new('Fire', part)
        fire.Size = 100
        fire.Heat = -12
        fire.TimeScale = 0.5
        fire.Color = Color3.fromRGB(255, 80, 0)
        fire.SecondaryColor = Color3.fromRGB(255, 255, 100)
        local smoke = Instance.new('Smoke', part.EffectAttachment)
        smoke.Size = 0.01
        smoke.Opacity = 0.7
        smoke.RiseVelocity = -4
        smoke.Enabled = false
        local sparkles = Instance.new('Sparkles', part)
        sparkles.SparkleColor = Color3.fromRGB(12, 34, 56)
        sparkles.TimeScale = 0.25
        print('effects-created', fire:IsA('Fire'), smoke:IsA('Smoke'), typeof(fire.Size), typeof(smoke.Size))
        """
};
effectProject.Scripts.Add(effectScript);
effectRuntime.Run(effectScript.Id);
var fireEffect = effectPart.Children.Single(node => node.Type == "Fire");
var smokeEffect = effectAttachment.Children.Single(node => node.Type == "Smoke");
var sparklesEffect = effectPart.Children.Single(node => node.Type == "Sparkles");
if (!effectOutput.Contains("effects-created\ttrue\ttrue\tnumber\tnumber")
    || fireEffect.EffectSize != 30 || fireEffect.Heat != -12 || fireEffect.TimeScale != .5f
    || fireEffect.Color != "#FF5000" || fireEffect.SecondaryColor != "#FFFF64"
    || smokeEffect.EffectSize != .1f || smokeEffect.Opacity != .7f || smokeEffect.RiseVelocity != -4 || smokeEffect.Enabled
    || sparklesEffect.Color != "#0C2238" || sparklesEffect.TimeScale != .25f)
    throw new Exception("Fire, Smoke, or Sparkles Lua X properties failed: " + string.Join(" | ", effectOutput));
var sparkleAliasScript = new GameScript { Source = "workspace.EffectPart.Sparkles.Color = Color3.fromRGB(22, 33, 44)" };
effectProject.Scripts.Add(sparkleAliasScript);
effectRuntime.Run(sparkleAliasScript.Id);
if (sparklesEffect.Color != "#16212C") throw new Exception("Sparkles Color alias did not update SparkleColor.");
var effectJson = JsonSerializer.Serialize(effectProject, GameProjectStore.JsonOptions);
var restoredEffectProject = JsonSerializer.Deserialize<GameProject>(effectJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Fire and Smoke project did not deserialize.");
GameProjectStore.EnsureProject(restoredEffectProject);
var restoredEffectPart = restoredEffectProject.Workspace.Children.Single(node => node.Id == effectPart.Id);
if (restoredEffectPart.Children.Single(node => node.Type == "Fire").EffectSize != 30
    || restoredEffectPart.Children.Single(node => node.Type == "Attachment").Children.Single(node => node.Type == "Smoke").RiseVelocity != -4
    || restoredEffectPart.Children.Single(node => node.Type == "Sparkles").Color != "#16212C")
    throw new Exception("Fire, Smoke, or Sparkles did not persist.");

var emitterProject = new GameProject();
var emitterPart = new SceneNode { Type = "Part", Name = "EmitterPart" };
var emitterAttachment = new SceneNode { Type = "Attachment", Name = "EmitterAttachment" };
emitterProject.Workspace.Children.Add(emitterPart);
emitterPart.Children.Add(emitterAttachment);
if (!GameProjectStore.CanParentNode(emitterProject, new SceneNode { Type = "ParticleEmitter" }, emitterPart)
    || !GameProjectStore.CanParentNode(emitterProject, new SceneNode { Type = "ParticleEmitter" }, emitterAttachment)
    || GameProjectStore.CanParentNode(emitterProject, new SceneNode { Type = "ParticleEmitter" }, emitterProject.Workspace))
    throw new Exception("ParticleEmitter parent rules failed.");
var emitterOutput = new List<string>();
var emitterRuntime = new LuaXRuntime(emitterProject, output: emitterOutput.Add);
var emitterScript = new GameScript
{
    Source = """
        local emitter = Instance.new('ParticleEmitter', workspace.EmitterPart.EmitterAttachment)
        emitter.Color = ColorSequence.new(Color3.fromRGB(12, 34, 56))
        emitter.Size = NumberSequence.new(2)
        emitter.Transparency = NumberSequence.new(0.3)
        emitter.Speed = NumberRange.new(2, 6)
        emitter.Lifetime = NumberRange.new(0.5, 1)
        emitter.SpreadAngle = Vector2.new(45, 30)
        emitter.EmissionDirection = Enum.NormalId.Front
        emitter.Rate = 20
        emitter.Texture = 'spark.png'
        emitter:Emit(15)
        print('emitter-types', typeof(emitter), typeof(emitter.Color), typeof(emitter.Size), typeof(emitter.Speed), typeof(emitter.SpreadAngle), emitter.Speed.Max)
        """
};
emitterProject.Scripts.Add(emitterScript);
emitterRuntime.Run(emitterScript.Id);
var particleEmitter = emitterAttachment.Children.Single(node => node.Type == "ParticleEmitter");
if (!emitterOutput.Contains("emitter-types\tParticleEmitter\tColorSequence\tNumberSequence\tNumberRange\tVector2\t6")
    || particleEmitter.Color != "#0C2238" || particleEmitter.EffectSize != 2 || Math.Abs(particleEmitter.Transparency - .3f) > .001f
    || particleEmitter.SpeedRange[0] != 2 || particleEmitter.SpeedRange[1] != 6
    || particleEmitter.LifetimeRange[0] != .5f || particleEmitter.LifetimeRange[1] != 1
    || particleEmitter.SpreadAngle[0] != 45 || particleEmitter.SpreadAngle[1] != 30
    || particleEmitter.EmissionDirection != "Front" || particleEmitter.Rate != 20 || particleEmitter.Texture != "spark.png" || particleEmitter.PendingParticleEmits != 15)
    throw new Exception("ParticleEmitter Lua X properties or Emit failed: " + string.Join(" | ", emitterOutput));
var particleGeometryMethod = typeof(SceneRenderService).GetMethod("BuildParticleEmitterGeometry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
    ?? throw new Exception("ParticleEmitter geometry builder is missing.");
var particleGeometry = particleGeometryMethod.Invoke(null, [particleEmitter, 0f, new float[] { 2, 2, 2 }, null, float.NegativeInfinity, float.PositiveInfinity, null])
    as HelixToolkit.SharpDX.Core.MeshGeometry3D;
if (particleGeometry?.Positions is null || particleGeometry.Positions.Count == 0)
    throw new Exception("ParticleEmitter did not produce visible geometry.");
var billboardGeometry = particleGeometryMethod.Invoke(null, [particleEmitter, 0f, new float[] { 2, 2, 2 }, null, float.NegativeInfinity, float.PositiveInfinity,
    (new SharpDX.Vector3(0, 0, 10), SharpDX.Vector3.UnitY)]) as HelixToolkit.SharpDX.Core.MeshGeometry3D;
if (billboardGeometry?.Positions is null || billboardGeometry.Positions.Count == 0
    || billboardGeometry.Positions.Count >= particleGeometry.Positions.Count
    || billboardGeometry.TextureCoordinates?.Count != billboardGeometry.Positions.Count)
    throw new Exception("Textured ParticleEmitter did not produce UV mapped billboards.");
var firstBillboard = billboardGeometry.Positions;
var billboardNormal = SharpDX.Vector3.Cross(firstBillboard[1] - firstBillboard[0], firstBillboard[3] - firstBillboard[0]);
if (SharpDX.Vector3.Dot(billboardNormal, new SharpDX.Vector3(0, 0, 10) - firstBillboard[0]) <= 0)
    throw new Exception("ParticleEmitter billboard does not face the camera.");
var clearedGeometry = particleGeometryMethod.Invoke(null, [particleEmitter, 0f, new float[] { 2, 2, 2 }, null, 0f, float.PositiveInfinity, null])
    as HelixToolkit.SharpDX.Core.MeshGeometry3D;
if (clearedGeometry?.Positions is null || clearedGeometry.Positions.Count != 0)
    throw new Exception("ParticleEmitter.Clear left previously emitted geometry visible.");
var emitterClone = GameProjectStore.CloneSubtree(particleEmitter, out _)
    ?? throw new Exception("ParticleEmitter did not clone.");
if (emitterClone.Rate != 20 || emitterClone.Texture != "spark.png" || emitterClone.PendingParticleEmits != 0)
    throw new Exception("ParticleEmitter clone lost settings or copied transient particles.");
var emitterJson = JsonSerializer.Serialize(emitterProject, GameProjectStore.JsonOptions);
var restoredEmitterProject = JsonSerializer.Deserialize<GameProject>(emitterJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("ParticleEmitter project did not deserialize.");
GameProjectStore.EnsureProject(restoredEmitterProject);
var restoredEmitter = restoredEmitterProject.Workspace.Children.Single(node => node.Id == emitterPart.Id)
    .Children.Single(node => node.Id == emitterAttachment.Id).Children.Single(node => node.Type == "ParticleEmitter");
if (restoredEmitter.Color != "#0C2238" || restoredEmitter.SpeedRange[1] != 6 || restoredEmitter.Texture != "spark.png" || restoredEmitter.PendingParticleEmits != 0)
    throw new Exception("ParticleEmitter settings did not persist correctly.");
var clearEmitterScript = new GameScript { Source = "workspace.EmitterPart.EmitterAttachment.ParticleEmitter:Clear()" };
emitterProject.Scripts.Add(clearEmitterScript);
emitterRuntime.Run(clearEmitterScript.Id);
if (particleEmitter.PendingParticleEmits != 0 || particleEmitter.ParticleClearVersion != 1)
    throw new Exception("ParticleEmitter.Clear did not discard pending particles.");
var sequenceScript = new GameScript
{
    Source = """
        local emitter = workspace.EmitterPart.EmitterAttachment.ParticleEmitter
        emitter.Size = NumberSequence.new{
            NumberSequenceKeypoint.new(0, 1),
            NumberSequenceKeypoint.new(0.5, 3, 0.25),
            NumberSequenceKeypoint.new(1, 1)
        }
        emitter.Color = ColorSequence.new{
            ColorSequenceKeypoint.new(0, Color3.fromRGB(255, 0, 0)),
            ColorSequenceKeypoint.new(1, Color3.fromRGB(0, 0, 255))
        }
        emitter.Transparency = NumberSequence.new(0, 1)
        print('sequence-keypoints', #emitter.Size.Keypoints, emitter.Size.Keypoints[2].Envelope,
            #emitter.Color.Keypoints, emitter.Color.Keypoints[2].Value.B,
            emitter.Transparency.Keypoints[2].Value)
        """
};
emitterProject.Scripts.Add(sequenceScript);
emitterRuntime.Run(sequenceScript.Id);
if (!emitterOutput.Contains("sequence-keypoints\t3\t0.25\t2\t1\t1")
    || particleEmitter.SizeSequence.Count != 3 || particleEmitter.SizeSequence[1].Envelope != .25f
    || particleEmitter.ColorSequence.Count != 2 || particleEmitter.ColorSequence[1].Value != "#0000FF"
    || particleEmitter.TransparencySequence.Count != 2 || particleEmitter.TransparencySequence[1].Value != 1)
    throw new Exception("ParticleEmitter keypoint assignment failed: " + string.Join(" | ", emitterOutput));
if (Math.Abs(ParticleSequenceService.SampleNumber(particleEmitter.SizeSequence, 0, .25f, .5f) - 2) > .001f
    || ParticleSequenceService.SampleColor(particleEmitter.ColorSequence, "#FFFFFF", .5f) is not { R: 128, B: 128 })
    throw new Exception("ParticleEmitter keypoint interpolation failed.");
var sequenceGeometry = particleGeometryMethod.Invoke(null, [particleEmitter, .4f, new float[] { 2, 2, 2 }, null,
    float.NegativeInfinity, float.PositiveInfinity, null]) as HelixToolkit.SharpDX.Core.MeshGeometry3D;
if (sequenceGeometry?.Colors is null || sequenceGeometry.Positions is null
    || sequenceGeometry.Colors.Count != sequenceGeometry.Positions.Count
    || sequenceGeometry.Colors.Select(color => color.Red).Distinct().Count() < 2
    || sequenceGeometry.Colors.Select(color => color.Alpha).Distinct().Count() < 2)
    throw new Exception("ParticleEmitter sequences did not reach per-particle mesh colors.");
var sequenceJson = JsonSerializer.Serialize(emitterProject, GameProjectStore.JsonOptions);
var sequenceRestored = JsonSerializer.Deserialize<GameProject>(sequenceJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("ParticleEmitter keypoint project did not deserialize.");
GameProjectStore.EnsureProject(sequenceRestored);
var restoredSequence = sequenceRestored.Workspace.Children.Single(node => node.Id == emitterPart.Id)
    .Children.Single(node => node.Id == emitterAttachment.Id).Children.Single(node => node.Id == particleEmitter.Id);
if (restoredSequence.SizeSequence.Count != 3 || restoredSequence.ColorSequence[1].Value != "#0000FF"
    || restoredSequence.TransparencySequence[1].Value != 1)
    throw new Exception("ParticleEmitter keypoints did not persist.");
var sequenceClone = GameProjectStore.CloneSubtree(particleEmitter, out _)
    ?? throw new Exception("ParticleEmitter keypoints did not clone.");
if (sequenceClone.SizeSequence.Count != 3 || sequenceClone.ColorSequence[1].Value != "#0000FF"
    || sequenceClone.TransparencySequence[1].Value != 1)
    throw new Exception("ParticleEmitter clone lost keypoints.");
if (!ParticleSequenceService.TryParseNumberText("0:1, 0.5:2:0.25, 1:0", 0, 100, out var editedSize)
    || editedSize.Count != 3 || ParticleSequenceService.TryParseNumberText("0.5:1, 1:0", 0, 100, out _)
    || !ParticleSequenceService.TryParseColorText("0:#FF0000, 1:#0000FF", out var editedColor)
    || editedColor.Count != 2 || ParticleSequenceService.TryParseColorText("0:bogus, 1:#0000FF", out _))
    throw new Exception("ParticleEmitter numeric keypoint editor validation failed.");
var invalidSequenceScript = new GameScript { Source = "print('invalid-sequence', typeof(NumberSequence.new(1e300)))" };
emitterProject.Scripts.Add(invalidSequenceScript);
emitterRuntime.Run(invalidSequenceScript.Id);
if (!emitterOutput.Contains("invalid-sequence\tnil"))
    throw new Exception("Overflowing NumberSequence values were accepted.");

var trailProject = new GameProject();
var trailPart = new SceneNode { Type = "Part", Name = "TrailPart" };
var trailAttachment0 = new SceneNode { Type = "Attachment", Name = "TrailAttachment0", Position = [-.5f, 0, 0] };
var trailAttachment1 = new SceneNode { Type = "Attachment", Name = "TrailAttachment1", Position = [.5f, 0, 0] };
trailProject.Workspace.Children.Add(trailPart);
trailPart.Children.Add(trailAttachment0);
trailPart.Children.Add(trailAttachment1);
if (!GameProjectStore.CanParentNode(trailProject, new SceneNode { Type = "Trail" }, trailPart)
    || GameProjectStore.CanParentNode(trailProject, new SceneNode { Type = "Trail" }, trailProject.Gui))
    throw new Exception("Trail parent rules failed.");
var trailOutput = new List<string>();
var trailRuntime = new LuaXRuntime(trailProject, output: trailOutput.Add);
var trailScript = new GameScript
{
    Source = """
        local trail = Instance.new('Trail', workspace.TrailPart)
        trail.Attachment0 = workspace.TrailPart.TrailAttachment0
        trail.Attachment1 = workspace.TrailPart.TrailAttachment1
        trail.Color = ColorSequence.new(Color3.fromRGB(255, 0, 0), Color3.fromRGB(0, 0, 255))
        trail.Transparency = NumberSequence.new(0, 1)
        trail.WidthScale = NumberSequence.new(1, 0)
        trail.Lifetime = 1.5
        trail.MinLength = 0.05
        trail.MaxLength = 5
        trail.FaceCamera = true
        trail.TextureMode = Enum.TextureMode.Wrap
        trail.TextureLength = 2
        trail.Texture = 'streak.png'
        print('trail-types', typeof(trail), typeof(trail.Color), typeof(trail.Transparency),
            typeof(trail.WidthScale), trail.Attachment0.Name, trail.Attachment1.Name)
        trail:Clear()
        """
};
trailProject.Scripts.Add(trailScript);
trailRuntime.Run(trailScript.Id);
var trailNode = trailPart.Children.Single(node => node.Type == "Trail");
if (!trailOutput.Contains("trail-types\tTrail\tColorSequence\tNumberSequence\tNumberSequence\tTrailAttachment0\tTrailAttachment1")
    || trailNode.Attachment0 != trailAttachment0.Id || trailNode.Attachment1 != trailAttachment1.Id
    || trailNode.TrailLifetime != 1.5f || trailNode.TrailMinLength != .05f || trailNode.TrailMaxLength != 5
    || trailNode.TrailWidthSequence.Count != 2 || trailNode.TrailWidthSequence[1].Value != 0
    || trailNode.ColorSequence[1].Value != "#0000FF" || trailNode.TransparencySequence[1].Value != 1
    || trailNode.Texture != "streak.png" || !trailNode.FaceCamera
    || trailNode.TextureMode != "Wrap" || trailNode.TextureLength != 2 || trailNode.TrailClearVersion != 1)
    throw new Exception("Trail Lua X properties or Clear failed: " + string.Join(" | ", trailOutput));
var trailSamples = new List<TrailSample>();
var initialTrailSample = new TrailSample(0, new SharpDX.Vector3(-.5f, 0, 0), new SharpDX.Vector3(.5f, 0, 0));
TrailGeometryService.AdvanceSamples(trailNode, trailSamples, initialTrailSample, 0);
TrailGeometryService.AdvanceSamples(trailNode, trailSamples,
    new TrailSample(.1f, new SharpDX.Vector3(-.48f, 0, 0), new SharpDX.Vector3(.52f, 0, 0)), .1f);
if (trailSamples.Count != 1) throw new Exception("Trail MinLength did not suppress tiny movements.");
TrailGeometryService.AdvanceSamples(trailNode, trailSamples,
    new TrailSample(.5f, new SharpDX.Vector3(-.5f, 0, 1), new SharpDX.Vector3(.5f, 0, 1)), .5f);
if (trailSamples.Count != 2) throw new Exception("Trail did not record attachment movement.");
var trailGeometry = TrailGeometryService.Build(trailNode, trailSamples, .5f);
if (trailGeometry.Positions is null || trailGeometry.Positions.Count == 0
    || trailGeometry.Colors?.Count != trailGeometry.Positions.Count
    || trailGeometry.TextureCoordinates?.Count != trailGeometry.Positions.Count
    || trailGeometry.Colors.Select(color => color.Blue).Distinct().Count() < 2
    || trailGeometry.Colors.Select(color => color.Alpha).Distinct().Count() < 2)
    throw new Exception("Trail ribbon or sequence colors were not generated.");
var textureTrail = new SceneNode { Type = "Trail", TrailMinLength = 0, TextureMode = "Stretch", TextureLength = 2 };
var textureSamples = new List<TrailSample>();
for (var index = 0; index < 3; index++)
    TrailGeometryService.AdvanceSamples(textureTrail, textureSamples,
        new TrailSample(index * .1f, new SharpDX.Vector3(-.5f, 0, index), new SharpDX.Vector3(.5f, 0, index)), index * .1f);
var stretched = TrailGeometryService.Build(textureTrail, textureSamples, .2f);
if (Math.Abs(stretched.TextureCoordinates[2].Y - 1) > .001f
    || Math.Abs(stretched.TextureCoordinates[10].Y - 2) > .001f)
    throw new Exception("Trail Stretch texture coordinates did not span the full path.");
textureTrail.TextureMode = "Wrap";
var wrapped = TrailGeometryService.Build(textureTrail, textureSamples, .2f);
if (Math.Abs(wrapped.TextureCoordinates[2].Y - .5f) > .001f)
    throw new Exception("Trail Wrap texture coordinates ignored TextureLength.");
textureSamples.RemoveAt(0);
textureTrail.TextureMode = "Static";
var staticTexture = TrailGeometryService.Build(textureTrail, textureSamples, .2f);
if (Math.Abs(staticTexture.TextureCoordinates[0].Y - .5f) > .001f)
    throw new Exception("Trail Static texture coordinates did not stay fixed when the oldest segment left.");
textureTrail.FaceCamera = true;
var frontFacing = TrailGeometryService.Build(textureTrail, textureSamples, .2f, new SharpDX.Vector3(0, 4, 0));
var sideFacing = TrailGeometryService.Build(textureTrail, textureSamples, .2f, new SharpDX.Vector3(4, 0, 0));
if (SharpDX.Vector3.DistanceSquared(frontFacing.Positions[0], sideFacing.Positions[0]) < .01f)
    throw new Exception("Trail FaceCamera did not respond to camera position.");
TrailGeometryService.AdvanceSamples(trailNode, trailSamples, null, 2.1f);
if (trailSamples.Count != 0) throw new Exception("Trail segments did not expire after Lifetime.");
var limitedTrail = new SceneNode { Type = "Trail", TrailMinLength = 0, TrailMaxLength = .5f, Enabled = false };
var limitedSamples = new List<TrailSample>();
TrailGeometryService.AdvanceSamples(limitedTrail, limitedSamples, initialTrailSample, 0);
if (limitedSamples.Count != 0) throw new Exception("Disabled Trail recorded segments.");
limitedTrail.Enabled = true;
TrailGeometryService.AdvanceSamples(limitedTrail, limitedSamples, initialTrailSample, 0);
TrailGeometryService.AdvanceSamples(limitedTrail, limitedSamples,
    new TrailSample(.1f, new SharpDX.Vector3(-.5f, 0, 1), new SharpDX.Vector3(.5f, 0, 1)), .1f);
if (limitedSamples.Count != 1) throw new Exception("Trail MaxLength did not limit the recorded path.");
var trailClone = GameProjectStore.CloneSubtree(trailNode, out _)
    ?? throw new Exception("Trail did not clone.");
if (trailClone.TrailLifetime != 1.5f || trailClone.ColorSequence.Count != 2 || trailClone.TrailClearVersion != 0
    || !trailClone.FaceCamera || trailClone.TextureMode != "Wrap" || trailClone.TextureLength != 2)
    throw new Exception("Trail clone lost settings or copied transient state.");
var trailJson = JsonSerializer.Serialize(trailProject, GameProjectStore.JsonOptions);
var restoredTrailProject = JsonSerializer.Deserialize<GameProject>(trailJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Trail project did not deserialize.");
GameProjectStore.EnsureProject(restoredTrailProject);
var restoredTrail = restoredTrailProject.Workspace.Children.Single(node => node.Id == trailPart.Id)
    .Children.Single(node => node.Type == "Trail");
if (restoredTrail.Attachment0 != trailAttachment0.Id || restoredTrail.Attachment1 != trailAttachment1.Id
    || restoredTrail.TrailWidthSequence[1].Value != 0 || restoredTrail.TrailClearVersion != 0
    || !restoredTrail.FaceCamera || restoredTrail.TextureMode != "Wrap" || restoredTrail.TextureLength != 2)
    throw new Exception("Trail settings or references did not persist.");

{
var vectorForceProject = new GameProject();
vectorForceProject.Workspace.Gravity = 0;
var forcePart = new SceneNode { Type = "Part", Name = "ForcePart", Position = [0, 4, 0], Anchored = false };
var forceAttachment = new SceneNode { Type = "Attachment", Name = "ForceAttachment" };
vectorForceProject.Workspace.Children.Add(forcePart);
forcePart.Children.Add(forceAttachment);
if (!GameProjectStore.CanParentNode(vectorForceProject, new SceneNode { Type = "VectorForce" }, forceAttachment)
    || GameProjectStore.CanParentNode(vectorForceProject, new SceneNode { Type = "VectorForce" }, vectorForceProject.Gui))
    throw new Exception("VectorForce parent rules failed.");
var vectorForceOutput = new List<string>();
var vectorForceRuntime = new LuaXRuntime(vectorForceProject, output: vectorForceOutput.Add);
var vectorForceScript = new GameScript
{
    Source = """
        local attachment = workspace.ForcePart.ForceAttachment
        local mover = Instance.new('VectorForce', attachment)
        print('vector-default', mover.RelativeTo)
        mover.Force = Vector3.new(80, 0, 0)
        mover.RelativeTo = Enum.ActuatorRelativeTo.Attachment0
        print('vector-force', typeof(mover), typeof(mover.Force), mover.Attachment0 == attachment, mover.RelativeTo)
        mover:SetProperty('RelativeTo', Enum.ActuatorRelativeTo.World)
        """
};
vectorForceProject.Scripts.Add(vectorForceScript);
vectorForceRuntime.Run(vectorForceScript.Id);
var forceNode = forceAttachment.Children.Single(node => node.Type == "VectorForce");
if (!vectorForceOutput.Contains("vector-default\tAttachment0")
    || !vectorForceOutput.Contains("vector-force\tVectorForce\tVector3\ttrue\tAttachment0")
    || forceNode.Attachment0 != forceAttachment.Id || forceNode.Force[0] != 80 || forceNode.RelativeTo != "World")
    throw new Exception("VectorForce Lua X properties failed: " + string.Join(" | ", vectorForceOutput));
new PhysicsSimulationService(vectorForceProject, _ => { }).Step(.1f);
if (Math.Abs(forcePart.Velocity[0] - 1) > .001f || forcePart.Position[0] <= 0)
    throw new Exception($"VectorForce did not accelerate its unanchored part: velocity={forcePart.Velocity[0]}, position={forcePart.Position[0]}, gravity={vectorForceProject.Workspace.Gravity}.");
forcePart.Position = [0, 4, 0]; forcePart.Velocity = [0, 0, 0]; forcePart.Rotation = [0, 90, 0];
forceNode.RelativeTo = "Attachment0";
new PhysicsSimulationService(vectorForceProject, _ => { }).Step(.1f);
if (Math.Abs(forcePart.Velocity[0]) > .001f || Math.Abs(forcePart.Velocity[2]) < .9f)
    throw new Exception("VectorForce did not follow Attachment0 orientation.");
forceNode.Enabled = false;
forcePart.Velocity = [0, 0, 0];
new PhysicsSimulationService(vectorForceProject, _ => { }).Step(.1f);
if (forcePart.Velocity.Any(value => Math.Abs(value) > .001f))
    throw new Exception("Disabled VectorForce still accelerated its part.");
forceNode.Enabled = true;
forceNode.RelativeTo = "World";
forcePart.Position = [0, 4, 0]; forcePart.Rotation = [0, 0, 0]; forcePart.Velocity = [0, 0, 0];
var weldedForcePart = new SceneNode { Type = "Part", Name = "WeldedForcePart", Position = [3, 4, 0], Anchored = false };
vectorForceProject.Workspace.Children.Add(weldedForcePart);
vectorForceProject.Workspace.Children.Add(new SceneNode { Type = "WeldConstraint", Part0 = forcePart.Id, Part1 = weldedForcePart.Id });
new PhysicsSimulationService(vectorForceProject, _ => { }).Step(.1f);
if (Math.Abs(forcePart.Velocity[0] - .5f) > .001f || Math.Abs(weldedForcePart.Velocity[0] - .5f) > .001f
    || Math.Abs((weldedForcePart.Position[0] - forcePart.Position[0]) - 3) > .001f)
    throw new Exception("VectorForce did not move the welded assembly using its combined mass.");
weldedForcePart.Anchored = true;
new PhysicsSimulationService(vectorForceProject, _ => { }).Step(.1f);
if (forcePart.Velocity.Any(value => Math.Abs(value) > .001f) || weldedForcePart.Velocity.Any(value => Math.Abs(value) > .001f))
    throw new Exception("VectorForce moved an assembly with an anchored member.");
forceNode.RelativeTo = "Attachment0";
var clonedForcePart = GameProjectStore.CloneSubtree(forcePart, out _) ?? throw new Exception("VectorForce parent did not clone.");
var clonedAttachment = clonedForcePart.Children.Single(node => node.Type == "Attachment");
if (clonedAttachment.Children.Single(node => node.Type == "VectorForce").Attachment0 != clonedAttachment.Id)
    throw new Exception("VectorForce clone did not reconnect Attachment0.");
var vectorForceJson = JsonSerializer.Serialize(vectorForceProject, GameProjectStore.JsonOptions);
var restoredVectorForceProject = JsonSerializer.Deserialize<GameProject>(vectorForceJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("VectorForce project did not deserialize.");
GameProjectStore.EnsureProject(restoredVectorForceProject);
var restoredForce = restoredVectorForceProject.Workspace.Children.Single(node => node.Id == forcePart.Id)
    .Children.Single(node => node.Id == forceAttachment.Id).Children.Single(node => node.Type == "VectorForce");
if (restoredForce.Attachment0 != forceAttachment.Id || restoredForce.Force[0] != 80 || restoredForce.RelativeTo != "Attachment0")
    throw new Exception("VectorForce settings did not persist.");
}

{
var velocityProject = new GameProject();
velocityProject.Workspace.Gravity = 100;
var velocityPart = new SceneNode { Type = "Part", Name = "VelocityPart", Position = [0, 50, 0], Anchored = false, CanCollide = false };
var velocityAttachment = new SceneNode { Type = "Attachment", Name = "VelocityAttachment" };
velocityProject.Workspace.Children.Add(velocityPart);
velocityPart.Children.Add(velocityAttachment);
if (!GameProjectStore.CanParentNode(velocityProject, new SceneNode { Type = "LinearVelocity" }, velocityAttachment)
    || GameProjectStore.CanParentNode(velocityProject, new SceneNode { Type = "LinearVelocity" }, velocityProject.Gui))
    throw new Exception("LinearVelocity parent rules failed.");
var velocityOutput = new List<string>();
var velocityRuntime = new LuaXRuntime(velocityProject, output: velocityOutput.Add);
var velocityScript = new GameScript
{
    Source = """
        local attachment = workspace.VelocityPart.VelocityAttachment
        local mover = Instance.new('LinearVelocity', attachment)
        mover.VectorVelocity = Vector3.new(6, 0, 0)
        mover.VelocityConstraintMode = Enum.VelocityConstraintMode.Vector
        mover.RelativeTo = Enum.ActuatorRelativeTo.World
        mover.ForceLimitsEnabled = false
        print('linear-velocity', typeof(mover), typeof(mover.VectorVelocity), mover.Attachment0 == attachment, mover.VelocityConstraintMode)
        mover:SetProperty('MaxForce', 80)
        """
};
velocityProject.Scripts.Add(velocityScript);
velocityRuntime.Run(velocityScript.Id);
var velocityNode = velocityAttachment.Children.Single(node => node.Type == "LinearVelocity");
if (!velocityOutput.Contains("linear-velocity\tLinearVelocity\tVector3\ttrue\tVector")
    || velocityNode.Attachment0 != velocityAttachment.Id || velocityNode.VectorVelocity[0] != 6
    || velocityNode.MaxForce != 80 || velocityNode.ForceLimitsEnabled)
    throw new Exception("LinearVelocity Lua X properties failed: " + string.Join(" | ", velocityOutput));
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0] - 6) > .001f || Math.Abs(velocityPart.Velocity[1]) > .001f)
    throw new Exception("LinearVelocity Vector mode did not hold target velocity against gravity.");
velocityProject.Workspace.Gravity = 0;
velocityNode.VelocityConstraintMode = "Line";
velocityNode.LineDirection = [1, 0, 0]; velocityNode.LineVelocity = 4;
velocityPart.Velocity = [1, 2, 3];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0] - 4) > .001f || Math.Abs(velocityPart.Velocity[1] - 2) > .001f || Math.Abs(velocityPart.Velocity[2] - 3) > .001f)
    throw new Exception("LinearVelocity Line mode changed an unconstrained axis.");
velocityNode.VelocityConstraintMode = "Plane";
velocityNode.PlaneVelocity = [3, 5];
velocityPart.Velocity = [0, 7, 0];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0] - 3) > .001f || Math.Abs(velocityPart.Velocity[1] - 7) > .001f || Math.Abs(velocityPart.Velocity[2] - 5) > .001f)
    throw new Exception("LinearVelocity Plane mode did not preserve normal velocity.");
velocityNode.VelocityConstraintMode = "Vector"; velocityNode.RelativeTo = "Attachment0";
velocityPart.Rotation = [0, 90, 0]; velocityPart.Velocity = [0, 0, 0];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0]) > .001f || Math.Abs(velocityPart.Velocity[2]) < 5.9f)
    throw new Exception("LinearVelocity did not follow Attachment0 orientation.");
velocityNode.RelativeTo = "World"; velocityNode.VectorVelocity = [10, 0, 0]; velocityNode.ForceLimitsEnabled = true;
velocityPart.Rotation = [0, 0, 0]; velocityPart.Velocity = [0, 0, 0];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0] - 1) > .001f)
    throw new Exception("LinearVelocity MaxForce did not limit velocity change by part mass.");
var secondVelocityPart = new SceneNode { Type = "Part", Name = "SecondVelocityPart", Position = [12, 50, 0], Anchored = false, CanCollide = false };
velocityProject.Workspace.Children.Add(secondVelocityPart);
velocityProject.Workspace.Children.Add(new SceneNode { Type = "WeldConstraint", Part0 = velocityPart.Id, Part1 = secondVelocityPart.Id });
velocityPart.Velocity = [0, 0, 0]; secondVelocityPart.Velocity = [0, 0, 0];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (Math.Abs(velocityPart.Velocity[0] - .5f) > .001f || Math.Abs(secondVelocityPart.Velocity[0] - .5f) > .001f)
    throw new Exception("LinearVelocity MaxForce ignored welded assembly mass.");
velocityNode.Enabled = false;
velocityPart.Velocity = [0, 0, 0]; secondVelocityPart.Velocity = [0, 0, 0];
new PhysicsSimulationService(velocityProject, _ => { }).Step(.1f);
if (velocityPart.Velocity.Any(value => Math.Abs(value) > .001f))
    throw new Exception("Disabled LinearVelocity still drove its assembly.");
var clonedVelocityPart = GameProjectStore.CloneSubtree(velocityPart, out _) ?? throw new Exception("LinearVelocity parent did not clone.");
var clonedVelocityAttachment = clonedVelocityPart.Children.Single(node => node.Type == "Attachment");
if (clonedVelocityAttachment.Children.Single(node => node.Type == "LinearVelocity").Attachment0 != clonedVelocityAttachment.Id)
    throw new Exception("LinearVelocity clone did not reconnect Attachment0.");
var velocityJson = JsonSerializer.Serialize(velocityProject, GameProjectStore.JsonOptions);
var restoredVelocityProject = JsonSerializer.Deserialize<GameProject>(velocityJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("LinearVelocity project did not deserialize.");
GameProjectStore.EnsureProject(restoredVelocityProject);
var restoredVelocity = restoredVelocityProject.Workspace.Children.Single(node => node.Id == velocityPart.Id)
    .Children.Single(node => node.Id == velocityAttachment.Id).Children.Single(node => node.Type == "LinearVelocity");
if (restoredVelocity.Attachment0 != velocityAttachment.Id || restoredVelocity.VectorVelocity[0] != 10
    || restoredVelocity.PlaneVelocity[0] != 3 || restoredVelocity.LineVelocity != 4
    || restoredVelocity.MaxForce != 80 || !restoredVelocity.ForceLimitsEnabled)
    throw new Exception("LinearVelocity settings did not persist.");
}

var attributeProject = new GameProject();
var attributedPart = new SceneNode { Type = "Part", Name = "AttributedPart" };
attributeProject.Workspace.Children.Add(attributedPart);
var attributeOutput = new List<string>();
var attributeRuntime = new LuaXRuntime(attributeProject, output: attributeOutput.Add);
var attributeScript = new GameScript
{
    Source = """
        local part = workspace.AttributedPart
        local healthSignal = part:GetAttributeChangedSignal('Health')
        print('attribute-signal-identity', healthSignal == part:GetAttributeChangedSignal('Health'))
        part.AttributeChanged:Connect(function(name) print('attribute-any', name) end)
        healthSignal:Connect(function() print('attribute-health') end)
        part:SetAttribute('Health', 25)
        part:SetAttribute('Health', 25)
        part:SetAttribute('Offset', Vector3.new(1, 2, 3))
        part:SetAttribute('Tint', Color3.fromRGB(10, 20, 30))
        part:SetAttribute('Enabled', true)
        part:SetAttribute('Label', 'hello')
        local attrs = part:GetAttributes()
        print('attribute-values', part:GetAttribute('Health'), attrs.Offset.Y, attrs.Tint.R > 0, attrs.Enabled, attrs.Label)
        part:SetAttribute('Health', nil)
        print('attribute-deleted', part:GetAttribute('Health') == nil)
        """
};
attributeProject.Scripts.Add(attributeScript);
attributeRuntime.Run(attributeScript.Id);
if (!attributeOutput.Contains("attribute-signal-identity\ttrue")
    || !attributeOutput.Contains("attribute-values\t25\t2\ttrue\ttrue\thello")
    || !attributeOutput.Contains("attribute-deleted\ttrue")
    || attributeOutput.Count(line => line == "attribute-health") != 2
    || attributeOutput.Count(line => line == "attribute-any\tHealth") != 2
    || attributedPart.Attributes.ContainsKey("Health")
    || attributedPart.Attributes["Offset"].Vector3Value[1] != 2
    || attributedPart.Attributes["Tint"].Color3Value != "#0A141E")
    throw new Exception("Typed attributes or attribute signals failed: " + string.Join(" | ", attributeOutput));
var disconnectScript = new GameScript
{
    Source = """
        local part = workspace.AttributedPart
        local connection = part:GetAttributeChangedSignal('Health'):Connect(function() print('disconnected-attribute') end)
        print('attribute-connection', typeof(connection), connection.Connected)
        connection:Disconnect()
        print('attribute-disconnected', connection.Connected)
        part:SetAttribute('Health', 9)
        local attrs = part:GetAttributes()
        attrs.Label = 'changed copy'
        print('attribute-copy', part:GetAttribute('Label'))
        """
};
attributeProject.Scripts.Add(disconnectScript);
attributeRuntime.Run(disconnectScript.Id);
if (!attributeOutput.Contains("attribute-connection\tRBXScriptConnection\ttrue")
    || !attributeOutput.Contains("attribute-disconnected\tfalse")
    || !attributeOutput.Contains("attribute-copy\thello")
    || attributeOutput.Contains("disconnected-attribute"))
    throw new Exception("Attribute connection or GetAttributes copy semantics failed: " + string.Join(" | ", attributeOutput));
var clonedAttributedPart = GameProjectStore.CloneSubtree(attributedPart, out _)
    ?? throw new Exception("Attributed part did not clone.");
if (clonedAttributedPart.Attributes["Label"].StringValue != "hello") throw new Exception("Cloning lost attributes.");
var attributeJson = JsonSerializer.Serialize(attributeProject, GameProjectStore.JsonOptions);
var restoredAttributeProject = JsonSerializer.Deserialize<GameProject>(attributeJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Attribute project did not deserialize.");
GameProjectStore.EnsureProject(restoredAttributeProject);
if (restoredAttributeProject.Workspace.Children.Single(node => node.Id == attributedPart.Id).Attributes["Tint"].Color3Value != "#0A141E")
    throw new Exception("Attributes did not persist.");

var tagProject = new GameProject();
var taggedPart = new SceneNode { Type = "Part", Name = "TaggedPart", Tags = ["Enemy", "Enemy", " "] };
tagProject.Workspace.Children.Add(taggedPart);
GameProjectStore.EnsureProject(tagProject);
if (!taggedPart.Tags.SequenceEqual(["Enemy"])) throw new Exception("Duplicate or blank tags were not normalized.");
var clonedTaggedPart = GameProjectStore.CloneSubtree(taggedPart, out _)
    ?? throw new Exception("Tagged part did not clone.");
if (!clonedTaggedPart.Tags.SequenceEqual(["Enemy"])) throw new Exception("Cloning lost tags.");
var tagJson = JsonSerializer.Serialize(tagProject, GameProjectStore.JsonOptions);
var restoredTagProject = JsonSerializer.Deserialize<GameProject>(tagJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("Tag project did not deserialize.");
GameProjectStore.EnsureProject(restoredTagProject);
if (!restoredTagProject.Workspace.Children.Single(node => node.Id == taggedPart.Id).Tags.SequenceEqual(["Enemy"]))
    throw new Exception("Tags did not persist.");
var tagOutput = new List<string>();
var tagRuntime = new LuaXRuntime(tagProject, output: tagOutput.Add);
var tagScript = new GameScript
{
    Source = """
        local tags = game:GetService('CollectionService')
        local part = workspace.TaggedPart
        print('tag-service', tags == game.CollectionService, typeof(tags), tags.ClassName)
        print('tag-initial', #tags:GetTagged('Enemy'), #tags:GetTags(part), tags:HasTag(part, 'Enemy'), part:HasTag('Enemy'))
        print('tag-signal-identity', tags:GetInstanceAddedSignal('Enemy') == tags:GetInstanceAddedSignal('Enemy'))
        tags:GetInstanceAddedSignal('Enemy'):Connect(function(instance) print('tag-added', instance.Name) end)
        tags:GetInstanceRemovedSignal('Enemy'):Connect(function(instance) print('tag-removed', instance.Name) end)
        part:AddTag('Enemy')
        tags:RemoveTag(part, 'Enemy')
        tags:AddTag(part, 'Enemy')
        local model = Instance.new('Model')
        model.Name = 'TaggedModel'
        local child = Instance.new('Part', model)
        child.Name = 'TaggedChild'
        child:AddTag('Enemy')
        print('tag-detached', #tags:GetTagged('Enemy'), #child:GetTags())
        model:SetProperty('Parent', workspace)
        print('tag-attached', #tags:GetTagged('Enemy'))
        model:SetProperty('Parent', nil)
        print('tag-detached-again', #tags:GetTagged('Enemy'))
        part:Destroy()
        print('tag-final', #tags:GetTagged('Enemy'), #tags:GetAllTags())
        """
};
tagProject.Scripts.Add(tagScript);
tagRuntime.Run(tagScript.Id);
if (!tagOutput.Contains("tag-service\ttrue\tCollectionService\tCollectionService")
    || !tagOutput.Contains("tag-initial\t1\t1\ttrue\ttrue")
    || !tagOutput.Contains("tag-signal-identity\ttrue")
    || !tagOutput.Contains("tag-detached\t1\t1")
    || !tagOutput.Contains("tag-attached\t2")
    || !tagOutput.Contains("tag-detached-again\t1")
    || !tagOutput.Contains("tag-final\t0\t0")
    || tagOutput.Count(line => line == "tag-added\tTaggedPart") != 1
    || tagOutput.Count(line => line == "tag-added\tTaggedChild") != 1
    || tagOutput.Count(line => line == "tag-removed\tTaggedPart") != 2
    || tagOutput.Count(line => line == "tag-removed\tTaggedChild") != 1)
    throw new Exception("Tag queries or lifecycle signals failed: " + string.Join(" | ", tagOutput));

var frameProject = new GameProject();
frameProject.Workspace.Gravity = 0;
var frameProbe = new SceneNode { Type = "Part", Name = "FrameProbe", Position = [0, 2, 0], Anchored = false };
frameProject.Workspace.Children.Add(frameProbe);
var frameScriptNode = new SceneNode { Type = "Script", Name = "FrameListener" };
frameProject.ServerScriptService.Children.Add(frameScriptNode);
frameProject.Scripts.Add(new GameScript
{
    Id = frameScriptNode.Id,
    Name = frameScriptNode.Name,
    Source = """
        local rs = game:GetService('RunService')
        print('frame-service', rs == game.RunService, typeof(rs), rs:IsRunning(), rs:IsStudio(), rs:IsEdit())
        local pre
        pre = rs.PreSimulation:Connect(function(dt)
            print('frame-pre', dt)
            workspace.FrameProbe.Velocity = Vector3.new(0, 10, 0)
            pre:Disconnect()
        end)
        rs.PostSimulation:Connect(function(dt) print('frame-post', dt, workspace.FrameProbe.Position.Y) end)
        rs.Heartbeat:Connect(function(dt) print('frame-heartbeat', dt) end)
        rs.PreRender:Connect(function(dt) print('frame-render', dt) end)
        """
});
var frameOutput = new List<string>();
var frameRuntime = new LuaXRuntime(frameProject, output: frameOutput.Add);
frameRuntime.Run(frameScriptNode.Id);
var framePhysics = new PhysicsSimulationService(frameProject, node =>
{
    frameRuntime.SynchronizeExternalProperty(node, "Position");
    frameRuntime.SynchronizeExternalProperty(node, "Velocity");
});
frameRuntime.UpdateBeforePhysics(.1f);
if (frameProbe.Velocity[1] != 10) throw new Exception("PreSimulation did not run before physics.");
framePhysics.Step(.1f);
frameRuntime.UpdateTouched(.1f);
var postLine = frameOutput.FirstOrDefault(line => line.StartsWith("frame-post\t", StringComparison.Ordinal));
if (Math.Abs(frameProbe.Position[1] - 3) > .001f
    || !frameOutput.Contains("frame-service\ttrue\tRunService\ttrue\ttrue\tfalse")
    || postLine is null
    || !double.TryParse(postLine.Split('\t')[^1], System.Globalization.CultureInfo.InvariantCulture, out var postPosition)
    || Math.Abs(postPosition - 3) > .001)
    throw new Exception("RunService context or post-physics position failed: " + string.Join(" | ", frameOutput));
var preIndex = frameOutput.FindIndex(line => line.StartsWith("frame-pre\t", StringComparison.Ordinal));
var postIndex = frameOutput.FindIndex(line => line.StartsWith("frame-post\t", StringComparison.Ordinal));
var heartbeatIndex = frameOutput.FindIndex(line => line.StartsWith("frame-heartbeat\t", StringComparison.Ordinal));
var renderIndex = frameOutput.FindIndex(line => line.StartsWith("frame-render\t", StringComparison.Ordinal));
if (preIndex < 0 || postIndex <= preIndex || heartbeatIndex <= postIndex || renderIndex <= heartbeatIndex)
    throw new Exception("RunService frame phases fired out of order: " + string.Join(" | ", frameOutput));
frameRuntime.UpdateBeforePhysics(.1f);
framePhysics.Step(.1f);
frameRuntime.UpdateTouched(.1f);
if (frameOutput.Count(line => line.StartsWith("frame-pre\t", StringComparison.Ordinal)) != 1
    || frameOutput.Count(line => line.StartsWith("frame-heartbeat\t", StringComparison.Ordinal)) != 2)
    throw new Exception("RunService connection lifecycle failed: " + string.Join(" | ", frameOutput));
var frameDisabler = new GameScript { Source = "game.ServerScriptService.FrameListener.Enabled = false" };
frameProject.Scripts.Add(frameDisabler);
frameRuntime.Run(frameDisabler.Id);
frameRuntime.UpdateBeforePhysics(.1f);
framePhysics.Step(.1f);
frameRuntime.UpdateTouched(.1f);
if (frameOutput.Count(line => line.StartsWith("frame-heartbeat\t", StringComparison.Ordinal)) != 2)
    throw new Exception("A disabled Script kept receiving RunService signals: " + string.Join(" | ", frameOutput));

Console.WriteLine("Object and runtime smoke passed.");
