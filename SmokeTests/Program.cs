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

Run("""
    local part = Instance.new('Part', workspace)
    part.Position = Vector3.new(1, 2, 3)
    part.Rotation = LookAtRotation(part.Position, Vector3.new(2, 2, 3))
    print('rotation-only', CFrame == nil, part.CFrame == nil, part.Position.X, part.Rotation.Y)
    """);
if (!output.Any(line => line.Contains("rotation-only\ttrue\ttrue\t1\t-90", StringComparison.Ordinal)))
    throw new Exception("Position, Rotation, or LookAtRotation regressed: " + string.Join(" | ", output));

Console.WriteLine("Object and runtime smoke passed.");
