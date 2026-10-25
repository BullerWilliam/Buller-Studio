using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class PlayerCharacterService
{
    private readonly GameProject project;
    private readonly SceneNode player;
    private readonly (SceneNode Node, float[] Offset)[] body;
    private readonly Dictionary<string, SceneNode> motors = new(StringComparer.Ordinal);
    private float facingYaw;
    private float gaitPhase;
    private float gaitStrength;

    public PlayerCharacterService(GameProject project, SceneNode player)
    {
        this.project = project;
        this.player = player;
        var name = "PlayerCharacter";
        var existingNames = GameProjectStore.Flatten(project.Workspace.Children).Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        for (var suffix = 2; existingNames.Contains(name); suffix++) name = $"PlayerCharacter{suffix}";
        var template = project.StarterPlayer.Children.FirstOrDefault(node => node.Type == "Model" && node.Name == "StarterCharacter");
        var renewedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var customCharacter = template is not null ? GameProjectStore.CloneSubtree(template, out renewedIds) : null;
        RenewedIds = template is not null && customCharacter is not null ? renewedIds : new Dictionary<string, string>(StringComparer.Ordinal);
        if (customCharacter is not null)
        {
            Model = customCharacter;
            Model.Name = name;
            Root = GameProjectStore.Flatten(Model.Children).FirstOrDefault(node => node.Name == "HumanoidRootPart" && SceneNodeTypes.IsBasePart(node))
                ?? (SceneModelService.ContainsPrimaryPart(Model, Model.PrimaryPart)
                    ? GameProjectStore.Flatten(Model.Children).First(node => node.Id == Model.PrimaryPart)
                    : GameProjectStore.Flatten(Model.Children).FirstOrDefault(SceneNodeTypes.IsBasePart))
                ?? CreateBodyPart("HumanoidRootPart", "Block", [1.4f, 3.6f, 1.4f], "#6688CC");
            if (!GameProjectStore.Flatten(Model.Children).Any(node => node.Id == Root.Id)) Model.Children.Add(Root);
            Root.Name = "HumanoidRootPart";
            Root.Anchored = false;
            Humanoid = GameProjectStore.Flatten(Model.Children).FirstOrDefault(node => node.Type == "Humanoid")
                ?? new SceneNode { Type = "Humanoid", Name = "Humanoid" };
            if (!GameProjectStore.Flatten(Model.Children).Any(node => node.Id == Humanoid.Id)) Model.Children.Add(Humanoid);
            Humanoid.Health = Humanoid.MaxHealth;
            if (!Model.Children.Any(node => node.Type == "BodyColors")) Model.Children.Add(new SceneNode { Type = "BodyColors", Name = "Body Colors" });
            Model.PrimaryPart = Root.Id;
            Model.HasModelPivot = true;
            SceneModelService.MoveTo(Model, FindSpawnPosition());
            body = GameProjectStore.Flatten(Model.Children).Where(SceneNodeTypes.IsBasePart)
                .Select(part => (part, new float[] { 0, 0, 0 })).ToArray();
            foreach (var motor in GameProjectStore.Flatten(Model.Children).Where(node => node.Type == "Motor6D")) motors[motor.Name] = motor;
        }
        else
        {
            Model = new SceneNode { Type = "Model", Name = name };
            Root = new SceneNode
            {
                Type = "Part", Name = "HumanoidRootPart", Shape = "Block", Size = [1.4f, 3.6f, 1.4f],
                Position = FindSpawnPosition(), Anchored = false, CanCollide = true,
                Transparency = 1, CastShadow = false, Color = "#6688CC"
            };
            Model.Children.Add(Root);
            Humanoid = new SceneNode { Type = "Humanoid", Name = "Humanoid" };
            Model.Children.Add(Humanoid);
            Model.Children.Add(new SceneNode { Type = "BodyColors", Name = "Body Colors" });
            body =
            [
                (CreateBodyPart("Torso", "Block", [1.35f, 1.7f, .8f], "#3973CF"), [0, .12f, 0]),
                (CreateBodyPart("Head", "Ball", [1.15f, 1.15f, 1.15f], "#E3B98A"), [0, 1.32f, 0]),
                (CreateBodyPart("LeftArm", "Block", [.42f, 1.5f, .42f], "#E3B98A"), [-1.02f, .08f, 0]),
                (CreateBodyPart("RightArm", "Block", [.42f, 1.5f, .42f], "#E3B98A"), [1.02f, .08f, 0]),
                (CreateBodyPart("LeftLeg", "Block", [.52f, 1.35f, .52f], "#343B58"), [-.37f, -1.05f, 0]),
                (CreateBodyPart("RightLeg", "Block", [.52f, 1.35f, .52f], "#343B58"), [.37f, -1.05f, 0])
            ];
            foreach (var (part, offset) in body)
            {
                part.Position = [Root.Position[0] + offset[0], Root.Position[1] + offset[1], Root.Position[2] + offset[2]];
                Model.Children.Add(part);
            }
            var torso = body[0].Node;
            AddMotor("RootJoint", Root, torso, [0, .12f, 0], [0, 0, 0]);
            AddMotor("Neck", torso, body[1].Node, [0, .625f, 0], [0, -.575f, 0]);
            AddMotor("LeftShoulder", torso, body[2].Node, [-.84f, .60f, 0], [.18f, .64f, 0]);
            AddMotor("RightShoulder", torso, body[3].Node, [.84f, .60f, 0], [-.18f, .64f, 0]);
            AddMotor("LeftHip", torso, body[4].Node, [-.37f, -.5f, 0], [0, .67f, 0]);
            AddMotor("RightHip", torso, body[5].Node, [.37f, -.5f, 0], [0, .67f, 0]);
        }
        Humanoid.WalkSpeed = Math.Clamp(project.StarterPlayer.WalkSpeed, 0, 1000);
        Humanoid.JumpPower = Math.Clamp(project.StarterPlayer.JumpPower, 0, 1000);
        SpawnProtection = new SceneNode { Type = "ForceField", Name = "ForceField", Enabled = SpawnLocation?.Duration > 0 };
        Model.Children.Add(SpawnProtection);
        project.Workspace.Children.Add(Model);
        UpdatePose();
    }

    public SceneNode Model { get; }
    public IReadOnlyDictionary<string, string> RenewedIds { get; }
    public SceneNode Root { get; }
    public SceneNode Humanoid { get; }
    public SceneNode SpawnProtection { get; }
    public SceneNode? SpawnLocation { get; private set; }
    public bool IsSeated { get; set; }
    public bool NeedsRespawn => PhysicsSimulationService.IsPartBelowDestroyHeight(Root, project.Workspace.FallenPartsDestroyHeight) || Humanoid.Health <= 0;

    public void SetFacingYaw(float yaw) => facingYaw = yaw;

    public void ApplyInput(PhysicsSimulationService physics, float cameraYaw, float forward, float strafe, bool jump, float elapsedSeconds = .02f)
    {
        var elapsed = Math.Clamp(float.IsFinite(elapsedSeconds) ? elapsedSeconds : .02f, .001f, .25f);
        if (Humanoid.Health <= 0)
        {
            Root.Velocity[0] = 0;
            Root.Velocity[2] = 0;
            gaitStrength = Math.Max(0, gaitStrength - elapsed * 8);
            return;
        }
        var yaw = cameraYaw * MathF.PI / 180;
        var x = strafe * MathF.Cos(yaw) - forward * MathF.Sin(yaw);
        var z = -strafe * MathF.Sin(yaw) - forward * MathF.Cos(yaw);
        var length = MathF.Sqrt(x * x + z * z);
        if (length > 1) { x /= length; z /= length; length = 1; }
        Root.Velocity[0] = x * Math.Clamp(Humanoid.WalkSpeed, 0, 1000);
        Root.Velocity[2] = z * Math.Clamp(Humanoid.WalkSpeed, 0, 1000);
        var walkSpeed = Math.Clamp(Humanoid.WalkSpeed, 0, 1000);
        var targetGaitStrength = walkSpeed > .001f ? length * Math.Clamp(walkSpeed / 7.5f, 0, 1) : 0;
        gaitStrength += (targetGaitStrength - gaitStrength) * Math.Clamp(elapsed * 10, 0, 1);
        if (length > .001f) gaitPhase += elapsed * Math.Clamp(walkSpeed / 7.5f, .25f, 3) * 8;
        if (jump && physics.IsGrounded(Root)) Root.Velocity[1] = Math.Clamp(Humanoid.JumpPower, 0, 1000);
        if (length > .001f) facingYaw = MathF.Atan2(-x, -z) * 180 / MathF.PI;
    }

    public IReadOnlyList<SceneNode> UpdatePose()
    {
        var changed = new List<SceneNode>();
        if (Root.Rotation[1] != facingYaw)
        {
            Root.Rotation[1] = facingYaw;
            changed.Add(Root);
        }
        var wave = IsSeated ? 0 : MathF.Sin(gaitPhase) * gaitStrength;
        var bounce = IsSeated ? 0 : MathF.Abs(MathF.Sin(gaitPhase * 2)) * gaitStrength * .035f;
        var airborne = !IsSeated && Root.Velocity[1] > 1.5f;
        SetMotorTransform("RootJoint", [0, bounce, 0], [0, 0, 0], changed);
        foreach (var (part, _) in body)
        {
            var pitch = 0f;
            var isLeg = part.Name.EndsWith("Leg", StringComparison.Ordinal);
            var transformPosition = new float[] { 0, 0, 0 };
            if (IsSeated && isLeg)
            {
                transformPosition[2] = -.45f;
                pitch = -12;
            }
            else if (part.Name is "LeftLeg" or "RightLeg" or "LeftArm" or "RightArm")
            {
                var leftSide = part.Name.StartsWith("Left", StringComparison.Ordinal);
                var opposingSide = leftSide ? -wave : wave;
                var arm = part.Name.EndsWith("Arm", StringComparison.Ordinal);
                pitch = airborne
                    ? arm ? -28 : 14
                    : (arm ? opposingSide : -opposingSide) * 28;
            }
            var motorName = part.Name switch
            {
                "Head" => "Neck",
                "LeftArm" => "LeftShoulder",
                "RightArm" => "RightShoulder",
                "LeftLeg" => "LeftHip",
                "RightLeg" => "RightHip",
                _ => string.Empty
            };
            if (motorName.Length > 0)
                SetMotorTransform(motorName, transformPosition, [-pitch, 0, 0], changed);
        }
        return changed;
    }

    public IReadOnlyList<SceneNode> Respawn()
    {
        Root.Position = FindSpawnPosition();
        Root.Velocity = [0, 0, 0];
        Root.Anchored = false;
        IsSeated = false;
        Humanoid.Health = Humanoid.MaxHealth;
        facingYaw = 0;
        gaitStrength = 0;
        gaitPhase = 0;
        return new[] { Root }.Concat(UpdatePose()).ToArray();
    }

    private SceneNode CreateBodyPart(string name, string shape, float[] size, string color) => new()
    {
        Type = "Part", Name = name, Shape = shape, Size = size, Color = color,
        Anchored = false, CanCollide = false, CanTouch = false
    };

    private void AddMotor(string name, SceneNode part0, SceneNode part1, float[] part0Offset, float[] part1Offset)
    {
        var motor = new SceneNode
        {
            Type = "Motor6D", Name = name, Part0 = part0.Id, Part1 = part1.Id,
            Part0OffsetPosition = part0Offset, Part1OffsetPosition = part1Offset,
            Enabled = true
        };
        part0.Children.Add(motor);
        motors[name] = motor;
    }

    private void SetMotorTransform(string name, float[] position, float[] rotation, List<SceneNode> changed)
    {
        if (!motors.TryGetValue(name, out var motor)) return;
        var normalizedRotation = rotation.Select(value => Math.Clamp(value, -360000, 360000)).ToArray();
        if (motor.MotorTransformPosition.SequenceEqual(position) && motor.MotorTransformRotation.SequenceEqual(normalizedRotation)) return;
        motor.MotorTransformPosition = (float[])position.Clone();
        motor.MotorTransformRotation = normalizedRotation;
        changed.Add(motor);
    }

    private float[] FindSpawnPosition()
    {
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        SpawnLocation = null;
        var pad = parts.FirstOrDefault(node => node.Type == "SpawnLocation" && node.Enabled && IsSpawnAllowed(node));
        if (pad is not null)
        {
            SpawnLocation = pad;
            return [pad.Position[0], (CollisionGeometry.TopYAt(pad, pad.Position[0], pad.Position[2]) ?? CollisionGeometry.TopY(pad)) + 1.801f, pad.Position[2]];
        }

        var ground = parts.Where(node => node.Type != "SpawnLocation" && node.Anchored && node.CanCollide
                && CollisionGeometry.TopYAt(node, 0, 0) is not null)
            .OrderByDescending(node => Math.Abs(node.Size[0] * node.Size[2]))
            .FirstOrDefault();
        var top = ground is null ? 0 : CollisionGeometry.TopYAt(ground, 0, 0) ?? 0;
        return [0, top + 1.801f, 0];
    }

    private bool IsSpawnAllowed(SceneNode spawn)
    {
        if (spawn.Neutral) return true;
        var team = project.Teams.Children.FirstOrDefault(node => node.Type == "Team" && node.Id == player.Team);
        return team is not null && string.Equals(team.TeamColor, spawn.TeamColor, StringComparison.OrdinalIgnoreCase);
    }
}
