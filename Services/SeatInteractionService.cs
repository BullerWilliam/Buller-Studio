using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class SeatInteractionService
{
    private readonly GameProject project;
    private readonly PlayerCharacterService character;
    private SceneNode? seat;

    public SeatInteractionService(GameProject project, PlayerCharacterService character)
    {
        this.project = project;
        this.character = character;
    }

    public bool IsSeated => seat is not null;

    public IReadOnlyList<SceneNode> BeforePhysics(bool jump)
    {
        if (seat is null) return [];
        var stillPresent = GameProjectStore.Flatten(project.Workspace.Children).Any(node => ReferenceEquals(node, seat));
        if (stillPresent && seat.CanCollide && character.Humanoid.Health > 0 && !jump) return [];

        var oldSeat = seat;
        seat = null;
        oldSeat.Occupant = string.Empty;
        character.IsSeated = false;
        character.Root.Anchored = false;
        character.Root.Position[1] = oldSeat.Position[1] + Math.Abs(oldSeat.Size[1]) * .5f + Math.Abs(character.Root.Size[1]) * .5f + .001f;
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
            var bottom = root.Position[1] - Math.Abs(root.Size[1]) * .5f;
            seat = GameProjectStore.Flatten(project.Workspace.Children)
                .Where(node => node.Type == "Seat" && node.CanCollide && (string.IsNullOrEmpty(node.Occupant) || node.Occupant == character.Humanoid.Id))
                .FirstOrDefault(node =>
                    Math.Abs(bottom - (node.Position[1] + Math.Abs(node.Size[1]) * .5f)) <= .08f
                    && Math.Abs(root.Position[0] - node.Position[0]) < (Math.Abs(root.Size[0]) + Math.Abs(node.Size[0])) * .5f
                    && Math.Abs(root.Position[2] - node.Position[2]) < (Math.Abs(root.Size[2]) + Math.Abs(node.Size[2])) * .5f);
            if (seat is null) return [];
            seat.Occupant = character.Humanoid.Id;
            root.Anchored = true;
            character.IsSeated = true;
        }

        character.Root.Position[0] = seat.Position[0];
        character.Root.Position[1] = seat.Position[1] + Math.Abs(seat.Size[1]) * .5f + 1.25f;
        character.Root.Position[2] = seat.Position[2];
        character.Root.Velocity = [0, 0, 0];
        seat.Occupant = character.Humanoid.Id;
        character.SetFacingYaw(seat.Rotation[1]);
        return [seat, character.Root];
    }
}
