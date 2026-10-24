using System.Text.Json.Serialization;

namespace BullerStudio.Models;

public sealed class GameProject
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled Game";
    public ProjectSettings Settings { get; set; } = new();
    public SceneNode Workspace { get; set; } = new() { Id = "workspace", Type = "Workspace", Name = "Workspace", Children = [new SceneNode { Id = "workspace-terrain", Type = "Terrain", Name = "Terrain" }] };
    public SceneNode Players { get; set; } = new() { Id = "players", Type = "Players", Name = "Players" };
    public SceneNode Teams { get; set; } = new() { Id = "teams", Type = "Teams", Name = "Teams" };
    public SceneNode Lighting { get; set; } = new() { Id = "lighting", Type = "Lighting", Name = "Lighting", Ambient = "#808080", ClockTime = 14, Children = [new SceneNode { Id = "sky-main", Type = "Sky", Name = "Sky" }] };
    public SceneNode SoundService { get; set; } = new() { Id = "sound-service", Type = "SoundService", Name = "SoundService" };
    public SceneNode ReplicatedStorage { get; set; } = new() { Id = "replicated-storage", Type = "ReplicatedStorage", Name = "ReplicatedStorage" };
    public SceneNode ServerScriptService { get; set; } = new() { Id = "server-script-service", Type = "ServerScriptService", Name = "ServerScriptService" };
    public SceneNode ServerStorage { get; set; } = new() { Id = "server-storage", Type = "ServerStorage", Name = "ServerStorage" };
    public SceneNode StarterPack { get; set; } = new() { Id = "starter-pack", Type = "StarterPack", Name = "StarterPack" };
    public SceneNode StarterPlayer { get; set; } = new() { Id = "starter-player", Type = "StarterPlayer", Name = "StarterPlayer", Children = [new SceneNode { Type = "Folder", Name = "StarterPlayerScripts" }, new SceneNode { Type = "Folder", Name = "StarterCharacterScripts" }] };
    public SceneNode Gui { get; set; } = new() { Id = "gui", Type = "StarterGui", Name = "StarterGui", Children = [new SceneNode { Id = "screen-gui-main", Type = "ScreenGui", Name = "ScreenGui" }] };
    public List<ProjectAsset> Assets { get; set; } = [];
    public List<GameScript> Scripts { get; set; } = [];
    [JsonIgnore] public string ProjectPath { get; set; } = string.Empty;
    [JsonIgnore] public bool BuildIncluded { get; set; } = true;
}

