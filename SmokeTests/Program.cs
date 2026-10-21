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
var weldChild = new SceneNode { Type = "Part", Name = "WeldChild", CanCollide = false, Position = [2, 0, 0] };
var weldGrandchild = new SceneNode { Type = "Part", Name = "WeldGrandchild", CanCollide = false, Position = [2, 1, 2], Rotation = [0, 30, 0] };
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
    Source = "local g = Instance.new('UIGradient', game.Gui.ScreenGui.GradientFrame); g.StartColor = Color3.fromRGB(255, 0, 0); g.EndColor = Color3.fromRGB(0, 0, 255); g.Rotation = Vector3.new(0, 0, 90); print('gradient-values', g.StartColor:ToHex(), g.EndColor:ToHex(), g.Rotation.Z)"
};
gradientProject.Scripts.Add(gradientScript);
gradientRuntime.Run(gradientScript.Id);
var gradientNode = gradientFrame.Children.SingleOrDefault(node => node.Type == "UIGradient");
if (gradientNode is null || gradientNode.StartColor != "#FF0000" || gradientNode.EndColor != "#0000FF" || gradientNode.Rotation[2] != 90
    || !gradientOutput.Any(line => line.Contains("gradient-values\tFF0000\t0000FF\t90", StringComparison.Ordinal)))
    throw new Exception("UIGradient Lua X properties failed: " + string.Join(" | ", gradientOutput));
var gradientJson = JsonSerializer.Serialize(gradientProject, GameProjectStore.JsonOptions);
var restoredGradientProject = JsonSerializer.Deserialize<GameProject>(gradientJson, GameProjectStore.JsonOptions)
    ?? throw new Exception("UIGradient project did not deserialize.");
GameProjectStore.EnsureProject(restoredGradientProject);
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
        if (brush is null || brush.GradientStops[0].Color != System.Windows.Media.Colors.Red
            || brush.GradientStops[1].Color != System.Windows.Media.Colors.Blue
            || Math.Abs(brush.StartPoint.X - .5) > .001 || Math.Abs(brush.StartPoint.Y) > .001)
            throw new Exception("UIGradient did not render the saved colors and rotation.");
        var restoredGradient = restoredGradientProject.Gui.Children.Single(node => node.Type == "ScreenGui")
            .Children.Single(node => node.Name == "GradientFrame").Children.Single(node => node.Type == "UIGradient");
        restoredGradient.Enabled = false;
        var plainCanvas = GuiRenderService.Render(restoredGradientProject, false, 200, 100);
        var plainFrame = (System.Windows.Controls.Border)plainCanvas.Children[0];
        var plainVisual = (System.Windows.Controls.Border)((System.Windows.Controls.Grid)plainFrame.Child).Children[0];
        if (plainVisual.Background is not System.Windows.Media.SolidColorBrush plainBrush
            || plainBrush.Color != System.Windows.Media.Color.FromRgb(0x22, 0x44, 0x66))
            throw new Exception("Disabling UIGradient did not restore the frame background.");
    }
    catch (Exception exception) { gradientRenderError = exception; }
});
renderThread.SetApartmentState(ApartmentState.STA);
renderThread.Start();
renderThread.Join();
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

Console.WriteLine("Object and runtime smoke passed.");
