using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class SeatInteractionService
{
    private sealed record VehicleMotor(SceneNode Node, float WheelRadius);
    private sealed record VehicleMotorState(float AngularVelocity, float AngularSpeed, float MaxAcceleration, float TargetAngle);
    private readonly GameProject project;
    private readonly PlayerCharacterService character;
    private SceneNode? seat;
    private float vehicleThrottle;
    private float vehicleSteer;
    private readonly Dictionary<string, (SceneNode Motor, VehicleMotorState Original)> activeVehicleMotors = new(StringComparer.Ordinal);

    public SeatInteractionService(GameProject project, PlayerCharacterService character)
    {
        this.project = project;
        this.character = character;
    }

    public bool IsSeated => seat is not null;
    public SceneNode? VehicleSeat => seat?.Type == "VehicleSeat" ? seat : null;

    public IReadOnlyList<SceneNode> Sit(SceneNode targetSeat, SceneNode humanoid)
    {
        if (targetSeat.Type is not ("Seat" or "VehicleSeat") || !targetSeat.CanCollide
            || targetSeat.VehicleSeatDisabled || humanoid.Id != character.Humanoid.Id || character.Humanoid.Health <= 0
            || !string.IsNullOrEmpty(targetSeat.Occupant) && targetSeat.Occupant != humanoid.Id
            || !GameProjectStore.Flatten(project.Workspace.Children).Any(node => ReferenceEquals(node, targetSeat))) return [];

        var changed = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
        foreach (var node in BeforePhysics(true)) changed[node.Id] = node;
        seat = targetSeat;
        targetSeat.Occupant = humanoid.Id;
        targetSeat.VehicleSeatThrottleFloat = 0;
        targetSeat.VehicleSeatSteerFloat = 0;
        character.IsSeated = true;
        character.Root.Anchored = true;
        character.Root.Position[0] = targetSeat.Position[0];
        character.Root.Position[1] = SeatCenterTop(targetSeat) + 1.25f;
        character.Root.Position[2] = targetSeat.Position[2];
        character.Root.Velocity = [0, 0, 0];
        character.SetFacingYaw(targetSeat.Rotation[1]);
        changed[targetSeat.Id] = targetSeat;
        changed[character.Root.Id] = character.Root;
        return changed.Values.ToArray();
    }

    public void SetVehicleInput(float throttle, float steer)
    {
        vehicleThrottle = Math.Clamp(throttle, -1, 1);
        vehicleSteer = Math.Clamp(steer, -1, 1);
    }

    public IReadOnlyList<SceneNode> UpdateVehicleMotorTargets()
    {
        var nodes = GameProjectStore.Flatten(project.Workspace.Children).ToArray();
        var targetSeat = seat is { Type: "VehicleSeat", VehicleSeatDisabled: false } ? seat : null;
        var targetMotors = targetSeat is null ? [] : FindConnectedMotors(targetSeat, nodes);
        var targetIds = targetMotors.Select(motor => motor.Node.Id).ToHashSet(StringComparer.Ordinal);
        var changed = new Dictionary<string, SceneNode>(StringComparer.Ordinal);

        foreach (var (id, state) in activeVehicleMotors.ToArray())
        {
            if (targetIds.Contains(id)) continue;
            state.Motor.HingeAngularVelocity = state.Original.AngularVelocity;
            state.Motor.HingeAngularSpeed = state.Original.AngularSpeed;
            state.Motor.HingeMotorMaxAcceleration = state.Original.MaxAcceleration;
            state.Motor.HingeTargetAngle = state.Original.TargetAngle;
            changed[id] = state.Motor;
            activeVehicleMotors.Remove(id);
        }

        if (targetSeat is not null)
        {
            targetSeat.VehicleSeatAreHingesDetected = targetMotors.Length;
            foreach (var vehicleMotor in targetMotors)
            {
                var motor = vehicleMotor.Node;
                if (!activeVehicleMotors.ContainsKey(motor.Id))
                    activeVehicleMotors[motor.Id] = (motor, new VehicleMotorState(motor.HingeAngularVelocity, motor.HingeAngularSpeed, motor.HingeMotorMaxAcceleration, motor.HingeTargetAngle));
                if (motor.HingeActuatorType == "Servo")
                {
                    var maxAngle = motor.HingeLimitsEnabled
                        ? Math.Max(Math.Abs(motor.HingeLowerAngle), Math.Abs(motor.HingeUpperAngle))
                        : 45f;
                    var targetAngle = vehicleSteer * Math.Clamp(maxAngle, 0, 180);
                    var angularSpeed = Math.Clamp(targetSeat.VehicleSeatTurnSpeed, 0, 1000);
                    if (motor.HingeTargetAngle == targetAngle && motor.HingeAngularSpeed == angularSpeed) continue;
                    motor.HingeTargetAngle = targetAngle;
                    motor.HingeAngularSpeed = angularSpeed;
                    changed[motor.Id] = motor;
                }
                else
                {
                    var speed = vehicleThrottle * Math.Clamp(targetSeat.VehicleSeatMaxSpeed, 0, 10000) / vehicleMotor.WheelRadius;
                    var acceleration = Math.Clamp(targetSeat.VehicleSeatTorque, 0, 1000000);
                    if (motor.HingeAngularVelocity == speed && motor.HingeMotorMaxAcceleration == acceleration) continue;
                    motor.HingeAngularVelocity = speed;
                    motor.HingeMotorMaxAcceleration = acceleration;
                    changed[motor.Id] = motor;
                }
            }
        }

        return changed.Values.ToArray();
    }

    private VehicleMotor[] FindConnectedMotors(SceneNode seat, IReadOnlyList<SceneNode> nodes)
    {
        var parents = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
        void IndexParents(SceneNode parent)
        {
            foreach (var child in parent.Children) parents[child.Id] = parent;
        }
        foreach (var parent in nodes.Prepend(project.Workspace)) IndexParents(parent);

        string? AttachmentPart(string attachmentId) =>
            nodes.FirstOrDefault(node => node.Id == attachmentId && node.Type == "Attachment") is { } attachment
            && parents.TryGetValue(attachment.Id, out var parent) && SceneNodeTypes.IsBasePart(parent)
                ? parent.Id : null;

        var rigidParts = new HashSet<string>(StringComparer.Ordinal) { seat.Id };
        bool expanded;
        do
        {
            expanded = false;
            foreach (var weld in nodes.Where(node => node.Type is "WeldConstraint" or "Motor6D" && node.Enabled))
            {
                if (rigidParts.Contains(weld.Part0) && rigidParts.Add(weld.Part1)) expanded = true;
                if (rigidParts.Contains(weld.Part1) && rigidParts.Add(weld.Part0)) expanded = true;
            }
            foreach (var rigid in nodes.Where(node => node.Type == "RigidConstraint" && node.Enabled))
            {
                var first = AttachmentPart(rigid.Attachment0);
                var second = AttachmentPart(rigid.Attachment1);
                if (first is not null && rigidParts.Contains(first) && second is not null && rigidParts.Add(second)) expanded = true;
                if (second is not null && rigidParts.Contains(second) && first is not null && rigidParts.Add(first)) expanded = true;
            }
        } while (expanded);

        return nodes.Where(node => (node.Type is "HingeConstraint" or "CylindricalConstraint")
                && node.Enabled && (node.HingeActuatorType is "Motor" or "Servo"))
            .Where(node => rigidParts.Contains(AttachmentPart(node.Attachment0) ?? string.Empty)
                ^ rigidParts.Contains(AttachmentPart(node.Attachment1) ?? string.Empty))
            .Select(motor =>
            {
                var part0 = AttachmentPart(motor.Attachment0);
                var part1 = AttachmentPart(motor.Attachment1);
                var wheelId = rigidParts.Contains(part0 ?? string.Empty) ? part1 : part0;
                var wheel = nodes.FirstOrDefault(node => node.Id == wheelId);
                var radius = wheel is null ? 1 : Math.Max(.05f, Math.Max(Math.Abs(wheel.Size[0]), Math.Abs(wheel.Size[2])) * .5f);
                return new VehicleMotor(motor, radius);
            })
            .ToArray();
    }

    public IReadOnlyList<SceneNode> BeforePhysics(bool jump)
    {
        if (seat is null) return [];
        var stillPresent = GameProjectStore.Flatten(project.Workspace.Children).Any(node => ReferenceEquals(node, seat));
        if (stillPresent && seat.CanCollide && !seat.VehicleSeatDisabled && character.Humanoid.Health > 0 && !jump) return [];

        var oldSeat = seat;
        seat = null;
        oldSeat.Occupant = string.Empty;
        oldSeat.VehicleSeatThrottleFloat = 0;
        oldSeat.VehicleSeatSteerFloat = 0;
        oldSeat.VehicleSeatAreHingesDetected = 0;
        character.IsSeated = false;
        character.Root.Anchored = false;
        character.Root.Position[1] = SeatCenterTop(oldSeat) + Math.Abs(character.Root.Size[1]) * .5f + .001f;
        character.Root.Velocity = [0, 0, 0];
        return [oldSeat, character.Root];
    }

    public IReadOnlyList<SceneNode> AfterPhysics()
    {
        if (character.Humanoid.Health <= 0) return [];
        if (seat is null)
        {
            var root = character.Root;
            if (root.Velocity[1] > .01f) return [];
            seat = GameProjectStore.Flatten(project.Workspace.Children)
                .Where(node => (node.Type is "Seat" or "VehicleSeat") && node.CanCollide && !node.VehicleSeatDisabled && (string.IsNullOrEmpty(node.Occupant) || node.Occupant == character.Humanoid.Id))
                .FirstOrDefault(node => CollisionGeometry.SweepFraction(root, node, 1, -.08f) < 1
                    && root.Position[1] >= node.Position[1]);
            if (seat is null) return [];
            seat.Occupant = character.Humanoid.Id;
            root.Anchored = true;
            character.IsSeated = true;
        }

        character.Root.Position[0] = seat.Position[0];
        character.Root.Position[1] = SeatCenterTop(seat) + 1.25f;
        character.Root.Position[2] = seat.Position[2];
        character.Root.Velocity = [0, 0, 0];
        seat.Occupant = character.Humanoid.Id;
        seat.VehicleSeatThrottleFloat = seat.Type == "VehicleSeat" ? vehicleThrottle : 0;
        seat.VehicleSeatSteerFloat = seat.Type == "VehicleSeat" ? vehicleSteer : 0;
        character.SetFacingYaw(seat.Rotation[1]);
        return [seat, character.Root];
    }

    private static float SeatCenterTop(SceneNode node) =>
        CollisionGeometry.TopYAt(node, node.Position[0], node.Position[2]) ?? CollisionGeometry.TopY(node);
}