public sealed class ProjectSettings
{
    public string Background { get; set; } = "#101522";
    public bool Shadows { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public float MoveSnapIncrement { get; set; } = 1;
    public float RotateSnapIncrement { get; set; } = 15;
}

public sealed class SceneNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Folder";
    public string Name { get; set; } = "Folder";
    public bool Archivable { get; set; } = true;
    public float[] Position { get; set; } = [0, 0, 0];
    public bool HasModelPivot { get; set; }
    public string PrimaryPart { get; set; } = string.Empty;
    public float[] PositionScale { get; set; } = [0, 0];
    public float[] AnchorPoint { get; set; } = [0, 0];
    public float[] PaddingLeft { get; set; } = [0, 0];
    public float[] PaddingTop { get; set; } = [0, 0];
    public float[] PaddingRight { get; set; } = [0, 0];
    public float[] PaddingBottom { get; set; } = [0, 0];
    public float Scale { get; set; } = 1;
    public float[] Rotation { get; set; } = [0, 0, 0];
    public string ToolTip { get; set; } = string.Empty;
    public bool RequiresHandle { get; set; } = true;
    public bool CanBeDropped { get; set; } = true;
    public bool ManualActivationOnly { get; set; }
    public float[] GripPosition { get; set; } = [.95f, .15f, -.35f];
    public float[] GripRotation { get; set; } = [0, 0, 0];
    public bool VehicleSeatDisabled { get; set; }
    public bool VehicleSeatHeadsUpDisplay { get; set; } = true;
    public float VehicleSeatMaxSpeed { get; set; } = 25;
    public float VehicleSeatTorque { get; set; } = 10;
    public float VehicleSeatTurnSpeed { get; set; } = 1;
    [JsonIgnore] public float VehicleSeatThrottleFloat { get; set; }
    [JsonIgnore] public float VehicleSeatSteerFloat { get; set; }
    [JsonIgnore] public int VehicleSeatAreHingesDetected { get; set; }
    public float[] Size { get; set; } = [2, 2, 2];
    public float[] SizeScale { get; set; } = [0, 0];
    public int ZIndex { get; set; } = 1;
    public int DisplayOrder { get; set; }
    public int LayoutOrder { get; set; }
    public string Color { get; set; } = "#4E9DFF";
    public string FillColor { get; set; } = "#00BFFF";
    public string OutlineColor { get; set; } = "#FFFFFF";
    public string StartColor { get; set; } = "#FFFFFF";
    public string EndColor { get; set; } = "#808080";
    public float FillTransparency { get; set; } = .5f;
    public float OutlineTransparency { get; set; }
    public string Adornee { get; set; } = string.Empty;
    public float MaxDistance { get; set; }
    public bool AlwaysOnTop { get; set; }
    public float PixelsPerStud { get; set; } = 50;
    public string SurfaceColor3 { get; set; } = "#FFFFFF";
    public float SurfaceTransparency { get; set; } = 1;
    public float LineThickness { get; set; } = .05f;
    public string Shape { get; set; } = "Block";
    public string Face { get; set; } = "Front";
    public float StudsPerTileU { get; set; } = 2;
    public float StudsPerTileV { get; set; } = 2;
    public float OffsetStudsU { get; set; }
    public float OffsetStudsV { get; set; }
    public string Material { get; set; } = "Plastic";
    public float Transparency { get; set; }
    public float BackgroundTransparency { get; set; }
    public float TextTransparency { get; set; }
    public float ImageTransparency { get; set; }
    public bool Enabled { get; set; } = true;
    public float Brightness { get; set; } = 1;
    public string Ambient { get; set; } = "#808080";
    public string AtmosphereDecay { get; set; } = "#607080";
    public float Density { get; set; } = .3f;
    public float Haze { get; set; }
    public float Glare { get; set; }
    public float Offset { get; set; }
    public float ClockTime { get; set; } = 14;
    public float Range { get; set; } = 16;
    public string SecondaryColor { get; set; } = "#FFE69A";
    public float EffectSize { get; set; } = 1;
    public float Heat { get; set; } = 9;
    public float RiseVelocity { get; set; } = 1;
    public float Opacity { get; set; } = .5f;
    public float TimeScale { get; set; } = 1;
    public float Rate { get; set; } = 5;
    public List<ParticleNumberKeypoint> SizeSequence { get; set; } = [];
    public List<ParticleColorKeypoint> ColorSequence { get; set; } = [];
    public List<ParticleNumberKeypoint> TransparencySequence { get; set; } = [];
    public float[] SpeedRange { get; set; } = [5, 5];
    public float[] LifetimeRange { get; set; } = [5, 5];
    public float[] SpreadAngle { get; set; } = [0, 0];
    public string EmissionDirection { get; set; } = "Top";
    [JsonIgnore] public int PendingParticleEmits { get; set; }
    [JsonIgnore] public int ParticleClearVersion { get; set; }
    public float BlastRadius { get; set; } = 10;
    public float BlastPressure { get; set; } = 500000;
    public float DestroyJointRadiusPercent { get; set; } = 1;
    [JsonIgnore] public float ExplosionAge { get; set; } = -1;
    public float Angle { get; set; } = 45;
    public string ActionText { get; set; } = "Interact";
    public string ObjectText { get; set; } = string.Empty;
    public float HoldDuration { get; set; }
    public float MaxActivationDistance { get; set; } = 10;
    public float Volume { get; set; } = .5f;
    public float PlaybackSpeed { get; set; } = 1;
    public float SoundServiceVolume { get; set; } = 1;
    public float SoundGroupVolume { get; set; } = 1;
    public string SoundGroupId { get; set; } = string.Empty;
    public float PitchShiftOctave { get; set; } = 1.25f;
    public bool Looped { get; set; }
    public bool Playing { get; set; }
    public float RollOffMinDistance { get; set; } = 10;
    public float RollOffMaxDistance { get; set; } = 100;
    public string Part0 { get; set; } = string.Empty;
    public string Part1 { get; set; } = string.Empty;
    public string Attachment0 { get; set; } = string.Empty;
    public string Attachment1 { get; set; } = string.Empty;
    public float[] Force { get; set; } = [0, 0, 0];
    public float[] BodyThrustForce { get; set; } = [0, 1, 0];
    public float[] BodyThrustLocation { get; set; } = [0, 0, 0];
    public float[] BodyVelocityTarget { get; set; } = [0, 0, 0];
    public float[] BodyVelocityMaxForce { get; set; } = [0, 0, 0];
    public float BodyVelocityP { get; set; } = 1250;
    public float[] BodyPositionTarget { get; set; } = [0, 0, 0];
    public float[] BodyPositionMaxForce { get; set; } = [0, 0, 0];
    public float BodyPositionP { get; set; } = 10000;
    public float BodyPositionD { get; set; } = 1000;
    public float[] BodyGyroTargetRotation { get; set; } = [0, 0, 0];
    public float[] BodyGyroMaxTorque { get; set; } = [0, 0, 0];
    public float BodyGyroP { get; set; } = 3000;
    public float BodyGyroD { get; set; } = 500;
    public float[] BodyAngularVelocityTarget { get; set; } = [0, 0, 0];
    public float[] BodyAngularVelocityMaxTorque { get; set; } = [0, 0, 0];
    public float BodyAngularVelocityP { get; set; } = 1250;
    public string RocketPropulsionTarget { get; set; } = string.Empty;
    public float[] RocketPropulsionTargetOffset { get; set; } = [0, 0, 0];
    public float RocketPropulsionTargetRadius { get; set; } = 4;
    public float RocketPropulsionCartoonFactor { get; set; } = .7f;
    public float RocketPropulsionMaxSpeed { get; set; } = 30;
    public float RocketPropulsionMaxThrust { get; set; } = 4000;
    public float[] RocketPropulsionMaxTorque { get; set; } = [400000, 400000, 0];
    public float RocketPropulsionThrustD { get; set; } = .001f;
    public float RocketPropulsionThrustP { get; set; } = 5;
    public float RocketPropulsionTurnD { get; set; } = 500;
    public float RocketPropulsionTurnP { get; set; } = 3000;
    [JsonIgnore] public bool RocketPropulsionActive { get; set; }
    [JsonIgnore] public bool RocketPropulsionReachedTarget { get; set; }
    public string RelativeTo { get; set; } = "World";
    public bool ApplyAtCenterOfMass { get; set; }
    public float LineForceMagnitude { get; set; } = 1000;
    public float LineForceMaxForce { get; set; } = 1000000;
    public bool LineForceInverseSquareLaw { get; set; }
    public bool LineForceReactionForceEnabled { get; set; }
    public float[] VectorVelocity { get; set; } = [0, 0, 0];
    public float[] LineDirection { get; set; } = [1, 0, 0];
    public float LineVelocity { get; set; }
    public float[] PlaneVelocity { get; set; } = [0, 0];
    public float[] PrimaryTangentAxis { get; set; } = [1, 0, 0];
    public float[] SecondaryTangentAxis { get; set; } = [0, 0, 1];
    public string VelocityConstraintMode { get; set; } = "Vector";
    public bool ForceLimitsEnabled { get; set; }
    public string ForceLimitMode { get; set; } = "Magnitude";
    public bool LinearVelocityReactionForceEnabled { get; set; } = true;
    public float[] MaxAxesForce { get; set; } = [1000, 1000, 1000];
    public float[] MaxPlanarAxesForce { get; set; } = [1000, 1000];
    public float MaxForce { get; set; } = 1000;
    public string AlignmentMode { get; set; } = "TwoAttachment";
    public float Responsiveness { get; set; } = 10;
    public float MaxVelocity { get; set; } = 100;
    public bool RigidityEnabled { get; set; }
    public bool ReactionForceEnabled { get; set; }
    public float[] AlignOrientationRotation { get; set; } = [0, 0, 0];
    public float[] AngularVelocityTarget { get; set; } = [0, 0, 0];
    public float[] TorqueTarget { get; set; } = [0, 0, 0];
    public float SpringDamping { get; set; } = 10;
    public float SpringFreeLength { get; set; } = 4;
    public bool SpringLimitsEnabled { get; set; }
    public float SpringMaxForce { get; set; } = 5000;
    public float SpringMaxLength { get; set; } = 10;
    public float SpringMinLength { get; set; }
    public float SpringStiffness { get; set; } = 100;
    [JsonIgnore] public float SpringCurrentLength { get; set; }
    public float TorsionSpringDamping { get; set; } = 10;
    public bool TorsionSpringLimitsEnabled { get; set; }
    public float TorsionSpringMaxAngle { get; set; } = 45;
    public float TorsionSpringMaxTorque { get; set; } = 10000;
    public float TorsionSpringRadius { get; set; } = .15f;
    public float TorsionSpringRestitution { get; set; }
    public float TorsionSpringStiffness { get; set; } = 100;
    public float TorsionSpringCoils { get; set; } = 3;
    [JsonIgnore] public float TorsionSpringCurrentAngle { get; set; }
    public bool BallSocketLimitsEnabled { get; set; }
    public float BallSocketUpperAngle { get; set; } = 45;
    public bool BallSocketTwistLimitsEnabled { get; set; }
    public float BallSocketTwistLowerAngle { get; set; } = -45;
    public float BallSocketTwistUpperAngle { get; set; } = 45;
    public float BallSocketMaxFrictionTorque { get; set; }
    public float BallSocketRadius { get; set; } = .15f;
    public float BallSocketRestitution { get; set; } = .2f;
    public bool UniversalLimitsEnabled { get; set; }
    public float UniversalMaxAngle { get; set; } = 45;
    public float UniversalRadius { get; set; } = .2f;
    public float UniversalRestitution { get; set; }
    public string HingeActuatorType { get; set; } = "None";
    public float HingeAngularResponsiveness { get; set; } = 45;
    public float HingeAngularSpeed { get; set; } = 10;
    public float HingeAngularVelocity { get; set; }
    [JsonIgnore] public float HingeCurrentAngle { get; set; }
    public bool HingeLimitsEnabled { get; set; }
    public float HingeLowerAngle { get; set; } = -45;
    public float HingeUpperAngle { get; set; } = 45;
    public float HingeMotorMaxAcceleration { get; set; } = 1000;
    public float HingeMotorMaxTorque { get; set; } = 10000;
    public float HingeRadius { get; set; } = .15f;
    public float HingeRestitution { get; set; }
    public float HingeServoMaxTorque { get; set; } = 10000;
    public float HingeTargetAngle { get; set; }
    public string PrismaticActuatorType { get; set; } = "None";
    [JsonIgnore] public float PrismaticCurrentPosition { get; set; }
    public bool PrismaticLimitsEnabled { get; set; }
    public float PrismaticLinearResponsiveness { get; set; } = 45;
    public float PrismaticLowerLimit { get; set; }
    public float PrismaticMotorMaxAcceleration { get; set; } = 1000;
    public float PrismaticMotorMaxForce { get; set; } = 10000;
    public float PrismaticRestitution { get; set; }
    public float PrismaticServoMaxForce { get; set; } = 10000;
    public float PrismaticSize { get; set; } = .15f;
    public float PrismaticSpeed { get; set; } = 10;
    public float PrismaticTargetPosition { get; set; }
    public float PrismaticUpperLimit { get; set; }
    public float PrismaticVelocity { get; set; }
    public float RopeLength { get; set; } = 10;
    public float RopeRestitution { get; set; }
    public bool RopeWinchEnabled { get; set; }
    public float RopeWinchForce { get; set; } = 10000;
    public float RopeWinchResponsiveness { get; set; } = 10;
    public float RopeWinchSpeed { get; set; } = 10;
    public float RopeWinchTarget { get; set; } = 5;
    [JsonIgnore] public float RopeCurrentLength { get; set; }
    public float RodLength { get; set; } = 10;
    public float RodLimitAngle0 { get; set; } = 45;
    public float RodLimitAngle1 { get; set; } = 45;
    public bool RodLimitsEnabled { get; set; }
    public float RodThickness { get; set; } = .1f;
    [JsonIgnore] public float RodCurrentDistance { get; set; }
    public float CylindricalInclinationAngle { get; set; }
    public bool CylindricalRotationAxisVisible { get; set; } = true;
    public float MaxTorque { get; set; } = 1000;
    public float MaxAngularVelocity { get; set; } = 100;
    public bool ReactionTorqueEnabled { get; set; }
    public float Width0 { get; set; } = .2f;
    public float Width1 { get; set; } = .2f;
    public float TrailLifetime { get; set; } = 2;
    public float TrailMinLength { get; set; } = .1f;
    public float TrailMaxLength { get; set; }
    public float TrailWidth { get; set; } = 1;
    public List<ParticleNumberKeypoint> TrailWidthSequence { get; set; } = [];
    public bool FaceCamera { get; set; }
    public string TextureMode { get; set; } = "Stretch";
    public float TextureLength { get; set; } = 1;
    [JsonIgnore] public int TrailClearVersion { get; set; }
    public string Text { get; set; } = "Text";
    public string PlaceholderText { get; set; } = string.Empty;
    public string TextColor { get; set; } = "#FFFFFF";
    public bool MultiLine { get; set; }
    public bool TextEditable { get; set; } = true;
    public bool ClearTextOnFocus { get; set; } = true;
    public string StrokeColor { get; set; } = "#FFFFFF";
    public float TextSize { get; set; } = 18;
    public bool TextScaled { get; set; }
    public float MinTextSize { get; set; } = 1;
    public float MaxTextSize { get; set; } = 1000;
    public float CornerRadius { get; set; }
    public float CornerRadiusScale { get; set; }
    public float[]? TopLeftRadius { get; set; }
    public float[]? TopRightRadius { get; set; }
    public float[]? BottomRightRadius { get; set; }
    public float[]? BottomLeftRadius { get; set; }
    public float[] ShadowBlurRadius { get; set; } = [0, 12];
    public float[] ShadowOffset { get; set; } = [0, 4];
    public float[] ShadowOffsetScale { get; set; } = [0, 0];
    public float[] ShadowSpread { get; set; } = [0, 0];
    public float[] ShadowSpreadScale { get; set; } = [0, 0];
    public bool ShadowInset { get; set; }
    public bool ShadowShowBehindParent { get; set; } = true;
    public string ShadowMode { get; set; } = "Shape";
    public float StrokeThickness { get; set; } = 1;
    public float LayoutSpacing { get; set; }
    public float ListPaddingScale { get; set; }
    public float[] CellSize { get; set; } = [100, 100];
    public float[] CellSizeScale { get; set; } = [0, 0];
    public float[] CellPadding { get; set; } = [0, 0];
    public float[] CellPaddingScale { get; set; } = [0, 0];
    public string FillDirection { get; set; } = "Horizontal";
    public int FillDirectionMaxCells { get; set; }
    public int TableColumns { get; set; } = 2;
    public float AspectRatio { get; set; } = 1;
    public string AspectType { get; set; } = "FitWithinMaxSize";
    public string DominantAxis { get; set; } = "Width";
    public float[] MinSize { get; set; } = [0, 0];
    public float[] MaxSize { get; set; } = [1000000, 1000000];
    public string LayoutDirection { get; set; } = "Vertical";
    public string HorizontalAlignment { get; set; } = "Left";
    public string VerticalAlignment { get; set; } = "Top";
    public string SortOrder { get; set; } = "LayoutOrder";
    public string FlexMode { get; set; } = "None";
    public float GrowRatio { get; set; }
    public float ShrinkRatio { get; set; }
    public string ItemLineAlignment { get; set; } = "Automatic";
    public bool Visible { get; set; } = true;
    public string SkyboxFront { get; set; } = string.Empty;
    public string SkyboxBack { get; set; } = string.Empty;
    public string SkyboxLeft { get; set; } = string.Empty;
    public string SkyboxRight { get; set; } = string.Empty;
    public string SkyboxTop { get; set; } = string.Empty;
    public string SkyboxBottom { get; set; } = string.Empty;
    public bool Anchored { get; set; } = true;
    public bool CanCollide { get; set; } = true;
    public bool CanTouch { get; set; } = true;
    public bool CanQuery { get; set; } = true;
    public float[] Velocity { get; set; } = [0, 0, 0];
    [JsonIgnore] public float[] AssemblyAngularVelocity { get; set; } = [0, 0, 0];
    public bool CastShadow { get; set; } = true;
    public float Gravity { get; set; } = 24;
    public bool AutoManageCharacter { get; set; } = true;
    public string Character { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public string TeamColor { get; set; } = "#E53935";
    public bool AutoAssignable { get; set; } = true;
    public bool Neutral { get; set; } = true;
    public bool AllowTeamChangeOnTouch { get; set; } = true;
    public float Duration { get; set; } = 10;
    public string Occupant { get; set; } = string.Empty;
    public float Health { get; set; } = 100;
    public float MaxHealth { get; set; } = 100;
    public float WalkSpeed { get; set; } = 7.5f;
    public float JumpPower { get; set; } = 10;
    public bool BooleanValue { get; set; }
    public long IntegerValue { get; set; }
    public double NumberValue { get; set; }
    public string StringValueText { get; set; } = string.Empty;
    public float[] Vector2Value { get; set; } = [0, 0];
    public float[] Vector3Value { get; set; } = [0, 0, 0];
    public string Color3Value { get; set; } = "#000000";
    public string ObjectValueId { get; set; } = string.Empty;
    public Dictionary<string, SceneAttributeValue> Attributes { get; set; } = new(StringComparer.Ordinal);
    public List<string> Tags { get; set; } = [];
    public string Asset { get; set; } = string.Empty;
    public string Texture { get; set; } = string.Empty;
    public string CameraMode { get; set; } = "Custom";
    public string Subject { get; set; } = string.Empty;
    public float FieldOfView { get; set; } = 55;
    public List<SceneNode> Children { get; set; } = [];
    [JsonIgnore] public bool IsExplorerVisible { get; set; } = true;
    [JsonIgnore] public bool IsExplorerSelected { get; set; }
}

public sealed record ParticleNumberKeypoint(float Time, float Value, float Envelope = 0);
public sealed record ParticleColorKeypoint(float Time, string Value);

public sealed class SceneAttributeValue
{
    public string Type { get; set; } = "String";
    public string StringValue { get; set; } = string.Empty;
    public bool BooleanValue { get; set; }
    public double NumberValue { get; set; }
    public float[] Vector3Value { get; set; } = [0, 0, 0];
    public string Color3Value { get; set; } = "#000000";
}

public sealed class ProjectAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class GameScript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Script";
    public string Source { get; set; } = "print('Hello from Lua X')";
}
