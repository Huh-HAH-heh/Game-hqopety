using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using NumericsVector3 = System.Numerics.Vector3;

namespace RimClone.Render;

/// <summary>
/// Interactive editor for the colonist's shared anatomy rig. All edits are applied
/// to the same BodyPartDefinition values used by UnitBodyStore, hit regions, and cards.
/// </summary>
public sealed class UnitBodyRigEditorOverlay : IDisposable
{
    private const int MaxParts = 128;
    private const int PropertyCount = 9;

    private static readonly Color BackdropColor = new(5, 9, 14, 255);
    private static readonly Color PanelColor = new(17, 24, 33, 255);
    private static readonly Color PanelEdge = new(77, 96, 118, 255);
    private static readonly Color MutedText = new(149, 166, 184, 255);
    private static readonly Color SelectedColor = new(230, 179, 85, 255);

    private readonly Action<string[], BodyPartDefinition[]> _saveRequested;
    private readonly Font? _font;
    private readonly RectangleShape _backdrop = new(new Vector2f(1f, 1f))
    {
        FillColor = BackdropColor
    };
    private readonly RectangleShape _panel = new(new Vector2f(1f, 1f))
    {
        FillColor = PanelColor,
        OutlineColor = PanelEdge,
        OutlineThickness = 1f
    };
    private readonly RectangleShape _header = new(new Vector2f(1f, 1f))
    {
        FillColor = new Color(29, 40, 54, 255)
    };
    private readonly RectangleShape _listPanel = new(new Vector2f(1f, 1f))
    {
        FillColor = new Color(20, 28, 38, 255),
        OutlineColor = new Color(56, 72, 91, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _propertiesPanel = new(new Vector2f(1f, 1f))
    {
        FillColor = new Color(20, 28, 38, 255),
        OutlineColor = new Color(56, 72, 91, 255),
        OutlineThickness = 1f
    };

    private readonly RectangleShape[] _partRows = new RectangleShape[MaxParts];
    private readonly Text?[] _partLabels = new Text?[MaxParts];
    private readonly RectangleShape[] _previewPanels = new RectangleShape[3];
    private readonly Text?[] _projectionLabels = new Text?[3];
    private readonly RectangleShape[] _symmetryButtons = new RectangleShape[3];
    private readonly Text?[] _symmetryLabels = new Text?[3];
    private readonly bool[] _symmetryEnabled = new bool[3];
    private readonly RectangleShape[] _sliderTracks = new RectangleShape[PropertyCount];
    private readonly RectangleShape[] _sliderFills = new RectangleShape[PropertyCount];
    private readonly RectangleShape[] _sliderKnobs = new RectangleShape[PropertyCount];
    private readonly Text?[] _sliderLabels = new Text?[PropertyCount];
    private readonly Text?[] _sliderValues = new Text?[PropertyCount];

    private readonly RectangleShape _saveButton = new(new Vector2f(104f, 30f))
    {
        FillColor = new Color(47, 99, 77, 255),
        OutlineColor = new Color(93, 151, 117, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _loadButton = new(new Vector2f(104f, 30f))
    {
        FillColor = new Color(45, 65, 84, 255),
        OutlineColor = new Color(82, 112, 139, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _exportButton = new(new Vector2f(104f, 30f))
    {
        FillColor = new Color(45, 55, 69, 255),
        OutlineColor = new Color(79, 95, 115, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _defaultsButton = new(new Vector2f(124f, 30f))
    {
        FillColor = new Color(45, 55, 69, 255),
        OutlineColor = new Color(79, 95, 115, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _reloadButton = new(new Vector2f(104f, 30f))
    {
        FillColor = new Color(45, 55, 69, 255),
        OutlineColor = new Color(79, 95, 115, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _closeButton = new(new Vector2f(30f, 30f))
    {
        FillColor = new Color(51, 40, 43, 255),
        OutlineColor = new Color(111, 74, 79, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _primitiveButton = new(new Vector2f(1f, 30f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _parentButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _allSymmetryButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(52, 74, 64, 255),
        OutlineColor = new Color(90, 128, 105, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _gridButton = new(new Vector2f(112f, 26f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _gridStepButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _surfaceSnapButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(52, 99, 74, 255),
        OutlineColor = new Color(90, 128, 105, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape[] _gridLines = new RectangleShape[2];
    private readonly RectangleShape _addRegionButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(44, 81, 67, 255),
        OutlineColor = new Color(77, 125, 101, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _addAllFormsButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(47, 65, 82, 255),
        OutlineColor = new Color(79, 103, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _removeRegionButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(76, 43, 47, 255),
        OutlineColor = new Color(131, 76, 82, 255),
        OutlineThickness = 1f
    };

    private readonly Text? _title;
    private readonly Text? _saveLabel;
    private readonly Text? _loadLabel;
    private readonly Text? _exportLabel;
    private readonly Text? _defaultsLabel;
    private readonly Text? _reloadLabel;
    private readonly Text? _parentLabel;
    private readonly Text? _allSymmetryLabel;
    private readonly Text? _gridLabel;
    private readonly Text? _gridStepLabel;
    private readonly Text? _surfaceSnapLabel;
    private readonly Text? _addRegionLabel;
    private readonly Text? _addAllFormsLabel;
    private readonly Text? _removeRegionLabel;
    private readonly Text? _closeLabel;
    private readonly Text? _listTitle;
    private readonly Text? _propertiesTitle;
    private readonly Text? _selectedName;
    private readonly Text? _selectedInfo;
    private readonly Text? _primitiveLabel;
    private readonly Text? _helpText;
    private readonly Text? _statusText;
    private readonly CircleShape _previewCircle = new(1f)
    {
        Origin = new Vector2f(1f, 1f)
    };
    private readonly RectangleShape _previewBox = new(new Vector2f(2f, 2f))
    {
        Origin = new Vector2f(1f, 1f)
    };
    private readonly ConvexShape _previewTriangle = new(3)
    {
        Origin = new Vector2f(0f, 0f)
    };

    private string[] _regionIds = Array.Empty<string>();
    private string[] _openedRegionIds = Array.Empty<string>();
    private BodyPartDefinition[] _parts = Array.Empty<BodyPartDefinition>();
    private BodyPartDefinition[] _defaultParts = Array.Empty<BodyPartDefinition>();
    private BodyPartDefinition[] _openedParts = Array.Empty<BodyPartDefinition>();
    private NumericsVector3[] _worldPositions = Array.Empty<NumericsVector3>();
    private Quaternion[] _worldOrientations = Array.Empty<Quaternion>();
    private ProjectedPart[][] _projected = new ProjectedPart[3][];
    private ProjectionLayout[] _projectionLayouts = new ProjectionLayout[3];

    private int _selectedPart;
    private int _selectedProperty;
    private int _activeProjection = -1;
    private float _headerScale = 1f;
    private int _draggingProperty = -1;
    private int _draggingProjection = -1;
    private bool _draggingPart;
    private int _surfaceSnapTargetPart = -1;
    private int _surfaceSnapProjection = -1;
    private float _surfaceSnapDirectionX = 1f;
    private float _surfaceSnapDirectionY;
    private bool _hasSurfaceSnapDirection;
    private int _lastMouseX;
    private int _lastMouseY;
    private float _listRowHeight = 28f;
    private float _propertyRowHeight = 46f;
    private int _listScroll;
    private int _listVisibleRows = 12;
    private bool _gridEnabled = true;
    private bool _surfaceSnapEnabled = false;
    private readonly float[] _gridSteps = { 0.0005f, 0.001f, 0.0025f, 0.005f, 0.01f, 0.025f, 0.05f, 0.1f, 0.2f };
    private int _gridStepIndex = 3;
    private string _status = "Перетаскивание привязано к сетке. Настрой углы X/Y/Z в параметрах.";
    private bool _disposed;

    public bool IsOpen { get; private set; }

    public UnitBodyRigEditorOverlay(Action<string[], BodyPartDefinition[]> saveRequested)
    {
        _saveRequested = saveRequested ?? throw new ArgumentNullException(nameof(saveRequested));
        _font = LoadFont();

        for (int i = 0; i < _partRows.Length; i++)
        {
            _partRows[i] = new RectangleShape(new Vector2f(0f, 0f))
            {
                FillColor = new Color(28, 38, 50, 255),
                OutlineColor = new Color(52, 67, 84, 255),
                OutlineThickness = 1f
            };
        }
        for (int i = 0; i < _gridLines.Length; i++)
        {
            _gridLines[i] = new RectangleShape(new Vector2f(1f, 1f))
            {
                FillColor = new Color(76, 97, 119, 92)
            };
        }

        string[] viewNames = { "СПЕРЕДИ  ·  Y / Z", "СБОКУ  ·  X / Z", "СВЕРХУ  ·  X / Y" };
        for (int i = 0; i < _previewPanels.Length; i++)
        {
            _previewPanels[i] = new RectangleShape(new Vector2f(0f, 0f))
            {
                FillColor = new Color(15, 22, 31, 255),
                OutlineColor = new Color(58, 75, 94, 255),
                OutlineThickness = 1f
            };
            _symmetryButtons[i] = new RectangleShape(new Vector2f(88f, 22f))
            {
                FillColor = new Color(39, 51, 65, 255),
                OutlineColor = new Color(74, 92, 113, 255),
                OutlineThickness = 1f
            };
            if (_font != null)
            {
                _projectionLabels[i] = new Text(_font, viewNames[i], 10)
                {
                    FillColor = new Color(185, 201, 219, 255),
                    Style = Text.Styles.Bold
                };
                _symmetryLabels[i] = new Text(_font, "СИМ: ВЫКЛ", 8)
                {
                    FillColor = MutedText,
                    Style = Text.Styles.Bold
                };
            }
        }

        string[] propertyNames =
        {
            "ЛОКАЛЬНЫЙ X",
            "ЛОКАЛЬНЫЙ Y",
            "ЛОКАЛЬНЫЙ Z",
            "РАЗМЕР X",
            "РАЗМЕР Y",
            "РАЗМЕР Z",
            "НАКЛОН X, ГРАДУСЫ",
            "НАКЛОН Y, ГРАДУСЫ",
            "ПОВОРОТ Z, ГРАДУСЫ"
        };
        for (int i = 0; i < PropertyCount; i++)
        {
            _sliderTracks[i] = new RectangleShape(new Vector2f(1f, 6f))
            {
                FillColor = new Color(46, 58, 73, 255)
            };
            _sliderFills[i] = new RectangleShape(new Vector2f(1f, 6f))
            {
                FillColor = new Color(83, 149, 125, 255)
            };
            _sliderKnobs[i] = new RectangleShape(new Vector2f(7f, 14f))
            {
                FillColor = new Color(224, 233, 243, 255),
                OutlineColor = new Color(32, 44, 57, 255),
                OutlineThickness = 1f
            };
            if (_font != null)
            {
                _sliderLabels[i] = new Text(_font, propertyNames[i], 9)
                {
                    FillColor = MutedText
                };
                _sliderValues[i] = new Text(_font, "", 10)
                {
                    FillColor = new Color(226, 233, 241, 255),
                    Style = Text.Styles.Bold
                };
            }
        }

        if (_font != null)
        {
            _title = new Text(_font, "КОНСТРУКТОР ТЕЛА КОЛОНИСТА", 16)
            {
                FillColor = new Color(235, 241, 249, 255),
                Style = Text.Styles.Bold
            };
            _saveLabel = new Text(_font, "СОХРАНИТЬ", 10)
            {
                FillColor = new Color(240, 248, 242, 255),
                Style = Text.Styles.Bold
            };
            _loadLabel = new Text(_font, "ЗАГРУЗИТЬ", 10)
            {
                FillColor = new Color(224, 236, 245, 255),
                Style = Text.Styles.Bold
            };
            _exportLabel = new Text(_font, "ЭКСПОРТ", 10)
            {
                FillColor = new Color(220, 229, 239, 255)
            };
            _defaultsLabel = new Text(_font, "ПО УМОЛЧАНИЮ", 9)
            {
                FillColor = new Color(220, 229, 239, 255)
            };
            _reloadLabel = new Text(_font, "ОТКАТИТЬ", 10)
            {
                FillColor = new Color(220, 229, 239, 255)
            };
            _closeLabel = new Text(_font, "×", 18)
            {
                FillColor = new Color(236, 220, 222, 255)
            };
            _listTitle = new Text(_font, "ЧАСТИ ТЕЛА", 10)
            {
                FillColor = new Color(160, 183, 207, 255),
                Style = Text.Styles.Bold
            };
            _propertiesTitle = new Text(_font, "ПАРАМЕТРЫ ВЫБРАННОЙ ЧАСТИ", 10)
            {
                FillColor = new Color(160, 183, 207, 255),
                Style = Text.Styles.Bold
            };
            _selectedName = new Text(_font, "", 13)
            {
                FillColor = SelectedColor,
                Style = Text.Styles.Bold
            };
            _selectedInfo = new Text(_font, "", 9)
            {
                FillColor = MutedText
            };
            _primitiveLabel = new Text(_font, "", 10)
            {
                FillColor = new Color(232, 240, 248, 255)
            };
            _parentLabel = new Text(_font, "", 9)
            {
                FillColor = new Color(232, 240, 248, 255)
            };
            _allSymmetryLabel = new Text(_font, "СИММЕТРИЯ: ВСЕ ВИДЫ", 9)
            {
                FillColor = new Color(232, 240, 248, 255)
            };
            _gridLabel = new Text(_font, "СЕТКА: ВКЛ", 9)
            {
                FillColor = new Color(232, 240, 248, 255),
                Style = Text.Styles.Bold
            };
            _gridStepLabel = new Text(_font, "0.005", 9)
            {
                FillColor = new Color(232, 240, 248, 255),
                Style = Text.Styles.Bold
            };
            _surfaceSnapLabel = new Text(_font, "НОРМ", 9)
            {
                FillColor = new Color(220, 239, 225, 255),
                Style = Text.Styles.Bold
            };
            _addRegionLabel = new Text(_font, "+ ДОБАВИТЬ РЕГИОН", 9)
            {
                FillColor = new Color(237, 246, 241, 255),
                Style = Text.Styles.Bold
            };
            _addAllFormsLabel = new Text(_font, "+ ВСЕ ФОРМЫ", 9)
            {
                FillColor = new Color(224, 233, 242, 255)
            };
            _removeRegionLabel = new Text(_font, "УДАЛИТЬ СВОЙ РЕГИОН", 9)
            {
                FillColor = new Color(243, 225, 227, 255)
            };
            _helpText = new Text(_font,
                "Перетаскивай сегменты; родитель задаёт зависимость. Ctrl+S — применить и сохранить.",
                9)
            {
                FillColor = MutedText
            };
            _statusText = new Text(_font, "", 9)
            {
                FillColor = new Color(150, 205, 173, 255)
            };
        }

        _previewTriangle.SetPoint(0, new Vector2f(0f, -1f));
        _previewTriangle.SetPoint(1, new Vector2f(1f, 1f));
        _previewTriangle.SetPoint(2, new Vector2f(-1f, 1f));
    }

    public void Open()
    {
        UnitAnatomyProfile anatomy = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        ReadOnlySpan<UnitAnatomyNode> nodes = anatomy.Nodes;
        if (!anatomy.HasSharedShapes || nodes.Length == 0 || nodes.Length > MaxParts)
        {
            SetStatus($"Невозможно открыть риг: допустимо от 1 до {MaxParts} регионов.");
            return;
        }

        var ids = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            ids[i] = nodes[i].Id;

        SetRigData(ids, anatomy.BodyParts.ToArray(), setDefaults: true, setOpenedSnapshot: true);
        _selectedProperty = 0;
        _draggingProperty = -1;
        _draggingProjection = -1;
        _draggingPart = false;
        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        _status = $"Загружено регионов: {_parts.Length}.";
        IsOpen = true;
    }

    private bool SetRigData(
        string[] regionIds,
        BodyPartDefinition[] parts,
        bool setDefaults,
        bool setOpenedSnapshot)
    {
        if (regionIds.Length != parts.Length || parts.Length == 0 || parts.Length > MaxParts)
        {
            SetStatus($"Риг должен содержать от 1 до {MaxParts} регионов.");
            return false;
        }

        var unique = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(regionIds[i]) || !unique.Add(regionIds[i]))
            {
                SetStatus($"Пустой или повторяющийся ID региона: {regionIds[i]}");
                return false;
            }

            if (parts[i].ParentIndex < -1 || parts[i].ParentIndex >= i)
            {
                SetStatus($"У региона «{PartName(regionIds[i])}» родитель должен находиться выше в списке.");
                return false;
            }
        }

        _regionIds = (string[])regionIds.Clone();
        _parts = (BodyPartDefinition[])parts.Clone();
        if (setDefaults || _defaultParts.Length != _parts.Length)
            _defaultParts = UnitBodyRigSettingsStore.GetDefaultBodyParts(_regionIds, _parts);
        if (setOpenedSnapshot)
        {
            _openedRegionIds = (string[])_regionIds.Clone();
            _openedParts = (BodyPartDefinition[])_parts.Clone();
        }

        _worldPositions = new NumericsVector3[_parts.Length];
        _worldOrientations = new Quaternion[_parts.Length];
        for (int view = 0; view < _projected.Length; view++)
            _projected[view] = new ProjectedPart[_parts.Length];

        for (int i = 0; i < _partLabels.Length; i++)
        {
            _partLabels[i]?.Dispose();
            _partLabels[i] = i < _regionIds.Length && _font != null
                ? new Text(_font, $"{i + 1:00}  {PartName(_regionIds[i])}", 10)
                {
                    FillColor = new Color(210, 221, 233, 255)
                }
                : null;
        }

        _selectedPart = Math.Clamp(_selectedPart, 0, _parts.Length - 1);
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _parts.Length - _listVisibleRows));
        return true;
    }

    public void Close()
    {
        IsOpen = false;
        _draggingProperty = -1;
        _draggingProjection = -1;
        _draggingPart = false;
        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        _activeProjection = -1;
    }

    public void SetStatus(string status) => _status = status ?? string.Empty;

    public bool HandleMousePressed(Vector2i position)
    {
        if (!IsOpen)
            return false;

        if (_closeButton.GetGlobalBounds().Contains(position))
        {
            Close();
            return true;
        }

        if (_saveButton.GetGlobalBounds().Contains(position))
        {
            Save();
            return true;
        }

        if (_loadButton.GetGlobalBounds().Contains(position))
        {
            LoadRigFromDialog();
            return true;
        }

        if (_exportButton.GetGlobalBounds().Contains(position))
        {
            ExportRigFromDialog();
            return true;
        }

        if (_defaultsButton.GetGlobalBounds().Contains(position))
        {
            if (_defaultParts.Length == _parts.Length)
                Array.Copy(_defaultParts, _parts, _parts.Length);
            _status = "Восстановлены базовые формы. Нажми «Сохранить», чтобы применить.";
            return true;
        }

        if (_reloadButton.GetGlobalBounds().Contains(position))
        {
            SetRigData(_openedRegionIds, _openedParts, setDefaults: false, setOpenedSnapshot: false);
            _status = "Возвращена конфигурация, с которой редактор был открыт.";
            return true;
        }

        if (_addRegionButton.GetGlobalBounds().Contains(position))
        {
            AddRegion();
            return true;
        }

        if (_addAllFormsButton.GetGlobalBounds().Contains(position))
        {
            AddAllPrimitiveExamples();
            return true;
        }

        if (_removeRegionButton.GetGlobalBounds().Contains(position))
        {
            RemoveSelectedCustomRegion();
            return true;
        }

        if (_parentButton.GetGlobalBounds().Contains(position))
        {
            CycleParentDependency();
            return true;
        }

        if (_allSymmetryButton.GetGlobalBounds().Contains(position))
        {
            bool enable = !_symmetryEnabled[0] || !_symmetryEnabled[1] || !_symmetryEnabled[2];
            Array.Fill(_symmetryEnabled, enable);
            _status = enable ? "Симметрия включена во всех проекциях." : "Симметрия выключена во всех проекциях.";
            return true;
        }

        if (_gridButton.GetGlobalBounds().Contains(position))
        {
            _gridEnabled = !_gridEnabled;
            _status = _gridEnabled
                ? $"Сетка и привязка включены. Шаг: {_gridSteps[_gridStepIndex]:0.000}."
                : "Сетка и привязка отключены.";
            return true;
        }

        if (_gridStepButton.GetGlobalBounds().Contains(position))
        {
            _gridStepIndex = (_gridStepIndex + 1) % _gridSteps.Length;
            _status = $"Шаг сетки: {_gridSteps[_gridStepIndex]:0.####}. Перемещение привязано к этому шагу.";
            return true;
        }

        if (_surfaceSnapButton.GetGlobalBounds().Contains(position))
        {
            _surfaceSnapEnabled = !_surfaceSnapEnabled;
            _surfaceSnapTargetPart = -1;
            _surfaceSnapProjection = -1;
            _hasSurfaceSnapDirection = false;
            _status = _surfaceSnapEnabled
                ? "Автопривязка включена: близкие части стыкуются по нормали поверхности. N — переключить."
                : "Автопривязка к поверхности отключена.";
            return true;
        }

        for (int projection = 0; projection < _symmetryButtons.Length; projection++)
        {
            if (!_symmetryButtons[projection].GetGlobalBounds().Contains(position))
                continue;

            _symmetryEnabled[projection] = !_symmetryEnabled[projection];
            _status = $"Симметрия «{new[] { "спереди", "сбоку", "сверху" }[projection]}»: " +
                (_symmetryEnabled[projection] ? "включена." : "выключена.");
            return true;
        }

        if (_primitiveButton.GetGlobalBounds().Contains(position))
        {
            BodyPartDefinition part = _parts[_selectedPart];
            BodyPrimitive primitive = part.Primitive switch
            {
                BodyPrimitive.Circle => BodyPrimitive.Box,
                BodyPrimitive.Box => BodyPrimitive.Triangle,
                _ => BodyPrimitive.Circle
            };
            _parts[_selectedPart] = Copy(part, primitive: primitive);
            ApplyEnabledSymmetry();
            _status = $"Форма «{PartName(_regionIds[_selectedPart])}»: {primitive}.";
            return true;
        }

        for (int visibleRow = 0; visibleRow < _listVisibleRows; visibleRow++)
        {
            int itemIndex = _listScroll + visibleRow;
            if (itemIndex >= _parts.Length ||
                !_partRows[visibleRow].GetGlobalBounds().Contains(position))
                continue;

            bool shiftPressed =
                Keyboard.IsKeyPressed(Keyboard.Key.LShift) ||
                Keyboard.IsKeyPressed(Keyboard.Key.RShift);
            if (shiftPressed && itemIndex < _selectedPart)
            {
                BodyPartDefinition selectedPart = _parts[_selectedPart];
                _parts[_selectedPart] = Copy(
                    selectedPart,
                    parentIndex: itemIndex,
                    anchor: BodyPartAnchor.Parent);
                _status = $"«{PartName(_regionIds[_selectedPart])}» теперь зависит от «{PartName(_regionIds[itemIndex])}». Нажми Ctrl+S.";
                return true;
            }

            _selectedPart = itemIndex;
            _surfaceSnapTargetPart = -1;
            _surfaceSnapProjection = -1;
            _hasSurfaceSnapDirection = false;
            _activeProjection = -1;
            _draggingProperty = -1;
            _draggingProjection = -1;
            _draggingPart = false;
            _status = $"Выбрано: {PartName(_regionIds[itemIndex])}. Shift+щелчок по более раннему региону задаёт родителя.";
            return true;
        }

        for (int i = 0; i < PropertyCount; i++)
        {
            if (!_sliderTracks[i].GetGlobalBounds().Contains(position))
                continue;

            _selectedProperty = i;
            _activeProjection = -1;
            _draggingProperty = i;
            UpdatePropertyFromSlider(i, position.X);
            return true;
        }

        for (int projection = 0; projection < _previewPanels.Length; projection++)
        {
            if (!_previewPanels[projection].GetGlobalBounds().Contains(position))
                continue;

            _activeProjection = projection;
            int hitPart = HitTestPreview(projection, position.X, position.Y);
            if (hitPart >= 0)
            {
                _selectedPart = hitPart;
                _surfaceSnapTargetPart = -1;
                _surfaceSnapProjection = -1;
                _hasSurfaceSnapDirection = false;
                _draggingProjection = projection;
                _draggingPart = true;
                _lastMouseX = position.X;
                _lastMouseY = position.Y;
                _status = $"Перемещение: {PartName(_regionIds[hitPart])}. Отпусти мышь для завершения.";
            }
            else
            {
                _status = $"Активен вид: {ProjectionName(projection)}. Стрелки перемещают выбранную часть по сетке.";
            }

            return true;
        }

        return true;
    }

    public bool HandleMouseMoved(Vector2i position)
    {
        if (!IsOpen)
            return false;

        if (_draggingProperty >= 0)
        {
            UpdatePropertyFromSlider(_draggingProperty, position.X);
            return true;
        }

        if (_draggingPart && _draggingProjection >= 0)
        {
            ApplyProjectionDrag(
                _draggingProjection,
                position.X - _lastMouseX,
                position.Y - _lastMouseY);
            _lastMouseX = position.X;
            _lastMouseY = position.Y;
            return true;
        }

        return true;
    }

    public bool HandleMouseReleased()
    {
        if (!IsOpen)
            return false;

        _draggingProperty = -1;
        _draggingProjection = -1;
        _draggingPart = false;
        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        return true;
    }

    public bool HandleMouseWheel(Vector2i position, float delta)
    {
        if (!IsOpen)
            return false;

        if (_listPanel.GetGlobalBounds().Contains(position))
        {
            int step = delta > 0f ? -3 : 3;
            _listScroll = Math.Clamp(_listScroll + step, 0, Math.Max(0, _parts.Length - _listVisibleRows));
        }

        return true;
    }

    public bool HandleKey(Keyboard.Key key, bool controlPressed)
    {
        if (!IsOpen)
            return false;

        if (key == Keyboard.Key.Escape)
        {
            Close();
            return true;
        }

        if (key == Keyboard.Key.N)
        {
            _surfaceSnapEnabled = !_surfaceSnapEnabled;
            _surfaceSnapTargetPart = -1;
            _surfaceSnapProjection = -1;
            _hasSurfaceSnapDirection = false;
            _status = _surfaceSnapEnabled
                ? "Автопривязка к нормали включена."
                : "Автопривязка к нормали отключена.";
            return true;
        }

        if (controlPressed && key == Keyboard.Key.S)
        {
            Save();
            return true;
        }

        if (_activeProjection >= 0 &&
            key is Keyboard.Key.Left or Keyboard.Key.Right or Keyboard.Key.Up or Keyboard.Key.Down)
        {
            int horizontal = key == Keyboard.Key.Left ? -1 : key == Keyboard.Key.Right ? 1 : 0;
            int vertical = key == Keyboard.Key.Up ? 1 : key == Keyboard.Key.Down ? -1 : 0;
            NudgeSelectedPart(_activeProjection, horizontal, vertical);
            return true;
        }

        if (key == Keyboard.Key.Up)
        {
            _selectedPart = (_selectedPart - 1 + _parts.Length) % _parts.Length;
            EnsureSelectionVisible();
            return true;
        }

        if (key == Keyboard.Key.Down)
        {
            _selectedPart = (_selectedPart + 1) % _parts.Length;
            EnsureSelectionVisible();
            return true;
        }

        if (key == Keyboard.Key.Tab)
        {
            _selectedProperty = (_selectedProperty + 1) % PropertyCount;
            return true;
        }

        if (key is Keyboard.Key.Left or Keyboard.Key.Right)
        {
            float direction = key == Keyboard.Key.Right ? 1f : -1f;
            AdjustProperty(_selectedProperty, direction);
            return true;
        }

        if (key == Keyboard.Key.P)
        {
            BodyPartDefinition part = _parts[_selectedPart];
            BodyPrimitive primitive = part.Primitive switch
            {
                BodyPrimitive.Circle => BodyPrimitive.Box,
                BodyPrimitive.Box => BodyPrimitive.Triangle,
                _ => BodyPrimitive.Circle
            };
            _parts[_selectedPart] = Copy(part, primitive: primitive);
            ApplyEnabledSymmetry();
            return true;
        }

        return true;
    }

    public void Draw(RenderWindow window, uint width, uint height)
    {
        if (!IsOpen)
            return;

        Layout(width, height);
        CalculateWorldTransforms();

        _backdrop.Position = new Vector2f(0f, 0f);
        _backdrop.Size = new Vector2f(width, height);
        window.Draw(_backdrop);
        window.Draw(_panel);
        window.Draw(_header);
        window.Draw(_listPanel);
        window.Draw(_propertiesPanel);

        if (_headerScale < 0.9f)
        {
            if (_saveLabel != null) _saveLabel.DisplayedString = "СОХР.";
            if (_loadLabel != null) _loadLabel.DisplayedString = "ЗАГР.";
            if (_exportLabel != null) _exportLabel.DisplayedString = "ЭКСП.";
            if (_defaultsLabel != null) _defaultsLabel.DisplayedString = "СБРОС";
            if (_reloadLabel != null) _reloadLabel.DisplayedString = "ОТКАТ";
        }
        else
        {
            if (_saveLabel != null) _saveLabel.DisplayedString = "СОХРАНИТЬ";
            if (_loadLabel != null) _loadLabel.DisplayedString = "ЗАГРУЗИТЬ";
            if (_exportLabel != null) _exportLabel.DisplayedString = "ЭКСПОРТ";
            if (_defaultsLabel != null) _defaultsLabel.DisplayedString = "ПО УМОЛЧАНИЮ";
            if (_reloadLabel != null) _reloadLabel.DisplayedString = "ОТКАТИТЬ";
        }
        if (_title != null)
            DrawText(window, _title);
        DrawButton(window, _saveButton, _saveLabel);
        DrawButton(window, _loadButton, _loadLabel);
        DrawButton(window, _exportButton, _exportLabel);
        DrawButton(window, _defaultsButton, _defaultsLabel);
        DrawButton(window, _reloadButton, _reloadLabel);
        DrawButton(window, _closeButton, _closeLabel);

        if (_listTitle != null)
            DrawText(window, _listTitle);
        if (_propertiesTitle != null)
            DrawText(window, _propertiesTitle);

        for (int visibleRow = 0; visibleRow < _listVisibleRows; visibleRow++)
        {
            int itemIndex = _listScroll + visibleRow;
            if (itemIndex >= _parts.Length)
                break;

            _partRows[visibleRow].FillColor = itemIndex == _selectedPart
                ? new Color(68, 79, 88, 255)
                : new Color(28, 38, 50, 255);
            _partRows[visibleRow].OutlineColor = itemIndex == _selectedPart
                ? SelectedColor
                : new Color(52, 67, 84, 255);
            window.Draw(_partRows[visibleRow]);

            if (_partLabels[itemIndex] != null)
            {
                _partLabels[itemIndex]!.DisplayedString = $"{itemIndex + 1:00}  {PartName(_regionIds[itemIndex])}";
                _partLabels[itemIndex]!.Position = new Vector2f(
                    _partRows[visibleRow].Position.X + 8f,
                    _partRows[visibleRow].Position.Y + 7f);
                DrawText(window, _partLabels[itemIndex]);
            }
        }

        DrawButton(window, _addRegionButton, _addRegionLabel);
        DrawButton(window, _addAllFormsButton, _addAllFormsLabel);
        DrawButton(window, _removeRegionButton, _removeRegionLabel);

        for (int projection = 0; projection < _previewPanels.Length; projection++)
            DrawProjection(window, projection);

        if (_selectedName != null)
        {
            _selectedName.DisplayedString = PartName(_regionIds[_selectedPart]);
            DrawText(window, _selectedName);
        }

        if (_selectedInfo != null)
        {
            BodyPartDefinition part = _parts[_selectedPart];
            string parent = part.ParentIndex >= 0 ? PartName(_regionIds[part.ParentIndex]) : "Корень";
            _selectedInfo.DisplayedString =
                $"Родитель: {parent}\nPivot: {part.Pivot}   Motion: {part.Motion}\nRole: {part.Role}   Anchor: {part.Anchor}";
            DrawText(window, _selectedInfo);
        }

        for (int i = 0; i < PropertyCount; i++)
        {
            DrawProperty(window, i);
        }

        if (_primitiveLabel != null)
        {
            _primitiveLabel.DisplayedString = $"ФОРМА: {_parts[_selectedPart].Primitive}   [P]";
            _primitiveLabel.Position = new Vector2f(
                _primitiveButton.Position.X + 8f,
                _primitiveButton.Position.Y + 8f);
            window.Draw(_primitiveButton);
            DrawText(window, _primitiveLabel);
        }

        if (_parentLabel != null)
        {
            int parent = _parts[_selectedPart].ParentIndex;
            string parentName = parent >= 0 ? PartName(_regionIds[parent]) : "корень";
            _parentLabel.DisplayedString = $"РОДИТЕЛЬ: {parentName}  [клик]";
            _parentLabel.Position = new Vector2f(
                _parentButton.Position.X + 8f,
                _parentButton.Position.Y + 6f);
            window.Draw(_parentButton);
            DrawText(window, _parentLabel);
        }

        if (_allSymmetryLabel != null)
        {
            _allSymmetryLabel.DisplayedString = _symmetryEnabled[0] && _symmetryEnabled[1] && _symmetryEnabled[2]
                ? "СИММЕТРИЯ ВСЕХ: ВКЛ"
                : "СИММЕТРИЯ ВСЕХ: ВЫКЛ";
            _allSymmetryLabel.Position = new Vector2f(
                _allSymmetryButton.Position.X + 8f,
                _allSymmetryButton.Position.Y + 6f);
            window.Draw(_allSymmetryButton);
            DrawText(window, _allSymmetryLabel);
        }

        if (_gridLabel != null)
        {
            _gridLabel.DisplayedString = _gridEnabled ? "СЕТКА" : "СЕТКА·";
            _gridLabel.Position = new Vector2f(_gridButton.Position.X + 8f, _gridButton.Position.Y + 6f);
            window.Draw(_gridButton);
            DrawText(window, _gridLabel);
        }

        if (_gridStepLabel != null)
        {
            _gridStepLabel.DisplayedString = $"{_gridSteps[_gridStepIndex]:0.####}";
            _gridStepLabel.Position = new Vector2f(_gridStepButton.Position.X + 3f, _gridStepButton.Position.Y + 6f);
            window.Draw(_gridStepButton);
            DrawText(window, _gridStepLabel);
        }

        if (_surfaceSnapLabel != null)
        {
            _surfaceSnapButton.FillColor = _surfaceSnapEnabled
                ? new Color(52, 99, 74, 255)
                : new Color(42, 57, 72, 255);
            _surfaceSnapLabel.DisplayedString = _surfaceSnapEnabled ? "НОРМ" : "НОРМ·";
            _surfaceSnapLabel.Position = new Vector2f(_surfaceSnapButton.Position.X + 6f, _surfaceSnapButton.Position.Y + 6f);
            window.Draw(_surfaceSnapButton);
            DrawText(window, _surfaceSnapLabel);
        }

        if (_helpText != null)
            DrawText(window, _helpText);
        if (_statusText != null)
        {
            _statusText.DisplayedString = _status;
            DrawText(window, _statusText);
        }
    }

    private void Layout(uint width, uint height)
    {
        float x = 16f;
        float y = 14f;
        float panelWidth = MathF.Max(800f, width - 32f);
        float panelHeight = MathF.Max(500f, height - 28f);
        if (panelWidth > width - 8f)
        {
            x = 4f;
            panelWidth = MathF.Max(1f, width - 8f);
        }
        if (panelHeight > height - 8f)
        {
            y = 4f;
            panelHeight = MathF.Max(1f, height - 8f);
        }

        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(panelWidth, panelHeight);
        _header.Position = new Vector2f(x + 1f, y + 1f);
        _header.Size = new Vector2f(panelWidth - 2f, 44f);

        // Compact the header on small windows instead of stacking captions over one another.
        _headerScale = Math.Clamp(panelWidth / 1180f, 0.65f, 1f);
        float buttonGap = 8f * _headerScale;
        float buttonHeight = 30f * _headerScale;
        float buttonY = y + (44f - buttonHeight) * 0.5f;
        float right = x + panelWidth - buttonGap;

        _closeButton.Size = new Vector2f(30f * _headerScale, buttonHeight);
        _closeButton.Position = new Vector2f(right - _closeButton.Size.X, buttonY);
        right = _closeButton.Position.X - buttonGap;
        _reloadButton.Size = new Vector2f(104f * _headerScale, buttonHeight);
        _reloadButton.Position = new Vector2f(right - _reloadButton.Size.X, buttonY);
        right = _reloadButton.Position.X - buttonGap;
        _defaultsButton.Size = new Vector2f(124f * _headerScale, buttonHeight);
        _defaultsButton.Position = new Vector2f(right - _defaultsButton.Size.X, buttonY);
        right = _defaultsButton.Position.X - buttonGap;
        _saveButton.Size = new Vector2f(104f * _headerScale, buttonHeight);
        _saveButton.Position = new Vector2f(right - _saveButton.Size.X, buttonY);
        right = _saveButton.Position.X - buttonGap;
        _exportButton.Size = new Vector2f(104f * _headerScale, buttonHeight);
        _exportButton.Position = new Vector2f(right - _exportButton.Size.X, buttonY);
        right = _exportButton.Position.X - buttonGap;
        _loadButton.Size = new Vector2f(104f * _headerScale, buttonHeight);
        _loadButton.Position = new Vector2f(right - _loadButton.Size.X, buttonY);

        if (_title != null)
        {
            _title.DisplayedString = _headerScale < 0.9f ? "КОНСТРУКТОР ТЕЛА" : "КОНСТРУКТОР ТЕЛА КОЛОНИСТА";
            _title.Position = new Vector2f(MathF.Round(x + 14f), MathF.Round(y + 12f));
        }
        if (_saveLabel != null) _saveLabel.Position = new Vector2f(_saveButton.Position.X + 4f, buttonY + MathF.Max(1f, (buttonHeight - 10f) * 0.5f));
        if (_loadLabel != null) _loadLabel.Position = new Vector2f(_loadButton.Position.X + 4f, buttonY + MathF.Max(1f, (buttonHeight - 10f) * 0.5f));
        if (_exportLabel != null) _exportLabel.Position = new Vector2f(_exportButton.Position.X + 4f, buttonY + MathF.Max(1f, (buttonHeight - 10f) * 0.5f));
        if (_defaultsLabel != null) _defaultsLabel.Position = new Vector2f(_defaultsButton.Position.X + 4f, buttonY + MathF.Max(1f, (buttonHeight - 9f) * 0.5f));
        if (_reloadLabel != null) _reloadLabel.Position = new Vector2f(_reloadButton.Position.X + 4f, buttonY + MathF.Max(1f, (buttonHeight - 10f) * 0.5f));
        if (_closeLabel != null)
        {
            _closeLabel.CharacterSize = (uint)MathF.Max(12f, 18f * _headerScale);
            _closeLabel.Position = new Vector2f(_closeButton.Position.X + (_closeButton.Size.X - _closeLabel.CharacterSize) * 0.5f, buttonY);
        }

        float contentY = y + 54f;
        float contentHeight = panelHeight - 104f;
        float listX = x + 10f;
        float listWidth = Math.Clamp(panelWidth * 0.18f, 140f, 188f);
        float propertiesWidth = Math.Clamp(panelWidth * 0.22f, 190f, 244f);
        float propertiesX = x + panelWidth - propertiesWidth - 10f;
        float previewsX = listX + listWidth + 12f;
        float previewsWidth = MathF.Max(180f, propertiesX - previewsX - 12f);
        float gap = 8f;
        float previewWidth = MathF.Max(55f, (previewsWidth - gap * 2f) / 3f);

        _listPanel.Position = new Vector2f(listX, contentY);
        _listPanel.Size = new Vector2f(listWidth, contentHeight);
        _propertiesPanel.Position = new Vector2f(propertiesX, contentY);
        _propertiesPanel.Size = new Vector2f(propertiesWidth, contentHeight);

        if (_listTitle != null)
            _listTitle.Position = new Vector2f(listX + 9f, contentY + 9f);
        if (_propertiesTitle != null)
            _propertiesTitle.Position = new Vector2f(propertiesX + 10f, contentY + 9f);

        _listRowHeight = 28f;
        _listVisibleRows = Math.Clamp((int)((contentHeight - 135f) / _listRowHeight), 4, MaxParts);
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _parts.Length - _listVisibleRows));
        if (_selectedPart < _listScroll)
            _listScroll = _selectedPart;
        else if (_selectedPart >= _listScroll + _listVisibleRows)
            _listScroll = _selectedPart - _listVisibleRows + 1;

        for (int i = 0; i < _partRows.Length; i++)
        {
            _partRows[i].Position = new Vector2f(listX + 5f, contentY + 33f + i * _listRowHeight);
            _partRows[i].Size = new Vector2f(listWidth - 10f, _listRowHeight - 3f);
        }

        _addRegionButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 94f);
        _addRegionButton.Size = new Vector2f(listWidth - 10f, 25f);
        _addAllFormsButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 63f);
        _addAllFormsButton.Size = new Vector2f(listWidth - 10f, 25f);
        _removeRegionButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 32f);
        _removeRegionButton.Size = new Vector2f(listWidth - 10f, 25f);

        if (_addRegionLabel != null)
            _addRegionLabel.Position = new Vector2f(_addRegionButton.Position.X + 8f, _addRegionButton.Position.Y + 6f);
        if (_addAllFormsLabel != null)
            _addAllFormsLabel.Position = new Vector2f(_addAllFormsButton.Position.X + 8f, _addAllFormsButton.Position.Y + 6f);
        if (_removeRegionLabel != null)
            _removeRegionLabel.Position = new Vector2f(_removeRegionButton.Position.X + 8f, _removeRegionButton.Position.Y + 6f);

        float propertyTopOffset = MathF.Min(110f, MathF.Max(64f, contentHeight * 0.22f));
        _propertyRowHeight = Math.Clamp(
            (contentHeight - propertyTopOffset - 131f) / PropertyCount,
            16f,
            37f);
        if (_selectedName != null)
            _selectedName.Position = new Vector2f(propertiesX + 10f, contentY + 31f);
        if (_selectedInfo != null)
            _selectedInfo.Position = new Vector2f(propertiesX + 10f, contentY + 52f);

        float propertyTop = contentY + propertyTopOffset;
        for (int i = 0; i < PropertyCount; i++)
        {
            float rowY = propertyTop + i * _propertyRowHeight;
            _sliderTracks[i].Position = new Vector2f(propertiesX + 12f, rowY + 25f);
            _sliderTracks[i].Size = new Vector2f(propertiesWidth - 24f, 6f);
        }

        _primitiveButton.Position = new Vector2f(propertiesX + 10f, propertyTop + PropertyCount * _propertyRowHeight + 8f);
        _primitiveButton.Size = new Vector2f(propertiesWidth - 20f, 30f);
        _parentButton.Position = new Vector2f(propertiesX + 10f, _primitiveButton.Position.Y + 35f);
        _parentButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _allSymmetryButton.Position = new Vector2f(propertiesX + 10f, _parentButton.Position.Y + 31f);
        _allSymmetryButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _gridButton.Position = new Vector2f(propertiesX + 10f, _allSymmetryButton.Position.Y + 31f);
        _gridButton.Size = new Vector2f(54f, 26f);
        _gridStepButton.Position = new Vector2f(_gridButton.Position.X + _gridButton.Size.X + 4f, _gridButton.Position.Y);
        _gridStepButton.Size = new Vector2f(48f, 26f);
        _surfaceSnapButton.Position = new Vector2f(_gridStepButton.Position.X + _gridStepButton.Size.X + 4f, _gridButton.Position.Y);
        _surfaceSnapButton.Size = new Vector2f(MathF.Max(54f, propertiesX + propertiesWidth - 10f - _surfaceSnapButton.Position.X), 26f);

        if (_helpText != null)
            _helpText.Position = new Vector2f(listX + 2f, y + panelHeight - 42f);
        if (_statusText != null)
            _statusText.Position = new Vector2f(propertiesX + 10f, y + panelHeight - 42f);

        float previewY = contentY;
        float previewHeight = contentHeight;
        for (int i = 0; i < _previewPanels.Length; i++)
        {
            float previewX = previewsX + i * (previewWidth + gap);
            _previewPanels[i].Position = new Vector2f(previewX, previewY);
            _previewPanels[i].Size = new Vector2f(previewWidth, previewHeight);
            if (_projectionLabels[i] != null)
            {
                _projectionLabels[i]!.DisplayedString = panelWidth < 980f
                    ? i switch { 0 => "СПЕРЕДИ", 1 => "СБОКУ", _ => "СВЕРХУ" }
                    : i switch { 0 => "СПЕРЕДИ  ·  Y / Z", 1 => "СБОКУ  ·  X / Z", _ => "СВЕРХУ  ·  X / Y" };
                _projectionLabels[i]!.Position = new Vector2f(previewX + 8f, previewY + 8f);
            }
            _symmetryButtons[i].Size = new Vector2f(MathF.Min(88f, MathF.Max(54f, previewWidth - 10f)), 22f);
            _symmetryButtons[i].Position = new Vector2f(previewX + previewWidth - _symmetryButtons[i].Size.X - 4f, previewY + 4f);
            if (_symmetryLabels[i] != null)
                _symmetryLabels[i]!.Position = new Vector2f(_symmetryButtons[i].Position.X + 6f, previewY + 10f);

            _projectionLayouts[i] = new ProjectionLayout(
                previewX + 8f,
                previewY + 30f,
                MathF.Max(12f, previewWidth - 16f),
                MathF.Max(12f, previewHeight - 42f));
        }
    }

    private void DrawProjection(RenderWindow window, int projection)
    {
        RectangleShape panel = _previewPanels[projection];
        bool active = projection == _activeProjection;
        panel.OutlineColor = active
            ? new Color(119, 174, 226, 255)
            : new Color(58, 75, 94, 255);
        panel.OutlineThickness = active ? 2f : 1f;
        window.Draw(panel);
        if (_projectionLabels[projection] != null)
            DrawText(window, _projectionLabels[projection]);

        _symmetryButtons[projection].FillColor = _symmetryEnabled[projection]
            ? new Color(52, 99, 74, 255)
            : new Color(39, 51, 65, 255);
        window.Draw(_symmetryButtons[projection]);
        if (_symmetryLabels[projection] != null)
        {
            _symmetryLabels[projection]!.DisplayedString = _symmetryEnabled[projection] ? "СИМ: ВКЛ" : "СИМ: ВЫКЛ";
            _symmetryLabels[projection]!.Position = new Vector2f(
                _symmetryButtons[projection].Position.X + 6f,
                _symmetryButtons[projection].Position.Y + 6f);
            DrawText(window, _symmetryLabels[projection]);
        }

        ProjectionLayout layout = _projectionLayouts[projection];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < _parts.Length; i++)
        {
            ProjectedPart geometry = Project(_parts[i], _worldPositions[i], _worldOrientations[i], projection);
            _projected[projection][i] = geometry;
            minX = MathF.Min(minX, geometry.X - geometry.HalfWidth);
            minY = MathF.Min(minY, geometry.Y - geometry.HalfHeight);
            maxX = MathF.Max(maxX, geometry.X + geometry.HalfWidth);
            maxY = MathF.Max(maxY, geometry.Y + geometry.HalfHeight);
        }

        float spanX = MathF.Max(0.01f, maxX - minX);
        float spanY = MathF.Max(0.01f, maxY - minY);
        float pixelsPerUnit = MathF.Max(1f, MathF.Min(
            MathF.Max(1f, layout.Width - 12f) / spanX,
            MathF.Max(1f, layout.Height - 12f) / spanY));
        float worldCenterX = (minX + maxX) * 0.5f;
        float worldCenterY = (minY + maxY) * 0.5f;
        float screenCenterX = layout.Left + layout.Width * 0.5f;
        float screenCenterY = layout.Top + layout.Height * 0.5f;
        layout = layout.WithTransform(pixelsPerUnit, screenCenterX, screenCenterY, worldCenterX, worldCenterY);
        _projectionLayouts[projection] = layout;
        DrawGrid(window, layout, projection);

        for (int i = 0; i < _parts.Length; i++)
        {
            ProjectedPart geometry = _projected[projection][i];
            float sx = screenCenterX + (geometry.X - worldCenterX) * pixelsPerUnit;
            float sy = projection == 2
                ? screenCenterY + (geometry.Y - worldCenterY) * pixelsPerUnit
                : screenCenterY - (geometry.Y - worldCenterY) * pixelsPerUnit;
            float halfWidth = MathF.Max(1.5f, geometry.HalfWidth * pixelsPerUnit);
            float halfHeight = MathF.Max(1.5f, geometry.HalfHeight * pixelsPerUnit);
            bool selected = i == _selectedPart;
            Color fill = selected
                ? new Color(115, 151, 104, 255)
                : RoleColor(_parts[i].Role);
            Color outline = selected ? SelectedColor : new Color(20, 28, 36, 255);
            float outlineScale = MathF.Max(halfWidth, halfHeight);
            float thickness = selected
                ? MathF.Min(1.5f, 1.7f / MathF.Max(1f, outlineScale))
                : MathF.Min(0.6f, 0.7f / MathF.Max(1f, outlineScale));

            if (geometry.Primitive == BodyPrimitive.Circle)
            {
                _previewCircle.Position = new Vector2f(sx, sy);
                _previewCircle.Scale = new Vector2f(halfWidth, halfHeight);
                _previewCircle.Rotation = geometry.ScreenRotation;
                _previewCircle.FillColor = fill;
                _previewCircle.OutlineColor = outline;
                _previewCircle.OutlineThickness = thickness;
                window.Draw(_previewCircle);
            }
            else if (geometry.Primitive == BodyPrimitive.Triangle)
            {
                _previewTriangle.Position = new Vector2f(sx, sy);
                _previewTriangle.Scale = new Vector2f(halfWidth, halfHeight);
                _previewTriangle.Rotation = geometry.ScreenRotation;
                _previewTriangle.FillColor = fill;
                _previewTriangle.OutlineColor = outline;
                _previewTriangle.OutlineThickness = thickness;
                window.Draw(_previewTriangle);
            }
            else
            {
                _previewBox.Position = new Vector2f(sx, sy);
                _previewBox.Scale = new Vector2f(halfWidth, halfHeight);
                _previewBox.Rotation = geometry.ScreenRotation;
                _previewBox.FillColor = fill;
                _previewBox.OutlineColor = outline;
                _previewBox.OutlineThickness = thickness;
                window.Draw(_previewBox);
            }

            geometry.ScreenX = sx;
            geometry.ScreenY = sy;
            geometry.ScreenHalfWidth = halfWidth;
            geometry.ScreenHalfHeight = halfHeight;
            _projected[projection][i] = geometry;
        }
    }

    private void DrawProperty(RenderWindow window, int index)
    {
        PropertyInfo info = GetProperty(index);
        RectangleShape track = _sliderTracks[index];
        float ratio = Math.Clamp((info.Value - info.Minimum) / (info.Maximum - info.Minimum), 0f, 1f);
        _sliderFills[index].Position = track.Position;
        _sliderFills[index].Size = new Vector2f(MathF.Max(1f, track.Size.X * ratio), track.Size.Y);
        _sliderKnobs[index].Position = new Vector2f(
            track.Position.X + track.Size.X * ratio - 3.5f,
            track.Position.Y - 4f);

        window.Draw(track);
        window.Draw(_sliderFills[index]);
        window.Draw(_sliderKnobs[index]);

        float rowY = track.Position.Y - 23f;
        if (_sliderLabels[index] != null)
        {
            _sliderLabels[index]!.Position = new Vector2f(track.Position.X, rowY);
            DrawText(window, _sliderLabels[index]);
        }

        if (_sliderValues[index] != null)
        {
            _sliderValues[index]!.DisplayedString = info.Format;
            _sliderValues[index]!.Position = new Vector2f(track.Position.X + track.Size.X - 78f, rowY - 1f);
            DrawText(window, _sliderValues[index]);
        }
    }

    private void CalculateWorldTransforms()
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            BodyPartDefinition part = _parts[i];
            int parent = part.ParentIndex;
            NumericsVector3 origin = NumericsVector3.Zero;
            Quaternion positionOrientation = Quaternion.Identity;
            Quaternion baseOrientation = Quaternion.Identity;

            if (part.Anchor == BodyPartAnchor.Parent && parent >= 0)
            {
                origin = _worldPositions[parent];
                positionOrientation = _worldOrientations[parent];
                baseOrientation = _worldOrientations[parent];
            }
            else if (part.Anchor == BodyPartAnchor.UnitHead)
            {
                // Keep the head's root attachment unrotated in the three-view editor.
                positionOrientation = Quaternion.Identity;
                baseOrientation = Quaternion.Identity;
            }

            NumericsVector3 joint = origin +
                BodyPartTransformMath.TransformPosition(part.LocalPosition, positionOrientation);
            Quaternion worldOrientation = BodyPartTransformMath.Compose(
                baseOrientation, part.LocalEulerRotation);

            if (part.Pivot == BodyPartPivot.Start)
            {
                joint += BodyPartTransformMath.TransformPosition(
                    new NumericsVector3(MathF.Abs(part.Scale.X), 0f, 0f),
                    worldOrientation);
            }

            _worldPositions[i] = joint;
            _worldOrientations[i] = worldOrientation;
        }
    }

    // Input events can arrive several times between rendered frames. Keep transforms and
    // projected hit/snap geometry current so each drag step starts from the previous edit.
    private void RefreshProjectedGeometry()
    {
        CalculateWorldTransforms();
        for (int projection = 0; projection < _projected.Length; projection++)
        {
            ProjectionLayout layout = _projectionLayouts[projection];
            for (int i = 0; i < _parts.Length; i++)
            {
                ProjectedPart geometry = Project(
                    _parts[i], _worldPositions[i], _worldOrientations[i], projection);

                if (layout.PixelsPerUnit > 0f)
                {
                    geometry.ScreenX = layout.ScreenCenterX +
                        (geometry.X - layout.WorldCenterX) * layout.PixelsPerUnit;
                    geometry.ScreenY = projection == 2
                        ? layout.ScreenCenterY + (geometry.Y - layout.WorldCenterY) * layout.PixelsPerUnit
                        : layout.ScreenCenterY - (geometry.Y - layout.WorldCenterY) * layout.PixelsPerUnit;
                    geometry.ScreenHalfWidth = MathF.Max(1.5f, geometry.HalfWidth * layout.PixelsPerUnit);
                    geometry.ScreenHalfHeight = MathF.Max(1.5f, geometry.HalfHeight * layout.PixelsPerUnit);
                }

                _projected[projection][i] = geometry;
            }
        }
    }

    private void ApplyProjectionDrag(int projection, int deltaX, int deltaY)
    {
        if (projection < 0 || projection >= _projectionLayouts.Length)
            return;
        ProjectionLayout layout = _projectionLayouts[projection];
        if (layout.PixelsPerUnit <= 0f)
            return;

        float dx = deltaX / layout.PixelsPerUnit;
        float dy = (projection == 2 ? deltaY : -deltaY) / layout.PixelsPerUnit;
        NumericsVector3 worldDelta = projection switch
        {
            0 => new NumericsVector3(0f, dx, dy),
            1 => new NumericsVector3(dx, 0f, dy),
            _ => new NumericsVector3(dx, dy, 0f)
        };
        ApplyProjectionWorldDelta(projection, worldDelta);
    }

    private void NudgeSelectedPart(int projection, int horizontal, int vertical)
    {
        if (_selectedPart < 0 || _selectedPart >= _parts.Length)
            return;

        float step = _gridEnabled ? _gridSteps[_gridStepIndex] : 0.005f;
        NumericsVector3 worldDelta = projection switch
        {
            0 => new NumericsVector3(0f, horizontal * step, vertical * step),
            1 => new NumericsVector3(horizontal * step, 0f, vertical * step),
            _ => new NumericsVector3(horizontal * step, -vertical * step, 0f)
        };
        ApplyProjectionWorldDelta(projection, worldDelta);
    }

    private string ProjectionName(int projection) => projection switch
    {
        0 => "спереди (Y/Z)",
        1 => "сбоку (X/Z)",
        _ => "сверху (X/Y)"
    };

    private void ApplyProjectionWorldDelta(int projection, NumericsVector3 worldDelta)
    {
        if (_selectedPart < 0 || _selectedPart >= _parts.Length ||
            projection < 0 || projection >= _projectionLayouts.Length)
            return;

        RefreshProjectedGeometry();
        BodyPartDefinition part = _parts[_selectedPart];
        NumericsVector3 currentCenter = _worldPositions[_selectedPart];
        NumericsVector3 targetCenter = currentCenter + worldDelta;
        if (_gridEnabled)
        {
            switch (projection)
            {
                case 0:
                    targetCenter.Y = Snap(targetCenter.Y);
                    targetCenter.Z = Snap(targetCenter.Z);
                    break;
                case 1:
                    targetCenter.X = Snap(targetCenter.X);
                    targetCenter.Z = Snap(targetCenter.Z);
                    break;
                default:
                    targetCenter.X = Snap(targetCenter.X);
                    targetCenter.Y = Snap(targetCenter.Y);
                    break;
            }
        }

        worldDelta = targetCenter - currentCenter;
        Vector2f snappedCenter = default;
        Vector2f surfaceNormal = default;
        int surfaceTargetPart = -1;
        bool alignToSurface = _surfaceSnapEnabled &&
            TrySnapToSurface(projection, worldDelta, out snappedCenter, out surfaceNormal, out surfaceTargetPart);
        if (alignToSurface)
        {
            ProjectedPart selectedGeometry = _projected[projection][_selectedPart];
            float currentProjectedX = selectedGeometry.X;
            float currentProjectedY = projection == 2 ? selectedGeometry.Y : -selectedGeometry.Y;
            float projectedDeltaX = snappedCenter.X - currentProjectedX;
            float projectedDeltaY = snappedCenter.Y - currentProjectedY;
            worldDelta = projection switch
            {
                0 => new NumericsVector3(0f, projectedDeltaX, -projectedDeltaY),
                1 => new NumericsVector3(projectedDeltaX, 0f, -projectedDeltaY),
                _ => new NumericsVector3(projectedDeltaX, projectedDeltaY, 0f)
            };
        }

        Quaternion parentOrientation =
            part.Anchor == BodyPartAnchor.Parent && part.ParentIndex >= 0
                ? _worldOrientations[part.ParentIndex]
                : Quaternion.Identity;
        NumericsVector3 localDelta = NumericsVector3.Transform(
            worldDelta, Quaternion.Inverse(parentOrientation));

        _parts[_selectedPart] = Copy(
            part,
            localPosition: part.LocalPosition + localDelta);
        if (alignToSurface)
            AlignSelectedPartToSurfaceNormal(projection, surfaceNormal.X, surfaceNormal.Y);
        ApplyEnabledSymmetry(projection);
        RefreshProjectedGeometry();
        _status = $"Изменено: {PartName(_regionIds[_selectedPart])}. " +
            $"Привязка: {(_gridEnabled ? _gridSteps[_gridStepIndex].ToString("0.####") : "выкл")}. Ctrl+S — сохранить.";
    }

    private void ApplySizeSymmetry()
    {
        if (_selectedPart < 0 || _selectedPart >= _regionIds.Length)
            return;

        string? counterpartId = GetCounterpartId(_regionIds[_selectedPart]);
        if (counterpartId == null)
            return;

        int counterpartIndex = Array.IndexOf(_regionIds, counterpartId);
        if (counterpartIndex < 0 || counterpartIndex == _selectedPart)
            return;

        _parts[counterpartIndex] = Copy(_parts[counterpartIndex], scale: _parts[_selectedPart].Scale);
    }

    private bool TrySnapToSurface(
        int projection,
        NumericsVector3 worldDelta,
        out Vector2f snappedCenter,
        out Vector2f surfaceNormal,
        out int targetPart)
    {
        snappedCenter = default;
        surfaceNormal = default;
        targetPart = -1;
        if (_selectedPart < 0 || _selectedPart >= _parts.Length ||
            projection < 0 || projection >= _projected.Length ||
            _projected[projection].Length != _parts.Length)
            return false;

        ProjectedPart selected = _projected[projection][_selectedPart];
        float currentX = selected.X;
        float currentY = projection == 2 ? selected.Y : -selected.Y;
        float deltaX = projection == 0 ? worldDelta.Y : worldDelta.X;
        float deltaY = projection == 2 ? worldDelta.Y : -worldDelta.Z;
        float candidateX = currentX + deltaX;
        float candidateY = currentY + deltaY;
        float snapDistance = Math.Clamp(MathF.Max(selected.HalfWidth, selected.HalfHeight) * 0.3f, 0.015f, 0.04f);

        // Preserve one target and a smoothed radial direction during a drag. Near a part's
        // center the radial direction is undefined, so keep the previous direction instead
        // of letting tiny mouse movements flip the calculated normal.
        if (_draggingPart && _draggingProjection == projection &&
            _surfaceSnapProjection == projection &&
            _surfaceSnapTargetPart >= 0 &&
            _surfaceSnapTargetPart < _parts.Length &&
            _surfaceSnapTargetPart != _selectedPart)
        {
            int lockedTarget = _surfaceSnapTargetPart;
            Vector2f lockedDirection = GetSnapDirection(
                projection, candidateX, candidateY, lockedTarget, smooth: true, useCachedDirection: true);
            if (TryGetSnapCandidate(projection, candidateX, candidateY, lockedTarget,
                    lockedDirection.X, lockedDirection.Y,
                    out Vector2f lockedCenter, out Vector2f lockedNormal, out float lockedError) &&
                lockedError <= snapDistance * 2.5f)
            {
                snappedCenter = lockedCenter;
                surfaceNormal = lockedNormal;
                targetPart = lockedTarget;
                _surfaceSnapDirectionX = lockedDirection.X;
                _surfaceSnapDirectionY = lockedDirection.Y;
                _hasSurfaceSnapDirection = true;
                return true;
            }
        }

        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        float bestDistance = snapDistance;
        Vector2f bestDirection = default;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (i == _selectedPart)
                continue;

            Vector2f direction = GetSnapDirection(
                projection, candidateX, candidateY, i, smooth: false, useCachedDirection: false);
            if (!TryGetSnapCandidate(projection, candidateX, candidateY, i,
                    direction.X, direction.Y,
                    out Vector2f desiredCenter, out Vector2f normal, out float error) ||
                error >= bestDistance)
                continue;

            bestDistance = error;
            snappedCenter = desiredCenter;
            surfaceNormal = normal;
            targetPart = i;
            bestDirection = direction;
        }

        if (targetPart < 0)
            return false;

        if (_draggingPart && _draggingProjection == projection)
        {
            _surfaceSnapTargetPart = targetPart;
            _surfaceSnapProjection = projection;
            _surfaceSnapDirectionX = bestDirection.X;
            _surfaceSnapDirectionY = bestDirection.Y;
            _hasSurfaceSnapDirection = true;
        }

        return true;
    }

    private Vector2f GetSnapDirection(
        int projection,
        float candidateX,
        float candidateY,
        int otherIndex,
        bool smooth,
        bool useCachedDirection)
    {
        ProjectedPart selected = _projected[projection][_selectedPart];
        ProjectedPart other = _projected[projection][otherIndex];
        float otherX = other.X;
        float otherY = projection == 2 ? other.Y : -other.Y;
        float directionX = candidateX - otherX;
        float directionY = candidateY - otherY;
        float distance = MathF.Sqrt(directionX * directionX + directionY * directionY);
        float centerDeadZone = Math.Clamp(
            MathF.Max(other.HalfWidth, other.HalfHeight) * 0.28f, 0.025f, 0.075f);

        if (distance < centerDeadZone)
        {
            if (useCachedDirection && _hasSurfaceSnapDirection)
                return new Vector2f(_surfaceSnapDirectionX, _surfaceSnapDirectionY);

            // At a coincident center there is no geometrically unique normal. Choose a
            // deterministic direction from the part's current axis rather than mouse noise.
            float angle = selected.ScreenRotation * MathF.PI / 180f;
            return new Vector2f(MathF.Cos(angle), MathF.Sin(angle));
        }

        directionX /= distance;
        directionY /= distance;
        if (smooth && useCachedDirection && _hasSurfaceSnapDirection)
        {
            const float response = 0.24f;
            float blendedX = _surfaceSnapDirectionX * (1f - response) + directionX * response;
            float blendedY = _surfaceSnapDirectionY * (1f - response) + directionY * response;
            float blendedLength = MathF.Sqrt(blendedX * blendedX + blendedY * blendedY);
            float dot = _surfaceSnapDirectionX * directionX + _surfaceSnapDirectionY * directionY;
            if (blendedLength > 0.0001f && dot > -0.75f)
            {
                directionX = blendedX / blendedLength;
                directionY = blendedY / blendedLength;
            }
        }

        return new Vector2f(directionX, directionY);
    }

    private bool TryGetSnapCandidate(
        int projection,
        float candidateX,
        float candidateY,
        int otherIndex,
        float directionX,
        float directionY,
        out Vector2f desiredCenter,
        out Vector2f surfaceNormal,
        out float error)
    {
        desiredCenter = default;
        surfaceNormal = default;
        error = float.PositiveInfinity;
        if (otherIndex < 0 || otherIndex >= _parts.Length || otherIndex == _selectedPart)
            return false;

        ProjectedPart selected = _projected[projection][_selectedPart];
        ProjectedPart other = _projected[projection][otherIndex];
        if (!TryGetSurfaceNormal(other, directionX, directionY,
                out float radius, out float normalX, out float normalY))
            return false;

        float support = GetProjectedSupportRadius(selected, normalX, normalY);
        float otherX = other.X;
        float otherY = projection == 2 ? other.Y : -other.Y;
        float desiredX = otherX + directionX * radius + normalX * (support + 0.0015f);
        float desiredY = otherY + directionY * radius + normalY * (support + 0.0015f);
        float errorX = candidateX - desiredX;
        float errorY = candidateY - desiredY;
        error = MathF.Sqrt(errorX * errorX + errorY * errorY);
        if (!float.IsFinite(error))
            return false;

        desiredCenter = new Vector2f(desiredX, desiredY);
        surfaceNormal = new Vector2f(normalX, normalY);
        return true;
    }

    private static bool TryGetSurfaceNormal(
        ProjectedPart shape,
        float directionX,
        float directionY,
        out float radius,
        out float normalX,
        out float normalY)
    {
        radius = 0f;
        normalX = 0f;
        normalY = 0f;

        float halfWidth = MathF.Max(0.001f, shape.HalfWidth);
        float halfHeight = MathF.Max(0.001f, shape.HalfHeight);
        float angle = shape.ScreenRotation * MathF.PI / 180f;
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        float localX = directionX * cos + directionY * sin;
        float localY = -directionX * sin + directionY * cos;
        float nx;
        float ny;

        if (shape.Primitive == BodyPrimitive.Box)
        {
            float tx = MathF.Abs(localX) > 0.00001f ? halfWidth / MathF.Abs(localX) : float.PositiveInfinity;
            float ty = MathF.Abs(localY) > 0.00001f ? halfHeight / MathF.Abs(localY) : float.PositiveInfinity;
            radius = MathF.Min(tx, ty);
            if (tx <= ty)
            {
                nx = MathF.Sign(localX);
                ny = 0f;
            }
            else
            {
                nx = 0f;
                ny = MathF.Sign(localY);
            }
        }
        else if (shape.Primitive == BodyPrimitive.Triangle)
        {
            Span<Vector2f> vertices = stackalloc Vector2f[3]
            {
                new Vector2f(0f, -halfHeight),
                new Vector2f(halfWidth, halfHeight),
                new Vector2f(-halfWidth, halfHeight)
            };
            radius = float.PositiveInfinity;
            nx = 0f;
            ny = 0f;
            for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            {
                Vector2f a = vertices[edgeIndex];
                Vector2f b = vertices[(edgeIndex + 1) % 3];
                float edgeX = b.X - a.X;
                float edgeY = b.Y - a.Y;
                float denominator = directionX * edgeY - directionY * edgeX;
                if (MathF.Abs(denominator) < 0.00001f)
                    continue;

                float t = (a.X * edgeY - a.Y * edgeX) / denominator;
                float u = (a.X * directionY - a.Y * directionX) / denominator;
                if (t <= 0f || u < 0f || u > 1f || t >= radius)
                    continue;

                radius = t;
                nx = edgeY;
                ny = -edgeX;
                if (nx * directionX + ny * directionY < 0f)
                {
                    nx = -nx;
                    ny = -ny;
                }
            }

            float normalLength = MathF.Sqrt(nx * nx + ny * ny);
            if (!float.IsFinite(radius) || normalLength < 0.00001f)
                return false;
            nx /= normalLength;
            ny /= normalLength;
        }
        else
        {
            float denominator = (localX * localX) / (halfWidth * halfWidth) +
                (localY * localY) / (halfHeight * halfHeight);
            if (denominator < 0.000001f)
            {
                radius = 0f;
                normalX = 0f;
                normalY = 0f;
                return false;
            }
            radius = 1f / MathF.Sqrt(denominator);
            nx = (localX * radius) / (halfWidth * halfWidth);
            ny = (localY * radius) / (halfHeight * halfHeight);
            float normalLength = MathF.Sqrt(nx * nx + ny * ny);
            nx /= MathF.Max(0.000001f, normalLength);
            ny /= MathF.Max(0.000001f, normalLength);
        }

        normalX = nx * cos - ny * sin;
        normalY = nx * sin + ny * cos;
        return float.IsFinite(radius) && float.IsFinite(normalX) && float.IsFinite(normalY);
    }

    private static float GetProjectedSupportRadius(ProjectedPart shape, float normalX, float normalY)
    {
        float angle = shape.ScreenRotation * MathF.PI / 180f;
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        float localX = normalX * cos + normalY * sin;
        float localY = -normalX * sin + normalY * cos;
        float halfWidth = MathF.Max(0.001f, shape.HalfWidth);
        float halfHeight = MathF.Max(0.001f, shape.HalfHeight);
        if (shape.Primitive == BodyPrimitive.Box)
            return halfWidth * MathF.Abs(localX) + halfHeight * MathF.Abs(localY);
        if (shape.Primitive == BodyPrimitive.Triangle)
            return MathF.Max(-halfHeight * localY,
                MathF.Max(halfWidth * localX + halfHeight * localY,
                    -halfWidth * localX + halfHeight * localY));
        return MathF.Sqrt(halfWidth * halfWidth * localX * localX + halfHeight * halfHeight * localY * localY);
    }

    private void AlignSelectedPartToSurfaceNormal(int projection, float normalX, float normalY)
    {
        if (_selectedPart < 0 || _selectedPart >= _parts.Length)
            return;

        ProjectedPart projected = _projected[projection][_selectedPart];
        float targetAngle = MathF.Atan2(normalY, normalX);
        float currentAngle = projected.ScreenRotation * MathF.PI / 180f;
        float delta = WrapAngle(targetAngle - currentAngle);
        if (MathF.Abs(delta) < 0.0001f)
            return;

        NumericsVector3 axis = projection switch
        {
            0 => NumericsVector3.UnitX,
            1 => NumericsVector3.UnitY,
            _ => NumericsVector3.UnitZ
        };
        float axisSign = projection == 0 ? -1f : 1f;
        Quaternion alignRotation = Quaternion.CreateFromAxisAngle(axis, delta * axisSign);
        Quaternion worldOrientation = Quaternion.Normalize(alignRotation * _worldOrientations[_selectedPart]);
        BodyPartDefinition part = _parts[_selectedPart];
        Quaternion parentOrientation = part.Anchor == BodyPartAnchor.Parent && part.ParentIndex >= 0
            ? _worldOrientations[part.ParentIndex]
            : Quaternion.Identity;
        Quaternion localOrientation = Quaternion.Normalize(Quaternion.Inverse(parentOrientation) * worldOrientation);
        NumericsVector3 euler = ToEulerRadians(localOrientation);
        _parts[_selectedPart] = Copy(part,
            localRotation: euler.Z,
            localRotationX: euler.X,
            localRotationY: euler.Y);
    }

    private static NumericsVector3 ToEulerRadians(Quaternion q)
    {
        float sinX = 2f * (q.W * q.X + q.Y * q.Z);
        float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float x = MathF.Atan2(sinX, cosX);
        float sinY = 2f * (q.W * q.Y - q.Z * q.X);
        float y = MathF.Abs(sinY) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinY) : MathF.Asin(sinY);
        float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
        float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        return new NumericsVector3(x, y, MathF.Atan2(sinZ, cosZ));
    }

    private static float WrapAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private int HitTestPreview(int projection, int x, int y)
    {
        ProjectedPart[] geometry = _projected[projection];
        for (int i = geometry.Length - 1; i >= 0; i--)
            if (ContainsProjectedPart(geometry[i], x, y))
                return i;
        return -1;
    }

    private static bool ContainsProjectedPart(ProjectedPart part, float x, float y)
    {
        float halfWidth = part.ScreenHalfWidth;
        float halfHeight = part.ScreenHalfHeight;
        if (halfWidth <= 0f || halfHeight <= 0f)
            return false;

        float radians = part.ScreenRotation * MathF.PI / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        float dx = x - part.ScreenX;
        float dy = y - part.ScreenY;
        // Test against the actual rotated primitive, not its loose axis-aligned box.
        float localX = dx * cos + dy * sin;
        float localY = -dx * sin + dy * cos;
        const float tolerance = 3f;

        if (part.Primitive == BodyPrimitive.Circle)
        {
            float nx = localX / (halfWidth + tolerance);
            float ny = localY / (halfHeight + tolerance);
            return nx * nx + ny * ny <= 1f;
        }

        if (part.Primitive == BodyPrimitive.Box)
            return MathF.Abs(localX) <= halfWidth + tolerance &&
                   MathF.Abs(localY) <= halfHeight + tolerance;

        float normalizedY = localY / halfHeight;
        if (normalizedY < -1f - tolerance / halfHeight ||
            normalizedY > 1f + tolerance / halfHeight)
            return false;
        float triangleHalfWidth = Math.Clamp((normalizedY + 1f) * 0.5f, 0f, 1f) * halfWidth;
        return MathF.Abs(localX) <= triangleHalfWidth + tolerance;
    }

    private void UpdatePropertyFromSlider(int propertyIndex, int mouseX)
    {
        RectangleShape track = _sliderTracks[propertyIndex];
        float ratio = Math.Clamp(
            (mouseX - track.Position.X) / MathF.Max(1f, track.Size.X),
            0f,
            1f);
        PropertyInfo info = GetProperty(propertyIndex);
        SetProperty(propertyIndex, info.Minimum + ratio * (info.Maximum - info.Minimum));
    }

    private void AdjustProperty(int propertyIndex, float direction)
    {
        PropertyInfo info = GetProperty(propertyIndex);
        float step = propertyIndex switch
        {
            0 or 1 or 2 => 0.01f,
            3 or 4 or 5 => 0.005f,
            _ => 2f
        };
        SetProperty(propertyIndex, info.Value + direction * step);
    }

    private PropertyInfo GetProperty(int index)
    {
        BodyPartDefinition part = _parts[_selectedPart];
        return index switch
        {
            0 => new PropertyInfo(-0.5f, 0.5f, part.LocalPosition.X, $"{part.LocalPosition.X:+0.000;-0.000;0.000}"),
            1 => new PropertyInfo(-0.5f, 0.5f, part.LocalPosition.Y, $"{part.LocalPosition.Y:+0.000;-0.000;0.000}"),
            2 => part.ParentIndex < 0
                ? new PropertyInfo(0f, 2f, part.LocalPosition.Z, $"{part.LocalPosition.Z:0.000}")
                : new PropertyInfo(-0.8f, 0.8f, part.LocalPosition.Z, $"{part.LocalPosition.Z:+0.000;-0.000;0.000}"),
            3 => new PropertyInfo(0.005f, 0.25f, part.Scale.X, $"{part.Scale.X:0.000}"),
            4 => new PropertyInfo(0.005f, 0.20f, part.Scale.Y, $"{part.Scale.Y:0.000}"),
            5 => new PropertyInfo(0.005f, 0.40f, part.Scale.Z, $"{part.Scale.Z:0.000}"),
            6 => new PropertyInfo(-180f, 180f, part.LocalRotationX * 180f / MathF.PI, $"{part.LocalRotationX * 180f / MathF.PI:+0.0;-0.0;0.0}°"),
            7 => new PropertyInfo(-180f, 180f, part.LocalRotationY * 180f / MathF.PI, $"{part.LocalRotationY * 180f / MathF.PI:+0.0;-0.0;0.0}°"),
            _ => new PropertyInfo(-180f, 180f, part.LocalRotation * 180f / MathF.PI, $"{part.LocalRotation * 180f / MathF.PI:+0.0;-0.0;0.0}°")
        };
    }

    private void SetProperty(int index, float value)
    {
        BodyPartDefinition part = _parts[_selectedPart];
        NumericsVector3 position = part.LocalPosition;
        NumericsVector3 scale = part.Scale;
        float rotationX = part.LocalRotationX;
        float rotationY = part.LocalRotationY;
        float rotation = part.LocalRotation;

        switch (index)
        {
            case 0:
                position.X = Math.Clamp(value, -0.5f, 0.5f);
                break;
            case 1:
                position.Y = Math.Clamp(value, -0.5f, 0.5f);
                break;
            case 2:
                position.Z = part.ParentIndex < 0
                    ? Math.Clamp(value, 0f, 2f)
                    : Math.Clamp(value, -0.8f, 0.8f);
                break;
            case 3:
                scale.X = Math.Clamp(value, 0.005f, 0.25f);
                break;
            case 4:
                scale.Y = Math.Clamp(value, 0.005f, 0.20f);
                break;
            case 5:
                scale.Z = Math.Clamp(value, 0.005f, 0.40f);
                break;
            case 6:
                rotationX = Math.Clamp(value, -180f, 180f) * MathF.PI / 180f;
                break;
            case 7:
                rotationY = Math.Clamp(value, -180f, 180f) * MathF.PI / 180f;
                break;
            default:
                rotation = Math.Clamp(value, -180f, 180f) * MathF.PI / 180f;
                break;
        }

        _parts[_selectedPart] = Copy(part, position, scale, rotation,
            localRotationX: rotationX, localRotationY: rotationY);
        if (index is >= 3 and <= 5)
            ApplySizeSymmetry();
        ApplyEnabledSymmetry();
        _status = $"Изменено: {PartName(_regionIds[_selectedPart])}. Нажми Ctrl+S для сохранения.";
    }

    private void Save()
    {
        _openedRegionIds = (string[])_regionIds.Clone();
        _openedParts = (BodyPartDefinition[])_parts.Clone();
        _saveRequested(
            (string[])_regionIds.Clone(),
            (BodyPartDefinition[])_parts.Clone());
    }

    private void AddRegion()
    {
        if (_parts.Length >= MaxParts)
        {
            _status = $"Достигнут предел в {MaxParts} регионов.";
            return;
        }

        int parent = _parts.Length == 0 ? -1 : Math.Clamp(_selectedPart, 0, _parts.Length - 1);
        string id = CreateUniqueRegionId("custom.segment");
        AppendRegions(
            new[] { id },
            new[]
            {
                new BodyPartDefinition(
                    new NumericsVector3(0f, 0f, -0.065f),
                    new NumericsVector3(0.035f, 0.022f, 0.06f),
                    BodyPrimitive.Circle,
                    parentIndex: parent,
                    anchor: parent >= 0 ? BodyPartAnchor.Parent : BodyPartAnchor.Parent,
                    role: BodyPartRole.Generic,
                    pivot: BodyPartPivot.Center)
            });
    }

    private void AddAllPrimitiveExamples()
    {
        if (_parts.Length + 3 > MaxParts)
        {
            _status = $"Для трёх примеров недостаточно места. Предел — {MaxParts} регионов.";
            return;
        }

        int parent = Math.Clamp(_selectedPart, 0, _parts.Length - 1);
        AppendRegions(
            new[]
            {
                CreateUniqueRegionId("custom.circle"),
                CreateUniqueRegionId("custom.box"),
                CreateUniqueRegionId("custom.triangle")
            },
            new[]
            {
                new BodyPartDefinition(new NumericsVector3(0f, 0.045f, -0.07f),
                    new NumericsVector3(0.032f, 0.022f, 0.055f), BodyPrimitive.Circle,
                    parentIndex: parent, anchor: BodyPartAnchor.Parent, role: BodyPartRole.Generic),
                new BodyPartDefinition(new NumericsVector3(0f, 0f, -0.07f),
                    new NumericsVector3(0.032f, 0.022f, 0.055f), BodyPrimitive.Box,
                    parentIndex: parent, anchor: BodyPartAnchor.Parent, role: BodyPartRole.Generic),
                new BodyPartDefinition(new NumericsVector3(0f, -0.045f, -0.07f),
                    new NumericsVector3(0.032f, 0.022f, 0.055f), BodyPrimitive.Triangle,
                    parentIndex: parent, anchor: BodyPartAnchor.Parent, role: BodyPartRole.Generic)
            });
    }

    private string CreateUniqueRegionId(string prefix)
    {
        for (int i = 1; i < 10000; i++)
        {
            string candidate = $"{prefix}.{i:000}";
            bool found = false;
            for (int j = 0; j < _regionIds.Length; j++)
                found |= string.Equals(_regionIds[j], candidate, StringComparison.Ordinal);
            if (!found)
                return candidate;
        }

        return $"{prefix}.{Guid.NewGuid():N}";
    }

    private void AppendRegions(string[] ids, BodyPartDefinition[] definitions)
    {
        var nextIds = new string[_regionIds.Length + ids.Length];
        var nextParts = new BodyPartDefinition[_parts.Length + definitions.Length];
        Array.Copy(_regionIds, nextIds, _regionIds.Length);
        Array.Copy(_parts, nextParts, _parts.Length);
        Array.Copy(ids, 0, nextIds, _regionIds.Length, ids.Length);
        Array.Copy(definitions, 0, nextParts, _parts.Length, definitions.Length);

        if (!SetRigData(nextIds, nextParts, setDefaults: false, setOpenedSnapshot: false))
            return;

        _selectedPart = _parts.Length - 1;
        EnsureSelectionVisible();
        _status = $"Добавлено регионов: {ids.Length}. Задай родителя и нажми «Сохранить».";
    }

    private void RemoveSelectedCustomRegion()
    {
        if (_selectedPart < 0 || _selectedPart >= _parts.Length ||
            !_regionIds[_selectedPart].StartsWith("custom.", StringComparison.Ordinal))
        {
            _status = "Удалять можно пользовательские регионы custom.*. Базовые части и органы защищены.";
            return;
        }

        int removed = _selectedPart;
        int fallbackParent = _parts[removed].ParentIndex;
        var nextIds = new string[_regionIds.Length - 1];
        var nextParts = new BodyPartDefinition[_parts.Length - 1];
        int write = 0;

        for (int i = 0; i < _parts.Length; i++)
        {
            if (i == removed)
                continue;

            BodyPartDefinition part = _parts[i];
            int parent = part.ParentIndex;
            if (parent == removed)
                parent = fallbackParent;
            if (parent > removed)
                parent--;

            if (parent >= write)
            {
                _status = $"Нельзя удалить регион: перепроверь зависимость «{PartName(_regionIds[i])}».";
                return;
            }

            nextIds[write] = _regionIds[i];
            nextParts[write] = Copy(
                part,
                parentIndex: parent,
                anchor: parent >= 0 ? BodyPartAnchor.Parent : part.Anchor);
            write++;
        }

        if (!SetRigData(nextIds, nextParts, setDefaults: false, setOpenedSnapshot: false))
            return;

        _selectedPart = Math.Clamp(removed, 0, _parts.Length - 1);
        EnsureSelectionVisible();
        _status = $"Удалён регион «{PartName(_regionIds[_selectedPart])}». Связанные дочерние части переподключены к его родителю.";
    }

    private void CycleParentDependency()
    {
        if (_selectedPart <= 0)
        {
            _status = "Первый регион не может иметь родителя: он является корнем рига.";
            return;
        }

        BodyPartDefinition part = _parts[_selectedPart];
        int nextParent = part.ParentIndex + 1;
        if (nextParent >= _selectedPart)
            nextParent = -1;

        BodyPartAnchor anchor = nextParent >= 0
            ? BodyPartAnchor.Parent
            : string.Equals(_regionIds[_selectedPart], "body.upper", StringComparison.Ordinal)
                ? BodyPartAnchor.UnitHead
                : BodyPartAnchor.Parent;

        _parts[_selectedPart] = Copy(part, parentIndex: nextParent, anchor: anchor);
        _status = nextParent >= 0
            ? $"«{PartName(_regionIds[_selectedPart])}» теперь зависит от «{PartName(_regionIds[nextParent])}». Нажми Ctrl+S."
            : $"«{PartName(_regionIds[_selectedPart])}» теперь привязана к корню юнита. Нажми Ctrl+S.";
    }

    private void EnsureSelectionVisible()
    {
        if (_selectedPart < _listScroll)
            _listScroll = _selectedPart;
        else if (_selectedPart >= _listScroll + _listVisibleRows)
            _listScroll = Math.Max(0, _selectedPart - _listVisibleRows + 1);
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _parts.Length - _listVisibleRows));
    }

    private void ApplyEnabledSymmetry(int activeProjection = -1)
    {
        if (_selectedPart < 0 || _selectedPart >= _regionIds.Length)
            return;

        bool symmetryActive = activeProjection >= 0
            ? _symmetryEnabled[activeProjection]
            : Array.Exists(_symmetryEnabled, enabled => enabled);
        if (!symmetryActive)
            return;

        string? counterpartId = GetCounterpartId(_regionIds[_selectedPart]);
        if (counterpartId == null)
            return;

        int counterpartIndex = Array.IndexOf(_regionIds, counterpartId);
        if (counterpartIndex < 0 || counterpartIndex == _selectedPart)
            return;

        BodyPartDefinition source = _parts[_selectedPart];
        BodyPartDefinition target = _parts[counterpartIndex];
        // Reflect the complete 3D transform across the unit's left/right (Y=0) plane.
        // This works regardless of which projection initiated the edit.
        NumericsVector3 mirroredPosition = new(
            source.LocalPosition.X,
            -source.LocalPosition.Y,
            source.LocalPosition.Z);
        _parts[counterpartIndex] = Copy(
            target,
            localPosition: mirroredPosition,
            scale: source.Scale,
            localRotation: -source.LocalRotation,
            localRotationX: -source.LocalRotationX,
            localRotationY: source.LocalRotationY,
            primitive: source.Primitive);
    }

    private static string? GetCounterpartId(string id)
    {
        string[] pairedIds =
        {
            "body.arm.left.upper", "body.arm.right.upper",
            "body.arm.left.lower", "body.arm.right.lower",
            "body.leg.left.upper", "body.leg.right.upper",
            "body.leg.left.lower", "body.leg.right.lower"
        };
        for (int i = 0; i < pairedIds.Length; i += 2)
        {
            if (id == pairedIds[i]) return pairedIds[i + 1];
            if (id == pairedIds[i + 1]) return pairedIds[i];
        }

        if (id.Contains(".left.", StringComparison.Ordinal))
            return id.Replace(".left.", ".right.", StringComparison.Ordinal);
        if (id.Contains(".right.", StringComparison.Ordinal))
            return id.Replace(".right.", ".left.", StringComparison.Ordinal);
        if (id.EndsWith(".left", StringComparison.Ordinal))
            return id[..^5] + ".right";
        if (id.EndsWith(".right", StringComparison.Ordinal))
            return id[..^6] + ".left";

        return null;
    }

    private void LoadRigFromDialog()
    {
        string? path = PickRigFile(save: false);
        if (string.IsNullOrWhiteSpace(path))
        {
            _status = OperatingSystem.IsWindows()
                ? "Загрузка отменена."
                : "Диалог выбора файлов доступен в Windows.";
            return;
        }

        if (!UnitBodyRigSettingsStore.LoadFile(path, out string[] ids, out BodyPartDefinition[] parts, out string error))
        {
            _status = $"Ошибка загрузки рига: {error}";
            return;
        }

        if (!SetRigData(ids, parts, setDefaults: true, setOpenedSnapshot: true))
            return;

        _status = $"Загружен файл «{Path.GetFileName(path)}». Применение к игре — кнопкой «Сохранить».";
        Save();
    }

    private void ExportRigFromDialog()
    {
        string? path = PickRigFile(save: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            _status = OperatingSystem.IsWindows() ? "Экспорт отменён." : "Диалог выбора файлов доступен в Windows.";
            return;
        }

        if (UnitBodyRigSettingsStore.ExportFile(path, _regionIds, _parts, out string error))
            _status = $"Риг экспортирован: {Path.GetFileName(path)}";
        else
            _status = $"Ошибка экспорта: {error}";
    }

    private static string? PickRigFile(bool save)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        const int maxFile = 1024;
        IntPtr filter = IntPtr.Zero;
        IntPtr fileBuffer = IntPtr.Zero;
        IntPtr initialDirectory = IntPtr.Zero;
        IntPtr title = IntPtr.Zero;
        IntPtr extension = IntPtr.Zero;

        try
        {
            // OPENFILENAME contains native pointers, not managed StringBuilder fields.
            // StringBuilder as a field makes Marshal.SizeOf<OpenFileName>() fail.
            filter = Marshal.StringToHGlobalUni(
                "RimClone rig (*.json)\0*.json\0Все файлы (*.*)\0*.*\0\0");
            fileBuffer = Marshal.AllocHGlobal(maxFile * sizeof(char));
            Marshal.Copy(new char[maxFile], 0, fileBuffer, maxFile);

            string? initialDirectoryPath = Path.GetDirectoryName(UnitBodyRigSettingsStore.FilePath);
            if (!string.IsNullOrWhiteSpace(initialDirectoryPath))
                initialDirectory = Marshal.StringToHGlobalUni(initialDirectoryPath);

            title = Marshal.StringToHGlobalUni(save ? "Сохранить риг как..." : "Загрузить риг");
            extension = Marshal.StringToHGlobalUni("json");

            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = IntPtr.Zero,
                hInstance = IntPtr.Zero,
                lpstrFilter = filter,
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter = 0,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = maxFile,
                lpstrFileTitle = IntPtr.Zero,
                nMaxFileTitle = 0,
                lpstrInitialDir = initialDirectory,
                lpstrTitle = title,
                Flags = 0x00000800 | 0x00080000 | (save ? 0x00000002 : 0x00001000),
                nFileOffset = 0,
                nFileExtension = 0,
                lpstrDefExt = extension,
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpTemplateName = IntPtr.Zero,
                pvReserved = IntPtr.Zero,
                dwReserved = 0,
                FlagsEx = 0
            };

            bool accepted = save ? GetSaveFileNameW(ref dialog) : GetOpenFileNameW(ref dialog);
            if (!accepted)
                return null;

            string? selectedPath = Marshal.PtrToStringUni(fileBuffer);
            return string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
        }
        finally
        {
            if (filter != IntPtr.Zero) Marshal.FreeHGlobal(filter);
            if (fileBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileBuffer);
            if (initialDirectory != IntPtr.Zero) Marshal.FreeHGlobal(initialDirectory);
            if (title != IntPtr.Zero) Marshal.FreeHGlobal(title);
            if (extension != IntPtr.Zero) Marshal.FreeHGlobal(extension);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);

    private static BodyPartDefinition Copy(
        BodyPartDefinition original,
        NumericsVector3? localPosition = null,
        NumericsVector3? scale = null,
        float? localRotation = null,
        BodyPrimitive? primitive = null,
        int? parentIndex = null,
        BodyPartAnchor? anchor = null,
        float? localRotationX = null,
        float? localRotationY = null) =>
        new(
            localPosition ?? original.LocalPosition,
            scale ?? original.Scale,
            primitive ?? original.Primitive,
            parentIndex: parentIndex ?? original.ParentIndex,
            localRotation: localRotation ?? original.LocalRotation,
            motion: original.Motion,
            followDistance: original.FollowDistance,
            anchor: anchor ?? original.Anchor,
            role: original.Role,
            pivot: original.Pivot,
            localRotationX: localRotationX ?? original.LocalRotationX,
            localRotationY: localRotationY ?? original.LocalRotationY);

    private static ProjectedPart Project(
        BodyPartDefinition part,
        NumericsVector3 position,
        Quaternion orientation,
        int projection)
    {
        int horizontalAxis = projection switch { 0 => 1, 1 => 0, _ => 0 };
        int verticalAxis = projection switch { 0 => 2, 1 => 2, _ => 1 };
        BodyPartTransformMath.ProjectedShape shape =
            BodyPartTransformMath.Project(part.Scale, orientation, horizontalAxis, verticalAxis);

        float x = Component(position, horizontalAxis);
        float y = Component(position, verticalAxis);
        // Top view follows the game's XY screen convention (positive Y points down).
        // Front/side views show positive world Z upwards, so their in-plane angle is inverted.
        float screenRotation = shape.Rotation * 180f / MathF.PI;
        if (projection != 2)
            screenRotation = -screenRotation;

        return new ProjectedPart(
            x,
            y,
            MathF.Max(0.001f, shape.Scale.X),
            MathF.Max(0.001f, shape.Scale.Y),
            screenRotation,
            part.Primitive);
    }

    private static float Component(NumericsVector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };

    private float Snap(float value) =>
        _gridEnabled ? MathF.Round(value / _gridSteps[_gridStepIndex]) * _gridSteps[_gridStepIndex] : value;

    private void DrawGrid(RenderWindow window, ProjectionLayout layout, int projection)
    {
        if (!_gridEnabled || layout.PixelsPerUnit <= 0f)
            return;

        float step = _gridSteps[_gridStepIndex];
        // Keep the preview readable at far zoom: render every Nth line while snapping
        // still uses the exact configured step.
        float minimumScreenSpacing = 4f;
        int stride = Math.Max(1, (int)MathF.Ceiling(minimumScreenSpacing / (step * layout.PixelsPerUnit)));
        float spacing = step * stride;
        float leftWorld = layout.WorldCenterX - layout.Width / (2f * layout.PixelsPerUnit);
        float rightWorld = layout.WorldCenterX + layout.Width / (2f * layout.PixelsPerUnit);
        float lowVertical = layout.WorldCenterY - layout.Height / (2f * layout.PixelsPerUnit);
        float highVertical = layout.WorldCenterY + layout.Height / (2f * layout.PixelsPerUnit);

        RectangleShape verticalLine = _gridLines[0];
        for (float worldX = MathF.Floor(leftWorld / spacing) * spacing;
             worldX <= rightWorld;
             worldX += spacing)
        {
            float screenX = layout.ScreenCenterX + (worldX - layout.WorldCenterX) * layout.PixelsPerUnit;
            RectangleShape gridLine = verticalLine;
            gridLine.Position = new Vector2f(screenX, layout.Top);
            gridLine.Size = new Vector2f(1f, layout.Height);
            gridLine.FillColor = MathF.Abs(worldX) < spacing * 0.001f
                ? new Color(125, 153, 180, 145)
                : new Color(76, 97, 119, 92);
            window.Draw(gridLine);
        }

        RectangleShape horizontalLine = _gridLines[1];
        for (float worldY = MathF.Floor(lowVertical / spacing) * spacing;
             worldY <= highVertical;
             worldY += spacing)
        {
            float screenY = projection == 2
                ? layout.ScreenCenterY + (worldY - layout.WorldCenterY) * layout.PixelsPerUnit
                : layout.ScreenCenterY - (worldY - layout.WorldCenterY) * layout.PixelsPerUnit;
            RectangleShape gridLine = horizontalLine;
            gridLine.Position = new Vector2f(layout.Left, screenY);
            gridLine.Size = new Vector2f(layout.Width, 1f);
            gridLine.FillColor = MathF.Abs(worldY) < spacing * 0.001f
                ? new Color(125, 153, 180, 145)
                : new Color(76, 97, 119, 92);
            window.Draw(gridLine);
        }
    }

    private static NumericsVector3 Rotate2D(NumericsVector3 value, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        return new NumericsVector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }

    private static Color RoleColor(BodyPartRole role) => role switch
    {
        BodyPartRole.Head => new Color(174, 111, 100, 255),
        BodyPartRole.Neck => new Color(161, 143, 104, 255),
        BodyPartRole.Torso => new Color(69, 142, 117, 255),
        BodyPartRole.UpperArm or BodyPartRole.Forearm => new Color(72, 150, 123, 255),
        BodyPartRole.Thigh or BodyPartRole.Shin => new Color(69, 139, 113, 255),
        _ => new Color(85, 139, 120, 255)
    };

    private static string PartName(string id) => id switch
    {
        "body.middle" => "Туловище · середина",
        "body.lower" => "Туловище · низ",
        "body.torso.upper" => "Туловище · верх",
        "body.arm.left.upper" => "Левая рука · верх",
        "body.arm.left.lower" => "Левая рука · низ",
        "body.arm.right.upper" => "Правая рука · верх",
        "body.arm.right.lower" => "Правая рука · низ",
        "body.leg.left.upper" => "Левая нога · верх",
        "body.leg.left.lower" => "Левая нога · низ",
        "body.leg.right.upper" => "Правая нога · верх",
        "body.leg.right.lower" => "Правая нога · низ",
        "body.neck" => "Шея",
        "body.upper" => "Голова",
        _ => id
    };

    private static void DrawButton(RenderWindow window, RectangleShape button, Text? label)
    {
        window.Draw(button);
        if (label != null)
        {
            var bounds = label.GetLocalBounds();
            label.Position = new Vector2f(
                MathF.Round(button.Position.X + (button.Size.X - bounds.Width) * 0.5f - bounds.Left),
                MathF.Round(button.Position.Y + (button.Size.Y - bounds.Height) * 0.5f - bounds.Top));
            DrawText(window, label);
        }
    }

    private static void DrawText(RenderWindow window, Text? text)
    {
        if (text == null)
            return;
        Vector2f position = text.Position;
        text.Position = new Vector2f(MathF.Round(position.X), MathF.Round(position.Y));
        window.Draw(text);
    }

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(folder, candidates[i]);
            if (!File.Exists(path))
                continue;

            try { return new Font(path); }
            catch { }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (RectangleShape shape in _partRows)
            shape.Dispose();
        foreach (Text? label in _partLabels)
            label?.Dispose();
        foreach (RectangleShape shape in _previewPanels)
            shape.Dispose();
        foreach (Text? label in _projectionLabels)
            label?.Dispose();
        foreach (RectangleShape shape in _gridLines)
            shape.Dispose();
        foreach (RectangleShape shape in _symmetryButtons)
            shape.Dispose();
        foreach (Text? label in _symmetryLabels)
            label?.Dispose();
        foreach (RectangleShape shape in _sliderTracks)
            shape.Dispose();
        foreach (RectangleShape shape in _sliderFills)
            shape.Dispose();
        foreach (RectangleShape shape in _sliderKnobs)
            shape.Dispose();
        foreach (Text? label in _sliderLabels)
            label?.Dispose();
        foreach (Text? label in _sliderValues)
            label?.Dispose();

        _title?.Dispose();
        _saveLabel?.Dispose();
        _loadLabel?.Dispose();
        _exportLabel?.Dispose();
        _defaultsLabel?.Dispose();
        _reloadLabel?.Dispose();
        _parentLabel?.Dispose();
        _allSymmetryLabel?.Dispose();
        _gridLabel?.Dispose();
        _gridStepLabel?.Dispose();
        _surfaceSnapLabel?.Dispose();
        _addRegionLabel?.Dispose();
        _addAllFormsLabel?.Dispose();
        _removeRegionLabel?.Dispose();
        _closeLabel?.Dispose();
        _listTitle?.Dispose();
        _propertiesTitle?.Dispose();
        _selectedName?.Dispose();
        _selectedInfo?.Dispose();
        _primitiveLabel?.Dispose();
        _helpText?.Dispose();
        _statusText?.Dispose();

        _backdrop.Dispose();
        _panel.Dispose();
        _header.Dispose();
        _listPanel.Dispose();
        _propertiesPanel.Dispose();
        _saveButton.Dispose();
        _loadButton.Dispose();
        _exportButton.Dispose();
        _defaultsButton.Dispose();
        _reloadButton.Dispose();
        _closeButton.Dispose();
        _primitiveButton.Dispose();
        _parentButton.Dispose();
        _allSymmetryButton.Dispose();
        _gridButton.Dispose();
        _gridStepButton.Dispose();
        _surfaceSnapButton.Dispose();
        _addRegionButton.Dispose();
        _addAllFormsButton.Dispose();
        _removeRegionButton.Dispose();
        _previewCircle.Dispose();
        _previewBox.Dispose();
        _previewTriangle.Dispose();
    }

    private readonly record struct PropertyInfo(float Minimum, float Maximum, float Value, string Format);

    private struct ProjectionLayout
    {
        public float Left;
        public float Top;
        public float Width;
        public float Height;
        public float PixelsPerUnit;
        public float ScreenCenterX;
        public float ScreenCenterY;
        public float WorldCenterX;
        public float WorldCenterY;

        public ProjectionLayout(float left, float top, float width, float height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            PixelsPerUnit = 0f;
            ScreenCenterX = 0f;
            ScreenCenterY = 0f;
            WorldCenterX = 0f;
            WorldCenterY = 0f;
        }

        public ProjectionLayout WithTransform(
            float pixelsPerUnit,
            float screenCenterX,
            float screenCenterY,
            float worldCenterX,
            float worldCenterY)
        {
            PixelsPerUnit = pixelsPerUnit;
            ScreenCenterX = screenCenterX;
            ScreenCenterY = screenCenterY;
            WorldCenterX = worldCenterX;
            WorldCenterY = worldCenterY;
            return this;
        }
    }

    private struct ProjectedPart
    {
        public float X;
        public float Y;
        public float HalfWidth;
        public float HalfHeight;
        public float ScreenRotation;
        public BodyPrimitive Primitive;
        public float ScreenX;
        public float ScreenY;
        public float ScreenHalfWidth;
        public float ScreenHalfHeight;

        public ProjectedPart(float x, float y, float halfWidth, float halfHeight, float screenRotation, BodyPrimitive primitive)
        {
            X = x;
            Y = y;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
            ScreenRotation = screenRotation;
            Primitive = primitive;
            ScreenX = 0f;
            ScreenY = 0f;
            ScreenHalfWidth = 0f;
            ScreenHalfHeight = 0f;
        }
    }
}
