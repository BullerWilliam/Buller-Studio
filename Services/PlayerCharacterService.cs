using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class PlayerCharacterService
{
    private readonly GameProject project;
    private readonly (SceneNode Node, float[] Offset)[] body;
    private float facingYaw;

    public PlayerCharacterService(GameProject project)
    {
        this.project = project;
        var name = "PlayerCharacter";
        var existingNames = GameProjectStore.Flatten(project.Workspace.Children).Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        for (var suffix = 2; existingNames.Contains(name); suffix++) name = $"PlayerCharacter{suffix}";

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
        body =
        [
            (CreateBodyPart("Torso", "Block", [1.35f, 1.7f, .8f], "#3973CF"), [0, .12f, 0]),
            (CreateBodyPart("Head", "Ball", [1.15f, 1.15f, 1.15f], "#E3B98A"), [0, 1.32f, 0]),
            (CreateBodyPart("LeftArm", "Block", [.42f, 1.5f, .42f], "#E3B98A"), [-1.02f, .08f, 0]),
            (CreateBodyPart("RightArm", "Block", [.42f, 1.5f, .42f], "#E3B98A"), [1.02f, .08f, 0]),
            (CreateBodyPart("LeftLeg", "Block", [.52f, 1.35f, .52f], "#343B58"), [-.37f, -1.05f, 0]),
            (CreateBodyPart("RightLeg", "Block", [.52f, 1.35f, .52f], "#343B58"), [.37f, -1.05f, 0])
        ];
        foreach (var (part, _) in body) Model.Children.Add(part);
        project.Workspace.Children.Add(Model);
        UpdatePose();
    }

    public SceneNode Model { get; }
    public SceneNode Root { get; }
    public SceneNode Humanoid { get; }
    public bool IsSeated { get; set; }
    public bool NeedsRespawn => Root.Position[1] < -60 || Humanoid.Health <= 0;

    public void SetFacingYaw(float yaw) => facingYaw = yaw;

    public void ApplyInput(PhysicsSimulationService physics, float cameraYaw, float forward, float strafe, bool jump)
    {
        if (Humanoid.Health <= 0)
        {
            Root.Velocity[0] = 0;
            Root.Velocity[2] = 0;
            return;
        }
        var yaw = cameraYaw * MathF.PI / 180;
        var x = strafe * MathF.Cos(yaw) - forward * MathF.Sin(yaw);
        var z = -strafe * MathF.Sin(yaw) - forward * MathF.Cos(yaw);
        var length = MathF.Sqrt(x * x + z * z);
        if (length > 1) { x /= length; z /= length; }
        Root.Velocity[0] = x * Math.Clamp(Humanoid.WalkSpeed, 0, 1000);
        Root.Velocity[2] = z * Math.Clamp(Humanoid.WalkSpeed, 0, 1000);
        if (jump && physics.IsGrounded(Root)) Root.Velocity[1] = Math.Clamp(Humanoid.JumpPower, 0, 1000);
        if (length > .001f) facingYaw = MathF.Atan2(-x, -z) * 180 / MathF.PI;
    }

    public IReadOnlyList<SceneNode> UpdatePose()
    {
        var changed = new List<SceneNode>();
        var yaw = facingYaw * MathF.PI / 180;
        var cosine = MathF.Cos(yaw);
        var sine = MathF.Sin(yaw);
        foreach (var (part, offset) in body)
        {
            var localY = IsSeated && part.Name.EndsWith("Leg", StringComparison.Ordinal) ? -.5f : offset[1];
            var localZ = IsSeated && part.Name.EndsWith("Leg", StringComparison.Ordinal) ? -.45f : offset[2];
            var position = new[]
            {
                Root.Position[0] + offset[0] * cosine + localZ * sine,
                Root.Position[1] + localY,
                Root.Position[2] - offset[0] * sine + localZ * cosine
            };
            if (part.Position.SequenceEqual(position) && part.Rotation[1] == facingYaw) continue;
            part.Position = position;
            part.Rotation[1] = facingYaw;
            changed.Add(part);
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
        return new[] { Root }.Concat(UpdatePose()).ToArray();
    }

    private SceneNode CreateBodyPart(string name, string shape, float[] size, string color) => new()
    {
        Type = "Part", Name = name, Shape = shape, Size = size, Color = color,
        Anchored = true, CanCollide = false, CanTouch = false
    };

    private float[] FindSpawnPosition()
    {
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        var pad = parts.FirstOrDefault(node => node.Type == "SpawnLocation" && node.Enabled);
        if (pad is not null)
            return [pad.Position[0], pad.Position[1] + Math.Abs(pad.Size[1]) * .5f + 1.801f, pad.Position[2]];

        var ground = parts.Where(node => node.Type != "SpawnLocation" && node.Anchored && node.CanCollide
                && Math.Abs(node.Position[0]) < Math.Abs(node.Size[0]) * .5f
                && Math.Abs(node.Position[2]) < Math.Abs(node.Size[2]) * .5f)
            .OrderByDescending(node => Math.Abs(node.Size[0] * node.Size[2]))
            .FirstOrDefault();
        var top = ground is null ? 0 : ground.Position[1] + Math.Abs(ground.Size[1]) * .5f;
        return [0, top + 1.801f, 0];
    }
}
