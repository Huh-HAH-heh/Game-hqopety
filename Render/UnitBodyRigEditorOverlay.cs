using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
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
    private const int PropertyCount = 7;

    private static readonly Color BackdropColor = new(5, 9, 14, 218);
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
    private float[] _worldRotations = Array.Empty<float>();
    private ProjectedPart[][] _projected = new ProjectedPart[3][];
    private ProjectionLayout[] _projectionLayouts = new ProjectionLayout[3];

    private int _selectedPart;
    private int _selectedProperty;
    private int _draggingProperty = -1;
    private int _draggingProjection = -1;
    private bool _draggingPart;
    private int _lastMouseX;
    private int _lastMouseY;
    private float _listRowHeight = 28f;
    private float _propertyRowHeight = 46f;
    private int _listScroll;
    private int _listVisibleRows = 12;
    private string _status = "Изменения можно двигать мышью или менять ползунками.";
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
            "ПОВОРОТ, ГРАДУСЫ"
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
        _worldRotations = new float[_parts.Length];
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
            _draggingProperty = i;
            UpdatePropertyFromSlider(i, position.X);
            return true;
        }

        for (int projection = 0; projection < _previewPanels.Length; projection++)
        {
            if (!_previewPanels[projection].GetGlobalBounds().Contains(position))
                continue;

            int hitPart = HitTestPreview(projection, position.X, position.Y);
            if (hitPart >= 0)
            {
                _selectedPart = hitPart;
                _draggingProjection = projection;
                _draggingPart = true;
                _lastMouseX = position.X;
                _lastMouseY = position.Y;
                _status = $"Перемещение: {PartName(_regionIds[hitPart])}. Отпусти мышь для завершения.";
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

        if (controlPressed && key == Keyboard.Key.S)
        {
            Save();
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

        if (_title != null)
            window.Draw(_title);
        DrawButton(window, _saveButton, _saveLabel);
        DrawButton(window, _loadButton, _loadLabel);
        DrawButton(window, _exportButton, _exportLabel);
        DrawButton(window, _defaultsButton, _defaultsLabel);
        DrawButton(window, _reloadButton, _reloadLabel);
        DrawButton(window, _closeButton, _closeLabel);

        if (_listTitle != null)
            window.Draw(_listTitle);
        if (_propertiesTitle != null)
            window.Draw(_propertiesTitle);

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
                window.Draw(_partLabels[itemIndex]!);
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
            window.Draw(_selectedName);
        }

        if (_selectedInfo != null)
        {
            BodyPartDefinition part = _parts[_selectedPart];
            string parent = part.ParentIndex >= 0 ? PartName(_regionIds[part.ParentIndex]) : "Корень";
            _selectedInfo.DisplayedString =
                $"Родитель: {parent}\nPivot: {part.Pivot}   Motion: {part.Motion}\nRole: {part.Role}   Anchor: {part.Anchor}";
            window.Draw(_selectedInfo);
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
            window.Draw(_primitiveLabel);
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
            window.Draw(_parentLabel);
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
            window.Draw(_allSymmetryLabel);
        }

        if (_helpText != null)
            window.Draw(_helpText);
        if (_statusText != null)
        {
            _statusText.DisplayedString = _status;
            window.Draw(_statusText);
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

        float buttonY = y + 7f;
        _saveButton.Position = new Vector2f(x + panelWidth - 390f, buttonY);
        _exportButton.Position = new Vector2f(x + panelWidth - 502f, buttonY);
        _loadButton.Position = new Vector2f(x + panelWidth - 614f, buttonY);
        _defaultsButton.Position = new Vector2f(x + panelWidth - 278f, buttonY);
        _reloadButton.Position = new Vector2f(x + panelWidth - 146f, buttonY);
        _closeButton.Position = new Vector2f(x + panelWidth - 38f, buttonY);

        if (_title != null)
            _title.Position = new Vector2f(x + 14f, y + 12f);
        if (_saveLabel != null)
            _saveLabel.Position = new Vector2f(_saveButton.Position.X + 14f, buttonY + 9f);
        if (_loadLabel != null)
            _loadLabel.Position = new Vector2f(_loadButton.Position.X + 12f, buttonY + 9f);
        if (_exportLabel != null)
            _exportLabel.Position = new Vector2f(_exportButton.Position.X + 16f, buttonY + 9f);
        if (_defaultsLabel != null)
            _defaultsLabel.Position = new Vector2f(_defaultsButton.Position.X + 10f, buttonY + 10f);
        if (_reloadLabel != null)
            _reloadLabel.Position = new Vector2f(_reloadButton.Position.X + 18f, buttonY + 9f);
        if (_closeLabel != null)
            _closeLabel.Position = new Vector2f(_closeButton.Position.X + 8f, buttonY + 2f);

        float contentY = y + 54f;
        float contentHeight = panelHeight - 104f;
        float listX = x + 10f;
        float listWidth = 188f;
        float propertiesWidth = 244f;
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

        _propertyRowHeight = MathF.Min(47f, MathF.Max(39f, (contentHeight - 232f) / PropertyCount));
        if (_selectedName != null)
            _selectedName.Position = new Vector2f(propertiesX + 10f, contentY + 31f);
        if (_selectedInfo != null)
            _selectedInfo.Position = new Vector2f(propertiesX + 10f, contentY + 52f);

        float propertyTop = contentY + 110f;
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
                _projectionLabels[i]!.Position = new Vector2f(previewX + 8f, previewY + 8f);
            _symmetryButtons[i].Position = new Vector2f(previewX + previewWidth - 96f, previewY + 4f);
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
        window.Draw(panel);
        if (_projectionLabels[projection] != null)
            window.Draw(_projectionLabels[projection]!);

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
            window.Draw(_symmetryLabels[projection]!);
        }

        ProjectionLayout layout = _projectionLayouts[projection];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < _parts.Length; i++)
        {
            ProjectedPart geometry = Project(_parts[i], _worldPositions[i], _worldRotations[i], projection);
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

        for (int i = 0; i < _parts.Length; i++)
        {
            ProjectedPart geometry = _projected[projection][i];
            float sx = screenCenterX + (geometry.X - worldCenterX) * pixelsPerUnit;
            float sy = screenCenterY - (geometry.Y - worldCenterY) * pixelsPerUnit;
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
                _previewCircle.Rotation = projection == 2 ? geometry.ScreenRotation : 0f;
                _previewCircle.FillColor = fill;
                _previewCircle.OutlineColor = outline;
                _previewCircle.OutlineThickness = thickness;
                window.Draw(_previewCircle);
            }
            else if (geometry.Primitive == BodyPrimitive.Triangle)
            {
                _previewTriangle.Position = new Vector2f(sx, sy);
                _previewTriangle.Scale = new Vector2f(halfWidth, halfHeight);
                _previewTriangle.Rotation = projection == 2 ? geometry.ScreenRotation : 0f;
                _previewTriangle.FillColor = fill;
                _previewTriangle.OutlineColor = outline;
                _previewTriangle.OutlineThickness = thickness;
                window.Draw(_previewTriangle);
            }
            else
            {
                _previewBox.Position = new Vector2f(sx, sy);
                _previewBox.Scale = new Vector2f(halfWidth, halfHeight);
                _previewBox.Rotation = projection == 2 ? geometry.ScreenRotation : 0f;
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
            window.Draw(_sliderLabels[index]!);
        }

        if (_sliderValues[index] != null)
        {
            _sliderValues[index]!.DisplayedString = info.Format;
            _sliderValues[index]!.Position = new Vector2f(track.Position.X + track.Size.X - 78f, rowY - 1f);
            window.Draw(_sliderValues[index]!);
        }
    }

    private void CalculateWorldTransforms()
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            BodyPartDefinition part = _parts[i];
            int parent = part.ParentIndex;
            NumericsVector3 origin = NumericsVector3.Zero;
            float positionRotation = 0f;
            float baseRotation = 0f;

            if (part.Anchor == BodyPartAnchor.Parent && parent >= 0)
            {
                origin = _worldPositions[parent];
                positionRotation = _worldRotations[parent];
                baseRotation = _worldRotations[parent];
            }
            else if (part.Anchor == BodyPartAnchor.UnitHead)
            {
                // Preview the head facing forward while keeping it anchored to the unit root.
                positionRotation = 0f;
                baseRotation = 0f;
            }

            NumericsVector3 joint = origin + Rotate2D(part.LocalPosition, positionRotation);
            float worldRotation = baseRotation + part.LocalRotation;
            if (part.Pivot == BodyPartPivot.Start)
            {
                joint += Rotate2D(
                    new NumericsVector3(MathF.Abs(part.Scale.X), 0f, 0f),
                    worldRotation);
            }

            _worldPositions[i] = joint;
            _worldRotations[i] = worldRotation;
        }
    }

    private void ApplyProjectionDrag(int projection, int deltaX, int deltaY)
    {
        ProjectionLayout layout = _projectionLayouts[projection];
        if (layout.PixelsPerUnit <= 0f)
            return;

        BodyPartDefinition part = _parts[_selectedPart];
        float dx = deltaX / layout.PixelsPerUnit;
        float dy = -deltaY / layout.PixelsPerUnit;
        float parentRotation = part.ParentIndex >= 0
            ? _worldRotations[part.ParentIndex]
            : 0f;
        float cos = MathF.Cos(parentRotation);
        float sin = MathF.Sin(parentRotation);

        NumericsVector3 localDelta = projection switch
        {
            0 => new NumericsVector3(
                sin * dx,
                cos * dx,
                dy),
            1 => new NumericsVector3(
                cos * dx,
                -sin * dx,
                dy),
            _ => new NumericsVector3(
                cos * dx + sin * dy,
                -sin * dx + cos * dy,
                0f)
        };

        _parts[_selectedPart] = Copy(
            part,
            localPosition: part.LocalPosition + localDelta);
        ApplyEnabledSymmetry(projection);
        _status = $"Изменено: {PartName(_regionIds[_selectedPart])}. Нажми Ctrl+S для сохранения.";
    }

    private int HitTestPreview(int projection, int x, int y)
    {
        ProjectedPart[] geometry = _projected[projection];
        for (int i = geometry.Length - 1; i >= 0; i--)
        {
            ProjectedPart part = geometry[i];
            if (part.ScreenHalfWidth <= 0f || part.ScreenHalfHeight <= 0f)
                continue;

            if (MathF.Abs(x - part.ScreenX) <= part.ScreenHalfWidth + 4f &&
                MathF.Abs(y - part.ScreenY) <= part.ScreenHalfHeight + 4f)
            {
                return i;
            }
        }

        return -1;
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
            _ => 5f
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
            _ => new PropertyInfo(-180f, 180f, part.LocalRotation * 180f / MathF.PI, $"{part.LocalRotation * 180f / MathF.PI:+0.0;-0.0;0.0}°")
        };
    }

    private void SetProperty(int index, float value)
    {
        BodyPartDefinition part = _parts[_selectedPart];
        NumericsVector3 position = part.LocalPosition;
        NumericsVector3 scale = part.Scale;
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
            default:
                rotation = Math.Clamp(value, -180f, 180f) * MathF.PI / 180f;
                break;
        }

        _parts[_selectedPart] = Copy(part, position, scale, rotation);
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

        string? counterpartId = GetCounterpartId(_regionIds[_selectedPart]);
        if (counterpartId == null)
            return;

        int counterpartIndex = Array.IndexOf(_regionIds, counterpartId);
        if (counterpartIndex < 0 || counterpartIndex == _selectedPart)
            return;

        BodyPartDefinition source = _parts[_selectedPart];
        for (int projection = 0; projection < _symmetryEnabled.Length; projection++)
        {
            if (!_symmetryEnabled[projection] ||
                (activeProjection >= 0 && activeProjection != projection))
                continue;

            BodyPartDefinition target = _parts[counterpartIndex];
            NumericsVector3 mirroredPosition = projection switch
            {
                0 => new NumericsVector3(target.LocalPosition.X, -source.LocalPosition.Y, source.LocalPosition.Z),
                1 => new NumericsVector3(-source.LocalPosition.X, target.LocalPosition.Y, source.LocalPosition.Z),
                _ => new NumericsVector3(source.LocalPosition.X, -source.LocalPosition.Y, source.LocalPosition.Z)
            };
            _parts[counterpartIndex] = Copy(
                target,
                localPosition: mirroredPosition,
                scale: source.Scale,
                localRotation: -source.LocalRotation,
                primitive: source.Primitive);
        }
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

        var buffer = new StringBuilder(1024);
        var titleBuffer = new StringBuilder(256);
        var dialog = new OpenFileName
        {
            lStructSize = Marshal.SizeOf<OpenFileName>(),
            hwndOwner = IntPtr.Zero,
            hInstance = IntPtr.Zero,
            lpstrFilter = "RimClone rig (*.json)\0*.json\0Все файлы (*.*)\0*.*\0\0",
            lpstrCustomFilter = IntPtr.Zero,
            nMaxCustFilter = 0,
            nFilterIndex = 1,
            lpstrFile = buffer,
            nMaxFile = buffer.Capacity,
            lpstrFileTitle = titleBuffer,
            nMaxFileTitle = titleBuffer.Capacity,
            lpstrInitialDir = Path.GetDirectoryName(UnitBodyRigSettingsStore.FilePath),
            lpstrTitle = save ? "Сохранить риг как..." : "Загрузить риг",
            Flags = 0x00000800 | 0x00080000 | (save ? 0x00000002 : 0x00001000),
            nFileOffset = 0,
            nFileExtension = 0,
            lpstrDefExt = "json",
            lCustData = IntPtr.Zero,
            lpfnHook = IntPtr.Zero,
            lpTemplateName = null,
            pvReserved = IntPtr.Zero,
            dwReserved = 0,
            FlagsEx = 0
        };

        bool accepted = save ? GetSaveFileNameW(ref dialog) : GetOpenFileNameW(ref dialog);
        return accepted ? dialog.lpstrFile.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public StringBuilder lpstrFile;
        public int nMaxFile;
        public StringBuilder? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
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
        BodyPartAnchor? anchor = null) =>
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
            pivot: original.Pivot);

    private static ProjectedPart Project(
        BodyPartDefinition part,
        NumericsVector3 position,
        float rotation,
        int projection)
    {
        float absSin = MathF.Abs(MathF.Sin(rotation));
        float absCos = MathF.Abs(MathF.Cos(rotation));
        float halfWidth;
        float halfHeight;
        float x;
        float y;
        float screenRotation = -rotation * 180f / MathF.PI;

        switch (projection)
        {
            case 0:
                x = position.Y;
                y = position.Z;
                halfWidth = absSin * MathF.Abs(part.Scale.X) + absCos * MathF.Abs(part.Scale.Y);
                halfHeight = MathF.Abs(part.Scale.Z);
                screenRotation = 0f;
                break;
            case 1:
                x = position.X;
                y = position.Z;
                halfWidth = absCos * MathF.Abs(part.Scale.X) + absSin * MathF.Abs(part.Scale.Y);
                halfHeight = MathF.Abs(part.Scale.Z);
                screenRotation = 0f;
                break;
            default:
                x = position.X;
                y = position.Y;
                halfWidth = absCos * MathF.Abs(part.Scale.X) + absSin * MathF.Abs(part.Scale.Y);
                halfHeight = absSin * MathF.Abs(part.Scale.X) + absCos * MathF.Abs(part.Scale.Y);
                break;
        }

        return new ProjectedPart(
            x,
            y,
            MathF.Max(0.001f, halfWidth),
            MathF.Max(0.001f, halfHeight),
            screenRotation,
            part.Primitive);
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
            window.Draw(label);
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
