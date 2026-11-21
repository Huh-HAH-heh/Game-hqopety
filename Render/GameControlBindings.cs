using System;
using System.IO;
using System.Text.Json;
using SFML.Window;

namespace RimClone.Render;

public enum GameControlAction
{
    RaiseTerrainSlice,
    LowerTerrainSlice,
    ToggleCameraProjection,
    RotateCameraCounterClockwise,
    RotateCameraClockwise,
    ToggleBlockTerrainPreview,
    ToggleInformationLayers,
    ToggleAutonomousMovement
}

public sealed class GameControlBindings
{
    private sealed class StoredBindings
    {
        public Keyboard.Key RaiseTerrainSlice { get; set; } = Keyboard.Key.Add;
        public Keyboard.Key LowerTerrainSlice { get; set; } = Keyboard.Key.Subtract;
        public Keyboard.Key ToggleCameraProjection { get; set; } = Keyboard.Key.Tab;
        public Keyboard.Key RotateCameraCounterClockwise { get; set; } = Keyboard.Key.Q;
        public Keyboard.Key RotateCameraClockwise { get; set; } = Keyboard.Key.E;
        public Keyboard.Key ToggleBlockTerrainPreview { get; set; } = Keyboard.Key.B;
        public Keyboard.Key ToggleInformationLayers { get; set; } = Keyboard.Key.V;
        public Keyboard.Key ToggleAutonomousMovement { get; set; } = Keyboard.Key.F;
    }

    private static readonly GameControlAction[] ActionList =
    {
        GameControlAction.RaiseTerrainSlice,
        GameControlAction.LowerTerrainSlice,
        GameControlAction.ToggleCameraProjection,
        GameControlAction.RotateCameraCounterClockwise,
        GameControlAction.RotateCameraClockwise,
        GameControlAction.ToggleBlockTerrainPreview,
        GameControlAction.ToggleInformationLayers,
        GameControlAction.ToggleAutonomousMovement
    };

    private readonly Keyboard.Key[] _keys =
    {
        Keyboard.Key.Add, Keyboard.Key.Subtract, Keyboard.Key.Tab,
        Keyboard.Key.Q, Keyboard.Key.E, Keyboard.Key.B,
        Keyboard.Key.V, Keyboard.Key.F
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RimClone",
        "controls.json");

    public static int ActionCount => ActionList.Length;
    public static GameControlAction GetActionAt(int index) => ActionList[index];

    public static string GetActionName(GameControlAction action) => action switch
    {
        GameControlAction.RaiseTerrainSlice => "Поднять срез террейна",
        GameControlAction.LowerTerrainSlice => "Опустить срез террейна",
        GameControlAction.ToggleCameraProjection => "Переключить проекцию камеры",
        GameControlAction.RotateCameraCounterClockwise => "Поворот камеры влево",
        GameControlAction.RotateCameraClockwise => "Поворот камеры вправо",
        GameControlAction.ToggleBlockTerrainPreview => "Просмотр блочного террейна",
        GameControlAction.ToggleInformationLayers => "Информационные слои",
        GameControlAction.ToggleAutonomousMovement => "Автономное движение выделения",
        _ => action.ToString()
    };

    public Keyboard.Key Get(GameControlAction action) => _keys[(int)action];

    public bool TrySet(GameControlAction action, Keyboard.Key key)
    {
        int index = (int)action;
        if (index < 0 || index >= _keys.Length || !IsBindable(key))
            return false;

        for (int i = 0; i < _keys.Length; i++)
        {
            if (i != index && _keys[i] == key)
                return false;
        }

        _keys[index] = key;
        return true;
    }

    public string[] GetBindingLabels()
    {
        string[] labels = new string[_keys.Length];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = GetKeyName(_keys[i]);
        return labels;
    }

