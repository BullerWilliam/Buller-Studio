using System.Runtime.InteropServices;
using System.Numerics;

namespace BullerStudio.Services;

public sealed class GamepadInputService
{
    private const ushort DPadUp = 0x0001;
    private const ushort DPadDown = 0x0002;
    private const ushort DPadLeft = 0x0004;
    private const ushort DPadRight = 0x0008;
    private const ushort Start = 0x0010;
    private const ushort Back = 0x0020;
    private const ushort LeftThumb = 0x0040;
    private const ushort RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort A = 0x1000;
    private const ushort B = 0x2000;
    private const ushort X = 0x4000;
    private const ushort Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out State state);

    private static readonly (ushort Mask, string Name)[] ButtonMap =
    [
        (DPadUp, "DPadUp"), (DPadDown, "DPadDown"), (DPadLeft, "DPadLeft"), (DPadRight, "DPadRight"),
        (Start, "ButtonStart"), (Back, "ButtonSelect"), (LeftThumb, "ButtonL3"), (RightThumb, "ButtonR3"),
        (LeftShoulder, "ButtonL1"), (RightShoulder, "ButtonR1"), (A, "ButtonA"), (B, "ButtonB"),
        (X, "ButtonX"), (Y, "ButtonY")
    ];

    private readonly HashSet<string> pressed = new(StringComparer.Ordinal);
    private bool isConnected;
    private Vector2 leftStick;
    private Vector2 rightStick;

    public bool IsConnected => isConnected;
    public Vector2 LeftStick => leftStick;
    public Vector2 RightStick => rightStick;

    public void Reset()
    {
        pressed.Clear();
        leftStick = rightStick = Vector2.Zero;
    }

    public IReadOnlyList<(string KeyCode, bool IsDown)> Poll()
    {
        State state = default;
        isConnected = false;
        for (uint index = 0; index < 4; index++)
        {
            try
            {
                if (XInputGetState(index, out state) == 0)
                {
                    isConnected = true;
                    break;
                }
            }
            catch (DllNotFoundException) { break; }
            catch (EntryPointNotFoundException) { break; }
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        if (isConnected)
        {
            foreach (var (mask, name) in ButtonMap)
                if ((state.Gamepad.Buttons & mask) != 0) current.Add(name);
            if (state.Gamepad.LeftTrigger >= 30) current.Add("ButtonL2");
            if (state.Gamepad.RightTrigger >= 30) current.Add("ButtonR2");
            leftStick = NormalizeStick(state.Gamepad.ThumbLX, state.Gamepad.ThumbLY, 7849);
            rightStick = NormalizeStick(state.Gamepad.ThumbRX, state.Gamepad.ThumbRY, 8689);
        }
        else leftStick = rightStick = Vector2.Zero;

        if (leftStick.LengthSquared() > .01f) current.Add("Thumbstick1");
        if (rightStick.LengthSquared() > .01f) current.Add("Thumbstick2");
        var changes = new List<(string KeyCode, bool IsDown)>();
        foreach (var key in current.Except(pressed, StringComparer.Ordinal)) changes.Add((key, true));
        foreach (var key in pressed.Except(current, StringComparer.Ordinal)) changes.Add((key, false));
        pressed.Clear();
        pressed.UnionWith(current);
        return changes;
    }

    private static Vector2 NormalizeStick(short x, short y, int deadzone)
    {
        var rawX = x / 32768f;
        var rawY = y / 32768f;
        var magnitude = Math.Min(1f, MathF.Sqrt(rawX * rawX + rawY * rawY));
        var deadzoneNormalized = deadzone / 32768f;
        if (magnitude <= deadzoneNormalized) return Vector2.Zero;
        var scaledMagnitude = (magnitude - deadzoneNormalized) / (1 - deadzoneNormalized);
        return new Vector2(rawX, rawY) / magnitude * scaledMagnitude;
    }
}
