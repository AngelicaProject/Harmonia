using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace Harmonia.UI;

// ImGui releases a text field on Enter. Keep the game's copy of that key
// suppressed through release, including when Enter closes the palette.
internal sealed class DictionaryKeyboard(IKeyState keys, Configuration configuration,
    Func<int, bool>? physicalKeyDown = null, Func<bool>? gameFocused = null, Action? saveShortcut = null, Func<bool>? gameTextInputActive = null)
{
    private bool capture;
    private bool captureEscape;
    private bool escapeHeld;
    private readonly Func<int, bool> physicalKeyDown = physicalKeyDown ?? IsPhysicalKeyDown;
    private readonly Func<bool> gameFocused = gameFocused ?? IsGameFocused;
    private int pendingToggle;
    private int consumedKey;
    private volatile bool recording;
    private bool waitingForRelease;
    private volatile bool blockEnter;
    private volatile bool blockEscape;
    private volatile bool blockNavigation;
    private volatile bool textInputActive;

    public bool IsRecording => recording;
    public bool ShortcutOwnsEscape => recording || consumedKey == (int)VirtualKey.ESCAPE;

    public string ShortcutLabel
    {
        get
        {
            var modifiers = configuration.DictionaryShortcutModifiers;
            return ((modifiers & 1) != 0 ? "Ctrl+" : string.Empty) +
                ((modifiers & 2) != 0 ? "Shift+" : string.Empty) +
                ((modifiers & 4) != 0 ? "Alt+" : string.Empty) +
                ((modifiers & 8) != 0 ? "Win+" : string.Empty) + KeyLabel(configuration.DictionaryShortcutKey);
        }
    }

    private static string KeyLabel(string key) => key switch
    {
        "RETURN" => "Enter", "SPACE" => "Space", "ESCAPE" => "Esc", "TAB" => "Tab",
        "BACK" => "Backspace", "DELETE" => "Delete", "INSERT" => "Insert",
        "PRIOR" => "Page Up", "NEXT" => "Page Down", "HOME" => "Home", "END" => "End",
        "UP" => "↑", "DOWN" => "↓", "LEFT" => "←", "RIGHT" => "→",
        "OEM_1" => ";", "OEM_PLUS" => "=", "OEM_COMMA" => ",", "OEM_MINUS" => "-",
        "OEM_PERIOD" => ".", "OEM_2" => "/", "OEM_3" => "`", "OEM_4" => "[",
        "OEM_5" => "\\", "OEM_6" => "]", "OEM_7" => "'",
        _ => key,
    };

    public void StartRecording()
    {
        waitingForRelease = true;
        recording = true;
        Interlocked.Exchange(ref pendingToggle, 0);
    }

    public void CancelRecording() => recording = false;

    private int ReadModifiers() =>
        (physicalKeyDown((int)VirtualKey.CONTROL) ? 1 : 0) |
        (physicalKeyDown((int)VirtualKey.SHIFT) ? 2 : 0) |
        (physicalKeyDown((int)VirtualKey.MENU) ? 4 : 0) |
        (physicalKeyDown((int)VirtualKey.LWIN) || physicalKeyDown((int)VirtualKey.RWIN) ? 8 : 0);

    private static bool IsMainKey(int key) => key is >= 8 and <= 254 &&
        key is not (16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165 or 229 or 231);

    private void SuppressKey(int key)
    {
        if (keys.IsVirtualKeyValid(key))
            keys[key] = false;
    }

    private void RecordShortcut()
    {
        var modifiers = ReadModifiers();
        var pressed = 0;
        for (var key = 8; key <= 254; key++)
        {
            if (!IsMainKey(key) || !physicalKeyDown(key))
                continue;
            SuppressKey(key);
            pressed = pressed == 0 ? key : pressed;
        }
        if (waitingForRelease)
        {
            if (pressed == 0 && modifiers == 0)
                waitingForRelease = false;
            return;
        }
        if (pressed == 0)
            return;
        recording = false;
        consumedKey = pressed;
        if (pressed == (int)VirtualKey.ESCAPE && modifiers == 0)
            return;
        configuration.DictionaryShortcutKey = ((VirtualKey)pressed).ToString();
        configuration.DictionaryShortcutModifiers = modifiers;
        saveShortcut?.Invoke();
    }

    public bool GameHasFocus => gameFocused();

    // Framework.Update runs before the game's original tick. Read physical
    // keys here: ImGui need not receive letters while no text field is active.
    public void PollShortcut(bool available)
    {
        var focused = gameFocused();
        if (consumedKey != 0)
        {
            if (focused && physicalKeyDown(consumedKey))
            {
                SuppressKey(consumedKey);
                return;
            }
            consumedKey = 0;
        }

        if (recording)
        {
            if (focused)
                RecordShortcut();
            return;
        }
        if (!available || !focused || !configuration.DictionaryShortcutEnabled ||
            !Enum.TryParse<VirtualKey>(configuration.DictionaryShortcutKey, out var mainKey) || !IsMainKey((int)mainKey))
            return;
        if (ReadModifiers() != configuration.DictionaryShortcutModifiers || !physicalKeyDown((int)mainKey))
            return;

        if (configuration.DictionaryShortcutModifiers == 0 &&
            ((int)mainKey is < 112 or > 135) && (textInputActive || (gameTextInputActive?.Invoke() ?? false)))
            return;
        consumedKey = (int)mainKey;
        SuppressKey(consumedKey);
        Interlocked.Increment(ref pendingToggle);
    }

    public bool BeginFrame()
    {
        capture = false;
        captureEscape = false;
        return (Interlocked.Exchange(ref pendingToggle, 0) & 1) != 0;
    }

    private static bool IsPhysicalKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsGameFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public bool PaletteEscapePressed(bool eligible)
    {
        var focused = gameFocused();
        var down = focused && physicalKeyDown((int)VirtualKey.ESCAPE);
        var pressed = down && !escapeHeld;
        escapeHeld = down;
        captureEscape = eligible && focused;
        return captureEscape && pressed;
    }

    public void Capture()
    {
        capture = true;
        ImGui.GetIO().WantTextInput = true;
        ImGui.GetIO().WantCaptureKeyboard = true;
    }

    public void EndFrame()
    {
        textInputActive = ImGui.GetIO().WantTextInput;
        blockEnter = capture || (blockEnter && (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter)));
        blockEscape = capture || captureEscape || (blockEscape && physicalKeyDown((int)VirtualKey.ESCAPE));
        blockNavigation = capture;
    }

    public void SuppressGameKeys()
    {
        if (blockEnter)
            keys[VirtualKey.RETURN] = false;
        if (blockEscape)
            keys[VirtualKey.ESCAPE] = false;
        if (blockNavigation)
        {
            if (!physicalKeyDown((int)VirtualKey.SHIFT))
                keys[VirtualKey.TAB] = false;
            keys[VirtualKey.UP] = false;
            keys[VirtualKey.DOWN] = false;
            if (keys[VirtualKey.CONTROL])
            {
                keys[VirtualKey.N] = false;
                keys[VirtualKey.P] = false;
            }
        }
    }
}