    public void ResetDefaults()
    {
        Keyboard.Key[] defaults =
        {
            Keyboard.Key.Add, Keyboard.Key.Subtract, Keyboard.Key.Tab,
            Keyboard.Key.Q, Keyboard.Key.E, Keyboard.Key.B,
            Keyboard.Key.V, Keyboard.Key.F
        };
        Array.Copy(defaults, _keys, _keys.Length);
    }

    public bool Save()
    {
        try
        {
            string path = SettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            var stored = new StoredBindings
            {
                RaiseTerrainSlice = Get(GameControlAction.RaiseTerrainSlice),
                LowerTerrainSlice = Get(GameControlAction.LowerTerrainSlice),
                ToggleCameraProjection = Get(GameControlAction.ToggleCameraProjection),
                RotateCameraCounterClockwise = Get(GameControlAction.RotateCameraCounterClockwise),
                RotateCameraClockwise = Get(GameControlAction.RotateCameraClockwise),
                ToggleBlockTerrainPreview = Get(GameControlAction.ToggleBlockTerrainPreview),
                ToggleInformationLayers = Get(GameControlAction.ToggleInformationLayers),
                ToggleAutonomousMovement = Get(GameControlAction.ToggleAutonomousMovement)
            };

            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Controls] Не удалось сохранить назначения клавиш: {exception.Message}");
            return false;
        }
    }

    public static GameControlBindings Load()
    {
        var bindings = new GameControlBindings();
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return bindings;

            StoredBindings? stored = JsonSerializer.Deserialize<StoredBindings>(File.ReadAllText(path));
            if (stored == null)
                return bindings;

            Keyboard.Key[] loaded =
            {
                stored.RaiseTerrainSlice, stored.LowerTerrainSlice,
                stored.ToggleCameraProjection, stored.RotateCameraCounterClockwise,
                stored.RotateCameraClockwise, stored.ToggleBlockTerrainPreview,
                stored.ToggleInformationLayers, stored.ToggleAutonomousMovement
            };

            bool valid = true;
            for (int i = 0; i < loaded.Length && valid; i++)
            {
                if (!IsBindable(loaded[i]))
                {
                    valid = false;
                    break;
                }

                for (int j = 0; j < i; j++)
                {
                    if (loaded[i] == loaded[j])
                    {
                        valid = false;
                        break;
                    }
                }
            }

            if (valid)
                Array.Copy(loaded, bindings._keys, loaded.Length);
            else
                Console.WriteLine("[Controls] Найдены конфликтующие назначения. Используются клавиши по умолчанию.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Controls] Не удалось загрузить назначения клавиш: {exception.Message}");
        }

        return bindings;
    }

    public static bool IsBindable(Keyboard.Key key)
    {
        if (!Enum.IsDefined(key))
            return false;

        return key is not Keyboard.Key.Unknown
            and not Keyboard.Key.Escape
            and not Keyboard.Key.LShift
            and not Keyboard.Key.RShift
            and not Keyboard.Key.LControl
            and not Keyboard.Key.RControl
            and not Keyboard.Key.LAlt
            and not Keyboard.Key.RAlt
            and not Keyboard.Key.LSystem
            and not Keyboard.Key.RSystem;
    }

    public static string GetKeyName(Keyboard.Key key) => key switch
    {
        Keyboard.Key.Add => "NUMPAD +",
        Keyboard.Key.Subtract => "NUMPAD −",
        Keyboard.Key.Tab => "TAB",
        Keyboard.Key.PageUp => "PAGE UP",
        Keyboard.Key.PageDown => "PAGE DOWN",
        Keyboard.Key.Space => "SPACE",
        Keyboard.Key.Enter => "ENTER",
        Keyboard.Key.Backspace => "BACKSPACE",
        Keyboard.Key.Left => "←",
        Keyboard.Key.Right => "→",
        Keyboard.Key.Up => "↑",
        Keyboard.Key.Down => "↓",
        _ => key.ToString().ToUpperInvariant()
    };
}
